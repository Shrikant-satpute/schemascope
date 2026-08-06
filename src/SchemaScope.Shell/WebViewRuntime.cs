using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace SchemaScope.Shell;

/// <summary>
/// WebView2 ships with Windows 10 and 11, so this almost never fires. When it
/// does, the user gets a plain explanation and a download link instead of a
/// stack trace.
/// </summary>
internal static class WebViewRuntime
{
    public static bool IsInstalled(out string? error)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            error = null;
            return !string.IsNullOrEmpty(version);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void ShowMissingRuntimeDialog(string? error)
    {
        const string url = "https://developer.microsoft.com/microsoft-edge/webview2/";

        var result = MessageBox.Show(
            "SchemaScope needs the Microsoft Edge WebView2 runtime, which is not installed on this PC.\n\n" +
            "It is a free Microsoft component and comes with Windows 10 and 11 by default.\n\n" +
            "Open the download page now?" +
            (string.IsNullOrEmpty(error) ? "" : $"\n\nDetail: {error}"),
            "SchemaScope",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Where WebView2 keeps its cache. Kept beside our own data, not in Temp.</summary>
    public static string UserDataFolder
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SchemaScope",
                "WebView2");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
