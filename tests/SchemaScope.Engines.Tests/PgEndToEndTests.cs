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

            // and nothing writes: scripts are refused for this engine
            var folder = JsonSerializer.Serialize(Path.Combine(o.Temp.Folder, "scripts"));
            var scripts = await o.PostJson($"/api/compare/{runId}/scripts",
                $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns"] } }""");
            Assert.Equal(HttpStatusCode.BadRequest, scripts.StatusCode);
            Assert.Contains("not available for PostgreSQL in this version", await scripts.Content.ReadAsStringAsync());
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
