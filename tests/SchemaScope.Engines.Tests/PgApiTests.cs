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
        Assert.Equal(["compare"], pg.GetProperty("capabilities").GetProperty("features").EnumerateArray().Select(f => f.GetString()));
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
    public async Task Scripts_object_scripts_and_apply_are_a_clear_400_for_postgres()
    {
        await _o.SetPreviewAsync(true);
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

        Assert.Equal(HttpStatusCode.OK, (await _o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
        var apply = await _o.PostJson($"/api/compare/{run}/apply",
            """{ "planId": "nope", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "app" }""");
        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
        Assert.Contains("not available in this version", await Message(apply));
        Assert.Empty(_o.Temp.Store.GetApplyRuns());
    }
}
