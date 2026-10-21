using System.Data.Common;
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

    /// <summary>
    /// When a secret saved against <paramref name="saved"/> may go out with <paramref name="incoming"/>: the same
    /// destination (<see cref="SameDestination"/>), and for an engine that takes an SSL mode, TLS no weaker than it
    /// was saved with. Otherwise anyone holding the API could re-post a saved connection with SSL off and have its
    /// password sent in clear text; a downgrade means typing the password again. SQL Server is unchanged.
    /// </summary>
    public static bool MayReleaseSecret(ConnectionSettings incoming, ConnectionSettings saved) =>
        SameDestination(incoming, saved) && !WeakerTls(incoming, saved);

    /// <summary>
    /// True when <paramref name="incoming"/> asks for less TLS than <paramref name="saved"/>: Disable &lt; Prefer &lt;
    /// Require &lt; Verify CA &lt; Verify full, with "Trust server certificate" bringing a verify mode down to
    /// Require. A raw string is released whole, its own TLS settings and all, so the form's fields do not count.
    /// </summary>
    public static bool WeakerTls(ConnectionSettings incoming, ConnectionSettings saved) =>
        incoming.Engine != DatabaseEngine.SqlServer && !incoming.UseRawConnectionString &&
        TlsRank(incoming) < TlsRank(saved);

    private static int TlsRank(ConnectionSettings s)
    {
        var mode = s.Ssl ?? SslMode.VerifyFull;
        if (s.TrustServerCertificate && mode is SslMode.VerifyFull or SslMode.VerifyCa) mode = SslMode.Require;
        return (int)mode;
    }

    public ConnectionSettings WithoutSecrets()
    {
        var copy = (ConnectionSettings)MemberwiseClone();
        copy.Password = string.IsNullOrEmpty(Password) ? null : "********";
        if (copy.UseRawConnectionString && !string.IsNullOrEmpty(copy.RawConnectionString))
            copy.RawConnectionString = MaskConnectionString(copy.RawConnectionString);
        return copy;
    }

    /// <summary>
    /// Every secret key's value replaced whole, quotes and all, so a quoted Password="a;b" leaves no tail behind and
    /// the result still parses (the secret rule reads where it points). The rest of the text is kept as typed. The
    /// connection string parser is the judge: when its reading of the masked string differs from the original's in
    /// anything but the secrets - or a URI-form string (its password sits in the host part), or one it cannot read -
    /// the whole string is masked.
    /// </summary>
    public static string MaskConnectionString(string cs)
    {
        const string masked = "********";
        if (ConnectionEndpoints.IsUri(cs)) return masked;

        var original = new DbConnectionStringBuilder();
        var result = new DbConnectionStringBuilder();
        string text;
        try
        {
            original.ConnectionString = cs;
            text = ReplaceSecretValues(cs, masked);
            result.ConnectionString = text;
        }
        catch (ArgumentException)
        {
            return masked;
        }

        if (original.Count != result.Count) return masked;
        foreach (string key in original.Keys)
        {
            var expected = SecretKeys.IsSecret(key) ? masked : original[key]?.ToString();
            if (!result.TryGetValue(key, out var value) || !string.Equals(value?.ToString(), expected, StringComparison.Ordinal))
                return masked;
        }
        return text;
    }

    /// <summary>key=value pairs as the ADO.NET grammar reads them ('==' in a key, quoted values with doubled quotes), secret values swapped.</summary>
    private static string ReplaceSecretValues(string cs, string masked)
    {
        var sb = new System.Text.StringBuilder(cs.Length);
        var i = 0;
        while (i < cs.Length)
        {
            var keyStart = i;
            while (i < cs.Length && cs[i] != ';')
            {
                if (cs[i] == '=')
                {
                    if (i + 1 < cs.Length && cs[i + 1] == '=') { i += 2; continue; }   // '==' is an '=' in the key
                    break;
                }
                i++;
            }
            sb.Append(cs, keyStart, i - keyStart);
            if (i >= cs.Length) break;
            if (cs[i] == ';')
            {
                sb.Append(';');
                i++;
                continue;
            }

            var key = cs[keyStart..i].Replace("==", "=").Trim();
            sb.Append('=');
            var valueStart = ++i;
            while (i < cs.Length && char.IsWhiteSpace(cs[i])) i++;
            if (i < cs.Length && cs[i] is '"' or '\'')
            {
                var quote = cs[i++];
                while (i < cs.Length)
                {
                    if (cs[i] == quote)
                    {
                        if (i + 1 < cs.Length && cs[i + 1] == quote) { i += 2; continue; }   // a doubled quote is one quote
                        i++;
                        break;
                    }
                    i++;
                }
            }
            while (i < cs.Length && cs[i] != ';') i++;

            if (SecretKeys.IsSecret(key)) sb.Append(masked);
            else sb.Append(cs, valueStart, i - valueStart);
        }
        return sb.ToString();
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
