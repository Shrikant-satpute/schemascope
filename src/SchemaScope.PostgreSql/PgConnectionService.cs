using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using NpgsqlSslMode = Npgsql.SslMode;
using SslMode = SchemaScope.Core.Engines.SslMode;

namespace SchemaScope.PostgreSql;

/// <summary>
/// Npgsql connection strings from the friendly form, connection tests and the database list. Everything here is read
/// only. Strings in URI form (postgres://user:password@host) are refused: the password sits in the host part.
/// </summary>
public sealed class PgConnectionService : IConnectionService
{
    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;

    /// <summary>Browse connects here: every cluster has it, so the list works before a database is chosen.</summary>
    public string? BrowseDatabase => "postgres";

    public string BuildConnectionString(ConnectionSettings s, string? overrideDatabase = null)
    {
        if (s.UseRawConnectionString && !string.IsNullOrWhiteSpace(s.RawConnectionString))
        {
            if (ConnectionEndpoints.IsUri(s.RawConnectionString))
                throw new ArgumentException("PostgreSQL connection strings in URI form are not accepted; use key=value pairs.");
            var raw = new NpgsqlConnectionStringBuilder(s.RawConnectionString);
            if (string.IsNullOrEmpty(raw.ApplicationName)) raw.ApplicationName = "SchemaScope";
            if (!string.IsNullOrWhiteSpace(overrideDatabase)) raw.Database = overrideDatabase;
            // the driver's own default (Prefer) never checks the certificate; a string that does not say otherwise is verified
            if (!NamesSslMode(s.RawConnectionString)) raw.SslMode = NpgsqlSslMode.VerifyFull;
            return raw.ConnectionString;
        }

        var b = new NpgsqlConnectionStringBuilder
        {
            Host = s.Server.Trim(),
            Database = overrideDatabase ?? s.Database,
            ApplicationName = "SchemaScope",
            Timeout = s.ConnectTimeoutSeconds <= 0 ? 15 : Math.Min(s.ConnectTimeoutSeconds, 1024),
            SslMode = EffectiveSslMode(s),
            Pooling = true,
            Username = s.UserName ?? "",
            Password = s.Password ?? ""
        };
        if (s.Port is { } port) b.Port = port;
        return b.ConnectionString;
    }

    /// <summary>
    /// TLS as the form asked for it. VerifyFull unless said otherwise; "Trust server certificate" is the explicit opt-in
    /// to encrypt without checking the certificate (Require, which never verifies in Npgsql).
    /// </summary>
    public static NpgsqlSslMode EffectiveSslMode(ConnectionSettings s)
    {
        var mode = s.Ssl ?? SslMode.VerifyFull;
        if (s.TrustServerCertificate && mode is SslMode.VerifyFull or SslMode.VerifyCa) return NpgsqlSslMode.Require;
        return mode switch
        {
            SslMode.Disable => NpgsqlSslMode.Disable,
            SslMode.Prefer => NpgsqlSslMode.Prefer,
            SslMode.Require => NpgsqlSslMode.Require,
            SslMode.VerifyCa => NpgsqlSslMode.VerifyCA,
            _ => NpgsqlSslMode.VerifyFull
        };
    }

    private static bool NamesSslMode(string cs)
    {
        try
        {
            var keys = new DbConnectionStringBuilder { ConnectionString = cs }.Keys.Cast<string>();
            return keys.Any(k => k.Replace(" ", "").Equals("sslmode", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ConnectionTestResult();
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = 30000;", conn, tx))
                await ro.ExecuteNonQueryAsync(ct);

            await using var cmd = new NpgsqlCommand(PgCatalogQueries.TestInfo, conn, tx) { CommandTimeout = 30 };
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (await r.ReadAsync(ct))
                {
                    result.ProductVersion = $"PostgreSQL {r.GetString(0)}";
                    result.DatabaseName = r.GetString(1);
                    result.Collation = r.IsDBNull(2) ? null : r.GetString(2);
                    result.ObjectCount = (int)r.GetInt64(3);
                }
            }
            await tx.RollbackAsync(ct);

            result.ServerName = conn.Host;
            result.CanViewDefinitions = true;   // pg_catalog shows every definition to any user who can connect
            result.Success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Success = false;
            result.Message = MessageOf(ex);
            result.Hint = HintFor(ex);
        }
        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    public async Task<IReadOnlyList<DatabaseListItem>> ListDatabasesAsync(string connectionString, CancellationToken ct = default)
    {
        var list = new List<DatabaseListItem>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(PgCatalogQueries.ListDatabases, conn) { CommandTimeout = 30 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DatabaseListItem { Name = r.GetString(0), HasAccess = r.GetBoolean(1) });
        return list;
    }

    /// <summary>
    /// Where the string really goes: every host of a multi-host string, the one port, the database and the user. Null
    /// when Npgsql cannot read it, when it is in URI form, or when the hosts and ports cannot be told apart for sure
    /// (<see cref="ConnectionEndpoints.Parse"/>, the same reading the store's secret rule uses).
    /// </summary>
    public EndpointInfo? ParseEndpoint(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || ConnectionEndpoints.IsUri(connectionString)) return null;
        NpgsqlConnectionStringBuilder b;
        try
        {
            b = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidCastException)
        {
            return null;
        }

        if (ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, connectionString) is not { } ep) return null;
        return new EndpointInfo(DatabaseEngine.PostgreSql, ep.Hosts, ep.Port ?? b.Port, null, b.Database ?? "",
            string.IsNullOrEmpty(b.Username) ? "PostgreSQL user: (default)" : $"PostgreSQL user: {b.Username}");
    }

    public IReadOnlyList<string> SecretValues(string connectionString) => SecretKeys.ValuesIn(connectionString);

    public string Mask(string connectionString) => ConnectionSettings.MaskConnectionString(connectionString);

    /// <summary>
    /// The driver's message, never a server DETAIL, WHERE or HINT (they can echo row values): Npgsql leaves those out
    /// unless Include Error Detail is on, and this reads <see cref="PostgresException.MessageText"/> alone. Truncated.
    /// </summary>
    public static string MessageOf(Exception ex)
    {
        var text = ex switch
        {
            PostgresException pg => $"{pg.SqlState}: {pg.MessageText}",
            NpgsqlException { InnerException: SocketException so } => $"Could not reach the server ({so.SocketErrorCode}).",
            NpgsqlException { InnerException: AuthenticationException } => "The TLS handshake failed: the server certificate could not be verified.",
            _ => ex.Message
        };
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    public static string? HintFor(Exception ex) => ex switch
    {
        PostgresException { SqlState: "28P01" } => "Login failed. Check the user name and password.",
        PostgresException { SqlState: "28000" } => "The server refused this user from this address (pg_hba.conf). Ask its administrator.",
        PostgresException { SqlState: "3D000" } => "That database does not exist on this server. Check the name, or use Browse.",
        PostgresException { SqlState: "42501" } => "This user lacks a permission it needs (CONNECT on the database, or USAGE on a schema).",
        NpgsqlException { InnerException: SocketException } or TimeoutException =>
            "Server not found or not reachable. Check the host, the port and any firewall.",
        NpgsqlException { InnerException: AuthenticationException } =>
            "The server certificate is not trusted. Tick 'Trust server certificate' to connect without checking it.",
        NpgsqlException n when n.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) =>
            "The server does not offer TLS as this SSL mode needs. Pick another SSL mode if that is expected.",
        _ => null
    };
}
