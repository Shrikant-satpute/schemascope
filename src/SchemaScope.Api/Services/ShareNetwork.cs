using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SchemaScope.Api.Services;

/// <summary>
/// The two practical questions a share raises: what address do I send people to,
/// and is Windows going to let them through.
/// </summary>
/// <summary>An address this machine answers on, and how much use it is to somebody else.</summary>
/// <param name="Value">The host part of the link.</param>
/// <param name="Kind">lan - a real network card | name - this computer's name | virtual - an adapter that exists only on this PC.</param>
public sealed record ShareAddress(string Value, string Kind);

/// <summary>A ready-made link, carrying the same warning about what it is.</summary>
public sealed record ShareLink(string Url, string Kind);

public static class ShareNetwork
{
    /// <summary>The name of the Windows firewall rule we manage. Fixed, so we never add a second one.</summary>
    public const string FirewallRuleName = "SchemaScope share";

    // The Share tab asks for all of this every few seconds. Enumerating adapters
    // is cheap but not free, and the firewall check starts a netsh process, which
    // is neither - so both answers are held briefly rather than worked out again
    // for every poll. Neither can change without the user doing something, and
    // the one thing they can do about the firewall clears the cache itself.
    private static readonly TimeSpan AddressCacheFor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FirewallCacheFor = TimeSpan.FromSeconds(30);
    private static readonly Lock CacheGate = new();

    private static List<ShareAddress>? _addresses;
    private static DateTime _addressesAtUtc = DateTime.MinValue;
    private static bool _firewallAllowed;
    private static int _firewallPort;
    private static DateTime _firewallAtUtc = DateTime.MinValue;

    /// <summary>
    /// Addresses this machine can be reached on, best first.
    ///
    /// The real network card leads. The computer name is only a name: unless the
    /// network runs a DNS server that knows this machine, or the other person is
    /// close enough on the network for a broadcast lookup, it resolves nowhere -
    /// which on a workgroup PC using public DNS is most of the time. A Hyper-V or
    /// WSL adapter goes last, because nobody outside this box can reach it.
    /// </summary>
    public static List<ShareAddress> Addresses()
    {
        lock (CacheGate)
        {
            if (_addresses is not null && DateTime.UtcNow - _addressesAtUtc < AddressCacheFor)
                return _addresses;
        }

        var found = ReadAddresses();

        lock (CacheGate)
        {
            _addresses = found;
            _addressesAtUtc = DateTime.UtcNow;
        }

        return found;
    }

