using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// The R2 review's cases on real servers, each a small drifted pair run end to end: generate, run every file with
/// psql (failures collected), compare again. Each case once failed in a way the big scenario did not show.
/// </summary>
public abstract class PgScriptReviewTestsBase(PgServer pg)
{
    /// <summary>Compare, generate, run everything, compare again: (outcome, failures, rows still not Same).</summary>
    private static async Task<(ScriptOutcome Outcome, List<string> Failures, List<string> Left)> RoundTripAsync(PgScriptPair pair,
        CompareOptions? compare = null)
    {
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(compare), compare: compare);
        var failures = await pair.RunAsync(outcome);
        return (outcome, failures, PgScriptPair.NotSame(await pair.CompareAsync(compare)));
    }

    protected async Task Missing_code_fails_rather_than_replace_what_the_target_gained()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA "Sales";
            CREATE FUNCTION "Sales".f() RETURNS text LANGUAGE sql AS $$ SELECT 'source' $$;
            CREATE PROCEDURE "Sales".p() LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
            CREATE VIEW "Sales".v AS SELECT 'source'::text AS who;
            CREATE TABLE "Sales".t (id integer, x integer);
            CREATE FUNCTION "Sales".trg() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.x := 1; RETURN NEW; END $$;
            CREATE TRIGGER t_trg BEFORE INSERT ON "Sales".t FOR EACH ROW EXECUTE FUNCTION "Sales".trg();
            """, """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales".t (id integer, x integer);
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync());
        var missing = PgScriptPair.Folder(outcome, PgScriptGenerator.MissingFolder);
        var triggers = PgScriptPair.Folder(outcome, PgScriptGenerator.TriggersFolder);
        Assert.DoesNotContain("OR REPLACE", missing);
        Assert.DoesNotContain("OR REPLACE", triggers);
        Assert.Contains("CREATE FUNCTION \"Sales\".f()", missing);
        Assert.Contains("CREATE VIEW \"Sales\".\"v\"", missing);
        Assert.Contains("CREATE FUNCTION \"Sales\".trg()", triggers);
        Assert.True(outcome.Validation.ModulesChecked > 0 && outcome.Validation.ModulesChecked == outcome.Validation.ModulesExact);

        // after the compare the target gains its own f, v and trg: running 03 and 07 again must fail and change nothing
        await pg.ExecAsync(pair.Tgt, """
            CREATE FUNCTION "Sales".f() RETURNS text LANGUAGE sql AS $$ SELECT 'target' $$;
            CREATE VIEW "Sales".v AS SELECT 'target'::text AS who;
            CREATE FUNCTION "Sales".trg() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.x := 2; RETURN NEW; END $$;
            """);
        var failures = await pair.RunAsync(outcome, [PgScriptGenerator.MissingFolder, PgScriptGenerator.TriggersFolder]);
        Assert.Contains(failures, f => f.Contains("function \"f\" already exists"));
        Assert.Contains(failures, f => f.Contains("relation \"v\" already exists"));
        Assert.Contains(failures, f => f.Contains("function \"trg\" already exists"));
        Assert.Equal("target", await pair.ScalarAsync("SELECT \"Sales\".f()"));
        Assert.Equal("target", await pair.ScalarAsync("SELECT who FROM \"Sales\".v"));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_trigger WHERE tgname = 't_trg'"));
        Assert.Contains("NEW.x := 2", (string)(await pair.ScalarAsync("SELECT prosrc FROM pg_proc WHERE proname = 'trg'"))!);
    }

    protected async Task A_check_calling_a_function_that_is_not_immutable_is_never_probed()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE t (id integer, name text,
                CONSTRAINT t_cancel CHECK (pg_cancel_backend(0) IS NOT NULL OR true),
                CONSTRAINT t_len CHECK (length(name) > 0),
                CONSTRAINT t_pos CHECK (id > 0));
            """, """
            CREATE TABLE t (id integer, name text);
            INSERT INTO t VALUES (1, 'a'), (2, 'b');
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), [ScriptSection.Constraints]);
        Assert.Contains(outcome.Warnings, w => w.Contains("t_cancel") && w.Contains("not checked") && w.Contains("pg_cancel_backend") &&
                                               w.Contains("IMMUTABLE"));
        Assert.DoesNotContain(outcome.Warnings, w => w.Contains("t_len") || w.Contains("t_pos"));
        var readme = File.ReadAllText(Path.Combine(outcome.Path!, "README.md"));
        Assert.Contains("t_len", readme);
        Assert.Contains("t_pos", readme);
    }

    protected async Task An_index_on_a_partitioned_table_is_built_on_every_partition()
    {
        const string tables = """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales".ptab (id integer NOT NULL, amount integer) PARTITION BY RANGE (id);
            CREATE TABLE "Sales".ptab_p1 PARTITION OF "Sales".ptab FOR VALUES FROM (0) TO (100);
            CREATE TABLE "Sales".ptab_p2 PARTITION OF "Sales".ptab FOR VALUES FROM (100) TO (200);
            """;
        await using var pair = await PgScriptPair.CreateAsync(pg, tables + """
            CREATE INDEX ptab_amount_ix ON "Sales".ptab (amount);
            """, tables + "INSERT INTO \"Sales\".ptab VALUES (1, 10), (150, 20);");
        var (outcome, failures, left) = await RoundTripAsync(pair);
        Assert.Empty(failures);
        Assert.DoesNotContain(" ON ONLY ", PgScriptPair.Folder(outcome, PgScriptGenerator.IndexesFolder));
        Assert.Equal(true, await pair.ScalarAsync("SELECT indisvalid FROM pg_index WHERE indexrelid = '\"Sales\".ptab_amount_ix'::regclass"));
        Assert.Equal(2L, await pair.ScalarAsync(
            "SELECT count(*) FROM pg_inherits i JOIN pg_index x ON x.indexrelid = i.inhrelid WHERE i.inhparent = '\"Sales\".ptab_amount_ix'::regclass AND x.indisvalid"));
        Assert.Empty(left);
        Assert.DoesNotContain(outcome.HeldBack, h => h.Object.StartsWith("Sales.ptab", StringComparison.Ordinal));
    }

    protected async Task A_foreign_key_to_a_unique_index_runs_after_the_index()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales".lookup (code text NOT NULL);
            CREATE UNIQUE INDEX lookup_code_ux ON "Sales".lookup (code);
            CREATE TABLE "Sales".uses_lookup (id integer PRIMARY KEY, code text REFERENCES "Sales".lookup (code));
            CREATE TABLE "Sales".ref2 (code text NOT NULL);
            CREATE UNIQUE INDEX ref2_code_ux ON "Sales".ref2 (code);
            CREATE TABLE "Sales".uses_ref2 (id integer PRIMARY KEY, code text REFERENCES "Sales".ref2 (code));
            """, """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales".ref2 (code text NOT NULL);
            CREATE TABLE "Sales".uses_ref2 (id integer PRIMARY KEY, code text);
            INSERT INTO "Sales".ref2 VALUES ('a');
            INSERT INTO "Sales".uses_ref2 VALUES (1, 'a');
            """);
        var (outcome, failures, left) = await RoundTripAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Empty(outcome.HeldBack);
        var indexes = PgScriptPair.Folder(outcome, PgScriptGenerator.IndexesFolder);
        Assert.True(indexes.IndexOf("CREATE UNIQUE INDEX lookup_code_ux", StringComparison.Ordinal) <
                    indexes.IndexOf("ADD CONSTRAINT \"uses_lookup_code_fkey\"", StringComparison.Ordinal));
        Assert.True(indexes.IndexOf("ADD CONSTRAINT \"uses_ref2_code_fkey\"", StringComparison.Ordinal) <
                    indexes.IndexOf("VALIDATE CONSTRAINT \"uses_ref2_code_fkey\"", StringComparison.Ordinal));
    }

    protected async Task Changed_views_are_replaced_in_dependency_order()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA app;
            CREATE TABLE app.t (id integer, name text);
            CREATE VIEW app.b_view AS SELECT id, name FROM app.t;
            CREATE VIEW app.a_view AS SELECT id, name FROM app.b_view;
            """, """
            CREATE SCHEMA app;
            CREATE TABLE app.t (id integer, name text);
            CREATE VIEW app.b_view AS SELECT id FROM app.t;
            CREATE VIEW app.a_view AS SELECT id FROM app.b_view;
            """);
        var (outcome, failures, left) = await RoundTripAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal(["b_view", "a_view"], PgScriptPair.FilesOf(outcome, PgScriptGenerator.DifferentFolder)
            .Select(f => File.ReadAllText(f.FullPath).Contains("VIEW \"app\".\"b_view\"") ? "b_view" : "a_view"));
    }

    protected async Task New_columns_keep_the_source_s_order()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales"."Customer" (id integer, "Name" text, tags text, created date, seq_no integer,
                name_len integer GENERATED ALWAYS AS (length("Name")) STORED, "Mixed Col" text);
            """, """
            CREATE SCHEMA "Sales";
            CREATE TABLE "Sales"."Customer" (id integer, "Name" text);
            INSERT INTO "Sales"."Customer" VALUES (1, 'Ann');
            """);
        var (outcome, failures, left) = await RoundTripAsync(pair);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Empty(outcome.HeldBack);
    }

    protected async Task Identity_limits_are_kept_and_compared()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE idt (id integer GENERATED ALWAYS AS IDENTITY (START WITH 5 INCREMENT BY 2 MAXVALUE 99999 CACHE 3 CYCLE), v integer);
            CREATE TABLE idt2 (v integer, id2 bigint GENERATED BY DEFAULT AS IDENTITY (MINVALUE -10 START WITH -10 MAXVALUE 500));
            """, """
            CREATE TABLE idt2 (v integer);
            INSERT INTO idt2 VALUES (1);
            """);
        var exact = new CompareOptions { IgnoreIdentitySeed = false };
        var (_, failures, left) = await RoundTripAsync(pair, exact);
        Assert.Empty(failures);
        Assert.Empty(left);
        Assert.Equal(99999L, await pair.ScalarAsync("SELECT seqmax FROM pg_sequence WHERE seqrelid = pg_get_serial_sequence('idt', 'id')::regclass"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT seqcycle FROM pg_sequence WHERE seqrelid = pg_get_serial_sequence('idt', 'id')::regclass"));
        Assert.Equal(-10L, await pair.ScalarAsync("SELECT seqmin FROM pg_sequence WHERE seqrelid = pg_get_serial_sequence('idt2', 'id2')::regclass"));

        // a limit that differs is a difference the compare shows
        await pg.ExecAsync(pair.Tgt, "ALTER TABLE idt ALTER COLUMN id SET MAXVALUE 5000;");
        Assert.Contains("Table public.idt: Different", PgScriptPair.NotSame(await pair.CompareAsync(exact)));
        Assert.Empty(PgScriptPair.NotSame(await pair.CompareAsync()));   // ignored with the identity seed, as by default
    }
}

