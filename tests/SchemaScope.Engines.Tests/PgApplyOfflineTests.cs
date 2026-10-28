using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>A plan file written the way the PostgreSQL generator writes one (UTF-8, no BOM), with its hash.</summary>
internal static class PgPlanFiles
{
    public static WrittenFile Write(string root, string folder, string file, string text, string? obj = null)
    {
        var full = Path.Combine(root, folder, "objects", file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new WrittenFile(folder, "objects\\" + file, full, WrittenFileKind.Statements)
        {
            Object = obj is null ? null : new ObjectName("app", obj),
            Sha256 = ApplyPlans.Hash(full),
        };
    }

    /// <summary>The files as an apply plan, read back and split by the PostgreSQL executor's splitter.</summary>
    public static (List<PlanFile> Plan, List<VerifiedFile> Verified) Plan(string root, params (string Folder, string File, string Text)[] files)
    {
        var written = files.Select(f => Write(root, f.Folder, f.File, f.Text, Path.GetFileNameWithoutExtension(f.File))).ToList();
        var (plan, _) = ApplyPlans.Build(written, []);
        Assert.Null(ApplyService.Verify(plan, PgApplyExecutor.Instance.Splitter, out var verified));
        return (plan, verified);
    }
}

/// <summary>The PostgreSQL apply's own rules that need no server: the preflight, the error wording, the identity key.</summary>
public sealed class PgApplyOfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"schemascope-pg-apply-{Guid.NewGuid():n}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly TargetFacts Facts = new("1|system:1|5432|app", new HashSet<string>());

    private string? Preflight(params string[] texts) =>
        PgApplyExecutor.Instance.Preflight(
            PgPlanFiles.Plan(_root, [.. texts.Select((t, i) => ("03_missing", $"{i + 1:000}_f{i}.sql", t))]).Verified, Facts);

    [Fact]
    public void The_apply_splits_with_the_very_splitter_the_validator_used()
    {
        Assert.Same(new PgScriptValidator().Splitter, PgApplyExecutor.Instance.Splitter);
        Assert.Same(PgStatementSplitter.Instance, PgApplyExecutor.Instance.Splitter);
        Assert.Equal(TransactionSemantics.PerFileTransactional, PgApplyExecutor.Instance.Transactions);
    }

    [Fact]
    public void Plain_generated_files_pass_the_preflight()
    {
        Assert.Null(Preflight(
            "SET LOCAL check_function_bodies = false;\nCREATE FUNCTION \"app\".\"f\"() RETURNS integer LANGUAGE sql AS $$ SELECT 1; $$;\n",
            "-- VACUUM and CONCURRENTLY in a comment, and 'ALTER SYSTEM' in a string, are not statements\n" +
            "COMMENT ON TABLE \"app\".\"t\" IS 'VACUUM; ALTER SYSTEM; BEGIN; COMMIT';\n",
            "CREATE FUNCTION app.ids() RETURNS SETOF integer LANGUAGE sql\nBEGIN ATOMIC\n    SELECT 1;\nEND;\n",
            "ALTER TYPE \"app\".\"mood\" ADD VALUE 'great';\nCREATE TABLE \"app\".\"n\" (id integer);\n"));
    }

    [Theory]
    [InlineData("CREATE INDEX CONCURRENTLY ix ON app.t (id);", "CONCURRENTLY")]
    [InlineData("DROP INDEX CONCURRENTLY app.ix;", "CONCURRENTLY")]
    [InlineData("REINDEX INDEX CONCURRENTLY app.ix;", "CONCURRENTLY")]
    [InlineData("VACUUM app.t;", "VACUUM")]
    [InlineData("ALTER SYSTEM SET work_mem = '1GB';", "ALTER SYSTEM")]
    [InlineData("CREATE DATABASE other;", "database or tablespace")]
    [InlineData("DROP TABLESPACE fast;", "database or tablespace")]
    [InlineData("COMMIT;", "ends or splits the file's transaction")]
    [InlineData("begin;", "ends or splits the file's transaction")]
    [InlineData("ROLLBACK;", "ends or splits the file's transaction")]
    [InlineData("SAVEPOINT a;", "ends or splits the file's transaction")]
    [InlineData("PREPARE TRANSACTION 'x';", "ends or splits the file's transaction")]
    public void What_cannot_run_inside_the_file_s_transaction_is_refused_before_anything_runs(string statement, string reason)
    {
        var problem = Preflight("CREATE TABLE app.ok (id integer);\n", $"CREATE TABLE app.a (id integer);\n{statement}\n");
        Assert.NotNull(problem);
        Assert.Contains("1 file(s)", problem);
        Assert.Contains("03_missing\\objects\\002_f1.sql (line 2:", problem);
        Assert.Contains(reason, problem);
        Assert.DoesNotContain("001_f0.sql", problem);
        Assert.Contains("Nothing was applied", problem);
    }

