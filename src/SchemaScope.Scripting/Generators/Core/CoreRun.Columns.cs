namespace SchemaScope.Scripting;

// 01_columns and 04_columntypes (generate.py "folder 02" and "folder 06").
internal sealed partial class CoreRun
{
    /// <summary>
    /// (table, lower-cased column) → why a constraint or index built on that column cannot be added: the column
    /// is one 01 does not create. generate.py's <c>blocked_set</c>, plus new columns of excluded tables.
    /// </summary>
    private readonly Dictionary<string, string> unavailableColumns = new(StringComparer.Ordinal);
    /// <summary>How many of <see cref="CoreFacts.BlockedColumns"/> are the plain "NOT NULL with no default" case.</summary>
    private int notNullBlocked;

    // ------------------------------------------------------------------ 01 compute

    /// <summary>
    /// generate.py lines 205-230: every source column missing from a table both sides have, in table order then
    /// column_id order. A NOT NULL column with no default (and not computed) is blocked: it cannot be added to a
    /// table that has rows. Identity columns are blocked by the same rule, as in the Python (SQL Server could add
    /// them, but that numbers every existing row - a decision, not a script).
    /// Added: 01 runs before 03, so a column whose alias type, computed expression or default needs something the
    /// target does not have yet (03 creates it later) cannot be added as written (Msg 2715 / 4121); a nullable
    /// column then goes in without its default (05 adds the default after 03), any other is blocked. A named default
    /// whose name another target object holds (Msg 2714) is treated the same way - 05 then holds the default.
    /// </summary>
    private async Task ComputeNewColumnsAsync()
    {
        var write = ctx.Selected(ScriptSection.NewColumns);
        var srcDef = ScriptSchema.ByTable(src.Defaults, src.NameComparer);
        var objects = await TargetObjectsAsync();

        void Block(ObjectName k, ColumnRow c, string why, string brief)
        {
            facts.BlockedColumns.Add(new BlockedColumn(k, c, why));
            MarkUnavailable(k, c.Col, $"built on a column 01_columns does not create ({brief})");
            if (write) Hold(ReviewLog.NotApplied, "column", $"{k.Name}.{c.Col}", k.Name, why, CoreFoldersGenerator.ColumnsFolder);
        }

        foreach (var k in commonTables)
        {
            if (!ctx.InScope(k))
            {
                // an object script leaves this table as it is, so a column 01 would add here is not there for what it creates
                foreach (var c in src.ColumnsOf(k))
                    if (tgt.FindColumn(k, c.Col) is null)
                        MarkUnavailable(k, c.Col, "built on a column this object script does not add (its table is not part of it)");
                continue;
            }
            var excluded = ctx.IsExcluded(k);
            var defaults = new Dictionary<string, DefaultRow>(StringComparer.Ordinal);
            foreach (var r in Of(srcDef, k)) defaults[r.Col] = r;
            foreach (var c in src.ColumnsOf(k))
            {
                if (tgt.FindColumn(k, c.Col) is not null) continue;
                var name = $"{k.Name}.{c.Col}";
                if (excluded)
                {
                    MarkUnavailable(k, c.Col, "built on a column 01_columns does not add (its table matches an exclude pattern)");
                    if (write) Hold(ReviewLog.NotApplied, "column", name, k.Name, ExcludedReason, CoreFoldersGenerator.ColumnsFolder);
                    continue;
                }
                var d = defaults.GetValueOrDefault(c.Col);

                if (MissingAliasType(c) is { } at)
                {
                    Block(k, c, $"its type {at.Key} is not in {tgtLabel} yet - 03_missing creates it, but 01_columns runs first (Msg 2715); add the column after 03_missing",
                        $"its type {at.Key} is not in {tgtLabel} yet");
                    continue;
                }
                if (c.IsComputed && UsesMissing(c.ComputedDef) is { } cf)
                {
                    Block(k, c, $"its computed expression uses {cf}, which {tgtLabel} does not have yet - 03_missing creates it, but 01_columns runs first (Msg 4121); add the column after 03_missing",
                        $"it uses {cf}, which {tgtLabel} does not have yet");
                    continue;
                }
                string? dropDefault = null;
                if (d is not null && UsesMissing(d.Definition) is { } df)
                    dropDefault = $"its default uses {df}, which {tgtLabel} does not have yet - 03_missing creates it, but 01_columns runs first (Msg 4121)";
                else if (d is { IsSystemNamed: false } && objects is not null && objects.TryGetValue(new ObjectName(d.Sch, d.CName), out var taken))
                    dropDefault = $"its default is named {d.CName}, and {tgtLabel} already has a {TypeWord(taken)} with that name (Msg 2714)";
                if (dropDefault is not null)
                {
                    if (!c.IsNullable)
                    {
                        Block(k, c, $"NOT NULL, and {dropDefault} - it cannot be added without that default", dropDefault);
                        continue;
                    }
                    d = null;   // added without the default; 05 adds it after 03, or holds it with the reason
                }

                if (!c.IsNullable && d is null && !c.IsComputed)
                {
                    notNullBlocked++;
                    Block(k, c,
                        $"NOT NULL in {srcLabel} with no default - it cannot be added to a table that already has rows" +
                        (c.IsIdentity ? " (an IDENTITY column: adding it numbers every existing row, so it is left to you)" : ""),
                        $"NOT NULL in {srcLabel}, no default");
                    continue;
                }
                facts.NewColumns.Add(new NewColumn(k, c, d));
            }
        }
    }

