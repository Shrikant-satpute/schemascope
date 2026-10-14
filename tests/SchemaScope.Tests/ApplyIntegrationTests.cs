using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>
/// SQL Server LocalDB, only ever with throwaway databases this class creates (SchemaScope_Test_&lt;id&gt;_Src / _Tgt)
/// and drops again. Where LocalDB is not installed (CI), the tests are skipped.
/// </summary>
internal static class LocalDb
{
    public const string Server = @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<string?> Missing = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return "LocalDB needs Windows.";
        try
        {
            using var conn = new SqlConnection(Conn("master"));
            conn.Open();
            return null;
        }
        catch (Exception ex)
        {
            return $"LocalDB is not available: {ex.Message}";
        }
    });

    public static string? SkipReason => Missing.Value;

    public static string Conn(string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            Encrypt = false,
            ConnectTimeout = 30,
        }.ConnectionString;

    public static string NewName(string id, string side) => $"SchemaScope_Test_{id}_{side}";

    public static void Create(string database, string script)
    {
        Exec("master", $"CREATE DATABASE [{database}]");
        Exec(database, script);
    }

    /// <summary>Runs <paramref name="script"/> batch by batch on one connection, so a SET carries to the next batch.</summary>
    public static void Exec(string database, string script)
    {
        using var conn = new SqlConnection(Conn(database));
        conn.Open();
        foreach (var batch in ScriptText.SplitBatches(script))
        {
            using var cmd = new SqlCommand(batch.Text, conn);
            cmd.ExecuteNonQuery();
        }
    }

    public static object? Scalar(string database, string sql)
    {
        using var conn = new SqlConnection(Conn(database));
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        return cmd.ExecuteScalar();
    }

    /// <summary>Drops a database this class created. Only ever a SchemaScope_Test_ name.</summary>
    public static void Drop(string database)
    {
        if (!database.StartsWith("SchemaScope_Test_", StringComparison.Ordinal)) throw new InvalidOperationException(database);
        SqlConnection.ClearAllPools();
        try
        {
            Exec("master", $"""
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END
                """);
        }
        catch (SqlException)
        {
            // best effort: a leftover throwaway database is harmless and named for what it is
        }
    }
}

/// <summary>A fact that is skipped when LocalDB is not there.</summary>
public sealed class LocalDbFactAttribute : FactAttribute
{
    public LocalDbFactAttribute()
    {
        if (LocalDb.SkipReason is { } reason) Skip = reason;
    }
}

public sealed class ApplyIntegrationTests
{
    private const string SourceSchema = """
        CREATE TABLE dbo.Customer (Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name nvarchar(100) NULL);
        CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NULL CONSTRAINT FK_Orders_Customer REFERENCES dbo.Customer (Id));
        CREATE TABLE dbo.NewTable (Id int NOT NULL CONSTRAINT PK_NewTable PRIMARY KEY, Note nvarchar(50) NULL);
        GO
        SET ANSI_NULLS OFF;
        SET QUOTED_IDENTIFIER OFF;
        GO
        CREATE PROCEDURE dbo.p_new AS SELECT Id, Note FROM dbo.NewTable WHERE Note = "x"
        GO
        """;

    private const string TargetSchema = """
        CREATE TABLE dbo.Customer (Id int NOT NULL CONSTRAINT PK_Customer PRIMARY KEY, Name nvarchar(50) NULL);
        CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
            CustomerId int NULL CONSTRAINT FK_Orders_Customer REFERENCES dbo.Customer (Id));
        ALTER TABLE dbo.Orders NOCHECK CONSTRAINT FK_Orders_Customer;
        INSERT dbo.Customer VALUES (1, N'Ada');
        """;

    private static object Side(string database) => new
    {
        inline = new { server = LocalDb.Server, database, auth = "windows", encrypt = false, trustServerCertificate = true },
    };

