using Microsoft.Data.Sqlite;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;

namespace SchemaScope.Tests;

/// <summary>
/// Store v2 (MULTI-DATABASE-PLAN §8): PRAGMA user_version, an empty engine_connections table, apply_runs.engine and
/// apply_batches.statement_index - with SQL Server rows left exactly where and as they were.
/// </summary>
public sealed class StoreMigrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"schemascope-migrate-{Guid.NewGuid():n}");
    private string DbPath => Path.Combine(_folder, "schemascope.db");

    public StoreMigrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private SqliteConnection Raw()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }

    private void Exec(string sql)
    {
        using var conn = Raw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var conn = Raw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private List<string> Columns(string table)
    {
        using var conn = Raw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Every column of every row, as text, for a byte-for-byte comparison.</summary>
    private string Dump(string table)
    {
        using var conn = Raw();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} ORDER BY 1";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i =>
                r.IsDBNull(i) ? "NULL" : r.GetValue(i) is byte[] b ? Convert.ToHexString(b) : Convert.ToString(r.GetValue(i)))));
        return string.Join("\n", rows);
    }

    /// <summary>The tables exactly as release builds before v2 created them.</summary>
    private const string V1 = """
        CREATE TABLE connections (
            id TEXT PRIMARY KEY, label TEXT NOT NULL DEFAULT '', server TEXT NOT NULL DEFAULT '',
            database_name TEXT NOT NULL DEFAULT '', auth INTEGER NOT NULL DEFAULT 0, username TEXT NULL,
            password_enc BLOB NULL, encrypt INTEGER NOT NULL DEFAULT 0, trust_cert INTEGER NOT NULL DEFAULT 1,
            connect_timeout INTEGER NOT NULL DEFAULT 15, app_intent_ro INTEGER NOT NULL DEFAULT 0,
            use_raw INTEGER NOT NULL DEFAULT 0, raw_cs_enc BLOB NULL, protected_server INTEGER NOT NULL DEFAULT 0,
            colour TEXT NOT NULL DEFAULT '', last_used TEXT NULL, sort_order INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE apply_runs (
            id TEXT PRIMARY KEY, started TEXT NOT NULL, finished TEXT NULL, compare_run TEXT NOT NULL,
            target_label TEXT NOT NULL, server TEXT NOT NULL, database_name TEXT NOT NULL, login TEXT NOT NULL,
            sections_json TEXT NOT NULL, success INTEGER NOT NULL DEFAULT 0, message TEXT NOT NULL DEFAULT '',
            stopped_at TEXT NULL);
        CREATE TABLE apply_batches (
            run_id TEXT NOT NULL, seq INTEGER NOT NULL, folder TEXT NOT NULL, file TEXT NOT NULL,
            object_name TEXT NOT NULL, ok INTEGER NOT NULL, error TEXT NULL, elapsed_ms INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (run_id, seq));
        INSERT INTO connections (id, label, server, database_name, auth, username, encrypt, trust_cert, colour, sort_order)
            VALUES ('c1', 'Prod', 'prod-sql', 'Sales', 1, 'reporting', 1, 0, '#123456', 3);
        INSERT INTO apply_runs (id, started, compare_run, target_label, server, database_name, login, sections_json, success, message)
            VALUES ('r1', '2026-10-01T09:00:00.0000000Z', 'run', 'Client', 'prod-sql', 'Sales', 'SQL login: deploy', '["01_columns"]', 1, 'ok');
        INSERT INTO apply_batches (run_id, seq, folder, file, object_name, ok, elapsed_ms)
            VALUES ('r1', 0, '01_columns', 'objects\001.sql', 'dbo.A', 1, 12);
        """;

    [Fact]
    public void A_new_store_is_version_2_with_an_empty_engine_table()
    {
        var store = new SchemaScopeStore(DbPath);

        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM engine_connections"));
        Assert.Contains("engine", Columns("apply_runs"));
        Assert.Contains("statement_index", Columns("apply_batches"));
        Assert.DoesNotContain("engine", Columns("connections"));   // SQL Server rows keep the table old builds read
        Assert.Empty(store.GetConnections());
    }

    [Fact]
    public void A_v1_store_is_migrated_and_its_rows_are_untouched()
    {
        Exec(V1);
        var before = Dump("connections");

        var store = new SchemaScopeStore(DbPath);

        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Equal(before, Dump("connections"));
        var c = Assert.Single(store.GetConnections());
        Assert.Equal(DatabaseEngine.SqlServer, c.Engine);
        Assert.Equal("prod-sql", c.Server);
        Assert.Null(c.Port);

        var run = Assert.Single(store.GetApplyRuns());
        Assert.Equal(DatabaseEngine.SqlServer, run.Engine);
        Assert.Null(Assert.Single(run.Files).StatementIndex);
    }

    [Fact]
    public void A_half_migrated_store_is_finished_and_opening_twice_changes_nothing()
    {
        Exec(V1 + "ALTER TABLE apply_runs ADD COLUMN engine INTEGER NOT NULL DEFAULT 0;");

        _ = new SchemaScopeStore(DbPath);
        var once = (Dump("connections"), Dump("apply_runs"), Dump("apply_batches"));
        _ = new SchemaScopeStore(DbPath);

        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        Assert.Contains("statement_index", Columns("apply_batches"));
        Assert.Equal(once, (Dump("connections"), Dump("apply_runs"), Dump("apply_batches")));
    }

    [Fact]
    public void A_store_from_a_newer_build_is_left_alone()
    {
        Exec(V1 + "PRAGMA user_version = 7;");
        _ = new SchemaScopeStore(DbPath);
        Assert.Equal(7L, Scalar("PRAGMA user_version"));
    }

    [Fact]
    public void Other_engines_go_to_their_own_table_with_one_id_space_and_one_order()
    {
        var store = new SchemaScopeStore(DbPath);
        var sql = store.SaveConnection(new ConnectionSettings { Label = "B sql", Server = "prod-sql" });
        var pg = store.SaveConnection(new ConnectionSettings
        {
            Label = "A pg", Engine = DatabaseEngine.PostgreSql, Server = "pg", Port = 6543, Ssl = SslMode.VerifyFull,
            EngineOptions = new() { ["search_path"] = "app" }
        });

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM connections"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM engine_connections"));

        var read = store.GetConnection(pg.Id)!;
        Assert.Equal(DatabaseEngine.PostgreSql, read.Engine);
        Assert.Equal(6543, read.Port);
        Assert.Equal(SslMode.VerifyFull, read.Ssl);
        Assert.Equal("app", read.EngineOptions!["search_path"]);

        // one order across both tables
        store.ReorderConnections([sql.Id, pg.Id]);
        Assert.Equal([sql.Id, pg.Id], store.GetConnections().Select(c => c.Id));
        store.ReorderConnections([pg.Id, sql.Id]);
        Assert.Equal([pg.Id, sql.Id], store.GetConnections().Select(c => c.Id));
        Assert.Equal([sql.Id], store.GetConnections(DatabaseEngine.SqlServer).Select(c => c.Id));

        // an engine change moves the row, keeps its place, and never duplicates the id
        read.Engine = DatabaseEngine.SqlServer;
        read.Port = null;
        read.Ssl = null;
        read.EngineOptions = null;
        store.SaveConnection(read);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM engine_connections"));
        Assert.Equal([pg.Id, sql.Id], store.GetConnections().Select(c => c.Id));

        store.DeleteConnection(pg.Id);
        Assert.Equal([sql.Id], store.GetConnections().Select(c => c.Id));
    }

    [Fact]
    public void The_audit_keeps_the_engine_and_the_statement_index()
    {
        var store = new SchemaScopeStore(DbPath);
        var run = new ApplyRunRecord { CompareRunId = "r", TargetLabel = "T", Engine = DatabaseEngine.PostgreSql, Message = "Running" };
        store.StartApplyRun(run);
        store.AddApplyFile(run.Id, 0, new ApplyFileRecord { Folder = "01", File = "a.sql", Ok = true, StatementIndex = 3 });
        store.FinishApplyRun(run);

        var read = Assert.Single(store.GetApplyRuns());
        Assert.Equal(DatabaseEngine.PostgreSql, read.Engine);
        Assert.Equal(3, Assert.Single(read.Files).StatementIndex);
    }
}
