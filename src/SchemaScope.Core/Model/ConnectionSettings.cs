using SchemaScope.Core.Engines;

namespace SchemaScope.Core.Model;

/// <summary>How a connection signs in. Append-only: the number is stored. Each engine allows a subset.</summary>
public enum AuthMode
{
    /// <summary>Integrated Security - the Windows account running SchemaScope.</summary>
    Windows = 0,
    SqlLogin = 1,
    AzureAdPassword = 2,
    /// <summary>Opens a browser window, supports MFA.</summary>
    AzureAdInteractive = 3,
    AzureAdDefault = 4,
    /// <summary>A user name and password for engines other than SQL Server (PostgreSQL, MySQL).</summary>
    Password = 5,
    Kerberos = 6,
    ClientCertificate = 7
}

/// <summary>
/// A saved connection. The password is never stored here in plain text - the
/// store encrypts it with Windows DPAPI before it touches disk.
/// </summary>
public sealed class ConnectionSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Label { get; set; } = "";

    /// <summary>What is on the other end. Every connection saved before this existed is SQL Server.</summary>
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;
    public string Server { get; set; } = "";

    /// <summary>Null = the engine's default port. SQL Server keeps its port in <see cref="Server"/> ("host,port").</summary>
    public int? Port { get; set; }

    /// <summary>TLS mode for engines that take one. Null = the engine's safe default.</summary>
    public SslMode? Ssl { get; set; }

    /// <summary>Engine-specific switches. Never a secret: those go through DPAPI like the password.</summary>
    public Dictionary<string, string>? EngineOptions { get; set; }

    public string Database { get; set; } = "";
    public AuthMode Auth { get; set; } = AuthMode.Windows;
    public string? UserName { get; set; }

    /// <summary>Only ever populated in memory or on the way in from the UI.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Encrypted by default, and the certificate is checked by default. An
    /// internal server with a self-signed certificate needs
    /// <see cref="TrustServerCertificate"/> switched on deliberately - the
    /// connection test says so when that is what went wrong.
    /// </summary>
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public bool ApplicationIntentReadOnly { get; set; }

    /// <summary>When true, <see cref="RawConnectionString"/> is used as typed.</summary>
    public bool UseRawConnectionString { get; set; }
    public string? RawConnectionString { get; set; }

    /// <summary>
    /// "Protected - never apply scripts here". SchemaScope is read-only by default; even with write mode on,
    /// applying scripts to this server is refused outright.
    /// </summary>
    public bool ProtectedServer { get; set; }

    public string Colour { get; set; } = "";
    public DateTime? LastUsedUtc { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : string.IsNullOrWhiteSpace(Database) ? Server
        : $"{Server}.{Database}";

    /// <summary>Whether this connection signs in with a password typed by the user.</summary>
    public bool UsesPassword =>
        !UseRawConnectionString && Auth is AuthMode.SqlLogin or AuthMode.AzureAdPassword or AuthMode.Password;

    /// <summary>The port this connects to: its own, or the engine's default.</summary>
    public int? EffectivePort => Port ?? DatabaseEngines.DefaultPort(Engine);

    /// <summary>
    /// The one rule for releasing a stored secret: it only ever goes back to the
    /// engine, server, port, login and auth mode it was saved against. The database
    /// may differ - one server is often compared against itself - because a database
    /// name cannot redirect a credential elsewhere. A raw connection string must also
    /// name the same hosts and port as the saved one; when either side cannot be read,
    /// it is not the same destination.
    /// </summary>
    public static bool SameDestination(ConnectionSettings a, ConnectionSettings b)
    {
        if (a.Engine != b.Engine || a.EffectivePort != b.EffectivePort ||
            !string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(a.UserName ?? "", b.UserName ?? "", StringComparison.OrdinalIgnoreCase) ||
            a.Auth != b.Auth ||
            a.UseRawConnectionString != b.UseRawConnectionString)
            return false;
        if (!a.UseRawConnectionString) return true;

        var ea = ConnectionEndpoints.Parse(a.Engine, a.RawConnectionString);
        var eb = ConnectionEndpoints.Parse(b.Engine, b.RawConnectionString);
        return ea is not null && eb is not null &&
               ea.Port == eb.Port &&
               string.Equals(ea.Instance ?? "", eb.Instance ?? "", StringComparison.OrdinalIgnoreCase) &&
               ea.Hosts.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(eb.Hosts);
    }

    public ConnectionSettings WithoutSecrets()
    {
        var copy = (ConnectionSettings)MemberwiseClone();
        copy.Password = string.IsNullOrEmpty(Password) ? null : "********";
        if (copy.UseRawConnectionString && !string.IsNullOrEmpty(copy.RawConnectionString))
            copy.RawConnectionString = MaskConnectionString(copy.RawConnectionString);
        return copy;
    }

    private static string MaskConnectionString(string cs)
    {
        var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq <= 0) continue;
            var key = parts[i][..eq].Trim();
            if (SecretKeys.IsSecret(key))
                parts[i] = $"{key}=********";
        }
        return string.Join(';', parts);
    }
}

public sealed class ConnectionTestResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Hint { get; set; }
    public long ElapsedMs { get; set; }
    public string? ProductVersion { get; set; }
    public string? Edition { get; set; }
    public string? ServerName { get; set; }
    public string? DatabaseName { get; set; }
    public string? Collation { get; set; }
    public int ObjectCount { get; set; }
    public bool CanViewDefinitions { get; set; }
}

public sealed class DatabaseListItem
{
    public string Name { get; set; } = "";
    public bool HasAccess { get; set; }
}
