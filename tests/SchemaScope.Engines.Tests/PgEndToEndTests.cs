using System.Net;
using System.Text.Json;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>PostgreSQL to PostgreSQL through the owner API, as the UI drives it, against a real container.</summary>
[Collection(PgCollection.Name)]
public sealed class PgEndToEndTests(PgServer pg)
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<string> SaveAsync(OwnerApp o, string database)
    {
        var r = await o.Post("/api/connections", pg.Settings(database));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Body(await r.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> RunToEndAsync(OwnerApp o, string runId)
    {
        for (var i = 0; i < 240; i++)
        {
            var summary = Body(await o.Http.GetStringAsync($"/api/compare/{runId}"));
            var status = summary.GetProperty("status").GetString();
            if (status != "running") return summary;
            await Task.Delay(250);
        }
        throw new TimeoutException("The compare did not finish.");
    }

    [PostgresFact]
    public async Task Two_saved_postgres_connections_compare_end_to_end()
    {
        var src = await pg.CreateDatabaseAsync("src");
        var tgt = await pg.CreateDatabaseAsync("tgt");
        await using var o = await OwnerApp.StartAsync();
        try
        {
            await pg.ExecAsync(src, PgScenario.Source);
            await pg.ExecAsync(tgt, PgScenario.Target);
            await o.SetPreviewAsync(true);

            var sourceId = await SaveAsync(o, src);
            var targetId = await SaveAsync(o, tgt);

            // the saved password is masked going out, and released again only to that same destination
            var list = Body(await o.Http.GetStringAsync("/api/connections")).EnumerateArray().ToList();
            Assert.All(list, c => Assert.NotEqual(pg.Password, c.GetProperty("password").GetString()));
            // a saved password needs DPAPI (Windows); elsewhere the form sends it each time, as the UI does there
            var asSent = SecretProtector.IsSupported ? list[0].GetRawText() : JsonSerializer.Serialize(pg.Settings(src), OwnerApp.Json);
            var test = await o.PostJson("/api/connections/test", asSent);
            var tested = Body(await test.Content.ReadAsStringAsync());
            Assert.True(tested.GetProperty("success").GetBoolean(), tested.GetRawText());
            Assert.StartsWith("PostgreSQL 16", tested.GetProperty("productVersion").GetString());

            var browse = await o.PostJson("/api/connections/databases", asSent);
            Assert.Contains(src, Body(await browse.Content.ReadAsStringAsync()).EnumerateArray().Select(d => d.GetProperty("name").GetString()));

            var started = SecretProtector.IsSupported
                ? await o.PostJson("/api/compare", $$"""
                    { "source": { "connectionId": "{{sourceId}}" }, "targets": [ { "connectionId": "{{targetId}}" } ] }
                    """)
                : await o.Post("/api/compare", new { source = new { inline = pg.Settings(src) }, targets = new[] { new { inline = pg.Settings(tgt) } } });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var runId = Body(await started.Content.ReadAsStringAsync()).GetProperty("runId").GetString()!;
            var summary = await RunToEndAsync(o, runId);
            Assert.Equal("done", summary.GetProperty("status").GetString());

            var result = summary.GetProperty("result");
            Assert.Equal("postgreSql", result.GetProperty("source").GetProperty("engine").GetString());
            var rows = result.GetProperty("rows").EnumerateArray().ToList();
            string StatusOf(string type, string name, string? signature = null) => rows
                .Single(r => r.GetProperty("type").GetString() == type && r.GetProperty("name").GetString() == name &&
                             (signature is null || r.GetProperty("signature").GetString() == signature))
                .GetProperty("cells")[0].GetProperty("status").GetString()!;

            Assert.Equal("same", StatusOf("table", "customers"));
            Assert.Equal("formattingOnly", StatusOf("scalarFunction", "fmt"));
            Assert.Equal("different", StatusOf("table", "orders"));
            Assert.Equal("missingInTarget", StatusOf("view", "v_missing"));
            Assert.Equal("missingInTarget", StatusOf("scalarFunction", "add", "text, text"));
            Assert.Equal("onlyInTarget", StatusOf("table", "extra"));

            // the diff panel: PostgreSQL text, PostgreSQL highlighting, and the column grid keyed exactly
            var key = rows.Single(r => r.GetProperty("name").GetString() == "orders").GetProperty("key").GetString()!;
            var detail = Body(await o.Http.GetStringAsync($"/api/compare/{runId}/object?key={Uri.EscapeDataString(key)}"));
            Assert.Equal("pgsql", detail.GetProperty("language").GetString());
            Assert.StartsWith("CREATE TABLE \"app\".\"orders\"", detail.GetProperty("sourceText").GetString());
            var columns = detail.GetProperty("targets")[0].GetProperty("structure")[0].GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal("onlyInTarget", columns.Single(c => c.GetProperty("name").GetString() == "note").GetProperty("status").GetString());

            // scripts go to a folder; with write mode on they come with an apply plan for this target
            Assert.Equal(HttpStatusCode.OK, (await o.PostJson("/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);
            var folder = JsonSerializer.Serialize(Path.Combine(o.Temp.Folder, "scripts"));
            var scripts = await o.PostJson($"/api/compare/{runId}/scripts",
                $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns", "missingObjects", "columnTypes", "constraints", "indexes"] } }""");
            Assert.Equal(HttpStatusCode.OK, scripts.StatusCode);
            var outcome = Body(await scripts.Content.ReadAsStringAsync());
            Assert.True(outcome.GetProperty("success").GetBoolean(), outcome.GetRawText());
            Assert.True(outcome.GetProperty("validation").GetProperty("passed").GetBoolean(), outcome.GetRawText());
            Assert.True(outcome.GetProperty("validation").GetProperty("level").GetProperty("parsed").GetBoolean());
            Assert.False(string.IsNullOrEmpty(outcome.GetProperty("planId").GetString()), outcome.GetRawText());
            Assert.Equal("postgreSql", outcome.GetProperty("applyTarget").GetProperty("engine").GetString());
            Assert.Equal(pg.Port, outcome.GetProperty("applyTarget").GetProperty("port").GetInt32());
            Assert.Equal("PostgreSQL user: postgres", outcome.GetProperty("applyTarget").GetProperty("login").GetString());
            var path = outcome.GetProperty("path").GetString()!;
            Assert.True(File.Exists(Path.Combine(path, "RUN.ps1")));
            var missing = string.Concat(Directory.EnumerateFiles(Path.Combine(path, "03_missing", "objects")).Select(File.ReadAllText));
            Assert.Contains("CREATE VIEW \"app\".\"v_missing\"", missing);
            Assert.DoesNotContain("OR REPLACE", missing);   // 03 creates, never replaces
            Assert.All(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories), f => Assert.DoesNotContain(pg.Password, File.ReadAllText(f)));

            // one object: a missing view, through pg_depend and the same generator
            var view = rows.Single(r => r.GetProperty("name").GetString() == "v_missing").GetProperty("key").GetString()!;
            var deps = Body(await o.Http.GetStringAsync($"/api/compare/{runId}/object-script/dependencies?key={Uri.EscapeDataString(view)}&targetIndex=0"));
            Assert.Equal("create", deps.GetProperty("plan").GetString());
            var one = await o.Post($"/api/compare/{runId}/object-script", new { targetIndex = 0, key = view, includeDependencies = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);
            var single = Body(await one.Content.ReadAsStringAsync());
            Assert.True(single.GetProperty("success").GetBoolean(), single.GetRawText());
            var script = single.GetProperty("script").GetString()!;
            Assert.Contains("CREATE VIEW \"app\".\"v_missing\"", script);
            Assert.DoesNotContain("OR REPLACE", script);
            Assert.DoesNotContain("\nGO", script);
            Assert.Single(single.GetProperty("parts").EnumerateArray());
            // object scripts go to the app's own scratch folder: leave nothing of this test there
            var written = Path.GetFullPath(single.GetProperty("path").GetString()!);
            Assert.StartsWith(Path.GetFullPath(SchemaScope.Api.Services.ObjectScripts.ScratchRoot) + Path.DirectorySeparatorChar, written);
            Directory.Delete(written, recursive: true);

            // an object only the target has: the DROP to copy, in PostgreSQL's spelling
            var extra = rows.Single(r => r.GetProperty("name").GetString() == "extra").GetProperty("key").GetString()!;
            var drop = Body(await (await o.Post($"/api/compare/{runId}/object-script", new { targetIndex = 0, key = extra, includeDependencies = Array.Empty<string>() }))
                .Content.ReadAsStringAsync());
            Assert.Contains("DROP TABLE \"app\".\"extra\";", drop.GetProperty("dropPreview").GetString());

            // and nothing is applied without a plan (PgApplyTests runs one)
            var apply = await o.PostJson($"/api/compare/{runId}/apply",
                $$"""{ "planId": "nope", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "{{tgt}}" }""");
            Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
            Assert.Empty(o.Temp.Store.GetApplyRuns());
        }
        finally
        {
            await pg.DropAsync(src);
            await pg.DropAsync(tgt);
        }
    }

    [PostgresFact]
    public async Task Tls_is_verified_by_default_and_never_quietly_dropped()
    {
        // the image has no TLS: the default (Verify full) must fail rather than fall back to an unencrypted link
        var settings = pg.Settings("postgres");
        settings.Ssl = null;
        settings.TrustServerCertificate = false;
        var svc = new PgConnectionService();
        var result = await svc.TestAsync(EndpointGuard.Checked(svc.BuildConnectionString(settings), pg));
        Assert.False(result.Success);

        settings.Ssl = SchemaScope.Core.Engines.SslMode.Disable;
        settings.TrustServerCertificate = true;   // the explicit opt-in to no TLS
        Assert.True((await svc.TestAsync(EndpointGuard.Checked(svc.BuildConnectionString(settings), pg))).Success);
    }

    [PostgresFact]
    public async Task A_failed_sign_in_says_so_without_echoing_the_password()
    {
        const string wrong = "not-the-password-4711";
        var svc = new PgConnectionService();
        var result = await svc.TestAsync(pg.ConnectionString("postgres", password: wrong));
        Assert.False(result.Success);
        Assert.StartsWith("28P01", result.Message);
        Assert.Equal("Login failed. Check the user name and password.", result.Hint);
        Assert.DoesNotContain(wrong, result.Message);
    }

    [PostgresFact]
    public async Task The_readers_transaction_refuses_every_write()
    {
        // the reader opens its transaction with exactly this; on a real server nothing can then be written in it
        var db = await pg.CreateDatabaseAsync("ro");
        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString(db));
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            await using (var setup = new Npgsql.NpgsqlCommand(PgSchemaReader.SessionSetup(30), conn, tx))
                await setup.ExecuteNonQueryAsync();

            await using var write = new Npgsql.NpgsqlCommand("CREATE TABLE written (id int)", conn, tx);
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => write.ExecuteNonQueryAsync());
            Assert.Equal("25006", refused.SqlState);   // read_only_sql_transaction
            await tx.RollbackAsync();

            await using var check = new Npgsql.NpgsqlCommand("SELECT to_regclass('public.written') IS NULL", conn);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            await pg.DropAsync(db);
        }
    }
}
