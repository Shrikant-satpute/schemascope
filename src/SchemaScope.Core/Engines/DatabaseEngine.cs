namespace SchemaScope.Core.Engines;

/// <summary>
/// The database product on the other end of a connection. Append-only: the number is what the store and every saved
/// JSON file hold, so a value is never renumbered or reused. Everything defaults to <see cref="SqlServer"/>, which is
/// what every connection, snapshot and audit row written before this existed was.
/// </summary>
public enum DatabaseEngine
{
    SqlServer = 0,
    PostgreSql = 1,
    MySql = 2,
    MariaDb = 3,
    Oracle = 4,
    Sqlite = 5
}

/// <summary>TLS for engines whose drivers take an SSL mode (PostgreSQL, MySQL). SQL Server keeps its Encrypt and TrustServerCertificate switches.</summary>
public enum SslMode
{
    Disable = 0,
    Prefer = 1,
    Require = 2,
    VerifyCa = 3,
    VerifyFull = 4
}

/// <summary>
/// Where a connection string really goes, as far as secret release and the Protected rule care: every host it may
/// connect to (multi-host and failover strings name more than one), the port, and for SQL Server the instance.
/// </summary>
/// <param name="LoginDescription">Who it signs in as, never the password: "SQL login: name". Empty when not read.</param>
/// <param name="Servers">
/// Each destination as the string spells it, for labels, RUN.ps1 and the SQL Server Protected rule (SQL Server: the
/// Data Source, then the failover partner). Null when the provider keeps only <paramref name="Hosts"/>.
/// </param>
/// <param name="FailoverPartner">
/// SQL Server: the failover partner as written, even when no Data Source is named (<paramref name="Servers"/> then
/// leaves it out), so the Protected rule still sees it. Null when none is set or the engine has none.
/// </param>
public sealed record EndpointInfo(DatabaseEngine Engine, IReadOnlyList<string> Hosts, int? Port,
    string? Instance, string Database, string LoginDescription, IReadOnlyList<string>? Servers = null,
    string? FailoverPartner = null);

public static class DatabaseEngines
{
    /// <summary>The port a connection with no port of its own uses. Null where there is no network port to speak of.</summary>
    public static int? DefaultPort(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => 1433,
        DatabaseEngine.PostgreSql => 5432,
        DatabaseEngine.MySql or DatabaseEngine.MariaDb => 3306,
        DatabaseEngine.Oracle => 1521,
        _ => null
    };
}
