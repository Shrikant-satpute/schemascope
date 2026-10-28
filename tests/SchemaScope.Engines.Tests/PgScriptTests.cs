using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// A drifted pair for "Create scripts": the source has what every folder writes, and a few things each folder must
/// hold back with a reason. The target has data, so the probes see real rows.
/// </summary>
public static class PgScriptScenario
{
    private const string Common = """
        CREATE SCHEMA app;
        CREATE TYPE app.mood AS ENUM ('sad', 'ok', 'happy');
        CREATE DOMAIN app.positive AS integer CHECK (VALUE > 0);
        CREATE FUNCTION app.add(a integer, b integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$;
        """;

    // a column in the middle: PostgreSQL can only add it at the end
    private const string OrderedSource = "CREATE TABLE app.ordered (a integer, b integer, c integer);\n";
    private const string OrderedTarget = "CREATE TABLE app.ordered (a integer, c integer);\n";

    public const string Source = Common + OrderedSource + """
        CREATE EXTENSION IF NOT EXISTS pgcrypto;
        CREATE TABLE app.customers (
            id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name text NOT NULL,
            email varchar(50),
            code varchar(10),
            score bigint CHECK (score >= 0),
            flag boolean NOT NULL DEFAULT false,
            nickname text,
            level integer NOT NULL DEFAULT 1,
            must text NOT NULL,
            CONSTRAINT customers_email_key UNIQUE (email),
            CONSTRAINT customers_name_check CHECK (length(name) > 1)
        );
        CREATE INDEX customers_name_ix ON app.customers (name);
        CREATE TABLE app.notes (id integer PRIMARY KEY, body varchar(80), customer_id integer REFERENCES app.customers (id));
        CREATE VIEW app.v_notes AS SELECT id, body FROM app.notes;
        CREATE VIEW app.v_customers AS SELECT id, name, email FROM app.customers;
        CREATE FUNCTION app.label(x integer) RETURNS text LANGUAGE plpgsql AS $$ BEGIN RETURN 'n' || x; END $$;
        CREATE FUNCTION app.kind(x integer) RETURNS bigint LANGUAGE sql AS $$ SELECT x::bigint $$;
        CREATE FUNCTION app.fmt(x integer) RETURNS integer LANGUAGE plpgsql AS $$
        BEGIN
            RETURN x * 2; -- double it
        END;
        $$;
        CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.total := coalesce(NEW.total, 0); RETURN NEW; END $$;
        CREATE TABLE app.orders (
            id bigserial PRIMARY KEY,
            customer_id integer NOT NULL REFERENCES app.customers (id) ON DELETE CASCADE,
            total numeric(10,2) NOT NULL DEFAULT 0 CHECK (total >= 0),
            mood app.mood DEFAULT 'ok',
            qty app.positive,
            doubled integer GENERATED ALWAYS AS (qty * 2) STORED
        );
        CREATE INDEX orders_customer_ix ON app.orders (customer_id) INCLUDE (total) WHERE total > 0;
        CREATE TRIGGER orders_touch BEFORE INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.touch();

        CREATE SCHEMA app2;
        CREATE TYPE app2.color AS ENUM ('red', 'green');
        CREATE TYPE app2.pair AS (a integer, b text);
        CREATE SEQUENCE app2.thing_seq START 10 INCREMENT 3;
        CREATE TABLE app2.things (id integer PRIMARY KEY DEFAULT nextval('app2.thing_seq'), color app2.color NOT NULL, p app2.pair);
        CREATE INDEX things_color_ix ON app2.things (color);
        CREATE VIEW app2.z_base AS SELECT id, color FROM app2.things;
        CREATE VIEW app2.a_top AS SELECT id FROM app2.z_base;
        CREATE MATERIALIZED VIEW app2.mv_colors AS SELECT color, count(*) AS n FROM app2.things GROUP BY color;
        CREATE INDEX mv_colors_ix ON app2.mv_colors (color);
        CREATE FUNCTION app2.thing_count() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM app2.things $$;
        CREATE PROCEDURE app2.reset() LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
        """;

    /// <summary>PostgreSQL 14 and later: a SQL-standard body, whose semicolons do not end the statement.</summary>
    public const string SourceAtomic = """
        CREATE FUNCTION app2.thing_ids() RETURNS SETOF integer LANGUAGE sql
        BEGIN ATOMIC
            SELECT id FROM app2.things;
        END;
        """;

