using System.Data.Common;

namespace SchemaScope.Api.Services;

/// <summary>
/// Last line of defence for "connection strings and passwords never appear": a driver or
/// IO message that echoed one back is masked before it reaches a browser, a log, or a
/// shared link.
/// </summary>
internal static class SecretScrubber
{
    public const string Mask = "********";

    /// <summary>
    /// Each whole connection string, plus any password inside it. Longest first, so a
    /// password never splits the masking of the full string.
    /// </summary>
    public static List<string> Secrets(IEnumerable<string?> connectionStrings)
    {
        var secrets = new List<string>();
        foreach (var cs in connectionStrings)
        {
            if (string.IsNullOrEmpty(cs)) continue;
            secrets.Add(cs);

            var parsed = new DbConnectionStringBuilder();
            try { parsed.ConnectionString = cs; }
            catch (ArgumentException) { continue; }

            foreach (var key in new[] { "Password", "Pwd" })
            {
                if (parsed.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } password)
                    secrets.Add(password);
            }
        }
        return [.. secrets.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length)];
    }

    public static string Scrub(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        return text;
    }
}