    /// <summary>Records a source column the target will not have after 01 (constraints, indexes, 03 modules and 07 triggers test it).</summary>
    private void MarkUnavailable(ObjectName table, string column, string why)
    {
        unavailableColumns[ColumnKey(table.Schema, table.Name, column)] = why;
        facts.UnavailableColumns.Add(new UnavailableColumn(table, column, why));
    }

    // ------------------------------------------------------------------ 01 write

    private FolderResult WriteColumns()
    {
        var items = new List<(string, string, ObjectName?)>();
        var n = 0;
        foreach (var nc in facts.NewColumns)
        {
            n++;
            items.Add(($"{N3(n)}_{ScriptText.Safe(nc.Table.Name)}_{ScriptText.Safe(nc.Column.Col)}.sql",
                Ddl.AddColumn(nc.Table.Schema, nc.Table.Name, nc.Column, coll, nc.Default), nc.Table));
        }
        var notes = new List<string>();
        if (facts.BlockedColumns.Count > 0)
            notes.Add(facts.BlockedColumns.Count == notNullBlocked
                ? $"{facts.BlockedColumns.Count} column(s) not added: NOT NULL with no default (99_review\\{ReviewLog.NotApplied})."
                : $"{facts.BlockedColumns.Count} column(s) not added: NOT NULL with no default, or they need something {tgtLabel} does not have yet (99_review\\{ReviewLog.NotApplied}).");
        var result = WriteStatementsFolder(CoreFoldersGenerator.ColumnsFolder, "New columns", items, notes);
        facts.ColumnsWritten = true;
        return result;
    }

    // ------------------------------------------------------------------ 04 compute

    internal static readonly Dictionary<string, int> IntSize = new(StringComparer.Ordinal)
    {
        ["tinyint"] = 1, ["smallint"] = 2, ["int"] = 4, ["bigint"] = 8,
    };

    internal static readonly HashSet<string> CharTypes = new(StringComparer.Ordinal) { "char", "varchar", "nchar", "nvarchar" };

    /// <summary>generate.py <c>col_width</c>: characters for the char family (2^31 for max), bytes for the int family, else null.</summary>
    internal static long? Width(ColumnRow c)
    {
        var t = c.Dt.ToLowerInvariant();
        if (CharTypes.Contains(t))
            return c.MaxLength == -1 ? 1L << 31 : t[0] == 'n' ? c.MaxLength / 2 : c.MaxLength;
        return IntSize.TryGetValue(t, out var s) ? s : null;
    }

    /// <summary>
    /// generate.py lines 415-497: widen only, relax only. Types are compared without collation (<c>bare</c>): the two
    /// default collations may differ and every text column would otherwise look different. The new type keeps the
    /// target column's own collation (<c>target_type</c>). Port fixes from python.md §1: a widening never tightens
    /// nullability (NULL when either side is nullable); widening stays inside tinyint &lt; smallint &lt; int &lt; bigint or
    /// inside the char family, and never goes from Unicode to non-Unicode (the Python's int→varchar and
    /// nvarchar→varchar holes); a target primary-key column is never made nullable, in a widening either.
    /// REV_COLS is gone: those columns fall into the normal groups. Added: a change SQL Server would refuse because
    /// something in the target depends on the column (Msg 5074 / 4922 - an index, key, foreign key, constraint,
    /// computed column, user statistics or schema-bound module; <see cref="ColumnCover"/> has the exact rule) is held
    /// back to not_applied.md instead of written. A nullability-only change passes unless a computed column or a
    /// schema-bound module reads the column (the primary-key case is the hold above).
    /// </summary>
    private async Task ComputeColumnTypesAsync()
    {
        var write = ctx.Selected(ScriptSection.ColumnTypes);
        var extra = await ctx.Probes.ColumnDependentsAsync(Ct);   // null: the probe noted "not checked"
        var cover = ColumnCover.Build(tgt, extra);
        var pkCols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in tgt.Keys.Where(r => r.KType == "PK"))
            foreach (var c in Ddl.BracketedNames(r.Cols)) pkCols.Add(ColumnKey(r.Sch, r.Tbl, c));

        var plain = new List<(ObjectName, ColumnRow, ColumnRow)>();
        var family = new List<(ObjectName, ColumnRow, ColumnRow)>();
        var widenInt = new List<(ObjectName, ColumnRow, ColumnRow)>();
        var relax = new List<(ObjectName, ColumnRow, ColumnRow)>();