    public const string Target = Common + OrderedTarget + """
        CREATE TABLE app.customers (
            id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name text NOT NULL,
            email varchar(20),
            code varchar(10) NOT NULL,
            score integer,
            flag boolean NOT NULL DEFAULT false,
            legacy text
        );
        CREATE INDEX customers_legacy_ix ON app.customers (legacy);
        CREATE TABLE app.notes (id integer PRIMARY KEY, body varchar(30));
        CREATE VIEW app.v_notes AS SELECT id, body FROM app.notes;
        CREATE VIEW app.v_customers AS SELECT id, name FROM app.customers;
        CREATE FUNCTION app.label(x integer) RETURNS text LANGUAGE plpgsql AS $$ BEGIN RETURN 'old' || x; END $$;
        CREATE FUNCTION app.kind(x integer) RETURNS integer LANGUAGE sql AS $$ SELECT x $$;
        CREATE FUNCTION app.fmt(x integer) RETURNS integer LANGUAGE plpgsql AS $$
        BEGIN
          /* the same code, laid out differently */
          RETURN   x*2;
        END;
        $$;
        CREATE TABLE app.extra (id integer);
        INSERT INTO app.customers (name, email, code, score) VALUES ('Ann', 'ann@x', 'A', 1), ('x', 'x@x', 'B', 2);
        INSERT INTO app.notes (id, body) VALUES (1, 'hello');
        """;
}

/// <summary>
/// "Create scripts" end to end on real servers: compare a drifted pair, generate, validate, run every file with psql
/// inside the container in RUN.ps1's order (then 07 and 10 by hand), compare again: everything not held back matches,
/// and every held item says why.
/// </summary>
public abstract class PgScriptTestsBase(PgServer pg)
{
    private static readonly ScriptSection[] Every =
        [ScriptSection.NewColumns, ScriptSection.TableTypes, ScriptSection.MissingObjects, ScriptSection.ColumnTypes, ScriptSection.Constraints,
         ScriptSection.Indexes, ScriptSection.Triggers, ScriptSection.Formatting, ScriptSection.DifferentModules];

