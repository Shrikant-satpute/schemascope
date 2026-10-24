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
/// Connections go through <see cref="DataSource"/>, which never answers a GSS/SSPI request with Windows credentials.
/// The form always carries a password; a raw string is used as typed, so one without a Password key gets Npgsql's
/// own fallbacks (PGPASSWORD, then pgpass.conf), as it would in any other Npgsql program.
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

        // SQL Server's "host,port": here the comma would list a host called after the port, and the password go to 5432
        if (ConnectionEndpoints.HasPortAfterComma(s.Server))
            throw new ArgumentException("For PostgreSQL the port goes in the Port field, not after a comma: a comma lists hosts.");
        // with no password Npgsql would send PGPASSWORD or a pgpass.conf entry instead (ConnectionRules.MissingPassword)
        if (string.IsNullOrEmpty(s.Password))
            throw new ArgumentException("Type the password: a PostgreSQL connection without one is not attempted.");

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

    /// <summary>
    /// Every connection SchemaScope opens to PostgreSQL comes from here. Password sign-in is the only one offered, but
    /// a server may still answer with a GSS or SSPI request, and Npgsql would then sign in as the Windows user running
    /// SchemaScope (NTLM or Kerberos) - to any host typed in, or anyone on the path. The negotiate callback refuses
    /// before a single token is sent. Dispose the data source with the connection.
    /// </summary>
    public static NpgsqlDataSource DataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseNegotiateOptionsCallback(_ => throw new WindowsSignInRefusedException());
        return builder.Build();
    }

    /// <summary>A connection from <see cref="DataSource"/>, opened; disposing it disposes its data source too.</summary>
    public static async Task<PgOpenConnection> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var source = DataSource(connectionString);
        try
        {
            var conn = source.CreateConnection();
            try
            {
                await conn.OpenAsync(ct);
                return new PgOpenConnection(source, conn);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
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
            await using var open = await OpenAsync(connectionString, ct);
            var conn = open.Connection;
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
        await using var open = await OpenAsync(connectionString, ct);
        await using var cmd = new NpgsqlCommand(PgCatalogQueries.ListDatabases, open.Connection) { CommandTimeout = 30 };
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
            WindowsSignInRefusedException => ex.Message,
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
        WindowsSignInRefusedException =>
            "Ask the server's administrator for a password sign-in (scram-sha-256 or md5 in pg_hba.conf) for this user.",
        NpgsqlException { InnerException: SocketException } or TimeoutException =>
            "Server not found or not reachable. Check the host, the port and any firewall.",
        NpgsqlException { InnerException: AuthenticationException } =>
            "The server certificate is not trusted. Tick 'Trust server certificate' to connect without checking it.",
        NpgsqlException n when n.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) =>
            "The server does not offer TLS as this SSL mode needs. Pick another SSL mode if that is expected.",
        _ => null
    };
}

/// <summary>An open connection and the data source it came from (<see cref="PgConnectionService.OpenAsync"/>).</summary>
public sealed class PgOpenConnection(NpgsqlDataSource source, NpgsqlConnection connection) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = connection;

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await source.DisposeAsync();
    }
}

/// <summary>
/// The server asked for Kerberos or SSPI (GSS) sign-in. SchemaScope refuses it, so the Windows user's credentials
/// never go to a server that only had to ask for them.
/// </summary>
public sealed class WindowsSignInRefusedException()
    : NpgsqlException("The server asked for Windows (Kerberos/SSPI) sign-in. SchemaScope signs in to PostgreSQL with a " +
                      "user name and password only, so it did not send your Windows credentials.");
