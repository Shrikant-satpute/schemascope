using System.Text;
using System.Text.RegularExpressions;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting.Planning;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>One file a PostgreSQL folder will hold.</summary>
/// <param name="Module">The source's code text the file must carry exactly (a RawModule), or null.</param>
/// <param name="Rollback">11_destructive: the text of the file's rollback\ twin (the dropped code as the target has it), or null.</param>
/// <param name="RollbackModule">The target's code text that rollback file must carry exactly.</param>
/// <param name="Modules">More than one source code text the file must carry exactly (11's view rebuilds), or null.</param>
internal sealed record PgFile(string Name, string Text, ObjectName? Object, string? Module = null, string? Rollback = null, string? RollbackModule = null,
    IReadOnlyList<string>? Modules = null);

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
    public PgFolder Destructive { get; } = new(PgScriptGenerator.DestructiveFolder, PgScriptGenerator.DestructiveTitle, false);

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

    /// <summary>
    /// Plans every selected folder, in the order the facts flow: the renames 09 makes and the enum labels 04 adds (01
    /// and 03 need to know of both), 01, 03, 04, 05, 06, 07, 10, 08, then what is left.
    /// </summary>
    public async Task PlanAsync(Action<int, string> report)
    {
        report(5, "Renames and enum labels");
        PlanRenames();
        if (Selected(ScriptSection.ColumnTypes)) PlanEnums();
        report(10, "01 New columns");
        if (Selected(ScriptSection.NewColumns)) await PlanColumnsAsync();
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
        ct.ThrowIfCancellationRequested();
        report(77, "11 Changes that can lose data");
        if (Destroys) await PlanDestructiveAsync();
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
        : o.Type == DbObjectType.Cast ? $"({o.Name})"
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

    // A table that is a child on the source but stands alone on the target (a detached partition) is changed as any
    // table there: its parent's changes do not reach it.
    private const string ChildReason =
        "a partition or inheritance child: its columns and keys come from its parent and change with it, not on their own";

    // ------------------------------------------------------------------ names

    /// <summary>
    /// The names <paramref name="o"/> holds in its schema's relation namespace: its own, and its indexes'. For an object 03
    /// creates (<paramref name="created"/>), only the indexes its own file makes - a table's keys, a materialized view's
    /// indexes; a table's other indexes take their names when 06 writes them.
    /// </summary>
    private void Take(DbObject o, bool created = false)
    {
        if (o.Type is DbObjectType.Table or DbObjectType.View or DbObjectType.MaterializedView or DbObjectType.Sequence or DbObjectType.CompositeType or DbObjectType.ForeignTable)
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
            if (o.Type is DbObjectType.Schema or DbObjectType.Trigger or DbObjectType.Extension or DbObjectType.Cast) continue;
            if (t.ContainsKey(o.Key)) continue;
            if (!before03 && created.Contains(o.Key)) continue;
            // the deparse names a user operator OPERATOR(schema.op): its name is never quoted
            if (o.Type == DbObjectType.Operator
                    ? text.Contains($"OPERATOR({PgDdl.Ident(o.Schema)}.{o.Name})", StringComparison.Ordinal)
                    : PgDdl.Mentions(text, o.Schema, o.Name))
                return o;
        }
        return null;
    }

    private static ConstraintInfo? DefaultOf(DbObject table, string column) =>
        table.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.Default && c.ColumnName == column);

    // ------------------------------------------------------------------ 01_columns

    private async Task PlanColumnsAsync()
    {
        // a child after the tables it hangs under: a column its parent's file adds reaches it from there
        foreach (var (src, tgt) in ChangedTables().OrderBy(p => IsChild(p.Src) ? 1 + Depth(p.Src) : 0))
        {
            var have = tgt.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var newCols = src.Columns.Where(c => !have.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList();
            if (newCols.Count == 0) continue;
            if (IsChild(src) && IsChild(tgt))
            {
                // a column of a parent comes with the parent's ADD COLUMN, which PostgreSQL adds to every child (at its
                // end); an inheritance child's own column is added to it as to any table
                var parents = ParentsOf(src, Src);
                var own = new List<ColumnInfo>();
                foreach (var c in newCols)
                {
                    if (parents.All(p => p.Columns.All(x => x.Name != c.Name))) { own.Add(c); continue; }
                    if (parents.Any(p => added.GetValueOrDefault(p.Key)?.Contains(c.Name) == true))
                    {
                        (added.TryGetValue(src.Key, out var inherited) ? inherited : added[src.Key] = new HashSet<string>(StringComparer.Ordinal)).Add(c.Name);
                        addedInOrder.Add((src.Key, c.Name));
                        review.Note(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                            "added by its parent's file: PostgreSQL adds a parent's new column to its children too", Columns.Name);
                    }
                    else review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"{ChildReason}, and 01 does not add it to {string.Join(", ", parents.Select(p => p.FullName))} in this run", Columns.Name);
                }
                newCols = own;
                if (newCols.Count == 0) continue;
            }
            // a typed table's columns are its type's (ALTER TYPE ... ADD ATTRIBUTE ... CASCADE)
            if (tgt.Properties.GetValueOrDefault("ofType") is { } ofType)
            {
                // the attribute 05 adds to the type reaches the table with CASCADE
                var type = Src.Objects.FirstOrDefault(o => o.Type == DbObjectType.CompositeType && PgDdl.Mentions(ofType, o.Schema, o.Name) &&
                                                           t.ContainsKey(o.Key) && InScope(o) && !IsExcluded(o));
                foreach (var c in newCols)
                {
                    if (Selected(ScriptSection.Constraints) && type is not null && type.Columns.Any(a => a.Name == c.Name) &&
                        t[type.Key].Columns.All(a => a.Name != c.Name))
                        review.Note(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                            $"added with its type's new attribute: 05_constraints adds it to {Display(type)} with CASCADE, which adds it to this typed table too",
                            Columns.Name);
                    else
                        review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                            $"the target's table is a typed table (OF {ofType}): its columns are its type's attributes, and PostgreSQL adds none to it on its own",
                            Columns.Name);
                }
                continue;
            }
            var rowTypeUsers = RowTypeUsers(tgt);

            // the source's order; a generated column moves only behind a new column it reads that comes after it
            var edges = new List<(int, int)>();
            for (var g = 0; g < newCols.Count; g++)
                if (newCols[g].IsComputed)
                    for (var n = 0; n < newCols.Count; n++)
                        if (n != g && PgDdl.MentionsColumn(newCols[g].ComputedDefinition, newCols[n].Name)) edges.Add((n, g));
            // a column that waits for 03_missing: one after it in the source's order waits with it, or it would end up before it
            var waits = compare.IgnoreColumnOrder ? null : newCols.FirstOrDefault(c => RenameReason(src, c.Name) is null && Needs03(src, c) is not null);
            foreach (var c in PlanOrder.StableOrder(newCols.Count, edges, []).Select(i => newCols[i]))
            {
                if (RenameReason(src, c.Name) is { } renamed)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName, renamed, Columns.Name);
                    continue;
                }
                var def = DefaultOf(src, c.Name);
                // NOT NULL with no default fills nothing in: PostgreSQL adds it only to a table without rows
                var empty = false;
                if (!c.IsNullable && def is null && !c.IsIdentity && !c.IsComputed)
                {
                    var rows = await probes.RowCountAsync(PgDdl.Q(src), ct);
                    if (rows.Count != 0)
                    {
                        review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                            "NOT NULL with no default: PostgreSQL cannot add it to a table that has rows" +
                            (rows.Checked ? $", and {N(rows.Count!.Value)} row(s) are there" : $" (whether this one has any could not be checked: {rows.NotCheckedReason})") +
                            ". Give it a default in the source, or add it by hand.", Columns.Name);
                        continue;
                    }
                    empty = true;
                }
                if (Needs03(src, c) is { } needs)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"it uses {Kind(needs)} {Display(needs)}, which the target does not have until 03_missing runs (after 01). " +
                        "Run the scripts, then create them again: this column is in the next run.", Columns.Name);
                    continue;
                }
                if (waits is not null && c.Ordinal > waits.Ordinal)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"it comes after {waits.Name} in the source, which waits for 03_missing; added now it would end up before that column, and " +
                        "PostgreSQL cannot move a column. Run the scripts, then create them again: this column is in the next run, after it.", Columns.Name);
                    continue;
                }
                if ((TooNew(c) ?? NotReady(c.DataType + "\n" + def?.Definition + "\n" + c.ComputedDefinition + "\n" + c.Collation)) is { } notReady)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName, notReady, Columns.Name);
                    continue;
                }
                if (c.IsComputed && MentionsBlocked(src, c.ComputedDefinition, newCols, c) is { } col)
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        $"its expression reads {col}, which 01 does not add", Columns.Name);
                    continue;
                }
                // a column with a value to fill in rewrites the rows, which PostgreSQL refuses while another table stores this one's row type
                if (rowTypeUsers.Count > 0 && (def is not null || c.IsIdentity || c.IsComputed))
                {
                    review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName,
                        RowTypeReason(rowTypeUsers, "add a column with a default, identity or generated value to"), Columns.Name);
                    continue;
                }

                var sql = (empty ? "-- NOT NULL with no default: the table had no rows when this was generated. If rows have arrived since, this file fails and changes nothing.\n" : "") +
                          $"ALTER TABLE {PgDdl.Q(src)} ADD COLUMN {ColumnDefinition(src, c)};\n";
                Columns.Files.Add(new PgFile($"column_{src.Schema}.{src.Name}.{c.Name}", Header(Columns, "column", $"{src.FullName}.{c.Name}") + sql,
                    new ObjectName(src.Schema, src.Name)));
                (added.TryGetValue(src.Key, out var set) ? set : added[src.Key] = new HashSet<string>(StringComparer.Ordinal)).Add(c.Name);
                addedInOrder.Add((src.Key, c.Name));
            }
        }
    }

    /// <summary>An object a new column's type, default, expression or collation names that the target lacks until 03 runs (after 01), or null.</summary>
    private DbObject? Needs03(DbObject table, ColumnInfo c) =>
        MissingNamed(c.DataType + "\n" + DefaultOf(table, c.Name)?.Definition + "\n" + c.ComputedDefinition + "\n" + c.Collation, before03: true);

    /// <summary>
    /// The tables a partition or inheritance child hangs under, in <paramref name="snapshot"/> (partitionOf and inherits
    /// are spelled as quote_ident spells them).
    /// </summary>
    private static List<DbObject> ParentsOf(DbObject child, SchemaSnapshot snapshot)
    {
        var names = child.Properties.GetValueOrDefault("partitionOf") is { } of ? [of]
            : SplitNames(child.Properties.GetValueOrDefault("inherits") ?? "").ToList();
        return [.. snapshot.Objects.Where(o => o.Type == DbObjectType.Table && names.Contains($"{PgDdl.Ident(o.Schema)}.{PgDdl.Ident(o.Name)}"))];
    }

    /// <summary>
    /// The target's columns that store <paramref name="table"/>'s row type (as a column of that type or an array of it,
    /// or of a composite type that holds it): while one does, PostgreSQL refuses any change to the table that rewrites
    /// its rows - a column's type, or a new column with a value to fill in.
    /// </summary>
    private List<string> RowTypeUsers(DbObject table)
    {
        // the target's table and composite type columns by the type they store (format_type's spelling, arrays as their element)
        typeUsers ??= Tgt.Objects.Where(o => o.Type is DbObjectType.Table or DbObjectType.CompositeType)
            .SelectMany(o => o.Columns.Select(c => (Owner: o, c.Name, Type: c.DataType.Trim().TrimEnd('[', ']'))))
            .ToLookup(x => x.Type, x => (x.Owner, x.Name), StringComparer.Ordinal);
        var users = new List<string>();
        var types = new Queue<DbObject>([table]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { table.Key };
        while (types.Count > 0)
        {
            var type = types.Dequeue();
            var spellings = new[] { $"{PgDdl.Ident(type.Schema)}.{PgDdl.Ident(type.Name)}", PgDdl.Q(type.Schema, type.Name),
                                    $"{PgDdl.Ident(type.Schema)}.{PgDdl.Q(type.Name)}", $"{PgDdl.Q(type.Schema)}.{PgDdl.Ident(type.Name)}" }.Distinct();
            foreach (var (owner, column) in spellings.SelectMany(s => typeUsers[s]).Where(u => u.Owner.Key != table.Key))
            {
                if (owner.Type == DbObjectType.Table) users.Add($"{owner.FullName}.{column}");
                else if (seen.Add(owner.Key)) types.Enqueue(owner);
            }
        }
        return [.. users.Distinct(StringComparer.Ordinal)];
    }

    private ILookup<string, (DbObject Owner, string Column)>? typeUsers;

    private static string RowTypeReason(List<string> users, string what) =>
        $"column {string.Join(", ", users)} on the target stores this table's row type, and PostgreSQL cannot {what} a table while one does " +
        "(it would have to rewrite the rows stored there too)";

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
            // an inheritance child's own column (no parent has it) changes on its own; any other column of a child with its parent
            var parents = IsChild(src) ? ParentsOf(src, Src) : [];
            var rowTypeUsers = RowTypeUsers(tgt);
            foreach (var c in common)
            {
                var tc = tgt.Columns.First(x => x.Name == c.Name);
                var typeChanges = !string.Equals(c.DataType, tc.DataType, StringComparison.Ordinal);
                // a NOT NULL that is NOT VALID (PostgreSQL 18) is neither relaxed nor tightened here: 05 validates the target's
                var relax = c.IsNullable && !tc.IsNullable && !c.NotNullNotValid;
                var tighten = !c.IsNullable && tc.IsNullable && !tc.NotNullNotValid;
                // ALWAYS and BY DEFAULT are the same sequence; SET GENERATED switches between them and keeps every value
                var identityKind = c.IsIdentity && tc.IsIdentity && c.IdentityKind != tc.IdentityKind ? c.IdentityKind : null;
                var column = $"{c.Name}";

                if (IsChild(src) && IsChild(tgt) && (typeChanges || relax || tighten || identityKind is not null) &&
                    (IsPartition(src) || parents.Any(p => p.Columns.Any(x => x.Name == c.Name))))
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName, ChildReason, ColumnTypes.Name);
                    continue;
                }
                if (c.IsIdentity != tc.IsIdentity)
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"identity differs (source: {Identity(c)}, target: {Identity(tc)}); changing it is not scripted", ColumnTypes.Name);
                else if (c.IsIdentity && !compare.IgnoreIdentitySeed && IdentitySequence(c) != IdentitySequence(tc))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"its identity sequence differs (source: {IdentitySequence(c)}; target: {IdentitySequence(tc)}); changing it is not scripted",
                        ColumnTypes.Name);
                if (c.IsComputed != tc.IsComputed || c.IsPersisted != tc.IsPersisted ||
                    !string.Equals(c.ComputedDefinition, tc.ComputedDefinition, StringComparison.Ordinal))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName, GeneratedDiffers(c, tc), ColumnTypes.Name);
                if (!compare.IgnoreCollation && !string.Equals(c.Collation, tc.Collation, StringComparison.Ordinal))
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"collation differs (source: {c.Collation ?? "default"}, target: {tc.Collation ?? "default"}); changing it rebuilds its indexes and is not scripted",
                        ColumnTypes.Name);
                if (c.NotNullNotValid && !tc.NotNullNotValid)
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName, tc.IsNullable
                        ? "the source's NOT NULL is NOT VALID and the target has none: add it by hand if you want it (ALTER TABLE ... ADD CONSTRAINT ... NOT NULL ... NOT VALID)"
                        : "the source's NOT NULL is NOT VALID and the target's is validated; the target is stricter, so nothing is changed", ColumnTypes.Name);
                // with 11_destructive on, NOT NULL and a type change that is no widening are its to plan (or to hold, with why);
                // a generated column is not, so 04 keeps holding it
                var toDestructive = Destroys && !c.IsComputed && !tc.IsComputed;
                if (tighten && !toDestructive)
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        "the source has NOT NULL and the target allows NULL: making it NOT NULL fails on existing NULLs and is not scripted",
                        ColumnTypes.Name);
                if (typeChanges && toDestructive && Widening(tc.DataType, c.DataType) is null) typeChanges = false;
                if (!typeChanges && !relax && identityKind is null) continue;

                // PostgreSQL refuses to change a column's type while a view, rule, generated column, policy, trigger or SQL-standard routine reads it
                if (typeChanges && ReadersOf(tgt, c.Name) is { Count: > 0 } readers)
                {
                    // read by views alone: 11_destructive (when it is on) drops and creates them again around the change
                    var viewsOnly = Readers(tgt, c.Name).All(r => r.By.Key != tgt.Key && r.By.Type is DbObjectType.View or DbObjectType.MaterializedView);
                    if (viewsOnly && Destroys && Widening(tc.DataType, c.DataType) is not null && PartitionKeyOf(tgt, c.Name) is null && rowTypeUsers.Count == 0)
                        widenUnderViews.Add((src, tgt, c, tc));
                    else
                        review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                            $"{string.Join(", ", readers)} {(readers.Count == 1 ? "reads" : "read")} this column on the target, and PostgreSQL cannot change " +
                            $"its type ({tc.DataType} to {c.DataType}) while {(readers.Count == 1 ? "it does" : "they do")}" +
                            (viewsOnly && !Destroys ? ". With changes that can lose data included, 11_destructive drops and creates them again around the change" : ""),
                            ColumnTypes.Name);
                    typeChanges = false;
                }
                else if (typeChanges && PartitionKeyOf(tgt, c.Name) is { } keyed)
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName,
                        $"it is part of the partition key of {keyed}, and PostgreSQL cannot change the type of a partition key column " +
                        $"({tc.DataType} to {c.DataType}); the table has to be rebuilt by hand", ColumnTypes.Name);
                    typeChanges = false;
                }
                else if (typeChanges && rowTypeUsers.Count > 0)
                {
                    review.Hold(ReviewLog.NotApplied, "column", column, src.FullName, RowTypeReason(rowTypeUsers, "change a column's type in"), ColumnTypes.Name);
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
                if (identityKind is not null)
                    sql.Append($"-- The target's identity is GENERATED {tc.IdentityKind}; the sequence and every value stay.\n")
                       .Append($"ALTER TABLE {PgDdl.Q(src)} ALTER COLUMN {PgDdl.Q(c.Name)} SET GENERATED {identityKind};\n");
                if (sql.Length == 0) continue;
                ColumnTypes.Files.Add(new PgFile($"alter_{src.Schema}.{src.Name}.{c.Name}",
                    Header(ColumnTypes, "column", $"{src.FullName}.{c.Name}") + sql, new ObjectName(src.Schema, src.Name)));
            }
        }
    }

    /// <summary>
    /// The table whose partition key names <paramref name="column"/>: <paramref name="table"/> itself, or a partition of
    /// it on the target (PostgreSQL changes a parent's column on every partition, and refuses where it is a key). Null when none.
    /// </summary>
    private string? PartitionKeyOf(DbObject table, string column)
    {
        var tables = new Queue<DbObject>([table]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (tables.Count > 0)
        {
            var at = tables.Dequeue();
            if (!seen.Add(at.Key)) continue;
            if (at.Properties.GetValueOrDefault("partitionBy") is { } key && PgDdl.MentionsColumn(key, column)) return at.FullName;
            var name = $"{PgDdl.Ident(at.Schema)}.{PgDdl.Ident(at.Name)}";
            foreach (var child in Tgt.Objects.Where(o => o.Type == DbObjectType.Table && o.Properties.GetValueOrDefault("partitionOf") == name))
                tables.Enqueue(child);
        }
        return null;
    }

    /// <summary>What differs about a generated column (or whether it is one), for the hold that says PostgreSQL cannot change it in place.</summary>
    internal static string GeneratedDiffers(ColumnInfo c, ColumnInfo tc)
    {
        static string Kind(ColumnInfo x) => !x.IsComputed ? "not generated" : x.IsPersisted ? "STORED" : "VIRTUAL";
        var what = c.IsComputed != tc.IsComputed
            ? $"it is {Kind(c)} on the source and {Kind(tc)} on the target"
            : !string.Equals(c.ComputedDefinition, tc.ComputedDefinition, StringComparison.Ordinal)
                ? "the generated expression differs" + (c.IsPersisted != tc.IsPersisted ? $", and it is {Kind(c)} on the source and {Kind(tc)} on the target" : "")
                : $"the expression is the same, but the column is {Kind(c)} on the source and {Kind(tc)} on the target";
        return $"{what}; PostgreSQL can only drop and re-add such a column";
    }

    private static string Identity(ColumnInfo c) => c.IsIdentity ? $"GENERATED {c.IdentityKind} AS IDENTITY" : "none";

    private static string IdentitySequence(ColumnInfo c) => $"START WITH {c.IdentitySeed} INCREMENT BY {c.IdentityIncrement} {c.IdentityOptions}".TrimEnd();

    /// <summary>The target objects whose definition reads <paramref name="column"/> of <paramref name="table"/> (pg_depend, column level).</summary>
    private List<string> ReadersOf(DbObject table, string column) => [.. Readers(table, column).Select(r => r.What).Distinct()];

    /// <summary>
    /// Each target object that reads the column as <see cref="ReadersOf"/> words it, with the object. A routine reads a
    /// column when its body is SQL-standard (BEGIN ATOMIC or RETURN): PostgreSQL records what such a body reads.
    /// </summary>
    private IEnumerable<(DbObject By, string What)> Readers(DbObject table, string column)
    {
        foreach (var o in Tgt.Objects)
            foreach (var d in o.DependsOn ?? [])
                if (d.Key == table.Key && d.Column == column && d.Via is "view" or "rule" or "default" or "policy" or "trigger" or "routine")
                    yield return (o, d.Via switch
                    {
                        "view" or "rule" when o.Key == table.Key => "a rule on the table",
                        "rule" => $"a rule on {Display(o)}",
                        "view" or "routine" => $"{Kind(o)} {Display(o)}",
                        "default" => o.Key == table.Key ? "a generated column of the table" : $"a default of {Display(o)}",
                        "policy" => "a row level security policy",
                        _ => o.Type == DbObjectType.Trigger ? $"trigger {Display(o)}" : "a trigger",
                    });
    }

    /// <summary>
    /// Null when <paramref name="to"/> is not a widening of <paramref name="from"/>; otherwise whether PostgreSQL rewrites
    /// the table for it (a longer varchar or numeric does not; int to bigint does). An array widens as its element type
    /// does, and is taken to rewrite the table.
    /// </summary>
    internal static bool? Widening(string from, string to)
    {
        if (from.TrimEnd().EndsWith("[]", StringComparison.Ordinal) || to.TrimEnd().EndsWith("[]", StringComparison.Ordinal))
            return from.TrimEnd().EndsWith("[]", StringComparison.Ordinal) && to.TrimEnd().EndsWith("[]", StringComparison.Ordinal) &&
                   Widening(from.TrimEnd()[..^2], to.TrimEnd()[..^2]) is not null ? true : null;
        var (fb, fa) = ParseType(from);
        var (tb, ta) = ParseType(to);
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

    /// <summary>A format_type spelling as its base type (with any time zone tail) and its modifiers: ("numeric", [10, 2]).</summary>
    private static (string Base, int[] Args) ParseType(string type)
    {
        var m = TypeRx().Match(type.Trim());
        if (!m.Success) return (type.Trim(), []);
        var args = m.Groups["args"].Success ? m.Groups["args"].Value.Split(',').Select(a => int.Parse(a.Trim())).ToArray() : [];
        return (m.Groups["base"].Value + m.Groups["tail"].Value, args);
    }

    /// <summary>format_type's spelling: "character varying(20)", "numeric(10,2)", "timestamp(3) without time zone".</summary>
    [GeneratedRegex(@"^(?<base>[a-z ]+?)(?:\((?<args>\d+(?:\s*,\s*\d+)?)\))?(?<tail> with(?:out)? time zone)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeRx();

    // ------------------------------------------------------------------ 05_constraints

    private readonly List<PgFile> validations = [];

    private async Task PlanConstraintsAsync()
    {
        var fks = new List<(DbObject Src, ForeignKeyInfo Fk, bool NewTable)>();
        // the roles the changed tables' policies and the changed schemas' owners name, asked for once
        var wanted = RolesOf(ChangedTables().Select(p => p.Src))
            .Concat(Src.Objects.Where(o => o.Type == DbObjectType.Schema && StatusOf(o) == Diff.Different)
                .Select(o => o.Properties.GetValueOrDefault("owner")).OfType<string>().Where(r => r.Length > 0))
            .Distinct(StringComparer.Ordinal).ToList();
        var roles = wanted.Count > 0 ? await probes.ExistingRolesAsync(wanted, ct) : [];
        foreach (var (table, theirs) in ChangedTables())
        {
            // a partition or inheritance child: what it has from its parent changes with the parent (PostgreSQL adds a
            // parent's check, and a partitioned table's key and foreign key, to each child); what is its own changes here
            var src = IsChild(theirs) ? Local(table, Src) : table;
            var tgt = Local(theirs, Tgt);
            PlanDefaults(src, tgt);
            await PlanChecksAsync(src, tgt);
            await PlanNotNullsAsync(src, tgt);
            await PlanKeysAsync(src, tgt);
            await PlanTableOptionsAsync(table, theirs, roles);
            // PostgreSQL before 18 cannot add a NOT VALID foreign key to a partitioned table: it is added validated
            var validatedOnly = PartitionedBefore18(theirs);
            foreach (var fk in src.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var tfk = tgt.ForeignKeys.FirstOrDefault(x => x.Name == fk.Name);
                if (tfk is null) { fks.Add((table, fk, false)); continue; }
                if (Strip(fk.Definition) != Strip(tfk.Definition))
                {
                    // the same columns and the same referenced key: only what it does on a delete or update, or when it is
                    // checked, differs - every row that met the old one meets this one, so it is swapped in one file
                    if (fk.Columns.SequenceEqual(tfk.Columns) && fk.ReferencedSchema == tfk.ReferencedSchema && fk.ReferencedTable == tfk.ReferencedTable &&
                        fk.ReferencedColumns.SequenceEqual(tfk.ReferencedColumns) && !fk.IsNotTrusted)
                    {
                        Constraints.Files.Add(new PgFile($"foreignkey_{src.Schema}.{src.Name}.{fk.Name}", Header(Constraints, "foreign key", $"{src.FullName}.{fk.Name}") +
                            $"-- The target's foreign key of this name: {PgDdl.OneLine(Strip(tfk.Definition))}\n" +
                            (validatedOnly
                                ? $"-- Added back validated in this file: PostgreSQL {Tgt.MajorVersion} cannot add a NOT VALID foreign key to a partitioned table; its rows met the same columns and key before.\n"
                                : "-- Added back NOT VALID (no long lock); its rows met the same columns and key before, and are checked by VALIDATE in a later file.\n") +
                            $"ALTER TABLE {PgDdl.Q(src)} DROP CONSTRAINT {PgDdl.Q(fk.Name)} RESTRICT;\n" +
                            $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(fk.Name)} {Strip(fk.Definition)}{(validatedOnly ? "" : " NOT VALID")};\n",
                            new ObjectName(src.Schema, src.Name)));
                        if (!validatedOnly) Validate(src, fk.Name);
                    }
                    else
                        review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                            $"the target's foreign key of this name is different ({Strip(tfk.Definition)}): it uses other columns or another key, so " +
                            "replacing it could refuse rows the target holds; it is not scripted", Constraints.Name);
                }
                else if (!fk.IsNotTrusted && tfk.IsNotTrusted)
                    Validate(src, fk.Name);
                else if (fk.IsNotTrusted && !tfk.IsNotTrusted)
                    review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                        "the source's copy is NOT VALID and the target's is validated; the target is stricter, so nothing is changed", Constraints.Name);
            }
            if (!Destroys)
                foreach (var tfk in tgt.ForeignKeys.Where(x => src.ForeignKeys.All(f => f.Name != x.Name)))
                    review.Hold(ReviewLog.NotAppliedDestructive, "foreignkey", tfk.Name, src.FullName, TargetOnly, Constraints.Name);
        }

        // the foreign keys of the tables 03 creates: their tables are empty and every table exists by now. A partition's
        // copies of its parent's come with the parent's own (PostgreSQL clones them onto every partition)
        foreach (var src in Src.Objects.Where(o => o.Type == DbObjectType.Table && created.Contains(o.Key))
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            var parent = IsPartition(src) ? ParentOf(src) : null;
            foreach (var fk in src.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
                if (parent is null || parent.ForeignKeys.All(p => Strip(p.Definition) != Strip(fk.Definition)))
                    fks.Add((src, fk, true));
        }

        foreach (var (src, fk, isNew) in fks) await PlanForeignKeyAsync(src, fk, isNew);

        await PlanSequencesAsync();
        await PlanDomainsAsync();
        PlanComposites();
        await PlanSchemaOwnersAsync(roles);

        Constraints.Files.AddRange(validations);
    }

    private const string TargetOnly =
        "only the target has it; removing it can change what the database accepts or lose data, so only 11_destructive drops it " +
        "(\"Include changes that can lose data\", with Read & write on)";

    /// <summary>
    /// A partition or inheritance child as 05 plans it: without the checks it has from a parent (same name and rule),
    /// and for a partition without the keys and foreign keys it has from its parent (same definition) - PostgreSQL
    /// adds those to every child with the parent's. Defaults stay: a child's default is set on the child. Any other
    /// table as it is.
    /// </summary>
    private static DbObject Local(DbObject table, SchemaSnapshot snapshot)
    {
        if (!IsChild(table)) return table;
        var parents = ParentsOf(table, snapshot);
        var partition = IsPartition(table);
        return new DbObject
        {
            Type = table.Type, Schema = table.Schema, Name = table.Name, Fold = table.Fold, Signature = table.Signature,
            Properties = table.Properties, Columns = table.Columns, DependsOn = table.DependsOn,
            Constraints = [.. table.Constraints.Where(c => c.Kind == ConstraintKind.Default ||
                parents.All(p => p.Constraints.All(x => x.Kind != ConstraintKind.Check || x.Name != c.Name || x.Definition != c.Definition)))],
            Indexes = [.. table.Indexes.Where(i => !partition || !IsKey(i) || parents.All(p => p.Indexes.All(x => !IsKey(x) || x.Definition != i.Definition)))],
            ForeignKeys = [.. table.ForeignKeys.Where(f => !partition || parents.All(p => p.ForeignKeys.All(x => Strip(x.Definition) != Strip(f.Definition))))],
        };
    }

    /// <summary>The target's table is partitioned and its PostgreSQL is older than 18, which cannot add a NOT VALID foreign key to it.</summary>
    private bool PartitionedBefore18(DbObject table) =>
        table.Properties.ContainsKey("partitionBy") && Tgt.MajorVersion is > 0 and < 18;

    /// <summary>
    /// PostgreSQL 18: a NOT NULL the target has NOT VALID where the source's is validated. ALTER COLUMN ... SET NOT NULL
    /// validates it - at the end of 05, once no row the probe found holds a NULL.
    /// </summary>
    private async Task PlanNotNullsAsync(DbObject src, DbObject tgt)
    {
        foreach (var c in src.Columns.Where(c => !c.IsNullable).OrderBy(c => c.Ordinal))
        {
            if (tgt.Columns.FirstOrDefault(x => x.Name == c.Name) is not { NotNullNotValid: true }) continue;
            var nulls = await probes.NullsAsync(PgDdl.Q(src), [c.Name], ct);
            if (nulls.Count > 0)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "not null", c.Name, src.FullName,
                    $"the target's NOT NULL is NOT VALID and {nulls.Count:N0} existing row(s) hold a NULL in it; fix the data first", Constraints.Name);
                continue;
            }
            validations.Add(new PgFile($"validate_{src.Schema}.{src.Name}.{c.Name}", Header(Constraints, "validate", $"{src.FullName}.{c.Name}") +
                "-- The target's NOT NULL is NOT VALID; SET NOT NULL checks the rows already there and makes it valid.\n" +
                $"ALTER TABLE {PgDdl.Q(src)} ALTER COLUMN {PgDdl.Q(c.Name)} SET NOT NULL;\n", new ObjectName(src.Schema, src.Name)));
        }
    }

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
            if (sd is not null && td is not null && sd.Definition == td.Definition) continue;
            if (sd is null)
            {
                if (!Destroys) review.Hold(ReviewLog.NotAppliedDestructive, "default", c.Name, src.FullName, TargetOnly, Constraints.Name);
                continue;
            }
            // an identity column has no default of its own; SET DEFAULT on one fails
            if (tc.IsIdentity) continue;
            if (MissingNamed(sd.Definition, before03: false) is { } needs)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "default", c.Name, src.FullName,
                    $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}", Constraints.Name);
                continue;
            }
            if (NotReady(sd.Definition) is { } notReady)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "default", c.Name, src.FullName, notReady, Constraints.Name);
                continue;
            }
            // what new rows get from now on; the rows already there keep their values
            Constraints.Files.Add(new PgFile($"default_{src.Schema}.{src.Name}.{c.Name}", Header(Constraints, "default", $"{src.FullName}.{c.Name}") +
                (td is null ? "" : $"-- The target's default was {PgDdl.OneLine(td.Definition)}; rows already there keep their values.\n") +
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
                // with 11_destructive on, it replaces such a check once the rows are checked against the source's rule;
                // the target's own rule is never validated here, it is about to go or stays held
                if (tck.Definition != ck.Definition)
                {
                    if (!Destroys)
                        review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName,
                            $"the target's check of this name is different ({tck.Definition}); replacing it is not scripted", Constraints.Name);
                }
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
            if (NotReady(ck.Definition) is { } notReady)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, src.FullName, notReady, Constraints.Name);
                continue;
            }
            // a column 01 adds is not on the target yet: every existing row gets its default there, so the rows are not probed
            if (!ck.IsNotTrusted && cols.FirstOrDefault(c => tgt.Columns.All(x => x.Name != c)) is { } adds)
                probes.NoteNotChecked($"rows of {PgDdl.Q(src)} that check {ck.Name} refuses",
                    $"it reads {adds}, which 01 adds and every existing row gets with its default; not probed (VALIDATE checks them when it runs)");
            else if (!ck.IsNotTrusted && CheckExpression(ck.Definition) is { } expression)
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
        if (!Destroys)
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

    /// <summary>A key's pg_get_constraintdef without when it is checked (DEFERRABLE, NOT DEFERRABLE, INITIALLY DEFERRED or IMMEDIATE).</summary>
    internal static string WithoutDeferrable(string? definition) => DeferrableRx().Replace(definition ?? "", "").Trim();

    [GeneratedRegex(@"\s+(?:NOT\s+)?DEFERRABLE\b|\s+INITIALLY\s+(?:DEFERRED|IMMEDIATE)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeferrableRx();

    private async Task PlanKeysAsync(DbObject src, DbObject tgt)
    {
        foreach (var key in src.Indexes.Where(IsKey).OrderBy(i => i.IsPrimaryKey ? 0 : 1).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            var what = key.IsPrimaryKey ? "primary key" : key.IsUniqueConstraint ? "unique key" : "exclusion constraint";
            var tkey = tgt.Indexes.FirstOrDefault(x => IsKey(x) && x.Name == key.Name);
            if (tkey is not null)
            {
                if (PgCanonicalWriter.KeyText(tkey.Definition, compare.IgnoreFillFactor) == PgCanonicalWriter.KeyText(key.Definition, compare.IgnoreFillFactor)) continue;
                // the same key whose index options alone differ (fillfactor, ...): set on its index, which keeps every row
                if (PgCanonicalWriter.WithoutKeyOptions(tkey.Definition) == PgCanonicalWriter.WithoutKeyOptions(key.Definition) &&
                    tkey.IsPrimaryKey == key.IsPrimaryKey && tkey.IsUniqueConstraint == key.IsUniqueConstraint)
                {
                    var mine = PgCanonicalWriter.KeyOptions(key.Definition);
                    var theirs = PgCanonicalWriter.KeyOptions(tkey.Definition);
                    if (compare.IgnoreFillFactor) { mine.Remove("fillfactor"); theirs.Remove("fillfactor"); }
                    var set = mine.Where(kv => theirs.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => $"{kv.Key} = {PgDdl.Lit(kv.Value)}").ToList();
                    var reset = theirs.Keys.Where(k => !mine.ContainsKey(k)).ToList();
                    var index = $"ALTER INDEX {PgDdl.Q(src.Schema, key.Name)}";
                    Constraints.Files.Add(new PgFile($"key_{src.Schema}.{src.Name}.{key.Name}", Header(Constraints, "key", $"{src.FullName}.{key.Name}") +
                        $"-- The target's {what} of this name: {PgDdl.OneLine(tkey.Definition ?? "")}. Only its index options differ; new pages use them, the rows stay.\n" +
                        (set.Count > 0 ? $"{index} SET ({string.Join(", ", set)});\n" : "") +
                        (reset.Count > 0 ? $"{index} RESET ({string.Join(", ", reset)});\n" : ""), new ObjectName(src.Schema, src.Name)));
                    continue;
                }
                // the same key, only checked at another moment (DEFERRABLE, INITIALLY ...): every row already meets it, so it is
                // swapped in one file; a foreign key that needs the old one would make the DROP fail, so it is held instead
                if (tkey.IsPrimaryKey == key.IsPrimaryKey && tkey.IsUniqueConstraint == key.IsUniqueConstraint &&
                    WithoutDeferrable(tkey.Definition) == WithoutDeferrable(key.Definition))
                {
                    var cols0 = key.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
                    var referencing = Tgt.Objects.Where(o => o.Type == DbObjectType.Table)
                        .SelectMany(o => o.ForeignKeys.Where(f => f.ReferencedSchema == tgt.Schema && f.ReferencedTable == tgt.Name &&
                                                                  f.ReferencedColumns.ToHashSet(StringComparer.Ordinal).SetEquals(cols0))
                            .Select(f => $"{o.FullName}.{f.Name}")).ToList();
                    if (referencing.Count > 0)
                        review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName,
                            $"the target's {what} of this name is checked at another moment ({tkey.Definition}), and foreign key {string.Join(", ", referencing)} " +
                            "needs it, so it cannot be dropped and added again here; change it by hand", Constraints.Name);
                    else
                        Constraints.Files.Add(new PgFile($"key_{src.Schema}.{src.Name}.{key.Name}", Header(Constraints, "key", $"{src.FullName}.{key.Name}") +
                            $"-- The target's {what} of this name: {PgDdl.OneLine(tkey.Definition ?? "")}. The same key checked at another moment: every row meets it.\n" +
                            $"ALTER TABLE {PgDdl.Q(src)} DROP CONSTRAINT {PgDdl.Q(key.Name)} RESTRICT;\n" +
                            $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(key.Name)} {key.Definition};\n", new ObjectName(src.Schema, src.Name)));
                    continue;
                }
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
            // an exclusion constraint's WHERE (and a key's INCLUDE) reads columns beyond its key columns
            if (Unavailable(src, cols.Concat(src.Columns.Where(c => PgDdl.MentionsColumn(key.Definition, c.Name)).Select(c => c.Name))) is { } col)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName, $"it uses {col}, which the target will not have", Constraints.Name);
                continue;
            }
            if ((IndexNeeds(src, key.Name) ?? NotReady(key.Definition)) is { } notReady)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "key", key.Name, src.FullName, notReady, Constraints.Name);
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
        if (!Destroys)
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
        // PostgreSQL before 18 cannot add a NOT VALID foreign key to a partitioned table
        var validatedOnly = PartitionedBefore18(Target(src) ?? src);
        if (validatedOnly && fk.IsNotTrusted)
        {
            review.Hold(ReviewLog.NotAppliedConstraints, "foreignkey", fk.Name, src.FullName,
                $"the source's foreign key is NOT VALID, and PostgreSQL {Tgt.MajorVersion} cannot add a NOT VALID foreign key to a partitioned table " +
                "(18 and later can); add it validated by hand once the rows meet it", folder.Name);
            return;
        }
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
        if (validatedOnly)
        {
            folder.Files.Add(new PgFile($"foreignkey_{src.Schema}.{src.Name}.{fk.Name}", Header(folder, "foreign key", $"{src.FullName}.{fk.Name}") +
                $"-- Added validated: PostgreSQL {Tgt.MajorVersion} cannot add a NOT VALID foreign key to a partitioned table, so the existing rows are " +
                "checked here, under the file's lock.\n" +
                $"ALTER TABLE {PgDdl.Q(src)} ADD CONSTRAINT {PgDdl.Q(fk.Name)} {def};\n", new ObjectName(src.Schema, src.Name)));
            return;
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
        var partitions = new List<(DbObject Src, DbObject? Tgt)>();
        foreach (var (src, tgt) in tables)
        {
            // a partition's plain indexes may come from its parent's: looked at once every parent's are planned (one 03
            // creates gets copies of its parent's then too). An inheritance child inherits no index: its are its own
            if (IsPartition(src) && (tgt is null || IsPartition(tgt))) { partitions.Add((src, tgt)); continue; }
            foreach (var ix in src.Indexes.Where(i => !IsKey(i)).OrderBy(i => i.Name, StringComparer.Ordinal))
                await PlanIndexAsync(src, tgt, ix);
            if (tgt is not null && !Destroys)
                foreach (var tix in tgt.Indexes.Where(x => !IsKey(x) && src.Indexes.All(i => IsKey(i) || i.Name != x.Name)))
                    review.Hold(ReviewLog.NotAppliedDestructive, "index", tix.Name, src.FullName, TargetOnly, Indexes.Name);
        }
        // a parent partition before the partitions under it: what 06 builds on it comes down to them
        foreach (var (src, tgt) in partitions.OrderBy(p => Depth(p.Src)))
            await PlanPartitionIndexesAsync(src, tgt);
        // a materialized view whose query is the same and whose indexes differ: its indexes, as a table's
        foreach (var (src, tgt) in ChangedMatviewIndexes())
        {
            foreach (var ix in src.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)) await PlanIndexAsync(src, tgt, ix);
            if (!Destroys)
                foreach (var tix in tgt.Indexes.Where(x => src.Indexes.All(i => i.Name != x.Name)))
                    review.Hold(ReviewLog.NotAppliedDestructive, "index", tix.Name, src.FullName, TargetOnly, Indexes.Name);
        }

        // extended statistics, on the tables both sides have and those 03 creates
        foreach (var (src, tgt) in ChangedTables().Select(p => (p.Src, Tgt: (DbObject?)p.Tgt))
                     .Concat(Src.Objects.Where(o => o.Type == DbObjectType.Table && created.Contains(o.Key))
                         .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                         .Select(o => (Src: o, Tgt: (DbObject?)null))))
            PlanStatistics(src, tgt);

        // the foreign keys whose referenced columns only a unique index of this folder covers, then their VALIDATE
        foreach (var (src, fk, isNew) in afterIndexes) await PlanForeignKeyAsync(src, fk, isNew, after06: true);
        Indexes.Files.AddRange(indexValidations);
    }

    /// <summary>
    /// A table's extended statistics (CREATE STATISTICS): one the target lacks is created, one that differs is dropped
    /// and created again in one file (it holds no rows: ANALYZE fills it again), each once the columns it reads are
    /// there. One only the target has is 11's to drop.
    /// </summary>
    private void PlanStatistics(DbObject src, DbObject? tgt)
    {
        foreach (var (key, definition) in src.Properties.Where(kv => kv.Key.StartsWith(PgCanonicalWriter.StatisticsPrefix, StringComparison.Ordinal) && kv.Value is not null)
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, Definition: kv.Value!)))
        {
            var name = key[PgCanonicalWriter.StatisticsPrefix.Length..];
            var theirs = tgt?.Properties.GetValueOrDefault(key);
            if (theirs == definition) continue;
            var on = definition.IndexOf(" ON ", StringComparison.Ordinal) is var at and >= 0 ? definition[at..] : definition;
            var why = src.Columns.Where(c => PgDdl.MentionsColumn(on, c.Name)).Select(c => c.Name) is var cols && Unavailable(src, cols) is { } col
                ? $"it reads {col}, which the target will not have"
                : MissingNamed(definition, before03: false) is { } needs
                    ? $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}"
                    : NotReady(definition);
            if (why is not null)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "statistics", name, src.FullName, why, Indexes.Name);
                continue;
            }
            Indexes.Files.Add(new PgFile($"statistics_{src.Schema}.{src.Name}.{name}", Header(Indexes, "statistics", $"{src.FullName}.{name}") +
                (theirs is null ? "" : $"-- The target's statistics of this name are different: {PgDdl.OneLine(theirs)}\nDROP STATISTICS {name};\n") +
                definition.TrimEnd() + "\n", new ObjectName(src.Schema, src.Name)));
        }
        if (tgt is not null && !Destroys)
            foreach (var key in tgt.Properties.Keys.Where(k => k.StartsWith(PgCanonicalWriter.StatisticsPrefix, StringComparison.Ordinal) && !src.Properties.ContainsKey(k)))
                review.Hold(ReviewLog.NotAppliedDestructive, "statistics", key[PgCanonicalWriter.StatisticsPrefix.Length..], src.FullName, TargetOnly, Indexes.Name);
    }

    /// <summary>
    /// One plain index of the source for a table: nothing when the target's of that name is the same, else built (an
    /// index of the same name and another definition replaced in one file), or held with why.
    /// </summary>
    private async Task PlanIndexAsync(DbObject src, DbObject? tgt, IndexInfo ix)
    {
        var tix = tgt?.Indexes.FirstOrDefault(x => !IsKey(x) && x.Name == ix.Name);
        if (tix is not null && SameIndex(tix, ix)) return;
        if (tix is null && Names(src.Schema).Contains(ix.Name))
        {
            review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                $"another table, view, sequence or index of schema {src.Schema} on the target is already called {ix.Name}", Indexes.Name);
            return;
        }
        if ((IndexNeeds(src, ix.Name) ?? NotReady(ix.Definition)) is { } notReady)
        {
            review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, notReady, Indexes.Name);
            return;
        }
        if (tgt is not null)
        {
            var cols = src.Columns.Where(c => PgDdl.MentionsColumn(ix.Definition, c.Name) || ix.KeyColumns.Any(k => k.Name == c.Name) ||
                                              ix.IncludedColumns.Contains(c.Name)).Select(c => c.Name);
            if (Unavailable(src, cols) is { } col)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, $"it uses {col}, which the target will not have", Indexes.Name);
                return;
            }
            if (tix is not null && StandingOn(tgt, tix) is { Count: > 0 } standing &&
                (!(ix.IsUnique && ix.FilterDefinition is null && SameKeyColumns(ix, tix)) ||
                 standing.FirstOrDefault(s => s.Fk.IsNotTrusted && PartitionedBefore18(s.Table)) is { Table: not null }))
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                    $"foreign key {string.Join(", ", standing.Select(s => $"{s.Table.FullName}.{s.Fk.Name}"))} on the target stands on its index of this name, " +
                    (ix.IsUnique && ix.FilterDefinition is null && SameKeyColumns(ix, tix)
                        ? $"and PostgreSQL {Tgt.MajorVersion} cannot add a NOT VALID foreign key to a partitioned table again"
                        : "and the source's index cannot stand in for it (it is not unique on the same columns, or it is partial)") +
                    ", so it cannot be dropped and built again here; change it by hand", Indexes.Name);
                return;
            }
            if (ix.IsUnique)
            {
                var have = ColumnNames(tgt);
                var plain = ix.KeyColumns.Select(k => k.Name).Where(have.Contains).ToList();
                var dups = plain.Count == ix.KeyColumns.Count
                    ? await probes.DuplicateGroupsAsync(PgDdl.Q(src), plain, ix.FilterDefinition, ct)
                    : new ProbeCount(null, "an index on an expression or a new column is not probed");
                if (dups.Count > 0)
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                        $"{dups.Count:N0} value(s) appear more than once in the existing rows", Indexes.Name);
                    return;
                }
                if (dups.Count is null) probes.NoteNotChecked($"duplicates under unique index {src.FullName}.{ix.Name}", dups.NotCheckedReason ?? "not run");
            }
            var rows = await probes.RowEstimateAsync(src.Schema, src.Name, ct);
            if (rows >= Options.LargeTableRows)
                review.Note(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                    $"built without CONCURRENTLY (it must run inside the file's transaction): writes to the table (about {rows:N0} rows) wait while it builds",
                    Indexes.Name);
        }
        // the same name and another definition: dropped and built again in one file, so the table is without it only
        // inside that file's transaction. A foreign key that stands on the old one is dropped first and added back on
        // the new one (the same unique columns, so every row that met it still does); a table whose replica identity
        // is this index gets it back as the source has it
        var sql = new StringBuilder();
        var after = new StringBuilder();
        if (tix is not null)
        {
            sql.Append($"-- The target's index of this name is different: {PgDdl.OneLine(tix.Definition ?? "")}\n");
            var standing = StandingOn(tgt!, tix);
            if (standing.Count > 0)
                sql.Append($"-- Foreign key(s) {PgDdl.OneLine(string.Join(", ", standing.Select(s => $"{s.Table.FullName}.{s.Fk.Name}")))} stand on it: dropped " +
                           "first and added back on the new index in this file, which checks their rows again.\n");
            foreach (var (table, fk) in standing)
            {
                sql.Append($"ALTER TABLE {PgDdl.Q(table)} DROP CONSTRAINT {PgDdl.Q(fk.Name)} RESTRICT;\n");
                after.Append($"ALTER TABLE {PgDdl.Q(table)} ADD CONSTRAINT {PgDdl.Q(fk.Name)} {Strip(fk.Definition)}{(fk.IsNotTrusted ? " NOT VALID" : "")};\n");
            }
            sql.Append($"DROP INDEX {PgDdl.Q(src.Schema, ix.Name)} RESTRICT;\n");
        }
        // the source's replica identity is this index: set once it is built (05 sets every other kind, before 06 runs)
        if (ReplicaIndex(src) == ix.Name)
            after.Append($"ALTER TABLE {PgDdl.Q(src)} REPLICA IDENTITY USING INDEX {PgDdl.Q(ix.Name)};\n");
        Indexes.Files.Add(new PgFile($"index_{src.Schema}.{src.Name}.{ix.Name}", Header(Indexes, "index", $"{src.FullName}.{ix.Name}") +
            sql + PgDdl.Statement(OnEveryPartition(src, ix.Definition ?? "")) + after, new ObjectName(src.Schema, src.Name)));
        Names(src.Schema).Add(ix.Name);
        built06.Add(HeldKey(src, ix.Name));
        if (src.Properties.ContainsKey("partitionBy")) (onPartitions.TryGetValue(src.Key, out var built) ? built : onPartitions[src.Key] = []).Add(ix);
        if (ix.IsUnique && ix.FilterDefinition is null && ix.KeyColumns.All(k => src.Columns.Any(c => c.Name == k.Name)))
            (addedUnique.TryGetValue(src.Key, out var sets) ? sets : addedUnique[src.Key] = []).Add(ix.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>How many tables a partition hangs under.</summary>
    private int Depth(DbObject table)
    {
        var n = 0;
        for (var at = ParentOf(table); at is not null && n < 64; at = ParentOf(at)) n++;
        return n;
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
    /// A partition's plain indexes: one that is the copy of an index of a table it hangs under comes with that one, and
    /// is fine when PostgreSQL will give the copy the source's name; the partition's own index (PostgreSQL builds one
    /// on a partition as on any table) is planned as any table's is. What cannot be done is held with why.
    /// </summary>
    private async Task PlanPartitionIndexesAsync(DbObject src, DbObject? tgt)
    {
        var parent = ParentOf(src);
        var fromParent = Inherited(src, tgt is null);
        // the target's copies of its own parent's indexes, which change only with that parent's
        var theirParent = tgt?.Properties.GetValueOrDefault("partitionOf") is { } of0
            ? Tgt.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && $"{PgDdl.Ident(o.Schema)}.{PgDdl.Ident(o.Name)}" == of0)
            : null;
        foreach (var ix in src.Indexes.Where(i => !IsKey(i)).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var tix = tgt?.Indexes.FirstOrDefault(x => !IsKey(x) && x.Name == ix.Name);
            var of = fromParent.FirstOrDefault(p => SameShape(p, ix));
            if (tix is not null)
            {
                if (SameIndex(tix, ix)) continue;
                if (of is null && theirParent?.Indexes.Any(i => !IsKey(i) && SameShape(i, tix)) != true) await PlanIndexAsync(src, tgt, ix);
                else review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName, ChildReason, Indexes.Name);
                continue;
            }
            if (of is null)
            {
                // the partition's own index, as any table's
                await PlanIndexAsync(src, tgt, ix);
                continue;
            }
            // PostgreSQL attaches a matching index the partition already has, or builds one under a name it chooses
            var name = tgt?.Indexes.FirstOrDefault(x => !IsKey(x) && SameShape(x, ix))?.Name ?? ChosenName(src, ix);
            if (name != ix.Name)
            {
                review.Hold(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                    $"PostgreSQL builds {parent!.FullName}.{of.Name} on this partition too, but names that copy {(name is null ? "itself" : name)}, not {ix.Name} as the source does",
                    Indexes.Name);
                continue;
            }
            Names(src.Schema).Add(name);
            review.Note(ReviewLog.NotAppliedConstraints, "index", ix.Name, src.FullName,
                $"built on this partition by its parent's index {parent!.FullName}.{of.Name}", Indexes.Name);
        }
        if (tgt is not null && !Destroys)
            foreach (var tix in tgt.Indexes.Where(x => !IsKey(x) && src.Indexes.All(i => IsKey(i) || i.Name != x.Name)))
                review.Hold(ReviewLog.NotAppliedDestructive, "index", tix.Name, src.FullName, TargetOnly, Indexes.Name);
    }

    /// <summary>
    /// The plain indexes PostgreSQL gives a partition from above: those 06 builds on any table it hangs under (an index
    /// of a partitioned table is built on every partition below it, sub-partitions too), and for a partition 03 creates
    /// (<paramref name="created"/>), what its parent has by then - CREATE TABLE ... PARTITION OF copies the parent's
    /// indexes, the target's own parent's, or for a parent 03 creates as well, what that one got the same way.
    /// </summary>
    private List<IndexInfo> Inherited(DbObject partition, bool created)
    {
        var list = new List<IndexInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var (at, made) = (partition, created); ParentOf(at) is { } parent && seen.Add(parent.Key); at = parent)
        {
            list.AddRange(onPartitions.GetValueOrDefault(parent.Key) ?? []);
            if (!made) continue;
            if (t.TryGetValue(parent.Key, out var tp))
            {
                list.AddRange(tp.Indexes.Where(i => !IsKey(i)));
                made = false;   // above an existing parent, only what 06 builds still comes down
            }
            else made = this.created.Contains(parent.Key);
        }
        return list;
    }

    /// <summary>The source's table a partition belongs to (partitionOf is spelled as quote_ident spells it), or null.</summary>
    private DbObject? ParentOf(DbObject partition) =>
        partition.Properties.GetValueOrDefault("partitionOf") is { } of
            ? Src.Objects.FirstOrDefault(o => o.Type == DbObjectType.Table && $"{PgDdl.Ident(o.Schema)}.{PgDdl.Ident(o.Name)}" == of)
            : null;

    /// <summary>
    /// Why the index (or the index behind a key) <paramref name="index"/> of <paramref name="table"/> cannot be built on
    /// the target: what it needs beyond the table's columns (pg_depend: an extension's operator class, a collation, a
    /// function, a type) is not there and this run does not create it, or the extension is in another schema. Null when
    /// nothing stands in the way.
    /// </summary>
    private string? IndexNeeds(DbObject table, string index)
    {
        foreach (var d in table.DependsOn ?? [])
        {
            if (d.Index != index) continue;
            if (t.TryGetValue(d.Key, out var have))
            {
                if (have.Type == DbObjectType.Extension && ExtensionElsewhere(have) is { } elsewhere) return elsewhere;
                continue;
            }
            if (created.Contains(d.Key) || !s.TryGetValue(d.Key, out var need)) continue;
            return need.Type == DbObjectType.Extension
                ? $"it needs extension {need.Name} (an operator class or function of it), which the target does not have - install it first"
                : $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
        }
        return null;
    }

    /// <summary>The same index but for its name and table: uniqueness, then the deparse from USING on (method, columns, INCLUDE, WHERE).</summary>
    private bool SameShape(IndexInfo a, IndexInfo b)
    {
        string? Tail(string? def) => PgCanonicalWriter.IndexText(def, compare.IgnoreCollation) is var text &&
                                     text.IndexOf(" USING ", StringComparison.Ordinal) is int at and >= 0 ? text[at..] : null;
        return a.IsUnique == b.IsUnique && Tail(a.Definition) is { } tail && tail == Tail(b.Definition);
    }

    /// <summary>The same index as the compare sees it: its definition, without COLLATE when collations are ignored.</summary>
    private bool SameIndex(IndexInfo a, IndexInfo b) =>
        PgCanonicalWriter.IndexText(a.Definition, compare.IgnoreCollation) == PgCanonicalWriter.IndexText(b.Definition, compare.IgnoreCollation);

    /// <summary>Indexes (table key, name) 06 builds in this run.</summary>
    private readonly HashSet<string> built06 = new(StringComparer.Ordinal);

    /// <summary>The same key columns, in any order (what a foreign key needs of the unique index it stands on).</summary>
    private static bool SameKeyColumns(IndexInfo a, IndexInfo b) =>
        a.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal).SetEquals(b.KeyColumns.Select(k => k.Name));

    /// <summary>
    /// The target's foreign keys that can stand on <paramref name="index"/> of <paramref name="table"/>: those that
    /// reference exactly its key columns, when it is unique and not partial. PostgreSQL makes such a foreign key depend
    /// on the unique index it found, so DROP INDEX ... RESTRICT fails while it is there.
    /// </summary>
    private List<(DbObject Table, ForeignKeyInfo Fk)> StandingOn(DbObject table, IndexInfo index)
    {
        if (!index.IsUnique || index.FilterDefinition is not null) return [];
        var columns = index.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);
        return [.. Tgt.Objects.Where(o => o.Type == DbObjectType.Table).OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
            .SelectMany(o => o.ForeignKeys.Where(f => f.ReferencedSchema == table.Schema && f.ReferencedTable == table.Name &&
                                                      f.ReferencedColumns.ToHashSet(StringComparer.Ordinal).SetEquals(columns))
                .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => (o, f)))];
    }

    /// <summary>A table's column names; a materialized view's are those it returns.</summary>
    private static HashSet<string> ColumnNames(DbObject relation) => relation.Type == DbObjectType.MaterializedView
        ? [.. Lines(relation.Properties.GetValueOrDefault("columns")).Select(l => PgDdl.UnquoteFirst(l)).OfType<string>()]
        : [.. relation.Columns.Select(c => c.Name)];

    /// <summary>The index a table's replica identity names (REPLICA IDENTITY USING INDEX), or null.</summary>
    private static string? ReplicaIndex(DbObject table) =>
        table.Properties.GetValueOrDefault("replicaIdentity") is { } ri && ri.StartsWith("USING INDEX ", StringComparison.Ordinal)
            ? PgDdl.UnquoteFirst(ri["USING INDEX ".Length..])
            : null;

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
