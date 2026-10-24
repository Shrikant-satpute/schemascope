using System.Net;
using System.Text.Json;
using Npgsql;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// 11_destructive on PostgreSQL, the DestructiveSection, DestructiveSafety and WriteMode scenarios on real servers:
/// what it drops and in which order, what it must never drop (a name the source has in another letter case, anything
/// still in use), the data probes, the check repeated inside a file, RESTRICT stopping a drop of something SchemaScope
/// could not see, and the opt-in over HTTP - write mode, the second tick - to a 100% compare.
/// </summary>
public abstract class PgDestructiveTestsBase(PgServer pg)
{
    private static readonly string Folder = PgScriptGenerator.DestructiveFolder;

    /// <summary>The file names of 11 in run order, without the number: "drop_table_app.extra".</summary>
    private static List<string> Names(ScriptOutcome outcome) =>
        [.. PgScriptPair.FilesOf(outcome, Folder).Select(f => Path.GetFileNameWithoutExtension(f.RelativePath)[4..])];

    private static string Text(ScriptOutcome outcome, string name) =>
        File.ReadAllText(PgScriptPair.FilesOf(outcome, Folder).Single(f => Path.GetFileNameWithoutExtension(f.RelativePath)[4..] == name).FullPath);

    private const string Source = """
        CREATE SCHEMA app;
        CREATE TABLE app.parent (
            id integer PRIMARY KEY,
            code varchar(10),
            amount numeric(10,2),
            note text,
            status text,
            CONSTRAINT parent_status_check CHECK (status IN ('A', 'B', 'C'))
        );
        CREATE TABLE app.child (
            id integer PRIMARY KEY,
            parent_id integer REFERENCES app.parent (id),
            qty integer NOT NULL
        );
        CREATE INDEX child_qty_ix ON app.child (qty);
        CREATE VIEW app.v_common AS SELECT 1 AS id;
        """;

    private const string Target = """
        CREATE SCHEMA app;
        CREATE TABLE app.parent (
            id integer PRIMARY KEY,
            code varchar(20),
            amount numeric(10,3),
            note text DEFAULT 'none',
            status text,
            extra_col text,
            CONSTRAINT parent_status_check CHECK (status IN ('A', 'B'))
        );
        INSERT INTO app.parent VALUES (1, 'abc', 1.255, 'n', 'A', 'x'), (2, 'de', 2, NULL, 'B', NULL);
        CREATE TABLE app.child (
            id integer PRIMARY KEY,
            parent_id integer REFERENCES app.parent (id),
            qty integer,
            CONSTRAINT child_qty_check CHECK (qty > 0),
            CONSTRAINT child_parent_uq UNIQUE (parent_id)
        );
        INSERT INTO app.child VALUES (1, 1, 5), (2, 2, 7);
        CREATE INDEX child_qty_ix ON app.child (qty);
        CREATE INDEX child_extra_ix ON app.child (parent_id, qty);
        CREATE TABLE app.extra (id integer PRIMARY KEY, name text);
        INSERT INTO app.extra VALUES (1, 'a'), (2, 'b'), (3, 'c');
        CREATE TABLE app.extra_child (id integer PRIMARY KEY, extra_id integer REFERENCES app.extra (id));
        CREATE VIEW app.v_extra AS SELECT id, name FROM app.extra;
        CREATE TABLE app.kept (id integer PRIMARY KEY);
        CREATE VIEW app.v_common AS SELECT id FROM app.kept;
        CREATE FUNCTION app.f_extra(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
        CREATE FUNCTION app.trg_parent() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER tr_parent BEFORE UPDATE OF extra_col ON app.parent FOR EACH ROW EXECUTE FUNCTION app.trg_parent();
        CREATE SEQUENCE app.seq_extra START 50;
        CREATE TYPE app.mood_extra AS ENUM ('a', 'b');
        CREATE FUNCTION app.uses_mood(m app.mood_extra) RETURNS text LANGUAGE sql AS $$ SELECT m::text $$;
        CREATE DOMAIN app.d_extra AS integer CHECK (VALUE > 0);
        """;

