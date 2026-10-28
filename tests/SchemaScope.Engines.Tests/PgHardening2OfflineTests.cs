using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>Hardening round 2's defects that need no server: apply stop, secret release, history, shares, exports, text helpers.</summary>
public sealed class PgHardening2OfflineTests : IDisposable
{
    private readonly TempStore _temp = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-r2-{Guid.NewGuid():n}");
    private const string RealPassword = "correct horse battery staple";

    public void Dispose()
    {
        _temp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------- Stop while the gates still run

    [Fact]
    public async Task A_stop_sent_while_the_gates_run_is_kept_and_nothing_is_applied()
    {
        var (plan, verified) = PgPlanFiles.Plan(_root, ("01_columns", "001_a.sql", "ALTER TABLE app.t ADD COLUMN c integer;\n"));
        var mode = new WriteMode();
        mode.Enable();
        var service = new ApplyService(_temp.Store, mode, NullLogger<ApplyService>.Instance, [PgApplyExecutor.Instance]);
        var id = Guid.NewGuid().ToString();
        // nothing listens on port 1: a file that ran would fail, so a stop that was lost shows as a failure
        var job = new ApplyJob("run", "Host=127.0.0.1;Port=1;Database=app;Username=u;Password=p;Timeout=2;SSL Mode=Disable", new ApplyTarget
        {
            Label = "Throwaway", Engine = DatabaseEngine.PostgreSql, Server = "127.0.0.1", Port = 1, Database = "app", Login = "PostgreSQL user: u",
        }, plan, ["01_columns"], id);

        Assert.False(service.Stop(id));   // nothing reports to it yet
        ApplyResult result;
        using (service.Reserve(id))
        {
            // the route's gates are running: the dialog's Stop reaches the apply now
            Assert.True(service.Stop(id));
            result = await service.RunAsync(job, verified, null);
        }
        Assert.False(result.Success);
        Assert.Empty(result.Applied);
        Assert.Equal("01_columns\\objects\\001_a.sql", result.StoppedAt);
        Assert.Contains("you stopped it", result.Message);
        Assert.False(service.Stop(id));   // the reservation ended with the request
        var audit = Assert.Single(_temp.Store.GetApplyRuns());
        Assert.False(audit.Success);
    }

    // ---------------- an edited raw string with its password masked

    private ConnectionSettings SavedRaw(string raw) => _temp.Store.SaveConnection(new ConnectionSettings
    {
        Engine = DatabaseEngine.PostgreSql, Label = "PG Tgt", Auth = AuthMode.Password, UseRawConnectionString = true, RawConnectionString = raw,
    });

    private static ConnectionSettings Edited(ConnectionSettings saved, string raw) => new()
    {
        Id = saved.Id, Label = saved.Label, Engine = saved.Engine, Auth = saved.Auth, UseRawConnectionString = true, RawConnectionString = raw,
    };

    [Fact]
    public void An_edited_raw_string_keeps_its_edits_and_gets_its_saved_password_back()
    {
        if (!SecretProtector.IsSupported) return;   // DPAPI
        var saved = SavedRaw($"Host=localhost;Port=49778;Database=ShopDb;Username=postgres;Password={RealPassword};SSL Mode=Disable");

        // another database and stronger TLS: the edit is kept, the password follows
        var edit = Edited(saved, "Host=localhost;Port=49778;Database=shopdb;Username=postgres;Password=********;SSL Mode=Require;Trust Server Certificate=true");
        Assert.True(SecretRelease.TryRehydrate(edit, _temp.Store, out var error), error);
        Assert.Contains(RealPassword, edit.RawConnectionString);
        Assert.Contains("shopdb", edit.RawConnectionString);
        Assert.DoesNotContain("ShopDb", edit.RawConnectionString);
        Assert.Equal("shopdb", ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, edit.RawConnectionString)!.Database);

        // the string as the UI shows it, untouched: the saved one
        var same = Edited(saved, saved.WithoutSecrets().RawConnectionString!);
        Assert.True(SecretRelease.TryRehydrate(same, _temp.Store, out _));
        Assert.Contains(RealPassword, same.RawConnectionString);
    }

    [Fact]
    public void An_edited_raw_string_that_weakens_tls_or_changes_the_login_gets_no_password()
    {
        if (!SecretProtector.IsSupported) return;   // DPAPI
        var saved = SavedRaw($"Host=localhost;Port=49778;Database=ShopDb;Username=postgres;Password={RealPassword};SSL Mode=Require");

        foreach (var raw in new[]
                 {
                     "Host=localhost;Port=49778;Database=ShopDb;Username=postgres;Password=********;SSL Mode=Disable",
                     "Host=localhost;Port=49778;Database=ShopDb;Username=Postgres;Password=********;SSL Mode=Require",
                     "Host=localhost;Port=49778;Database=ShopDb;Username=postgres;Password=********;SSL Password=********;SSL Mode=Require",
                 })
        {
            var edit = Edited(saved, raw);
            Assert.False(SecretRelease.TryRehydrate(edit, _temp.Store, out var error), raw);
            Assert.Contains("Type the password", error);
            Assert.DoesNotContain(RealPassword, edit.RawConnectionString);
        }
    }

    // ---------------- history and shares

