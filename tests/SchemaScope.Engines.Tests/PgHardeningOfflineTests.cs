using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// Hardening round 1, the cases that need no server: the deparse spelled one way across versions, the parsing behind
/// policies and enum labels, the counts, the runner on a long path, and the API's wording and secret rule.
/// </summary>
public class PgHardeningOfflineTests
{
    // ------------------------------------------------------------------ the deparse across versions

    [Theory]
    // 14 and later print the SQL-standard forms; 13 the function calls. Both read as the call.
    [InlineData("(now() AT TIME ZONE 'utc'::text)", "timezone('utc'::text, now())")]
    [InlineData("((created AT TIME ZONE 'utc'::text) AT TIME ZONE 'x'::text)", "timezone('x'::text, timezone('utc'::text, created))")]
    [InlineData("SUBSTRING(name FROM 1 FOR 2) <> 'xx'::text", "\"substring\"(name, 1, 2) <> 'xx'::text")]
    [InlineData("SUBSTRING(name FROM 3)", "\"substring\"(name, 3)")]
    [InlineData("POSITION(('a'::text) IN (name)) >= 0", "\"position\"(name, 'a'::text) >= 0")]
    [InlineData("TRIM(BOTH ' '::text FROM name)", "btrim(name, ' '::text)")]
    [InlineData("TRIM(LEADING FROM name)", "ltrim(name)")]
    [InlineData("OVERLAY(name PLACING 'x'::text FROM 1)", "\"overlay\"(name, 'x'::text, 1)")]
    [InlineData("OVERLAY(name PLACING 'x'::text FROM 1 FOR 2)", "\"overlay\"(name, 'x'::text, 1, 2)")]
    public void The_sql_standard_forms_read_as_the_function_call(string newer, string older)
    {
        Assert.Equal(older, PgDeparse.FunctionSyntax(newer));
        Assert.Equal(older, PgDeparse.FunctionSyntax(older));
    }

    [Theory]
    // a literal, a quoted name, an explicit call and a form it cannot read are left exactly as they are
    [InlineData("'x AT TIME ZONE y'")]
    [InlineData("\"SUBSTRING(\"(a)")]
    [InlineData("timezone('utc'::text, now())")]
    [InlineData("SUBSTRING(name SIMILAR 'a'::text ESCAPE '#'::text)")]
    [InlineData("EXTRACT(year FROM created) > 2000::numeric")]
    [InlineData("f(x AT TIME ZONE 'utc')")]
    public void Anything_else_is_left_as_it_is(string text) => Assert.Equal(text, PgDeparse.FunctionSyntax(text));

    [Theory]
    [InlineData("IN p_tenant_id integer, IN p_id integer", "p_tenant_id integer, p_id integer")]
    [InlineData("IN a integer, INOUT b integer, OUT c text", "a integer, INOUT b integer, OUT c text")]
    [InlineData("IN p text DEFAULT 'IN x, IN y'::text", "p text DEFAULT 'IN x, IN y'::text")]
    [InlineData("a integer, VARIADIC b integer[]", "a integer, VARIADIC b integer[]")]
    [InlineData("", "")]
    public void An_in_mode_is_the_default_and_is_left_out(string newer, string expected) => Assert.Equal(expected, PgDeparse.Arguments(newer));

    [Theory]
    // a single relation: before 16 every column carries its prefix, from 16 none at the query's own level
    [InlineData(" SELECT t.id,\n    t.tenant_id\n   FROM s.t t\n  WHERE t.id > 0\n  ORDER BY t.id;", " SELECT id,\n    tenant_id\n   FROM s.t t\n  WHERE id > 0\n  ORDER BY id;")]
    [InlineData(" SELECT t.id\n   FROM s.t;", " SELECT id\n   FROM s.t;")]
    [InlineData(" SELECT t.id,\n    ( SELECT count(*) AS count\n           FROM s.t x\n          WHERE x.id = t.id) AS c\n   FROM s.t t;",
                " SELECT id,\n    ( SELECT count(*) AS count\n           FROM s.t x\n          WHERE x.id = t.id) AS c\n   FROM s.t t;")]
    [InlineData(" SELECT \"T\".\"Id\"\n   FROM s.\"T\";", " SELECT \"Id\"\n   FROM s.\"T\";")]
    // the alias is also a schema's name: the FROM item and a call in that schema keep it
    [InlineData(" SELECT s.id,\n    s.f(s.id) AS f\n   FROM s.t s;", " SELECT id,\n    s.f(id) AS f\n   FROM s.t s;")]
    // a whole-row reference keeps its prefix in 16 as well
    [InlineData(" SELECT t.id,\n    row_to_json(t.*) AS j\n   FROM s.t t;", " SELECT id,\n    row_to_json(t.*) AS j\n   FROM s.t t;")]
    public void A_view_on_one_relation_reads_as_sixteen_prints_it(string older, string sixteen) =>
        Assert.Equal(sixteen, PgDeparse.UnqualifiedSingleRelation(older));

