using System.Text;
using System.Text.RegularExpressions;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting.Planning;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>One file a PostgreSQL folder will hold.</summary>
/// <param name="Module">The source's code text the file must carry exactly (a RawModule), or null.</param>
internal sealed record PgFile(string Name, string Text, ObjectName? Object, string? Module = null);

/// <summary>One numbered folder, filled while planning and written at the end.</summary>
internal sealed class PgFolder(string name, string title, bool inRunAll)
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public bool InRunAll { get; } = inRunAll;
    public List<PgFile> Files { get; } = [];
    public List<string> Notes { get; } = [];
}

/// <summary>
/// One PostgreSQL "Create scripts" run over the compare's two snapshots: decides, folder by folder, what to write and
/// what to hold back (each hold with its reason), using the probes only to look at the target's data. Nothing here
/// touches either database except through <see cref="PgTargetProbes"/>, which is read-only.
/// </summary>
internal sealed partial class PgScriptRun
{
    private readonly ScriptRequestContext req;
    private readonly ReviewLog review;
    private readonly PgTargetProbes probes;
    private readonly CancellationToken ct;
    private readonly CompareOptions compare;

    public SchemaSnapshot Src { get; }
    public SchemaSnapshot Tgt { get; }
    private readonly Dictionary<string, DbObject> s;
    private readonly Dictionary<string, DbObject> t;

    // ---- facts the folders pass on
    /// <summary>Keys of the objects 03 creates.</summary>
    private readonly HashSet<string> created = new(StringComparer.Ordinal);
    /// <summary>Missing objects 03 does not create, with why.</summary>
    private readonly Dictionary<string, string> notCreated = new(StringComparer.Ordinal);
    /// <summary>Trigger functions 07 creates with their trigger instead of 03.</summary>
    private readonly HashSet<string> withTrigger = new(StringComparer.Ordinal);
    /// <summary>Table key -> columns 01 adds.</summary>
    private readonly Dictionary<string, HashSet<string>> added = new(StringComparer.Ordinal);
    /// <summary>(table key, column) in the order 01 adds them: PostgreSQL appends each at the end.</summary>
    private readonly List<(string Table, string Column)> addedInOrder = [];
    /// <summary>Table key -> key column sets 05 adds (for the foreign keys that need them).</summary>
    private readonly Dictionary<string, List<HashSet<string>>> addedKeys = new(StringComparer.Ordinal);
    /// <summary>schema -> relation and index names in use on the target, and those this run creates.</summary>
    private readonly Dictionary<string, HashSet<string>> taken = new(StringComparer.Ordinal);

    public PgFolder Columns { get; } = new(PgScriptGenerator.ColumnsFolder, "New columns", true);
    public PgFolder Missing { get; } = new(PgScriptGenerator.MissingFolder, "Missing objects", true);
    public PgFolder ColumnTypes { get; } = new(PgScriptGenerator.ColumnTypesFolder, "Column types", true);
    public PgFolder Constraints { get; } = new(PgScriptGenerator.ConstraintsFolder, "Constraints", true);
    public PgFolder Indexes { get; } = new(PgScriptGenerator.IndexesFolder, "Indexes", true);
    public PgFolder Triggers { get; } = new(PgScriptGenerator.TriggersFolder, "Triggers", false);
    public PgFolder Different { get; } = new(PgScriptGenerator.DifferentFolder, "Different code", false);

    public PgScriptRun(ScriptRequestContext req, ReviewLog review, PgTargetProbes probes, CancellationToken ct)
    {
        this.req = req;
        this.review = review;
        this.probes = probes;
        this.ct = ct;
        compare = req.CompareOptions ?? new CompareOptions();
        Src = req.SourceSnap!;
        Tgt = req.TargetSnap!;
        s = Src.BuildIndex();
        t = Tgt.BuildIndex();
        foreach (var o in Tgt.Objects) Take(o);
    }

    private ScriptOptions Options => req.Options;
    private bool Selected(ScriptSection section) => Options.Sections.Contains(section);
    public bool Scoped => Options.OnlyKeys is not null || Options.OnlyObjects is not null;

    // ------------------------------------------------------------------ the run

    /// <summary>Plans every selected folder, in the order the facts flow: 01, 03, 04, 05, 06, 07, 10, then what is left.</summary>
    public async Task PlanAsync(Action<int, string> report)
    {
        report(10, "01 New columns");
        if (Selected(ScriptSection.NewColumns)) PlanColumns();
        ct.ThrowIfCancellationRequested();
        report(20, "03 Missing objects");
        if (Selected(ScriptSection.MissingObjects)) await PlanMissingAsync();
        ct.ThrowIfCancellationRequested();
        report(35, "04 Column types");
        if (Selected(ScriptSection.ColumnTypes)) await PlanColumnTypesAsync();
        ct.ThrowIfCancellationRequested();
        report(45, "05 Constraints");
        if (Selected(ScriptSection.Constraints)) await PlanConstraintsAsync();
        ct.ThrowIfCancellationRequested();
        report(60, "06 Indexes");
        if (Selected(ScriptSection.Indexes)) await PlanIndexesAsync();
        ct.ThrowIfCancellationRequested();
        report(70, "07 Triggers");
        if (Selected(ScriptSection.Triggers)) PlanTriggers();
        report(75, "10 Different code");
        if (Selected(ScriptSection.DifferentModules)) PlanDifferent();
        if (Selected(ScriptSection.Formatting)) PlanFormatting();
        PlanLeftOver();
    }

    // ------------------------------------------------------------------ status and scope

    private enum Diff { Same, Missing, Formatting, Different }

    private Diff StatusOf(DbObject src)
    {
        if (!t.TryGetValue(src.Key, out var tgt)) return Diff.Missing;
        if (src.Hash == tgt.Hash) return Diff.Same;
        if (src.Type.IsModule() && src.LooseHash.Length > 0 && src.LooseHash == tgt.LooseHash) return Diff.Formatting;
        return Diff.Different;
    }

    private DbObject? Target(DbObject src) => t.GetValueOrDefault(src.Key);