    [Fact]
    public void A_postgres_database_s_letter_case_is_part_of_the_history_signature()
    {
        static CompareHistoryDatabase Db(DatabaseEngine engine, string database) =>
            new() { ConnectionId = "c1", Engine = engine, Server = "localhost", Database = database };
        var source = Db(DatabaseEngine.PostgreSql, "src");
        Assert.NotEqual(CompareHistoryEntry.BuildSignature(source, [Db(DatabaseEngine.PostgreSql, "ShopDb")], exactDatabases: true),
            CompareHistoryEntry.BuildSignature(source, [Db(DatabaseEngine.PostgreSql, "shopdb")], exactDatabases: true));
        // SQL Server keeps the signature it always had
        var sql = Db(DatabaseEngine.SqlServer, "Src");
        Assert.Equal(CompareHistoryEntry.BuildSignature(sql, [Db(DatabaseEngine.SqlServer, "ShopDb")], exactDatabases: true),
            CompareHistoryEntry.BuildSignature(sql, [Db(DatabaseEngine.SqlServer, "shopdb")], exactDatabases: true));
        Assert.Equal("c1/src=>c1/shopdb", CompareHistoryEntry.BuildSignature(sql, [Db(DatabaseEngine.SqlServer, "ShopDb")]));
    }

    [Fact]
    public async Task A_share_card_names_a_postgres_source_with_its_port()
    {
        await using var o = await OwnerApp.StartAsync();
        var session = o.App.Services.GetRequiredService<CompareSessionStore>().Create();
        session.Status = "done";
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "PG Src", Engine = DatabaseEngine.PostgreSql, Server = "localhost", Port = 49777, Database = "ShopDb" },
            Targets = [new TargetSummary { Label = "PG Tgt", Engine = DatabaseEngine.PostgreSql, Server = "localhost", Port = 49778, Database = "ShopDb" }],
            Rows = [],
        };
        var record = o.App.Services.GetRequiredService<ShareStore>().Publish(session, o.App.Services.GetRequiredService<CompareService>(), TimeSpan.FromMinutes(5));
        Assert.Equal("localhost:49777 / ShopDb", record.SourceDetail);
    }

    // ---------------- .sql export

    [Fact]
    public void A_sql_export_writes_every_overload_to_its_own_file()
    {
        DbObject Fn(string signature, string body) => new()
        {
            Type = DbObjectType.ScalarFunction, Schema = "core", Name = "format_money", Fold = IdentifierFold.Exact, Signature = signature, Definition = body,
        };
        var a = Fn("numeric", "CREATE FUNCTION core.format_money(p_amount numeric) ...");
        var b = Fn("numeric, character", "CREATE FUNCTION core.format_money(p_amount numeric, p_currency character) ...");
        // two tables whose names differ only by letter case: two objects, one file name on Windows
        var t1 = new DbObject { Type = DbObjectType.Table, Schema = "app", Name = "Users", Fold = IdentifierFold.Exact, CompareText = "CREATE TABLE \"app\".\"Users\" ();" };
        var t2 = new DbObject { Type = DbObjectType.Table, Schema = "app", Name = "users", Fold = IdentifierFold.Exact, CompareText = "CREATE TABLE \"app\".\"users\" ();" };
        var session = new CompareSession
        {
            Source = new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, Objects = [a, b, t1, t2] },
            Result = new CompareResult
            {
                Source = new SnapshotInfo { Label = "dev", Engine = DatabaseEngine.PostgreSql },
                Rows = [.. new[] { a, b, t1, t2 }.Select(o => new CompareRow { Key = o.Key, Type = o.Type, Schema = o.Schema, Name = o.Name, Signature = o.Signature })],
            },
        };
        var result = new ExportService().WriteSqlFiles(session, new ExportRequest { Format = "sql", DifferencesOnly = false, OutputFolder = _root });
        Assert.True(result.Success, result.Message);
        var files = Directory.GetFiles(_root, "*.sql", SearchOption.AllDirectories);
        Assert.Equal(4, result.FileCount);
        Assert.Equal(4, files.Length);
        Assert.Contains(files, f => File.ReadAllText(f).Contains("p_currency", StringComparison.Ordinal));
        Assert.Contains(files, f => File.ReadAllText(f).Contains("\"Users\"", StringComparison.Ordinal));
        Assert.Contains(files, f => File.ReadAllText(f).Contains("\"users\"", StringComparison.Ordinal));
    }

    // ---------------- text helpers

    [Theory]
    [InlineData("(length(VALUE) <= 3)", "(length((\"c\")) <= 3)")]
    [InlineData("((VALUE)::text <> 'VALUE')", "(((\"c\"))::text <> 'VALUE')")]
    [InlineData("(VALUE > 0 AND \"VALUE\" IS NULL)", "((\"c\") > 0 AND \"VALUE\" IS NULL)")]
    public void A_domain_check_reads_the_column_where_it_reads_value(string expression, string expected) =>
        Assert.Equal(expected, PgScriptRun.OnValue(expression, "\"c\""));

    [Theory]
    [InlineData("UNIQUE (ref) DEFERRABLE INITIALLY DEFERRED", "UNIQUE (ref)")]
    [InlineData("PRIMARY KEY (id) DEFERRABLE", "PRIMARY KEY (id)")]
    [InlineData("UNIQUE (ref)", "UNIQUE (ref)")]
    public void A_key_s_definition_without_when_it_is_checked(string definition, string expected) =>
        Assert.Equal(expected, PgScriptRun.WithoutDeferrable(definition));
}