    protected async Task Drops_extras_in_dependency_order_holds_what_would_break_and_the_files_run()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, Source, Target);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        var names = Names(outcome);
        int At(string name) { var i = names.IndexOf(name); Assert.True(i >= 0, $"{name} not in [{string.Join(", ", names)}]"); return i; }

        // column changes: a narrowing every row fits, NULL to NOT NULL; the scale change that would round a value is held
        Assert.Contains("ALTER TABLE \"app\".\"parent\" ALTER COLUMN \"code\" TYPE character varying(10);", Text(outcome, "alter_column_app.parent.code"));
        Assert.EndsWith("ALTER TABLE \"app\".\"child\" ALTER COLUMN \"qty\" SET NOT NULL;\n", Text(outcome, "alter_column_app.child.qty"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "app.parent.amount" && h.Reason.Contains("1 row(s)"));
        // the check whose rule differs is replaced once the rows are checked; the target's own constraints, default and index go
        var replace = Text(outcome, "replace_check_app.parent.parent_status_check");
        Assert.True(replace.Contains("ADD CONSTRAINT \"parent_status_check\" CHECK (status = ANY (ARRAY['A'::text, 'B'::text, 'C'::text]));"), replace);
        foreach (var f in new[] { "drop_default_app.parent.note", "drop_check_app.child.child_qty_check", "drop_unique_app.child.child_parent_uq",
                                  "drop_index_app.child.child_extra_ix", "drop_sequence_app.seq_extra", "drop_domain_app.d_extra" })
            At(f);

        // dependents first: the view and the referencing table before the table, the trigger before its function and the
        // column it names, the function before the type it takes
        Assert.True(At("drop_view_app.v_extra") < At("drop_table_app.extra"));
        Assert.True(At("drop_table_app.extra_child") < At("drop_table_app.extra"));
        var trigger = names.Single(n => n.StartsWith("drop_trigger_", StringComparison.Ordinal));
        Assert.True(At(trigger) < At("drop_column_app.parent.extra_col"));
        var trgFunction = names.Single(n => n.Contains("app.trg_parent", StringComparison.Ordinal));
        Assert.True(At(trigger) < names.IndexOf(trgFunction));
        var usesMood = names.Single(n => n.Contains("app.uses_mood", StringComparison.Ordinal));
        Assert.True(names.IndexOf(usesMood) < At("drop_type_app.mood_extra"));

        // a table code that stays reads is held, never dropped
        Assert.Contains(outcome.HeldBack, h => h.Object == "app.kept" && h.Reason.Contains("view app.v_common uses it"));
        Assert.DoesNotContain(names, n => n.Contains("kept", StringComparison.Ordinal));
        Assert.Equal(2, outcome.HeldBack.Count(h => h.Section == Folder));

        // every DROP is RESTRICT; the headers count what goes
        Assert.All(PgScriptPair.FilesOf(outcome, Folder), f =>
        {
            var text = File.ReadAllText(f.FullPath);
            Assert.StartsWith($"-- {Folder}: CAN LOSE DATA.", text);
            foreach (var line in text.Split('\n').Where(l => l.StartsWith("DROP ", StringComparison.Ordinal) || l.Contains(" DROP CONSTRAINT ") || l.Contains(" DROP COLUMN ")))
                Assert.EndsWith(" RESTRICT;", line);
        });
        Assert.Contains("and its 3 row(s)", Text(outcome, "drop_table_app.extra"));
        Assert.Contains("1 row(s) hold a value in it", Text(outcome, "drop_column_app.parent.extra_col"));
        Assert.Contains(outcome.Files, f => f.Folder == Folder && f.RelativePath == @"rollback\_ALL.sql");
        var folder = Assert.Single(outcome.Folders, f => f.Folder == Folder);
        Assert.False(folder.InRunAll);
        Assert.True(folder.HasRollback);
        Assert.Contains("11_destructive - changes that can lose data", File.ReadAllText(Path.Combine(outcome.Path!, "README.md")));

        // run everything, 11 last, file by file as run_all.ps1 does: every file runs
        Assert.Empty(await pair.RunAsync(outcome, [.. PgScriptPair.RunOrder, Folder]));
        Assert.Equal(DBNull.Value, await pair.ScalarAsync("SELECT to_regclass('app.extra')::text"));
        Assert.Equal(DBNull.Value, await pair.ScalarAsync("SELECT to_regprocedure('app.f_extra(integer)')::text"));
        Assert.Equal(DBNull.Value, await pair.ScalarAsync("SELECT to_regtype('app.mood_extra')::text"));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = 'app.parent'::regclass AND attname = 'extra_col'"));
        Assert.Equal("app.kept", await pair.ScalarAsync("SELECT to_regclass('app.kept')::text"));
        Assert.Equal("character varying(10)", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'app.parent'::regclass AND attname = 'code'"));
        Assert.Equal("numeric(10,3)", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'app.parent'::regclass AND attname = 'amount'"));
        Assert.Equal(true, await pair.ScalarAsync("SELECT attnotnull FROM pg_attribute WHERE attrelid = 'app.child'::regclass AND attname = 'qty'"));
        Assert.Equal("app.child_qty_ix", await pair.ScalarAsync("SELECT to_regclass('app.child_qty_ix')::text"));
        Assert.Contains("'C'", (string)(await pair.ScalarAsync("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'parent_status_check'"))!);
        Assert.Equal(2L, await pair.ScalarAsync("SELECT count(*) FROM app.parent"));
        Assert.Equal(1.255m, await pair.ScalarAsync("SELECT amount FROM app.parent WHERE id = 1"));

        // compare again: only what was held still differs
        Assert.Equal(["Table app.parent: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));

        // a dropped function comes back from its rollback file
        var rollback = outcome.Files.Single(f => f.Folder == Folder && f.IsRollback && f.RelativePath.Contains("app.f_extra", StringComparison.Ordinal) &&
                                                 !f.RelativePath.EndsWith("_ALL.sql", StringComparison.Ordinal));
        Assert.Null(await pair.RunFileAsync(outcome, rollback));
        Assert.Equal(7, await pair.ScalarAsync("SELECT app.f_extra(7)"));
    }

    protected async Task A_name_the_source_has_in_another_letter_case_is_never_dropped()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE "Users" (id integer PRIMARY KEY, "Email" text);
            CREATE TABLE "Orders" (id integer PRIMARY KEY);
            CREATE SEQUENCE "Ticket";
            """, """
            CREATE TABLE "Users" (id integer PRIMARY KEY, "Email" text, email text, tmp integer);
            INSERT INTO "Users" VALUES (1, 'A@X', 'a@x', 9);
            CREATE TABLE users (id integer, tmp integer);
            INSERT INTO users VALUES (1, 1), (2, 2);
            CREATE TABLE orders (id integer PRIMARY KEY);
            INSERT INTO orders VALUES (10);
            CREATE SEQUENCE ticket START 500;
            CREATE TABLE "Archive" (id integer);
            INSERT INTO "Archive" VALUES (1);
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        var names = Names(outcome);

        // only what has no twin goes, named exactly
        Assert.Equal(["drop_column_public.Users.tmp", "drop_table_public.Archive"], names);
        Assert.Contains("ALTER TABLE \"public\".\"Users\" DROP COLUMN \"tmp\" RESTRICT;", Text(outcome, "drop_column_public.Users.tmp"));
        Assert.Contains("DROP TABLE \"public\".\"Archive\" RESTRICT;", Text(outcome, "drop_table_public.Archive"));
        foreach (var held in new[] { "public.users", "public.orders", "public.ticket", "public.Users.email" })
            Assert.Contains(outcome.HeldBack, h => h.Object == held && h.Reason.Contains("another letter case"));

        Assert.Empty(await pair.RunAsync(outcome, [.. PgScriptPair.RunOrder, Folder]));
        // the twins and their rows are all still there; the exact names went
        Assert.Equal(2L, await pair.ScalarAsync("SELECT count(*) FROM users"));
        Assert.Equal(1L, await pair.ScalarAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = 'users'::regclass AND attname = 'tmp'"));
        Assert.Equal(10, await pair.ScalarAsync("SELECT id FROM orders"));
        Assert.Equal("ticket", await pair.ScalarAsync("SELECT to_regclass('ticket')::text"));
        Assert.Equal("a@x", await pair.ScalarAsync("SELECT email FROM \"Users\""));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_attribute WHERE attrelid = '\"Users\"'::regclass AND attname = 'tmp'"));
        Assert.Equal(DBNull.Value, await pair.ScalarAsync("SELECT to_regclass('\"Archive\"')::text"));
        // 03 created the source's spellings beside them
        Assert.Equal("\"Orders\"", await pair.ScalarAsync("SELECT to_regclass('\"Orders\"')::text"));
        Assert.Equal("\"Ticket\"", await pair.ScalarAsync("SELECT to_regclass('\"Ticket\"')::text"));
    }

    protected async Task Restrict_stops_a_drop_of_what_something_unseen_depends_on()
    {
        // the compare leaves schema "hidden" out, so SchemaScope never sees its view of app.extra; DROP ... RESTRICT does
        await using var pair = await PgScriptPair.CreateAsync(pg, "CREATE SCHEMA app;", """
            CREATE SCHEMA app;
            CREATE TABLE app.extra (id integer, name text);
            INSERT INTO app.extra VALUES (1, 'a'), (2, 'b');
            CREATE SCHEMA hidden;
            CREATE VIEW hidden.v AS SELECT id FROM app.extra;
            """);
        var compare = new CompareOptions { ExcludedSchemas = ["hidden"] };
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(compare), compare: compare, destructive: true);
        Assert.Equal(["drop_table_app.extra"], Names(outcome));

        var failures = await pair.RunAsync(outcome, [Folder]);
        var failure = Assert.Single(failures);
        Assert.Contains("cannot drop table app.extra because other objects depend on it", failure);
        Assert.Equal(2L, await pair.ScalarAsync("SELECT count(*) FROM app.extra"));
        Assert.Equal("hidden.v", await pair.ScalarAsync("SELECT to_regclass('hidden.v')::text"));
    }

    protected async Task A_narrowing_checked_when_generated_refuses_a_row_written_since_instead_of_rounding_it()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE t (id integer PRIMARY KEY, amount numeric(10,2), code varchar(5), n smallint);
            """, """
            CREATE TABLE t (id integer PRIMARY KEY, amount numeric(10,3), code text, n bigint);
            INSERT INTO t VALUES (1, 1.25, 'abc', 7), (2, NULL, NULL, NULL);
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        Assert.Equal(["alter_column_public.t.amount", "alter_column_public.t.code", "alter_column_public.t.n"], Names(outcome));
        var amount = Text(outcome, "alter_column_public.t.amount");
        Assert.Contains("Checked when generated: every row of Target fits.", amount);
        Assert.Contains("LOCK TABLE \"public\".\"t\" IN ACCESS EXCLUSIVE MODE;", amount);
        Assert.Contains("RAISE EXCEPTION USING MESSAGE", amount);

        // a row written since the scripts were made would be rounded: the file refuses it and changes nothing
        await pg.ExecAsync(pair.Tgt, "INSERT INTO t VALUES (3, 2.345, 'abcdef', 40000);");
        var failures = await pair.RunAsync(outcome, [Folder]);
        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, f => f.Contains("Rows of public.t.amount no longer fit numeric(10,2); nothing was changed"));
        Assert.Equal("numeric(10,3)", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 't'::regclass AND attname = 'amount'"));
        Assert.Equal(2.345m, await pair.ScalarAsync("SELECT amount FROM t WHERE id = 3"));
        Assert.Equal("text", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 't'::regclass AND attname = 'code'"));

        // the rows that no longer fit are found when the scripts are made again: nothing is written for them
        var again = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        Assert.Empty(Names(again));
        foreach (var column in new[] { "amount", "code", "n" })
            Assert.Contains(again.HeldBack, h => h.Object == $"public.t.{column}" && h.Reason.Contains("1 row(s) in Target would not survive"));
    }

    protected async Task Without_the_data_checks_nothing_that_needs_one_is_written()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE t (id integer PRIMARY KEY, code varchar(5) NOT NULL);
            """, """
            CREATE TABLE t (id integer PRIMARY KEY, code varchar(9), extra text);
            CREATE TABLE gone (id integer);
            """);
        var before = await pair.CompareAsync();
        var source = new ScriptEndpoint("Source", pg.Host, pair.Src, pg.ConnectionString(pair.Src), Core.Engines.DatabaseEngine.PostgreSql);
        var target = new ScriptEndpoint("Target", pg.Host, pair.Tgt, pg.ConnectionString(pair.Tgt), Core.Engines.DatabaseEngine.PostgreSql);
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-destructive-{Guid.NewGuid():n}");
        try
        {
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(source, target, before.Source, before.Targets[0], before.Result, 0,
                new ScriptOptions { Sections = [.. PgScriptPair.Every], OutputFolder = output, IncludeDestructive = true, CheckExistingData = false },
                new HashSet<string>(), new Dictionary<string, Scripting.LineChange>(), new CompareOptions()), default, null);
            Assert.True(outcome.Success, outcome.Message);
            // the drops go (their headers say the rows were not counted); the column change that needs a probe is held
            Assert.Equal(["drop_column_public.t.extra", "drop_table_public.gone"], Names(outcome));
            Assert.Contains("Rows not counted (existing-data checks are switched off)", Text(outcome, "drop_table_public.gone"));
            Assert.Contains("values were not counted (existing-data checks are switched off)", Text(outcome, "drop_column_public.t.extra"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "public.t.code" && h.Reason.Contains("could not be checked") && h.Reason.Contains("switched off"));
        }
        finally
        {
            try { Directory.Delete(output, true); } catch (IOException) { }
        }
    }

    // ---------------- R3b review ----------------

    /// <summary>Generates as <paramref name="user"/> (the probes' login) instead of the container's superuser.</summary>
    private async Task<ScriptOutcome> GenerateAsAsync(PgScriptPair pair, string user, string password)
    {
        var before = await pair.CompareAsync();
        var source = new ScriptEndpoint("Source", pg.Host, pair.Src, pg.ConnectionString(pair.Src), Core.Engines.DatabaseEngine.PostgreSql);
        var target = new ScriptEndpoint("Target", pg.Host, pair.Tgt, pg.ConnectionString(pair.Tgt, user, password), Core.Engines.DatabaseEngine.PostgreSql);
        var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(source, target, before.Source, before.Targets[0], before.Result, 0,
            new ScriptOptions { Sections = [.. PgScriptPair.Every], OutputFolder = Path.Combine(Path.GetTempPath(), $"schemascope-pg-destructive-{Guid.NewGuid():n}"),
                IncludeDestructive = true },
            new HashSet<string>(), new Dictionary<string, Scripting.LineChange>(), new CompareOptions()), default, null);
        Assert.True(outcome.Success, outcome.Message);
        return outcome;
    }

    protected async Task Row_level_security_never_hides_rows_from_a_narrowing_or_a_count()
    {
        var role = await pg.CreateRoleAsync();
        try
        {
            await using var pair = await PgScriptPair.CreateAsync(pg, """
                CREATE TABLE m (id integer PRIMARY KEY, amount numeric(10,2));
                CREATE TABLE n (id integer PRIMARY KEY, amount numeric(10,2));
                """, """
                CREATE TABLE m (id integer PRIMARY KEY, amount numeric(10,3));
                INSERT INTO m VALUES (1, 1.25), (2, 2.345);
                CREATE TABLE n (id integer PRIMARY KEY, amount numeric(10,3));
                INSERT INTO n VALUES (1, 1.25);
                CREATE TABLE gone (id integer);
                INSERT INTO gone VALUES (1), (2);
                """);
            // the owner is subject to its own policies under FORCE; each policy shows it row 1 only (gone: nothing)
            foreach (var (table, rule) in new[] { ("m", "id = 1"), ("n", "id = 1"), ("gone", "false") })
                await pg.ExecAsync(pair.Tgt, $"""
                    ALTER TABLE {table} OWNER TO "{role.Name}";
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY p ON {table} USING ({rule});
                    """);

            // probing as the owner: PostgreSQL refuses the filtered read, so nothing is taken as fitting and nothing is counted short
            var asOwner = await GenerateAsAsync(pair, role.Name, role.Password);
            try
            {
                Assert.DoesNotContain(Names(asOwner), n => n.StartsWith("alter_column_", StringComparison.Ordinal));
                foreach (var column in new[] { "public.m.amount", "public.n.amount" })
                    Assert.Contains(asOwner.HeldBack, h => h.Object == column && h.Reason.Contains("could not be checked") && h.Reason.Contains("row-level security"));
                var gone = Text(asOwner, "drop_table_public.gone");
                Assert.Contains("Rows not counted", gone);
                Assert.Contains("row-level security", gone);
                Assert.DoesNotContain("its 0 row(s)", gone);
            }
            finally
            {
                try { Directory.Delete(asOwner.Path!, true); } catch (IOException) { }
            }

            // probing as the superuser sees every row: m's hidden 2.345 holds it; n fits and is written
            var asSuper = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
            Assert.Contains(asSuper.HeldBack, h => h.Object == "public.m.amount" && h.Reason.Contains("1 row(s) in Target would not survive"));
            var file = Text(asSuper, "alter_column_public.n.amount");
            Assert.Contains("SET LOCAL row_security = off;", file);

            // a row written since, hidden from the owner who runs the file: the check inside it fails instead of rounding it
            await pg.ExecAsync(pair.Tgt, "INSERT INTO n VALUES (2, 2.345);");
            await using (var conn = new NpgsqlConnection(pg.ConnectionString(pair.Tgt, role.Name, role.Password)))
            {
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();
                await using var cmd = new NpgsqlCommand(file, conn, tx);
                var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("row-level security", error.MessageText);
            }
            Assert.Equal(2.345m, await pair.ScalarAsync("SELECT amount FROM n WHERE id = 2"));
            Assert.Equal("numeric(10,3)", await pair.ScalarAsync("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'n'::regclass AND attname = 'amount'"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await pg.DropRoleAsync(role.Name);
        }
    }

    protected async Task A_check_whose_rule_differs_is_replaced_or_held_never_validated_in_05()
    {
        // t: a row breaks the target's old rule and the source's (held); u: the source's rule fits (replaced)
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE t (id integer PRIMARY KEY, v integer, CONSTRAINT v_ck CHECK (v > 10));
            CREATE TABLE u (id integer PRIMARY KEY, v integer, CONSTRAINT u_ck CHECK (v > 1));
            """, """
            CREATE TABLE t (id integer PRIMARY KEY, v integer);
            INSERT INTO t VALUES (1, 0);
            ALTER TABLE t ADD CONSTRAINT v_ck CHECK (v > 5) NOT VALID;
            CREATE TABLE u (id integer PRIMARY KEY, v integer);
            INSERT INTO u VALUES (1, 3);
            ALTER TABLE u ADD CONSTRAINT u_ck CHECK (v > 5) NOT VALID;
            """);
        var off = await pair.GenerateAsync(await pair.CompareAsync());
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        foreach (var o in new[] { off, outcome })
            Assert.DoesNotContain(PgScriptPair.FilesOf(o, PgScriptGenerator.ConstraintsFolder), f => f.RelativePath.Contains("validate_", StringComparison.Ordinal));
        Assert.Equal(["replace_check_public.u.u_ck"], Names(outcome));
        Assert.Contains(outcome.HeldBack, h => h.Object == "public.t.v_ck" && h.Section == Folder && h.Reason.Contains("1 row(s)"));

        Assert.Empty(await pair.RunAsync(outcome, [.. PgScriptPair.RunOrder, Folder]));
        Assert.Equal(["Table public.t: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));
        Assert.Equal(false, await pair.ScalarAsync("SELECT convalidated FROM pg_constraint WHERE conname = 'v_ck'"));
    }

    protected async Task A_sequence_owned_by_a_dropped_column_or_table_is_never_dropped_unlisted()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE SEQUENCE s;
            CREATE SEQUENCE s2 START 100;
            CREATE SEQUENCE s3;
            CREATE TABLE t (id integer PRIMARY KEY);
            """, """
            CREATE TABLE t (id integer PRIMARY KEY, x integer);
            CREATE SEQUENCE s OWNED BY t.x;
            CREATE TABLE extra (id integer, x integer);
            CREATE SEQUENCE s2 START 100 OWNED BY extra.x;
            CREATE TABLE "order" (id integer, x integer);
            CREATE SEQUENCE s3 OWNED BY "order".x;
            CREATE TABLE gone (id serial PRIMARY KEY);
            """);
        var outcome = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        // a drop that would take a sequence the source has along is held, naming it; a target-only serial goes with its table
        foreach (var (held, sequence) in new[] { ("public.t.x", "public.s"), ("public.extra", "public.s2"), ("public.order", "public.s3") })
            Assert.Contains(outcome.HeldBack, h => h.Object == held && h.Reason.Contains($"sequence {sequence}, which Source also has, is OWNED BY it"));
        Assert.Equal(["drop_table_public.gone", "drop_sequence_public.gone_id_seq"], Names(outcome));

        Assert.Empty(await pair.RunAsync(outcome, [.. PgScriptPair.RunOrder, Folder]));
        foreach (var sequence in new[] { "s", "s2", "s3" })
            Assert.Equal(sequence, await pair.ScalarAsync($"SELECT to_regclass('{sequence}')::text"));
        Assert.Equal(DBNull.Value, await pair.ScalarAsync("SELECT to_regclass('gone')::text"));
        // none went missing, and 05 set them OWNED BY NONE as the source has them: only the held column is left
        Assert.Equal(["Table public.t: Different"], PgScriptPair.NotSame(await pair.CompareAsync()));
        Assert.Equal(0L, await pair.ScalarAsync("SELECT count(*) FROM pg_depend WHERE classid = 'pg_class'::regclass AND deptype = 'a' AND objid IN ('s'::regclass, 's2'::regclass, 's3'::regclass)"));
    }

    protected async Task A_generated_column_change_is_held_by_04_with_or_without_destructive()
    {
        await using var pair = await PgScriptPair.CreateAsync(pg, """
            CREATE TABLE g (id integer PRIMARY KEY, a smallint GENERATED ALWAYS AS (id * 2) STORED, h integer GENERATED ALWAYS AS (id * 3) STORED NOT NULL);
            """, """
            CREATE TABLE g (id integer PRIMARY KEY, a integer GENERATED ALWAYS AS (id * 2) STORED, h integer GENERATED ALWAYS AS (id * 3) STORED);
            INSERT INTO g (id) VALUES (1);
            """);
        static List<string> Held(ScriptOutcome o) => [.. o.HeldBack.Select(h => $"{h.Section} {h.Object}: {h.Reason}").Order(StringComparer.Ordinal)];
        var off = await pair.GenerateAsync(await pair.CompareAsync());
        var on = await pair.GenerateAsync(await pair.CompareAsync(), destructive: true);
        Assert.Contains(off.HeldBack, h => h.Object == "public.g.a");
        Assert.Contains(off.HeldBack, h => h.Object == "public.g.h");
        Assert.Equal(Held(off), Held(on));
        Assert.Empty(Names(on));
    }

    // ---------------- over HTTP, as the UI drives it ----------------

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private static Task<HttpResponseMessage> ScriptsAsync(OwnerApp o, string runId, string folder, bool destructive) =>
        o.Post($"/api/compare/{runId}/scripts", new
        {
            targetIndex = 0,
            options = new
            {
                outputFolder = folder,
                sections = new[] { "newColumns", "missingObjects", "columnTypes", "constraints", "indexes", "triggers", "differentModules" },
                includeDestructive = destructive,
            },
        });

    private static Task<HttpResponseMessage> ApplyAsync(OwnerApp o, string runId, string planId, IEnumerable<string> sections, string confirm, bool acceptDataLoss) =>
        o.Post($"/api/compare/{runId}/apply", new
        {
            planId, targetIndex = 0, sections, confirmDatabase = confirm, acceptDataLoss, progressId = Guid.NewGuid().ToString(),
        });

    protected async Task Changes_that_can_lose_data_need_write_mode_and_the_second_tick_then_compare_to_a_match()
    {
        var src = await pg.CreateDatabaseAsync("src");
        var tgt = await pg.CreateDatabaseAsync("tgt");
        await using var o = await OwnerApp.StartAsync();
        try
        {
            await pg.ExecAsync(src, "CREATE SCHEMA app; CREATE TABLE app.t (id integer PRIMARY KEY, name text, code varchar(5), n integer);");
            // a narrowing every row fits (checked again inside its file), a default on the target's own sequence, a function
            await pg.ExecAsync(tgt, """
                CREATE SCHEMA app;
                CREATE SEQUENCE app.s_old;
                CREATE TABLE app.t (id integer PRIMARY KEY, name text, code varchar(20), n integer DEFAULT nextval('app.s_old'), extra text);
                INSERT INTO app.t VALUES (1, 'a', 'abc', 1, 'x');
                CREATE TABLE app.old (id integer);
                INSERT INTO app.old VALUES (1), (2);
                CREATE VIEW app.v_old AS SELECT id FROM app.old;
                CREATE FUNCTION app.f_old(x integer) RETURNS integer LANGUAGE plpgsql AS $$ BEGIN RETURN x; END $$;
                """);
            await o.SetPreviewAsync(true);
            var before = await PgApplyTestsBase.CompareAsync(o, pg.Settings(src), pg.Settings(tgt));
            Assert.True(PgApplyTestsBase.MatchPercent(before) < 100);
            var runId = before.GetProperty("runId").GetString()!;
            var folder = Path.Combine(o.Temp.Folder, "scripts");

            // not even generated without write mode
            var refused = await ScriptsAsync(o, runId, folder, destructive: true);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Turn on write mode to include changes that can lose data", await refused.Content.ReadAsStringAsync());
            Assert.False(Directory.Exists(folder));

            Assert.Equal(HttpStatusCode.OK, (await o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
            var r = await ScriptsAsync(o, runId, folder, destructive: true);
            var text = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode == HttpStatusCode.OK, text);
            var outcome = Body(text);
            Assert.True(outcome.GetProperty("success").GetBoolean(), text);
            Assert.True(outcome.GetProperty("validation").GetProperty("passed").GetBoolean(), text);
            Assert.Equal(0, outcome.GetProperty("heldBack").GetArrayLength());
            var planId = outcome.GetProperty("planId").GetString()!;
            var section = Assert.Single(outcome.GetProperty("planSections").EnumerateArray());
            Assert.Equal(Folder, section.GetProperty("folder").GetString());
            Assert.Equal("destructive", section.GetProperty("risk").GetString());
            Assert.False(section.GetProperty("defaultSelected").GetBoolean());
            // the narrowing, the default, the function, the view, the column, the table, the sequence (rollback\ is never planned)
            Assert.Equal(7, section.GetProperty("files").GetInt32());

            // the second tick is required; refused before anything runs, and the plan is still there
            var noTick = await ApplyAsync(o, runId, planId, [Folder], tgt, acceptDataLoss: false);
            Assert.Equal(HttpStatusCode.BadRequest, noTick.StatusCode);
            Assert.Contains("I understand this can lose data", await noTick.Content.ReadAsStringAsync());
            Assert.Empty(o.Temp.Store.GetApplyRuns());
            await using (var conn = new NpgsqlConnection(pg.ConnectionString(tgt)))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM app.old", conn);
                Assert.Equal(2L, await cmd.ExecuteScalarAsync());
            }

            var applied = await ApplyAsync(o, runId, planId, [Folder], tgt, acceptDataLoss: true);
            var resultText = await applied.Content.ReadAsStringAsync();
            Assert.True(applied.StatusCode == HttpStatusCode.OK, resultText);
            Assert.True(Body(resultText).GetProperty("success").GetBoolean(), resultText);

            var after = await PgApplyTestsBase.CompareAsync(o, pg.Settings(src), pg.Settings(tgt));
            Assert.Equal(100, PgApplyTestsBase.MatchPercent(after));
            await using (var conn = new NpgsqlConnection(pg.ConnectionString(tgt)))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT code || ':' || n FROM app.t WHERE id = 1", conn);
                Assert.Equal("abc:1", await cmd.ExecuteScalarAsync());
            }
            var audit = Assert.Single(o.Temp.Store.GetApplyRuns());
            Assert.True(audit.Success);
            Assert.Equal(7, audit.Files.Count);
            Assert.All(audit.Files, f => Assert.Equal(Folder, f.Folder));
        }
        finally
        {
            await pg.DropAsync(src);
            await pg.DropAsync(tgt);
        }
    }
}

[Collection(PgCollection.Name)]
public sealed class PgDestructiveTests(PgServer pg) : PgDestructiveTestsBase(pg)
{
    [PostgresFact] public Task Row_level_security_fails_closed_on_16() => Row_level_security_never_hides_rows_from_a_narrowing_or_a_count();
    [PostgresFact] public Task A_differing_check_is_never_validated_on_16() => A_check_whose_rule_differs_is_replaced_or_held_never_validated_in_05();
    [PostgresFact] public Task Owned_sequences_hold_the_drop_on_16() => A_sequence_owned_by_a_dropped_column_or_table_is_never_dropped_unlisted();
    [PostgresFact] public Task Generated_columns_stay_held_on_16() => A_generated_column_change_is_held_by_04_with_or_without_destructive();
    [PostgresFact] public Task Drops_in_order_and_runs_on_16() => Drops_extras_in_dependency_order_holds_what_would_break_and_the_files_run();
    [PostgresFact] public Task Case_twins_are_never_dropped_on_16() => A_name_the_source_has_in_another_letter_case_is_never_dropped();
    [PostgresFact] public Task Restrict_is_the_backstop_on_16() => Restrict_stops_a_drop_of_what_something_unseen_depends_on();
    [PostgresFact] public Task The_fit_is_checked_again_when_the_file_runs_on_16() => A_narrowing_checked_when_generated_refuses_a_row_written_since_instead_of_rounding_it();
    [PostgresFact] public Task Without_data_checks_on_16() => Without_the_data_checks_nothing_that_needs_one_is_written();
    [PostgresFact] public Task Over_http_with_the_second_tick_on_16() => Changes_that_can_lose_data_need_write_mode_and_the_second_tick_then_compare_to_a_match();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13DestructiveTests(Pg13Server pg) : PgDestructiveTestsBase(pg)
{
    [PostgresFact] public Task Row_level_security_fails_closed_on_13() => Row_level_security_never_hides_rows_from_a_narrowing_or_a_count();
    [PostgresFact] public Task A_differing_check_is_never_validated_on_13() => A_check_whose_rule_differs_is_replaced_or_held_never_validated_in_05();
    [PostgresFact] public Task Owned_sequences_hold_the_drop_on_13() => A_sequence_owned_by_a_dropped_column_or_table_is_never_dropped_unlisted();
    [PostgresFact] public Task Generated_columns_stay_held_on_13() => A_generated_column_change_is_held_by_04_with_or_without_destructive();
    [PostgresFact] public Task Drops_in_order_and_runs_on_13() => Drops_extras_in_dependency_order_holds_what_would_break_and_the_files_run();
    [PostgresFact] public Task Case_twins_are_never_dropped_on_13() => A_name_the_source_has_in_another_letter_case_is_never_dropped();
    [PostgresFact] public Task Restrict_is_the_backstop_on_13() => Restrict_stops_a_drop_of_what_something_unseen_depends_on();
    [PostgresFact] public Task The_fit_is_checked_again_when_the_file_runs_on_13() => A_narrowing_checked_when_generated_refuses_a_row_written_since_instead_of_rounding_it();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18DestructiveTests(Pg18Server pg) : PgDestructiveTestsBase(pg)
{
    [PostgresFact] public Task Row_level_security_fails_closed_on_18() => Row_level_security_never_hides_rows_from_a_narrowing_or_a_count();
    [PostgresFact] public Task A_differing_check_is_never_validated_on_18() => A_check_whose_rule_differs_is_replaced_or_held_never_validated_in_05();
    [PostgresFact] public Task Owned_sequences_hold_the_drop_on_18() => A_sequence_owned_by_a_dropped_column_or_table_is_never_dropped_unlisted();
    [PostgresFact] public Task Generated_columns_stay_held_on_18() => A_generated_column_change_is_held_by_04_with_or_without_destructive();
    [PostgresFact] public Task Drops_in_order_and_runs_on_18() => Drops_extras_in_dependency_order_holds_what_would_break_and_the_files_run();
    [PostgresFact] public Task Case_twins_are_never_dropped_on_18() => A_name_the_source_has_in_another_letter_case_is_never_dropped();
    [PostgresFact] public Task Restrict_is_the_backstop_on_18() => Restrict_stops_a_drop_of_what_something_unseen_depends_on();
    [PostgresFact] public Task The_fit_is_checked_again_when_the_file_runs_on_18() => A_narrowing_checked_when_generated_refuses_a_row_written_since_instead_of_rounding_it();
    [PostgresFact] public Task Over_http_with_the_second_tick_on_18() => Changes_that_can_lose_data_need_write_mode_and_the_second_tick_then_compare_to_a_match();
}
