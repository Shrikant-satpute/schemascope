namespace SchemaScope.Scripting;

// 05_constraints and 06_indexes (generate.py "folder 04" and "folder 05").
internal sealed partial class CoreRun
{
    private readonly List<string> constraintNotes = [];
    private readonly List<string> indexNotes = [];

    /// <summary>
    /// A generate.py identity key (K_DEF, K_CK, K_KEY, K_FK, K_IX). <see cref="Eq"/> decides "already present"
    /// (schema/table parts folded on case-insensitive databases); <see cref="Sort"/> is the tuple in its own
    /// spelling joined by NUL, which sorts ordinally exactly as Python sorts the tuple.
    /// </summary>
    private readonly record struct RowKey(string Eq, string Sort);

    private RowKey Key(params (string Value, bool IsName)[] parts) => new(
        string.Join('\0', parts.Select(p => p.IsName ? Fold(p.Value) : p.Value)),
        string.Join('\0', parts.Select(p => p.Value)));

    private RowKey KDef(DefaultRow r) => Key((r.Sch, true), (r.Tbl, true), (r.Col.ToLowerInvariant(), false));
    private RowKey KCk(CheckRow r) => Key((r.Sch, true), (r.Tbl, true), (ScriptText.Norm(r.Definition), false));
    private RowKey KKey(KeyRow r) => Key((r.Sch, true), (r.Tbl, true), (r.KType, false), (ScriptText.Norm(r.Cols), false));
    private RowKey KFk(ForeignKeyRow r) => Key((r.Sch, true), (r.Tbl, true), (ScriptText.Norm(r.Cols), false),
        (r.RSch, true), (r.RTbl, true), (ScriptText.Norm(r.RCols), false));
    private RowKey KIx(IndexRow r) => Key((r.Sch, true), (r.Tbl, true), (ScriptText.Norm(r.Cols), false),
        (ScriptText.Norm(r.Incl), false), (r.IsUnique ? "True" : "False", false));

    /// <summary>
    /// generate.py <c>[r for k, r in sorted(keyset(seq, kf).items()) if k not in have]</c>: source rows keyed (a
    /// repeated key keeps the last row, like the dict), sorted by key, minus keys the target already has.
    /// </summary>
    private static List<T> Todo<T>(IEnumerable<T> source, IEnumerable<T> target, Func<T, RowKey> kf)
    {
        var have = new HashSet<string>(target.Select(r => kf(r).Eq), StringComparer.Ordinal);
        var keyed = new Dictionary<string, (string Sort, T Row)>(StringComparer.Ordinal);
        foreach (var r in source)
        {
            var k = kf(r);
            keyed[k.Eq] = (keyed.TryGetValue(k.Eq, out var old) ? old.Sort : k.Sort, r);
        }
        return keyed.Where(e => !have.Contains(e.Key))
            .OrderBy(e => e.Value.Sort, StringComparer.Ordinal)
            .Select(e => e.Value.Row)
            .ToList();
    }

    /// <summary>Tables 03 creates, for "is this table new" tests (source spelling).</summary>
    private HashSet<ObjectName> NewTables() => new(facts.MissingTables, src.NameComparer);

    /// <summary>Why a table is unusable: neither in the target nor created by 03 (quarantined, excluded or held back).</summary>
    private string? Absent(ObjectName t, HashSet<ObjectName> newTables) =>
        tgt.Tables.Contains(t) || newTables.Contains(t) ? null : $"table {t} is not in {tgtLabel} and 03_missing does not create it";

    private string? Excluded(ObjectName table, string? name) =>
        ctx.IsExcluded(table) || (!string.IsNullOrEmpty(name) && ctx.IsExcluded(new ObjectName(table.Schema, name))) ? ExcludedReason : null;

    /// <summary>
    /// generate.py <c>uses_blocked</c>: the bracketed names in the row's column lists or definition, tested against the
    /// columns 01 does not create; for a foreign key also the referenced columns on the referenced table.
    /// </summary>
    private string? Unavailable(string sch, string tbl, IEnumerable<string?> texts, ForeignKeyRow? fk = null)
    {
        if (unavailableColumns.Count == 0) return null;
        foreach (var t in texts)
            foreach (var c in Ddl.BracketedNames(t))
                if (unavailableColumns.TryGetValue(ColumnKey(sch, tbl, c), out var why)) return why;
        if (fk is not null)
            foreach (var c in Ddl.BracketedNames(fk.RCols))
                if (unavailableColumns.TryGetValue(ColumnKey(fk.RSch, fk.RTbl, c), out var why)) return why;
        return null;
    }

