using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SchemaScope.Api;

namespace SchemaScope.Shell;

/// <summary>
/// The desktop entry point.
///
/// SchemaScope is one exe: it starts its own web server on a private loopback
/// port, then shows that page in a WebView2 window. Nothing listens on a public
/// interface and nothing is fetched from the internet.
///
/// Run with --server to start the API alone on a fixed port. That is what the
/// Vite dev server proxies to while working on the front end.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--server", StringComparer.OrdinalIgnoreCase))
        {
            RunHeadless(args);
            return;
        }

        RunDesktop(args);
    }

    private static void RunDesktop(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (!WebViewRuntime.IsInstalled(out var runtimeError))
        {
            WebViewRuntime.ShowMissingRuntimeDialog(runtimeError);
            return;
        }

        var url = $"http://127.0.0.1:{FindFreeLoopbackPort()}";

        // Minted here and handed to the window in its launch URL. Nothing else
        // on the machine ever learns it, which is what keeps another local
        // process - or a web page using DNS rebinding - out of the API.
        var auth = LocalAuth.Mint();

        var app = SchemaScopeApi.Build(args, desktop: true, auth);
        app.Urls.Clear();
        app.Urls.Add(url);

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"SchemaScope could not start its local server.\n\n{ex.Message}",
                "SchemaScope", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (var form = new MainForm($"{url}/?{LocalAuth.QueryName}={auth.Token}", url))
        {
            Application.Run(form);
        }

        try
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            app.StopAsync(shutdown.Token).GetAwaiter().GetResult();
        }
        catch
        {
            // shutting down anyway
        }
    }

    /// <summary>Server only, for front end development. No window.</summary>
    private static void RunHeadless(string[] args)
    {
        AttachConsole(AttachParentProcess);

        var port = 5199;
        var portIndex = Array.FindIndex(args, a => a.Equals("--port", StringComparison.OrdinalIgnoreCase));
        if (portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsed))
            port = parsed;

        // The Vite dev proxy adds the matching header to everything it forwards,
        // so development uses a fixed token rather than a minted one.
        var app = SchemaScopeApi.Build(args, desktop: true, new LocalAuth(LocalAuth.DevToken));
        app.Urls.Clear();
        app.Urls.Add($"http://127.0.0.1:{port}");

        Console.WriteLine($"SchemaScope API listening on http://127.0.0.1:{port}  (Ctrl+C to stop)");
        app.Run();
    }

    /// <summary>
    /// Asks the OS for a free port on loopback. Binding to port 0 and reading
    /// back what we got beats guessing a port that is already in use.
    /// </summary>
    private static int FindFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // A WinExe has no console of its own. Borrow the one that launched us so
    // --server can actually report where it is listening.
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
