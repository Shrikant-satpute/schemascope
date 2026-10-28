using Npgsql;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// Hardening round 3 on real servers. Each case is a reported defect: a small drifted pair, compared, scripted, applied
/// the way Write mode applies (each file in its own transaction, every folder in run order, failures collected rather
/// than stopping), compared again.
/// </summary>
public abstract class PgHardening3TestsBase(PgServer pg)
{
    private async Task<(ScriptOutcome Outcome, List<string> Failures, List<string> Left)> RoundAsync(PgScriptPair pair, bool destructive = false)
    {
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: destructive);
        var failures = await PgHardeningTestsBase.ApplyAsync(pg, pair.Tgt, outcome);
        return (outcome, failures, PgScriptPair.NotSame(await pair.CompareAsync()));
    }

    private static string Held(ScriptOutcome o) => string.Join("\n", o.HeldBack.Select(h => $"{h.Section} | {h.Object} | {h.Reason}"));

    protected async Task<int> MajorAsync(PgScriptPair pair) =>
        (int)(await pair.ScalarAsync("SELECT current_setting('server_version_num')::int") as int? ?? 0) / 10000;

    // ---------------- 03: a key name another relation of the target holds

    protected async Task A_new_table_s_key_name_taken_on_the_target_waits_and_the_plan_converges()
    {
        const string table = """
            CREATE SCHEMA ops;
            CREATE TABLE ops."Order Lines" (id integer PRIMARY KEY, "Line No" integer NOT NULL, note text);
            CREATE INDEX "Order Lines_note_ix" ON ops."Order Lines" (note);
            CREATE VIEW ops."report/daily:v1" AS SELECT id, note FROM ops."Order Lines";
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, table, table + """
            INSERT INTO ops."Order Lines" VALUES (1, 1, 'n');
            ALTER TABLE ops."Order Lines" RENAME TO order_lines;
            """);
        var (first, failures, _) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + Held(first));
        Assert.Contains(first.HeldBack, h => h.Object.Contains("Order Lines_pkey") && h.Reason.Contains("already has on the target"));
        // 10 replaces the view so it reads the new table: 11 drops the old one in the same plan
        Assert.DoesNotContain(first.HeldBack, h => h.Object.Contains("order_lines"));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_class WHERE relname = 'order_lines'"));

        var (_, again, left) = await RoundAsync(pair, destructive: true);
        Assert.Empty(again);
        Assert.Empty(left);
    }

    // ---------------- 03: a new partition and the target's default partition

    protected async Task A_new_partition_waits_while_the_default_partition_holds_its_rows()
    {
        const string common = """
            CREATE SCHEMA feat;
            CREATE TABLE feat.event (id integer, at date NOT NULL) PARTITION BY RANGE (at);
            CREATE TABLE feat.event_2025 PARTITION OF feat.event FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
            CREATE TABLE feat.event_default PARTITION OF feat.event DEFAULT;
            CREATE TABLE feat.kind (id integer, k text) PARTITION BY LIST (k);
            CREATE TABLE feat.kind_a PARTITION OF feat.kind FOR VALUES IN ('a');
            CREATE TABLE feat.kind_default PARTITION OF feat.kind DEFAULT;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE TABLE feat.event_2027 PARTITION OF feat.event FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
            CREATE TABLE feat.kind_b PARTITION OF feat.kind FOR VALUES IN ('b', NULL);
            """, common + """
            INSERT INTO feat.event VALUES (1, '2027-05-01'), (2, '2030-01-01');
            INSERT INTO feat.kind VALUES (1, 'c');
            """);
        var (first, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Contains(first.HeldBack, h => h.Object == "feat.event_2027" && h.Reason.Contains("default partition"));
        // the default partition's 'c' row is no value of the new list partition
        Assert.Equal(["Table feat.event_2027: MissingInTarget"], left);

        await pg.ExecAsync(pair.Tgt, "DELETE FROM feat.event WHERE id = 1");
        var (_, again, done) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(done);
    }

    // ---------------- a materialized view whose indexes alone differ

    protected async Task A_materialized_view_s_indexes_are_changed_without_replacing_it()
    {
        const string mv = """
            CREATE SCHEMA hr;
            CREATE TABLE hr.e (id integer, dept text);
            INSERT INTO hr.e VALUES (1, 'a'), (2, 'b'), (3, 'b');
            CREATE MATERIALIZED VIEW hr.mv AS SELECT dept, count(*) AS n FROM hr.e GROUP BY dept;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, mv + """
            CREATE UNIQUE INDEX mv_key ON hr.mv (dept);
            CREATE INDEX mv_n_ix ON hr.mv (n);
            """, mv + """
            CREATE INDEX mv_n_ix ON hr.mv (n, dept);
            CREATE INDEX mv_extra ON hr.mv (dept, n);
            """);
        Assert.Equal(["Materialized view hr.mv: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object == "hr.mv");
        Assert.Contains("CREATE UNIQUE INDEX mv_key ON hr.mv", PgScriptPair.Folder(outcome, PgScriptGenerator.IndexesFolder));
        Assert.Contains("DROP INDEX \"hr\".\"mv_extra\" RESTRICT", PgScriptPair.Folder(outcome, PgScriptGenerator.DestructiveFolder));
        Assert.Empty(left);
        Assert.Equal(2L, await pair.ScalarAsync("SELECT n FROM hr.mv WHERE dept = 'b'"));
    }

    // ---------------- 11: what the folder drops itself no longer holds a drop back

    protected async Task A_function_used_only_by_what_11_drops_goes_in_the_same_run()
    {
        const string common = "CREATE SCHEMA hr; CREATE TABLE hr.skill (id integer); CREATE TABLE hr.employee (id integer);";
        await using var pair = await PgScriptPair.CreateAsync(pg, common, common + """
            CREATE FUNCTION hr.legacy_fn(integer) RETURNS integer IMMUTABLE LANGUAGE sql AS 'SELECT $1';
            CREATE INDEX ix_legacy_fn ON hr.skill (hr.legacy_fn(id));
            ALTER TABLE hr.employee ADD COLUMN legacy_calc integer DEFAULT hr.legacy_fn(1);
            """);
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.Contains("legacy_fn"));
        Assert.Empty(left);
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_proc WHERE proname = 'legacy_fn'"));
    }

    // ---------------- 01: NOT NULL with no default on an empty table

    protected async Task A_not_null_column_with_no_default_is_added_to_an_empty_table()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA hr;
            CREATE TABLE hr.holiday (id integer, region_code text NOT NULL);
            CREATE TABLE hr.h2 (id integer, region_code text NOT NULL);
            """, """
            CREATE SCHEMA hr;
            CREATE TABLE hr.holiday (id integer);
            CREATE TABLE hr.h2 (id integer);
            INSERT INTO hr.h2 VALUES (1);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object == "hr.holiday.region_code");
        Assert.Contains(outcome.HeldBack, h => h.Object == "hr.h2.region_code" && h.Reason.Contains("1 row"));
        Assert.Equal(["Table hr.h2: Different"], left);
    }

    // ---------------- 06: a changed unique index a foreign key stands on

    protected async Task A_changed_unique_index_a_foreign_key_needs_is_rebuilt_with_it()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA xf;
            CREATE TABLE xf.p (id integer, code text);
            CREATE UNIQUE INDEX p_code_ux ON xf.p (code) INCLUDE (id);
            CREATE TABLE xf.c (code text REFERENCES xf.p (code));
            """, """
            CREATE SCHEMA xf;
            CREATE TABLE xf.p (id integer, code text);
            CREATE UNIQUE INDEX p_code_ux ON xf.p (code);
            CREATE TABLE xf.c (code text REFERENCES xf.p (code));
            INSERT INTO xf.p VALUES (1, 'a');
            INSERT INTO xf.c VALUES ('a');
            """);
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal(true, await pair.ScalarAsync("SELECT convalidated FROM pg_constraint WHERE conname = 'c_code_fkey'"));
    }

    // ---------------- 04: a column a SQL-standard function body reads

    protected async Task A_column_a_sql_standard_function_reads_is_held_with_why()
    {
        if (await MajorAsyncOf(pg) < 14) return;
        const string fn = "CREATE FUNCTION aa.f() RETURNS bigint LANGUAGE sql RETURN (SELECT max(b) FROM aa.w);";
        const string atomic = "CREATE FUNCTION aa.g() RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT max(a) FROM aa.w; END;";
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA aa; CREATE TABLE aa.w (a bigint, b bigint);" + fn,
            "CREATE SCHEMA aa; CREATE TABLE aa.w (a integer, b integer);" + fn);
        // through psql: Npgsql would split a BEGIN ATOMIC body at its semicolons
        foreach (var db in new[] { pair.Src, pair.Tgt })
        {
            var (code, _, stderr) = await pg.ExecInContainerAsync("psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", PgServer.User, "-d", db, "-c", atomic);
            Assert.True(code == 0, stderr);
        }
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "aa.w.a" && h.Reason.Contains("aa.g()"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "aa.w.b" && h.Reason.Contains("aa.f()"));
        Assert.Equal(["Table aa.w: Different"], left);
    }

    private static async Task<int> MajorAsyncOf(PgServer server)
    {
        await using var conn = new NpgsqlConnection(server.ConnectionString("postgres"));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT current_setting('server_version_num')::int", conn);
        return (int)(await cmd.ExecuteScalarAsync())! / 10000;
    }

    // ---------------- foreign tables

    protected async Task Foreign_tables_are_compared_and_what_needs_a_missing_one_is_held()
    {
        const string common = """
            CREATE EXTENSION postgres_fdw;
            CREATE SERVER hunt_srv FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'localhost', dbname 'x');
            CREATE SCHEMA fv;
            CREATE FOREIGN TABLE fv.both (id integer NOT NULL, name text) SERVER hunt_srv OPTIONS (table_name 'both');
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE FOREIGN TABLE fv.remote (id integer, name text) SERVER hunt_srv;
            CREATE VIEW fv.v_remote AS SELECT id, name FROM fv.remote;
            CREATE VIEW fv.v_both AS SELECT id FROM fv.both;
            """, common);
        var before = PgScriptPair.NotSame(await pair.CompareAsync());
        Assert.Contains("Foreign table fv.remote: MissingInTarget", before);
        Assert.DoesNotContain(before, r => r.Contains("fv.both:"));

        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Contains(outcome.HeldBack, h => h.Object == "fv.remote" && h.Reason.Contains("foreign table"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "fv.v_remote" && h.Reason.Contains("fv.remote"));
        Assert.Equal(["Foreign table fv.remote: MissingInTarget", "View fv.v_remote: MissingInTarget"], left);
    }

    // ---------------- 05: a sequence's new range and the target's current value

    protected async Task A_sequence_range_the_current_value_is_outside_of_is_held()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA sq;
            CREATE SEQUENCE sq.a MAXVALUE 100;
            CREATE SEQUENCE sq.b AS smallint;
            CREATE SEQUENCE sq.c MINVALUE 10 START 10;
            CREATE SEQUENCE sq.d MAXVALUE 1000;
            """, """
            CREATE SCHEMA sq;
            CREATE SEQUENCE sq.a; SELECT setval('sq.a', 500);
            CREATE SEQUENCE sq.b AS integer; SELECT setval('sq.b', 40000);
            CREATE SEQUENCE sq.c; SELECT setval('sq.c', 3);
            CREATE SEQUENCE sq.d; SELECT setval('sq.d', 5);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        foreach (var name in new[] { "sq.a", "sq.b", "sq.c" })
            Assert.Contains(outcome.HeldBack, h => h.Object == name && h.Reason.Contains("current value"));
        Assert.Equal(["Sequence sq.a: Different", "Sequence sq.b: Different", "Sequence sq.c: Different"], left);
    }

    // ---------------- 06: an index's COLLATE with collations ignored

    protected async Task An_index_s_collate_clause_is_no_difference_when_collations_are_ignored()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA co;
            CREATE TABLE co.t (id integer, a text COLLATE "C", b varchar(20) COLLATE "POSIX", c text);
            CREATE INDEX t_c_ix ON co.t (c COLLATE "C");
            """, """
            CREATE SCHEMA co;
            CREATE TABLE co.t (id integer, a text, b varchar(20), c text COLLATE "C");
            INSERT INTO co.t VALUES (1, 'x', 'y', 'z');
            """);
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        var again = await pair.GenerateAsync(await pair.CompareAsync());
        Assert.Empty(PgScriptPair.FilesOf(again, PgScriptGenerator.IndexesFolder));
    }

    // ---------------- replica identity

    protected async Task Replica_identity_is_compared_and_kept_through_an_index_rebuild()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA idp;
            CREATE TABLE idp.t (id integer NOT NULL, a integer, b integer);
            CREATE UNIQUE INDEX t_id ON idp.t (id) WHERE id > 0;
            ALTER TABLE idp.t REPLICA IDENTITY FULL;
            CREATE TABLE idp.u (id integer, k integer NOT NULL);
            CREATE UNIQUE INDEX u_k ON idp.u (k DESC);
            ALTER TABLE idp.u REPLICA IDENTITY USING INDEX u_k;
            CREATE TABLE idp.n (id integer NOT NULL, k integer NOT NULL);
            CREATE UNIQUE INDEX n_k ON idp.n (k);
            ALTER TABLE idp.n REPLICA IDENTITY USING INDEX n_k;
            """, """
            CREATE SCHEMA idp;
            CREATE TABLE idp.t (id integer NOT NULL, a integer, b integer);
            CREATE UNIQUE INDEX t_id ON idp.t (id);
            ALTER TABLE idp.t REPLICA IDENTITY USING INDEX t_id;
            CREATE TABLE idp.u (id integer, k integer NOT NULL);
            CREATE UNIQUE INDEX u_k ON idp.u (k);
            ALTER TABLE idp.u REPLICA IDENTITY USING INDEX u_k;
            CREATE TABLE idp.n (id integer NOT NULL, k integer NOT NULL);
            """);
        Assert.Contains("Table idp.t: Different", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (_, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal("f", await pair.ScalarAsync("SELECT relreplident::text FROM pg_class WHERE oid = 'idp.t'::regclass"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT indisreplident FROM pg_index WHERE indexrelid = 'idp.u_k'::regclass"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT indisreplident FROM pg_index WHERE indexrelid = 'idp.n_k'::regclass"));
    }

    // ---------------- 04: arrays widen as their elements do

    protected async Task An_array_column_widens_as_its_element_does()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg,
            "CREATE SCHEMA w; CREATE TABLE w.t (a text, b text[], c varchar(20)[], d bigint[], e numeric, f varchar);", """
            CREATE SCHEMA w;
            CREATE TABLE w.t (a varchar(10), b varchar(10)[], c varchar(10)[], d integer[], e numeric(10,2), f varchar(5));
            INSERT INTO w.t VALUES ('a', '{x}', '{y}', '{1}', 1.5, 'f');
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.StartsWith("w.t.", StringComparison.Ordinal));
        Assert.Empty(left);
    }

    // ---------------- schema owners the login cannot become

    protected async Task A_schema_owner_the_login_cannot_become_is_not_scripted()
    {
        var (deploy, password) = await pg.CreateRoleAsync();
        try
        {
            await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA sales; CREATE SCHEMA guest; CREATE TABLE sales.t (id integer);", "SELECT 1");
            await pg.ExecAsync(pair.Tgt, $"GRANT CREATE ON DATABASE \"{pair.Tgt}\" TO \"{deploy}\"");
            await using (var conn = new NpgsqlConnection(pg.ConnectionString(pair.Tgt, deploy, password)))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("CREATE SCHEMA sales; CREATE TABLE sales.t (id integer);", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            var asDeploy = pg.ConnectionString(pair.Tgt, deploy, password);
            var run = await new CompareRunner(_ => new PgSchemaReader()).RunAsync(pg.Source("source", pair.Src),
                [pg.Source("t0", pair.Tgt, deploy, password)], new CompareOptions());
            Assert.Contains("Schema sales.sales: Different", PgScriptPair.NotSame(run));

            var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-r3-{Guid.NewGuid():n}");
            try
            {
                var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(
                    new ScriptEndpoint("Source", pg.Host, pair.Src, pg.ConnectionString(pair.Src), DatabaseEngine.PostgreSql),
                    new ScriptEndpoint("Target", pg.Host, pair.Tgt, asDeploy, DatabaseEngine.PostgreSql),
                    run.Source, run.Targets[0], run.Result, 0,
                    new ScriptOptions { Sections = [.. PgScriptPair.Every], OutputFolder = output },
                    new HashSet<string>(), new Dictionary<string, LineChange>(), new CompareOptions()), default, null);
                Assert.True(outcome.Success, outcome.Message);
                Assert.DoesNotContain("AUTHORIZATION", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
                Assert.DoesNotContain("OWNER TO", PgScriptPair.Folder(outcome, PgScriptGenerator.ConstraintsFolder));
                Assert.Contains(outcome.HeldBack, h => h.Object == "sales" && h.Reason.Contains("SET ROLE"));

                var (files, _) = ApplyPlans.Build(outcome.Files, outcome.Folders);
                foreach (var f in files)
                {
                    var file = new VerifiedFile(f, PgStatementSplitter.Instance.Split(ScriptWriter.ReadBack(f.FullPath)));
                    Assert.Null(await PgApplyExecutor.Instance.RunFileAsync(asDeploy, pair.Tgt, file, default, () => { }));
                }
            }
            finally
            {
                try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await pg.DropRoleAsync(deploy);
        }
    }

    // ---------------- 11: a view whose columns change, and a widening only views stand in the way of

    protected async Task Views_whose_columns_change_are_dropped_and_created_again_around_the_widening()
    {
        const string table = """
            CREATE SCHEMA sales;
            CREATE TABLE sales."order" (id integer, name text, total numeric(12,2));
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, table + """
            CREATE VIEW sales.v_open_orders AS SELECT id, name, total FROM sales."order";
            CREATE VIEW sales.v_top AS SELECT id, total FROM sales.v_open_orders WHERE total > 10;
            CREATE MATERIALIZED VIEW sales.mv_totals AS SELECT sum(total) AS s FROM sales."order";
            CREATE UNIQUE INDEX mv_totals_s ON sales.mv_totals (s);
            """, table.Replace("numeric(12,2)", "numeric(10,2)") + """
            INSERT INTO sales."order" VALUES (1, 'a', 12.50);
            CREATE VIEW sales.v_open_orders AS SELECT id, total FROM sales."order";
            CREATE VIEW sales.v_top AS SELECT id, total FROM sales.v_open_orders WHERE total > 10;
            CREATE MATERIALIZED VIEW sales.mv_totals AS SELECT sum(total) AS s FROM sales."order";
            CREATE UNIQUE INDEX mv_totals_s ON sales.mv_totals (s);
            """);

        // without 11 both are held, and say what would make them
        var safe = await pair.GenerateAsync(await pair.CompareAsync());
        Assert.Contains(safe.HeldBack, h => h.Object == "sales.v_open_orders" && h.Reason.Contains("11_destructive"));
        Assert.Contains(safe.HeldBack, h => h.Object == "sales.order.total" && h.Reason.Contains("11_destructive"));

        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + Held(outcome));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.Contains("v_open_orders") || h.Object.Contains("total"));
        Assert.Empty(left);
        Assert.Equal(12.50m, await pair.ScalarAsync("SELECT s FROM sales.mv_totals"));
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM sales.v_top"));
    }
}

[Collection(PgCollection.Name)]
public sealed class PgHardening3Tests(PgServer pg) : PgHardening3TestsBase(pg)
{
    [PostgresFact] public Task Key_name_clash_on_16() => A_new_table_s_key_name_taken_on_the_target_waits_and_the_plan_converges();
    [PostgresFact] public Task Default_partition_on_16() => A_new_partition_waits_while_the_default_partition_holds_its_rows();
    [PostgresFact] public Task Matview_indexes_on_16() => A_materialized_view_s_indexes_are_changed_without_replacing_it();
    [PostgresFact] public Task Destructive_index_dependent_on_16() => A_function_used_only_by_what_11_drops_goes_in_the_same_run();
    [PostgresFact] public Task Not_null_empty_table_on_16() => A_not_null_column_with_no_default_is_added_to_an_empty_table();
    [PostgresFact] public Task Index_under_foreign_key_on_16() => A_changed_unique_index_a_foreign_key_needs_is_rebuilt_with_it();
    [PostgresFact] public Task Sql_standard_function_on_16() => A_column_a_sql_standard_function_reads_is_held_with_why();
    [PostgresFact] public Task Foreign_tables_on_16() => Foreign_tables_are_compared_and_what_needs_a_missing_one_is_held();
    [PostgresFact] public Task Sequence_range_on_16() => A_sequence_range_the_current_value_is_outside_of_is_held();
    [PostgresFact] public Task Index_collate_on_16() => An_index_s_collate_clause_is_no_difference_when_collations_are_ignored();
    [PostgresFact] public Task Replica_identity_on_16() => Replica_identity_is_compared_and_kept_through_an_index_rebuild();
    [PostgresFact] public Task Array_widening_on_16() => An_array_column_widens_as_its_element_does();
    [PostgresFact] public Task Schema_owner_on_16() => A_schema_owner_the_login_cannot_become_is_not_scripted();
    [PostgresFact] public Task View_rebuild_on_16() => Views_whose_columns_change_are_dropped_and_created_again_around_the_widening();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13Hardening3Tests(Pg13Server pg) : PgHardening3TestsBase(pg)
{
    [PostgresFact] public Task Key_name_clash_on_13() => A_new_table_s_key_name_taken_on_the_target_waits_and_the_plan_converges();
    [PostgresFact] public Task Default_partition_on_13() => A_new_partition_waits_while_the_default_partition_holds_its_rows();
    [PostgresFact] public Task Matview_indexes_on_13() => A_materialized_view_s_indexes_are_changed_without_replacing_it();
    [PostgresFact] public Task Not_null_empty_table_on_13() => A_not_null_column_with_no_default_is_added_to_an_empty_table();
    [PostgresFact] public Task Index_under_foreign_key_on_13() => A_changed_unique_index_a_foreign_key_needs_is_rebuilt_with_it();
    [PostgresFact] public Task Foreign_tables_on_13() => Foreign_tables_are_compared_and_what_needs_a_missing_one_is_held();
    [PostgresFact] public Task Sequence_range_on_13() => A_sequence_range_the_current_value_is_outside_of_is_held();
    [PostgresFact] public Task Index_collate_on_13() => An_index_s_collate_clause_is_no_difference_when_collations_are_ignored();
    [PostgresFact] public Task Replica_identity_on_13() => Replica_identity_is_compared_and_kept_through_an_index_rebuild();
    [PostgresFact] public Task Array_widening_on_13() => An_array_column_widens_as_its_element_does();
    [PostgresFact] public Task Schema_owner_on_13() => A_schema_owner_the_login_cannot_become_is_not_scripted();
    [PostgresFact] public Task View_rebuild_on_13() => Views_whose_columns_change_are_dropped_and_created_again_around_the_widening();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18Hardening3Tests(Pg18Server pg) : PgHardening3TestsBase(pg)
{
    [PostgresFact] public Task Key_name_clash_on_18() => A_new_table_s_key_name_taken_on_the_target_waits_and_the_plan_converges();
    [PostgresFact] public Task Default_partition_on_18() => A_new_partition_waits_while_the_default_partition_holds_its_rows();
    [PostgresFact] public Task Matview_indexes_on_18() => A_materialized_view_s_indexes_are_changed_without_replacing_it();
    [PostgresFact] public Task Destructive_index_dependent_on_18() => A_function_used_only_by_what_11_drops_goes_in_the_same_run();
    [PostgresFact] public Task Index_under_foreign_key_on_18() => A_changed_unique_index_a_foreign_key_needs_is_rebuilt_with_it();
    [PostgresFact] public Task Sql_standard_function_on_18() => A_column_a_sql_standard_function_reads_is_held_with_why();
    [PostgresFact] public Task Foreign_tables_on_18() => Foreign_tables_are_compared_and_what_needs_a_missing_one_is_held();
    [PostgresFact] public Task Sequence_range_on_18() => A_sequence_range_the_current_value_is_outside_of_is_held();
    [PostgresFact] public Task Index_collate_on_18() => An_index_s_collate_clause_is_no_difference_when_collations_are_ignored();
    [PostgresFact] public Task Replica_identity_on_18() => Replica_identity_is_compared_and_kept_through_an_index_rebuild();
    [PostgresFact] public Task Array_widening_on_18() => An_array_column_widens_as_its_element_does();
    [PostgresFact] public Task Schema_owner_on_18() => A_schema_owner_the_login_cannot_become_is_not_scripted();
    [PostgresFact] public Task View_rebuild_on_18() => Views_whose_columns_change_are_dropped_and_created_again_around_the_widening();
}