    [Fact]
    public void A_new_enum_value_used_in_the_same_file_is_refused_and_one_used_later_is_not()
    {
        var problem = Preflight(
            "ALTER TYPE app.mood ADD VALUE IF NOT EXISTS 'it''s fine' AFTER 'ok';\nCREATE TABLE app.t (m app.mood DEFAULT 'it''s fine');\n");
        Assert.NotNull(problem);
        Assert.Contains("line 2: uses 'it''s fine', which ALTER TYPE ... ADD VALUE on line 1 adds", problem);

        Assert.Null(Preflight("ALTER TYPE app.mood ADD VALUE 'great';\n", "CREATE TABLE app.t (m app.mood DEFAULT 'great');\n"));
    }

    [Fact]
    public void An_error_names_the_statement_and_the_line_and_never_the_detail()
    {
        var statement = new SqlBatch("ALTER TABLE app.t\n    ADD CONSTRAINT u UNIQUE (v);", 7);
        var duplicate = new PostgresException("could not create unique index \"u\"", "ERROR", "ERROR", "23505",
            detail: "Key (v)=(secret-row-value) is duplicated.", hint: "a hint that echoes secret-row-value", position: 22,
            where: "SQL statement \"secret-row-value\"");

        var text = PgApplyExecutor.Describe(duplicate, statement, 1);
        Assert.Equal("ERROR 23505 at statement 2 (line 8): could not create unique index \"u\"", text);
        Assert.DoesNotContain("secret-row-value", text);
        Assert.Equal("ERROR 23505 (line 8): could not create unique index \"u\"", PgApplyExecutor.Instance.DescribeError(duplicate, statement));

        var noPosition = new PostgresException("relation \"app.t\" does not exist", "ERROR", "ERROR", "42P01");
        Assert.Equal("ERROR 42P01 at statement 1 (line 7): relation \"app.t\" does not exist", PgApplyExecutor.Describe(noPosition, statement, 0));
        Assert.Equal("ERROR 42P01: relation \"app.t\" does not exist", PgApplyExecutor.Describe(noPosition, null, null));

        var longer = new PostgresException(new string('x', 1000), "ERROR", "ERROR", "XX000");
        Assert.True(PgApplyExecutor.Describe(longer, statement, 0).Length < 500);
    }

    [Fact]
    public void The_target_is_known_by_its_cluster_then_its_address_and_never_by_a_guess()
    {
        Assert.Equal("1|system:7301|5432|app", PgApplyExecutor.KeyOf("7301", "10.0.0.5", 5432, "app"));
        Assert.Equal("1|address:10.0.0.5|5432|app", PgApplyExecutor.KeyOf(null, "10.0.0.5", 5432, "app"));
        // a Unix socket has neither an address nor a port; without the system identifier the target is unknown
        Assert.Null(PgApplyExecutor.KeyOf(null, null, null, "app"));
        Assert.Null(PgApplyExecutor.KeyOf(null, "10.0.0.5", null, "app"));
        Assert.Null(PgApplyExecutor.KeyOf(" ", null, 5432, "app"));
        // exact database names: App and app are two targets
        Assert.NotEqual(PgApplyExecutor.KeyOf("1", null, 5432, "App"), PgApplyExecutor.KeyOf("1", null, 5432, "app"));
    }

