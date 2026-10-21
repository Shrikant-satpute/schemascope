using SchemaScope.SqlServer;

namespace SchemaScope.Api.Services;

/// <summary>
/// One spelling for one SQL Server, so a protected connection is matched however a target names its server. The
/// rule itself is the SQL Server identity's (<see cref="SqlServerIdentity"/>); this keeps its old name for the routes
/// and their tests.
/// </summary>
public static class ServerNames
{
    public static string Normalise(string? server) => SqlServerIdentity.Normalise(server);

    public static bool Same(string? a, string? b) => SqlServerIdentity.Same(a, b);

    /// <summary>The machine alone, instance and port dropped: what the Protected rule matches across engines.</summary>
    public static string Host(string? server) => SqlServerIdentity.Host(server);
}
