using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting;

/// <summary>
/// The 99_review reports of report.py (<c>column_type_differences.md</c>, <c>drifted_modules.md</c>,
/// <c>target_only_objects.md</c>) and check_flow.py (<c>flow_safety.md</c>). They script nothing: they tell a
/// human what the generated folders leave alone and whether running them can break what the target runs today.
/// Every document goes through <see cref="ReviewLog.SetDocument"/>, so it is written with the rest of
/// 99_review at the end of the run. Returns no folders.
/// </summary>
/// <remarks>
/// Differences from the Python, on purpose:
/// <list type="bullet">
/// <item>Wording uses the real source/target labels; the C-Tech-only "25.8 million rows" sentence is gone.
/// <c>ctech_only_objects.md</c> is <c>target_only_objects.md</c>.</item>
/// <item>column_type_differences.md: the Direction label is computed properly (report.py called a
/// nullability-only difference "C-TECH wider", ignored (max) and compared decimals by byte size), and a
/// Scripted column says which rows 04 changes - report.py claimed "no script" for rows 04 did widen.</item>
/// <item>drifted_modules.md marks the modules 08 replaces; target_only_objects.md marks target-only modules
/// 02 drops and recreates, so the "nothing touches them" sentence stays true.</item>
/// <item>flow_safety.md reads the structured plan in <see cref="CoreFacts"/> instead of regex-parsing the
/// generated files, uses Query Store totals over everything it holds (no BACKUP_TAKEN date), matches the
/// writers regex on the table's real schema instead of <c>dbo</c>, does not count a computed-only new column
/// as an INSERT-without-column-list break (SQL Server leaves computed columns out of the implicit list), adds
/// 05's checked keys/foreign keys/checks to the large-table list (they scan the table under a schema lock),
/// and flags 03 modules that take a table type 02 would change.</item>
/// </list>
/// </remarks>
public sealed class ReviewReports : IFolderGenerator
{
    /// <summary>report.py section 1.</summary>
    public const string ColumnTypesFile = "column_type_differences.md";
    /// <summary>report.py section 2.</summary>
    public const string DriftedModulesFile = "drifted_modules.md";
    /// <summary>report.py section 3 (ctech_only_objects.md in the Python).</summary>
    public const string TargetOnlyFile = "target_only_objects.md";
    /// <summary>check_flow.py.</summary>
    public const string FlowSafetyFile = "flow_safety.md";

    private const string BodyDiffers = "body differs";
    private const string FormattingOnly = "formatting only";
    private const string NoText = "no text to compare (encrypted)";