    private static async Task<JsonElement> CompareAsync(HttpClient http, string src, string tgt)
    {
        var started = await http.PostAsJsonAsync("/api/compare", new { source = Side(src), targets = new[] { Side(tgt) } });
        started.EnsureSuccessStatusCode();
        var runId = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString();
        for (var i = 0; i < 240; i++)
        {
            var run = JsonDocument.Parse(await http.GetStringAsync($"/api/compare/{runId}")).RootElement;
            if (run.GetProperty("status").GetString() != "running")
            {
                Assert.Equal("done", run.GetProperty("status").GetString());
                return run;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The compare did not finish.");
    }

    private static double MatchPercent(JsonElement run) =>
        run.GetProperty("result").GetProperty("targets")[0].GetProperty("matchPercent").GetDouble();

    [LocalDbFact]
    public async Task Default_sections_bring_the_target_level_with_the_source_and_keep_module_options()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        string src = LocalDb.NewName(id, "Src"), tgt = LocalDb.NewName(id, "Tgt");
        using var temp = new TempStore();
        using var output = new TempFolder();
        var auth = LocalAuth.Mint();
        var app = SchemaScopeApi.Build([], desktop: false, auth, storePath: temp.DbPath);
        try
        {
            LocalDb.Create(src, SourceSchema);
            LocalDb.Create(tgt, TargetSchema);

            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.Add(LocalAuth.HeaderName, auth.Token);

            var before = await CompareAsync(http, src, tgt);
            Assert.True(MatchPercent(before) < 100);
            var runId = before.GetProperty("runId").GetString();

            (await http.PostAsJsonAsync("/api/write-mode", new { enabled = true, confirm = "ENABLE" })).EnsureSuccessStatusCode();
            var scripts = await http.PostAsJsonAsync($"/api/compare/{runId}/scripts", new
            {
                targetIndex = 0,
                options = new
                {
                    outputFolder = output.Path,
                    sections = new[] { "newColumns", "missingObjects", "columnTypes", "constraints", "indexes", "formatting" },
                },
            });
            var outcomeText = await scripts.Content.ReadAsStringAsync();
            Assert.True(scripts.StatusCode == HttpStatusCode.OK, outcomeText);
            var outcome = JsonDocument.Parse(outcomeText).RootElement;
            Assert.True(outcome.GetProperty("success").GetBoolean(), outcomeText);
            var planId = outcome.GetProperty("planId").GetString();
            Assert.False(string.IsNullOrEmpty(planId), outcomeText);
            var target = outcome.GetProperty("applyTarget");
            Assert.Equal(tgt, target.GetProperty("database").GetString());
            Assert.StartsWith("Windows: ", target.GetProperty("login").GetString());
            Assert.False(target.GetProperty("protected").GetBoolean());

            var sections = outcome.GetProperty("planSections").EnumerateArray()
                .Where(s => s.GetProperty("defaultSelected").GetBoolean())
                .Select(s => s.GetProperty("folder").GetString()!).ToList();
            Assert.Contains("03_missing", sections);
            Assert.Contains("04_columntypes", sections);

            var applied = await http.PostAsJsonAsync($"/api/compare/{runId}/apply", new
            {
                planId,
                targetIndex = 0,
                sections,
                confirmDatabase = tgt.ToUpperInvariant(),
                acceptDataLoss = false,
                progressId = Guid.NewGuid().ToString(),
            });
            var resultText = await applied.Content.ReadAsStringAsync();
            Assert.True(applied.StatusCode == HttpStatusCode.OK, resultText);
            var result = JsonDocument.Parse(resultText).RootElement;
            Assert.True(result.GetProperty("success").GetBoolean(), resultText);
            Assert.All(result.GetProperty("applied").EnumerateArray(), f => Assert.True(f.GetProperty("ok").GetBoolean()));

            var after = await CompareAsync(http, src, tgt);
            Assert.Equal(100, MatchPercent(after));

            // the module kept the source's options: "x" is still a string, not a column
            Assert.Equal(0, Convert.ToInt32(LocalDb.Scalar(tgt,
                "SELECT CAST(uses_ansi_nulls AS int) + CAST(uses_quoted_identifier AS int) FROM sys.sql_modules WHERE object_id = OBJECT_ID('dbo.p_new')")));
            Assert.Equal(200, Convert.ToInt32(LocalDb.Scalar(tgt,
                "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Customer') AND name = 'Name'")));
            Assert.Equal(0, Convert.ToInt32(LocalDb.Scalar(tgt,
                "SELECT CAST(is_disabled AS int) + CAST(is_not_trusted AS int) FROM sys.foreign_keys WHERE name = 'FK_Orders_Customer'")));

            var run = Assert.Single(temp.Store.GetApplyRuns());
            Assert.True(run.Success);
            Assert.Equal(tgt, run.Database);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            LocalDb.Drop(src);
            LocalDb.Drop(tgt);
        }
    }

    [LocalDbFact]
    public async Task A_failing_file_is_rolled_back_and_nothing_after_it_runs()
    {
        var tgt = LocalDb.NewName(Guid.NewGuid().ToString("n")[..8], "Tgt");
        using var temp = new TempStore();
        using var files = new TempFolder();
        try
        {
            LocalDb.Create(tgt, "SELECT 1");
            var written = new[]
            {
                PlanFiles.Write(files.Path, "04_columntypes", "001_kept.sql", "CREATE TABLE dbo.Kept (Opts int)\r\nGO\r\nINSERT dbo.Kept VALUES (@@OPTIONS)\r\nGO\r\n", "Kept"),
                PlanFiles.Write(files.Path, "01_columns", "001_fail.sql", "CREATE TABLE dbo.RolledBack (Id int)\r\nGO\r\n\r\nRAISERROR('boom', 16, 1)\r\nGO\r\n", "RolledBack"),
                PlanFiles.Write(files.Path, "03_missing", "001_never.sql", "CREATE TABLE dbo.NeverRun (Id int)\r\n", "NeverRun"),
            };
            var (planFiles, _) = ApplyPlans.Build(written, []);
            Assert.Null(ApplyService.Verify(planFiles, out var verified));

            var mode = new WriteMode();
            mode.Enable();
            var service = new ApplyService(temp.Store, mode, NullLogger<ApplyService>.Instance);
            var target = new ApplyTarget { Label = "Throwaway", Server = LocalDb.Server, Database = tgt, Login = "Windows: test" };
            var result = await service.RunAsync(
                new ApplyJob("run", LocalDb.Conn(tgt), target, planFiles, ["04_columntypes", "01_columns", "03_missing"], null), verified, null);

            Assert.False(result.Success);
            Assert.Equal(2, result.Applied.Count);
            Assert.True(result.Applied[0].Ok);
            Assert.False(result.Applied[1].Ok);
            Assert.Contains("Msg 50000, line 4: boom", result.Applied[1].Error);
            Assert.Equal("01_columns\\objects\\001_fail.sql", result.StoppedAt);
            Assert.Contains("stay applied", result.Message);

            Assert.Equal(ApplyService.SqlcmdOptions, Convert.ToInt32(LocalDb.Scalar(tgt, "SELECT Opts FROM dbo.Kept")));
            Assert.Equal(DBNull.Value, LocalDb.Scalar(tgt, "SELECT OBJECT_ID('dbo.RolledBack')"));
            Assert.Equal(DBNull.Value, LocalDb.Scalar(tgt, "SELECT OBJECT_ID('dbo.NeverRun')"));

            var run = Assert.Single(temp.Store.GetApplyRuns());
            Assert.False(run.Success);
            Assert.Equal(2, run.Files.Count);
        }
        finally
        {
            LocalDb.Drop(tgt);
        }
    }
}