    [Theory]
    [InlineData(" SELECT a.id\n   FROM s.t a\n     JOIN s.t b ON a.id = b.id;")]
    [InlineData(" SELECT a.id\n   FROM s.t a,\n    s.u b;")]
    [InlineData(" SELECT t.id\n   FROM s.t\nUNION\n SELECT u.id\n   FROM s.u;")]
    [InlineData(" SELECT g.i\n   FROM generate_series(1, 3) g(i);")]
    [InlineData(" WITH c AS (\n         SELECT 1 AS x\n        )\n SELECT c.x\n   FROM c;")]
    public void Anything_but_one_plain_relation_keeps_its_prefixes(string viewdef) => Assert.Equal(viewdef, PgDeparse.UnqualifiedSingleRelation(viewdef));

    [Theory]
    [InlineData("floatrange", "floatmultirange")]
    [InlineData("rng", "rng_multirange")]
    [InlineData("my_range_t", "my_multirange_t")]
    public void A_multirange_is_named_as_postgres_names_it(string range, string multirange) =>
        Assert.Equal(multirange, PgSchemaReader.DefaultMultirangeName(range));

    // ------------------------------------------------------------------ what the planner reads

    [Theory]
    [InlineData("CREATE POLICY \"p\" ON \"s\".\"t\" AS PERMISSIVE FOR SELECT TO PUBLIC USING ((score > 10));", "PUBLIC")]
    [InlineData("CREATE POLICY \"p\" ON \"s\".\"t\" AS PERMISSIVE FOR ALL TO app, \"Read, Only\" USING (true) WITH CHECK (true);", "app|Read, Only")]
    [InlineData("CREATE POLICY \"p\" ON \"s\".\"t\" AS RESTRICTIVE FOR INSERT TO app WITH CHECK ((id > 0));", "app")]
    [InlineData("CREATE POLICY \"p\" ON \"s\".\"t\" AS PERMISSIVE FOR DELETE TO \"a\"\"b\";", "a\"b")]
    public void A_policy_s_roles_are_read_from_its_text(string policy, string roles) =>
        Assert.Equal(roles.Split('|'), PgScriptRun.PolicyRoles(policy));

    [Fact]
    public void Enum_labels_are_read_as_their_literals()
    {
        var e = new DbObject { Type = DbObjectType.EnumType, Schema = "s", Name = "e", Fold = IdentifierFold.Exact };
        e.Properties["labels"] = "'a', 'it''s', 'c, d', E'x\\\\y'";
        Assert.Equal(["'a'", "'it''s'", "'c, d'", "E'x\\\\y'"], PgScriptRun.EnumLabels(e));
    }

    [Fact]
    public void A_schema_named_in_a_body_is_found_bare_or_quoted()
    {
        Assert.True(PgDdl.MentionsSchema("SELECT ext.digest(t, 'sha1')", "ext"));
        Assert.True(PgDdl.MentionsSchema("SELECT \"Ext\".digest(t)", "Ext"));
        Assert.False(PgDdl.MentionsSchema("SELECT next.digest(t)", "ext"));
        Assert.False(PgDdl.MentionsSchema("SELECT a.ext.b", "ext"));
    }

    // ------------------------------------------------------------------ counts

    [Fact]
    public void An_object_neither_the_source_nor_a_target_has_is_not_counted_for_it()
    {
        static DbObject T(string name) => new() { Type = DbObjectType.Table, Schema = "app", Name = name, Fold = IdentifierFold.Exact, Hash = name };
        static SchemaSnapshot Snap(params string[] tables) => new() { Engine = DatabaseEngine.PostgreSql, Objects = [.. tables.Select(T)] };
        var result = new CompareEngine(new CompareOptions()).Compare(Snap("a", "b"), [Snap("a", "b"), Snap("a", "b", "legacy")]);
        var plain = result.Targets[0];
        Assert.Equal(2, plain.Same);
        Assert.Equal(2, plain.TotalObjects);
        Assert.Equal(100, plain.MatchPercent);
        Assert.Equal(2, result.Targets[1].Same);
        Assert.Equal(1, result.Targets[1].OnlyInTarget);
    }

