using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Engines.Tests;

/// <summary>The owner API with PostgreSQL in it, no database needed: the preview switch, the refusals and their wording.</summary>
public sealed class PgRouteTests : IAsyncLifetime
{
    private OwnerApp _o = null!;

    public async Task InitializeAsync() => _o = await OwnerApp.StartAsync();
    public async Task DisposeAsync() => await _o.DisposeAsync();

    private static string PgConnection(string extra = "") => $$"""
        { "engine": "postgreSql", "label": "pg", "server": "127.0.0.1", "port": 1, "database": "app", "auth": "password",
          "userName": "u", "password": "pw", "ssl": "disable", "trustServerCertificate": true {{extra}} }
        """;

    private static async Task<string> Message(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString() ?? "";

    [Fact]
    public async Task With_the_switch_off_postgres_is_not_offered_and_is_refused()
    {
        var engines = JsonDocument.Parse(await _o.Http.GetStringAsync("/api/engines")).RootElement;
        Assert.Equal(["sqlServer"], engines.EnumerateArray().Select(e => e.GetProperty("engine").GetString()));

        var preview = JsonDocument.Parse(await _o.Http.GetStringAsync("/api/engines/preview")).RootElement;
        Assert.False(preview.GetProperty("enabled").GetBoolean());
        Assert.Equal(["PostgreSQL"], preview.GetProperty("engines").EnumerateArray().Select(e => e.GetString()));

        foreach (var path in new[] { "/api/connections", "/api/connections/test", "/api/connections/databases" })
        {
            var r = await _o.PostJson(path, PgConnection());
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("Show preview engines", await Message(r));
        }
        Assert.Empty(_o.Temp.Store.GetConnections());
    }

    [Fact]
    public async Task With_the_switch_on_postgres_is_described_and_can_be_saved()
    {
        await _o.SetPreviewAsync(true);
        var engines = JsonDocument.Parse(await _o.Http.GetStringAsync("/api/engines")).RootElement.EnumerateArray().ToList();
        var pg = engines.Single(e => e.GetProperty("engine").GetString() == "postgreSql");
        Assert.True(pg.GetProperty("preview").GetBoolean());
        Assert.Equal("postgres", pg.GetProperty("browseDatabase").GetString());
        Assert.Equal("pgsql", pg.GetProperty("monacoLanguage").GetString());
        Assert.Equal(["apply", "compare", "objectScripts", "scripts"], pg.GetProperty("capabilities").GetProperty("features").EnumerateArray().Select(f => f.GetString()).Order());
        // the apply dialog's promise comes from the engine's transaction semantics, and the typed name is exact
        Assert.Equal("perFileTransactional", pg.GetProperty("transactions").GetString());
        Assert.StartsWith("Each file runs in its own transaction", pg.GetProperty("apply").GetProperty("transactions").GetString());
        Assert.True(pg.GetProperty("apply").GetProperty("typedNameExact").GetBoolean());
        Assert.False(engines.Single(e => e.GetProperty("engine").GetString() == "sqlServer").TryGetProperty("apply", out _));
        // the dialog's wording for this engine: psql, an honest validation line, and the sections that do not apply greyed out
        var scripts = pg.GetProperty("scripts");
        Assert.Equal("psql", scripts.GetProperty("runner").GetString());
        Assert.StartsWith("Parsed with the PostgreSQL parser", scripts.GetProperty("validation").GetString());
        var sections = scripts.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(["tableTypes", "formatting"], sections.Where(s => !s.GetProperty("applicable").GetBoolean()).Select(s => s.GetProperty("section").GetString()));
        Assert.False(engines.Single(e => e.GetProperty("engine").GetString() == "sqlServer").TryGetProperty("scripts", out _));
        Assert.True(pg.GetProperty("form").GetProperty("sslMode").GetBoolean());
        Assert.False(pg.GetProperty("defaults").GetProperty("trustServerCertificate").GetBoolean());

        var saved = await _o.PostJson("/api/connections", PgConnection());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
        // never sent back as typed
        Assert.NotEqual("pw", body.TryGetProperty("password", out var password) ? password.GetString() : null);
        if (Core.Storage.SecretProtector.IsSupported) Assert.Equal("********", password.GetString());
        Assert.Equal("postgreSql", body.GetProperty("engine").GetString());

        // the static helpers read strings with the same engines DI has turned on
        Assert.Equal(_o.App.Services.GetRequiredService<EngineRegistry>().All.Select(p => p.Engine),
            Api.SchemaScopeApi.Engines.All.Select(p => p.Engine));
    }

    [Fact]
    public async Task A_postgres_password_is_stored_encrypted_in_engine_connections()
    {
        if (!OperatingSystem.IsWindows()) return;   // DPAPI
        await _o.SetPreviewAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/connections", PgConnection()
            .Replace("\"pw\"", "\"correct-horse-battery\""))).StatusCode);

        await using var conn = new SqliteConnection($"Data Source={_o.Temp.DbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM engine_connections";
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (r.IsDBNull(i)) continue;
            var value = r.GetValue(i);
            var text = value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : value.ToString() ?? "";
            Assert.DoesNotContain("correct-horse-battery", text);
        }
        await using var legacy = conn.CreateCommand();
        legacy.CommandText = "SELECT COUNT(*) FROM connections";
        Assert.Equal(0L, (long)(await legacy.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_preview_switch_takes_json_and_the_token()
    {
        var form = await _o.Http.PostAsync("/api/engines/preview", new FormUrlEncodedContent([new("enabled", "true")]));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);

        using var anonymous = new HttpClient { BaseAddress = _o.Http.BaseAddress };
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync("/api/engines/preview")).StatusCode);
        var post = await anonymous.PostAsync("/api/engines/preview",
            new StringContent("""{ "enabled": true }""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.False(_o.App.Services.GetRequiredService<EngineCatalog>().PreviewEnabled);
    }

    [Fact]
    public async Task A_uri_form_string_is_refused_before_anything_connects()
    {
        await _o.SetPreviewAsync(true);
        var r = await _o.PostJson("/api/connections/test", """
            { "engine": "postgreSql", "auth": "password", "useRawConnectionString": true,
              "rawConnectionString": "postgres://deploy:hunter2@127.0.0.1:1/app" }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var message = await Message(r);
        Assert.Contains("URI", message);
        Assert.DoesNotContain("hunter2", message);
    }

    [Fact]
    public async Task A_cross_engine_compare_is_refused_by_the_server()
    {
        await _o.SetPreviewAsync(true);
        var r = await _o.PostJson("/api/compare", $$"""
            { "source": { "inline": { "server": "127.0.0.1,1", "database": "a" } },
              "targets": [ { "inline": {{PgConnection()}} } ] }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Comparing across engines is not available", await Message(r));
    }

    /// <summary>A finished PostgreSQL-to-PostgreSQL run, as a compare would leave it.</summary>
    private string PgRun()
    {
        var session = _o.App.Services.GetRequiredService<CompareSessionStore>().Create();
        session.Status = "done";
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "Dev", Engine = DatabaseEngine.PostgreSql, Server = "pg", Database = "app" },
            Targets = [new TargetSummary { Label = "Prod", Engine = DatabaseEngine.PostgreSql, Server = "pg2", Database = "app" }],
            Rows = [new CompareRow { Key = "1|app.t", Type = DbObjectType.Table, Schema = "app", Name = "t",
                Cells = [new TargetCell { Status = ObjectStatus.Different }], Worst = ObjectStatus.Different }]
        };
        session.SourceConnection = new SchemaSource { Label = "Dev", Engine = DatabaseEngine.PostgreSql, Server = "pg", Database = "app", ConnectionString = "Host=pg;Database=app" };
        session.TargetConnections = [new SchemaSource { Label = "Prod", Engine = DatabaseEngine.PostgreSql, Server = "pg2", Database = "app", ConnectionString = "Host=pg2;Database=app" }];
        return session.Id;
    }

    [Fact]
    public async Task Without_the_preview_switch_postgres_scripts_are_a_clear_400()
    {
        var run = PgRun();
        var folder = JsonSerializer.Serialize(Path.Combine(_o.Temp.Folder, "scripts"));
        var scripts = await _o.PostJson($"/api/compare/{run}/scripts",
            $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns"] } }""");
        Assert.Equal(HttpStatusCode.BadRequest, scripts.StatusCode);
        Assert.Equal("Scripts are not available for PostgreSQL in this version.", await Message(scripts));
        Assert.False(Directory.Exists(Path.Combine(_o.Temp.Folder, "scripts")));

        var one = await _o.PostJson($"/api/compare/{run}/object-script", """{ "targetIndex": 0, "key": "1|app.t", "includeDependencies": [] }""");
        Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);
        Assert.Equal("Single-object scripts are not available for PostgreSQL in this version.", await Message(one));
    }

    [Fact]
    public async Task Postgres_scripts_need_the_compare_s_snapshots_and_apply_needs_a_plan()
    {
        await _o.SetPreviewAsync(true);
        var run = PgRun();
        var folder = JsonSerializer.Serialize(Path.Combine(_o.Temp.Folder, "scripts"));

        // this session has no snapshots, so the generator refuses before it writes anything
        var scripts = await _o.PostJson($"/api/compare/{run}/scripts",
            $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns"] } }""");
        Assert.Equal(HttpStatusCode.OK, scripts.StatusCode);
        var body = JsonDocument.Parse(await scripts.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("could not be read in this comparison", body.GetProperty("message").GetString());
        Assert.False(Directory.Exists(Path.Combine(_o.Temp.Folder, "scripts")));

        var one = await _o.PostJson($"/api/compare/{run}/object-script", """{ "targetIndex": 0, "key": "1|app.t", "includeDependencies": [] }""");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.False(JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
        // 11_destructive does not exist for PostgreSQL yet, whatever the dialog sends
        var destructive = await _o.PostJson($"/api/compare/{run}/scripts",
            $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns"], "includeDestructive": true } }""");
        Assert.Equal(HttpStatusCode.BadRequest, destructive.StatusCode);
        Assert.Equal("Changes that can lose data are not available for PostgreSQL in this version.", await Message(destructive));

        var apply = await _o.PostJson($"/api/compare/{run}/apply",
            """{ "planId": "nope", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "app" }""");
        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
        Assert.Contains("unknown or has expired", await Message(apply));
        Assert.Empty(_o.Temp.Store.GetApplyRuns());
    }

    /// <summary>A finished run with the given connections and a live plan for its first target; nothing listens on port 1.</summary>
    private (string RunId, string PlanId) RunWithPlan(SchemaSource source, SchemaSource target)
    {
        var session = _o.App.Services.GetRequiredService<CompareSessionStore>().Create();
        session.Status = "done";
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "Dev", Engine = source.Engine, Server = source.Server, Database = source.Database },
            Targets = [new TargetSummary { Label = "Prod", Engine = target.Engine, Server = target.Server, Database = target.Database }],
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

    private static SchemaSource Pg(string label, string cs) => new()
    {
        Label = label, Engine = DatabaseEngine.PostgreSql, Server = "127.0.0.1", Database = "App",
        ConnectionString = $"{cs};Timeout=2;SSL Mode=Disable",
    };

    private Task<HttpResponseMessage> Apply(string run, string plan, string confirm) =>
        _o.PostJson($"/api/compare/{run}/apply",
            $$"""{ "planId": "{{plan}}", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "{{confirm}}" }""");

    [Fact]
    public async Task The_postgres_gates_refuse_in_order_before_anything_is_run_or_logged()
    {
        await _o.SetPreviewAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);

        // PostgreSQL names are exact: "app" is not the database "App"
        var (run, plan) = RunWithPlan(Pg("Dev", "Host=127.0.0.1;Port=1;Database=Src"), Pg("Prod", "Host=127.0.0.1;Port=1;Database=App"));
        var wrongCase = await Apply(run, plan, "app");
        Assert.Equal(HttpStatusCode.BadRequest, wrongCase.StatusCode);
        Assert.Equal("Type the target database name, App, exactly as written (letter case counts), to confirm.", await Message(wrongCase));

        // the source itself, however its host is spelled; on another port it is another server
        (run, plan) = RunWithPlan(Pg("Dev", "Host=localhost;Port=1;Database=App"), Pg("Prod", "Host=127.0.0.1;Port=1;Database=App"));
        var source = await Apply(run, plan, "App");
        Assert.Equal(HttpStatusCode.Forbidden, source.StatusCode);
        Assert.Contains("source database", await Message(source));

        // so the next gate is reached: a server that cannot be asked who it is is refused (SQL Server would go on)
        (run, plan) = RunWithPlan(Pg("Dev", "Host=127.0.0.1;Port=2;Database=App"), Pg("Prod", "Host=127.0.0.1;Port=1;Database=App"));
        var unknown = await Apply(run, plan, "App");
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal("Prod could not be asked who it is, so nothing was applied.", await Message(unknown));
        // and the refusals did not use up the plan
        Assert.NotNull(_o.App.Services.GetRequiredService<ApplyPlanStore>().Get(plan));

        // scripts across engines are never applied, whatever the session holds
        (run, plan) = RunWithPlan(new SchemaSource { Label = "Dev", Server = "127.0.0.1,1", Database = "App",
            ConnectionString = "Server=127.0.0.1,1;Database=App;Integrated Security=true;Encrypt=false" }, Pg("Prod", "Host=127.0.0.1;Port=1;Database=App"));
        var across = await Apply(run, plan, "App");
        Assert.Equal(HttpStatusCode.BadRequest, across.StatusCode);
        Assert.Equal("Scripts made across engines are never applied from SchemaScope.", await Message(across));

        Assert.Empty(_o.Temp.Store.GetApplyRuns());
    }

    [Fact]
    public async Task Without_the_preview_switch_there_is_no_postgres_apply()
    {
        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
        var (run, plan) = RunWithPlan(Pg("Dev", "Host=127.0.0.1;Port=2;Database=App"), Pg("Prod", "Host=127.0.0.1;Port=1;Database=App"));
        var apply = await Apply(run, plan, "App");
        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
        Assert.Equal("Applying scripts to PostgreSql is not available in this version.", await Message(apply));
    }
}