[Collection(PgCollection.Name)]
public sealed class PgScriptReviewTests(PgServer pg) : PgScriptReviewTestsBase(pg)
{
    [PostgresFact] public Task Missing_code_fails_rather_than_replace_on_16() => Missing_code_fails_rather_than_replace_what_the_target_gained();
    [PostgresFact] public Task Volatile_checks_are_not_probed_on_16() => A_check_calling_a_function_that_is_not_immutable_is_never_probed();
    [PostgresFact] public Task Partitioned_index_on_16() => An_index_on_a_partitioned_table_is_built_on_every_partition();
    [PostgresFact] public Task Foreign_key_to_a_unique_index_on_16() => A_foreign_key_to_a_unique_index_runs_after_the_index();
    [PostgresFact] public Task Dependent_changed_views_on_16() => Changed_views_are_replaced_in_dependency_order();
    [PostgresFact] public Task Column_order_on_16() => New_columns_keep_the_source_s_order();
    [PostgresFact] public Task Identity_limits_on_16() => Identity_limits_are_kept_and_compared();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13ScriptReviewTests(Pg13Server pg) : PgScriptReviewTestsBase(pg)
{
    [PostgresFact] public Task Missing_code_fails_rather_than_replace_on_13() => Missing_code_fails_rather_than_replace_what_the_target_gained();
    [PostgresFact] public Task Volatile_checks_are_not_probed_on_13() => A_check_calling_a_function_that_is_not_immutable_is_never_probed();
    [PostgresFact] public Task Partitioned_index_on_13() => An_index_on_a_partitioned_table_is_built_on_every_partition();
    [PostgresFact] public Task Foreign_key_to_a_unique_index_on_13() => A_foreign_key_to_a_unique_index_runs_after_the_index();
    [PostgresFact] public Task Dependent_changed_views_on_13() => Changed_views_are_replaced_in_dependency_order();
    [PostgresFact] public Task Column_order_on_13() => New_columns_keep_the_source_s_order();
    [PostgresFact] public Task Identity_limits_on_13() => Identity_limits_are_kept_and_compared();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18ScriptReviewTests(Pg18Server pg) : PgScriptReviewTestsBase(pg)
{
    [PostgresFact] public Task Missing_code_fails_rather_than_replace_on_18() => Missing_code_fails_rather_than_replace_what_the_target_gained();
    [PostgresFact] public Task Volatile_checks_are_not_probed_on_18() => A_check_calling_a_function_that_is_not_immutable_is_never_probed();
    [PostgresFact] public Task Partitioned_index_on_18() => An_index_on_a_partitioned_table_is_built_on_every_partition();
    [PostgresFact] public Task Foreign_key_to_a_unique_index_on_18() => A_foreign_key_to_a_unique_index_runs_after_the_index();
    [PostgresFact] public Task Dependent_changed_views_on_18() => Changed_views_are_replaced_in_dependency_order();
    [PostgresFact] public Task Column_order_on_18() => New_columns_keep_the_source_s_order();
    [PostgresFact] public Task Identity_limits_on_18() => Identity_limits_are_kept_and_compared();
}
