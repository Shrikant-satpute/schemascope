using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;

namespace SchemaScope.Api.Services;

/// <summary>
/// What a connection coming in from the UI may say, checked before anything is saved, tested or read. SQL Server and
/// PostgreSQL are wired up in this version; a connection for any other engine is refused here rather than handed to
/// a driver that would send its credentials to whatever answers on that host. Whether a preview engine is turned on
/// is the <see cref="EngineCatalog"/>'s to say (<see cref="NotEnabled"/>).
/// </summary>
public static class ConnectionRules
{
    private static readonly HashSet<AuthMode> SqlServerAuth =
        [AuthMode.Windows, AuthMode.SqlLogin, AuthMode.AzureAdPassword, AuthMode.AzureAdInteractive, AuthMode.AzureAdDefault];

    /// <summary>The product's own name for messages: "PostgreSQL", not the enum's "PostgreSql".</summary>
    /// <summary>
    /// How a typed database name is checked against the target's before an apply: SQL Server as today (case does not
    /// matter), every other engine exactly - PostgreSQL's "App" and "app" are two databases.
    /// </summary>
    public static StringComparison TypedNameComparison(DatabaseEngine engine) =>
        engine == DatabaseEngine.SqlServer ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NameOf(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => "SQL Server",
        DatabaseEngine.PostgreSql => "PostgreSQL",
        DatabaseEngine.MySql => "MySQL",
        DatabaseEngine.MariaDb => "MariaDB",
        DatabaseEngine.Oracle => "Oracle",
        DatabaseEngine.Sqlite => "SQLite",
        _ => "That database"
    };

    /// <summary>For an engine this build carries but has turned off (a preview engine with the switch off).</summary>
    public static string NotEnabled(DatabaseEngine engine) =>
        $"{NameOf(engine)} is a preview in this version: turn on \"Show preview engines\" on the Connections screen to use it.";

    /// <summary>Null when the connection may be used, else the reason, worded for the user.</summary>
    public static string? Problem(ConnectionSettings s)
    {
        if (s.Engine == DatabaseEngine.PostgreSql) return PostgreSqlProblem(s);
        if (s.Engine != DatabaseEngine.SqlServer)
            return $"{(Enum.IsDefined(s.Engine) ? s.Engine.ToString() : "That database")} connections are not available in this version of SchemaScope.";
        if (!SqlServerAuth.Contains(s.Auth))
            return "That sign-in method is not available for SQL Server.";
        if (s.Port is not null)
            return "For SQL Server, put the port in the server name: host,port.";
        if (s.Ssl is not null)
            return "SQL Server uses the Encrypt and Trust server certificate switches, not an SSL mode.";
        if (s.EngineOptions is { Count: > 0 } options)
            return options.Keys.Any(SecretKeys.IsSecret)
                ? "Engine options never hold a password or other secret."
                : "SQL Server takes no engine options.";
        if (s.UseRawConnectionString && ConnectionEndpoints.IsUri(s.RawConnectionString))
            return "Connection strings in URI form (scheme://user:password@host) are not accepted. " +
                   "Use key=value pairs, e.g. Server=host;Database=name;Integrated Security=true.";
        return null;
    }

    /// <summary>
    /// PostgreSQL in this version: a user name and password, a port in range, TLS verified unless the user ticked
    /// "Trust server certificate" (Require and Prefer never check the certificate, and Disable has no TLS at all, so
    /// they need that tick too), no
    /// engine options, and a raw string Npgsql's way round - never a URI, never another driver's keys.
    /// </summary>
    private static string? PostgreSqlProblem(ConnectionSettings s)
    {
        if (s.UseRawConnectionString)
        {
            if (ConnectionEndpoints.IsUri(s.RawConnectionString))
                return "Connection strings in URI form (postgres://user:password@host) are not accepted. " +
                       "Use key=value pairs, e.g. Host=host;Port=5432;Database=name;Username=user.";
            // Npgsql's own reading: a key it does not know, or hosts and ports that cannot be told apart, is refused
            if (!string.IsNullOrWhiteSpace(s.RawConnectionString) &&
                new PgConnectionService().ParseEndpoint(s.RawConnectionString) is null)
                return "That is not a PostgreSQL connection string SchemaScope can read: name the host (or hosts) and one port, " +
                       "e.g. Host=host;Port=5432;Database=name;Username=user.";
        }
        if (s.Auth != AuthMode.Password)
            return "That sign-in method is not available for PostgreSQL in this version: use a user name and password.";
        if (s.Port is { } port && port is <= 0 or > 65535)
            return "The port must be a number from 1 to 65535.";
        if (s.Ssl is { } mode && !Enum.IsDefined(mode))
            return "Unknown SSL mode.";
        if (s.Ssl is SslMode.Require or SslMode.Prefer && !s.TrustServerCertificate)
            return "SSL mode Require and Prefer do not check the server certificate. Tick \"Trust server certificate\" " +
                   "to connect that way on purpose, or use Verify full.";
        if (s.Ssl is SslMode.Disable && !s.TrustServerCertificate)
            return "SSL mode Disable sends the password and every query unencrypted. Tick \"Trust server certificate\" " +
                   "to connect that way on purpose, or use Verify full.";
        if (s.EngineOptions is { Count: > 0 } options)
            return options.Keys.Any(SecretKeys.IsSecret)
                ? "Engine options never hold a password or other secret."
                : "PostgreSQL takes no engine options in this version.";
        return null;
    }

    /// <summary>
    /// Checked once the stored secret was put back (<see cref="SecretRelease.TryRehydrate"/>): a PostgreSQL sign-in
    /// from the form always carries its password. Given none, Npgsql would send one SchemaScope never stored - the
    /// PGPASSWORD environment variable or a pgpass.conf entry, which may match any host - so an empty one is refused.
    /// A raw string is used as typed: one without a password gets the driver's own fallbacks, as it would anywhere.
    /// </summary>
    public static string? MissingPassword(ConnectionSettings s) =>
        s.Engine == DatabaseEngine.PostgreSql && !s.UseRawConnectionString && s.Auth == AuthMode.Password &&
        string.IsNullOrEmpty(s.Password)
            ? "Type the password: a PostgreSQL connection without one is not attempted."
            : null;
}
