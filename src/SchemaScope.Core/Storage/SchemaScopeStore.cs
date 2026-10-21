using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Core.Storage;

public sealed class CompareProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public CompareOptions Options { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Everything SchemaScope remembers between sessions, in one local SQLite file
/// under %LOCALAPPDATA%\SchemaScope. Nothing is ever sent anywhere.
/// </summary>
public sealed class SchemaScopeStore
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SchemaScopeStore(string? path = null)
    {
        DatabasePath = path ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString;

        Initialise();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchemaScope",
        "schemascope.db");

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialise()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS connections (
                id                TEXT PRIMARY KEY,
                label             TEXT NOT NULL DEFAULT '',
                server            TEXT NOT NULL DEFAULT '',
                database_name     TEXT NOT NULL DEFAULT '',
                auth              INTEGER NOT NULL DEFAULT 0,
                username          TEXT NULL,
                password_enc      BLOB NULL,
                encrypt           INTEGER NOT NULL DEFAULT 0,
                trust_cert        INTEGER NOT NULL DEFAULT 1,
                connect_timeout   INTEGER NOT NULL DEFAULT 15,
                app_intent_ro     INTEGER NOT NULL DEFAULT 0,
                use_raw           INTEGER NOT NULL DEFAULT 0,
                raw_cs_enc        BLOB NULL,
                protected_server  INTEGER NOT NULL DEFAULT 0,
                colour            TEXT NOT NULL DEFAULT '',
                last_used         TEXT NULL,
                sort_order        INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS profiles (
                id           TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                options_json TEXT NOT NULL,
                created      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS compare_history (
                id            TEXT PRIMARY KEY,
                signature     TEXT NOT NULL UNIQUE,
                first_run     TEXT NOT NULL,
                last_run      TEXT NOT NULL,
                run_count     INTEGER NOT NULL DEFAULT 1,
                source_json   TEXT NOT NULL,
                targets_json  TEXT NOT NULL,
                options_json  TEXT NOT NULL,
                object_count  INTEGER NOT NULL DEFAULT 0,
                diff_count    INTEGER NOT NULL DEFAULT 0,
                match_percent REAL NOT NULL DEFAULT 0,
                total_ms      INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_compare_history_last_run
                ON compare_history (last_run DESC);

            CREATE TABLE IF NOT EXISTS shares (
                token         TEXT PRIMARY KEY,
                source_label  TEXT NOT NULL,
                source_detail TEXT NOT NULL,
                targets_json  TEXT NOT NULL,
                compared      TEXT NOT NULL,
                created       TEXT NOT NULL,
                expires       TEXT NOT NULL,
                object_count  INTEGER NOT NULL DEFAULT 0,
                total_views   INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS apply_runs (
                id            TEXT PRIMARY KEY,
                started       TEXT NOT NULL,
                finished      TEXT NULL,
                compare_run   TEXT NOT NULL,
                target_label  TEXT NOT NULL,
                server        TEXT NOT NULL,
                database_name TEXT NOT NULL,
                login         TEXT NOT NULL,
                sections_json TEXT NOT NULL,
                success       INTEGER NOT NULL DEFAULT 0,
                message       TEXT NOT NULL DEFAULT '',
                stopped_at    TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_apply_runs_started
                ON apply_runs (started DESC);

            CREATE TABLE IF NOT EXISTS apply_batches (
                run_id      TEXT NOT NULL,
                seq         INTEGER NOT NULL,
                folder      TEXT NOT NULL,
                file        TEXT NOT NULL,
                object_name TEXT NOT NULL,
                ok          INTEGER NOT NULL,
                error       TEXT NULL,
                elapsed_ms  INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (run_id, seq)
            );
            """;
        cmd.ExecuteNonQuery();

        Migrate(conn);
    }

    /// <summary>The schema version this build writes. v1 is the schema above, as every earlier build left it.</summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Brings an older store up to <see cref="SchemaVersion"/>, recorded in PRAGMA user_version. Every step checks
    /// what is already there first, so a store a crash left half-migrated is finished, never broken. A store
    /// written by a newer build is left as it is.
    /// </summary>
    private static void Migrate(SqliteConnection conn)
    {
        if (UserVersion(conn) >= SchemaVersion) return;

        using var tx = conn.BeginTransaction();

        // v2: connections to engines other than SQL Server live in their own table, which shipped builds
        // never read - so an older SchemaScope or QueryScope can never send their credentials over SqlClient.
        // SQL Server rows stay exactly as they are in connections.
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS engine_connections (
                id                  TEXT PRIMARY KEY,
                label               TEXT NOT NULL DEFAULT '',
                server              TEXT NOT NULL DEFAULT '',
                database_name       TEXT NOT NULL DEFAULT '',
                auth                INTEGER NOT NULL DEFAULT 0,
                username            TEXT NULL,
                password_enc        BLOB NULL,
                encrypt             INTEGER NOT NULL DEFAULT 0,
                trust_cert          INTEGER NOT NULL DEFAULT 1,
                connect_timeout     INTEGER NOT NULL DEFAULT 15,
                app_intent_ro       INTEGER NOT NULL DEFAULT 0,
                use_raw             INTEGER NOT NULL DEFAULT 0,
                raw_cs_enc          BLOB NULL,
                protected_server    INTEGER NOT NULL DEFAULT 0,
                colour              TEXT NOT NULL DEFAULT '',
                last_used           TEXT NULL,
                sort_order          INTEGER NOT NULL DEFAULT 0,
                engine              INTEGER NOT NULL,
                port                INTEGER NULL,
                ssl_mode            INTEGER NULL,
                engine_options_json TEXT NULL
            );
            """);
        if (!HasColumn(conn, tx, "apply_runs", "engine"))
            Exec(conn, tx, "ALTER TABLE apply_runs ADD COLUMN engine INTEGER NOT NULL DEFAULT 0;");
        if (!HasColumn(conn, tx, "apply_batches", "statement_index"))
            Exec(conn, tx, "ALTER TABLE apply_batches ADD COLUMN statement_index INTEGER NULL;");

        Exec(conn, tx, $"PRAGMA user_version = {SchemaVersion};");
        tx.Commit();
    }

    /// <summary>PRAGMA user_version: 0 for a store no build has versioned yet.</summary>
    public static int UserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool HasColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---------------- connections ----------------

    /// <summary>
    /// Both connection tables as one: SQL Server rows from connections (engine 0, no port or SSL mode), every other
    /// engine from engine_connections. One id space and one sort order across the two.
    /// </summary>
    private const string AllConnections = """
        SELECT id, label, server, database_name, auth, username, password_enc, encrypt, trust_cert, connect_timeout,
               app_intent_ro, use_raw, raw_cs_enc, protected_server, colour, last_used, sort_order,
               0 AS engine, NULL AS port, NULL AS ssl_mode, NULL AS engine_options_json
        FROM connections
        UNION ALL
        SELECT id, label, server, database_name, auth, username, password_enc, encrypt, trust_cert, connect_timeout,
               app_intent_ro, use_raw, raw_cs_enc, protected_server, colour, last_used, sort_order,
               engine, port, ssl_mode, engine_options_json
        FROM engine_connections
        """;

    public List<ConnectionSettings> GetConnections()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM ({AllConnections}) ORDER BY sort_order, label, server";
        using var r = cmd.ExecuteReader();

        var list = new List<ConnectionSettings>();
        while (r.Read()) list.Add(MapConnection(r));
        return list;
    }

    /// <summary>One engine's connections, e.g. SQL Server only for QueryScope.</summary>
    public List<ConnectionSettings> GetConnections(DatabaseEngine engine) =>
        [.. GetConnections().Where(c => c.Engine == engine)];

    public ConnectionSettings? GetConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM ({AllConnections}) WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapConnection(r) : null;
    }

    public ConnectionSettings SaveConnection(ConnectionSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString("n");

        // A blank password on an existing row means "leave what is saved alone",
        // so the UI never has to round-trip the real secret. That only holds while
        // the row still points where the secret was saved for: re-pointing a saved
        // connection at another server or login drops the password rather than
        // carrying it along to whoever answers there.
        // The same rule covers a masked raw connection string, and a change of engine or port: a secret
        // saved for SQL Server on a host never follows the row to PostgreSQL on that host.
        byte[]? passwordEnc;
        byte[]? rawEnc;
        var existing = GetConnectionRawSecrets(s.Id);
        var saved = GetConnection(s.Id);
        var sameDestination = saved is not null && ConnectionSettings.SameDestination(s, saved);

        if (string.IsNullOrEmpty(s.Password) || s.Password == "********")
            passwordEnc = sameDestination ? existing.Password : null;
        else
            passwordEnc = SecretProtector.Protect(s.Password);

        if (s.UseRawConnectionString)
        {
            rawEnc = string.IsNullOrEmpty(s.RawConnectionString) || s.RawConnectionString.Contains("********")
                ? sameDestination ? existing.Raw : null
                : SecretProtector.Protect(s.RawConnectionString);
        }
        else
        {
            rawEnc = null;
        }

        if (s.Engine != DatabaseEngine.SqlServer)
            return SaveEngineConnection(s, saved, passwordEnc, rawEnc);

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        // moving from engine_connections (its engine was changed to SQL Server): one row, one id, same place in the list
        var sortOrder = saved?.Engine is { } was && was != DatabaseEngine.SqlServer ? TakeSortOrder(conn, tx, "engine_connections", s.Id) : 0;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO connections
                (id, label, server, database_name, auth, username, password_enc, encrypt,
                 trust_cert, connect_timeout, app_intent_ro, use_raw, raw_cs_enc,
                 protected_server, colour, last_used, sort_order)
            VALUES
                ($id, $label, $server, $db, $auth, $user, $pwd, $encrypt,
                 $trust, $timeout, $ro, $useRaw, $raw,
                 $protected, $colour, $lastUsed,
                 COALESCE((SELECT sort_order FROM connections WHERE id = $id), $sort))
            ON CONFLICT(id) DO UPDATE SET
                label = excluded.label,
                server = excluded.server,
                database_name = excluded.database_name,
                auth = excluded.auth,
                username = excluded.username,
                password_enc = excluded.password_enc,
                encrypt = excluded.encrypt,
                trust_cert = excluded.trust_cert,
                connect_timeout = excluded.connect_timeout,
                app_intent_ro = excluded.app_intent_ro,
                use_raw = excluded.use_raw,
                raw_cs_enc = excluded.raw_cs_enc,
                protected_server = excluded.protected_server,
                colour = excluded.colour,
                last_used = excluded.last_used;
            """;

        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$label", s.Label ?? "");
        cmd.Parameters.AddWithValue("$server", s.Server ?? "");
        cmd.Parameters.AddWithValue("$db", s.Database ?? "");
        cmd.Parameters.AddWithValue("$auth", (int)s.Auth);
        cmd.Parameters.AddWithValue("$user", (object?)s.UserName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pwd", (object?)passwordEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$encrypt", s.Encrypt ? 1 : 0);
        cmd.Parameters.AddWithValue("$trust", s.TrustServerCertificate ? 1 : 0);
        cmd.Parameters.AddWithValue("$timeout", s.ConnectTimeoutSeconds);
        cmd.Parameters.AddWithValue("$ro", s.ApplicationIntentReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$useRaw", s.UseRawConnectionString ? 1 : 0);
        cmd.Parameters.AddWithValue("$raw", (object?)rawEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$protected", s.ProtectedServer ? 1 : 0);
        cmd.Parameters.AddWithValue("$colour", s.Colour ?? "");
        cmd.Parameters.AddWithValue("$lastUsed", (object?)s.LastUsedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", sortOrder);

        cmd.ExecuteNonQuery();
        tx.Commit();
        return GetConnection(s.Id)!;
    }

    /// <summary>A connection to any engine but SQL Server: engine_connections, never the table shipped builds read.</summary>
    private ConnectionSettings SaveEngineConnection(ConnectionSettings s, ConnectionSettings? saved, byte[]? passwordEnc, byte[]? rawEnc)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var sortOrder = saved?.Engine == DatabaseEngine.SqlServer ? TakeSortOrder(conn, tx, "connections", s.Id) : 0;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO engine_connections
                (id, label, server, database_name, auth, username, password_enc, encrypt,
                 trust_cert, connect_timeout, app_intent_ro, use_raw, raw_cs_enc,
                 protected_server, colour, last_used, sort_order,
                 engine, port, ssl_mode, engine_options_json)
            VALUES
                ($id, $label, $server, $db, $auth, $user, $pwd, $encrypt,
                 $trust, $timeout, $ro, $useRaw, $raw,
                 $protected, $colour, $lastUsed,
                 COALESCE((SELECT sort_order FROM engine_connections WHERE id = $id), $sort),
                 $engine, $port, $ssl, $options)
            ON CONFLICT(id) DO UPDATE SET
                label = excluded.label,
                server = excluded.server,
                database_name = excluded.database_name,
                auth = excluded.auth,
                username = excluded.username,
                password_enc = excluded.password_enc,
                encrypt = excluded.encrypt,
                trust_cert = excluded.trust_cert,
                connect_timeout = excluded.connect_timeout,
                app_intent_ro = excluded.app_intent_ro,
                use_raw = excluded.use_raw,
                raw_cs_enc = excluded.raw_cs_enc,
                protected_server = excluded.protected_server,
                colour = excluded.colour,
                last_used = excluded.last_used,
                engine = excluded.engine,
                port = excluded.port,
                ssl_mode = excluded.ssl_mode,
                engine_options_json = excluded.engine_options_json;
            """;

        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$label", s.Label ?? "");
        cmd.Parameters.AddWithValue("$server", s.Server ?? "");
        cmd.Parameters.AddWithValue("$db", s.Database ?? "");
        cmd.Parameters.AddWithValue("$auth", (int)s.Auth);
        cmd.Parameters.AddWithValue("$user", (object?)s.UserName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pwd", (object?)passwordEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$encrypt", s.Encrypt ? 1 : 0);
        cmd.Parameters.AddWithValue("$trust", s.TrustServerCertificate ? 1 : 0);
        cmd.Parameters.AddWithValue("$timeout", s.ConnectTimeoutSeconds);
        cmd.Parameters.AddWithValue("$ro", s.ApplicationIntentReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$useRaw", s.UseRawConnectionString ? 1 : 0);
        cmd.Parameters.AddWithValue("$raw", (object?)rawEnc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$protected", s.ProtectedServer ? 1 : 0);
        cmd.Parameters.AddWithValue("$colour", s.Colour ?? "");
        cmd.Parameters.AddWithValue("$lastUsed", (object?)s.LastUsedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", sortOrder);
        cmd.Parameters.AddWithValue("$engine", (int)s.Engine);
        cmd.Parameters.AddWithValue("$port", (object?)s.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ssl", s.Ssl is { } ssl ? (int)ssl : DBNull.Value);
        cmd.Parameters.AddWithValue("$options",
            s.EngineOptions is { Count: > 0 } options ? JsonSerializer.Serialize(options) : DBNull.Value);

        cmd.ExecuteNonQuery();
        tx.Commit();
        return GetConnection(s.Id)!;
    }

    /// <summary>Deletes the row from <paramref name="table"/> and returns its sort order, for a row changing tables.</summary>
    private static int TakeSortOrder(SqliteConnection conn, SqliteTransaction tx, string table, string id)
    {
        using var find = conn.CreateCommand();
        find.Transaction = tx;
        find.CommandText = $"SELECT sort_order FROM {table} WHERE id = $id";
        find.Parameters.AddWithValue("$id", id);
        var order = find.ExecuteScalar() is { } o and not DBNull ? Convert.ToInt32(o) : 0;

        using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = $"DELETE FROM {table} WHERE id = $id";
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
        return order;
    }

    public void DeleteConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM connections WHERE id = $id; DELETE FROM engine_connections WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TouchConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE connections SET last_used = $now WHERE id = $id;
            UPDATE engine_connections SET last_used = $now WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>One order across both connection tables.</summary>
    public void ReorderConnections(IReadOnlyList<string> orderedIds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE connections SET sort_order = $o WHERE id = $id;
                UPDATE engine_connections SET sort_order = $o WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private (byte[]? Password, byte[]? Raw) GetConnectionRawSecrets(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT password_enc, raw_cs_enc FROM ({AllConnections}) WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (
            r.IsDBNull(0) ? null : (byte[])r["password_enc"],
            r.IsDBNull(1) ? null : (byte[])r["raw_cs_enc"]);
    }

    private static ConnectionSettings MapConnection(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Label = r.GetString(r.GetOrdinal("label")),
        Server = r.GetString(r.GetOrdinal("server")),
        Database = r.GetString(r.GetOrdinal("database_name")),
        Auth = (AuthMode)r.GetInt32(r.GetOrdinal("auth")),
        UserName = r.IsDBNull(r.GetOrdinal("username")) ? null : r.GetString(r.GetOrdinal("username")),
        Password = SecretProtector.Unprotect(
            r.IsDBNull(r.GetOrdinal("password_enc")) ? null : (byte[])r["password_enc"]),
        Encrypt = r.GetInt32(r.GetOrdinal("encrypt")) == 1,
        TrustServerCertificate = r.GetInt32(r.GetOrdinal("trust_cert")) == 1,
        ConnectTimeoutSeconds = r.GetInt32(r.GetOrdinal("connect_timeout")),
        ApplicationIntentReadOnly = r.GetInt32(r.GetOrdinal("app_intent_ro")) == 1,
        UseRawConnectionString = r.GetInt32(r.GetOrdinal("use_raw")) == 1,
        RawConnectionString = SecretProtector.Unprotect(
            r.IsDBNull(r.GetOrdinal("raw_cs_enc")) ? null : (byte[])r["raw_cs_enc"]),
        ProtectedServer = r.GetInt32(r.GetOrdinal("protected_server")) == 1,
        Engine = (DatabaseEngine)r.GetInt32(r.GetOrdinal("engine")),
        Port = r.IsDBNull(r.GetOrdinal("port")) ? null : r.GetInt32(r.GetOrdinal("port")),
        Ssl = r.IsDBNull(r.GetOrdinal("ssl_mode")) ? null : (SslMode)r.GetInt32(r.GetOrdinal("ssl_mode")),
        EngineOptions = r.IsDBNull(r.GetOrdinal("engine_options_json"))
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(r.GetOrdinal("engine_options_json"))),
        Colour = r.GetString(r.GetOrdinal("colour")),
        LastUsedUtc = r.IsDBNull(r.GetOrdinal("last_used"))
            ? null
            : DateTime.Parse(r.GetString(r.GetOrdinal("last_used"))).ToUniversalTime()
    };

    // ---------------- profiles ----------------

    public List<CompareProfile> GetProfiles()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, options_json, created FROM profiles ORDER BY name";
        using var r = cmd.ExecuteReader();

        var list = new List<CompareProfile>();
        while (r.Read())
        {
            list.Add(new CompareProfile
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Options = JsonSerializer.Deserialize<CompareOptions>(r.GetString(2)) ?? new CompareOptions(),
                CreatedUtc = DateTime.Parse(r.GetString(3)).ToUniversalTime()
            });
        }
        return list;
    }

    public CompareProfile SaveProfile(CompareProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("n");

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (id, name, options_json, created)
            VALUES ($id, $name, $json, $created)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, options_json = excluded.options_json;
            """;
        cmd.Parameters.AddWithValue("$id", p.Id);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p.Options));
        cmd.Parameters.AddWithValue("$created", p.CreatedUtc.ToString("O"));
        cmd.ExecuteNonQuery();
        return p;
    }

    public void DeleteProfile(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------------- compare history ----------------

    /// <summary>Older runs fall off the end. This is a shortcut, not an audit log.</summary>
    private const int MaxHistoryEntries = 50;

    public List<CompareHistoryEntry> GetHistory()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, signature, first_run, last_run, run_count, source_json, targets_json,
                   options_json, object_count, diff_count, match_percent, total_ms
            FROM compare_history
            ORDER BY last_run DESC;
            """;
        using var r = cmd.ExecuteReader();

        var list = new List<CompareHistoryEntry>();
        while (r.Read())
        {
            list.Add(new CompareHistoryEntry
            {
                Id = r.GetString(0),
                Signature = r.GetString(1),
                FirstRunUtc = ReadUtc(r.GetString(2)),
                LastRunUtc = ReadUtc(r.GetString(3)),
                RunCount = r.GetInt32(4),
                Source = JsonSerializer.Deserialize<CompareHistoryDatabase>(r.GetString(5)) ?? new(),
                Targets = JsonSerializer.Deserialize<List<CompareHistoryDatabase>>(r.GetString(6)) ?? [],
                Options = JsonSerializer.Deserialize<CompareOptions>(r.GetString(7)) ?? new(),
                ObjectCount = r.GetInt32(8),
                DifferenceCount = r.GetInt32(9),
                LowestMatchPercent = r.GetDouble(10),
                TotalMs = r.GetInt64(11)
            });
        }
        return list;
    }

    /// <summary>
    /// Records a finished compare. Running the same pairing again keeps the one
    /// row - the counter goes up and the numbers are replaced - so the list stays
    /// a list of comparisons rather than a list of clicks.
    /// </summary>
    public void SaveHistory(CompareHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Signature))
            entry.Signature = CompareHistoryEntry.BuildSignature(entry.Source, entry.Targets);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO compare_history
                (id, signature, first_run, last_run, run_count, source_json, targets_json,
                 options_json, object_count, diff_count, match_percent, total_ms)
            VALUES
                ($id, $sig, $first, $last, 1, $source, $targets,
                 $options, $objects, $diffs, $percent, $ms)
            ON CONFLICT(signature) DO UPDATE SET
                last_run = excluded.last_run,
                run_count = compare_history.run_count + 1,
                source_json = excluded.source_json,
                targets_json = excluded.targets_json,
                options_json = excluded.options_json,
                object_count = excluded.object_count,
                diff_count = excluded.diff_count,
                match_percent = excluded.match_percent,
                total_ms = excluded.total_ms;
            """;

        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$sig", entry.Signature);
        cmd.Parameters.AddWithValue("$first", entry.FirstRunUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$last", entry.LastRunUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$source", JsonSerializer.Serialize(entry.Source));
        cmd.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(entry.Targets));
        cmd.Parameters.AddWithValue("$options", JsonSerializer.Serialize(entry.Options));
        cmd.Parameters.AddWithValue("$objects", entry.ObjectCount);
        cmd.Parameters.AddWithValue("$diffs", entry.DifferenceCount);
        cmd.Parameters.AddWithValue("$percent", entry.LowestMatchPercent);
        cmd.Parameters.AddWithValue("$ms", entry.TotalMs);
        cmd.ExecuteNonQuery();

        using var trim = conn.CreateCommand();
        trim.CommandText = """
            DELETE FROM compare_history
            WHERE id NOT IN (SELECT id FROM compare_history ORDER BY last_run DESC LIMIT $keep);
            """;
        trim.Parameters.AddWithValue("$keep", MaxHistoryEntries);
        trim.ExecuteNonQuery();
    }

    public void DeleteHistory(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM compare_history WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearHistory()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM compare_history";
        cmd.ExecuteNonQuery();
    }

    // ---------------- shares ----------------

    public List<ShareRecord> GetShares()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT token, source_label, source_detail, targets_json, compared, created,
                   expires, object_count, total_views
            FROM shares
            ORDER BY created DESC;
            """;
        using var r = cmd.ExecuteReader();

        var list = new List<ShareRecord>();
        while (r.Read())
        {
            list.Add(new ShareRecord
            {
                Token = r.GetString(0),
                SourceLabel = r.GetString(1),
                SourceDetail = r.GetString(2),
                TargetLabels = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [],
                ComparedUtc = ReadUtc(r.GetString(4)),
                CreatedUtc = ReadUtc(r.GetString(5)),
                ExpiresUtc = ReadUtc(r.GetString(6)),
                ObjectCount = r.GetInt32(7),
                TotalViews = r.GetInt32(8)
            });
        }
        return list;
    }

    public void SaveShare(ShareRecord share)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shares
                (token, source_label, source_detail, targets_json, compared, created,
                 expires, object_count, total_views)
            VALUES
                ($token, $label, $detail, $targets, $compared, $created,
                 $expires, $objects, $views)
            ON CONFLICT(token) DO UPDATE SET
                expires = excluded.expires,
                total_views = excluded.total_views;
            """;
        cmd.Parameters.AddWithValue("$token", share.Token);
        cmd.Parameters.AddWithValue("$label", share.SourceLabel);
        cmd.Parameters.AddWithValue("$detail", share.SourceDetail);
        cmd.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(share.TargetLabels));
        cmd.Parameters.AddWithValue("$compared", share.ComparedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$created", share.CreatedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$expires", share.ExpiresUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$objects", share.ObjectCount);
        cmd.Parameters.AddWithValue("$views", share.TotalViews);
        cmd.ExecuteNonQuery();
    }

    public void DeleteShare(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM shares WHERE token = $token";
        cmd.Parameters.AddWithValue("$token", token);
        cmd.ExecuteNonQuery();
    }

    public void CountShareView(string token)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE shares SET total_views = total_views + 1 WHERE token = $token";
        cmd.Parameters.AddWithValue("$token", token);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops shares that have run out of time. Returns their tokens so the payload files can go too.</summary>
    public List<string> DeleteExpiredShares(DateTime nowUtc)
    {
        using var conn = Open();

        var expired = new List<string>();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT token FROM shares WHERE expires <= $now";
            find.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
            using var r = find.ExecuteReader();
            while (r.Read()) expired.Add(r.GetString(0));
        }

        if (expired.Count == 0) return expired;

        using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM shares WHERE expires <= $now";
        del.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        del.ExecuteNonQuery();

        return expired;
    }

    // ---------------- apply audit ----------------
    // Written as the apply goes - the run before its first file, each file as it ends - so a crash
    // part-way still leaves a record of what reached the database. Never trimmed: it is an audit log.

    public void StartApplyRun(ApplyRunRecord run)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO apply_runs
                (id, started, compare_run, target_label, server, database_name, login, sections_json, message, engine)
            VALUES
                ($id, $started, $run, $label, $server, $db, $login, $sections, $message, $engine);
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$run", run.CompareRunId);
        cmd.Parameters.AddWithValue("$label", run.TargetLabel);
        cmd.Parameters.AddWithValue("$server", run.Server);
        cmd.Parameters.AddWithValue("$db", run.Database);
        cmd.Parameters.AddWithValue("$login", run.Login);
        cmd.Parameters.AddWithValue("$sections", JsonSerializer.Serialize(run.Sections));
        cmd.Parameters.AddWithValue("$message", run.Message);
        cmd.Parameters.AddWithValue("$engine", (int)run.Engine);
        cmd.ExecuteNonQuery();
    }

    public void AddApplyFile(string runId, int seq, ApplyFileRecord file)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO apply_batches (run_id, seq, folder, file, object_name, ok, error, elapsed_ms, statement_index)
            VALUES ($run, $seq, $folder, $file, $object, $ok, $error, $ms, $statement);
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$folder", file.Folder);
        cmd.Parameters.AddWithValue("$file", file.File);
        cmd.Parameters.AddWithValue("$object", file.ObjectName);
        cmd.Parameters.AddWithValue("$ok", file.Ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$error", (object?)file.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", file.ElapsedMs);
        cmd.Parameters.AddWithValue("$statement", (object?)file.StatementIndex ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void FinishApplyRun(ApplyRunRecord run)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE apply_runs
            SET finished = $finished, success = $success, message = $message, stopped_at = $stopped
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", run.Id);
        cmd.Parameters.AddWithValue("$finished", (run.FinishedUtc ?? DateTime.UtcNow).ToString("O"));
        cmd.Parameters.AddWithValue("$success", run.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$message", run.Message);
        cmd.Parameters.AddWithValue("$stopped", (object?)run.StoppedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The most recent applies, newest first, each with its files in run order.</summary>
    public List<ApplyRunRecord> GetApplyRuns(int limit = 50)
    {
        using var conn = Open();
        var runs = new List<ApplyRunRecord>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, started, finished, compare_run, target_label, server, database_name, login,
                       sections_json, success, message, stopped_at, engine
                FROM apply_runs
                ORDER BY started DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                runs.Add(new ApplyRunRecord
                {
                    Id = r.GetString(0),
                    StartedUtc = ReadUtc(r.GetString(1)),
                    FinishedUtc = r.IsDBNull(2) ? null : ReadUtc(r.GetString(2)),
                    CompareRunId = r.GetString(3),
                    TargetLabel = r.GetString(4),
                    Server = r.GetString(5),
                    Database = r.GetString(6),
                    Login = r.GetString(7),
                    Sections = JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? [],
                    Success = r.GetInt32(9) == 1,
                    Message = r.GetString(10),
                    StoppedAt = r.IsDBNull(11) ? null : r.GetString(11),
                    Engine = (DatabaseEngine)r.GetInt32(12)
                });
            }
        }

        foreach (var run in runs)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT folder, file, object_name, ok, error, elapsed_ms, statement_index
                FROM apply_batches WHERE run_id = $id ORDER BY seq;
                """;
            cmd.Parameters.AddWithValue("$id", run.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                run.Files.Add(new ApplyFileRecord
                {
                    Folder = r.GetString(0),
                    File = r.GetString(1),
                    ObjectName = r.GetString(2),
                    Ok = r.GetInt32(3) == 1,
                    Error = r.IsDBNull(4) ? null : r.GetString(4),
                    ElapsedMs = r.GetInt64(5),
                    StatementIndex = r.IsDBNull(6) ? null : r.GetInt32(6)
                });
            }
        }
        return runs;
    }

    // Fully qualified: System.Globalization.CompareOptions would collide with ours.
    private static DateTime ReadUtc(string text) => DateTime.Parse(
        text,
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    // ---------------- settings ----------------

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
