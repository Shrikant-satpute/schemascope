using System.Data.Common;
using System.Text.RegularExpressions;

namespace SchemaScope.Core.Engines;

/// <summary>
/// Reads where a key=value connection string goes, for each engine, without any driver: Core carries none, and the
/// store has to apply the secret-release rule on its own. Deliberately strict - a string it cannot read, a URI-form
/// string, or a string written for another engine's driver gives null, and every caller treats null as "unknown
/// destination": no stored secret is released and the Protected rule refuses.
/// </summary>
public static partial class ConnectionEndpoints
{
    /// <summary>
    /// postgres://user:pass@host/db and friends, jdbc:postgresql://... too: refused at input, since the password sits
    /// in the host part.
    /// </summary>
    public static bool IsUri(string? connectionString) =>
        connectionString is not null && UriForm().IsMatch(connectionString);

    [GeneratedRegex(@"^\s*[A-Za-z][A-Za-z0-9+.:\-]*://", RegexOptions.CultureInvariant)]
    private static partial Regex UriForm();

    /// <summary>The password in a URI-form string's user:password@ part, still percent-encoded; null when there is none.</summary>
    public static string? UriPassword(string connectionString)
    {
        var start = connectionString.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return null;
        var rest = connectionString[(start + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var authority = end >= 0 ? rest[..end] : rest;
        var at = authority.LastIndexOf('@');
        if (at < 0) return null;
        var colon = authority.IndexOf(':');
        return colon >= 0 && colon + 1 < at ? authority[(colon + 1)..at] : null;
    }

    public static EndpointInfo? Parse(DatabaseEngine engine, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || IsUri(connectionString)) return null;

        var parsed = new DbConnectionStringBuilder();
        try
        {
            parsed.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var keys = parsed.Keys.Cast<string>().Select(k => k.Trim().ToLowerInvariant()).ToHashSet();
        string? Value(params string[] names)
        {
            foreach (var name in names)
                if (parsed.TryGetValue(name, out var v) && v?.ToString() is { } text && text.Trim().Length > 0)
                    return text.Trim();
            return null;
        }

        return engine switch
        {
            DatabaseEngine.SqlServer => keys.Overlaps(NotSqlClient) ? null : SqlServer(Value),
            DatabaseEngine.PostgreSql => keys.Overlaps(NotNpgsql) ? null : HostList(engine, Value("host", "server"), Value("port"),
                Value("database", "db") ?? ""),
            DatabaseEngine.MySql or DatabaseEngine.MariaDb => keys.Overlaps(NotMySql) ? null : HostList(engine,
                Value("server", "host", "data source", "datasource", "address", "addr", "network address"), Value("port"),
                Value("database", "initial catalog") ?? ""),
            _ => null
        };
    }

    // Keys that only another engine's driver takes. SqlClient itself throws on unknown keywords before it connects;
    // these lists make the same refusal here, where there is no driver to ask.
    private static readonly HashSet<string> NotSqlClient =
        ["host", "port", "username", "user name", "sslmode", "ssl mode", "ssl password", "passfile", "search path",
         "searchpath", "sslcert", "ssl certificate", "sslkey", "ssl key", "root certificate", "target session attributes",
         "allowuservariables", "allow user variables", "allowloadlocalinfile", "allow load local infile"];

    private static readonly HashSet<string> NotNpgsql =
        ["data source", "initial catalog", "address", "addr", "network address", "trusted_connection", "failover partner",
         "allowuservariables", "allow user variables", "allowloadlocalinfile", "allow load local infile"];

    private static readonly HashSet<string> NotMySql =
        ["integrated security", "trusted_connection", "failover partner", "passfile", "search path", "searchpath",
         "target session attributes", "root certificate"];

    private static EndpointInfo? SqlServer(Func<string[], string?> value)
    {
        var dataSource = value(["data source", "server", "address", "addr", "network address"]);
        if (dataSource is null) return null;
        var main = SqlServerHost(dataSource);
        if (main is null) return null;

        var hosts = new List<string> { main.Value.Host };
        // a failover partner is somewhere the password is sent too
        if (value(["failover partner"]) is { } partner)
        {
            if (SqlServerHost(partner) is not { } p) return null;
            hosts.Add(p.Host);
        }
        return new EndpointInfo(DatabaseEngine.SqlServer, hosts, main.Value.Port, main.Value.Instance,
            value(["initial catalog", "database"]) ?? "", "");
    }

    /// <summary>"tcp:host\instance,port", "np:\\host\pipe\...", "(localdb)\name": the host, instance and port.</summary>
    private static (string Host, string? Instance, int? Port)? SqlServerHost(string dataSource)
    {
        var s = dataSource.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..].Trim(); break; }

        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var pipeHost = s[2..].Split('\\')[0].Trim();
            return pipeHost.Length == 0 ? null : (pipeHost, null, null);
        }

        int? port = null;
        if (s.IndexOf(',') is var comma and >= 0)
        {
            if (!int.TryParse(s[(comma + 1)..].Trim(), out var p) || p is <= 0 or > 65535) return null;
            port = p;
            s = s[..comma];
        }
        var slash = s.IndexOf('\\');
        var host = (slash >= 0 ? s[..slash] : s).Trim();
        var instance = slash >= 0 ? s[(slash + 1)..].Trim() : null;
        if (host.Length == 0) return null;
        return (host, string.IsNullOrEmpty(instance) ? null : instance, port);
    }

    /// <summary>"a,b:5433" plus an optional Port key: every host, and the one port (a per-host port must agree with it).</summary>
    private static EndpointInfo? HostList(DatabaseEngine engine, string? hostValue, string? portValue, string database)
    {
        if (hostValue is null) return null;
        int? port = null;
        if (portValue is not null)
        {
            if (!int.TryParse(portValue, out var p) || p is <= 0 or > 65535) return null;
            port = p;
        }

        var hosts = new List<string>();
        foreach (var part in hostValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var host = part;
            // host:port, but not a bare IPv6 address; [::1]:5432 is
            var colon = part.LastIndexOf(':');
            if (colon > 0 && (part.IndexOf(':') == colon || part.StartsWith('[')) && !part.EndsWith(']'))
            {
                if (!int.TryParse(part[(colon + 1)..], out var p) || p is <= 0 or > 65535) return null;
                if (port is not null && port != p) return null;   // two ports for one string: refuse rather than guess
                port = p;
                host = part[..colon];
            }
            host = host.Trim('[', ']').Trim();
            if (host.Length == 0) return null;
            hosts.Add(host);
        }
        return hosts.Count == 0 ? null : new EndpointInfo(engine, hosts, port, null, database, "");
    }
}

/// <summary>The one rule for which connection string keys hold a secret, for masking and scrubbing alike.</summary>
public static class SecretKeys
{
    /// <summary>Password, PWD, SSL Password, CertificatePassword, Passphrase, Client Secret, Passfile, ...</summary>
    public static bool IsSecret(string key)
    {
        var k = key.Trim();
        return k.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               k.Contains("pwd", StringComparison.OrdinalIgnoreCase) ||
               k.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
               k.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("passfile", StringComparison.OrdinalIgnoreCase);
    }
}