    [Fact]
    public async Task Validation_counts_each_statement_once_not_again_for_ALL_sql()
    {
        var src = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql, MajorVersion = 16,
            Objects =
            [
                new() { Type = DbObjectType.Schema, Schema = "app", Name = "app", Fold = IdentifierFold.Exact, Hash = "s" },
                new() { Type = DbObjectType.View, Schema = "app", Name = "v", Fold = IdentifierFold.Exact, Hash = "v",
                        Definition = "CREATE OR REPLACE VIEW \"app\".\"v\" AS\n SELECT 1 AS one;" },
            ]
        };
        var tgt = new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, MajorVersion = 16, Objects = [src.Objects[0]] };
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-offline-{Guid.NewGuid():n}");
        try
        {
            var endpoint = new ScriptEndpoint("Target", "db.example", "tgt", "Host=db.example;Database=tgt", DatabaseEngine.PostgreSql);
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(endpoint with { Label = "Source" }, endpoint, src, tgt,
                new CompareResult(), 0, new ScriptOptions { Sections = [ScriptSection.MissingObjects], OutputFolder = output, CheckExistingData = false },
                new HashSet<string>(), new Dictionary<string, LineChange>()), default, null);
            Assert.True(outcome.Success, outcome.Message);
            Assert.Equal(1, outcome.Validation.Batches);
            Assert.Contains("- Statements: 1 ", File.ReadAllText(Path.Combine(outcome.Path!, "99_review", "validation.md")));
        }
        finally
        {
            try { Directory.Delete(output, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_policy_that_names_a_role_the_target_may_lack_is_held_and_row_security_stays_off()
    {
        DbObject Table(string name, string policy)
        {
            var t = new DbObject { Type = DbObjectType.Table, Schema = "crm", Name = name, Fold = IdentifierFold.Exact, Hash = name };
            t.Columns = [new ColumnInfo { Name = "id", DataType = "integer", Ordinal = 1 }, new ColumnInfo { Name = "score", DataType = "integer", IsNullable = true, Ordinal = 2 }];
            t.Properties["rowSecurity"] = "ENABLE";
            t.Properties["forceRowSecurity"] = "FORCE";
            t.Properties[PgCanonicalWriter.PolicyPrefix + "p"] = policy;
            return t;
        }
        var schema = new DbObject { Type = DbObjectType.Schema, Schema = "crm", Name = "crm", Fold = IdentifierFold.Exact, Hash = "s" };
        var src = new SchemaSnapshot
        {
            Engine = DatabaseEngine.PostgreSql, MajorVersion = 16,
            Objects =
            [
                schema,
                Table("lead_score", "CREATE POLICY \"p\" ON \"crm\".\"lead_score\" AS PERMISSIVE FOR SELECT TO PUBLIC USING ((score > 10));"),
                Table("secret", "CREATE POLICY \"p\" ON \"crm\".\"secret\" AS PERMISSIVE FOR SELECT TO app_reader USING (true);"),
            ]
        };
        var tgt = new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, MajorVersion = 16, Objects = [schema] };
        var output = Path.Combine(Path.GetTempPath(), $"schemascope-pg-offline-{Guid.NewGuid():n}");
        try
        {
            // a target that cannot be reached: which roles it has cannot be asked
            var endpoint = new ScriptEndpoint("Target", "127.0.0.1", "tgt", "Host=127.0.0.1;Port=1;Database=tgt;Timeout=1", DatabaseEngine.PostgreSql);
            var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(endpoint with { Label = "Source" }, endpoint, src, tgt,
                new CompareResult(), 0, new ScriptOptions { Sections = [ScriptSection.MissingObjects], OutputFolder = output, CheckExistingData = false },
                new HashSet<string>(), new Dictionary<string, LineChange>()), default, null);
            Assert.True(outcome.Success, outcome.Message);
            var missing = string.Join("\n", outcome.Files.Where(f => f.Folder == PgScriptGenerator.MissingFolder && f.IsObjectFile).Select(f => File.ReadAllText(f.FullPath)));
            // TO PUBLIC needs no role: created, and row level security with it
            Assert.Contains("CREATE POLICY \"p\" ON \"crm\".\"lead_score\"", missing);
            Assert.Contains("ALTER TABLE \"crm\".\"lead_score\" FORCE ROW LEVEL SECURITY;", missing);
            // a named role: the policy is held, and so is row level security, so the table is not left locked
            Assert.DoesNotContain("ON \"crm\".\"secret\" AS", missing);
            Assert.DoesNotContain("ALTER TABLE \"crm\".\"secret\" ENABLE ROW LEVEL SECURITY", missing);
            Assert.Contains(outcome.HeldBack, h => h.Object == "crm.secret.p" && h.Reason.Contains("app_reader"));
            Assert.Contains(outcome.HeldBack, h => h.Object == "crm.secret.rowSecurity" && h.Reason.Contains("row level security is left off"));
        }
        finally
        {
            try { Directory.Delete(output, true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ runner

    [Fact]
    public void A_file_whose_path_is_too_long_for_psql_runs_from_a_short_copy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-long-{Guid.NewGuid():n}", new string('d', 90), new string('e', 40));
        var objects = Directory.CreateDirectory(Path.Combine(root, "05_constraints", "objects")).FullName;
        var name = "001_procedure_billing.statement_insert_integer__character_varying__character_varying__character_varying_.sql";
        var file = Path.Combine(objects, name);
        File.WriteAllText(file, "SELECT 1;\n");
        Assert.True(file.Length >= 260, $"{file.Length}");
        File.WriteAllText(Path.Combine(root, "05_constraints", "run_all.ps1"), PgRunners.Folder(new PgRunnerTarget("db.example", 6543, "deploy", "verify-full")),
            new UTF8Encoding(true));
        var bin = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"schemascope-pg-long-bin-{Guid.NewGuid():n}")).FullName;
        var log = Path.Combine(bin, "ran.txt");
        try
        {
            // a stand-in psql: records the file it was given and whether that file could be read, connects nowhere
            File.WriteAllText(Path.Combine(bin, "psql.cmd"),
                "@setlocal enabledelayedexpansion\r\n@set last=\r\n@for %%a in (%*) do @set last=%%~a\r\n" +
                "@if exist \"!last!\" (echo !last! ^| READ>>\"%SS_RUNLOG%\") else (echo !last! ^| MISSING>>\"%SS_RUNLOG%\")\r\n@exit /b 0\r\n");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(root, "05_constraints", "run_all.ps1")}\" -Database d")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            psi.Environment["SS_RUNLOG"] = log;
            psi.Environment["PGPASSWORD"] = "not-a-real-password";
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), output);
            Assert.True(p.ExitCode == 0, output);
            var line = Assert.Single(File.ReadAllLines(log));
            var given = line.Split(" | ")[0].Trim();
            Assert.EndsWith("| READ", line.Trim());
            Assert.True(given.Length < 260, given);
            Assert.NotEqual(file, given);
            // the copy is removed once psql is done
            Assert.False(File.Exists(given));
        }
        finally
        {
            Directory.Delete(bin, true);
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, true);
        }
    }

    // ------------------------------------------------------------------ connections and secrets

    [Theory]
    [InlineData("localhost,61909")]
    [InlineData("a, 5433")]
    public void A_sql_server_style_port_after_a_comma_is_no_postgres_host(string server)
    {
        Assert.True(ConnectionEndpoints.HasPortAfterComma(server));
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, $"Host={server};Database=d"));
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, $"Server={server};User Id=postgres;Password=x;Database=d"));
        var s = new ConnectionSettings { Engine = DatabaseEngine.PostgreSql, Server = server, Database = "d", Auth = AuthMode.Password, UserName = "u", Password = "p" };
        Assert.Throws<ArgumentException>(() => new PgConnectionService().BuildConnectionString(s));
        Assert.Contains("the port goes in its own field", ConnectionRules.Problem(s));
        Assert.Contains("the port goes in its own field",
            ConnectionRules.Problem(new ConnectionSettings { Engine = DatabaseEngine.PostgreSql, UseRawConnectionString = true, Auth = AuthMode.Password,
                RawConnectionString = $"Server={server};User Id=postgres;Password=x;Database=d" }));
        // a host list and an address stay what they were
        Assert.False(ConnectionEndpoints.HasPortAfterComma("db1,db2"));
        Assert.Equal(["db1", "db2"], ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Host=db1,db2;Port=5433;Database=d")!.Hosts);
        Assert.Equal(["10.0.0.5"], ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Host=10.0.0.5;Database=d")!.Hosts);
    }

    [Fact]
    public void A_postgres_role_in_another_letter_case_is_another_destination()
    {
        ConnectionSettings Pg(string user) => new()
        {
            Engine = DatabaseEngine.PostgreSql, Server = "localhost", Port = 61909, Database = "d", Auth = AuthMode.Password, UserName = user, Password = "p",
        };
        Assert.True(ConnectionSettings.SameDestination(Pg("postgres"), Pg("postgres")));
        Assert.False(ConnectionSettings.SameDestination(Pg("Postgres"), Pg("postgres")));
        // SQL Server logins keep comparing without case
        ConnectionSettings Sql(string user) => new() { Server = "sql1", Database = "d", Auth = AuthMode.SqlLogin, UserName = user, Password = "p" };
        Assert.True(ConnectionSettings.SameDestination(Sql("Sa"), Sql("sa")));
    }
}

