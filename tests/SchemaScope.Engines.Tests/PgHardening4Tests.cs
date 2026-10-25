using System.Net;
using System.Text.Json;
using Npgsql;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// Hardening round 4 on real servers. Each case is a reported defect: a small drifted pair, compared, scripted, applied
/// the way Write mode applies (<see cref="PgApplyExecutor"/>: each file in its own transaction, every folder in run
/// order, failures collected rather than stopping), compared again.
/// </summary>
public abstract class PgHardening4TestsBase(PgServer pg)
{
    private async Task<(ScriptOutcome Outcome, List<string> Failures, List<string> Left)> RoundAsync(PgScriptPair pair, bool destructive = false,
        CompareOptions? options = null)
    {
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(options), destructive: destructive, compare: options);
        var failures = await PgHardeningTestsBase.ApplyAsync(pg, pair.Tgt, outcome);
        return (outcome, failures, PgScriptPair.NotSame(await pair.CompareAsync(options)));
    }

    private static string Held(ScriptOutcome o) => string.Join("\n", o.HeldBack.Select(h => $"{h.Section} | {h.Object} | {h.Reason}"));

    protected async Task<int> MajorAsync(PgScriptPair pair) =>
        (int)(await pair.ScalarAsync("SELECT current_setting('server_version_num')::int") as int? ?? 0) / 10000;

    // ---------------- 05: an exclusion constraint whose WHERE reads a column 01 holds back

    protected async Task An_exclusion_constraint_waits_for_the_column_its_where_reads()
    {
        const string common = "CREATE EXTENSION IF NOT EXISTS btree_gist; CREATE SCHEMA ops;";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE TYPE ops.booking_state AS ENUM ('confirmed', 'cancelled');
            CREATE TABLE ops.booking (id integer PRIMARY KEY, hotel_id integer, room_no integer, during tstzrange, state ops.booking_state,
                CONSTRAINT ex_booking_overlap EXCLUDE USING gist (hotel_id WITH =, room_no WITH =, during WITH &&) WHERE (state <> 'cancelled'));
            """, common + """
            CREATE TABLE ops.booking (id integer PRIMARY KEY, hotel_id integer, room_no integer, during tstzrange);
            INSERT INTO ops.booking VALUES (1, 1, 1, '[2026-01-01,2026-01-05)');
            """);
        var (first, failures, _) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + Held(first));
        Assert.Contains(first.HeldBack, h => h.Object.Contains("ex_booking_overlap") && h.Reason.Contains("state, which the target will not have"));
        Assert.DoesNotContain("ex_booking_overlap", PgScriptPair.Folder(first, PgScriptGenerator.ConstraintsFolder));

        var (_, again, left) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(left);
    }

    // ---------------- syntax the target's version does not have

    protected async Task Unique_nulls_not_distinct_is_held_for_a_target_older_than_15()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA ops;
            CREATE TABLE ops.hotel (id integer PRIMARY KEY, code text, CONSTRAINT uq_hotel_code UNIQUE NULLS NOT DISTINCT (code));
            CREATE TABLE ops.room (id integer PRIMARY KEY, hotel_id integer REFERENCES ops.hotel (id), no text);
            CREATE TABLE ops.desk (id integer, code text);
            CREATE UNIQUE INDEX desk_code_ix ON ops.desk (code) NULLS NOT DISTINCT;
            """, """
            CREATE SCHEMA ops;
            CREATE TABLE ops.desk (id integer, code text);
            """);
        var before = await pair.CompareAsync();
        // the same catalog read, scripted for a PostgreSQL 13 target
        before.Targets[0].MajorVersion = 13;
        var outcome = await pair.GenerateAsync(before);
        Assert.Contains(outcome.HeldBack, h => h.Object == "ops.hotel" && h.Reason.Contains("NULLS NOT DISTINCT, which PostgreSQL 13 does not have (15 and later)"));
        Assert.Contains(outcome.HeldBack, h => h.Object.Contains("desk_code_ix") && h.Reason.Contains("NULLS NOT DISTINCT"));
        // and what needs the held table waits with it
        Assert.Contains(outcome.HeldBack, h => h.Object == "ops.room.room_hotel_id_fkey");
        var all = string.Concat(outcome.Files.Where(f => f.IsObjectFile).Select(f => File.ReadAllText(f.FullPath)));
        Assert.DoesNotContain("NULLS NOT DISTINCT", all);

        // a target that has it gets it
        var current = await pair.GenerateAsync(await pair.CompareAsync());
        Assert.Contains("NULLS NOT DISTINCT", PgScriptPair.Folder(current, PgScriptGenerator.MissingFolder));
    }

    protected async Task A_virtual_generated_column_is_held_for_a_target_older_than_18()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA g;
            CREATE TABLE g.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL);
            CREATE TABLE g.u (a integer);
            ALTER TABLE g.u ADD COLUMN v integer GENERATED ALWAYS AS (a + 1) VIRTUAL;
            """, "CREATE SCHEMA g; CREATE TABLE g.u (a integer);");
        var before = await pair.CompareAsync();
        before.Targets[0].MajorVersion = 17;
        var outcome = await pair.GenerateAsync(before);
        Assert.Contains(outcome.HeldBack, h => h.Object == "g.t" && h.Reason.Contains("VIRTUAL generated column, which PostgreSQL 17 does not have (18 and later)"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "g.u.v" && h.Reason.Contains("VIRTUAL generated column"));
        Assert.Empty(PgScriptPair.FilesOf(outcome, PgScriptGenerator.ColumnsFolder));
    }

    // ---------------- a partition's copy of its parent's identity (PostgreSQL 17 and later)

    protected async Task A_partition_s_identity_is_its_parent_s_on_every_version()
    {
        const string events = """
            CREATE SCHEMA ops;
            CREATE TABLE ops.event_log (id bigint GENERATED BY DEFAULT AS IDENTITY, at date NOT NULL, kind text) PARTITION BY RANGE (at);
            CREATE TABLE ops.event_log_2025q1 PARTITION OF ops.event_log FOR VALUES FROM ('2025-01-01') TO ('2025-04-01');
            CREATE TABLE ops.event_log_default PARTITION OF ops.event_log DEFAULT;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, events, "SELECT 1;");
        var before = await pair.CompareAsync();
        var parent = before.Source.Objects.Single(o => o.Name == "event_log");
        Assert.True(parent.Columns.Single(c => c.Name == "id").IsIdentity);
        foreach (var partition in before.Source.Objects.Where(o => o.Name.StartsWith("event_log_", StringComparison.Ordinal)))
            Assert.False(partition.Columns.Single(c => c.Name == "id").IsIdentity, partition.Name);

        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Reason.Contains("identity differs"));
        Assert.Empty(left);
    }

    // ---------------- 10: a changed parameter default goes through CREATE OR REPLACE

    protected async Task A_changed_parameter_default_is_replaced_in_place()
    {
        const string common = """
            CREATE SCHEMA ops;
            CREATE TYPE ops.booking_state AS ENUM ('confirmed', 'cancelled', 'pending');
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE FUNCTION ops.occupancy(p_hotel integer, VARIADIC p_states ops.booking_state[] DEFAULT ARRAY['confirmed', 'pending']::ops.booking_state[])
                RETURNS integer LANGUAGE sql AS $$ SELECT p_hotel + cardinality(p_states) $$;
            CREATE FUNCTION ops.keep(a integer, b integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$;
            """, common + """
            CREATE FUNCTION ops.occupancy(p_hotel integer, VARIADIC p_states ops.booking_state[] DEFAULT ARRAY['confirmed']::ops.booking_state[])
                RETURNS integer LANGUAGE sql AS $$ SELECT p_hotel + cardinality(p_states) $$;
            CREATE FUNCTION ops.keep(a integer, b integer DEFAULT 1) RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$;
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.Contains("occupancy"));
        // removing a default is what CREATE OR REPLACE refuses
        Assert.Contains(outcome.HeldBack, h => h.Object.Contains("ops.keep") && h.Reason.Contains("remove a parameter's default"));
        Assert.Equal(["Scalar function ops.keep integer, integer: Different"], left);
        Assert.Equal(3, await pair.ScalarAsync("SELECT ops.occupancy(1)"));
    }

    // ---------------- single-object scripts change a sequence or a domain as Create scripts does

    protected async Task A_sequence_and_a_domain_get_their_single_object_script()
    {
        const string common = "CREATE SCHEMA ops;";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE SEQUENCE ops.ticket_no CYCLE CACHE 10 MAXVALUE 90000;
            CREATE DOMAIN ops.positive_qty AS integer DEFAULT 1 CONSTRAINT positive_qty_check CHECK (VALUE > 0);
            """, common + """
            CREATE SEQUENCE ops.ticket_no NO CYCLE CACHE 1 MAXVALUE 50000;
            CREATE DOMAIN ops.positive_qty AS integer DEFAULT 2 CONSTRAINT positive_qty_check CHECK (VALUE >= 0);
            """);
        var before = await pair.CompareAsync();
        var session = new CompareSession { Source = before.Source, Targets = [.. before.Targets], Result = before.Result, Status = "done" };
        var source = new ScriptEndpoint("Source", pg.Host, pair.Src, pg.ConnectionString(pair.Src), DatabaseEngine.PostgreSql);
        var target = new ScriptEndpoint("Target", pg.Host, pair.Tgt, pg.ConnectionString(pair.Tgt), DatabaseEngine.PostgreSql);
        var root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-r4-{Guid.NewGuid():n}");
        try
        {
            foreach (var name in new[] { "ticket_no", "positive_qty" })
            {
                var row = before.Result.Rows.Single(r => r.Name == name);
                var deps = PgObjectScripts.Dependencies(session, row, 0);
                Assert.Equal("alter", deps.Plan);
                Assert.Empty(deps.Warnings);
                var one = await PgObjectScripts.RunAsync(session, row, 0, [], source, target, new PgScriptGenerator(), root, null, default);
                Assert.True(one.Success, one.Message);
                Assert.Contains(name == "ticket_no" ? "ALTER SEQUENCE \"ops\".\"ticket_no\"" : "ALTER DOMAIN \"ops\".\"positive_qty\"", one.Script);
                Assert.Empty(await PgHardeningTestsBase.ApplyAsync(pg, pair.Tgt, one.Generated!));
            }
            Assert.Empty(PgScriptPair.NotSame(await pair.CompareAsync()));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---------------- 05: FORCE row level security, and the README's Undo

    protected async Task Force_row_level_security_and_the_undo_text_say_what_they_do()
    {
        const string common = """
            CREATE SCHEMA ops;
            CREATE TABLE ops.hotel (id integer PRIMARY KEY);
            CREATE TABLE ops.cache_entry (id integer);
            ALTER TABLE ops.cache_entry ENABLE ROW LEVEL SECURITY;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE TABLE ops.room (id integer, hotel_id integer CONSTRAINT fk_room_hotel REFERENCES ops.hotel (id) ON DELETE CASCADE);
            """, common + """
            CREATE TABLE ops.room (id integer, hotel_id integer CONSTRAINT fk_room_hotel REFERENCES ops.hotel (id) ON DELETE RESTRICT);
            ALTER TABLE ops.cache_entry FORCE ROW LEVEL SECURITY;
            """);
        var (outcome, failures, _) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var force = Assert.Single(outcome.HeldBack, h => h.Object.Contains("forceRowSecurity"));
        Assert.Contains("stops the policies applying to the table's owner", force.Reason);
        Assert.DoesNotContain("every role see every row", force.Reason);

        var readme = File.ReadAllText(Path.Combine(outcome.Path!, "README.md"));
        Assert.DoesNotContain("05, 06 and 07 only add", readme);
        Assert.Contains("1 file(s) of 05_constraints drop something of the target's", readme);
    }

    // ---------------- a partition the target has detached

    protected async Task A_detached_partition_is_changed_as_the_table_it_now_is()
    {
        const string events = """
            CREATE SCHEMA ops;
            CREATE TABLE ops.event_log (id bigint, at date NOT NULL, kind text) PARTITION BY RANGE (at);
            CREATE TABLE ops.event_log_2025 PARTITION OF ops.event_log FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
            CREATE TABLE ops.event_log_default PARTITION OF ops.event_log DEFAULT;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, events, events + """
            ALTER TABLE ops.event_log ALTER COLUMN kind TYPE varchar(20);
            ALTER TABLE ops.event_log DETACH PARTITION ops.event_log_default;
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object == "ops.event_log_default.kind");
        Assert.Equal("text", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'ops.event_log_default'::regclass AND attname = 'kind'"));
        // what is left is that it hangs nowhere, which is said
        Assert.Equal(["Table ops.event_log_default: Different"], left);
        Assert.Contains(outcome.HeldBack, h => h.Object.Contains("partitionOf"));
    }

    // ---------------- Write mode keeps the login's search_path

    protected async Task A_function_body_that_names_objects_without_a_schema_runs_in_write_mode()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE public.lim (n integer);
            INSERT INTO public.lim VALUES (10);
            CREATE FUNCTION public.maxn() RETURNS integer LANGUAGE sql STABLE AS 'SELECT max(n) FROM lim';
            CREATE TABLE public.w (x integer CHECK (x <= public.maxn()));
            CREATE TABLE public.d (x integer, code integer DEFAULT public.maxn());
            CREATE TABLE public.t (id integer);
            CREATE TABLE public.z (x integer);
            """, """
            CREATE TABLE public.lim (n integer);
            INSERT INTO public.lim VALUES (10);
            CREATE TABLE public.w (x integer);
            INSERT INTO public.w VALUES (1);
            CREATE TABLE public.d (x integer);
            INSERT INTO public.d VALUES (1);
            CREATE TABLE public.t (id integer);
            INSERT INTO public.t VALUES (1), (2);
            CREATE TABLE public.z (x integer);
            INSERT INTO public.z VALUES (1);
            """);
        // a materialized view's query and an index's expression: from PostgreSQL 17 these run with search_path
        // pg_catalog, pg_temp on every session, so such bodies cannot be made on the source either
        var major = await MajorAsync(pair);
        if (major < 17)
            await pg.ExecAsync(pair.Src, """
                CREATE FUNCTION public.cnt() RETURNS bigint LANGUAGE sql STABLE AS 'SELECT count(*) FROM t';
                CREATE MATERIALIZED VIEW public.mv AS SELECT public.cnt() AS n;
                CREATE FUNCTION public.inc(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';
                CREATE FUNCTION public.dbl(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT inc($1) * 2';
                CREATE INDEX z_ix ON public.z (public.dbl(x));
                """);
        var (_, failures, _) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var (_, again, left) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(left);
        if (major < 17) Assert.Equal(2L, await pair.ScalarAsync("SELECT n FROM public.mv"));
        Assert.Equal(10, await pair.ScalarAsync("SELECT code FROM public.d"));
    }

    // ---------------- 04: a column a stored generated column reads (PostgreSQL 13 and 14 record it column to column)

    protected async Task A_column_a_generated_column_reads_is_held_on_every_version()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA w;
            CREATE TABLE w.t (id bigint, a bigint, b bigint GENERATED ALWAYS AS (a * 2) STORED);
            """, """
            CREATE SCHEMA w;
            CREATE TABLE w.t (id bigint, a integer, b bigint GENERATED ALWAYS AS (a * 2) STORED);
            INSERT INTO w.t (id, a) VALUES (1, 2);
            """);
        var (outcome, failures, _) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Contains(outcome.HeldBack, h => h.Object == "w.t.a" && h.Reason.Contains("a generated column of the table reads this column"));
    }

    // ---------------- objects that were not read: text search, operator classes, casts, extended statistics

    protected async Task A_text_search_configuration_is_created_before_what_uses_it()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA ts;
            CREATE TEXT SEARCH DICTIONARY ts.simple_dict (TEMPLATE = pg_catalog.simple, STOPWORDS = english);
            CREATE TEXT SEARCH CONFIGURATION ts.cfg (COPY = english);
            ALTER TEXT SEARCH CONFIGURATION ts.cfg ALTER MAPPING FOR asciiword WITH ts.simple_dict, english_stem;
            CREATE TABLE ts.doc (id integer, body text, tsv tsvector GENERATED ALWAYS AS (to_tsvector('ts.cfg'::regconfig, body)) STORED);
            CREATE INDEX doc_fts ON ts.doc USING gin (to_tsvector('ts.cfg'::regconfig, body));
            """, "CREATE SCHEMA ts;");
        var before = await pair.CompareAsync();
        Assert.Contains("Text search configuration ts.cfg: MissingInTarget", PgScriptPair.NotSame(before));
        Assert.Contains("Text search dictionary ts.simple_dict: MissingInTarget", PgScriptPair.NotSame(before));

        var (_, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Empty(left);
        // the target's configuration parses as the source's: the stop word goes, the rest is the simple dictionary's
        await pg.ExecAsync(pair.Tgt, "INSERT INTO ts.doc (id, body) VALUES (1, 'The running dogs')");
        Assert.Equal("'dogs':3 'running':2", (await pair.ScalarAsync("SELECT tsv::text FROM ts.doc"))?.ToString());
    }

    protected async Task A_user_operator_class_is_created_before_the_index_that_uses_it()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA oc;
            CREATE FUNCTION oc.cmp(integer, integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT CASE WHEN $1 > $2 THEN -1 WHEN $1 < $2 THEN 1 ELSE 0 END';
            CREATE OPERATOR CLASS oc.rev_ops FOR TYPE integer USING btree AS
                OPERATOR 1 > , OPERATOR 2 >= , OPERATOR 3 = , OPERATOR 4 <= , OPERATOR 5 < , FUNCTION 1 oc.cmp(integer, integer);
            CREATE TABLE oc.t (v integer);
            CREATE INDEX t_rev ON oc.t (v oc.rev_ops);
            """, "CREATE SCHEMA oc;");
        Assert.Contains("Operator class oc.rev_ops btree: MissingInTarget", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (_, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Empty(left);
    }

    protected async Task A_user_cast_is_created_before_the_default_that_needs_it()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA uc;
            CREATE TYPE uc.money2 AS (amount numeric);
            CREATE FUNCTION uc.to_m(numeric) RETURNS uc.money2 LANGUAGE sql IMMUTABLE AS 'SELECT ROW($1)::uc.money2';
            CREATE CAST (numeric AS uc.money2) WITH FUNCTION uc.to_m(numeric) AS ASSIGNMENT;
            CREATE TABLE uc.t (id integer, m uc.money2 DEFAULT 5::numeric);
            """, "CREATE SCHEMA uc;");
        Assert.Contains("Cast uc.numeric AS uc.money2: MissingInTarget", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (_, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Empty(left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO uc.t (id) VALUES (1)");
        Assert.Equal("(5)", (await pair.ScalarAsync("SELECT m::text FROM uc.t"))?.ToString());
    }

    protected async Task Extended_statistics_are_compared_and_scripted()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA st;
            CREATE TABLE st.t (a integer, b integer, c integer);
            CREATE STATISTICS st.ab (dependencies, ndistinct) ON a, b FROM st.t;
            CREATE TABLE st.n (a integer, b integer);
            CREATE STATISTICS st.n_ab ON a, b FROM st.n;
            ALTER STATISTICS st.n_ab SET STATISTICS 500;
            CREATE TABLE st.u (a integer, b integer);
            """, """
            CREATE SCHEMA st;
            CREATE TABLE st.t (a integer, b integer, c integer);
            CREATE STATISTICS st.ab (mcv) ON a, c FROM st.t;
            CREATE TABLE st.u (a integer, b integer);
            CREATE STATISTICS st.u_old ON a, b FROM st.u;
            """);
        var before = PgScriptPair.NotSame(await pair.CompareAsync());
        Assert.Contains("Table st.t: Different", before);
        Assert.Contains("Table st.u: Different", before);
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + Held(outcome));
        Assert.Empty(left);
        Assert.Equal(500, await pair.ScalarAsync("SELECT stxstattarget::int FROM pg_statistic_ext WHERE stxname = 'n_ab'"));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_statistic_ext WHERE stxname = 'u_old'"));
    }

    // ---------------- views and materialized views

    protected async Task A_view_column_default_is_compared_and_scripted()
    {
        const string common = "CREATE SCHEMA v; CREATE TABLE v.t (id integer, n text);";
        const string views = """CREATE VIEW v.w ("Ident", "Name") AS SELECT id, n FROM v.t; CREATE VIEW v.w2 AS SELECT id, n FROM v.t;""";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + views + """
            ALTER VIEW v.w ALTER COLUMN "Name" SET DEFAULT 'none';
            CREATE VIEW v.w3 AS SELECT id, n FROM v.t;
            ALTER VIEW v.w3 ALTER COLUMN n SET DEFAULT 'x';
            """, common + views + "ALTER VIEW v.w2 ALTER COLUMN n SET DEFAULT 'old';");
        var before = PgScriptPair.NotSame(await pair.CompareAsync());
        Assert.Contains("View v.w: Different", before);
        Assert.Contains("View v.w2: Different", before);
        var (_, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Empty(left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO v.w (\"Ident\") VALUES (1)");
        Assert.Equal("none", await pair.ScalarAsync("SELECT n FROM v.t WHERE id = 1"));
    }

    protected async Task A_materialized_view_with_no_data_is_created_unpopulated()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA v;
            CREATE MATERIALIZED VIEW v.m_nodata AS SELECT 1 AS x WITH NO DATA;
            CREATE MATERIALIZED VIEW v.m_data AS SELECT 1 AS x;
            """, "CREATE SCHEMA v; CREATE MATERIALIZED VIEW v.m_data AS SELECT 1 AS x WITH NO DATA;");
        Assert.Contains("Materialized view v.m_data: Different", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Contains("WITH NO DATA", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
        Assert.Equal(false, await pair.ScalarAsync("SELECT relispopulated FROM pg_class WHERE oid = 'v.m_nodata'::regclass"));
        Assert.Equal(["Materialized view v.m_data: Different"], left);
    }

    // ---------------- keys: their index's fill factor

    protected async Task A_key_s_fill_factor_is_compared_when_fill_factor_counts()
    {
        const string common = "CREATE SCHEMA f;";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE TABLE f.t (id integer PRIMARY KEY WITH (fillfactor = 80), code text CONSTRAINT t_code_key UNIQUE WITH (fillfactor = 70) DEFERRABLE);
            CREATE TABLE f.n (id integer PRIMARY KEY WITH (fillfactor = 60));
            """, common + "CREATE TABLE f.t (id integer PRIMARY KEY, code text CONSTRAINT t_code_key UNIQUE DEFERRABLE);");
        // ignored by default, as a table's own fillfactor is
        Assert.Equal(["Table f.n: MissingInTarget"], PgScriptPair.NotSame(await pair.CompareAsync()));

        var counts = new CompareOptions { IgnoreFillFactor = false };
        Assert.Contains("Table f.t: Different", PgScriptPair.NotSame(await pair.CompareAsync(counts)));
        var (_, failures, left) = await RoundAsync(pair, options: counts);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Empty(left);
        Assert.Equal("{fillfactor=80}", (await pair.ScalarAsync("SELECT reloptions::text FROM pg_class WHERE oid = 'f.t_pkey'::regclass"))?.ToString());
        Assert.Equal("{fillfactor=60}", (await pair.ScalarAsync("SELECT reloptions::text FROM pg_class WHERE oid = 'f.n_pkey'::regclass"))?.ToString());
    }

    // ---------------- 11: the views a rebuild creates again are checked against the source's text

    protected async Task A_rebuild_s_view_bodies_are_checked_as_the_source_s_text()
    {
        const string views = """
            CREATE VIEW sales.v_open AS SELECT id, total FROM sales."order" WHERE total > 0;
            CREATE MATERIALIZED VIEW sales.mv_totals AS SELECT sum(total) AS s FROM sales."order";
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA sales;
            CREATE TABLE sales."order" (id integer, total numeric(12,2));
            """ + views, """
            CREATE SCHEMA sales;
            CREATE TABLE sales."order" (id integer, total numeric(10,2));
            INSERT INTO sales."order" VALUES (1, 12.50);
            """ + views);
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + Held(outcome));
        Assert.Empty(left);
        Assert.Single(PgScriptPair.FilesOf(outcome, PgScriptGenerator.DestructiveFolder), f => f.RelativePath.Contains("rebuild_"));
        Assert.True(outcome.Validation.ModulesChecked >= 2, $"{outcome.Validation.ModulesExact} of {outcome.Validation.ModulesChecked}");
        Assert.Equal(outcome.Validation.ModulesChecked, outcome.Validation.ModulesExact);
    }

    // ---------------- PostgreSQL 18: STORED on one side, VIRTUAL on the other

    protected async Task A_stored_and_a_virtual_column_are_held_for_their_kind()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA g;
            CREATE TABLE g.t (a integer, b integer GENERATED ALWAYS AS (a * 2) VIRTUAL, c integer GENERATED ALWAYS AS (a + 1) STORED);
            """, """
            CREATE SCHEMA g;
            CREATE TABLE g.t (a integer, b integer GENERATED ALWAYS AS (a * 2) STORED, c integer GENERATED ALWAYS AS (a + 1) VIRTUAL);
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync());
        var b = Assert.Single(outcome.HeldBack, h => h.Object == "g.t.b");
        Assert.Equal("the expression is the same, but the column is VIRTUAL on the source and STORED on the target; PostgreSQL can only drop and re-add such a column", b.Reason);
        Assert.Contains(outcome.HeldBack, h => h.Object == "g.t.c" && h.Reason.Contains("STORED on the source and VIRTUAL on the target"));
    }

    // ---------------- a raw connection string that names no database

    protected async Task A_raw_string_without_a_database_is_confirmed_by_the_database_it_opens()
    {
        if (!SecretProtector.IsSupported) return;   // the saved connections hold their password with DPAPI
        var src = await pg.CreateDatabaseAsync("src");
        var schema = $"r4nodb_{Guid.NewGuid():n}"[..20];
        await using var o = await OwnerApp.StartAsync();
        try
        {
            await pg.ExecAsync(src, $"CREATE SCHEMA {schema}; CREATE TABLE {schema}.t (id integer);");
            await o.SetPreviewAsync(true);
            static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;
            async Task<string> Save(ConnectionSettings s) =>
                Body(await (await o.Post("/api/connections", s)).Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
            var sourceId = await Save(pg.Settings(src));
            var raw = pg.Settings("postgres");
            raw.Database = "";   // Connection string mode: the form's own Database box is empty
            raw.UseRawConnectionString = true;
            raw.RawConnectionString = EndpointGuard.Checked(
                $"Host={pg.Host};Port={pg.Port};Username={PgServer.User};Password={pg.Password};SSL Mode=Disable;Trust Server Certificate=true", pg);
            var targetId = await Save(raw);

            var started = await o.PostJson("/api/compare", $$"""{ "source": { "connectionId": "{{sourceId}}" }, "targets": [ { "connectionId": "{{targetId}}" } ] }""");
            var runId = Body(await started.Content.ReadAsStringAsync()).GetProperty("runId").GetString()!;
            for (var i = 0; i < 240 && Body(await o.Http.GetStringAsync($"/api/compare/{runId}")).GetProperty("status").GetString() == "running"; i++)
                await Task.Delay(250);

            Assert.Equal(HttpStatusCode.OK, (await o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
            var folder = JsonSerializer.Serialize(Path.Combine(o.Temp.Folder, "scripts"));
            var scripts = Body(await (await o.PostJson($"/api/compare/{runId}/scripts",
                $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["missingObjects"] } }""")).Content.ReadAsStringAsync());
            // the database the read reached, by the name the server gives it: never an empty name to type
            Assert.Equal("postgres", scripts.GetProperty("applyTarget").GetProperty("database").GetString());
            var planId = scripts.GetProperty("planId").GetString();
            Assert.False(string.IsNullOrEmpty(planId), scripts.GetRawText());

            var empty = await o.PostJson($"/api/compare/{runId}/apply",
                $$"""{ "planId": "{{planId}}", "targetIndex": 0, "sections": ["03_missing"], "confirmDatabase": "" }""");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Contains("Type the target database name, postgres", Body(await empty.Content.ReadAsStringAsync()).GetProperty("message").GetString());
            Assert.Empty(o.Temp.Store.GetApplyRuns());

            var typed = await o.PostJson($"/api/compare/{runId}/apply",
                $$"""{ "planId": "{{planId}}", "targetIndex": 0, "sections": ["03_missing"], "confirmDatabase": "postgres" }""");
            var applied = Body(await typed.Content.ReadAsStringAsync());
            Assert.True(applied.GetProperty("success").GetBoolean(), applied.GetRawText());
            await using var conn = new NpgsqlConnection(pg.ConnectionString("postgres"));
            await conn.OpenAsync();
            await using var check = new NpgsqlCommand($"SELECT to_regclass('{schema}.t') IS NOT NULL", conn);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            await pg.ExecAsync("postgres", $"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await pg.DropAsync(src);
        }
    }

    // ---------------- a Passfile that cannot be read

    protected async Task A_passfile_s_path_is_never_echoed_by_the_connection_test()
    {
        var svc = new PgConnectionService();
        var raw = $"Host={pg.Host};Port={pg.Port};Username={PgServer.User};Database=postgres;SSL Mode=Disable;Trust Server Certificate=true;" +
                  "Passfile=C:\\nope\\Zq9PassfileSecret.txt";
        var cs = svc.BuildConnectionString(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, UseRawConnectionString = true, RawConnectionString = EndpointGuard.Checked(raw, pg),
        });
        var result = await svc.TestAsync(cs);
        Assert.False(result.Success);
        Assert.DoesNotContain("Zq9PassfileSecret", result.Message ?? "");
        Assert.DoesNotContain("nope", result.Message ?? "");
    }
}

[Collection(PgCollection.Name)]
public sealed class PgHardening4Tests(PgServer pg) : PgHardening4TestsBase(pg)
{
    [PostgresFact] public Task Exclusion_where_on_16() => An_exclusion_constraint_waits_for_the_column_its_where_reads();
    [PostgresFact] public Task Nulls_not_distinct_on_16() => Unique_nulls_not_distinct_is_held_for_a_target_older_than_15();
    [PostgresFact] public Task Partition_identity_on_16() => A_partition_s_identity_is_its_parent_s_on_every_version();
    [PostgresFact] public Task Parameter_default_on_16() => A_changed_parameter_default_is_replaced_in_place();
    [PostgresFact] public Task Object_script_sequence_domain_on_16() => A_sequence_and_a_domain_get_their_single_object_script();
    [PostgresFact] public Task Force_rls_and_undo_on_16() => Force_row_level_security_and_the_undo_text_say_what_they_do();
    [PostgresFact] public Task Detached_partition_on_16() => A_detached_partition_is_changed_as_the_table_it_now_is();
    [PostgresFact] public Task Search_path_on_16() => A_function_body_that_names_objects_without_a_schema_runs_in_write_mode();
    [PostgresFact] public Task Generated_reader_on_16() => A_column_a_generated_column_reads_is_held_on_every_version();
    [PostgresFact] public Task Text_search_on_16() => A_text_search_configuration_is_created_before_what_uses_it();
    [PostgresFact] public Task Operator_class_on_16() => A_user_operator_class_is_created_before_the_index_that_uses_it();
    [PostgresFact] public Task Cast_on_16() => A_user_cast_is_created_before_the_default_that_needs_it();
    [PostgresFact] public Task Statistics_on_16() => Extended_statistics_are_compared_and_scripted();
    [PostgresFact] public Task View_default_on_16() => A_view_column_default_is_compared_and_scripted();
    [PostgresFact] public Task Matview_no_data_on_16() => A_materialized_view_with_no_data_is_created_unpopulated();
    [PostgresFact] public Task Key_fillfactor_on_16() => A_key_s_fill_factor_is_compared_when_fill_factor_counts();
    [PostgresFact] public Task Rebuild_bodies_on_16() => A_rebuild_s_view_bodies_are_checked_as_the_source_s_text();
    [PostgresFact] public Task Passfile_on_16() => A_passfile_s_path_is_never_echoed_by_the_connection_test();
    [PostgresFact] public Task Raw_string_without_database_on_16() => A_raw_string_without_a_database_is_confirmed_by_the_database_it_opens();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13Hardening4Tests(Pg13Server pg) : PgHardening4TestsBase(pg)
{
    [PostgresFact] public Task Exclusion_where_on_13() => An_exclusion_constraint_waits_for_the_column_its_where_reads();
    [PostgresFact] public Task Parameter_default_on_13() => A_changed_parameter_default_is_replaced_in_place();
    [PostgresFact] public Task Object_script_sequence_domain_on_13() => A_sequence_and_a_domain_get_their_single_object_script();
    [PostgresFact] public Task Force_rls_and_undo_on_13() => Force_row_level_security_and_the_undo_text_say_what_they_do();
    [PostgresFact] public Task Detached_partition_on_13() => A_detached_partition_is_changed_as_the_table_it_now_is();
    [PostgresFact] public Task Search_path_on_13() => A_function_body_that_names_objects_without_a_schema_runs_in_write_mode();
    [PostgresFact] public Task Generated_reader_on_13() => A_column_a_generated_column_reads_is_held_on_every_version();
    [PostgresFact] public Task Text_search_on_13() => A_text_search_configuration_is_created_before_what_uses_it();
    [PostgresFact] public Task Operator_class_on_13() => A_user_operator_class_is_created_before_the_index_that_uses_it();
    [PostgresFact] public Task Cast_on_13() => A_user_cast_is_created_before_the_default_that_needs_it();
    [PostgresFact] public Task Statistics_on_13() => Extended_statistics_are_compared_and_scripted();
    [PostgresFact] public Task View_default_on_13() => A_view_column_default_is_compared_and_scripted();
    [PostgresFact] public Task Matview_no_data_on_13() => A_materialized_view_with_no_data_is_created_unpopulated();
    [PostgresFact] public Task Key_fillfactor_on_13() => A_key_s_fill_factor_is_compared_when_fill_factor_counts();
    [PostgresFact] public Task Rebuild_bodies_on_13() => A_rebuild_s_view_bodies_are_checked_as_the_source_s_text();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18Hardening4Tests(Pg18Server pg) : PgHardening4TestsBase(pg)
{
    [PostgresFact] public Task Exclusion_where_on_18() => An_exclusion_constraint_waits_for_the_column_its_where_reads();
    [PostgresFact] public Task Nulls_not_distinct_on_18() => Unique_nulls_not_distinct_is_held_for_a_target_older_than_15();
    [PostgresFact] public Task Virtual_column_on_18() => A_virtual_generated_column_is_held_for_a_target_older_than_18();
    [PostgresFact] public Task Partition_identity_on_18() => A_partition_s_identity_is_its_parent_s_on_every_version();
    [PostgresFact] public Task Parameter_default_on_18() => A_changed_parameter_default_is_replaced_in_place();
    [PostgresFact] public Task Object_script_sequence_domain_on_18() => A_sequence_and_a_domain_get_their_single_object_script();
    [PostgresFact] public Task Detached_partition_on_18() => A_detached_partition_is_changed_as_the_table_it_now_is();
    [PostgresFact] public Task Search_path_on_18() => A_function_body_that_names_objects_without_a_schema_runs_in_write_mode();
    [PostgresFact] public Task Generated_reader_on_18() => A_column_a_generated_column_reads_is_held_on_every_version();
    [PostgresFact] public Task Text_search_on_18() => A_text_search_configuration_is_created_before_what_uses_it();
    [PostgresFact] public Task Operator_class_on_18() => A_user_operator_class_is_created_before_the_index_that_uses_it();
    [PostgresFact] public Task Cast_on_18() => A_user_cast_is_created_before_the_default_that_needs_it();
    [PostgresFact] public Task Statistics_on_18() => Extended_statistics_are_compared_and_scripted();
    [PostgresFact] public Task View_default_on_18() => A_view_column_default_is_compared_and_scripted();
    [PostgresFact] public Task Matview_no_data_on_18() => A_materialized_view_with_no_data_is_created_unpopulated();
    [PostgresFact] public Task Key_fillfactor_on_18() => A_key_s_fill_factor_is_compared_when_fill_factor_counts();
    [PostgresFact] public Task Rebuild_bodies_on_18() => A_rebuild_s_view_bodies_are_checked_as_the_source_s_text();
    [PostgresFact] public Task Stored_virtual_reason_on_18() => A_stored_and_a_virtual_column_are_held_for_their_kind();
}