    /// <summary>In the run: everything for a whole database; for an object script, only the objects asked for.</summary>
    private bool InScope(DbObject o)
    {
        if (Options.OnlyKeys is { } keys) return keys.Contains(o.Key);
        if (Options.OnlyObjects is { } names)
            return names.Contains(o.Type == DbObjectType.Schema ? new ObjectName(o.Name, "") : new ObjectName(o.Schema, o.Name));
        return true;
    }

    private List<Regex>? patterns;

    /// <summary>An exclude pattern matches schema.name or name (* and ?, case-insensitive), as the SQL Server generator matches them.</summary>
    private bool IsExcluded(DbObject o)
    {
        patterns ??= [.. Options.ExcludePatterns.Select(p => p.Trim()).Where(p => p.Length > 0)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))];
        var full = o.Type == DbObjectType.Schema ? o.Name : $"{o.Schema}.{o.Name}";
        return patterns.Any(rx => rx.IsMatch(full) || rx.IsMatch(o.Name));
    }

    private static string KeyOf(DbObjectType type, string schema, string name, string? signature = null) =>
        new DbObject { Type = type, Schema = schema, Name = name, Fold = IdentifierFold.Exact, Signature = signature }.Key;

    private static ObjectName NameOf(DbObject o) => o.Type == DbObjectType.Schema ? new ObjectName(o.Name, "") : new ObjectName(o.Schema, o.Name);

    private static string Display(DbObject o) =>
        o.Type is DbObjectType.Schema or DbObjectType.Extension ? o.Name
        : o.Signature is { } sig && o.Type != DbObjectType.Trigger ? $"{o.Schema}.{o.Name}({sig})"
        : o.Type == DbObjectType.Trigger ? $"{o.Schema}.{o.Name} {o.Signature}"
        : $"{o.Schema}.{o.Name}";

    private static string Kind(DbObject o) => o.Type.Label().ToLowerInvariant();

    /// <summary>Tables of the source that the target has too and whose text differs, in name order.</summary>
    private IEnumerable<(DbObject Src, DbObject Tgt)> ChangedTables() =>
        Src.Objects.Where(o => o.Type == DbObjectType.Table && InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different)
            .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => (o, Target(o)!));

    /// <summary>A partition or an inheritance child: its columns and keys come from its parent, so it is never changed on its own.</summary>
    private static bool IsChild(DbObject table) =>
        table.Properties.ContainsKey("partitionOf") || table.Properties.ContainsKey("inherits");

    private const string ChildReason =
        "a partition or inheritance child: its columns and keys come from its parent, and SchemaScope scripts the parent only";

    // ------------------------------------------------------------------ names

    /// <summary>
    /// The names <paramref name="o"/> holds in its schema's relation namespace: its own, and its indexes'. For an object 03
    /// creates (<paramref name="created"/>), only the indexes its own file makes - a table's keys, a materialized view's
    /// indexes; a table's other indexes take their names when 06 writes them.
    /// </summary>
    private void Take(DbObject o, bool created = false)
    {
        if (o.Type is DbObjectType.Table or DbObjectType.View or DbObjectType.MaterializedView or DbObjectType.Sequence or DbObjectType.CompositeType)
            Names(o.Schema).Add(o.Name);
        foreach (var ix in o.Indexes.Where(i => !created || o.Type != DbObjectType.Table || IsKey(i))) Names(o.Schema).Add(ix.Name);
    }

    private HashSet<string> Names(string schema)
    {
        if (!taken.TryGetValue(schema, out var set)) taken[schema] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    // ------------------------------------------------------------------ columns: what the target has once 01 has run

    /// <summary>True when the target's table has the column, or 01 adds it, or 03 creates the table.</summary>
    private bool HasColumn(string tableKey, string column)
    {
        if (created.Contains(tableKey)) return true;
        if (added.TryGetValue(tableKey, out var cols) && cols.Contains(column)) return true;
        return t.TryGetValue(tableKey, out var tgt) && tgt.Columns.Any(c => c.Name == column);
    }

    /// <summary>The first column of <paramref name="columns"/> the target will not have, or null.</summary>
    private string? Unavailable(DbObject table, IEnumerable<string> columns) => columns.FirstOrDefault(c => !HasColumn(table.Key, c));

    /// <summary>
    /// An object the target lacks that <paramref name="text"/> names (a type, sequence, function or table in its
    /// deparse), and that will still be missing when the statement runs: missing until 03 for 01; for later folders,
    /// one that 03 does not create.
    /// </summary>
    private DbObject? MissingNamed(string? text, bool before03)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var o in Src.Objects)
        {
            if (o.Type is DbObjectType.Schema or DbObjectType.Trigger or DbObjectType.Extension) continue;
            if (t.ContainsKey(o.Key)) continue;
            if (!before03 && created.Contains(o.Key)) continue;
            if (PgDdl.Mentions(text, o.Schema, o.Name)) return o;
        }
        return null;
    }

    private static ConstraintInfo? DefaultOf(DbObject table, string column) =>
        table.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.Default && c.ColumnName == column);

    // ------------------------------------------------------------------ 01_columns

    private void PlanColumns()
    {
        foreach (var (src, tgt) in ChangedTables())
        {
            var have = tgt.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var newCols = src.Columns.Where(c => !have.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList();
            if (newCols.Count == 0) continue;
            if (IsChild(src))
            {
                foreach (var c in newCols) review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName, ChildReason, Columns.Name);
                continue;
            }

            // the source's order; a generated column moves only behind a new column it reads that comes after it
            var edges = new List<(int, int)>();
            for (var g = 0; g < newCols.Count; g++)
                if (newCols[g].IsComputed)
                    for (var n = 0; n < newCols.Count; n++)
                        if (n != g && PgDdl.MentionsColumn(newCols[g].ComputedDefinition, newCols[n].Name)) edges.Add((n, g));
            foreach (var c in PlanOrder.StableOrder(newCols.Count, edges, []).Select(i => newCols[i]))
            {
                var def = DefaultOf(src, c.Name);
                if (!c.IsNullable && def is null && !c.IsIdentity && !c.IsComputed)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        "NOT NULL with no default: PostgreSQL cannot add it to a table that has rows. Give it a default in the source, or add it by hand.",
                        Columns.Name);
                    continue;
                }
                if (MissingNamed(c.DataType + "\n" + def?.Definition + "\n" + c.ComputedDefinition, before03: true) is { } needs)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"it uses {Kind(needs)} {Display(needs)}, which the target does not have until 03_missing runs (after 01). " +
                        "Run the scripts, then create them again: this column is in the next run.", Columns.Name);
                    continue;
                }
                if (c.IsComputed && MentionsBlocked(src, c.ComputedDefinition, newCols, c) is { } col)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"its expression reads {col}, which 01 does not add", Columns.Name);
                    continue;
                }

                var sql = $"ALTER TABLE {PgDdl.Q(src)} ADD COLUMN {ColumnDefinition(src, c)};\n";
                Columns.Files.Add(new PgFile($"column_{src.Schema}.{src.Name}.{c.Name}", Header(Columns, "column", $"{src.FullName}.{c.Name}") + sql,
                    new ObjectName(src.Schema, src.Name)));
                (added.TryGetValue(src.Key, out var set) ? set : added[src.Key] = new HashSet<string>(StringComparer.Ordinal)).Add(c.Name);
                addedInOrder.Add((src.Key, c.Name));
            }
        }
    }

    /// <summary>A new column the expression reads that 01 is not adding (held, or not new at all but missing).</summary>
    private string? MentionsBlocked(DbObject table, string? expression, List<ColumnInfo> newCols, ColumnInfo self) =>
        newCols.Where(n => n.Name != self.Name && PgDdl.MentionsColumn(expression, n.Name) && !HasColumn(table.Key, n.Name))
            .Select(n => n.Name).FirstOrDefault();

    /// <summary>A column as CREATE TABLE and ADD COLUMN write it, from the source's catalog facts.</summary>
    private static string ColumnDefinition(DbObject table, ColumnInfo c)
    {
        var sb = new StringBuilder();
        sb.Append(PgDdl.Q(c.Name)).Append(' ').Append(c.DataType);
        if (!string.IsNullOrEmpty(c.Collation)) sb.Append(" COLLATE ").Append(c.Collation);
        if (c.IsIdentity)
        {
            // the identity sequence's start, step and limits, so the target's sequence is the source's
            var options = string.Join(" ", new[]
            {
                c.IdentitySeed is { } seed ? $"START WITH {seed}" : null,
                c.IdentityIncrement is { } inc ? $"INCREMENT BY {inc}" : null,
                c.IdentityOptions,
            }.OfType<string>());
            sb.Append($" GENERATED {c.IdentityKind ?? "BY DEFAULT"} AS IDENTITY").Append(options.Length > 0 ? $" ({options})" : "");
        }
        if (c.IsComputed) sb.Append($" GENERATED ALWAYS AS ({c.ComputedDefinition}) {(c.IsPersisted ? "STORED" : "VIRTUAL")}");
        if (!c.IsNullable && !c.IsIdentity) sb.Append(" NOT NULL");
        if (!c.IsComputed && DefaultOf(table, c.Name) is { } def) sb.Append(" DEFAULT ").Append(def.Definition);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 04_columntypes

    private async Task PlanColumnTypesAsync()
    {
        foreach (var (src, tgt) in ChangedTables())
        {
            var common = src.Columns.Where(c => tgt.Columns.Any(x => x.Name == c.Name)).OrderBy(c => c.Ordinal);
            foreach (var c in common)
            {
                var tc = tgt.Columns.First(x => x.Name == c.Name);
                var typeChanges = !string.Equals(c.DataType, tc.DataType, StringComparison.Ordinal);
                var relax = c.IsNullable && !tc.IsNullable;
                var tighten = !c.IsNullable && tc.IsNullable;
                var column = $"{c.Name}";

                if (IsChild(src) && (typeChanges || relax || tighten))
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName, ChildReason, ColumnTypes.Name);
                    continue;
                }
                if (c.IsIdentity != tc.IsIdentity || c.IdentityKind != tc.IdentityKind)
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"identity differs (source: {Identity(c)}, target: {Identity(tc)}); changing it is not scripted", ColumnTypes.Name);
                else if (c.IsIdentity && !compare.IgnoreIdentitySeed && IdentitySequence(c) != IdentitySequence(tc))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"its identity sequence differs (source: {IdentitySequence(c)}; target: {IdentitySequence(tc)}); changing it is not scripted",
                        ColumnTypes.Name);
                if (c.IsComputed != tc.IsComputed || c.IsPersisted != tc.IsPersisted ||
                    !string.Equals(c.ComputedDefinition, tc.ComputedDefinition, StringComparison.Ordinal))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        "the generated expression differs; PostgreSQL can only drop and re-add such a column", ColumnTypes.Name);
                if (!compare.IgnoreCollation && !string.Equals(c.Collation, tc.Collation, StringComparison.Ordinal))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"collation differs (source: {c.Collation ?? "default"}, target: {tc.Collation ?? "default"}); changing it rebuilds its indexes and is not scripted",
                        ColumnTypes.Name);
                if (tighten)
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        "the source has NOT NULL and the target allows NULL: making it NOT NULL fails on existing NULLs and is not scripted",
                        ColumnTypes.Name);
                if (!typeChanges && !relax) continue;

                // PostgreSQL refuses to change a column's type while a view, rule, generated column, policy or trigger reads it
                if (typeChanges && ReadersOf(tgt, c.Name) is { Count: > 0 } readers)
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"{string.Join(", ", readers)} {(readers.Count == 1 ? "reads" : "read")} this column on the target, and PostgreSQL cannot change " +
                        $"its type ({tc.DataType} to {c.DataType}) while {(readers.Count == 1 ? "it does" : "they do")}", ColumnTypes.Name);
                    typeChanges = false;
                }
                else if (typeChanges && Widening(tc.DataType, c.DataType) is null)
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"{tc.DataType} to {c.DataType} is not a widening (04 only widens); it can lose data and is held for review", ColumnTypes.Name);
                    typeChanges = false;
                }
                else if (typeChanges && Widening(tc.DataType, c.DataType) == true)
                {
                    var rows = await probes.RowEstimateAsync(src.Schema, src.Name, ct);
                    if (rows >= Options.LargeTableRows)
                        review.Note(ReviewLog.NotApplied, "column", column, src.FullName,
                            $"{tc.DataType} to {c.DataType} rewrites the whole table (about {rows:N0} rows) under an ACCESS EXCLUSIVE lock", ColumnTypes.Name);
                }
                if (relax && tgt.Indexes.Any(ix => ix.IsPrimaryKey && ix.KeyColumns.Any(k => k.Name == c.Name)))
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        "the target's primary key uses this column, so it cannot allow NULL", ColumnTypes.Name);
                    relax = false;
                }

                var sql = new StringBuilder();
                if (typeChanges) sql.Append($"ALTER TABLE {PgDdl.Q(src)} ALTER COLUMN {PgDdl.Q(c.Name)} TYPE {c.DataType};\n");
                if (relax) sql.Append($"ALTER TABLE {PgDdl.Q(src)} ALTER COLUMN {PgDdl.Q(c.Name)} DROP NOT NULL;\n");
                if (sql.Length == 0) continue;
                ColumnTypes.Files.Add(new PgFile($"alter_{src.Schema}.{src.Name}.{c.Name}",
                    Header(ColumnTypes, "column", $"{src.FullName}.{c.Name}") + sql, new ObjectName(src.Schema, src.Name)));
            }
        }
    }

    private static string Identity(ColumnInfo c) => c.IsIdentity ? $"GENERATED {c.IdentityKind} AS IDENTITY" : "none";

    private static string IdentitySequence(ColumnInfo c) => $"START WITH {c.IdentitySeed} INCREMENT BY {c.IdentityIncrement} {c.IdentityOptions}".TrimEnd();

    /// <summary>The target objects whose definition reads <paramref name="column"/> of <paramref name="table"/> (pg_depend, column level).</summary>
    private List<string> ReadersOf(DbObject table, string column)
    {
        var readers = new List<string>();
        foreach (var o in Tgt.Objects)
            foreach (var d in o.DependsOn ?? [])
                if (d.Key == table.Key && d.Column == column && d.Via is "view" or "default" or "policy" or "trigger")
                {
                    var what = d.Via switch
                    {
                        "view" when o.Key == table.Key => "a rule on the table",
                        "view" => $"{Kind(o)} {Display(o)}",
                        "default" => o.Key == table.Key ? "a generated column of the table" : $"a default of {Display(o)}",
                        "policy" => "a row level security policy",
                        _ => o.Type == DbObjectType.Trigger ? $"trigger {Display(o)}" : "a trigger",
                    };
                    if (!readers.Contains(what)) readers.Add(what);
                }
        return readers;
    }

    /// <summary>
    /// Null when <paramref name="to"/> is not a widening of <paramref name="from"/>; otherwise whether PostgreSQL rewrites
    /// the table for it (a longer varchar or numeric does not; int to bigint does).
    /// </summary>
    internal static bool? Widening(string from, string to)
    {
        static (string Base, int[] Args) Parse(string type)
        {
            var m = TypeRx().Match(type.Trim());
            if (!m.Success) return (type.Trim(), []);
            var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => int.Parse(a.Trim())).ToArray() : [];
            return (m.Groups["base"].Value + m.Groups["tail"].Value, args);
        }
        var (fb, fa) = Parse(from);
        var (tb, ta) = Parse(to);
        string[] ints = ["smallint", "integer", "bigint"];
        if (Array.IndexOf(ints, fb) is >= 0 and var fi && Array.IndexOf(ints, tb) is var ti && ti > fi && fa.Length == 0 && ta.Length == 0) return true;
        if (fb == "real" && tb == "double precision") return true;
        switch (fb)
        {
            case "character varying" when tb == "text": return false;
            case "character varying" when tb == "character varying":
                return ta.Length == 0 || (fa.Length == 1 && ta[0] > fa[0]) ? false : null;
            case "numeric" when tb == "numeric":
                if (ta.Length == 0) return false;
                if (fa.Length == 0) return null;
                var fs = fa.Length > 1 ? fa[1] : 0;
                var ts = ta.Length > 1 ? ta[1] : 0;
                return fs == ts && ta[0] > fa[0] ? false : null;
            case "bit varying" when tb == "bit varying":
                return ta.Length == 0 || (fa.Length == 1 && ta[0] > fa[0]) ? false : null;
            case "timestamp without time zone" or "timestamp with time zone" or "time without time zone" or "time with time zone" when tb == fb:
                return ta.Length == 0 || (fa.Length == 1 && ta[0] > fa[0]) ? false : null;
        }
        return null;
    }

    /// <summary>format_type's spelling: "character varying(20)", "numeric(10,2)", "timestamp(3) without time zone".</summary>
    [GeneratedRegex(@"^(?<base>[a-z ]+?)(?:\((?<args>\d+(?:\s*,\s*\d+)?)\))?(?<tail> with(?:out)? time zone)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeRx();

    // ------------------------------------------------------------------ 05_constraints

    private readonly List<PgFile> validations = [];

    private async Task PlanConstraintsAsync()
    {
        var fks = new List<(DbObject Src, ForeignKeyInfo Fk, bool NewTable)>();
        foreach (var (src, tgt) in ChangedTables())
        {
            if (IsChild(src))
            {
                var indexes = !IsPartition(src) || !Selected(ScriptSection.Indexes);   // else 06 looks at a partition's plain indexes
                if (KeyText(src, indexes) != KeyText(tgt, indexes))
                    review.Hold(ReviewLog.NotAppliedConstraints, "table", src.FullName, null, ChildReason, Constraints.Name);
                continue;
            }
            PlanDefaults(src, tgt);
            await PlanChecksAsync(src, tgt);
            await PlanKeysAsync(src, tgt);
            foreach (var fk in src.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var tfk = tgt.ForeignKeys.FirstOrDefault(x => x.Name == fk.Name);
                if (tfk is null) { fks.Add((src, fk, false)); continue; }
                if (Strip(fk.Definition) != Strip(tfk.Definition))
                    review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                        $"the target's foreign key of this name is different ({Strip(tfk.Definition)}); replacing it is not scripted", Constraints.Name);
                else if (!fk.IsNotTrusted && tfk.IsNotTrusted)
                    Validate(src, fk.Name);
                else if (fk.IsNotTrusted && !tfk.IsNotTrusted)
                    review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                        "the source's copy is NOT VALID and the target's is validated; the target is stricter, so nothing is changed", Constraints.Name);
            }
            foreach (var tfk in tgt.ForeignKeys.Where(x => src.ForeignKeys.All(f => f.Name != x.Name)))
                review.Hold(ReviewLog.NotAppliedDestructive, "foreignkey", tfk.Name, src.FullName, TargetOnly, Constraints.Name);
        }

        // the foreign keys of the tables 03 creates: their tables are empty and every table exists by now
        foreach (var src in Src.Objects.Where(o => o.Type == DbObjectType.Table && created.Contains(o.Key))
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
            foreach (var fk in src.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
                fks.Add((src, fk, true));

        foreach (var (src, fk, isNew) in fks) await PlanForeignKeyAsync(src, fk, isNew);

        Constraints.Files.AddRange(validations);
    }

    private const string TargetOnly =
        "only the target has it; removing it can change what the database accepts or lose data, and 11_destructive is not available for PostgreSQL in this version";

    private static string KeyText(DbObject o, bool indexes = true) => string.Join("\n",
        o.Indexes.Where(i => indexes || IsKey(i)).Select(i => i.Name + i.Definition).Concat(o.Constraints.Select(c => c.Name + c.Definition)).Concat(o.ForeignKeys.Select(f => f.Name + f.Definition)).Order(StringComparer.Ordinal));

    /// <summary>A foreign key's definition without NOT VALID (its trust is compared on its own).</summary>
    private static string Strip(string? def)
    {
        var d = (def ?? "").Trim();
        return d.EndsWith(" NOT VALID", StringComparison.Ordinal) ? d[..^" NOT VALID".Length] : d;
    }

    /// <summary>A VALIDATE file for the end of 05, or with <paramref name="after06"/> for the end of 06.</summary>
    private void Validate(DbObject table, string constraint, bool after06 = false)
    {
        var sql = $"ALTER TABLE {PgDdl.Q(table)} VALIDATE CONSTRAINT {PgDdl.Q(constraint)};\n";
        (after06 ? indexValidations : validations).Add(new PgFile($"validate_{table.Schema}.{table.Name}.{constraint}",
            Header(after06 ? Indexes : Constraints, "validate", $"{table.FullName}.{constraint}") +
            "-- Checks the rows that were there before; new rows were already checked from the moment it was added.\n" + sql,
            new ObjectName(table.Schema, table.Name)));
    }

    private void PlanDefaults(DbObject src, DbObject tgt)
    {
        foreach (var c in src.Columns.OrderBy(c => c.Ordinal))
        {
            var tc = tgt.Columns.FirstOrDefault(x => x.Name == c.Name);
            if (tc is null || c.IsComputed) continue;   // a new column comes with its default from 01
            var sd = DefaultOf(src, c.Name);
            var td = DefaultOf(tgt, c.Name);
            if (sd is null && td is null) continue;
            if (sd is not null && td is not null)
            {
                if (sd.Definition != td.Definition)
                    review.Hold(ReviewLog.NotAppliedConstraints, "default", c.Name, src.FullName,
                        $"the target's default is {td.Definition}, the source's {sd.Definition}; changing what new rows get is not scripted", Constraints.Name);
                continue;
            }
            if (sd is null)
            {
                review.Hold(ReviewLog.NotAppliedDestructive, "default", c.Name, src.FullName, TargetOnly, Constraints.Name);
                continue;
            }
            if (MissingNamed(sd.Definition, before03: false) is { } needs)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "default", c.Name, src.FullName,
                    $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}", Constraints.Name);
                continue;
            }
            Constraints.Files.Add(new PgFile($"default_{src.Schema}.{src.Name}.{c.Name}", Header(Constraints, "default", $"{src.FullName}.{c.Name}") +
                $"ALTER TABLE {PgDdl.Q(src)} ALTER COLUMN {PgDdl.Q(c.Name)} SET DEFAULT {sd.Definition};\n", new ObjectName(src.Schema, src.Name)));
        }
    }

    private string Why(DbObject missing) => notCreated.TryGetValue(missing.Key, out var why) ? $" ({why})" : "";

    private async Task PlanChecksAsync(DbObject src, DbObject tgt)
    {
        foreach (var ck in src.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var tck = tgt.Constraints.FirstOrDefault(x => x.Kind == ConstraintKind.Check && x.Name == ck.Name);
            if (tck is not null)
            {
                if (tck.Definition != ck.Definition)
                    review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName,
                        $"the target's check of this name is different ({tck.Definition}); replacing it is not scripted", Constraints.Name);
                else if (!ck.IsNotTrusted && tck.IsNotTrusted) Validate(src, ck.Name);
                continue;
            }
            if (src.Columns.Where(c => PgDdl.MentionsColumn(ck.Definition, c.Name)).Select(c => c.Name) is var cols &&
                Unavailable(src, cols) is { } col)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName, $"it reads {col}, which the target will not have", Constraints.Name);
                continue;
            }
            if (MissingNamed(ck.Definition, before03: false) is { } needs)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName,
                    $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}", Constraints.Name);
                continue;
            }
            if (!ck.IsNotTrusted && CheckExpression(ck.Definition) is { } expression)
            {
                var bad = await probes.CheckViolationsAsync(PgDdl.Q(src), ck.Name, expression, ct);
                if (bad.Count > 0)
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName,
                        $"{bad.Count:N0} existing row(s) break it; fix the data first", Constraints.Name);
                    continue;
                }
            }
            Constraints.Files.Add(new PgFile($"check_{src.Schema}.{src.Name}.{ck.Name}", Header(Constraints, "check", $"{src.FullName}.{ck.Name}") +
                "-- Added NOT VALID: checks new rows at once, without a long lock; the existing rows are checked by VALIDATE in a later file.\n" +
                $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(ck.Name)} {ck.Definition} NOT VALID;\n", new ObjectName(src.Schema, src.Name)));
            if (!ck.IsNotTrusted) Validate(src, ck.Name);
        }
        foreach (var tck in tgt.Constraints.Where(x => x.Kind == ConstraintKind.Check && src.Constraints.All(c => c.Kind != ConstraintKind.Check || c.Name != x.Name)))
            review.Hold(ReviewLog.NotAppliedDestructive, "check", tck.Name, src.FullName, TargetOnly, Constraints.Name);
    }

    /// <summary>The expression of "CHECK ((x > 0))" (and "... NO INHERIT"), or null.</summary>
    internal static string? CheckExpression(string definition)
    {
        var m = CheckRx().Match(definition.Trim());
        return m.Success ? m.Groups["e"].Value : null;
    }

    [GeneratedRegex(@"^CHECK \((?<e>.*)\)(?: NO INHERIT)?$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CheckRx();

    private static bool IsKey(IndexInfo i) => i.IsPrimaryKey || i.IsUniqueConstraint || i.TypeDesc == PgCanonicalWriter.ExcludeConstraint;

    private async Task PlanKeysAsync(DbObject src, DbObject tgt)
    {
        foreach (var key in src.Indexes.Where(IsKey).OrderBy(i => i.IsPrimaryKey ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            var what = key.IsPrimaryKey ? "primary key" : key.IsUniqueConstraint ? "unique key" : "exclusion constraint";
            var tkey = tgt.Indexes.FirstOrDefault(x => IsKey(x) && x.Name == key.Name);
            if (tkey is not null)
            {
                if (tkey.Definition != key.Definition)
                    review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                        $"the target's {what} of this name is different ({tkey.Definition}); replacing it is not scripted", Constraints.Name);
                continue;
            }
            if (key.IsPrimaryKey && tgt.Indexes.FirstOrDefault(x => x.IsPrimaryKey) is { } otherPk)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                    $"the target already has primary key {otherPk.Name} ({otherPk.Definition}); a table has one", Constraints.Name);
                continue;
            }
            var cols = key.KeyColumns.OrderBy(k => k.Ordinal).Select(k => k.Name).ToList();
            if (Unavailable(src, cols) is { } col)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName, $"it uses {col}, which the target will not have", Constraints.Name);
                continue;
            }
            if (Names(src.Schema).Contains(key.Name))
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                    $"its index would be called {key.Name}, a name another table, view, sequence or index of schema {src.Schema} already has on the target",
                    Constraints.Name);
                continue;
            }
            // only columns the target has now hold rows to check; one 01 adds is NULL or its default everywhere
            var existing = cols.Where(c => tgt.Columns.Any(x => x.Name == c)).ToList();
            if (key.IsPrimaryKey)
            {
                var nulls = await probes.NullsAsync(PgDdl.Q(src), existing, ct);
                if (nulls.Count > 0)
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                        $"{nulls.Count:N0} existing row(s) have a NULL in it; a primary key needs every value", Constraints.Name);
                    continue;
                }
            }
            if (!key.IsPrimaryKey && !key.IsUniqueConstraint)
                probes.NoteNotChecked($"existing rows against exclusion constraint {src.FullName}.{key.Name}", "exclusion constraints are not probed");
            else if (existing.Count != cols.Count)
            {
                // an identity column 01 adds numbers every row, so the key is unique whatever the others hold
                if (probes.CheckExistingData && !cols.Except(existing).All(c => src.Columns.Any(x => x.Name == c && x.IsIdentity)))
                    probes.NoteNotChecked($"duplicates under {what} {src.FullName}.{key.Name}",
                        "it uses a column 01 adds, which every existing row gets with the same default; not probed");
            }
            else
            {
                var dups = await probes.DuplicateGroupsAsync(PgDdl.Q(src), cols, null, ct);
                if (dups.Count > 0)
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                        $"{dups.Count:N0} value(s) appear more than once in the existing rows", Constraints.Name);
                    continue;
                }
            }
            Constraints.Files.Add(new PgFile($"key_{src.Schema}.{src.Name}.{key.Name}", Header(Constraints, "key", $"{src.FullName}.{key.Name}") +
                $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(key.Name)} {key.Definition};\n", new ObjectName(src.Schema, src.Name)));
            Names(src.Schema).Add(key.Name);
            if (key.IsPrimaryKey || key.IsUniqueConstraint)
                (addedKeys.TryGetValue(src.Key, out var list) ? list : addedKeys[src.Key] = []).Add(cols.ToHashSet(StringComparer.Ordinal));
        }
        foreach (var tkey in tgt.Indexes.Where(x => IsKey(x) && src.Indexes.All(i => !IsKey(i) || i.Name != x.Name)))
            review.Hold(ReviewLog.NotAppliedDestructive, "key", tkey.Name, src.FullName, TargetOnly, Constraints.Name);
    }

    /// <summary>Foreign keys whose referenced columns only a unique index of 06 covers: planned again at the end of 06.</summary>
    private readonly List<(DbObject Src, ForeignKeyInfo Fk, bool NewTable)> afterIndexes = [];
    /// <summary>VALIDATE files for the foreign keys 06 adds, after them.</summary>
    private readonly List<PgFile> indexValidations = [];
    /// <summary>Table key -> column sets of the plain unique indexes 06 adds.</summary>
    private readonly Dictionary<string, List<HashSet<string>>> addedUnique = new(StringComparer.Ordinal);

    private async Task PlanForeignKeyAsync(DbObject src, ForeignKeyInfo fk, bool isNew, bool after06 = false)
    {
        var refKey = KeyOf(DbObjectType.Table, fk.ReferencedSchema, fk.ReferencedTable);
        var refName = $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
        var refExists = t.ContainsKey(refKey);
        if (!refExists && !created.Contains(refKey))
        {
            var why = s.ContainsKey(refKey) && notCreated.TryGetValue(refKey, out var w) ? $" ({w})" : "";
            review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                $"it references {refName}, which the target does not have and this run does not create{why}", Constraints.Name);
            return;
        }
        if (Unavailable(src, fk.Columns) is { } col)
        {
            review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName, $"it uses {col}, which the target will not have", Constraints.Name);
            return;
        }
        var refCols = fk.ReferencedColumns.ToHashSet(StringComparer.Ordinal);
        bool On(IndexInfo i) => i.KeyColumns.Count == refCols.Count && i.KeyColumns.All(k => refCols.Contains(k.Name));
        bool Unique(DbObject table) => table.Indexes.Any(i => (i.IsPrimaryKey || i.IsUniqueConstraint || (i.IsUnique && i.FilterDefinition is null)) && On(i));
        // a table 03 creates has its primary and unique keys at once; its unique indexes come in 06
        var keyed = (created.Contains(refKey) && s.TryGetValue(refKey, out var refSrc) && refSrc.Indexes.Any(i => (i.IsPrimaryKey || i.IsUniqueConstraint) && On(i))) ||
                    (t.TryGetValue(refKey, out var refTgt) && Unique(refTgt)) ||
                    (addedKeys.TryGetValue(refKey, out var keys) && keys.Any(k => k.SetEquals(refCols))) ||
                    (after06 && addedUnique.TryGetValue(refKey, out var unique) && unique.Any(k => k.SetEquals(refCols)));
        if (!keyed)
        {
            var index = s.GetValueOrDefault(refKey)?.Indexes.FirstOrDefault(i => !IsKey(i) && i.IsUnique && i.FilterDefinition is null && On(i));
            if (index is not null && !after06 && Selected(ScriptSection.Indexes))
            {
                afterIndexes.Add((src, fk, isNew));   // planned again once 06 has added the index
                return;
            }
            review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                index is null
                    ? $"on the target, {refName} has no primary or unique key on ({string.Join(", ", fk.ReferencedColumns)}) and this run does not add one"
                    : $"on the target, {refName} has no primary or unique key on ({string.Join(", ", fk.ReferencedColumns)}); the source's unique index {index.Name} " +
                      $"would be one, and {(Selected(ScriptSection.Indexes) ? "06_indexes does not add it in this run" : "06_indexes is not selected")}",
                after06 ? Indexes.Name : Constraints.Name);
            return;
        }
        var folder = after06 ? Indexes : Constraints;

        var def = Strip(fk.Definition);
        if (isNew)
        {
            folder.Files.Add(new PgFile($"foreignkey_{src.Schema}.{src.Name}.{fk.Name}", Header(folder, "foreign key", $"{src.FullName}.{fk.Name}") +
                $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(fk.Name)} {def}{(fk.IsNotTrusted ? " NOT VALID" : "")};\n", new ObjectName(src.Schema, src.Name)));
            return;
        }
        if (!fk.IsNotTrusted)
        {
            var existing = fk.Columns.All(c => Target(src)!.Columns.Any(x => x.Name == c));
            var orphans = existing
                ? await probes.OrphansAsync(PgDdl.Q(src), fk.Columns, refExists ? PgDdl.Q(fk.ReferencedSchema, fk.ReferencedTable) : null, fk.ReferencedColumns, ct)
                : new ProbeCount(0);
            if (orphans.Count > 0)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                    $"{orphans.Count:N0} existing row(s) have no match in {refName}; fix the data first", folder.Name);
                return;
            }
        }
        folder.Files.Add(new PgFile($"foreignkey_{src.Schema}.{src.Name}.{fk.Name}", Header(folder, "foreign key", $"{src.FullName}.{fk.Name}") +
            "-- Added NOT VALID: checks new rows at once, without a long lock; the existing rows are checked by VALIDATE in a later file.\n" +
            $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(fk.Name)} {def} NOT VALID;\n", new ObjectName(src.Schema, src.Name)));
        if (!fk.IsNotTrusted) Validate(src, fk.Name, after06);
    }

    // ------------------------------------------------------------------ 06_indexes

    private async Task PlanIndexesAsync()
    {
        var tables = ChangedTables().Select(p => (p.Src, Tgt: (DbObject?)p.Tgt))
            .Concat(Src.Objects.Where(o => o.Type == DbObjectType.Table && created.Contains(o.Key))
                .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => (Src: o, Tgt: (DbObject?)null)));
        var partitions = new List<(DbObject Src, DbObject Tgt)>();
        foreach (var (src, tgt) in tables)
        {
            if (IsChild(src) && tgt is not null)
            {
                // a partition's plain indexes come from its parent's: looked at once every parent's are planned; an inheritance child's are held in 05
                if (IsPartition(src)) partitions.Add((src, tgt));
                continue;
            }
            foreach (var ix in src.Indexes.Where(i => !IsKey(i)).OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var tix = tgt?.Indexes.FirstOrDefault(x => !IsKey(x) && x.Name == ix.Name);
                if (tix is not null)
                {
                    if (tix.Definition != ix.Definition)
                        review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                            $"the target's index of this name is different ({tix.Definition}); replacing it is not scripted", Indexes.Name);
                    continue;
                }
                if (Names(src.Schema).Contains(ix.Name))
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                        $"another table, view, sequence or index of schema {src.Schema} on the target is already called {ix.Name}", Indexes.Name);
                    continue;
                }
                if (tgt is not null)
                {
                    var cols = src.Columns.Where(c => PgDdl.MentionsColumn(ix.Definition, c.Name) || ix.KeyColumns.Any(k => k.Name == c.Name) ||
                                                      ix.IncludedColumns.Contains(c.Name)).Select(c => c.Name);
                    if (Unavailable(src, cols) is { } col)
                    {
                        review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, $"it uses {col}, which the target will not have", Indexes.Name);
                        continue;
                    }
                    if (ix.IsUnique)
                    {
                        var plain = ix.KeyColumns.Select(k => k.Name).Where(n => tgt.Columns.Any(c => c.Name == n)).ToList();
                        var dups = plain.Count == ix.KeyColumns.Count
                            ? await probes.DuplicateGroupsAsync(PgDdl.Q(src), plain, ix.FilterDefinition, ct)
                            : new ProbeCount(null, "an index on an expression or a new column is not probed");
                        if (dups.Count > 0)
                        {
                            review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                                $"{dups.Count:N0} value(s) appear more than once in the existing rows", Indexes.Name);
                            continue;
                        }
                        if (dups.Count is null) probes.NoteNotChecked($"duplicates under unique index {src.FullName}.{ix.Name}", dups.NotCheckedReason ?? "not run");
                    }
                    var rows = await probes.RowEstimateAsync(src.Schema, src.Name, ct);
                    if (rows >= Options.LargeTableRows)
                        review.Note(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                            $"built without CONCURRENTLY (it must run inside the file's transaction): writes to the table (about {rows:N0} rows) wait while it builds",
                            Indexes.Name);
                }
                Indexes.Files.Add(new PgFile($"index_{src.Schema}.{src.Name}.{ix.Name}", Header(Indexes, "index", $"{src.FullName}.{ix.Name}") +
                    PgDdl.Statement(OnEveryPartition(src, ix.Definition ?? "")), new ObjectName(src.Schema, src.Name)));
                Names(src.Schema).Add(ix.Name);
                if (src.Properties.ContainsKey("partitionBy")) (onPartitions.TryGetValue(src.Key, out var built) ? built : onPartitions[src.Key] = []).Add(ix);
                if (ix.IsUnique && ix.FilterDefinition is null && ix.KeyColumns.All(k => src.Columns.Any(c => c.Name == k.Name)))
                    (addedUnique.TryGetValue(src.Key, out var sets) ? sets : addedUnique[src.Key] = []).Add(ix.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal));
            }
            if (tgt is not null)
                foreach (var tix in tgt.Indexes.Where(x => !IsKey(x) && src.Indexes.All(i => IsKey(i) || i.Name != x.Name)))
                    review.Hold(ReviewLog.NotAppliedDestructive, "index", tix.Name, src.FullName, TargetOnly, Indexes.Name);
        }
        foreach (var (src, tgt) in partitions) PlanPartitionIndexes(src, tgt);

        // the foreign keys whose referenced columns only a unique index of this folder covers, then their VALIDATE
        foreach (var (src, fk, isNew) in afterIndexes) await PlanForeignKeyAsync(src, fk, isNew, after06: true);
        Indexes.Files.AddRange(indexValidations);
    }

    /// <summary>Parent table key -> the indexes 06 writes on it that PostgreSQL also builds on each of its partitions.</summary>
    private readonly Dictionary<string, List<IndexInfo>> onPartitions = new(StringComparer.Ordinal);

    private static bool IsPartition(DbObject table) => table.Properties.ContainsKey("partitionOf");

    /// <summary>
    /// pg_get_indexdef writes an index of a partitioned table as ON ONLY the parent. Built that way it stays invalid and
    /// no partition gets one; without ONLY, PostgreSQL builds it on every partition (or attaches a matching index a
    /// partition already has) and the parent's index is valid.
    /// </summary>
    internal static string OnEveryPartition(DbObject table, string definition) =>
        table.Properties.ContainsKey("partitionBy") ? OnOnlyRx().Replace(definition, "${head}", 1) : definition;

    [GeneratedRegex(@"^(?<head>CREATE (?:UNIQUE )?INDEX (?:""(?:[^""]|"""")*""|\S+) ON )ONLY ", RegexOptions.CultureInvariant)]
    private static partial Regex OnOnlyRx();

    /// <summary>
    /// A partition's plain indexes: each the source has and the target lacks is fine when it is the copy of a parent's
    /// index 06 writes, and PostgreSQL will give that copy the source's name; anything else is held with why.
    /// </summary>
    private void PlanPartitionIndexes(DbObject src, DbObject tgt)
    {
        var parent = Src.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && $"{PgDdl.Ident(o.Schema)}.{PgDdl.Ident(o.Name)}" == src.Properties["partitionOf"]);
        var fromParent = parent is not null ? onPartitions.GetValueOrDefault(parent.Key) ?? [] : [];
        foreach (var ix in src.Indexes.Where(i => !IsKey(i)).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var tix = tgt.Indexes.FirstOrDefault(x => !IsKey(x) && x.Name == ix.Name);
            if (tix is not null)
            {
                if (tix.Definition != ix.Definition) review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, ChildReason, Indexes.Name);
                continue;
            }
            var of = fromParent.FirstOrDefault(p => SameShape(p, ix));
            if (of is null)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, ChildReason, Indexes.Name);
                continue;
            }
            // PostgreSQL attaches a matching index the partition already has, or builds one under a name it chooses
            var name = tgt.Indexes.FirstOrDefault(x => !IsKey(x) && SameShape(x, ix))?.Name ?? ChosenName(src, ix);
            if (name != ix.Name)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                    $"06 builds {parent!.FullName}.{of.Name} on this partition too, but PostgreSQL names that copy {(name is null ? "itself" : name)}, not {ix.Name} as the source does",
                    Indexes.Name);
                continue;
            }
            Names(src.Schema).Add(name);
            review.Note(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                $"built on this partition by 06's index {parent!.FullName}.{of.Name} on its parent", Indexes.Name);
        }
        foreach (var tix in tgt.Indexes.Where(x => !IsKey(x) && src.Indexes.All(i => IsKey(i) || i.Name != x.Name)))
            review.Hold(ReviewLog.NotAppliedDestructive, "index", tix.Name, src.FullName, TargetOnly, Indexes.Name);
    }

    /// <summary>The same index but for its name and table: uniqueness, then the deparse from USING on (method, columns, INCLUDE, WHERE).</summary>
    private static bool SameShape(IndexInfo a, IndexInfo b)
    {
        static string? Tail(string? def) => def?.IndexOf(" USING ", StringComparison.Ordinal) is int at and >= 0 ? def[at..] : null;
        return a.IsUnique == b.IsUnique && Tail(a.Definition) is { } tail && tail == Tail(b.Definition);
    }

    /// <summary>
    /// The name PostgreSQL gives a partition's copy of its parent's index (ChooseIndexName: table_columns_idx), when it
    /// can be told for sure: plain columns, a name that fits in 63 bytes and is not taken. Null otherwise.
    /// </summary>
    private string? ChosenName(DbObject partition, IndexInfo ix)
    {
        var columns = ix.KeyColumns.Select(k => k.Name).Concat(ix.IncludedColumns).ToList();
        if (columns.Count == 0 || columns.Distinct(StringComparer.Ordinal).Count() != columns.Count ||
            !columns.All(c => partition.Columns.Any(x => x.Name == c))) return null;
        var name = $"{partition.Name}_{string.Join("_", columns)}_idx";
        return Encoding.UTF8.GetByteCount(name) <= 63 && !Names(partition.Schema).Contains(name) ? name : null;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The comment every file starts with.</summary>
    private static string Header(PgFolder folder, string what, string name) => $"-- {folder.Name}: {what} {PgDdl.OneLine(name)}\n";
}