        foreach (var k in commonTables)
        {
            if (!ctx.InScope(k)) continue;
            var srcCols = new Dictionary<string, ColumnRow>(StringComparer.Ordinal);
            foreach (var c in src.ColumnsOf(k)) srcCols[c.Col.ToLowerInvariant()] = c;
            var tgtCols = new Dictionary<string, ColumnRow>(StringComparer.Ordinal);
            foreach (var c in tgt.ColumnsOf(k)) tgtCols[c.Col.ToLowerInvariant()] = c;
            var excluded = ctx.IsExcluded(k);

            foreach (var (cn, c) in srcCols.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!tgtCols.TryGetValue(cn, out var p) || c.IsComputed || p.IsComputed) continue;
                var sameType = Bare(c) == Bare(p);
                List<(ObjectName, ColumnRow, ColumnRow)>? group = null;
                if (sameType && c.IsNullable && !p.IsNullable)
                {
                    if (pkCols.Contains(ColumnKey(k.Schema, k.Name, cn)))
                    {
                        if (write)
                            Hold(ReviewLog.NotAppliedConstraints, "column", $"{k.Name}.{c.Col}", k.Name,
                                $"in {tgtLabel}'s primary key - {srcLabel} redesigned this key, so making the column nullable is a table rebuild, not a column change",
                                CoreFoldersGenerator.ColumnTypesFolder);
                        continue;
                    }
                    group = relax;
                }
                else if (!sameType && Width(c) is { } wd && Width(p) is { } wp && wd > wp)
                {
                    var cd = c.Dt.ToLowerInvariant();
                    var pd = p.Dt.ToLowerInvariant();
                    var ints = IntSize.ContainsKey(cd) && IntSize.ContainsKey(pd);
                    var chars = CharTypes.Contains(cd) && CharTypes.Contains(pd);
                    if (!ints && !chars) continue;                          // cross-family: column_type_differences.md lists it
                    if (chars && pd[0] == 'n' && cd[0] != 'n') continue;     // Unicode → non-Unicode loses characters
                    group = cd == pd ? plain : ints ? widenInt : family;
                }
                if (group is null) continue;
                if (excluded)
                {
                    if (write) Hold(ReviewLog.NotApplied, "column", $"{k.Name}.{c.Col}", k.Name, ExcludedReason, CoreFoldersGenerator.ColumnTypesFolder);
                    continue;
                }
                var blockers = ColumnCover.Blockers(cover.Of(k, p.Col), typeChanges: !sameType, p.Dt, c.Dt, c.MaxLength == -1);
                if (blockers.Count > 0)
                {
                    if (write)
                        Hold(ReviewLog.NotApplied, "column", $"{k.Name}.{c.Col}", k.Name,
                            $"{tgtLabel}'s {string.Join(", ", blockers)} depend{(blockers.Count == 1 ? "s" : "")} on this column, so SQL Server refuses " +
                            (sameType ? "to change it" : $"to change its type from {Bare(p)} to {Bare(c)}") +
                            " (Msg 5074) - drop and recreate them around the change by hand",
                            CoreFoldersGenerator.ColumnTypesFolder);
                    continue;
                }
                group.Add((k, c, p));
            }
        }

        var n = 0;
        foreach (var (name, list) in new[] { ("plain", plain), ("family", family), ("widen_int", widenInt), ("relax", relax) })
            foreach (var (k, c, p) in list)
            {
                n++;
                var newType = Ddl.QType(c.Dt, c.MaxLength, c.Precision, c.Scale, p.CollationName, tgt.Collation);
                var nullable = c.IsNullable || p.IsNullable;
                if (nullable && !p.IsNullable && pkCols.Contains(ColumnKey(k.Schema, k.Name, c.Col))) nullable = false;
                var sql = Ddl.AlterColumn(k.Schema, k.Name, c.Col, newType, nullable);
                var file = $"{N3(n)}_{ScriptText.Safe(k.Name)}_{ScriptText.Safe(c.Col)}.sql";
                facts.IntendedAlters.Add(new IntendedAlter(n, file, k, c.Col, c, p, name, newType, nullable, sql));
            }
    }

    /// <summary>generate.py <c>bare</c>: the type alone, without any collation.</summary>
    private string Bare(ColumnRow c) => Ddl.QType(c.Dt, c.MaxLength, c.Precision, c.Scale, null, coll);

    // ------------------------------------------------------------------ 04 write

    private FolderResult WriteColumnTypes()
    {
        var items = facts.IntendedAlters.Select(a => (a.File, a.Sql, (ObjectName?)a.Table));
        var notes = new List<string>();
        var groups = facts.IntendedAlters.GroupBy(a => a.Group).ToDictionary(g => g.Key, g => g.Count());
        if (facts.IntendedAlters.Count > 0)
            notes.Add($"{groups.GetValueOrDefault("plain")} same-type widening(s), {groups.GetValueOrDefault("family")} char-family change(s), " +
                      $"{groups.GetValueOrDefault("widen_int")} integer widening(s), {groups.GetValueOrDefault("relax")} NOT NULL to NULL.");
        return WriteStatementsFolder(CoreFoldersGenerator.ColumnTypesFolder, "Column types", items, notes);
    }
}