    private static List<ShareAddress> ReadAddresses()
    {
        List<ShareAddress> real = [];
        List<ShareAddress> virtualOnly = [];

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel))
            {
                var isVirtual = IsVirtual(adapter);

                foreach (var ip in adapter.GetIPProperties().UnicastAddresses
                             .Select(a => a.Address)
                             .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                             // 169.254.x.x means the machine asked for an address
                             // and never got one. Nobody else can reach it.
                             .Where(a => !a.ToString().StartsWith("169.254", StringComparison.Ordinal)))
                {
                    (isVirtual ? virtualOnly : real).Add(new ShareAddress(ip.ToString(), isVirtual ? "virtual" : "lan"));
                }
            }
        }
        catch
        {
            // No usable adapter list. The machine name alone is still worth having.
        }

        return
        [
            .. real.DistinctBy(a => a.Value),
            new ShareAddress(Environment.MachineName, "name"),
            .. virtualOnly.DistinctBy(a => a.Value)
        ];
    }

    /// <summary>
    /// A best guess at the adapters that only exist inside this machine. Getting
    /// it wrong only changes the order they are offered in, never the list.
    /// </summary>
    private static bool IsVirtual(NetworkInterface adapter)
    {
        string[] giveaways = ["hyper-v", "virtual", "vmware", "virtualbox", "vethernet", "wsl", "tap", "loopback"];
        var description = $"{adapter.Description} {adapter.Name}".ToLowerInvariant();
        return giveaways.Any(description.Contains);
    }

    /// <summary>
    /// Whether our inbound rule is in place for this port and this exe. A missing
    /// rule is the single most common reason a link works on this machine and
    /// nowhere else. A rule of the right name is not enough: one added by another
    /// copy of SchemaScope (a dev build, an older install) is pinned to that copy's
    /// path or port and lets nothing through to this one.
    /// </summary>
    public static bool FirewallAllows(int port)
    {
        if (!OperatingSystem.IsWindows()) return true;

        lock (CacheGate)
        {
            if (_firewallPort == port && DateTime.UtcNow - _firewallAtUtc < FirewallCacheFor)
                return _firewallAllowed;
        }

        // Deliberately outside the lock: netsh takes long enough that holding one
        // would queue every other caller behind it.
        var allowed = AskNetshAboutRule(port);

        lock (CacheGate)
        {
            _firewallAllowed = allowed;
            _firewallPort = port;
            _firewallAtUtc = DateTime.UtcNow;
        }

        return allowed;
    }

    /// <summary>Drops the remembered answer, so the next ask really goes and looks.</summary>
    public static void ForgetFirewallState()
    {
        lock (CacheGate) _firewallAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// netsh by its full System32 path. A bare "netsh" would be looked up on PATH and in the
    /// working directory, and the add-rule call runs elevated - so a planted netsh.exe would
    /// be handed administrator rights by the user's own UAC click.
    /// </summary>
    private static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");

    private static bool AskNetshAboutRule(int port) =>
        ReadOurRules() is { } output && RuleCovers(output, port, ProgramForRule);

    /// <summary>netsh's verbose listing of every rule with our name, or null when there is none.</summary>
    private static string? ReadOurRules()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Netsh)
            {
                Arguments = $"advfirewall firewall show rule name=\"{FirewallRuleName}\" verbose",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null) return null;

            // Read while it runs: a full pipe would otherwise stall netsh until the timeout.
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) return null;
            return output.Wait(1000) ? output.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether one of the rules in a verbose netsh listing opens <paramref name="port"/> to
    /// <paramref name="program"/>. netsh translates its labels ("LocalPort:", "Program:") but
    /// never the values, so each rule is matched on its values alone: one must be the port,
    /// and - when we know our own path - one must be that path. A rule pinned to another exe,
    /// or opening another port, does not count.
    /// </summary>
    internal static bool RuleCovers(string netshOutput, int port, string? program)
    {
        var wantedPort = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        foreach (var rule in netshOutput.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var values = rule.Split('\n')
                .Select(line => line.IndexOf(':') is var colon and >= 0 ? line[(colon + 1)..].Trim() : "")
                .Where(v => v.Length > 0)
                .ToList();

            if (!values.Contains(wantedPort, StringComparer.Ordinal)) continue;
            if (program is null || values.Contains(program, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Adds the inbound rule. Needs administrator rights, so Windows puts up its
    /// own prompt - we never try to work around that.
    /// </summary>
    public static (bool Ok, string Message) AllowThroughFirewall(int port)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "This only applies to Windows.");

        if (port is < 1 or > 65535)
            return (false, "That is not a valid port.");

        // One UAC prompt for the lot: clear out any earlier copy of our rule (netsh
        // happily adds a second with the same name) and any rule Windows made for
        // this exe from its own "allow access" prompt - broad, any port and any
        // address, or a Block that would override ours - then add the narrow one.
        // The exit code is the add's, which is the one that matters.
        var (ok, _) = RunElevated([.. CleanupArguments, AddRuleArguments(port)]);
        return ok
            ? (true, $"Port {port} is now allowed through Windows Firewall on private and domain networks.")
            : (false, "The firewall rule was not added. You can add it yourself from an admin command prompt.");
    }

    /// <summary>Takes our rule away again. The rule outlives the app, so this has to exist.</summary>
    public static (bool Ok, string Message) RemoveFirewallRule()
    {
        if (!OperatingSystem.IsWindows())
            return (false, "This only applies to Windows.");

        // Deleting a rule that is not there "fails", so the exit code of the last
        // delete says little. Whether our rule is still listed afterwards says it all.
        var (ran, _) = RunElevated(CleanupArguments);
        var ok = ran || ReadOurRules() is null;
        return ok
            ? (true, "The SchemaScope firewall rule has been removed.")
            : (false, "The firewall rule was not removed. You can remove it yourself from an admin command prompt.");
    }

    /// <summary>
    /// Every inbound rule that is ours to remove: the named one, and any rule Windows made
    /// for this exe by path. Nothing that names another program is touched.
    /// </summary>
    internal static string[] CleanupArguments => ProgramForRule is { } program
        ? [RemoveRuleArguments, $"advfirewall firewall delete rule name=all dir=in program=\"{program}\""]
        : [RemoveRuleArguments];

    /// <summary>
    /// Runs one or more netsh commands elevated, under a single UAC prompt. More than one
    /// goes through cmd.exe (by its System32 path, for the same reason as netsh), chained
    /// with &amp; so each runs whatever the one before returned.
    /// </summary>
    private static (bool Ok, int ExitCode) RunElevated(string[] commands)
    {
        var (file, arguments) = commands.Length == 1
            ? (Netsh, commands[0])
            : (Path.Combine(Environment.SystemDirectory, "cmd.exe"),
               "/d /s /c \"" + string.Join(" & ", commands.Select(c => $"\"{Netsh}\" {c}")) + "\"");

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file)
            {
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                // CreateNoWindow is ignored with shell execute; this keeps cmd.exe out of sight.
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null) return (false, -1);

            process.WaitForExit(60_000);

            // Whatever just happened, the remembered answer is now out of date.
            ForgetFirewallState();
            return (process.ExitCode == 0, process.ExitCode);
        }
        catch
        {
            // Most often: the user said no to the UAC prompt.
            ForgetFirewallState();
            return (false, -1);
        }
    }

    /// <summary>
    /// This exe's path as a firewall rule names it, or null when it cannot be passed safely:
    /// a quote would end the argument, and cmd.exe would expand a %.
    /// </summary>
    private static string? ProgramForRule =>
        Environment.ProcessPath is { Length: > 0 } path && !path.Contains('"') && !path.Contains('%')
            ? path
            : null;

    /// <summary>
    /// As narrow as a useful rule can be: private and domain networks only (never a café's
    /// public Wi-Fi), callers on the local subnet only, and only this program, so another
    /// program binding the same port does not get through. A per-user install puts that
    /// program in the user's own profile, so this narrows the rule; it is no wall against
    /// something already running as that user.
    /// </summary>
    internal static string AddRuleArguments(int port)
    {
        var programPart = ProgramForRule is { } program ? $" program=\"{program}\"" : "";

        return $"advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
               $"dir=in action=allow protocol=TCP localport={port} " +
               $"profile=private,domain remoteip=localsubnet{programPart}";
    }

    internal static string RemoveRuleArguments =>
        $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"";

    /// <summary>Shown in the UI so nobody has to take the button on trust.</summary>
    public static string FirewallCommand(int port) => $"netsh {AddRuleArguments(port)}";
}
