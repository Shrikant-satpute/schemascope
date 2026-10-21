using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Api.Services;

/// <summary>
/// What a connection coming in from the UI may say, checked before anything is saved, tested or read. Only SQL
/// Server is wired up in this version, so a connection for any other engine is refused here rather than handed to
/// SqlClient - which would send its credentials over TDS to whatever answers on that host.
/// </summary>
public static class ConnectionRules
{
    private static readonly HashSet<AuthMode> SqlServerAuth =
        [AuthMode.Windows, AuthMode.SqlLogin, AuthMode.AzureAdPassword, AuthMode.AzureAdInteractive, AuthMode.AzureAdDefault];

    /// <summary>Null when the connection may be used, else the reason, worded for the user.</summary>
    public static string? Problem(ConnectionSettings s)
    {
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
}
