using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SchemaScope.Shell;

/// <summary>The application window: a title bar and a WebView2 filling the rest.</summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly string _startUrl;
    private readonly string _origin;

    /// <param name="startUrl">Launch URL, carrying the API token as a query parameter.</param>
    /// <param name="origin">Bare loopback origin, used to police navigation.</param>
    public MainForm(string startUrl, string origin)
    {
        _startUrl = startUrl;
        _origin = origin;

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
                if (!args.Uri.StartsWith(_origin, StringComparison.OrdinalIgnoreCase))
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _webView.Dispose();
        base.Dispose(disposing);
    }
}