    private async Task<int> VersionAsync(string db)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString(db));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT current_setting('server_version_num')::int", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task<CompareRun> CompareAsync(PgServer pg, string src, string tgt) =>
        new CompareRunner(_ => new PgSchemaReader()).RunAsync(pg.Source("source", src), [pg.Source("t0", tgt)], new CompareOptions());

    /// <summary>
    /// The rows the re-compare still shows as not Same, each with every hold that keeps it so (a hold on a sibling
    /// item never explains a row on its own): anything else left different is a defect.
    /// </summary>
    private static readonly Dictionary<string, string[]> StillDifferent = new(StringComparer.Ordinal)
    {
        ["Extension pgcrypto.pgcrypto: MissingInTarget"] = ["pgcrypto"],
        ["Scalar function app.kind integer: Different"] = ["app.kind(integer)"],
        ["Table app.customers: Different"] = ["app.customers.must", "app.customers.customers_name_check", "app.customers.legacy", "app.customers.customers_legacy_ix"],
        ["Table app.notes: Different"] = ["app.notes.body"],
        ["Table app.ordered: Different"] = ["app.ordered"],
    };

    protected async Task Generate_validate_run_with_psql_and_match()
    {
        var src = await pg.CreateDatabaseAsync("src");
        var tgt = await pg.CreateDatabaseAsync("tgt");
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-scripts-{Guid.NewGuid():n}");
        try
        {
            var atomic = await VersionAsync("postgres") >= 140000;
            await pg.ExecAsync(src, PgScriptScenario.Source);
            if (atomic)
            {
                // through psql, which knows a BEGIN ATOMIC body's semicolons do not end the statement
                var (code, _, err) = await pg.ExecInContainerAsync("psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", PgServer.User, "-d", src, "-c", PgScriptScenario.SourceAtomic);
                Assert.True(code == 0, err);
            }
            await pg.ExecAsync(tgt, PgScriptScenario.Target);

            var before = await CompareAsync(pg, src, tgt);
            Assert.False(before.Targets[0].Failed, before.Targets[0].Error);
            var options = new ScriptOptions { Sections = [.. Every], OutputFolder = output };
            var source = new ScriptEndpoint("Source", pg.Host, src, pg.ConnectionString(src), DatabaseEngine.PostgreSql);
            var target = new ScriptEndpoint("Target", pg.Host, tgt, pg.ConnectionString(tgt), DatabaseEngine.PostgreSql);
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(source, target, before.Source, before.Targets[0],
                before.Result, 0, options, new HashSet<string>(), new Dictionary<string, LineChange>(), new CompareOptions()), default, null);

            Assert.True(outcome.Success, outcome.Message);
            Assert.True(outcome.Validation.Passed, string.Join("\n", outcome.Validation.Problems));
            Assert.True(outcome.Validation.Level!.Parsed);
            Assert.False(outcome.Validation.Level.ModuleTextProven);
            Assert.StartsWith("libpg_query", outcome.Validation.Level.Parser);
            Assert.Equal(0, outcome.Validation.SyntaxErrors);
            Assert.True(outcome.Validation.ModulesChecked > 0 && outcome.Validation.ModulesChecked == outcome.Validation.ModulesExact);
            Assert.All(outcome.HeldBack, h => Assert.False(string.IsNullOrWhiteSpace(h.Reason), h.Object));

            // what each folder holds, and that nothing that must not be written is
            string Folder(string f) => string.Join("\n", outcome.Files.Where(x => x.Folder == f && x.IsObjectFile).OrderBy(x => x.RelativePath, StringComparer.Ordinal)
                .Select(x => File.ReadAllText(x.FullPath)));
            foreach (var f in outcome.Files.Where(f => f.FullPath.EndsWith(".sql", StringComparison.Ordinal)))
            {
                var bytes = await File.ReadAllBytesAsync(f.FullPath);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, $"{f.RelativePath} has a BOM");
                Assert.DoesNotContain("CONCURRENTLY", File.ReadAllText(f.FullPath));
            }
            foreach (var f in Directory.EnumerateFiles(outcome.Path!, "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(pg.Password, File.ReadAllText(f));
            Assert.Contains("ADD COLUMN \"nickname\" text", Folder("01_columns"));
            Assert.Contains("ADD COLUMN \"level\" integer NOT NULL DEFAULT 1", Folder("01_columns"));
            Assert.DoesNotContain("\"must\"", Folder("01_columns"));
            var missing = Folder("03_missing");
            Assert.True(missing.IndexOf("\"app2\".\"z_base\"", StringComparison.Ordinal) < missing.IndexOf("\"app2\".\"a_top\"", StringComparison.Ordinal),
                "a view comes after the view it reads (pg_depend order)");
            Assert.Contains("not compile-checked", missing);
            Assert.DoesNotContain("CREATE EXTENSION", missing);
            Assert.Contains("ALTER COLUMN \"email\" TYPE character varying(50)", Folder("04_columntypes"));
            Assert.Contains("ALTER COLUMN \"code\" DROP NOT NULL", Folder("04_columntypes"));
            Assert.DoesNotContain("\"body\"", Folder("04_columntypes"));
            var constraints = Folder("05_constraints");
            Assert.Contains("NOT VALID", constraints);
            Assert.True(constraints.IndexOf("ADD CONSTRAINT \"notes_customer_id_fkey\"", StringComparison.Ordinal) <
                        constraints.IndexOf("VALIDATE CONSTRAINT \"notes_customer_id_fkey\"", StringComparison.Ordinal));
            Assert.DoesNotContain("customers_name_check", constraints);
            var triggers = Folder("07_triggers");
            Assert.True(triggers.IndexOf("FUNCTION app.touch()", StringComparison.Ordinal) < triggers.IndexOf("CREATE TRIGGER orders_touch", StringComparison.Ordinal));
            Assert.DoesNotContain("app.touch()", missing);
            Assert.Contains("CREATE OR REPLACE FUNCTION app.label(x integer)", Folder("10_different_modules"));
            Assert.Contains("CREATE OR REPLACE VIEW", Folder("10_different_modules"));
            Assert.Contains(outcome.Folders, f => f.Folder == "02_tabletypes" && f.Notes.Any(n => n.StartsWith("Not applicable")));
            // code that differs only in layout is re-created from the source's text by 08, not held
            Assert.Contains("CREATE OR REPLACE FUNCTION app.fmt(x integer)", Folder("08_formatting_modules"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "app.customers.must" && h.Reason.Contains("NOT NULL with no default"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "app.notes.body" && h.Reason.Contains("v_notes"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "app.customers.customers_name_check" && h.Reason.Contains("1 existing row"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "pgcrypto" && h.Reason.Contains("CREATE EXTENSION"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "app.kind(integer)" && h.Reason.Contains("return type"));
            Assert.DoesNotContain(outcome.HeldBack, h => h.Object == "app.fmt(integer)");
            Assert.Contains("ADD COLUMN \"b\" integer", Folder("01_columns"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "app.ordered" && h.Reason.Contains("cannot move one"));

            var run = File.ReadAllText(Path.Combine(outcome.Path!, "RUN.ps1"));
            Assert.Contains("\"04_columntypes\",\"01_columns\",\"03_missing\",\"05_constraints\",\"06_indexes\"", run);
            var runner = File.ReadAllText(Path.Combine(outcome.Path!, "03_missing", "run_all.ps1"));
            Assert.Contains("psql -X -v ON_ERROR_STOP=1 --single-transaction", runner);

            // run it the way RUN.ps1 does, with psql inside the container, then 07 and 10 by hand: every file, failures collected
            var order = Regex.Match(run, @"\$folders = @\((?<f>[^)]*)\)").Groups["f"].Value.Split(',').Select(f => f.Trim('"')).ToList();
            order.AddRange(["07_triggers", "08_formatting_modules", "10_different_modules"]);
            Assert.All(order, folder => Assert.NotEmpty(PgScriptPair.FilesOf(outcome, folder)));
            Assert.Empty(await new PgScriptPair(pg, src, tgt, output).RunAsync(outcome, order));

            // exactly these rows still differ, and each is kept so by the holds listed for it; everything else matches
            var after = await CompareAsync(pg, src, tgt);
            var rows = after.Result.Rows;
            Assert.Equal(StillDifferent.Keys.Order(StringComparer.Ordinal), PgScriptPair.NotSame(after));
            var expectedHolds = StillDifferent.Values.SelectMany(h => h).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expectedHolds, outcome.HeldBack.Select(h => h.Object).Order(StringComparer.Ordinal));

            // and nothing that was not held is left: scripts made again from the new compare write nothing and hold the same things
            var again = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(source, target, after.Source, after.Targets[0],
                after.Result, 0, options, new HashSet<string>(), new Dictionary<string, LineChange>(), new CompareOptions()), default, null);
            Assert.True(again.Success, again.Message);
            Assert.Empty(again.Files.Where(f => f.IsObjectFile).Select(f => f.RelativePath));
            Assert.Equal(expectedHolds, again.HeldBack.Select(h => h.Object).Order(StringComparer.Ordinal));
            foreach (var name in new[] { "orders", "things" })
                Assert.Equal(ObjectStatus.Same, rows.Single(r => r.Type == DbObjectType.Table && r.Name == name).Cells[0].Status);
            Assert.Equal(ObjectStatus.Same, rows.Single(r => r.Type == DbObjectType.Trigger && r.Name == "orders_touch").Cells[0].Status);
            Assert.Equal(ObjectStatus.Same, rows.Single(r => r.Name == "label").Cells[0].Status);
            Assert.Equal(ObjectStatus.Same, rows.Single(r => r.Name == "v_customers").Cells[0].Status);
            if (atomic) Assert.Equal(ObjectStatus.Same, rows.Single(r => r.Name == "thing_ids").Cells[0].Status);
        }
        finally
        {
            await pg.DropAsync(src);
            await pg.DropAsync(tgt);
            try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

[Collection(PgCollection.Name)]
public sealed class PgScriptTests(PgServer pg) : PgScriptTestsBase(pg)
{
    [PostgresFact]
    public Task Scripts_for_a_drifted_pair_run_with_psql_and_match_on_postgres_16() => Generate_validate_run_with_psql_and_match();
}

[Collection(Pg13Collection.Name)]
public sealed class Pg13ScriptTests(Pg13Server pg) : PgScriptTestsBase(pg)
{
    [PostgresFact]
    public Task Scripts_for_a_drifted_pair_run_with_psql_and_match_on_postgres_13() => Generate_validate_run_with_psql_and_match();
}

[Collection(Pg18Collection.Name)]
public sealed class Pg18ScriptTests(Pg18Server pg) : PgScriptTestsBase(pg)
{
    [PostgresFact]
    public Task Scripts_for_a_drifted_pair_run_with_psql_and_match_on_postgres_18() => Generate_validate_run_with_psql_and_match();
}
