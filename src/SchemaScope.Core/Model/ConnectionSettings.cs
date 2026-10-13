namespace SchemaScope.Core.Model;

public enum AuthMode
{
    /// <summary>Integrated Security - the Windows account running SchemaScope.</summary>
    Windows = 0,
    SqlLogin = 1,
    AzureAdPassword = 2,
    /// <summary>Opens a browser window, supports MFA.</summary>
    AzureAdInteractive = 3,
    AzureAdDefault = 4
}

/// <summary>
/// A saved connection. The password is never stored here in plain text - the
/// store encrypts it with Windows DPAPI before it touches disk.
/// </summary>
public sealed class ConnectionSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Label { get; set; } = "";
    public string Server { get; set; } = "";
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

    /// <summary>Marks a server the user never wants written to. v1 never writes at all.</summary>
    public bool ProtectedServer { get; set; }

    public string Colour { get; set; } = "";
    public DateTime? LastUsedUtc { get; set; }

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : string.IsNullOrWhiteSpace(Database) ? Server
        : $"{Server}.{Database}";

    /// <summary>Whether this connection signs in with a password typed by the user.</summary>
    public bool UsesPassword =>
        !UseRawConnectionString && Auth is AuthMode.SqlLogin or AuthMode.AzureAdPassword;

    /// <summary>
    /// The one rule for releasing a stored secret: it only ever goes back to the
    /// server, login and auth mode it was saved against. The database may differ
    /// - one server is often compared against itself - because a database name
    /// cannot redirect a credential elsewhere.
    /// </summary>
    public static bool SameDestination(ConnectionSettings a, ConnectionSettings b) =>
        string.Equals(a.Server, b.Server, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.UserName ?? "", b.UserName ?? "", StringComparison.OrdinalIgnoreCase) &&
        a.Auth == b.Auth &&
        a.UseRawConnectionString == b.UseRawConnectionString;

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
            if (key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("pwd", StringComparison.OrdinalIgnoreCase))
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