    /// <summary>
    /// generate.py <c>has_duplicates</c>: would existing target rows reject a unique key? Only the key's columns the
    /// target already has are tested; a column 01 is about to add holds one constant on every existing row, so it
    /// cannot separate rows that already collide. With no column left the whole key is constant: any second row
    /// is a duplicate. A new table (03) has no rows.
    /// </summary>
    private async Task<ProbeCount> DuplicatesAsync(ObjectName table, string? cols, HashSet<ObjectName> newTables)
    {
        if (newTables.Contains(table)) return new ProbeCount(0);
        var present = tgt.ColumnsOf(table).Select(c => c.Col.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var names = Ddl.BracketedNames(cols).Where(c => present.Contains(c.ToLowerInvariant())).ToList();
        return await ctx.Probes.DuplicateGroupsAsync(table, names, Ct);
    }

    /// <summary>Target tables that already have a clustered index or clustered key → its name.</summary>
    private Dictionary<string, string> TargetClustered()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in tgt.Keys.Where(r => r.TypeDesc == "CLUSTERED")) d.TryAdd(TableKey(r.Sch, r.Tbl), r.CName);
        foreach (var r in tgt.Indexes.Where(r => r.TypeDesc == "CLUSTERED" && !r.IsHypothetical)) d.TryAdd(TableKey(r.Sch, r.Tbl), r.IName);
        return d;
    }

    private string ClusteredReason(string existing) =>
        $"{tgtLabel}'s table already has a clustered index ({existing}) - a table holds only one";

    // ------------------------------------------------------------------ 05 compute

    /// <summary>
    /// generate.py lines 278-381. Groups default, check, key, foreignkey; each one's rows the target lacks, sorted
    /// by key; defaults/checks/keys of new tables are already inline in 03 (their foreign keys are not); checks of
    /// new tables that call a function come last in the check group. Skip pipeline in the Python's order: a default
    /// 01 attached inline (silent), a column 01 does not create, a second primary key, a name the target already
    /// uses for another rule, duplicates under a unique key. Added (documented in DESIGN §7 / python.md):
    /// exclusions, a table that is not there, a name another target object holds, a second clustered index, and
    /// disabled source constraints (disabled again right after the ADD; a system-named one cannot be named, so
    /// it is held back). Also held: a primary key on a column the target has as NULL (Msg 8111), a default or check
    /// on a function or sequence nobody creates (Msg 4121), and a foreign key whose referenced columns carry no
    /// primary key or unique key when 05 runs (Msg 1776). Defaults of new tables deferred by 03 (they use a function
    /// 03 creates after its tables) come last in the default group.
    /// </summary>
    private async Task ComputeConstraintsAsync()
    {
        var write = ctx.Selected(ScriptSection.Constraints);
        var newTables = NewTables();
        var inlineDefaults = facts.NewColumns.Where(c => c.Default is not null)
            .Select(c => ColumnKey(c.Table.Schema, c.Table.Name, c.Column.Col)).ToHashSet(StringComparer.Ordinal);
        var tgtNames = new HashSet<string>(tgt.NameStringComparer);
        foreach (var r in tgt.Defaults.Cast<INamedConstraint>().Concat(tgt.Checks).Concat(tgt.Keys).Concat(tgt.FKeys))
            if (!string.IsNullOrEmpty(r.CName)) tgtNames.Add(r.CName);
        var tgtPk = tgt.Keys.Where(r => r.KType == "PK").Select(r => TableKey(r.Sch, r.Tbl)).ToHashSet(StringComparer.Ordinal);
        var clustered = TargetClustered();
        var objects = await TargetObjectsAsync();
        var notChecked = 0;
        string? notCheckedWhy = null;

        // a function or sequence the target lacks that 03 does not create either: a default or check on it fails (Msg 4121)
        var created03 = new HashSet<ObjectName>(
            facts.MissingItems.Where(i => i.Kind is "function" or "sequence").Select(i => i.Object), src.NameComparer);
        string? Uncreated(string? expression) => UsesMissing(expression, (name, _) => !created03.Contains(name)) is { } f
            ? $"uses {f}, which {tgtLabel} does not have and 03_missing does not create - ADD would fail (Msg 4121)"
            : null;

        // candidate keys (by table and column set) a foreign key can reference when 05's foreign keys run: the target's
        // primary keys, unique constraints and plain unique indexes, the keys 03's tables are created with, and the
        // keys 05 itself adds (the key group runs before the foreign keys) - Msg 1776 otherwise
        string ColSet(string sch, string tbl, string? cols) =>
            TableKey(sch, tbl) + "\0\0" + string.Join("\0", Ddl.BracketedNames(cols).Select(c => c.ToLowerInvariant()).Order(StringComparer.Ordinal));
        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in tgt.Keys) candidateKeys.Add(ColSet(r.Sch, r.Tbl, r.Cols));
        foreach (var r in tgt.Indexes.Where(r => r.IsUnique && string.IsNullOrEmpty(r.FilterDefinition) && !r.IsHypothetical && !r.IsDisabled))
            candidateKeys.Add(ColSet(r.Sch, r.Tbl, r.Cols));
        foreach (var r in src.Keys.Where(r => newTables.Contains(r.Table))) candidateKeys.Add(ColSet(r.Sch, r.Tbl, r.Cols));
        string? NoCandidateKey(ForeignKeyRow f)
        {
            var want = ColSet(f.RSch, f.RTbl, f.RCols);
            if (candidateKeys.Contains(want)) return null;
            var later = src.Indexes.FirstOrDefault(i => i.IsUnique && string.IsNullOrEmpty(i.FilterDefinition) &&
                                                         ColSet(i.Sch, i.Tbl, i.Cols) == want && src.NameComparer.Equals(i.Table, f.Referenced));
            return later is not null
                ? $"{f.Referenced} has no primary key or unique constraint on ({f.RCols}) when 05 runs - {srcLabel} uses the unique index " +
                  $"{later.IName}, which 06_indexes adds afterwards; add this foreign key by hand after 06 (Msg 1776)"
                : $"{f.Referenced} has no primary key, unique constraint or unique index on ({f.RCols}) in {tgtLabel} - FOREIGN KEY would fail (Msg 1776)";
        }

        // a primary key needs NOT NULL columns, and 04 never tightens NULL to NOT NULL (Msg 8111)
        string? NullableKeyColumn(ObjectName table, string cols) =>
            Ddl.BracketedNames(cols).FirstOrDefault(c => tgt.FindColumn(table, c) is { IsNullable: true }) is { } nc
                ? $"column {nc} is NULL in {tgtLabel} - a PRIMARY KEY needs NOT NULL columns (Msg 8111), and 04_columntypes never tightens NULL to NOT NULL"
                : null;

        var groups = new List<(string Label, List<INamedConstraint> Rows)>
        {
            ("default", Todo(src.Defaults, tgt.Defaults, KDef).Where(r => !newTables.Contains(r.Table))
                .Concat(facts.DeferredDefaults.Where(r => newTables.Contains(r.Table))).Cast<INamedConstraint>().ToList()),
            ("check", Todo(src.Checks, tgt.Checks, KCk).Where(r => !newTables.Contains(r.Table))
                .Concat(facts.DeferredChecks.Where(r => newTables.Contains(r.Table))).Cast<INamedConstraint>().ToList()),
            ("key", Todo(src.Keys, tgt.Keys, KKey).Where(r => !newTables.Contains(r.Table)).Cast<INamedConstraint>().ToList()),
            ("foreignkey", Todo(src.FKeys, tgt.FKeys, KFk).Cast<INamedConstraint>().ToList()),
        };

        var n = 0;
        foreach (var (label, rows) in groups)
        {
            foreach (var r in rows)
            {
                Ct.ThrowIfCancellationRequested();
                var table = new ObjectName(r.Sch, r.Tbl);
                if (r is DefaultRow d0 && inlineDefaults.Contains(ColumnKey(d0.Sch, d0.Tbl, d0.Col))) continue;   // 01 attached it

                var fk = r as ForeignKeyRow;
                var key = r as KeyRow;
                var disabled = r is CheckRow { IsDisabled: true } || fk is { IsDisabled: true };
                var why = Excluded(table, r.CName)
                    ?? Absent(table, newTables)
                    ?? (fk is not null ? Absent(fk.Referenced, newTables) : null)
                    ?? r switch
                    {
                        // the default's own column too: 01 may block a NOT NULL column that has a default it cannot use yet
                        DefaultRow d => Unavailable(r.Sch, r.Tbl, [d.Definition, Ddl.Bracket(d.Col)]),
                        CheckRow c => Unavailable(r.Sch, r.Tbl, [c.Definition]),
                        KeyRow k => Unavailable(r.Sch, r.Tbl, [k.Cols]),
                        ForeignKeyRow f => Unavailable(r.Sch, r.Tbl, [f.Cols], f),
                        _ => null,
                    }
                    ?? (key is { KType: "PK" } && tgtPk.Contains(TableKey(table))
                        ? $"{tgtLabel}'s table already has a different primary key - a table holds only one (Msg 1779); {srcLabel} redesigned the key"
                        : null)
                    ?? (key is { KType: "PK" } ? NullableKeyColumn(table, key.Cols) : null)
                    ?? (r is DefaultRow dd ? Uncreated(dd.Definition) : r is CheckRow cc ? Uncreated(cc.Definition) : null)
                    ?? (fk is not null ? NoCandidateKey(fk) : null)
                    ?? (!r.IsSystemNamed && tgtNames.Contains(r.CName)
                        ? $"{tgtLabel} already has a constraint with this name, holding a different rule"
                        : null)
                    ?? (!r.IsSystemNamed && objects is not null && objects.TryGetValue(new ObjectName(r.Sch, r.CName), out var ot)
                        ? $"{tgtLabel} already has a {TypeWord(ot)} with this name - constraint names share the schema's object namespace (Msg 2714)"
                        : null)
                    ?? (key is { TypeDesc: "CLUSTERED" } && clustered.TryGetValue(TableKey(table), out var cl) ? ClusteredReason(cl) : null)
                    ?? (disabled && r.IsSystemNamed
                        ? $"disabled in {srcLabel}, and its name is made up by SQL Server, so it cannot be disabled by name after it is added"
                        : null);

                if (why is null && key is { KType: "UQ" })
                {
                    var dup = await DuplicatesAsync(table, key.Cols, newTables);
                    if (dup.Count > 0) why = $"{tgtLabel} data has {Num(dup.Count.Value)} duplicate group(s) on {key.Cols}";
                    else if (!dup.Checked) { notChecked++; notCheckedWhy ??= dup.NotCheckedReason; }
                }
                if (why is not null)
                {
                    if (write) Hold(ReviewLog.NotAppliedConstraints, label, r.CName, r.Tbl, why, CoreFoldersGenerator.ConstraintsFolder);
                    continue;
                }

                var sql = r switch
                {
                    DefaultRow d => Ddl.AddDefault(d),
                    CheckRow c => Ddl.AddCheck(c),
                    KeyRow k => Ddl.AddKey(k),
                    _ => Ddl.AddForeignKey(fk!),
                };
                if (disabled) sql += ScriptText.Go + Ddl.DisableConstraint(r);
                if (key is not null) candidateKeys.Add(ColSet(key.Sch, key.Tbl, key.Cols));
                n++;
                facts.Constraints.Add(new EmittedStatement(n, $"{N3(n)}_{label}_{ScriptText.Safe(r.Tbl)}.sql", label, table, r.CName, sql)
                {
                    Default = r as DefaultRow,
                    Check = r as CheckRow,
                    Key = key,
                    ForeignKey = fk,
                });
            }
        }

        if (notChecked > 0)
            constraintNotes.Add($"{notChecked} unique key(s) were not checked against existing {tgtLabel} data ({notCheckedWhy}).");
        var disabledCount = facts.Constraints.Count(s => s.Check is { IsDisabled: true } || s.ForeignKey is { IsDisabled: true });
        if (disabledCount > 0)
            constraintNotes.Add($"{disabledCount} constraint(s) are disabled in {srcLabel}; each is disabled again right after it is added.");
    }

    // ------------------------------------------------------------------ 06 compute

    /// <summary>
    /// generate.py lines 383-413: source indexes the target lacks (K_IX), sorted by key. Held back: an index name the
    /// target already uses on that table (the Python looked across all tables; index names are per table, and a
    /// PK/UNIQUE constraint's index counts too), a column 01 does not create, duplicates under a unique index.
    /// Added: unscriptable indexes (columnstore, XML, spatial, hypothetical, disabled), exclusions, a table that
    /// is not there, and a second clustered index.
    /// </summary>
    private async Task ComputeIndexesAsync()
    {
        var write = ctx.Selected(ScriptSection.Indexes);
        var newTables = NewTables();
        var clustered = TargetClustered();
        string NameKey(string sch, string tbl, string name) => TableKey(sch, tbl) + "\0" + (tgt.IsCaseInsensitive ? name.ToLowerInvariant() : name);
        var indexNames = tgt.Indexes.Select(r => NameKey(r.Sch, r.Tbl, r.IName)).ToHashSet(StringComparer.Ordinal);
        var keyNames = tgt.Keys.Select(r => NameKey(r.Sch, r.Tbl, r.CName)).ToHashSet(StringComparer.Ordinal);
        var notChecked = 0;
        string? notCheckedWhy = null;

        var n = 0;
        foreach (var r in Todo(src.Indexes, tgt.Indexes, KIx))
        {
            Ct.ThrowIfCancellationRequested();
            var table = r.Table;
            var nk = NameKey(r.Sch, r.Tbl, r.IName);
            var why = Ddl.UnscriptableIndexReason(r)
                ?? Excluded(table, r.IName)
                ?? Absent(table, newTables)
                ?? (indexNames.Contains(nk) ? $"{tgtLabel} already has an index with this name over other columns" : null)
                ?? (keyNames.Contains(nk) ? $"{tgtLabel} already has a primary key or unique constraint with this name on the table" : null)
                ?? (r.TypeDesc == "CLUSTERED" && clustered.TryGetValue(TableKey(table), out var cl) ? ClusteredReason(cl) : null)
                ?? Unavailable(r.Sch, r.Tbl, [r.Cols, r.Incl, r.FilterDefinition]);

            if (why is null && r.IsUnique)
            {
                var dup = await DuplicatesAsync(table, r.Cols, newTables);
                if (dup.Count > 0) why = $"{tgtLabel} data has {Num(dup.Count.Value)} duplicate group(s) on {r.Cols}";
                else if (!dup.Checked) { notChecked++; notCheckedWhy ??= dup.NotCheckedReason; }
            }
            if (why is not null)
            {
                if (write) Hold(ReviewLog.NotAppliedConstraints, "index", r.IName, r.Tbl, why, CoreFoldersGenerator.IndexesFolder);
                continue;
            }
            n++;
            facts.Indexes.Add(new EmittedStatement(n, $"{N3(n)}_index_{ScriptText.Safe(r.IName)}.sql", "index", table, r.IName, Ddl.CreateIndex(r))
            {
                Index = r,
            });
        }
        if (notChecked > 0)
            indexNotes.Add($"{notChecked} unique index(es) were not checked against existing {tgtLabel} data ({notCheckedWhy}).");
    }

    // ------------------------------------------------------------------ 05 / 06 write

    private FolderResult WriteConstraints()
    {
        var notes = new List<string>(constraintNotes);
        var held = ctx.Review.ItemsFor(ReviewLog.NotAppliedConstraints).Count(i => i.Section == CoreFoldersGenerator.ConstraintsFolder);
        if (held > 0) notes.Insert(0, $"{held} held back (99_review\\{ReviewLog.NotAppliedConstraints}).");
        return WriteStatementsFolder(CoreFoldersGenerator.ConstraintsFolder, "Constraints",
            facts.Constraints.Select(s => (s.File, s.Sql, (ObjectName?)s.Table)), notes);
    }

    private FolderResult WriteIndexes()
    {
        var notes = new List<string>(indexNotes);
        var held = ctx.Review.ItemsFor(ReviewLog.NotAppliedConstraints).Count(i => i.Section == CoreFoldersGenerator.IndexesFolder);
        if (held > 0) notes.Insert(0, $"{held} held back (99_review\\{ReviewLog.NotAppliedConstraints}).");
        return WriteStatementsFolder(CoreFoldersGenerator.IndexesFolder, "Indexes",
            facts.Indexes.Select(s => (s.File, s.Sql, (ObjectName?)s.Table)), notes);
    }
}
