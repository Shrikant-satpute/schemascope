using System.Text;

namespace SchemaScope.Scripting;

/// <summary>One thing held back for a human decision.</summary>
/// <param name="File">The 99_review file it is listed in, e.g. "not_applied_constraints.md".</param>
/// <param name="Kind">default, check, key, foreignkey, index, column, module, table, trigger, ...</param>
/// <param name="Name">The object, constraint or index name.</param>
/// <param name="Table">The table it belongs to, when there is one.</param>
/// <param name="Reason">Why it was not scripted - plain words, shown as-is.</param>
/// <param name="Section">The folder it would have gone to (e.g. "05_constraints"), or "review".</param>
/// <param name="IsNote">
/// True for something that WAS scripted but deserves a look (e.g. a trigger that compiles only with a
/// warning). It is listed in its review file like any row, but it is not a held-back item.
/// </param>
public sealed record ReviewItem(string File, string Kind, string Name, string? Table, string Reason, string Section, bool IsNote = false);

/// <summary>
/// Collects everything the generators hold back and every warning, then writes <c>99_review\*.md</c>
/// once at the end. Generators call <see cref="Hold"/> (one table row) or <see cref="SetDocument"/>
/// (a whole report they format themselves); nothing is written until <see cref="WriteAll"/>.
/// Thread-safe.
/// </summary>
public sealed class ReviewLog
{
    /// <summary>The review folder name.</summary>
    public const string Folder = "99_review";

    /// <summary>05/06 items held back (generate.py).</summary>
    public const string NotAppliedConstraints = "not_applied_constraints.md";
    /// <summary>03 objects left out by quarantine / exclusions / broken in source.</summary>
    public const string QuarantinedNotApplied = "quarantined_not_applied.md";
    /// <summary>08/09 items held back (generate_formatting.py).</summary>
    public const string FormattingNotApplied = "formatting_not_applied.md";
    /// <summary>01/04/07 and other column- or object-level items.</summary>
    public const string NotApplied = "not_applied.md";
    /// <summary>Modules with different code that 10_different_modules does not replace, and why.</summary>
    public const string DifferentNotApplied = "different_not_applied.md";
    /// <summary>Every module 10_different_modules replaces, with what to check before running it.</summary>
    public const string DifferentModulesFile = "different_modules.md";
    /// <summary>Modules whose CREATE header named an old (sp_rename'd) name; the scripts use the catalog name.</summary>
    public const string RenamedHeaders = "renamed_module_headers.md";

    private readonly object gate = new();
    private readonly List<ReviewItem> items = [];
    private readonly List<string> warnings = [];
    private readonly Dictionary<string, (string Title, string Intro)> headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> documents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one held-back item. <paramref name="section"/> defaults to "review".</summary>
    public void Hold(string file, string kind, string name, string? table, string reason, string? section = null)
    {
        lock (gate) items.Add(new ReviewItem(file, kind, name, table, reason, section ?? "review"));
    }

    /// <summary>
    /// Records a row for something that was scripted anyway but needs a look. It is written to
    /// <paramref name="file"/> with the held rows, but <see cref="ToHeldBack"/> leaves it out, so the
    /// result panel's "held back" count only counts what was really not scripted.
    /// </summary>
    public void Note(string file, string kind, string name, string? table, string reason, string? section = null)
    {
        lock (gate) items.Add(new ReviewItem(file, kind, name, table, reason, section ?? "review", IsNote: true));
    }

    /// <summary>Records a warning shown in the result panel and README (e.g. "same object, different case").</summary>
    public void Warn(string text)
    {
        lock (gate) warnings.Add(text);
    }

    /// <summary>Sets the heading and the paragraph written above a held-items table. Optional; a default is used.</summary>
    public void Describe(string file, string title, string intro)
    {
        lock (gate) headers[file] = (title, intro);
    }

    /// <summary>
    /// Supplies a whole markdown document for <paramref name="file"/> (flow_safety.md, validation.md, ...).
    /// If <see cref="Hold"/> items exist for the same file, their table is appended after it.
    /// </summary>
    public void SetDocument(string file, string markdown)
    {
        lock (gate) documents[file] = markdown;
    }

    /// <summary>Every held item, in the order recorded.</summary>
    public IReadOnlyList<ReviewItem> Items { get { lock (gate) return items.ToList(); } }

