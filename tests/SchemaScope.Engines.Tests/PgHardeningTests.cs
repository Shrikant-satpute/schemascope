using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// Hardening round 1 on real servers. Each case is a reported defect: a small drifted pair, compared, scripted with
/// every section, applied the way Write mode applies (each file in its own transaction, PgApplyExecutor, every
/// folder in run order, failures collected rather than stopping), compared again.
/// </summary>
public abstract class PgHardeningTestsBase(PgServer pg)
{
    /// <summary>Every file of the run (or of <paramref name="folders"/>), in the plan's run order, through the executor Apply uses.</summary>
    internal static async Task<List<string>> ApplyAsync(PgServer server, string database, ScriptOutcome outcome, IEnumerable<string>? folders = null)
    {
        var only = folders?.ToHashSet(StringComparer.Ordinal);
        var (files, _) = ApplyPlans.Build(outcome.Files, outcome.Folders);
        var failures = new List<string>();
        foreach (var f in files.Where(f => only is null || only.Contains(f.Folder)))
        {
            var file = new VerifiedFile(f, PgStatementSplitter.Instance.Split(ScriptWriter.ReadBack(f.FullPath)));
            if (await PgApplyExecutor.Instance.RunFileAsync(server.ConnectionString(database), database, file, default, () => { }) is { } error)
                failures.Add($"{f.Folder}\\{f.RelativePath}: {error}");
        }
        return failures;
    }

