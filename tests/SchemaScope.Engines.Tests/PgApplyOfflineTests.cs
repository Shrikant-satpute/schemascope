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