    /// <summary>Every warning, in the order recorded.</summary>
    public IReadOnlyList<string> Warnings { get { lock (gate) return warnings.ToList(); } }

    /// <summary>Held items of one file.</summary>
    public IReadOnlyList<ReviewItem> ItemsFor(string file) =>
        Items.Where(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The held items as API rows (<paramref name="notes"/>: the scripted-but-noted rows instead). Object = "table.name"
    /// when a table is known and not already part of the name.
    /// </summary>
    public List<HeldBackItem> ToHeldBack(bool notes = false) => Items.Where(i => i.IsNote == notes).Select(i => new HeldBackItem
    {
        Section = i.Section,
        Object = i.Table is { Length: > 0 } t && !i.Name.Contains('.') ? $"{t}.{i.Name}" : i.Name,
        Reason = i.Reason,
    }).ToList();

    /// <summary>
    /// Writes every document and every held-items file to <c>99_review\</c> (UTF-8, no BOM, LF).
    /// Each held-items file is a table: Kind | Name | Table | Why it is held back (Table is dropped when
    /// no row has one). Pipes inside cells become '/', as the Python did. Rows that were scripted anyway
    /// (<see cref="Note"/>) follow in a section of their own, so the count and the intro speak only of what
    /// was really held back.
    /// </summary>
    public void WriteAll(ScriptWriter writer)
    {
        Dictionary<string, string> docs;
        Dictionary<string, (string Title, string Intro)> heads;
        List<ReviewItem> all;
        lock (gate)
        {
            docs = new Dictionary<string, string>(documents, StringComparer.OrdinalIgnoreCase);
            heads = new Dictionary<string, (string, string)>(headers, StringComparer.OrdinalIgnoreCase);
            all = items.ToList();
        }

        var files = docs.Keys.Concat(all.Select(i => i.File)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in files)
        {
            var sb = new StringBuilder();
            var rows = all.Where(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
            var held = rows.Where(i => !i.IsNote).ToList();
            var notes = rows.Where(i => i.IsNote).ToList();
            if (docs.TryGetValue(file, out var doc))
            {
                sb.Append(doc);
                if (rows.Count > 0 && !doc.EndsWith('\n')) sb.Append('\n');
                if (rows.Count > 0) sb.Append('\n');
            }
            else
            {
                var (title, intro) = heads.TryGetValue(file, out var h)
                    ? h
                    : (DefaultTitle(file), "Each item below needs a decision, so no script was generated for it.");
                sb.Append("# ").Append(title).Append("\n\n");
                if (held.Count > 0 || notes.Count == 0)
                    sb.Append(held.Count).Append(held.Count == 1 ? " item. " : " items. ").Append(intro).Append("\n\n");
                else sb.Append("Nothing was held back.\n\n");
            }
            if (held.Count > 0) AppendTable(sb, held, "Why it is held back");
            if (notes.Count > 0)
            {
                if (held.Count > 0) sb.Append('\n');
                sb.Append("## Scripted, but check these\n\n");
                sb.Append(notes.Count).Append(notes.Count == 1 ? " item was" : " items were")
                  .Append(" scripted anyway; each is listed because it changes or may affect something worth a look.\n\n");
                AppendTable(sb, notes, "What to check");
            }
            writer.WriteMarkdown(Folder, file, sb.ToString());
        }
    }

    private static void AppendTable(StringBuilder sb, List<ReviewItem> rows, string why)
    {
        var withTable = rows.Any(r => !string.IsNullOrEmpty(r.Table));
        sb.Append(withTable ? $"| Kind | Name | Table | {why} |\n|---|---|---|---|\n"
                            : $"| Kind | Name | {why} |\n|---|---|---|\n");
        foreach (var r in rows)
        {
            sb.Append("| ").Append(Cell(r.Kind)).Append(" | ").Append(Cell(r.Name));
            if (withTable) sb.Append(" | ").Append(Cell(r.Table ?? ""));
            sb.Append(" | ").Append(Cell(r.Reason)).Append(" |\n");
        }
    }

    /// <summary>A markdown table cell: '|' → '/', line breaks → space.</summary>
    public static string Cell(string s) => (s ?? "").Replace('|', '/').ReplaceLineEndings(" ");

    private static string DefaultTitle(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file).Replace('_', ' ');
        return n.Length == 0 ? "Held back" : char.ToUpperInvariant(n[0]) + n[1..];
    }
}