    /// <summary>Compare, script every section, apply every folder, compare again: (outcome, failures, rows still not Same).</summary>
    private async Task<(ScriptOutcome Outcome, List<string> Failures, List<string> Left)> RoundAsync(PgScriptPair pair, bool destructive = false)
    {
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: destructive);
        var failures = await ApplyAsync(pg, pair.Tgt, outcome);
        return (outcome, failures, PgScriptPair.NotSame(await pair.CompareAsync()));
    }

    private static string Held(ScriptOutcome o) => string.Join("\n", o.HeldBack.Select(h => $"{h.Section} | {h.Object} | {h.Reason}"));

    // ---------------- reader

    protected async Task Range_types_are_objects_and_their_constructors_are_not()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA rg;
            CREATE TYPE rg.r AS RANGE (subtype = int4);
            CREATE TABLE rg.t (x rg.r);
            CREATE SCHEMA ty;
            CREATE TYPE ty.rng AS RANGE (subtype = float8);
            CREATE TABLE ty.r (id integer, x ty.rng);
            """, """
            CREATE SCHEMA rg;
            CREATE TYPE rg.r AS RANGE (subtype = int8);
            CREATE TABLE rg.t (x integer);
            CREATE SCHEMA ty;
            """);
        var before = await pair.CompareAsync();
        // no constructor or multirange function is listed as a routine of its own
        Assert.DoesNotContain(before.Result.Rows, r => r.Type.IsModule());
        Assert.Contains("Range type rg.r: Different", PgScriptPair.NotSame(before));
        Assert.Contains("Range type ty.rng: MissingInTarget", PgScriptPair.NotSame(before));

        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain("LANGUAGE internal", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
        Assert.Contains("CREATE TYPE \"ty\".\"rng\" AS RANGE (SUBTYPE = double precision);", PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder));
        Assert.Contains(outcome.HeldBack, h => h.Object == "rg.r" && h.Reason.Contains("cannot be changed in place"));
        Assert.Equal(["Range type rg.r: Different", "Table rg.t: Different"], left);
        Assert.Equal(1, await pair.ScalarAsync("INSERT INTO ty.r VALUES (1, '[1.5,2.5)') RETURNING id"));
    }

    protected async Task Table_rules_are_compared_and_created()
    {
        const string tables = "CREATE SCHEMA r; CREATE TABLE r.t (id integer); CREATE TABLE r.log (id integer);";
        await using var pair = await PgScriptPair.CreateAsync(pg, tables + """
            CREATE RULE t_log AS ON INSERT TO r.t DO ALSO INSERT INTO r.log VALUES (NEW.id);
            CREATE RULE t_noupd AS ON UPDATE TO r.t DO INSTEAD NOTHING;
            """, tables);
        Assert.Equal(["Table r.t: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains("CREATE RULE t_noupd AS", PgScriptPair.Folder(outcome, PgScriptGenerator.TriggersFolder));
        Assert.Empty(left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO r.t VALUES (1); UPDATE r.t SET id = 2;");
        Assert.Equal(1, await pair.ScalarAsync("SELECT id FROM r.t"));
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM r.log"));
    }

    protected async Task Collations_are_objects_created_before_what_uses_them()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA ty;
            CREATE COLLATION ty.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            CREATE TABLE ty.coll (name text COLLATE ty.ci);
            CREATE SCHEMA co;
            CREATE COLLATION co.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            CREATE TABLE co.t (name text COLLATE co.ci);
            """, """
            CREATE SCHEMA ty;
            CREATE SCHEMA co;
            CREATE COLLATION co.ci (provider = icu, locale = 'und-u-ks-level1', deterministic = false);
            CREATE TABLE co.t (name text COLLATE co.ci);
            """);
        Assert.Contains("Collation co.ci: Different", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "co.ci" && h.Reason.Contains("cannot be changed in place"));
        Assert.Equal(["Collation co.ci: Different"], left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO ty.coll VALUES ('Abc')");
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM ty.coll WHERE name = 'abc'"));
    }

    // ---------------- 03 and 05

    protected async Task An_extension_s_operator_class_holds_the_table_that_needs_it()
    {
        const string existing = "CREATE SCHEMA hr; CREATE TABLE hr.room (id integer PRIMARY KEY, room text NOT NULL, during tsrange NOT NULL);";
        await using var pair = await PgScriptPair.CreateAsync(pg, existing + """
            CREATE EXTENSION IF NOT EXISTS btree_gist;
            ALTER TABLE hr.room ADD CONSTRAINT ex_room EXCLUDE USING gist (room WITH =, during WITH &&);
            CREATE TABLE hr.room_booking (
                id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                room text NOT NULL,
                during tsrange NOT NULL,
                CONSTRAINT ex_hr_room_booking_overlap EXCLUDE USING gist (room WITH =, during WITH &&));
            CREATE TABLE hr.after_it (id integer);
            """, existing);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "hr.room_booking" && h.Reason.Contains("extension btree_gist"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "hr.room.ex_room" && h.Reason.Contains("extension btree_gist"));
        Assert.Equal(["Extension btree_gist.btree_gist: MissingInTarget", "Table hr.room: Different", "Table hr.room_booking: MissingInTarget"], left);
    }

    protected async Task Partitions_and_inheritance_children_are_created_with_their_parent()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA m;
            CREATE TABLE m.p (id integer, k integer) PARTITION BY LIST (k);
            CREATE TABLE m.p1 PARTITION OF m.p FOR VALUES IN (1, 2);
            CREATE TABLE m.p_def PARTITION OF m.p DEFAULT;
            CREATE TABLE m.par (id integer PRIMARY KEY, label text);
            CREATE TABLE m.kid (extra text) INHERITS (m.par);
            CREATE SCHEMA audit;
            CREATE TABLE audit.event_log (
                id bigint GENERATED ALWAYS AS IDENTITY,
                occurred_at timestamp without time zone NOT NULL,
                tenant_id integer NOT NULL,
                payload jsonb,
                CONSTRAINT pk_audit_event_log PRIMARY KEY (id, occurred_at)
            ) PARTITION BY RANGE (occurred_at);
            CREATE TABLE audit.event_log_2026 PARTITION OF audit.event_log FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
            CREATE TABLE audit.event_log_2027 PARTITION OF audit.event_log FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');
            CREATE INDEX ix_audit_event_log_tenant ON audit.event_log (tenant_id, occurred_at);
            """, "CREATE SCHEMA m;");
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        var missing = PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder);
        Assert.Contains("CREATE TABLE \"m\".\"p1\" PARTITION OF m.p FOR VALUES IN (1, 2);", missing);
        Assert.Contains("INHERITS (m.par)", missing);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Reason.Contains("partition or inheritance child"));
        Assert.Empty(left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO m.p VALUES (1, 1), (2, 9); INSERT INTO audit.event_log (occurred_at, tenant_id) VALUES ('2026-05-01', 1);");
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM m.p_def"));
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM audit.event_log_2026"));
    }

    protected async Task Row_level_security_is_switched_on_only_with_its_policies()
    {
        const string common = "CREATE SCHEMA crm; CREATE TABLE crm.tenant_setting (id integer PRIMARY KEY, tenant_id integer NOT NULL);";
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            ALTER TABLE crm.tenant_setting ENABLE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON crm.tenant_setting USING (tenant_id = 1);
            CREATE TABLE crm.lead_score (id integer PRIMARY KEY, score integer);
            ALTER TABLE crm.lead_score ENABLE ROW LEVEL SECURITY;
            ALTER TABLE crm.lead_score FORCE ROW LEVEL SECURITY;
            CREATE POLICY lead_score_read ON crm.lead_score FOR SELECT TO PUBLIC USING (score > 10);
            """, common);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.Contains("lead_score") || h.Object.Contains("tenant"));
        Assert.Empty(left);
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM pg_policy WHERE polname = 'lead_score_read'"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT relforcerowsecurity FROM pg_class WHERE oid = 'crm.lead_score'::regclass"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT relrowsecurity FROM pg_class WHERE oid = 'crm.tenant_setting'::regclass"));
    }

    protected async Task A_sequence_s_owner_is_set_once_its_column_is_there()
    {
        // a partial apply left the sequence without its table
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA sales;
            CREATE SEQUENCE sales.invoice_no AS integer START WITH 5000 INCREMENT BY 5 MINVALUE 5000 MAXVALUE 999999 CYCLE;
            CREATE TABLE sales.invoice_ref (id integer PRIMARY KEY DEFAULT nextval('sales.invoice_no'), note text);
            ALTER SEQUENCE sales.invoice_no OWNED BY sales.invoice_ref.id;
            CREATE SCHEMA c;
            CREATE TABLE c.existing (id integer PRIMARY KEY, a integer, ser serial);
            """, """
            CREATE SCHEMA sales;
            CREATE SEQUENCE sales.invoice_no AS integer START WITH 5000 INCREMENT BY 5 MINVALUE 5000 MAXVALUE 999999 CYCLE;
            CREATE SCHEMA c;
            CREATE TABLE c.existing (id integer PRIMARY KEY, a integer);
            INSERT INTO c.existing VALUES (1, 1);
            """);
        var (first, failures, _) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains("ALTER SEQUENCE \"sales\".\"invoice_no\" OWNED BY \"sales\".\"invoice_ref\".\"id\";", PgScriptPair.Folder(first, PgScriptGenerator.ConstraintsFolder));
        // the serial column needs its sequence, which 03 makes after 01: it is the next run's, which then finishes it
        Assert.Contains(first.HeldBack, h => h.Object == "c.existing.ser" && h.Reason.Contains("next run"));
        var (_, again, left) = await RoundAsync(pair);
        Assert.Empty(again);
        Assert.Empty(left);
        var (last, _, _) = await RoundAsync(pair);
        Assert.DoesNotContain(last.Files, f => f.IsObjectFile);
    }

    protected async Task Existing_objects_are_brought_in_line_where_nothing_can_be_lost()
    {
        const string common = """
            CREATE SCHEMA core;
            CREATE TABLE core.tenant (id integer PRIMARY KEY);
            INSERT INTO core.tenant VALUES (1);
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE TYPE core.priority_level AS ENUM ('low', 'normal', 'urgent', 'high');
            CREATE SEQUENCE core.document_number INCREMENT BY 1;
            CREATE DOMAIN core.percentage AS integer CONSTRAINT percentage_range CHECK (VALUE BETWEEN 0 AND 100);
            CREATE TABLE core.price_list (id integer PRIMARY KEY, tenant_id integer REFERENCES core.tenant (id), status smallint NOT NULL DEFAULT 0,
                pct core.percentage, a integer, b integer) WITH (fillfactor = 70);
            CREATE INDEX ix_price_list_ab ON core.price_list (a, b);
            CREATE TABLE core.credit_note (id integer PRIMARY KEY);
            ALTER TABLE core.price_list ADD COLUMN level core.priority_level DEFAULT 'urgent';
            """, common + """
            CREATE TYPE core.priority_level AS ENUM ('low', 'normal', 'high');
            CREATE SEQUENCE core.document_number INCREMENT BY 10;
            CREATE DOMAIN core.percentage AS integer CONSTRAINT percentage_range CHECK (VALUE BETWEEN 0 AND 200);
            CREATE TABLE core.price_list (id integer PRIMARY KEY, tenant_id integer REFERENCES core.tenant (id) ON DELETE CASCADE,
                status smallint NOT NULL DEFAULT 1, pct core.percentage, a integer, b integer);
            CREATE INDEX ix_price_list_ab ON core.price_list (a);
            CREATE UNLOGGED TABLE core.credit_note (id integer PRIMARY KEY);
            INSERT INTO core.price_list VALUES (1, 1, 1, 50, 1, 2);
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Contains("ADD VALUE 'urgent' AFTER 'normal'", PgScriptPair.Folder(outcome, PgScriptGenerator.ColumnTypesFolder));
        Assert.Equal("1", (await pair.ScalarAsync("SELECT status::text FROM core.price_list"))!.ToString());
        Assert.Equal(50, await pair.ScalarAsync("SELECT pct FROM core.price_list"));
    }

    protected async Task Dependents_of_a_held_enum_or_a_moved_extension_are_held_too()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA ev;
            CREATE TYPE ev.e AS ENUM ('b', 'a', 'c');
            CREATE TABLE ev.t (id integer, v ev.e DEFAULT 'c');
            CREATE SCHEMA ext;
            CREATE EXTENSION pgcrypto SCHEMA ext;
            CREATE SCHEMA es;
            CREATE TABLE es.t (h bytea DEFAULT ext.digest('a', 'sha256'));
            CREATE FUNCTION es.hh(t text) RETURNS bytea LANGUAGE sql AS $$ SELECT ext.digest(t, 'sha1') $$;
            """, """
            CREATE SCHEMA ev;
            CREATE TYPE ev.e AS ENUM ('a', 'b');
            CREATE TABLE ev.t (id integer);
            CREATE EXTENSION pgcrypto;
            """);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        Assert.Contains(outcome.HeldBack, h => h.Object == "ev.e" && h.Reason.Contains("cannot reorder"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "ev.t.v" && h.Reason.Contains("label 'c'"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "es.t" && h.Reason.Contains("schema ext"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "es.hh(text)" && h.Reason.Contains("schema ext"));
        Assert.Contains("Table ev.t: Different", left);
    }

    // ---------------- 07, 08, 09

    protected async Task Two_triggers_share_one_new_function_and_a_view_gets_its_instead_of_trigger()
    {
        const string common = """
            CREATE SCHEMA st; CREATE TABLE st.a (id integer); CREATE TABLE st.b (id integer);
            CREATE SCHEMA tv; CREATE TABLE tv.base (id integer); CREATE VIEW tv.v AS SELECT id FROM tv.base;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, common + """
            CREATE FUNCTION st.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TRIGGER a_t BEFORE INSERT ON st.a FOR EACH ROW EXECUTE FUNCTION st.f();
            CREATE TRIGGER b_t BEFORE INSERT ON st.b FOR EACH ROW EXECUTE FUNCTION st.f();
            CREATE FUNCTION tv.trg() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO tv.base VALUES (NEW.id); RETURN NEW; END $$;
            CREATE TRIGGER v_ins INSTEAD OF INSERT ON tv.v FOR EACH ROW EXECUTE FUNCTION tv.trg();
            """, common);
        var (outcome, failures, left) = await RoundAsync(pair);
        Assert.Empty(failures);
        var triggers = PgScriptPair.Folder(outcome, PgScriptGenerator.TriggersFolder);
        Assert.Equal(triggers.IndexOf("CREATE FUNCTION st.f()", StringComparison.Ordinal), triggers.LastIndexOf("CREATE FUNCTION st.f()", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.StartsWith("tv.", StringComparison.Ordinal));
        Assert.Empty(left);
        await pg.ExecAsync(pair.Tgt, "INSERT INTO tv.v VALUES (7)");
        Assert.Equal(7, await pair.ScalarAsync("SELECT id FROM tv.base"));
    }

    protected async Task Layout_only_code_and_case_only_names_are_aligned_and_look_alikes_keep_their_values()
    {
        const string fmt = """
            CREATE FUNCTION s.f(x integer) RETURNS integer LANGUAGE plpgsql AS $$
            BEGIN
                RETURN x * 2; -- double it
            END;
            $$;
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA s;" + fmt + """
            CREATE TABLE s.r (code integer, "CreatedBy" text DEFAULT 'x');
            CREATE TABLE s.l (code integer, created_by text NOT NULL DEFAULT CURRENT_USER);
            """, "CREATE SCHEMA s;" + fmt.Replace("    RETURN x * 2; -- double it", "  RETURN   x*2;") + """
            CREATE TABLE s.r (code integer, createdby text DEFAULT 'x');
            CREATE TABLE s.l (code integer, "CreatedBy" text);
            INSERT INTO s.r VALUES (1, 'kept');
            INSERT INTO s.l VALUES (1, 'alice'), (2, 'bob');
            """);
        Assert.Contains("Scalar function s.f integer: FormattingOnly", PgScriptPair.NotSame(await pair.CompareAsync()));
        var (outcome, failures, left) = await RoundAsync(pair, destructive: true);
        Assert.Empty(failures);
        Assert.Contains("RENAME COLUMN \"createdby\" TO \"CreatedBy\"", PgScriptPair.Folder(outcome, PgScriptGenerator.FormattingTablesFolder));
        Assert.Contains("CREATE OR REPLACE FUNCTION s.f(x integer)", PgScriptPair.Folder(outcome, PgScriptGenerator.FormattingModulesFolder));
        Assert.Contains(outcome.HeldBack, h => h.Object == "s.l.created_by" && h.Reason.Contains("same column under another spelling"));
        Assert.DoesNotContain("DROP COLUMN", PgScriptPair.Folder(outcome, PgScriptGenerator.DestructiveFolder));
        Assert.Equal(["Table s.l: Different"], left);
        Assert.Equal("kept", await pair.ScalarAsync("SELECT \"CreatedBy\" FROM s.r"));
        Assert.Equal("alice,bob", await pair.ScalarAsync("SELECT string_agg(\"CreatedBy\", ',' ORDER BY code) FROM s.l"));
    }
}

[Collection(PgCollection.Name)]
public sealed class PgHardeningTests(PgServer pg) : PgHardeningTestsBase(pg)
{
    [PostgresFact] public Task Range_types_on_16() => Range_types_are_objects_and_their_constructors_are_not();
    [PostgresFact] public Task Rules_on_16() => Table_rules_are_compared_and_created();
    [PostgresFact] public Task Collations_on_16() => Collations_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Extension_operator_class_on_16() => An_extension_s_operator_class_holds_the_table_that_needs_it();
    [PostgresFact] public Task Partitions_on_16() => Partitions_and_inheritance_children_are_created_with_their_parent();
    [PostgresFact] public Task Row_level_security_on_16() => Row_level_security_is_switched_on_only_with_its_policies();
    [PostgresFact] public Task Sequence_owner_on_16() => A_sequence_s_owner_is_set_once_its_column_is_there();
    [PostgresFact] public Task Existing_objects_on_16() => Existing_objects_are_brought_in_line_where_nothing_can_be_lost();
    [PostgresFact] public Task Held_dependents_on_16() => Dependents_of_a_held_enum_or_a_moved_extension_are_held_too();
    [PostgresFact] public Task Triggers_on_16() => Two_triggers_share_one_new_function_and_a_view_gets_its_instead_of_trigger();
    [PostgresFact] public Task Formatting_and_names_on_16() => Layout_only_code_and_case_only_names_are_aligned_and_look_alikes_keep_their_values();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13HardeningTests(Pg13Server pg) : PgHardeningTestsBase(pg)
{
    [PostgresFact] public Task Range_types_on_13() => Range_types_are_objects_and_their_constructors_are_not();
    [PostgresFact] public Task Rules_on_13() => Table_rules_are_compared_and_created();
    [PostgresFact] public Task Collations_on_13() => Collations_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Extension_operator_class_on_13() => An_extension_s_operator_class_holds_the_table_that_needs_it();
    [PostgresFact] public Task Partitions_on_13() => Partitions_and_inheritance_children_are_created_with_their_parent();
    [PostgresFact] public Task Row_level_security_on_13() => Row_level_security_is_switched_on_only_with_its_policies();
    [PostgresFact] public Task Sequence_owner_on_13() => A_sequence_s_owner_is_set_once_its_column_is_there();
    [PostgresFact] public Task Existing_objects_on_13() => Existing_objects_are_brought_in_line_where_nothing_can_be_lost();
    [PostgresFact] public Task Held_dependents_on_13() => Dependents_of_a_held_enum_or_a_moved_extension_are_held_too();
    [PostgresFact] public Task Triggers_on_13() => Two_triggers_share_one_new_function_and_a_view_gets_its_instead_of_trigger();
    [PostgresFact] public Task Formatting_and_names_on_13() => Layout_only_code_and_case_only_names_are_aligned_and_look_alikes_keep_their_values();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18HardeningTests(Pg18Server pg) : PgHardeningTestsBase(pg)
{
    [PostgresFact] public Task Range_types_on_18() => Range_types_are_objects_and_their_constructors_are_not();
    [PostgresFact] public Task Rules_on_18() => Table_rules_are_compared_and_created();
    [PostgresFact] public Task Collations_on_18() => Collations_are_objects_created_before_what_uses_them();
    [PostgresFact] public Task Extension_operator_class_on_18() => An_extension_s_operator_class_holds_the_table_that_needs_it();
    [PostgresFact] public Task Partitions_on_18() => Partitions_and_inheritance_children_are_created_with_their_parent();
    [PostgresFact] public Task Row_level_security_on_18() => Row_level_security_is_switched_on_only_with_its_policies();
    [PostgresFact] public Task Sequence_owner_on_18() => A_sequence_s_owner_is_set_once_its_column_is_there();
    [PostgresFact] public Task Existing_objects_on_18() => Existing_objects_are_brought_in_line_where_nothing_can_be_lost();
    [PostgresFact] public Task Held_dependents_on_18() => Dependents_of_a_held_enum_or_a_moved_extension_are_held_too();
    [PostgresFact] public Task Triggers_on_18() => Two_triggers_share_one_new_function_and_a_view_gets_its_instead_of_trigger();
    [PostgresFact] public Task Formatting_and_names_on_18() => Layout_only_code_and_case_only_names_are_aligned_and_look_alikes_keep_their_values();
}

/// <summary>
/// One schema built the same way on PostgreSQL 16 and on 13 (a second container) compares as the same schema: the
/// deparse is read as every version spells it. A real change made on the 13 copy is still found, scripted and applied.
/// </summary>
[Collection(PgCollection.Name)]
public sealed class PgCrossVersionTests(PgServer pg16, Pg13Server pg13) : IClassFixture<Pg13Server>
{
    private const string Ddl = """
        CREATE SCHEMA app;
        CREATE TABLE app.t (id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, tenant_id integer NOT NULL, name text,
            created timestamp without time zone NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
            CONSTRAINT t_name CHECK (substring(name from 1 for 2) <> 'xx' AND position('a' in name) >= 0 AND trim(both ' ' from name) <> ''));
        CREATE VIEW app.v_t AS SELECT t.id, t.tenant_id FROM app.t t WHERE t.id > 0 ORDER BY t.id;
        CREATE VIEW app.v_plain AS SELECT id, created AT TIME ZONE 'utc' AS z FROM app.t;
        CREATE VIEW app.v_join AS SELECT a.id FROM app.t a JOIN app.t b ON a.id = b.id;
        CREATE MATERIALIZED VIEW app.mv AS SELECT t.tenant_id, count(*) AS n FROM app.t t GROUP BY t.tenant_id;
        CREATE PROCEDURE app.t_delete(p_tenant_id integer, p_id integer) LANGUAGE sql AS $$ DELETE FROM app.t WHERE tenant_id = p_tenant_id AND id = p_id $$;
        CREATE PROCEDURE app.t_touch(p_id integer, INOUT p_n integer DEFAULT 0) LANGUAGE plpgsql AS $$ BEGIN p_n := p_n + 1; END $$;
        """;

    [PostgresFact]
    public async Task The_same_ddl_on_16_and_13_compares_the_same_and_a_real_change_still_converges()
    {
        var src = await pg16.CreateDatabaseAsync("src");
        var tgt = await pg13.CreateDatabaseAsync("tgt");
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-cross-{Guid.NewGuid():n}");
        try
        {
            await pg16.ExecAsync(src, Ddl);
            await pg13.ExecAsync(tgt, Ddl);
            Task<CompareRun> Compare() => new CompareRunner(_ => new PgSchemaReader()).RunAsync(pg16.Source("source", src), [pg13.Source("t0", tgt)], new CompareOptions());
            var same = await Compare();
            Assert.False(same.Targets[0].Failed, same.Targets[0].Error);
            Assert.Empty(PgScriptPair.NotSame(same));

            // a real change on the 13 copy: found, scripted, applied, and nothing else is touched
            await pg13.ExecAsync(tgt, "CREATE OR REPLACE PROCEDURE app.t_delete(p_tenant_id integer, p_id integer) LANGUAGE sql AS $$ SELECT 1 $$;");
            var before = await Compare();
            Assert.Equal(["Procedure app.t_delete integer, integer: Different"], PgScriptPair.NotSame(before));
            var options = new ScriptOptions { Sections = [.. PgScriptPair.Every], OutputFolder = output };
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(
                new ScriptEndpoint("Source", pg16.Host, src, pg16.ConnectionString(src), DatabaseEngine.PostgreSql),
                new ScriptEndpoint("Target", pg13.Host, tgt, pg13.ConnectionString(tgt), DatabaseEngine.PostgreSql),
                before.Source, before.Targets[0], before.Result, 0, options, new HashSet<string>(), new Dictionary<string, LineChange>(),
                new CompareOptions()), default, null);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Empty(outcome.HeldBack);
            Assert.Empty(await PgHardeningTestsBase.ApplyAsync(pg13, tgt, outcome));
            Assert.Empty(PgScriptPair.NotSame(await Compare()));
        }
        finally
        {
            await pg16.DropAsync(src);
            await pg13.DropAsync(tgt);
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
