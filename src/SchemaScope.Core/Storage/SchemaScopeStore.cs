using System.Text.Json;
using Microsoft.Data.Sqlite;
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
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------------- connections ----------------

    public List<ConnectionSettings> GetConnections()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM connections ORDER BY sort_order, label, server";
        using var r = cmd.ExecuteReader();

        var list = new List<ConnectionSettings>();
        while (r.Read()) list.Add(MapConnection(r));
        return list;
    }

    public ConnectionSettings? GetConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM connections WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? MapConnection(r) : null;
    }

    public ConnectionSettings SaveConnection(ConnectionSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Id)) s.Id = Guid.NewGuid().ToString("n");

        // A blank password on an existing row means "leave what is saved alone",
        // so the UI never has to round-trip the real secret.
        byte[]? passwordEnc;
        byte[]? rawEnc;
        var existing = GetConnectionRawSecrets(s.Id);

        if (string.IsNullOrEmpty(s.Password) || s.Password == "********")
            passwordEnc = existing.Password;
        else
            passwordEnc = SecretProtector.Protect(s.Password);

        if (s.UseRawConnectionString)
        {
            rawEnc = string.IsNullOrEmpty(s.RawConnectionString) || s.RawConnectionString.Contains("********")
                ? existing.Raw
                : SecretProtector.Protect(s.RawConnectionString);
        }
        else
        {
            rawEnc = null;
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO connections
                (id, label, server, database_name, auth, username, password_enc, encrypt,
                 trust_cert, connect_timeout, app_intent_ro, use_raw, raw_cs_enc,
                 protected_server, colour, last_used, sort_order)
            VALUES
                ($id, $label, $server, $db, $auth, $user, $pwd, $encrypt,
                 $trust, $timeout, $ro, $useRaw, $raw,
                 $protected, $colour, $lastUsed,
                 COALESCE((SELECT sort_order FROM connections WHERE id = $id), 0))
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

        cmd.ExecuteNonQuery();
        return GetConnection(s.Id)!;
    }

    public void DeleteConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM connections WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TouchConnection(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE connections SET last_used = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void ReorderConnections(IReadOnlyList<string> orderedIds)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE connections SET sort_order = $o WHERE id = $id";
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
        cmd.CommandText = "SELECT password_enc, raw_cs_enc FROM connections WHERE id = $id";
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