/// <summary>The apply refusals' wording, over HTTP with no database: which connection is Protected, and the preview switch.</summary>
public sealed class PgHardeningRouteTests : IAsyncLifetime
{
    private OwnerApp _o = null!;

    public async Task InitializeAsync() => _o = await OwnerApp.StartAsync();
    public async Task DisposeAsync() => await _o.DisposeAsync();

    private static async Task<string> Message(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString() ?? "";

    private (string RunId, string PlanId) RunWithPlan(SchemaSource source, SchemaSource target)
    {
        var session = _o.App.Services.GetRequiredService<CompareSessionStore>().Create();
        session.Status = "done";
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "Dev", Engine = source.Engine, Server = source.Server, Database = source.Database },
            Targets = [new TargetSummary { Label = target.Label, Engine = target.Engine, Server = target.Server, Database = target.Database }],
            Rows = [],
        };
        session.SourceConnection = source;
        session.TargetConnections = [target];
        var plan = _o.App.Services.GetRequiredService<ApplyPlanStore>().Add(new ApplyPlan
        {
            RunId = session.Id, TargetIndex = 0,
            Sections = [new PlanSection { Folder = "01_columns", Title = "New columns", Files = 1, DefaultSelected = true }],
        });
        return (session.Id, plan.Id);
    }

    private static SchemaSource Pg(string label, string cs, bool protectedServer = false) => new()
    {
        Label = label, Engine = DatabaseEngine.PostgreSql, Server = "127.0.0.1", Database = "App", ProtectedServer = protectedServer,
        ConnectionString = $"{cs};Timeout=2;SSL Mode=Disable",
    };

    private Task<HttpResponseMessage> Apply(string run, string plan) =>
        _o.PostJson($"/api/compare/{run}/apply", $$"""{ "planId": "{{plan}}", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "App" }""");

    [Fact]
    public async Task The_refusal_names_the_protected_connection_it_is_about()
    {
        await _o.SetPreviewAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);

        // another saved connection on the same host, any port, is the Protected one
        _o.Temp.Store.SaveConnection(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Label = "prot-other-port", Server = "127.0.0.1", Port = 6, Database = "whatever",
            Auth = AuthMode.Password, UserName = "u", Password = "p", Ssl = Core.Engines.SslMode.Disable, TrustServerCertificate = true, ProtectedServer = true,
        });
        var (run, plan) = RunWithPlan(Pg("Dev", "Host=127.0.0.2;Port=2;Database=Src"), Pg("pgdemo_client01", "Host=127.0.0.1;Port=61911;Database=App"));
        var other = await Apply(run, plan);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("pgdemo_client01 is on a server marked \"Protected - never apply scripts here\". Nothing was applied.", await Message(other));

        // the target's own connection marked: it says so
        (run, plan) = RunWithPlan(Pg("Dev", "Host=127.0.0.2;Port=2;Database=Src"), Pg("Prod", "Host=127.0.0.3;Port=1;Database=App", protectedServer: true));
        var own = await Apply(run, plan);
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal("Prod is marked \"Protected - never apply scripts here\". Nothing was applied.", await Message(own));
        Assert.Empty(_o.Temp.Store.GetApplyRuns());
    }
}
