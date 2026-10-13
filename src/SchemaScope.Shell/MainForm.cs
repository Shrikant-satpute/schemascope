using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchemaScope.Shell;

/// <summary>The application window: a title bar and a WebView2 filling the rest.</summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly string _startUrl;
    private readonly string _origin;
    private CoreWebView2? _core;

    /// <summary>A second Browse click while the dialog is up must not stack a second dialog.</summary>
    private bool _pickerOpen;

    /// <param name="startUrl">Launch URL, carrying the API token as a query parameter.</param>
    /// <param name="origin">Bare loopback origin, used to police navigation and page messages.</param>
    public MainForm(string startUrl, string origin)
    {
        _startUrl = startUrl;
        _origin = origin.TrimEnd('/');

        Text = "SchemaScope";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 640);
        Size = new Size(1560, 940);
        // The page starts on the light theme, so the window does too - otherwise
        // there is a dark flash for the split second before the UI paints.
        BackColor = Color.FromArgb(244, 245, 248);
        Icon = AppIcon.Load();

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(244, 245, 248),
        };

        Controls.Add(_webView);
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: WebViewRuntime.UserDataFolder);

            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true; // keep Ctrl+F, F12
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            // Defence in depth alongside the server's CSP: refuse to navigate
            // anywhere except our own loopback origin, and open any external
            // link the user manages to click in their normal browser instead.
            core.NavigationStarting += (_, args) =>
            {
                if (!IsOwnPage(args.Uri))
                    args.Cancel = true;
            };

            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(args.Uri) { UseShellExecute = true });
                }
            };

            core.DocumentTitleChanged += (_, _) =>
            {
                var title = core.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(title) ? "SchemaScope" : title;
            };

            // The page's only way to ask the desktop for something (a folder picker, an
            // Explorer window). A message channel rather than an HTTP route, so no other
            // page on this machine can trigger it; it fires on the UI thread.
            _core = core;
            core.WebMessageReceived += OnWebMessage;

            core.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"SchemaScope could not start the embedded browser.\n\n{ex.Message}",
                "SchemaScope",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    // -----------------------------------------------------------------
    //  Page -> desktop messages
    //
    //  { id, type: "pickFolder", initial?, title? }  -> { id, type: "pickFolderResult", path }  (path null = cancelled)
    //  { id, type: "openFolder", path }      -> no reply; opens an existing folder in Explorer
    // -----------------------------------------------------------------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsDisposed || !IsOwnPage(e.Source)) return;

        JsonElement id;
        string? type, initial, path, title;
        try
        {
            // WebMessageAsJson, not TryGetWebMessageAsString: the page posts objects, and the
            // string accessor throws on those.
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // Echoed back unchanged, whatever its JSON type, so the page can match the reply.
            id = root.TryGetProperty("id", out var idValue) ? idValue.Clone() : default;
            type = ReadString(root, "type");
            initial = ReadString(root, "initial");
            title = ReadString(root, "title");
            path = ReadString(root, "path");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return;
        }

        switch (type)
        {
            case "pickFolder":
                PickFolder(id, initial, title);
                break;
            case "openFolder":
                OpenFolder(path);
                break;
        }
    }

    /// <summary>
    /// Our own loopback origin or a path under it. Compared against the bare origin, not the
    /// start URL, because the start URL carries the launch token and the page drops it from
    /// the address bar once read. A bare prefix test would also accept http://127.0.0.1:51234
    /// when we are on http://127.0.0.1:5123, hence the boundary check. A share link (a LAN
    /// address on the viewer port) never matches, so it can never reach this channel.
    /// </summary>
    private bool IsOwnPage(string? uri) =>
        uri is not null &&
        uri.StartsWith(_origin, StringComparison.OrdinalIgnoreCase) &&
        (uri.Length == _origin.Length || uri[_origin.Length] is '/' or '?' or '#');

    /// <summary>
    /// \\host\share and device paths. Even checking whether one exists makes Windows offer the
    /// user's NTLM credentials to that host, so a path from the page is refused before any IO.
    /// </summary>
    private static bool IsNetworkPath(string path) =>
        path.Trim().Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal);

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <param name="title">The picker's title, as the page words it ("Choose where to write the scripts" when absent).</param>
    private void PickFolder(JsonElement id, string? initial, string? title)
    {
        if (_pickerOpen)
        {
            Reply(id, null, busy: true);
            return;
        }
        _pickerOpen = true;

        // WebView2 event handlers are not reentrant: a modal loop inside one can hang the
        // control. BeginInvoke runs the dialog after this handler has returned.
        BeginInvoke(new Action(() =>
        {
            string? chosen = null;
            try
            {
                if (IsDisposed) return;

                using var dialog = new FolderBrowserDialog
                {
                    // the page's own wording, plain text and short; anything else falls back to the default
                    Description = !string.IsNullOrWhiteSpace(title) && title.Length <= 120 && !title.Any(char.IsControl)
                        ? title.Trim()
                        : "Choose where to write the scripts",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                };
                if (!string.IsNullOrWhiteSpace(initial) && Path.IsPathFullyQualified(initial) &&
                    !IsNetworkPath(initial) && Directory.Exists(initial))
                    dialog.InitialDirectory = initial;

                // Owned by the form, so it stays modal over the page instead of hiding behind it.
                if (dialog.ShowDialog(this) == DialogResult.OK) chosen = dialog.SelectedPath;
            }
            catch (Exception)
            {
                chosen = null; // a failed dialog reads to the page as "cancelled"
            }
            finally
            {
                _pickerOpen = false;
                Reply(id, chosen, busy: false);
            }
        }));
    }

    private void Reply(JsonElement id, string? path, bool busy)
    {
        if (IsDisposed || _core is null) return;

        object? echoed = id.ValueKind == JsonValueKind.Undefined ? null : id;
        var json = busy
            ? JsonSerializer.Serialize(new { id = echoed, type = "pickFolderResult", path, busy })
            : JsonSerializer.Serialize(new { id = echoed, type = "pickFolderResult", path });

        try
        {
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // The page or the control went away while the dialog was open; nobody is listening.
        }
    }

    /// <summary>
    /// Opens a folder in Explorer - and only a folder that exists. Explorer is started by its
    /// full path with the folder as a single argument and no shell execute, so a path from the
    /// page can never run or open a file, and a stray explorer.exe beside the app is never picked.
    /// </summary>
    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsNetworkPath(path)) return;

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }

        if (!Directory.Exists(full)) return;
        // Explorer splits its argument on commas (its "/select,x" syntax), so "D:\out\setup.exe,x" would act
        // on D:\out\setup.exe. A folder with a comma is not opened; the page still offers "Copy path".
        if (full.Contains(',')) return;

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(explorer)
            {
                ArgumentList = { full },
                UseShellExecute = false,
            });
        }
        catch (Win32Exception)
        {
            // Explorer could not start; the page still shows the path with a copy button.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_core is not null) _core.WebMessageReceived -= OnWebMessage;
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
