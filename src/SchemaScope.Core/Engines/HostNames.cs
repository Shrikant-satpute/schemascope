using System.Net;

namespace SchemaScope.Core.Engines;

/// <summary>
/// One spelling for one machine, whatever the engine: '.', "(local)", "localhost" and the loopback addresses read as
/// this machine, a DNS suffix is dropped, and the result is upper case. Another machine's IP address is not resolved:
/// it only matches the same address. Ports and instances are the caller's to split off first.
/// </summary>
public static class HostNames
{
    public static string Normalise(string? host)
    {
        var h = (host ?? "").Trim();
        if (h is "." or "(local)" || h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(h.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
            h = Environment.MachineName;
        else if (!IPAddress.TryParse(h.Trim('[', ']'), out _) && h.IndexOf('.') is var dot and > 0)
            h = h[..dot];
        return h.ToUpperInvariant();
    }

    public static bool Same(string? a, string? b) => Normalise(a) == Normalise(b);
}
