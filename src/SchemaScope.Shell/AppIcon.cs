namespace SchemaScope.Shell;

/// <summary>
/// The window icon: three schema rows lined up, and the one that drifted.
/// Read from the same .ico that is baked into the exe, so the title bar, the
/// taskbar and the desktop shortcut all show one mark.
/// </summary>
internal static class AppIcon
{
    public static Icon? Load()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly
                .GetManifestResourceStream("SchemaScope.Shell.schemascope.ico");

            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}