    [Fact]
    public void One_cluster_is_its_system_identifier_whatever_database_and_else_its_address_and_port()
    {
        static TargetFacts F(string key, params string[] hosts) => new(key, new HashSet<string>()) { Hosts = hosts };
        Assert.True(PgApplyExecutor.SameCluster(F("1|system:7|5432|prod", "172.17.0.2"), F("1|system:7|5432|other", "10.0.0.5")));
        Assert.False(PgApplyExecutor.SameCluster(F("1|system:7|5432|prod", "172.17.0.2"), F("1|system:8|5432|prod", "172.17.0.2")));
        // a login that may not read the identifier on either side: the reported address and port
        Assert.True(PgApplyExecutor.SameCluster(F("1|address:10.0.0.5|5432|a", "10.0.0.5"), F("1|system:7|5432|b", "10.0.0.5")));
        Assert.False(PgApplyExecutor.SameCluster(F("1|address:10.0.0.5|5432|a", "10.0.0.5"), F("1|address:10.0.0.5|5433|a", "10.0.0.5")));
        Assert.False(PgApplyExecutor.SameCluster(F("1|address:10.0.0.5|5432|a", "10.0.0.5"), F("1|address:10.0.0.6|5432|a", "10.0.0.6")));
    }

    // ---- the Protected re-check under a second spelling: resolved names and the protected server's own answer ----

    private const string Target = "Host=10.0.0.5;Port=5432;Database=app;Username=deploy;Password=pw";
    private static readonly TargetFacts TargetFacts = new("1|system:7301|5432|app", new HashSet<string>()) { Hosts = ["10.0.0.5"] };

    private static ConnectionSettings ProtectedPg(string server, string label = "Prod") =>
        new() { Engine = DatabaseEngine.PostgreSql, Label = label, Server = server, Port = 5432, Database = "prod", ProtectedServer = true };

