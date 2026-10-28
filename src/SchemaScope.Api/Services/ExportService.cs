using System.Globalization;
using System.Net;
using System.Text;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Api.Services;

/// <summary>
/// Turns a finished compare into something you can send to someone else:
/// a standalone HTML report, a spreadsheet, wiki-ready Markdown, or a folder
/// of .sql files.
/// </summary>
public sealed class ExportService
{
    private static readonly string[] StatusLabels =
        ["same", "formatting", "different", "missing", "extra", "unreadable"];

    private static IEnumerable<CompareRow> Filter(CompareResult result, ExportRequest request)
    {
        var rows = result.Rows.AsEnumerable();
        if (request.DifferencesOnly) rows = rows.Where(r => r.HasDifference);
        if (request.Keys.Count > 0)
        {
            var keys = request.Keys.ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(r => keys.Contains(r.Key));
        }
        return rows;
    }

    // ---------------- CSV ----------------

    public string BuildCsv(CompareResult result, ExportRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("Type,Schema,Name");
        foreach (var t in result.Targets) sb.Append(',').Append(Csv(t.Label));
        foreach (var t in result.Targets) sb.Append(',').Append(Csv(t.Label + " changed lines"));
        sb.AppendLine();

        foreach (var row in Filter(result, request))
        {
            sb.Append(Csv(row.TypeLabel)).Append(',')
              .Append(Csv(row.Schema)).Append(',')
              .Append(Csv(row.Name));

            foreach (var c in row.Cells) sb.Append(',').Append(Label(c.Status));
            foreach (var c in row.Cells) sb.Append(',').Append(c.ChangedLines);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string Label(ObjectStatus s) => StatusLabels[(int)s];

    /// <summary>"PostgreSQL 16.4": the engine and its version, for engines other than SQL Server (whose reports are unchanged).</summary>
    private static string EngineText(DatabaseEngine engine, string? version) =>
        string.IsNullOrWhiteSpace(version) ? ConnectionRules.NameOf(engine) : $"{ConnectionRules.NameOf(engine)} {version}";

    /// <summary>host:port, or the host alone when no port is known.</summary>
    private static string HostPort(string server, int? port) => port is { } p ? $"{server}:{p}" : server;

    // ---------------- Markdown ----------------

    public string BuildMarkdown(CompareResult result, ExportRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Schema comparison - {result.Source.Label}");
        sb.AppendLine();
        sb.AppendLine($"- Run: {result.StartedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine(result.Source.Engine == DatabaseEngine.SqlServer
            ? $"- Source: `{result.Source.Server}` / `{result.Source.Database}` ({result.Source.ObjectCount} objects)"
            : $"- Source: {EngineText(result.Source.Engine, result.Source.ProductVersion)} `{HostPort(result.Source.Server, result.Source.Port)}` / " +
              $"`{result.Source.Database}` ({result.Source.ObjectCount} objects)");
        sb.AppendLine($"- Compared in {result.TotalMs} ms");
        sb.AppendLine();

        sb.AppendLine("## Drift by target");
        sb.AppendLine();
        sb.AppendLine("| Target | Match | Same | Formatting | Different | Missing | Extra |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var t in result.Targets)
        {
            // another engine's target also says where it is: two databases of one name on one host differ by port
            var label = t.Engine == DatabaseEngine.SqlServer ? t.Label
                : $"{t.Label} ({EngineText(t.Engine, t.ProductVersion)} `{HostPort(t.Server, t.Port)}` / `{t.Database}`)";
            if (t.Failed) { sb.AppendLine($"| {label} | FAILED | | | | | |"); continue; }
            sb.AppendLine($"| {label} | {t.MatchPercent:0.0}% | {t.Same} | {t.FormattingOnly} | " +
                          $"{t.Different} | {t.MissingInTarget} | {t.OnlyInTarget} |");
        }
        sb.AppendLine();

        var rows = Filter(result, request).ToList();
        sb.AppendLine($"## Differences ({rows.Count})");
        sb.AppendLine();
        sb.Append("| Type | Object |");
        foreach (var t in result.Targets) sb.Append(' ').Append(t.Label).Append(" |");
        sb.AppendLine();
        sb.Append("|---|---|");
        foreach (var _ in result.Targets) sb.Append("---|");
        sb.AppendLine();

        foreach (var row in rows)
        {
            sb.Append("| ").Append(row.TypeLabel).Append(" | `").Append(row.FullName).Append("` |");
            foreach (var c in row.Cells) sb.Append(' ').Append(Symbol(c)).Append(" |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Symbol(TargetCell c) => c.Status switch
    {
        ObjectStatus.Same => "=",
        ObjectStatus.FormattingOnly => "~",
        ObjectStatus.Different => $"≠ {c.ChangedLines}",
        ObjectStatus.MissingInTarget => "missing",
        ObjectStatus.OnlyInTarget => "extra",
        _ => "?"
    };

    // ---------------- HTML ----------------

    public string BuildHtml(CompareResult result, ExportRequest request)
    {
        var rows = Filter(result, request).ToList();
        var sb = new StringBuilder(64 * 1024);

        sb.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>SchemaScope report</title>
            <style>
              :root{--bg:#f7f8fa;--panel:#fff;--line:#e3e6ec;--text:#1a1d26;--muted:#6b7280;
                    --same:#0b9070;--fmt:#3b6fb5;--diff:#b57500;--miss:#c22f2f;--extra:#4a3aa7}
              @media (prefers-color-scheme:dark){
                :root{--bg:#0f1117;--panel:#171a23;--line:#242835;--text:#e6e8ef;--muted:#8b91a5;
                      --same:#3fb950;--fmt:#58a6ff;--diff:#d29922;--miss:#f85149;--extra:#bc8cff}}
              *{box-sizing:border-box}
              body{margin:0;padding:32px;background:var(--bg);color:var(--text);
                   font:14px/1.5 -apple-system,Segoe UI,Roboto,sans-serif}
              h1{font-size:22px;margin:0 0 4px}
              .sub{color:var(--muted);margin-bottom:24px}
              .cards{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:28px}
              .card{background:var(--panel);border:1px solid var(--line);border-radius:10px;
                    padding:14px 16px;min-width:190px}
              .card h3{margin:0 0 8px;font-size:13px;font-weight:600}
              .pct{font-size:26px;font-weight:600;margin-bottom:6px}
              .bar{height:6px;border-radius:3px;background:var(--line);overflow:hidden;margin-bottom:8px}
              .bar span{display:block;height:100%;background:var(--same)}
              .chips{display:flex;gap:10px;flex-wrap:wrap;font-size:12px;color:var(--muted)}
              .wrap{overflow-x:auto;background:var(--panel);border:1px solid var(--line);border-radius:10px}
              table{border-collapse:collapse;width:100%;font-size:13px}
              th,td{padding:7px 12px;text-align:left;border-bottom:1px solid var(--line);white-space:nowrap}
              th{position:sticky;top:0;background:var(--panel);font-weight:600;font-size:12px;
                 text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
              td.obj{font-family:ui-monospace,Consolas,monospace;white-space:normal}
              .t{font-weight:600;font-size:12px}
              .s-same{color:var(--same)} .s-fmt{color:var(--fmt)} .s-diff{color:var(--diff)}
              .s-miss{color:var(--miss)} .s-extra{color:var(--extra)} .s-na{color:var(--muted)}
              .warn{background:#fdf3d8;border:1px solid #e0c377;border-radius:8px;padding:10px 14px;
                    margin-bottom:16px;font-size:13px;color:#5c4408}
              @media (prefers-color-scheme:dark){
                .warn{background:#3a2a10;border-color:#7a5a1a;color:var(--text)}}
              footer{margin-top:24px;color:var(--muted);font-size:12px}
            </style></head><body>
            """);

        sb.Append("<h1>Schema comparison</h1>");
        sb.Append($"<div class=\"sub\">Source <b>{H(result.Source.Label)}</b> " +
                  (result.Source.Engine == DatabaseEngine.SqlServer
                      ? $"({H(result.Source.Server)} / {H(result.Source.Database)}) &middot; "
                      : $"({H(EngineText(result.Source.Engine, result.Source.ProductVersion))}, {H(HostPort(result.Source.Server, result.Source.Port))} / " +
                        $"{H(result.Source.Database)}) &middot; ") +
                  $"{result.Source.ObjectCount} objects &middot; " +
                  $"{result.StartedUtc:yyyy-MM-dd HH:mm} UTC &middot; compared in {result.TotalMs} ms</div>");

        foreach (var w in result.Warnings)
            sb.Append($"<div class=\"warn\">{H(w)}</div>");

        sb.Append("<div class=\"cards\">");
        foreach (var t in result.Targets)
        {
            sb.Append("<div class=\"card\">");
            sb.Append($"<h3>{H(t.Label)}</h3>");
            if (t.Engine != DatabaseEngine.SqlServer)
                sb.Append($"<div class=\"chips\">{H(EngineText(t.Engine, t.ProductVersion))}, {H(HostPort(t.Server, t.Port))} / {H(t.Database)}</div>");
            if (t.Failed)
            {
                sb.Append($"<div class=\"s-miss\">Could not be read</div><div class=\"chips\">{H(t.Error ?? "")}</div>");
            }
            else
            {
                sb.Append($"<div class=\"pct\">{t.MatchPercent.ToString("0.0", CultureInfo.InvariantCulture)}%</div>");
                sb.Append($"<div class=\"bar\"><span style=\"width:{t.MatchPercent.ToString("0.0", CultureInfo.InvariantCulture)}%\"></span></div>");
                sb.Append("<div class=\"chips\">");
                sb.Append($"<span class=\"s-diff\">{t.Different} different</span>");
                sb.Append($"<span class=\"s-miss\">{t.MissingInTarget} missing</span>");
                sb.Append($"<span class=\"s-extra\">{t.OnlyInTarget} extra</span>");
                if (t.FormattingOnly > 0) sb.Append($"<span class=\"s-fmt\">{t.FormattingOnly} formatting</span>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }
        sb.Append("</div>");

        sb.Append($"<div class=\"sub\">{rows.Count} object(s) listed</div>");
        sb.Append("<div class=\"wrap\"><table><thead><tr><th>Type</th><th>Object</th>");
        foreach (var t in result.Targets) sb.Append($"<th>{H(t.Label)}</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var row in rows)
        {
            sb.Append("<tr>");
            sb.Append($"<td class=\"t\">{H(row.TypeLabel)}</td>");
            sb.Append($"<td class=\"obj\">{H(row.FullName)}</td>");
            foreach (var c in row.Cells)
                sb.Append($"<td class=\"{CssFor(c.Status)}\">{H(Symbol(c))}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>");
        sb.Append("<footer>Generated by SchemaScope. This report was produced locally; nothing was uploaded anywhere.</footer>");
        sb.Append("</body></html>");

        return sb.ToString();
    }

    private static string CssFor(ObjectStatus s) => s switch
    {
        ObjectStatus.Same => "s-same",
        ObjectStatus.FormattingOnly => "s-fmt",
        ObjectStatus.Different => "s-diff",
        ObjectStatus.MissingInTarget => "s-miss",
        ObjectStatus.OnlyInTarget => "s-extra",
        _ => "s-na"
    };

    private static string H(string s) => WebUtility.HtmlEncode(s);

    // ---------------- .sql files ----------------

    /// <summary>
    /// Writes one .sql file per object into Type subfolders. Handy for dropping
    /// straight into a schema project or a git repo.
    /// </summary>
    public ExportResult WriteSqlFiles(CompareSession session, ExportRequest request)
    {
        if (session.Result is null || session.Source is null)
            return new ExportResult { Success = false, Message = "That run has no result yet." };

        if (string.IsNullOrWhiteSpace(request.OutputFolder))
            return new ExportResult { Success = false, Message = "Choose a folder to write the files into." };

        if (!IsWritableLocation(request.OutputFolder, out var root, out var refusal))
            return new ExportResult { Success = false, Message = refusal };

        // the session's index, keyed as the rows are (two objects with one key do not throw here)
        var byKey = request.TargetIndex is int ti && ti >= 0 && ti < session.Targets.Count
            ? session.TargetIndexes[ti]
            : session.SourceIndex;
        Directory.CreateDirectory(root);

        var written = 0;
        // every file once: PostgreSQL overloads (one name, other argument types) and names that differ only by letter case
        // (two objects there, one file name on Windows) each get a file of their own
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Filter(session.Result, request))
        {
            if (!byKey.TryGetValue(row.Key, out var obj)) continue;
            if (string.IsNullOrWhiteSpace(obj.DisplayText)) continue;

            var folder = Path.Combine(root, Safe(row.TypeLabel));
            Directory.CreateDirectory(folder);

            var stem = Safe(row.Signature is { Length: > 0 } sig ? $"{row.Schema}.{row.Name}({sig})" : $"{row.Schema}.{row.Name}");
            var file = Path.Combine(folder, stem + ".sql");
            for (var n = 2; !used.Add(file); n++) file = Path.Combine(folder, $"{stem} ({n}).sql");
            File.WriteAllText(file, obj.DisplayText, new UTF8Encoding(true));
            written++;
        }

        return new ExportResult
        {
            Success = true,
            Path = root,
            FileCount = written,
            Message = $"Wrote {written} file(s) to {root}"
        };
    }

    /// <summary>
    /// Folders that writing into would be an attack rather than an export.
    /// Startup is the obvious one - a .sql file there is harmless, but the
    /// endpoint that put it there would not be.
    /// </summary>
    private static readonly Environment.SpecialFolder[] OffLimits =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86,
        Environment.SpecialFolder.Startup,
        Environment.SpecialFolder.CommonStartup,
    ];

    /// <summary>
    /// The folder comes in on the request, so it is checked rather than
    /// trusted. Anything absolute is fine except a system location, which is
    /// only ever named by something trying to drop a file where it will be
    /// picked up rather than read.
    /// </summary>
    internal static bool IsWritableLocation(string folder, out string root, out string? refusal)
    {
        root = "";
        refusal = null;

        var trimmed = folder.Trim();

        // A \\host\share path would leave the machine, and Windows offers the
        // user's NTLM credentials to that host the moment it is probed - before
        // anything has been written. Every route that writes files comes through
        // here, so it is refused here, ahead of the first Directory call.
        if (IsNetworkPath(trimmed))
        {
            refusal = "Choose a folder on this computer - files are not written to network paths.";
            return false;
        }

        // Checked before GetFullPath, which would happily resolve a relative
        // path against whatever the working directory happens to be.
        if (!Path.IsPathRooted(trimmed))
        {
            refusal = "Choose a full folder path, for example D:\\schema-export.";
            return false;
        }

        try
        {
            root = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = "That does not look like a valid folder path.";
            return false;
        }

        foreach (var special in OffLimits)
        {
            var reserved = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(reserved)) continue;

            if (root.StartsWith(reserved, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "That is a system folder. Pick somewhere like your Documents or a project folder.";
                return false;
            }
        }

        return true;
    }

    /// <summary>UNC (\\host\share), device (\\?\UNC\..., \\.\...) and forward-slash variants.</summary>
    internal static bool IsNetworkPath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
