using System.Net;

namespace SchemaScope.Api.Services;

/// <summary>
/// One spelling for one server, so a protected connection is matched however a target names its server:
/// "tcp:" / "np:" / "lpc:" / "admin:" and ",port" dropped, '.', "(local)", "localhost" and the loopback addresses read
/// as this machine, a DNS suffix dropped (SERVERPROPERTY('ServerName') has none), and the default instance's name
/// left out. Another machine's IP address is not resolved: it only matches the same address.
/// </summary>
public static class ServerNames
{
    public static string Normalise(string? server)
    {
        var s = (server ?? "").Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        if (s.IndexOf(',') is var comma and >= 0) s = s[..comma];

        var slash = s.IndexOf('\\');
        var host = (slash >= 0 ? s[..slash] : s).Trim();
        var instance = slash >= 0 ? s[(slash + 1)..].Trim() : "";

        if (host is "." or "(local)" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
            host = Environment.MachineName;
        else if (!IPAddress.TryParse(host.Trim('[', ']'), out _) && host.IndexOf('.') is var dot and > 0)
            host = host[..dot];

        if (instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)) instance = "";
        return (instance.Length > 0 ? $"{host}\\{instance}" : host).ToUpperInvariant();
    }

    public static bool Same(string? a, string? b) => Normalise(a) == Normalise(b);
}