    private static Task<IPAddress[]> Dns(string host, CancellationToken _) => host.ToLowerInvariant() switch
    {
        "db1.corp" or "prod-alias" or "prodsql.corp" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }),
        "test.corp" => Task.FromResult(new[] { IPAddress.Parse("10.0.0.9") }),
        _ => throw new System.Net.Sockets.SocketException(11001),
    };

    private static Task<string?> Check(IReadOnlyList<ConnectionSettings>? guarded, string target = Target, string? askedSystem = "9999") =>
        SchemaScopeApi.ProtectedElsewhereAsync(guarded, DatabaseEngine.PostgreSql, target, TargetFacts,
            (_, _) => Task.FromResult<TargetFacts?>(askedSystem is null ? null
                : new TargetFacts($"1|system:{askedSystem}|5432|prod", new HashSet<string>()) { Hosts = ["10.0.0.99"] }),
            PgApplyExecutor.SameCluster, Dns);

    [Fact]
    public async Task A_protected_name_reached_by_its_address_or_another_alias_is_refused()
    {
        // saved by name, the target written as that name's address, or as another name for it
        Assert.Contains("Protected", await Check([ProtectedPg("db1.corp")]));
        Assert.Contains("Protected", await Check([ProtectedPg("prod-alias")], "Host=db1.corp;Port=5432;Database=app"));
        // a protected SQL Server on that machine protects PostgreSQL on it, prefix, instance and port aside
        Assert.Contains("Protected", await Check([new ConnectionSettings { Server = "tcp:prodsql.corp\\A,1433", Database = "x", ProtectedServer = true }]));
        // the address the target reports counts as well as the one it was reached by
        Assert.Contains("Protected", await Check([ProtectedPg("10.0.0.5")], "Host=test.corp;Port=5432;Database=app"));
        // localhost saved, this machine's own address or name used
        var local = Local();
        if (local is not null)
            Assert.Contains("Protected", await SchemaScopeApi.ProtectedElsewhereAsync([ProtectedPg("127.0.0.1")], DatabaseEngine.PostgreSql,
                $"Host={local};Port=5432;Database=app", TargetFacts, (_, _) => Task.FromResult<TargetFacts?>(null), PgApplyExecutor.SameCluster, Dns));
        Assert.Contains("Protected", await SchemaScopeApi.ProtectedElsewhereAsync([ProtectedPg("localhost")], DatabaseEngine.PostgreSql,
            $"Host={Environment.MachineName};Port=5432;Database=app", TargetFacts, (_, _) => Task.FromResult<TargetFacts?>(null), PgApplyExecutor.SameCluster, Dns));
    }

    [Fact]
    public async Task A_protected_server_reached_through_nat_is_known_by_its_own_answer()
    {
        // no address in common (port forwarding, NAT), but the protected connection reports the target's cluster
        var guarded = new[] { ProtectedPg("test.corp") };
        Assert.Contains("Protected", await Check(guarded, askedSystem: "7301"));
        Assert.Null(await Check(guarded, askedSystem: "9999"));
        // a protected connection to another engine is not asked: its host alone decides
        Assert.Null(await Check([new ConnectionSettings { Server = "test.corp", Database = "x", ProtectedServer = true }], askedSystem: null));
    }

    [Fact]
    public async Task What_cannot_be_decided_is_refused()
    {
        Assert.Contains("Protected", await Check(null));                                       // the store cannot be read
        Assert.Contains("could not be resolved", await Check([ProtectedPg("gone.example.invalid")]));
        Assert.Contains("could not be asked", await Check([ProtectedPg("test.corp")], askedSystem: null));
        Assert.Contains("Protected", await Check([ProtectedPg("test.corp")], "Host=gone.example.invalid;Port=5432;Database=app"));
        Assert.Contains("Protected", await Check([new ConnectionSettings
            { Engine = DatabaseEngine.PostgreSql, UseRawConnectionString = true, RawConnectionString = "Host=prod:1;Port=2", ProtectedServer = true }]));
        Assert.Contains("Protected", await SchemaScopeApi.ProtectedElsewhereAsync([ProtectedPg("test.corp")], DatabaseEngine.PostgreSql, Target,
            TargetFacts, (_, _) => throw new InvalidOperationException("boom"), PgApplyExecutor.SameCluster, Dns));
        // nothing protected: nothing to resolve or ask
        Assert.Null(await Check([]));
    }

    /// <summary>One of this machine's own IPv4 addresses other than loopback, or null.</summary>
    internal static string? Local() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                                 !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))?.ToString();

    [Fact]
    public void The_typed_name_is_exact_on_postgres_and_as_before_on_sql_server()
    {
        Assert.Equal(StringComparison.Ordinal, ConnectionRules.TypedNameComparison(DatabaseEngine.PostgreSql));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, ConnectionRules.TypedNameComparison(DatabaseEngine.SqlServer));
    }

    private static SchemaSource Pg(string cs) => new() { Engine = DatabaseEngine.PostgreSql, ConnectionString = cs };

    [Theory]
    [InlineData("Host=db;Port=5432;Database=app", "Host=db;Database=app", true)]                 // the default port is 5432
    [InlineData("Host=localhost;Port=6000;Database=app", "Host=127.0.0.1;Port=6000;Database=app", true)]
    [InlineData("Host=a,db;Port=6000;Database=app", "Host=db;Port=6000;Database=app", true)]      // a host in common
    [InlineData("Host=db;Port=6000;Database=app", "Host=db;Port=6001;Database=app", false)]       // another port, another server
    [InlineData("Host=db;Port=6000;Database=App", "Host=db;Port=6000;Database=app", false)]       // another database
    [InlineData("Host=db;Port=6000;Database=app", "Host=other;Port=6000;Database=app", false)]
    [InlineData("Host=db:1;Port=2;Database=app", "Host=db;Port=6000;Database=app", true)]         // unreadable: fail closed
    public void The_source_is_one_engine_port_host_and_exact_database(string source, string target, bool same)
    {
        var s = Pg(source);
        var t = Pg(target);
        var sdb = Core.Engines.ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, source)?.Database ?? "";
        var tdb = Core.Engines.ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, target)?.Database ?? "";
        Assert.Equal(same, SchemaScopeApi.SameDatabase(s, t, sdb, tdb));
        // and never across engines
        Assert.False(SchemaScopeApi.SameDatabase(s, new SchemaSource { Engine = DatabaseEngine.SqlServer, ConnectionString = "Server=db;Database=app" }, sdb, "app"));
    }

    [Fact]
    public async Task The_owner_app_applies_to_postgres_with_its_executor_and_the_viewer_has_none()
    {
        await using var o = await OwnerApp.StartAsync();
        var applier = o.App.Services.GetRequiredService<ApplyService>();
        Assert.Same(PgApplyExecutor.Instance, applier.ExecutorFor(DatabaseEngine.PostgreSql));
        Assert.Same(SqlServerApplyExecutor.Instance, applier.ExecutorFor(DatabaseEngine.SqlServer));
        Assert.Null(applier.ExecutorFor(DatabaseEngine.MySql));

        // an ApplyService built the old way still runs SQL Server only
        var legacy = new ApplyService(o.Temp.Store, new WriteMode(), NullLogger<ApplyService>.Instance);
        Assert.Null(legacy.ExecutorFor(DatabaseEngine.PostgreSql));
    }
}