    public async Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx)
    {
        if (!ctx.Scoped)   // whole-database reports: an object script is about its own objects
        {
            ctx.Review.SetDocument(ColumnTypesFile, ColumnTypeReport(ctx));
            ctx.Review.SetDocument(DriftedModulesFile, DriftedModuleReport(ctx));
            ctx.Review.SetDocument(TargetOnlyFile, TargetOnlyReport(ctx));
        }
        ctx.Ct.ThrowIfCancellationRequested();
        ctx.Review.SetDocument(FlowSafetyFile, await new FlowSafetyCheck(ctx).BuildAsync());
        return [];
    }

    // ------------------------------------------------------------------ column_type_differences.md

    private sealed record TypeRow(
        bool Narrows, string Table, string Column, string SrcType, string SrcNull, string TgtType, string TgtNull,
        string Direction, string Scripted);

    /// <summary>
    /// report.py section 1: columns on both sides whose declared type (report.py <c>declared</c>) or
    /// nullability differ. Rows where making the target match the source would narrow or tighten it come
    /// first - those are the ones nobody should apply blindly.
    /// </summary>
    private static string ColumnTypeReport(ScriptContext ctx)
    {
        var src = ctx.SourceEndpoint.Label;
        var tgt = ctx.TargetEndpoint.Label;
        IReadOnlyList<IntendedAlter> alters = ctx.Selected(ScriptSection.ColumnTypes) ? ctx.Facts.IntendedAlters : [];

        var rows = new List<TypeRow>();
        foreach (var k in ctx.Source.SortedTables())
        {
            if (!ctx.Target.Tables.Contains(k)) continue;
            foreach (var c in ctx.Source.ColumnsOf(k).OrderBy(c => c.Col.ToLowerInvariant(), StringComparer.Ordinal))
            {
                var p = ctx.Target.FindColumn(k, c.Col);
                if (p is null) continue;
                string dc = Declared(c), dp = Declared(p);
                var sameType = dc == dp;
                if (sameType && c.IsNullable == p.IsNullable) continue;
                var (direction, narrows) = Direction(c, p, sameType, src, tgt);
                var alter = alters.FirstOrDefault(a => a.Table.SameAs(k) && string.Equals(a.Column, c.Col, StringComparison.OrdinalIgnoreCase));
                var by11 = ctx.Facts.DestructiveAlters.Any(a => a.Table.SameAs(k) && string.Equals(a.Column, p.Col, StringComparison.OrdinalIgnoreCase));
                rows.Add(new TypeRow(narrows, ShowTable(k), c.Col, dc, NullText(c.IsNullable), dp, NullText(p.IsNullable),
                    direction, alter is not null ? $"04 ({alter.Group})" : by11 ? "11" : "-"));
            }
        }

        var changedBy11 = rows.Count(r => r.Scripted == "11");
        var scripted = rows.Count(r => r.Scripted != "-") - changedBy11;
        var sb = new StringBuilder();
        sb.Append($"# Columns where {src} and {tgt} disagree on the type\n\n");
        sb.Append($"{rows.Count} columns. ");
        if (changedBy11 > 0)
            sb.Append($"{scripted} of them are changed by 04_columntypes and {changedBy11} by 11_destructive (marked in the Scripted column). " +
                      "**No script is generated for the others.** Each one is a data decision.\n\n");
        else if (!ctx.Selected(ScriptSection.ColumnTypes))
            sb.Append("04_columntypes was not generated in this run, so **no script is generated for any of these.** Each one is a data decision.\n\n");
        else if (scripted == 0)
            sb.Append("**No script is generated for any of these.** Each one is a data decision.\n\n");
        else
            sb.Append($"{scripted} of them are changed by 04_columntypes (marked in the Scripted column - it only widens a column or allows NULL). " +
                      "**No script is generated for the others.** Each one is a data decision.\n\n");
        sb.Append($"> Rows where {tgt} is wider or allows NULL come first. Making those match {src} would narrow the column " +
                  (ctx.Facts.DestructiveAlters.Count == 0
                      ? "or forbid NULL on rows that may already hold it, so no generated script does that.\n\n"
                      : "or forbid NULL on rows that may already hold it; only the rows marked `11` are changed, by 11_destructive, after a check that every row fits.\n\n"));
        sb.Append($"| Table | Column | {Cell(src)} type | {Cell(src)} null | {Cell(tgt)} type | {Cell(tgt)} null | Direction | Scripted |\n");
        sb.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows.OrderBy(r => !r.Narrows).ThenBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Column, StringComparer.Ordinal))
            sb.Append($"| {Cell(r.Table)} | {Cell(r.Column)} | `{r.SrcType}` | {r.SrcNull} | `{r.TgtType}` | {r.TgtNull} | {Cell(r.Direction)} | {r.Scripted} |\n");
        return sb.ToString();
    }

    /// <summary>report.py <c>declared</c>: lower-case type, (n)/(max) for string and binary types, decimal(p,s) without a space, everything else bare.</summary>
    private static string Declared(ColumnRow c)
    {
        var t = c.Dt.ToLowerInvariant();
        var ml = c.MaxLength;
        return t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{t}({(ml == -1 ? "max" : I(ml))})",
            "nvarchar" or "nchar" => $"{t}({(ml == -1 ? "max" : I(ml / 2))})",
            "decimal" or "numeric" => $"{t}({I(c.Precision)},{I(c.Scale)})",
            _ => t,
        };
    }

    /// <summary>
    /// Which side is wider, and whether matching the source would narrow or tighten the target.
    /// report.py's label was wrong for a nullability-only difference and for (max); this one is not.
    /// </summary>
    private static (string Text, bool Narrows) Direction(ColumnRow s, ColumnRow t, bool sameDeclared, string src, string tgt)
    {
        if (sameDeclared)
            return s.IsNullable ? ($"{src} allows NULL", false) : ($"{tgt} allows NULL", true);
        var sdt = s.Dt.ToLowerInvariant();
        var tdt = t.Dt.ToLowerInvariant();
        int cmp;
        if (sdt != tdt)
        {
            // int -> bigint, varchar -> nvarchar: one side holds every value of the other. That is the row
            // (a target bigint the source still has as int) nobody may "fix" by applying the source.
            if (IntRank.TryGetValue(sdt, out var si) && IntRank.TryGetValue(tdt, out var ti)) cmp = si.CompareTo(ti);
            else if (StringFamily.Contains(sdt) && StringFamily.Contains(tdt))
            {
                var (sc, su) = Chars(s);
                var (tc, tu) = Chars(t);
                if (sc >= tc && su >= tu) cmp = 1;
                else if (sc <= tc && su <= tu) cmp = -1;
                else return ("type differs", false);
            }
            else return ("type differs", false);
            return cmp > 0 ? ($"{src} wider (type differs)", false) : ($"{tgt} wider (type differs)", true);
        }
        if (sdt is "decimal" or "numeric")
        {
            int si = s.Precision - s.Scale, ti = t.Precision - t.Scale;
            if (si >= ti && s.Scale >= t.Scale) cmp = 1;
            else if (si <= ti && s.Scale <= t.Scale) cmp = -1;
            else return ("precision differs", false);
        }
        else
        {
            cmp = s.MaxLength == -1 ? 1 : t.MaxLength == -1 ? -1 : s.MaxLength.CompareTo(t.MaxLength);
        }
        return cmp > 0 ? ($"{src} wider", false) : ($"{tgt} wider", true);
    }

    private static readonly Dictionary<string, int> IntRank = new(StringComparer.Ordinal)
    {
        ["tinyint"] = 1, ["smallint"] = 2, ["int"] = 3, ["bigint"] = 4,
    };

    private static readonly HashSet<string> StringFamily = new(StringComparer.Ordinal) { "char", "varchar", "nchar", "nvarchar" };

    /// <summary>Characters a string column holds (max = unlimited) and whether it is Unicode.</summary>
    private static (long Chars, int Unicode) Chars(ColumnRow c)
    {
        var unicode = c.Dt.StartsWith('n') || c.Dt.StartsWith('N') ? 1 : 0;
        long chars = c.MaxLength == -1 ? long.MaxValue : unicode == 1 ? c.MaxLength / 2 : c.MaxLength;
        return (chars, unicode);
    }

    private static string NullText(bool nullable) => nullable ? "NULL" : "NOT NULL";

    // ------------------------------------------------------------------ drifted_modules.md

    /// <summary>
    /// report.py section 2: modules on both sides whose text differs, split into "formatting only" and
    /// "body differs" with report.py's <c>strip_and_norm</c>. The target may carry its own fixes, so
    /// nothing here is overwritten except what 08 proved to be formatting only.
    /// </summary>
    private static string DriftedModuleReport(ScriptContext ctx)
    {
        var src = ctx.SourceEndpoint.Label;
        var tgt = ctx.TargetEndpoint.Label;
        var replaced = new HashSet<ObjectName>(
            ctx.Selected(ScriptSection.Formatting) ? ctx.Facts.FormattingModules : [], ObjectName.IgnoreCase);
        var by10Set = new HashSet<ObjectName>(
            ctx.Selected(ScriptSection.DifferentModules) ? ctx.Facts.DifferentModules : [], ObjectName.IgnoreCase);

        var drift = new List<(string Name, string Type, string Kind, int SrcChars, int TgtChars, bool By08, bool By10)>();
        foreach (var k in ctx.Source.SortedModules())
        {
            var a = ctx.Source.Modules[k];
            var b = ctx.Target.FindModule(k);
            if (b is null || string.Equals(a.Definition, b.Definition, StringComparison.Ordinal)) continue;
            var kind = a.Definition is null || b.Definition is null ? NoText
                : TsqlTokens.StripAndNorm(a.Definition) == TsqlTokens.StripAndNorm(b.Definition) ? FormattingOnly
                : BodyDiffers;
            drift.Add((k.ToString(), TypeName(a.OType), kind, CodePoints(a.Definition), CodePoints(b.Definition), replaced.Contains(k), by10Set.Contains(k)));
        }

        var by08 = drift.Count(d => d.By08);
        var by10 = drift.Count(d => d.By10);
        var sb = new StringBuilder();
        sb.Append("# Modules that exist on both sides but are not identical\n\n");
        sb.Append($"{drift.Count} modules. {tgt} may carry its own changes that {src} does not have, so overwriting them " +
                  $"with the {src} text could remove working logic. ");
        // without 10 the sentence stays the generator's original one
        if (by10 == 0)
            sb.Append(by08 > 0
                ? $"Only the {by08} modules marked `08` are replaced (formatting only, by 08_formatting_modules, with a rollback). **No script is generated for the others.**\n\n"
                : "**No script is generated for any of these.**\n\n");
        else
            sb.Append(by08 > 0
                ? $"Only the {by08} modules marked `08` (formatting only, by 08_formatting_modules) and the {by10} marked `10` (different code, by 10_different_modules - see {ReviewLog.DifferentModulesFile}) are replaced, each with a rollback. **No script is generated for the others.**\n\n"
                : $"Only the {by10} modules marked `10` are replaced (different code, by 10_different_modules, with a rollback - see {ReviewLog.DifferentModulesFile}). **No script is generated for the others.**\n\n");
        sb.Append($"- body differs: {drift.Count(d => d.Kind == BodyDiffers)}\n");
        sb.Append($"- formatting only: {drift.Count(d => d.Kind == FormattingOnly)}\n");
        var noText = drift.Count(d => d.Kind == NoText);
        if (noText > 0) sb.Append($"- {NoText}: {noText}\n");
        sb.Append('\n');
        sb.Append($"| Object | Type | Difference | {Cell(src)} chars | {Cell(tgt)} chars | Scripted |\n|---|---|---|---:|---:|---|\n");
        foreach (var d in drift.OrderBy(d => d.Kind != BodyDiffers).ThenBy(d => d.Name, StringComparer.Ordinal))
            sb.Append($"| {Cell(d.Name)} | {d.Type} | {d.Kind} | {I(d.SrcChars)} | {I(d.TgtChars)} | {(d.By08 ? "08" : d.By10 ? "10" : "-")} |\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ target_only_objects.md

    /// <summary>
    /// report.py section 3: tables, table types and modules only the target has. The generator never drops
    /// them; the two exceptions (02 recreating a module that uses a changed table type, 09 renaming a name
    /// that differs only in case) are named per row so the promise in the intro stays exact.
    /// </summary>
    private static string TargetOnlyReport(ScriptContext ctx)
    {
        var src = ctx.SourceEndpoint.Label;
        var tgt = ctx.TargetEndpoint.Label;
        var blockers = new HashSet<ObjectName>(ctx.Facts.TableTypesWritten ? ctx.Facts.TableTypeBlockers : [], ObjectName.IgnoreCase);

        var dropped = new HashSet<ObjectName>(ctx.Facts.DestructiveDrops, ObjectName.IgnoreCase);
        string Note(ObjectName n)
        {
            if (dropped.Contains(n)) return "11_destructive drops it";
            if (blockers.Contains(n))
                return "02_tabletypes drops and recreates it from its current text (it uses a changed table type)";
            var rename = ctx.Facts.Renames.FirstOrDefault(r =>
                r.Kind is "table" or "object" &&
                string.Equals(r.Schema, n.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Old, n.Name, StringComparison.Ordinal));
            return rename is null ? "" : $"09_formatting_tables renames it to {rename.New}";
        }

        var items = new List<(string Name, string Type, string Note)>();
        foreach (var t in ctx.Target.SortedTables())
            if (!ctx.Source.Tables.Contains(t)) items.Add((t.ToString(), "Table", Note(t)));
        foreach (var t in ctx.Target.SortedTableTypes())
            if (!ctx.Source.TableTypes.Contains(t)) items.Add((t.ToString(), "Table type", dropped.Contains(t) ? "11_destructive drops it" : ""));
        foreach (var k in ctx.Target.SortedModules())
            if (!ctx.Source.Modules.ContainsKey(k)) items.Add((k.ToString(), TypeName(ctx.Target.Modules[k].OType), Note(k)));

        var noted = items.Count(i => i.Note.Length > 0);
        var sb = new StringBuilder();
        sb.Append($"# Objects that exist only in {tgt}\n\n");
        sb.Append($"{items.Count} objects ({tgt} has them, {src} does not). ");
        sb.Append(noted == 0
            ? "Nothing in any generated script drops, alters or renames them. "
            : $"Nothing in any generated script drops, alters or renames them, except the {noted} noted below. ");
        sb.Append("Re-run the SchemaScope compare after running the scripts to confirm every one of them is still there.\n\n");
        if (noted == 0)
        {
            sb.Append("| Object | Type |\n|---|---|\n");
            foreach (var i in items) sb.Append($"| {Cell(i.Name)} | {i.Type} |\n");
        }
        else
        {
            sb.Append("| Object | Type | Note |\n|---|---|---|\n");
            foreach (var i in items) sb.Append($"| {Cell(i.Name)} | {i.Type} | {Cell(i.Note)} |\n");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ shared helpers

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string I(int v) => v.ToString(Inv);

    /// <summary>Python's <c>'{:,}'.format(n)</c>.</summary>
    private static string N(long v) => v.ToString("N0", Inv);

    private static string Cell(string s) => ReviewLog.Cell(s);

    /// <summary>The table as the Python showed it (name only) when it is in dbo; schema-qualified otherwise, so two schemas never look alike.</summary>
    private static string ShowTable(ObjectName t) =>
        string.Equals(t.Schema, "dbo", StringComparison.OrdinalIgnoreCase) ? t.Name : t.ToString();

    /// <summary>report.py TYPE.</summary>
    private static string TypeName(string otype) => (otype ?? "").Trim() switch
    {
        "P" => "Procedure",
        "FN" => "Scalar function",
        "IF" or "TF" => "Table function",
        "V" => "View",
        "TR" => "Trigger",
        var o => o,
    };

    /// <summary>Python <c>len(str)</c> counts code points; a .NET string counts UTF-16 units.</summary>
    private static int CodePoints(string? s)
    {
        if (s is null) return 0;
        var n = s.Length;
        for (var i = 0; i + 1 < s.Length; i++)
            if (char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1])) { n--; i++; }
        return n;
    }

    /// <summary>
    /// Whether a 05 statement reads every row: a rule added WITH NOCHECK, a default, and a state change that only
    /// enables or disables (no WITH CHECK re-trust) touch metadata only.
    /// </summary>
    internal static bool ScansTable(EmittedStatement s) =>
        s.Kind == "state"
            ? s.Sql.Contains("WITH CHECK CHECK CONSTRAINT", StringComparison.OrdinalIgnoreCase)
            : s.Default is null && !s.Sql.Contains("WITH NOCHECK", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ flow_safety.md

    /// <summary>
    /// Port of check_flow.py: can the generated folders break what the target runs today - its own
    /// procedures, its triggers and its deployed application? Searches the target's module text
    /// (check_flow <c>norm</c> = <see cref="TsqlTokens.FlowNorm"/>), reads row counts and Query Store from
    /// the catalog, and - only when <see cref="ScriptOptions.CheckExistingData"/> is on - counts the
    /// existing rows a new 05 rule would refuse. Each finding is ranked by how often the affected module ran.
    /// </summary>
    private sealed class FlowSafetyCheck(ScriptContext ctx)
    {
        // Module bodies can be large and come from someone else's database: bound every regex.
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);
        private const RegexOptions Rx = RegexOptions.CultureInvariant;

        private static readonly Regex TriggerStarCopy = new(
            @"insert (?:into )?[#@]?\w+ ?(?:\([^)]*\) ?)?select (?:\w+\.)?\* from (inserted|deleted)", Rx, RegexTimeout);

        // check_flow's `\bon\s+(?:\[?dbo\]?\.)?\[?(\w+)\]?` on norm'd text (brackets already gone), any schema.
        private static readonly Regex TriggerOn = new(@"\bon\s+(?:\w+\.)?(\w+)", Rx, RegexTimeout);

        private string Src => ctx.SourceEndpoint.Label;
        private string Tgt => ctx.TargetEndpoint.Label;
        private readonly StringBuilder body = new();
        private readonly List<string> summary = [];
        private readonly SortedSet<string> unscanned = new(StringComparer.Ordinal);
        private readonly Dictionary<ObjectName, string> bodies = new(ObjectName.IgnoreCase);
        private readonly Dictionary<ObjectName, List<ObjectName>> writers = new(ObjectName.IgnoreCase);
        private QueryStoreUsage qs = new();
        private IReadOnlyDictionary<ObjectName, long>? rowCounts;

        public async Task<string> BuildAsync()
        {
            var ct = ctx.Ct;
            qs = await ctx.Probes.QueryStoreAsync(ct);
            rowCounts = await ctx.Probes.RowCountsAsync(ct);
            foreach (var k in ctx.Target.SortedModules())
                if (ctx.Target.Modules[k].Definition is { } d) bodies[k] = TsqlTokens.FlowNorm(d);

            await NewColumnsAsync();
            ct.ThrowIfCancellationRequested();
            TableTypes();
            await ConstraintsAsync();
            ct.ThrowIfCancellationRequested();
            LargeTables();
            await NotGeneratedAsync();
            if (!ctx.Options.CheckExistingData)
                Warn("existing-data checks were switched off - the 05 rules were not tested against the rows already in the table.");
            if (unscanned.Count > 0)
                Warn($"{unscanned.Count} {Tgt} module(s) were too slow to search and were skipped - see flow_safety.md.");
            return Head() + body;
        }

        // -------------------------------------------------------------- head + summary

        private string Head()
        {
            var sb = new StringBuilder();
            sb.Append($"# Flow safety - can these scripts break {Tgt} today?\n\n");
            sb.Append($"Generated by SchemaScope. Reads `{ctx.TargetEndpoint.Database}` ({Tgt}) and `{ctx.SourceEndpoint.Database}` ({Src}); writes nothing.\n");
            sb.Append($"Runs = Query Store executions at {Tgt} over everything Query Store holds: {qs.WindowText}.");
            if (qs.State is { } state && !string.Equals(state, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
                sb.Append($" Query Store is {state} now, so recent runs may be missing.");
            if (!qs.Available) sb.Append(" Every run count shows `-`, so the tables below are not ordered by use.");
            sb.Append('\n');
            sb.Append($"Folders generated in this run: {string.Join(", ", GeneratedFolders())}.\n\n");
            if (!ctx.Options.CheckExistingData)
                sb.Append($"> **Existing-data checks were skipped** (switched off for this run): no COUNT query was sent to {Tgt}. " +
                          "Only the catalog-based checks ran - module text, row counts from metadata and Query Store. " +
                          "Rules marked \"not checked\" need a data check before 05 is run.\n\n");
            if (unscanned.Count > 0)
                sb.Append($"> These {Tgt} modules could not be searched in time and are missing from the findings below: " +
                          $"{string.Join(", ", unscanned.Select(u => $"`{u}`"))}.\n\n");
            sb.Append("## Summary\n\n");
            foreach (var line in summary) sb.Append("- ").Append(line).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        private IEnumerable<string> GeneratedFolders()
        {
            (ScriptSection S, string F)[] map =
            [
                (ScriptSection.NewColumns, "01_columns"), (ScriptSection.TableTypes, "02_tabletypes"),
                (ScriptSection.MissingObjects, "03_missing"), (ScriptSection.ColumnTypes, "04_columntypes"),
                (ScriptSection.Constraints, "05_constraints"), (ScriptSection.Indexes, "06_indexes"),
                (ScriptSection.Triggers, "07_triggers"), (ScriptSection.Formatting, "08_formatting_modules / 09_formatting_tables"),
                (ScriptSection.DifferentModules, "10_different_modules"),
            ];
            var list = map.Where(m => ctx.Selected(m.S)).Select(m => m.F).ToList();
            return list.Count == 0 ? ["none"] : list;
        }

        // -------------------------------------------------------------- 01

        private async Task NewColumnsAsync()
        {
            if (!ctx.Selected(ScriptSection.NewColumns))
            {
                body.Append("## 01_columns - not generated in this run\n\nNo column is added, so no existing code is affected by new columns.\n\n");
                summary.Add("01 not generated - no new-column checks.");
                return;
            }

            var newCols = ctx.Facts.NewColumns;
            var added = newCols.GroupBy(n => n.Table, ObjectName.IgnoreCase).ToList();
            var parents = await ctx.Probes.TriggerParentsAsync(ctx.Ct);
            var noColList = new List<(ObjectName Module, ObjectName Table)>();
            var starCopy = new List<(ObjectName Module, ObjectName Table, string Into)>();

            foreach (var g in added)
            {
                var t = NameRx(g.Key);
                // The implicit column list of INSERT skips computed columns, so only a real column changes its count.
                var countChanges = g.Any(n => !n.Column.IsComputed);
                var ins = new Regex(@"insert (?:into )?" + t + @" ?(?:with ?\([^)]*\) ?)?(select|values|exec|execute)\b", Rx, RegexTimeout);
                var star = new Regex(@"insert (?:into )?([#@]?\w+) ?(?:\([^)]*\) ?)?select (?:top ?\(?\d+\)? )?(?:distinct )?(?:\w+\.)?\* from " + t,
                    Rx, RegexTimeout);
                foreach (var (k, b) in bodies)
                {
                    if (countChanges && Hit(ins, k, b)) noColList.Add((k, g.Key));
                    foreach (var m in Matches(star, k, b))
                    {
                        var into = m.Groups[1].Value;
                        if (!into.StartsWith('#') || b.Contains("create table " + into, StringComparison.Ordinal))
                            starCopy.Add((k, g.Key, into));
                    }
                }
                foreach (var tp in parents ?? [])
                    if (tp.Table.SameAs(g.Key) && bodies.TryGetValue(tp.Trigger, out var tb) && Hit(TriggerStarCopy, tp.Trigger, tb))
                        starCopy.Add((tp.Trigger, g.Key, "inserted/deleted"));
            }
            starCopy = starCopy.Distinct().ToList();

            body.Append($"## 01_columns - {newCols.Count} new columns on {added.Count} existing tables\n\n");
            body.Append("**`INSERT` with no column list into a table that gains a column** - fails with " +
                        "*Msg 213 Column name or number of supplied values does not match* on the first call.\n\n");
            if (noColList.Count == 0) body.Append("None found.\n\n");
            else
            {
                body.Append($"| {Cell(Tgt)} module | Inserts into | Runs |\n|---|---|---:|\n");
                foreach (var (k, tbl) in noColList.OrderByDescending(x => qs.RunsFor(x.Module)))
                    body.Append($"| {Cell(k.ToString())} | {Cell(ShowTable(tbl))} | {Runs(k)} |\n");
                body.Append('\n');
                foreach (var (k, tbl) in noColList)
                    Warn($"{k} inserts into {tbl} with no column list - it fails (Msg 213) as soon as 01_columns adds a column{RanSuffix(k)}.");
            }

            body.Append("**`SELECT *` copied into a fixed table, temp table or table variable** - same error " +
                        "if the receiving table does not grow too. Needs a human read.\n\n");
            if (starCopy.Count == 0) body.Append("None found.\n\n");
            else
            {
                body.Append($"| {Cell(Tgt)} module | Reads | Into | Runs |\n|---|---|---|---:|\n");
                foreach (var (k, tbl, into) in starCopy.OrderByDescending(x => qs.RunsFor(x.Module)))
                    body.Append($"| {Cell(k.ToString())} | {Cell(ShowTable(tbl))} | `{Cell(into)}` | {Runs(k)} |\n");
                body.Append('\n');
                Warn($"{starCopy.Count} {Tgt} module(s) copy SELECT * from a table that gains columns - read the 01 section of flow_safety.md.");
            }

            var nnd = newCols
                .Select((n, i) => (n, i))
                .Where(x => !x.n.Column.IsNullable && x.n.Default is not null && !x.n.Column.IsComputed)
                .Select(x => (File: File01(x.n, x.i), x.n.Table, x.n.Column.Col, Rows: RowsOf(x.n.Table)))
                .OrderByDescending(x => x.Rows ?? 0)
                .ToList();
            body.Append("**`NOT NULL` + `DEFAULT` columns** - every existing row is written. On Standard " +
                        "Edition that is a full rewrite of the table under a lock.\n\n");
            if (nnd.Count == 0) body.Append("None.\n\n");
            else
            {
                body.Append("| File | Table | Column | Rows |\n|---|---|---|---:|\n");
                foreach (var (file, tbl, col, rows) in nnd)
                    body.Append($"| {Cell(file)} | {Cell(ShowTable(tbl))} | {Cell(col)} | {RowsText(rows)} |\n");
                body.Append('\n');
            }

            summary.Add($"01 `INSERT` without a column list into a table that gains a column: {noColList.Count}");
            summary.Add($"01 `SELECT *` copies from a table that gains a column: {starCopy.Count}");
            summary.Add($"01 `NOT NULL` + `DEFAULT` columns (every row rewritten): {nnd.Count}");
        }

        /// <summary>
        /// The 01 file a new column was written to: generate.py names it <c>NNN_table_column.sql</c> with NNN its
        /// position in emit order. Taken from the files actually written when possible.
        /// </summary>
        private string File01(NewColumn n, int index)
        {
            var suffix = $"_{ScriptText.Safe(n.Table.Name)}_{ScriptText.Safe(n.Column.Col)}.sql";
            var expected = (index + 1).ToString("000", Inv) + suffix;
            var written = ctx.Writer.FilesIn("01_columns").Where(f => f.IsObjectFile).Select(f => Path.GetFileName(f.RelativePath)).ToList();
            return written.FirstOrDefault(f => string.Equals(f, expected, StringComparison.OrdinalIgnoreCase))
                   ?? written.FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                   ?? expected;
        }

        // -------------------------------------------------------------- 02 (+ 03 modules that need 02)

        private void TableTypes()
        {
            // DriftedTableTypes is every drifted type (10 needs them all); an object script reports only its own
            var drifted = ctx.Facts.DriftedTableTypes.Where(ctx.InScope).ToList();
            var generated = ctx.Facts.TableTypesWritten || ctx.Selected(ScriptSection.TableTypes);

            if (drifted.Count == 0)
            {
                body.Append("## 02_tabletypes - no table type differs\n\n");
                summary.Add("02 table types whose live callers would break: 0");
                return;
            }

            body.Append(generated
                ? $"## 02_tabletypes - {drifted.Count} table types rebuilt\n\n"
                : $"## 02_tabletypes - not generated in this run ({drifted.Count} table types differ)\n\n");
            body.Append("A procedure that takes one of these as a parameter is called by the application with " +
                        "a DataTable of the **old** column count. SqlClient refuses a table-valued parameter " +
                        "whose column count differs from the type, so the call fails before the procedure " +
                        "starts. Unless the column count is unchanged, this folder belongs with the new " +
                        "application release. When the count is unchanged the call still runs, but values are " +
                        "matched by position, so a renamed column receives what the old one held.\n\n");
            if (!generated)
                body.Append("This run did not generate 02_tabletypes, so nothing below changes now; the table shows what would break when it is run.\n\n");
            body.Append($"| Table type | {Cell(Src)} cols | {Cell(Tgt)} cols | Used by ({Cell(Tgt)}) | Runs |\n|---|---:|---:|---|---:|\n");

            var breaks = 0;
            foreach (var t in drifted)
            {
                var users = ctx.Target.TtParams.Where(p => p.Type.SameAs(t)).Select(p => p.Module)
                    .Distinct(ObjectName.IgnoreCase).Order(ObjectName.Ordinal).ToList();
                int nd = ctx.Source.ColumnsOfType(t).Count, np = ctx.Target.ColumnsOfType(t).Count;
                if (nd != np)
                {
                    foreach (var u in users.Where(u => qs.RunsFor(u) > 0))
                    {
                        breaks++;
                        if (generated)
                            Warn($"02_tabletypes changes {t} from {np} to {nd} columns; {u} takes it and ran {N(qs.RunsFor(u))} times - " +
                                 "the deployed application's calls fail until the application is updated.");
                    }
                    if (generated && !qs.Available && users.Count > 0)
                        Warn($"02_tabletypes changes {t} from {np} to {nd} columns; Query Store could not be read, so it is unknown whether " +
                             $"{string.Join(", ", users)} is called by the deployed application - its calls would fail.");
                }
                if (users.Count == 0)
                    body.Append($"| {Cell(ShowTable(t))} | {nd} | {np} | - | - |\n");
                foreach (var u in users)
                    body.Append($"| {Cell(ShowTable(t))} | {nd} | {np} | {Cell(u.ToString())} | {Runs(u)} |\n");
            }
            body.Append('\n');
            summary.Add($"02 table-type callers that ran and would break{(generated ? "" : " (02 not generated)")}: {breaks}");

            // generic.md §5: a 03 module that takes a drifted type is created against the target's OLD type unless 02 runs first.
            if (!(ctx.Facts.MissingWritten || ctx.Selected(ScriptSection.MissingObjects))) return;
            var needs02 = ctx.Facts.MissingItems
                .Where(i => i.Module is not null)
                .SelectMany(i => ctx.Source.TtParams
                    .Where(p => p.Module.SameAs(i.Object) && drifted.Any(d => d.SameAs(p.Type)))
                    .Select(p => (Module: i.Object, Type: p.Type, i.File)))
                .Distinct()
                .ToList();
            if (needs02.Count == 0) return;
            body.Append("**03_missing modules that take one of these table types** - created against the type " +
                        $"{Tgt} has now unless 02_tabletypes runs first. A body that reads a column the old type " +
                        "lacks fails at CREATE (Msg 207); one that does not still receives the old columns from callers.\n\n");
            body.Append("| 03 file | Module | Table type |\n|---|---|---|\n");
            foreach (var (m, t, file) in needs02)
                body.Append($"| {Cell(file)} | {Cell(m.ToString())} | {Cell(ShowTable(t))} |\n");
            body.Append('\n');
            foreach (var (m, t, _) in needs02)
                Warn(generated
                    ? $"03_missing creates {m}, which takes table type {t}: run 02_tabletypes first (RUN.ps1 -WithTableTypes) or its CREATE may fail."
                    : $"03_missing creates {m}, which takes table type {t}; {t} differs on {Tgt} and 02_tabletypes is not generated - its CREATE may fail.");
            summary.Add($"03 modules that need a changed table type from 02: {needs02.Count}");
        }

        // -------------------------------------------------------------- 05

        private sealed record RuleRow(string File, ObjectName Table, string Rule, long? Bad, string? Note, int Writers, long TopRuns);

        private async Task ConstraintsAsync()
        {
            if (!ctx.Selected(ScriptSection.Constraints))
            {
                body.Append("## 05_constraints - not generated in this run\n\n");
                summary.Add("05 not generated - no rule checks.");
                return;
            }

            var rows = new List<RuleRow>();
            foreach (var s in ctx.Facts.Constraints)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                if (s.ForeignKey is null && s.Check is null) continue;
                if (!ctx.Target.Tables.Contains(s.Table)) continue;   // a table 03 creates has no rows yet

                string rule;
                long? bad = null;
                string? note = null;
                if (s.ForeignKey is { } fk)
                {
                    rule = ShowTable(fk.Referenced);
                    var pc = Ddl.BracketedNames(fk.Cols);
                    var rc = Ddl.BracketedNames(fk.RCols);
                    var missing = pc.Where(c => ctx.Target.FindColumn(s.Table, c) is null).ToList();
                    if (fk.IsDisabled)
                        note = $"disabled on {Src} - added disabled, not enforced";
                    else if (missing.Count > 0)
                        note = ctx.Selected(ScriptSection.NewColumns) && missing.All(c => ctx.Facts.IsNewColumn(s.Table, c))
                            ? "column added by 01 - starts NULL/default"
                            : $"column {string.Join(", ", missing)} not in {Tgt} - the ADD fails unless 01_columns adds it";
                    else
                    {
                        ObjectName? referenced = ctx.Target.Tables.Contains(fk.Referenced) ? fk.Referenced : null;
                        var probe = await ctx.Probes.ForeignKeyOrphansAsync(s.Table, pc, referenced, rc, ctx.Ct);
                        bad = probe.Count;
                        note = probe.Checked ? null : NotCheckedText(probe);
                    }
                }
                else
                {
                    var ck = s.Check!;
                    rule = "CHECK " + (ck.IsSystemNamed ? "" : ck.CName);
                    if (ck.IsDisabled)
                        note = $"disabled on {Src} - added disabled, not enforced";
                    else
                    {
                        var probe = await ctx.Probes.CheckViolationsAsync(s.Table, ck.Definition, ctx.Ct);
                        bad = probe.Count;
                        note = probe.Checked ? null : NotCheckedText(probe);
                    }
                }
                var w = WritersOf(s.Table);
                var top = w.Count == 0 ? 0 : w.Max(k => qs.RunsFor(k));
                rows.Add(new RuleRow(s.File, s.Table, rule, bad, note, w.Count, top));

                if (bad > 0)
                    Warn(s.Sql.Contains("WITH NOCHECK", StringComparison.OrdinalIgnoreCase)
                        ? $"{s.File}: {N(bad.Value)} existing rows in {s.Table} break {rule}; it is added WITH NOCHECK, but the code that writes such rows will be refused from now on."
                        : $"{s.File}: {N(bad.Value)} existing rows in {s.Table} break {rule} - the ADD fails when 05 runs.");
            }

            body.Append($"## 05_constraints - rules added to tables {Tgt} already writes to\n\n");
            body.Append("A foreign key or check on an existing table is checked on **every** insert and " +
                        "update from now on. Rows that break it today mean (a) the `WITH CHECK` fails at " +
                        "deploy time, and (b) the code that wrote them will be refused next time. A table " +
                        "that nobody writes to and that has 0 bad rows is safe.\n\n");
            if (rows.Count == 0) body.Append("No foreign key or check is added to an existing table.\n\n");
            else
            {
                body.Append($"| File | Table | References / rule | Rows that break it today | {Cell(Tgt)} modules that write the table | Busiest writer runs |\n");
                body.Append("|---|---|---|---:|---:|---:|\n");
                foreach (var r in rows.OrderByDescending(r => r.Bad ?? 0).ThenBy(r => r.Table.Name, StringComparer.Ordinal))
                    body.Append($"| {Cell(r.File)} | {Cell(ShowTable(r.Table))} | {Cell(r.Rule)} | {Cell(r.Note ?? N(r.Bad ?? 0))} | {r.Writers} | {(r.TopRuns > 0 ? N(r.TopRuns) : "-")} |\n");
                body.Append('\n');
            }

            var broken = rows.Count(r => r.Bad > 0);
            // only rows whose count was attempted and failed; "column added by 01" and disabled rules are answers, not gaps
            var untested = rows.Count(r => r.Bad is null && r.Note is not null && r.Note.StartsWith("not ", StringComparison.Ordinal));
            summary.Add($"05 rules that existing rows already break: {broken} of {rows.Count} on existing tables" +
                        (untested > 0 ? $" ({untested} could not be counted - see the table)" : ""));
            summary.Add($"05 rules on tables {Tgt} code writes to: {rows.Count(r => r.Writers > 0)}");
        }

        private string NotCheckedText(ProbeCount probe) =>
            !ctx.Options.CheckExistingData
                ? "not checked (existing-data checks off)"
                : "not testable yet: " + Shorten(DistinctSentences(probe.NotCheckedReason ?? "probe failed"), 100);

        /// <summary>
        /// SqlClient joins every error of a batch, so a CHECK naming a missing column three times reads
        /// "Invalid column name 'X'. Invalid column name 'X'. ..." - each sentence is kept once.
        /// </summary>
        private static string DistinctSentences(string text)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return string.Join(" ", Regex.Split(text.ReplaceLineEndings(" "), @"(?<=\.)\s+")
                .Where(p => p.Length > 0 && seen.Add(Regex.Replace(p, @"^Msg \d+:\s*", ""))));
        }

        /// <summary>check_flow's writers: target modules that INSERT / UPDATE / MERGE the table (by its real schema, not dbo).</summary>
        private List<ObjectName> WritersOf(ObjectName table)
        {
            if (writers.TryGetValue(table, out var w)) return w;
            var rx = new Regex(@"(insert (?:into )?|update |merge (?:into )?)" + NameRx(table), Rx, RegexTimeout);
            w = bodies.Where(kv => Hit(rx, kv.Key, kv.Value)).Select(kv => kv.Key).ToList();
            writers[table] = w;
            return w;
        }

        // -------------------------------------------------------------- 04 / 05 / 06 on large tables

        private void LargeTables()
        {
            var threshold = ctx.Options.LargeTableRows;
            body.Append("## 04 / 05 / 06 - statements that lock a large table\n\n");
            body.Append("Not a logic risk - a locking one. Each statement below holds the table while it " +
                        "runs; if the application writes to these tables continuously, plan a window. " +
                        $"Listed: tables with at least {N(threshold)} rows (row counts from sys.partitions). " +
                        "05 rules added `WITH NOCHECK`, defaults and state changes that do not re-trust change metadata only and are not listed.\n\n");
            if (rowCounts is null)
            {
                body.Append($"Row counts of {Tgt} could not be read, so this check did not run.\n\n");
                summary.Add("04/05/06 statements on large tables: not checked (row counts unavailable)");
                return;
            }

            var big = new List<(string Folder, string File, ObjectName Table, long Rows)>();
            if (ctx.Selected(ScriptSection.ColumnTypes))
                foreach (var a in ctx.Facts.IntendedAlters) big.Add(("04", a.File, a.Table, RowsOf(a.Table) ?? 0));
            if (ctx.Selected(ScriptSection.Constraints))
                foreach (var s in ctx.Facts.Constraints)
                    if (ScansTable(s)) big.Add(("05", s.File, s.Table, RowsOf(s.Table) ?? 0));
            if (ctx.Selected(ScriptSection.Indexes))
                foreach (var s in ctx.Facts.Indexes) big.Add(("06", s.File, s.Table, RowsOf(s.Table) ?? 0));
            var hits = big.Where(b => b.Rows >= threshold).OrderByDescending(b => b.Rows).ToList();

            if (hits.Count == 0) body.Append("None.\n\n");
            else
            {
                body.Append("| Folder | File | Table | Rows |\n|---|---|---|---:|\n");
                foreach (var (f, file, tbl, n) in hits)
                    body.Append($"| {f} | {Cell(file)} | {Cell(ShowTable(tbl))} | {N(n)} |\n");
                body.Append('\n');
            }

            var nndBig = ctx.Selected(ScriptSection.NewColumns)
                ? ctx.Facts.NewColumns.Count(n => !n.Column.IsNullable && n.Default is not null && !n.Column.IsComputed && (RowsOf(n.Table) ?? 0) >= threshold)
                : 0;
            summary.Add($"04/05/06 statements on tables with at least {N(threshold)} rows: {hits.Count}");
            if (hits.Count + nndBig > 0)
                Warn($"{hits.Count + nndBig} statement(s) lock a table with at least {N(threshold)} rows - run them in a maintenance window (flow_safety.md).");
        }

        // -------------------------------------------------------------- not generated

        private async Task NotGeneratedAsync()
        {
            body.Append("## Not generated on purpose\n\n");

            var missingTr = ctx.Facts.MissingModules
                .Where(m => ctx.Source.FindModule(m) is { } r && string.Equals(r.OType.Trim(), "TR", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var by07 = ctx.Selected(ScriptSection.Triggers);
            IReadOnlyList<ObjectName> created = by07 ? ctx.Facts.Triggers : [];
            var notCreated = missingTr.Where(m => !created.Any(c => c.SameAs(m))).ToList();

            if (!by07 || notCreated.Count > 0)
            {
                body.Append(by07
                    ? $"**{Src} triggers {Tgt} does not have that 07_triggers did not create** - see {ReviewLog.NotApplied} for why each was left out.\n\n"
                    : $"**{Src} triggers {Tgt} does not have** - `03_missing` creates no triggers and `07_triggers` was not generated. " +
                      "A trigger changes what every write to its table does, so each is a decision.\n\n");
                if (notCreated.Count == 0) body.Append("None.\n\n");
                else
                {
                    var parents = await ctx.SourceProbes.TriggerParentsAsync(ctx.Ct);
                    foreach (var m in notCreated)
                        body.Append($"- `{m}` on `{TriggerTable(m, parents)}`\n");
                    body.Append('\n');
                }
            }
            summary.Add($"{Src} triggers not created: {notCreated.Count}");

            body.Append($"**Columns not generated** - 01_columns cannot add these (see {ReviewLog.NotApplied}):\n\n");
            var blocked = ctx.Facts.BlockedColumns;
            if (blocked.Count == 0) body.Append("None.\n");
            else
                foreach (var b in blocked)
                    body.Append($"- `{b.Table}.{b.Column.Col}` - {b.Reason.ReplaceLineEndings(" ")}\n");
            summary.Add($"Columns not generated: {blocked.Count}");
        }

        /// <summary>The trigger's table from the source catalog; check_flow's regex on the trigger text when that probe failed.</summary>
        private string TriggerTable(ObjectName trigger, IReadOnlyList<TriggerParent>? parents)
        {
            if (parents?.FirstOrDefault(p => p.Trigger.SameAs(trigger)) is { } p) return ShowTable(p.Table);
            var def = ctx.Source.FindModule(trigger)?.Definition;
            if (def is null) return "?";
            var m = Matches(TriggerOn, trigger, TsqlTokens.FlowNorm(def)).FirstOrDefault();
            return m is null ? "?" : m.Groups[1].Value;
        }

        // -------------------------------------------------------------- helpers

        /// <summary>check_flow <c>name_rx</c>: <c>(?:sch\.)?obj\b</c>, lower-cased to match <see cref="TsqlTokens.FlowNorm"/> text.</summary>
        private static string NameRx(ObjectName n) =>
            $@"(?:{Regex.Escape(n.Schema.ToLowerInvariant())}\.)?{Regex.Escape(n.Name.ToLowerInvariant())}\b";

        private bool Hit(Regex rx, ObjectName module, string text)
        {
            try { return rx.IsMatch(text); }
            catch (RegexMatchTimeoutException) { unscanned.Add(module.ToString()); return false; }
        }

        private List<Match> Matches(Regex rx, ObjectName module, string text)
        {
            try { return rx.Matches(text).ToList(); }
            catch (RegexMatchTimeoutException) { unscanned.Add(module.ToString()); return []; }
        }

        /// <summary>Rows of a table, or null when row counts could not be read (a missing table is 0, as in the Python).</summary>
        private long? RowsOf(ObjectName table) =>
            rowCounts is null ? null : rowCounts.TryGetValue(table, out var n) ? n : 0;

        private static string RowsText(long? n) => n is null ? "not read" : N(n.Value);

        /// <summary>check_flow <c>runs</c>: the count, or "-" for none.</summary>
        private string Runs(ObjectName k)
        {
            var n = qs.RunsFor(k);
            return n > 0 ? N(n) : "-";
        }

        private string RanSuffix(ObjectName k)
        {
            var n = qs.RunsFor(k);
            return n > 0 ? $" (it ran {N(n)} times in Query Store)" : "";
        }

        private void Warn(string text) => ctx.Review.Warn("flow_safety: " + text);

        private static string Shorten(string s, int max)
        {
            s = s.ReplaceLineEndings(" ");
            return s.Length > max ? s[..max] + "..." : s;
        }
    }
}
