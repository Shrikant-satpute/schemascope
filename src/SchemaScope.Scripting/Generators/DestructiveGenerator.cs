using System.Globalization;
using System.Text;

namespace SchemaScope.Scripting;

/// <summary>
/// Folder 11_destructive: the changes that can lose data, written only when <see cref="ScriptOptions.IncludeDestructive"/>
/// is on and never run by RUN.ps1. One file per statement or object, in a fixed order:
/// <list type="number">
/// <item>column type changes no other folder makes (narrowing, another type family, a precision change) and NULL to NOT NULL,
/// each after a read-only data-fit probe;</item>
/// <item>CHECK constraints whose name both sides use for a different rule: DROP + ADD after a violation probe;</item>
/// <item>DROPs of target-only foreign keys, checks, unique constraints and indexes on tables both sides have;</item>
/// <item>DROPs of target-only procedures, functions, views and triggers (each with a <c>rollback\</c> file holding its current text);</item>
/// <item>DROPs of target-only columns (their default first);</item>
/// <item>DROPs of target-only tables (the file header gives the row count), then synonyms, sequences and table types.</item>
/// </list>
/// A drop waits for everything that depends on it: an object can only go when every dependent (a module that uses it, a
/// foreign key, an index, a schema-bound view, ...) goes in an earlier file of this folder. Anything that would stay
/// behind, anything a probe could not check, and every row that would not survive a column change is held back to
/// <c>99_review\not_applied_destructive.md</c> with the reason.
/// </summary>
public sealed class DestructiveGenerator : IFolderGenerator
{
    public const string Folder = "11_destructive";
    public const string Title = "Changes that can lose data";

    /// <summary>File order: the folder runs the kinds in this order, then dependencies move an item only as far as they must.</summary>
    private enum Kind { AlterColumn, ReplaceCheck, DropForeignKey, DropCheck, DropUnique, DropIndex, DropModule, DropColumn, DropTable, DropSynonym, DropSequence, DropTableType }

    /// <summary>Something that must be gone before an item can go: <see cref="By"/> is the item that drops it, or null when it stays.</summary>
    private sealed record Dep(string What, Item? By, bool Unknown = false);

    private sealed class Item
    {
        public required Kind Kind { get; init; }
        /// <summary>What review rows and file names show: schema.name, table.column or the constraint name.</summary>
        public required string Name { get; init; }
        /// <summary>The review's Kind column: procedure, table, column, check, ...</summary>
        public required string Word { get; init; }
        public required ObjectName Table { get; init; }   // the object itself for object drops
        public string? Sub { get; init; }                 // column, constraint or index name
        public ModuleRow? Module { get; init; }
        public string FileWord { get; init; } = "";
        public List<Dep> Deps { get; } = [];
        public List<string> Statements { get; } = [];
        public List<string> Header { get; } = [];
        /// <summary>Scripted-but-check rows for review, written only when the item is kept.</summary>
        public List<(string Kind, string Name, string Reason)> Notes { get; } = [];
        public string? Held { get; set; }
        public bool IsObject => Kind is Kind.DropModule or Kind.DropTable or Kind.DropSynonym or Kind.DropSequence or Kind.DropTableType;
    }

    private static readonly HashSet<string> Unprobeable = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "xml", "sql_variant", "timestamp", "rowversion", "hierarchyid", "geometry", "geography",
    };

    public async Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx)
    {
        var ct = ctx.Ct;
        var src = ctx.Source;
        var tgt = ctx.Target;
        var cmp = tgt.NameComparer;
        var names = tgt.NameStringComparer;
        var tgtLabel = ctx.TargetEndpoint.Label;
        var srcLabel = ctx.SourceEndpoint.Label;
        var result = new FolderResult { Folder = Folder, Title = Title, InRunAll = false };

        ctx.Review.Describe(ReviewLog.NotAppliedDestructive, "Changes that can lose data, held back",
            $"11_destructive would drop or change each of these in {tgtLabel}, but it would fail, leave something broken, or lose " +
            "rows - or a read-only check could not prove otherwise - so no script is written for it.");

        // ---- what the target has around each object (read-only catalog probes; null = could not be read)
        var deps = await ctx.Probes.ModuleDependenciesAsync(ct);
        var typeRefs = await ctx.Probes.TypeReferencersAsync(ct);
        var colDeps = await ctx.Probes.ColumnDependentsAsync(ct);
        var triggerParents = await ctx.Probes.TriggerParentsAsync(ct);
        var special = await ctx.Probes.SpecialTableNamesAsync(ct);
        var specialNames = new HashSet<string>(special ?? [], StringComparer.OrdinalIgnoreCase);
        string? Special(ObjectName table) =>
            special is null ? "could not check whether the table is CDC-tracked, replicated or temporal"
            : specialNames.Contains(table.Name) ? "the table is CDC-tracked, replicated or temporal - change it by hand" : null;

        var items = new List<Item>();
        void Hold(Item i, string why) => i.Held ??= why;

        var common = src.SortedTables().Where(t => tgt.Tables.Contains(t) && ctx.InScope(t)).ToList();

        // ---- checks, foreign keys, unique keys and indexes on tables both sides have
        foreach (var k in common)
        {
            ct.ThrowIfCancellationRequested();
            var excluded = ctx.IsExcluded(k);
            Item Constraint(Kind kind, string word, string name, string fileWord)
            {
                var i = new Item { Kind = kind, Name = name, Word = word, Table = k, Sub = name, FileWord = fileWord };
                items.Add(i);
                if (excluded || ctx.IsExcluded(new ObjectName(k.Schema, name))) Hold(i, "matches an exclude pattern");
                return i;
            }
            var srcChecks = src.Checks.Where(r => cmp.Equals(r.Table, k)).ToList();
            foreach (var t in tgt.Checks.Where(r => cmp.Equals(r.Table, k)).OrderBy(r => r.CName, StringComparer.Ordinal))
            {
                if (srcChecks.Any(s => ScriptText.Norm(s.Definition) == ScriptText.Norm(t.Definition))) continue;
                var same = t.IsSystemNamed ? null : srcChecks.FirstOrDefault(s => !s.IsSystemNamed && names.Equals(s.CName, t.CName));
                if (same is null)
                {
                    var d = Constraint(Kind.DropCheck, "check", t.CName, "drop_check");
                    d.Statements.Add(DropConstraint(k, t.CName));
                    d.Header.Add($"Drops CHECK {t.CName} on {k}: {srcLabel} has no such rule. Rule: {t.Definition}");
                    continue;
                }
                var r = Constraint(Kind.ReplaceCheck, "check", t.CName, "replace_check");
                r.Header.Add($"Replaces CHECK {t.CName} on {k} with the {srcLabel} rule.");
                r.Header.Add($"{tgtLabel} rule: {t.Definition}");
                r.Header.Add($"{srcLabel} rule: {same.Definition}");
                r.Statements.Add(DropConstraint(k, t.CName));
                r.Statements.Add(Ddl.AddCheck(same with { Sch = k.Schema, Tbl = k.Name }));
                if (same.IsDisabled) r.Statements.Add(Ddl.DisableConstraint(same with { Sch = k.Schema, Tbl = k.Name }));
                if (r.Held is null && !same.IsNotTrusted && !same.IsDisabled)
                {
                    var probe = await ctx.Probes.CheckViolationsAsync(k, same.Definition, ct);
                    if (probe.Count > 0) Hold(r, $"{N(probe.Count.Value)} row(s) in {tgtLabel} break the {srcLabel} rule {same.Definition} - ADD WITH CHECK would fail");
                    else if (!probe.Checked) Hold(r, $"existing {tgtLabel} rows could not be checked against the {srcLabel} rule ({probe.NotCheckedReason})");
                    else r.Header.Add($"Checked when generated: no {tgtLabel} row breaks the {srcLabel} rule.");
                }
            }

            var srcFks = src.FKeys.Where(r => cmp.Equals(r.Table, k)).Select(FkKey).ToHashSet(StringComparer.Ordinal);
            foreach (var f in tgt.FKeys.Where(r => cmp.Equals(r.Table, k)).OrderBy(r => r.CName, StringComparer.Ordinal))
            {
                if (srcFks.Contains(FkKey(f))) continue;
                var i = Constraint(Kind.DropForeignKey, "foreignkey", f.CName, "drop_fk");
                i.Statements.Add(DropConstraint(k, f.CName));
                i.Header.Add($"Drops FOREIGN KEY {f.CName} on {k} ({f.Cols} -> {f.Referenced} ({f.RCols})): {srcLabel} has no such key.");
            }

            var srcKeys = src.Keys.Where(r => cmp.Equals(r.Table, k)).Select(r => r.KType + "\0" + ScriptText.Norm(r.Cols)).ToHashSet(StringComparer.Ordinal);
            foreach (var u in tgt.Keys.Where(r => cmp.Equals(r.Table, k) && r.KType == "UQ").OrderBy(r => r.CName, StringComparer.Ordinal))
            {
                if (srcKeys.Contains(u.KType + "\0" + ScriptText.Norm(u.Cols))) continue;
                var i = Constraint(Kind.DropUnique, "key", u.CName, "drop_unique");
                i.Statements.Add(DropConstraint(k, u.CName));
                i.Header.Add($"Drops UNIQUE {u.CName} on {k} ({u.Cols}): {srcLabel} has no such key. Duplicates become possible.");
            }

            var srcIx = src.Indexes.Where(r => cmp.Equals(r.Table, k)).Select(IxKey).ToHashSet(StringComparer.Ordinal);
            foreach (var x in tgt.Indexes.Where(r => cmp.Equals(r.Table, k)).OrderBy(r => r.IName, StringComparer.Ordinal))
            {
                if (srcIx.Contains(IxKey(x))) continue;
                var i = Constraint(Kind.DropIndex, "index", x.IName, "drop_index");
                i.Statements.Add($"DROP INDEX {Ddl.Bracket(x.IName)} ON {k.Bracketed}");
                i.Header.Add($"Drops {(x.IsUnique ? "UNIQUE " : "")}{x.TypeDesc} INDEX {x.IName} on {k} ({x.Cols}): {srcLabel} has no such index.");
            }
        }

        // ---- target-only modules
        foreach (var k in tgt.SortedModules())
        {
            if (src.Modules.ContainsKey(k) || !ctx.InScope(k)) continue;
            var m = tgt.Modules[k];
            var word = FormattingGenerator.KindWord(m.OType);
            var i = new Item { Kind = Kind.DropModule, Name = k.ToString(), Word = word, Table = k, Module = m, FileWord = "drop_" + ScriptText.Safe(word) };
            items.Add(i);
            if (ctx.IsExcluded(k)) Hold(i, "matches an exclude pattern");
            else if (Ddl.DropWord(m.OType) is not { } dw) Hold(i, $"object type {m.OType.Trim()} - drop it by hand");
            else if (m.Definition is null) Hold(i, "no text (encrypted), so no rollback could be written - drop it by hand if you are sure");
            else
            {
                i.Statements.Add($"DROP {dw} {k.Bracketed}");
                i.Header.Add($"Drops {word} {k}: {srcLabel} does not have it. rollback\\ holds its current text.");
            }
        }

        // ---- target-only columns of tables both sides have
        foreach (var k in common)
        {
            foreach (var p in tgt.ColumnsOf(k))
            {
                if (src.FindColumn(k, p.Col) is not null) continue;
                var i = new Item { Kind = Kind.DropColumn, Name = $"{k.Name}.{p.Col}", Word = "column", Table = k, Sub = p.Col, FileWord = "drop_column" };
                items.Add(i);
                if (ctx.IsExcluded(k)) Hold(i, "matches an exclude pattern");
                if (Special(k) is { } sp) Hold(i, sp);
                foreach (var d in tgt.Defaults.Where(d => cmp.Equals(d.Table, k) && names.Equals(d.Col, p.Col)))
                    i.Statements.Add(DropConstraint(k, d.CName));
                i.Statements.Add($"ALTER TABLE {k.Bracketed} DROP COLUMN {Ddl.Bracket(p.Col)}");
                i.Header.Add($"Drops column {p.Col} ({Bare(p)} {(p.IsNullable ? "NULL" : "NOT NULL")}) of {k}: {srcLabel} does not have it. Its values are lost.");
            }
        }

        // ---- target-only tables, synonyms, sequences, table types
        foreach (var k in tgt.SortedTables())
        {
            if (src.Tables.Contains(k) || !ctx.InScope(k)) continue;
            var i = new Item { Kind = Kind.DropTable, Name = k.ToString(), Word = "table", Table = k, FileWord = "drop_table" };
            items.Add(i);
            if (ctx.IsExcluded(k)) Hold(i, "matches an exclude pattern");
            if (Special(k) is { } sp) Hold(i, sp);
            i.Statements.Add($"DROP TABLE {k.Bracketed}");
        }
        var srcSyn = await ctx.SourceProbes.SynonymsAsync(ct);
        var tgtSyn = await ctx.Probes.SynonymsAsync(ct);
        if (srcSyn is not null && tgtSyn is not null)
        {
            var have = new HashSet<ObjectName>(srcSyn.Select(s => s.Key), cmp);
            foreach (var s in tgtSyn.Where(s => !have.Contains(s.Key) && ctx.InScope(s.Key)).OrderBy(s => s.Key, ObjectName.Ordinal))
                items.Add(ObjectDrop(ctx, Kind.DropSynonym, s.Key, "synonym", "SYNONYM", $"Drops synonym {s.Key} (for {s.BaseObject}): {srcLabel} does not have it."));
        }
        var srcSeq = await ctx.SourceProbes.SequencesAsync(ct);
        var tgtSeq = await ctx.Probes.SequencesAsync(ct);
        if (srcSeq is not null && tgtSeq is not null)
        {
            var have = new HashSet<ObjectName>(srcSeq.Select(s => s.Key), cmp);
            foreach (var s in tgtSeq.Where(s => !have.Contains(s.Key) && ctx.InScope(s.Key)).OrderBy(s => s.Key, ObjectName.Ordinal))
                items.Add(ObjectDrop(ctx, Kind.DropSequence, s.Key, "sequence", "SEQUENCE",
                    $"Drops sequence {s.Key}: {srcLabel} does not have it. Its current value is lost: {Ddl.CreateSequence(s)}"));
        }
        foreach (var k in tgt.SortedTableTypes())
            if (!src.TableTypes.Contains(k) && ctx.InScope(k))
                items.Add(ObjectDrop(ctx, Kind.DropTableType, k, "table type", "TYPE", $"Drops table type {k}: {srcLabel} does not have it."));

        // ---- what has to go first
        var objects = new Dictionary<ObjectName, Item>(cmp);
        foreach (var i in items.Where(i => i.IsObject)) objects[i.Table] = i;
        Item? Find(Kind kind, ObjectName table, string sub) =>
            items.FirstOrDefault(i => i.Kind == kind && cmp.Equals(i.Table, table) && names.Equals(i.Sub, sub));
        Item? FindCheck(ObjectName table, string cname) => Find(Kind.DropCheck, table, cname) ?? Find(Kind.ReplaceCheck, table, cname);
        Item? FindColumn(ObjectName table, string col) =>
            items.FirstOrDefault(i => i.Kind == Kind.DropColumn && cmp.Equals(i.Table, table) && string.Equals(i.Sub, col, StringComparison.OrdinalIgnoreCase));
        Item? TableItem(ObjectName t) => objects.TryGetValue(t, out var i) && i.Kind == Kind.DropTable ? i : null;
        var parents = new Dictionary<ObjectName, ObjectName>(cmp);
        foreach (var tp in triggerParents ?? []) parents.TryAdd(tp.Trigger, tp.Table);

        // a referencing object, as sys.sql_expression_dependencies names it: a module, a table (computed column), a check or a default
        Dep? Resolve(ObjectName r, Item self)
        {
            var own = self.Table;
            if (cmp.Equals(r, own)) return null;
            if (objects.TryGetValue(r, out var o)) return new Dep($"{o.Word} {r} uses it", o);
            if (parents.TryGetValue(r, out var parent) && self.Kind == Kind.DropTable && cmp.Equals(parent, own)) return null;
            if (tgt.Checks.FirstOrDefault(c => cmp.Equals(new ObjectName(c.Sch, c.CName), r)) is { } ck)
            {
                if (self.Kind == Kind.DropTable && cmp.Equals(ck.Table, own)) return null;
                return new Dep($"check {ck.CName} on {ck.Table} uses it", FindCheck(ck.Table, ck.CName) ?? TableItem(ck.Table));
            }
            if (tgt.Defaults.FirstOrDefault(d => cmp.Equals(new ObjectName(d.Sch, d.CName), r)) is { } df)
            {
                if (self.Kind == Kind.DropTable && cmp.Equals(df.Table, own)) return null;
                return new Dep($"default {df.CName} on {df.Table}.{df.Col} uses it", FindColumn(df.Table, df.Col) ?? TableItem(df.Table));
            }
            if (tgt.Tables.Contains(r)) return new Dep($"table {r} uses it (a computed column or constraint)", TableItem(r));
            return new Dep($"{(tgt.FindModule(r) is { } m ? FormattingGenerator.KindWord(m.OType) : "object")} {r} uses it", null);
        }
        void AddRefs(Item i, ObjectName target)
        {
            if (deps is null) { i.Deps.Add(new Dep($"could not read what uses {target} in {tgtLabel}", null, Unknown: true)); return; }
            foreach (var d in deps.Where(d => cmp.Equals(d.Referenced, target)))
                if (Resolve(d.Referencing, i) is { } dep) i.Deps.Add(dep);
        }
        void AddFkRefs(Item i, ObjectName table, Func<ForeignKeyRow, bool> which)
        {
            foreach (var f in tgt.FKeys.Where(f => f.RTbl.Length > 0 && cmp.Equals(f.Referenced, table) && which(f)))
            {
                if (cmp.Equals(f.Table, table) && i.Kind == Kind.DropTable) continue;   // a self-reference goes with the table
                i.Deps.Add(new Dep($"foreign key {f.CName} on {f.Table} references it", TableItem(f.Table) ?? Find(Kind.DropForeignKey, f.Table, f.CName)));
            }
        }
        static HashSet<string> ColSet(string? cols) => Ddl.BracketedNames(cols).Select(c => c.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        // ---- column changes on tables both sides have, planned last: a dependent another file drops is waited for, not recreated
        var cover = ColumnCover.Build(tgt, colDeps);
        foreach (var k in common)
        {
            foreach (var c in src.ColumnsOf(k).OrderBy(c => c.Col.ToLowerInvariant(), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var p = tgt.FindColumn(k, c.Col);
                if (p is null || c.IsComputed || p.IsComputed) continue;
                // a widening is 04's; with NOT NULL as well it is planned here, to the source's type, so it never undoes 04
                var typeDiffers = Bare(c) != Bare(p);
                var tighten = !c.IsNullable && p.IsNullable;
                if (!(typeDiffers && !IsWidening(c, p)) && !tighten) continue;
                var item = new Item
                {
                    Kind = Kind.AlterColumn, Name = $"{k.Name}.{p.Col}", Word = "column", Table = k, Sub = p.Col, FileWord = "alter_column",
                };
                items.Add(item);
                await PlanAlterAsync(ctx, item, k, c, p, typeDiffers, tighten, cover, Special(k), colDeps is null, Dropping);
            }
        }

        // the item that drops a column dependent an ALTER COLUMN would trip over (a unique key, foreign key or index), if any
        Item? Dropping(ColumnDependent d, ObjectName table) => d.Kind switch
        {
            ColumnDependentKind.Unique => tgt.Keys.FirstOrDefault(u => cmp.Equals(u.Table, table) && $"unique key {u.CName}" == d.Name) is { } u
                ? Find(Kind.DropUnique, table, u.CName) : null,
            ColumnDependentKind.Index => tgt.Indexes.FirstOrDefault(x => cmp.Equals(x.Table, table) && $"index {x.IName}" == d.Name) is { } x
                ? Find(Kind.DropIndex, table, x.IName) : null,
            ColumnDependentKind.ForeignKey => tgt.FKeys.FirstOrDefault(f =>
                    (cmp.Equals(f.Table, table) && $"foreign key {f.CName}" == d.Name) || $"foreign key {f.CName} on {f.Sch}.{f.Tbl}" == d.Name) is { } f
                ? Find(Kind.DropForeignKey, f.Table, f.CName) ?? TableItem(f.Table) : null,
            _ => null,
        };

        foreach (var i in items)
        {
            switch (i.Kind)
            {
                case Kind.DropModule or Kind.DropSynonym or Kind.DropSequence:
                    AddRefs(i, i.Table);
                    break;
                case Kind.DropTable:
                    AddRefs(i, i.Table);
                    AddFkRefs(i, i.Table, _ => true);
                    break;
                case Kind.DropTableType:
                    if (typeRefs is null) i.Deps.Add(new Dep($"could not read which modules use {i.Table} in {tgtLabel}", null, Unknown: true));
                    else
                        foreach (var r in typeRefs.Where(r => cmp.Equals(r.Type, i.Table)))
                            i.Deps.Add(new Dep($"{(tgt.FindModule(r.Module) is { } m ? FormattingGenerator.KindWord(m.OType) : "module")} {r.Module} uses it",
                                objects.TryGetValue(r.Module, out var o) ? o : null));
                    break;
                case Kind.DropUnique:
                {
                    var key = ColSet(tgt.Keys.First(u => cmp.Equals(u.Table, i.Table) && u.CName == i.Sub).Cols);
                    AddFkRefs(i, i.Table, f => ColSet(f.RCols).SetEquals(key));
                    break;
                }
                case Kind.DropIndex:
                {
                    var x = tgt.Indexes.First(r => cmp.Equals(r.Table, i.Table) && r.IName == i.Sub);
                    if (x.IsUnique) AddFkRefs(i, i.Table, f => ColSet(f.RCols).SetEquals(ColSet(x.Cols)));
                    break;
                }
                case Kind.DropColumn:
                    ColumnDeps(i);
                    break;
            }
        }

        void ColumnDeps(Item i)
        {
            var k = i.Table;
            var col = i.Sub!.ToLowerInvariant();
            bool Uses(string? text) => ColSet(text).Contains(col);
            foreach (var c in tgt.Checks.Where(c => cmp.Equals(c.Table, k) && Uses(c.Definition)))
                i.Deps.Add(new Dep($"check {c.CName} uses it", FindCheck(k, c.CName)));
            foreach (var x in tgt.Indexes.Where(x => cmp.Equals(x.Table, k) && (Uses(x.Cols) || Uses(x.Incl) || Uses(x.FilterDefinition))))
                i.Deps.Add(new Dep($"index {x.IName} is built on it", Find(Kind.DropIndex, k, x.IName)));
            foreach (var u in tgt.Keys.Where(u => cmp.Equals(u.Table, k) && Uses(u.Cols)))
                i.Deps.Add(new Dep($"{(u.KType == "PK" ? "primary key" : "unique key")} {u.CName} is built on it", u.KType == "UQ" ? Find(Kind.DropUnique, k, u.CName) : null));
            foreach (var f in tgt.FKeys.Where(f => cmp.Equals(f.Table, k) && Uses(f.Cols)))
                i.Deps.Add(new Dep($"foreign key {f.CName} is built on it", Find(Kind.DropForeignKey, k, f.CName)));
            AddFkRefs(i, k, f => Uses(f.RCols));
            foreach (var c in tgt.ColumnsOf(k).Where(c => c.IsComputed && !names.Equals(c.Col, i.Sub) && Uses(c.ComputedDef)))
                i.Deps.Add(new Dep($"computed column {c.Col} uses it", FindColumn(k, c.Col)));
            if (colDeps is null)
                i.Deps.Add(new Dep($"could not read the statistics and schema-bound views or functions on {k}", null, Unknown: true));
            foreach (var e in (colDeps ?? []).Where(e => cmp.Equals(e.Table, k) && string.Equals(e.Column, i.Sub, StringComparison.OrdinalIgnoreCase)))
            {
                if (e.Kind == ColumnDependentKind.Statistics) { i.Deps.Add(new Dep($"{e.Name} is built on it", null)); continue; }
                var by = items.FirstOrDefault(o => o.Kind == Kind.DropModule && "schema-bound " + o.Name == e.Name);
                i.Deps.Add(new Dep($"{e.Name} reads it", by));
            }
        }

        // ---- hold everything whose dependents stay; then order, holding any non-module dependency cycle
        List<Item> kept;
        while (true)
        {
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var i in items.Where(i => i.Held is null))
                {
                    var why = i.Deps.Select(d =>
                            d.Unknown ? d.What
                            : d.By is null ? $"{d.What}, and it stays (11_destructive does not drop it)"
                            : d.By.Held is not null ? $"{d.What}, and that is held back too"
                            : null)
                        .FirstOrDefault(w => w is not null);
                    if (why is null) continue;
                    i.Held = why;
                    changed = true;
                }
            }
            kept = items.Where(i => i.Held is null)
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Table, ObjectName.Ordinal)
                .ThenBy(i => i.Sub ?? "", StringComparer.Ordinal)
                .ToList();
            var index = kept.Select((i, n) => (i, n)).ToDictionary(x => x.i, x => x.n);
            var edges = kept.SelectMany(i => i.Deps.Where(d => d.By is not null && index.ContainsKey(d.By)).Select(d => (index[d.By!], index[i]))).ToList();
            var forced = new List<int>();
            var order = CoreRun.StableOrder(kept.Count, edges, forced);
            var stuck = forced.Select(n => kept[n]).Where(i => i.Kind != Kind.DropModule).ToList();
            if (stuck.Count == 0)
            {
                kept = order.Select(n => kept[n]).ToList();
                break;
            }
            foreach (var i in stuck) i.Held = "in a dependency cycle (for example tables whose foreign keys reference each other) - drop it by hand";
        }

        // ---- holds, scripted-with-a-note rows, and the facts later reports read
        foreach (var i in items.Where(i => i.Held is not null))
            ctx.Review.Hold(ReviewLog.NotAppliedDestructive, i.Word, i.IsObject ? i.Name : i.Sub ?? i.Name,
                i.IsObject ? null : i.Table.Name, i.Held!, Folder);
        foreach (var i in kept)
            foreach (var (kind, name, reason) in i.Notes)
                ctx.Review.Note(ReviewLog.NotAppliedDestructive, kind, name, i.Table.Name, reason, Folder);
        NoteColumnReaders(ctx, kept);

        if (kept.Count == 0)
        {
            var notes = new List<string> { ScriptGenerator.NothingToDoNote };
            var held = items.Count(i => i.Held is not null);
            notes.Add(held == 0
                ? $"{tgtLabel} has nothing that only it holds, and no column change that could lose data."
                : $"{held} change(s) held back - see 99_review\\{ReviewLog.NotAppliedDestructive}.");
            result.Notes.AddRange(notes);
            return [result];
        }

        // ---- row counts for the tables that go
        foreach (var i in kept.Where(i => i.Kind == Kind.DropTable))
        {
            var probe = await ctx.Probes.RowCountProbeAsync(i.Table, ct);
            i.Header.Insert(0, probe.Checked
                ? $"Drops table {i.Table} and its {N(probe.Count!.Value)} row(s) (COUNT_BIG(*) when this script was generated): {srcLabel} does not have it."
                : $"Drops table {i.Table} and all its rows: {srcLabel} does not have it. Rows not counted ({probe.NotCheckedReason}); " +
                  $"sys.partitions says about {N(await ctx.Probes.RowCountAsync(i.Table, ct))}.");
        }

        // ---- write
        Directory.CreateDirectory(ctx.Writer.Resolve(Folder, "objects"));
        var all = new StringBuilder(ScriptText.HdrTbl);
        var rollbacks = new List<(string File, ModuleRow Module)>();
        var n = 0;
        foreach (var i in kept)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var file = $"{n:000}_{i.FileWord}_{ScriptText.Safe(i.IsObject ? i.Table.Name : $"{i.Table.Name}_{i.Sub}")}.sql";
            var body = Body(i);
            ctx.Writer.WriteSql(Folder, "objects\\" + file, ScriptText.HdrTbl + body, WrittenFileKind.Statements, i.Table);
            all.Append(body);
            if (i.Module is { } m) rollbacks.Add((file, m));
            if (i.IsObject) ctx.Facts.DestructiveDrops.Add(i.Table);
            if (i.Kind == Kind.AlterColumn) ctx.Facts.DestructiveAlters.Add((i.Table, i.Sub!));
        }
        ctx.Writer.WriteSql(Folder, "_ALL.sql", all.ToString(), WrittenFileKind.Statements);
        if (rollbacks.Count > 0)
        {
            var back = new StringBuilder();
            var expected = new List<ExpectedModule>();
            foreach (var (file, m) in rollbacks)
                WriteModuleRollback(ctx, "rollback\\" + file, m);
            // undo in reverse: what was dropped last (what the others used) comes back first
            foreach (var (_, m) in Enumerable.Reverse(rollbacks))
            {
                back.Append(ScriptText.ModHdr(m)).Append(ScriptText.ExecBlock(m.Definition!)).Append("GO\r\n");
                expected.Add(new ExpectedModule(m.Name, m.Definition!, ModuleSettings.Of(m)));
            }
            ctx.Writer.WriteSql(Folder, "rollback\\_ALL.sql", back.ToString(), WrittenFileKind.ExecBlockModules, expectedModules: expected);
            result.HasRollback = true;
        }
        ctx.Writer.WriteRunner(Folder, ctx.RunnerServer);

        result.Files = ctx.Writer.ObjectFileCount(Folder);
        var kinds = kept.GroupBy(i => i.Kind switch
            {
                Kind.AlterColumn => "column change",
                Kind.ReplaceCheck => "replaced check",
                Kind.DropColumn => "dropped column",
                _ => "dropped " + i.Word,
            })
            .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        result.Notes.Add($"{string.Join(", ", kinds)}.");
        result.Notes.Add($"CAN LOSE DATA: never run by RUN.ps1. Back up {tgtLabel} first; a dropped table or column cannot be rolled back " +
                         "(rollback\\ only recreates dropped procedures, functions, views and triggers).");
        var heldCount = items.Count(i => i.Held is not null);
        if (heldCount > 0) result.Notes.Add($"{heldCount} held back (99_review\\{ReviewLog.NotAppliedDestructive}).");
        ctx.Review.Warn($"11_destructive: {result.Files} file(s) drop or change things in {tgtLabel} that can lose data - read the README before running any of them.");
        return [result];
    }

    // ------------------------------------------------------------------ column changes

    /// <summary>
    /// Plans one ALTER COLUMN to the source's type and nullability (keeping the target column's collation for a text
    /// type), or holds it: identity, alias or CLR types and types a probe cannot compare; a dependent SQL Server would
    /// not let the change through that cannot simply be dropped and recreated (keys, foreign keys, computed columns,
    /// statistics, schema-bound modules, an unscriptable index); and any row the data-fit probe finds, or a probe failure.
    /// </summary>
    private static async Task PlanAlterAsync(ScriptContext ctx, Item i, ObjectName k, ColumnRow c, ColumnRow p,
        bool typeDiffers, bool tighten, ColumnCover cover, string? special, bool depsUnknown, Func<ColumnDependent, ObjectName, Item?> dropping)
    {
        var tgt = ctx.Target;
        var tgtLabel = ctx.TargetEndpoint.Label;
        var srcLabel = ctx.SourceEndpoint.Label;
        var cmp = tgt.NameComparer;
        var from = $"{Bare(p)} {(p.IsNullable ? "NULL" : "NOT NULL")}";
        var bothText = CoreRun.CharTypes.Contains(c.Dt.ToLowerInvariant()) && CoreRun.CharTypes.Contains(p.Dt.ToLowerInvariant());
        var newType = typeDiffers
            ? Ddl.QType(c.Dt, c.MaxLength, c.Precision, c.Scale, bothText ? p.CollationName : null, tgt.Collation)
            : Ddl.QType(p.Dt, p.MaxLength, p.Precision, p.Scale, p.CollationName, tgt.Collation);
        var to = $"{(typeDiffers ? Bare(c) : Bare(p))} {(c.IsNullable ? "NULL" : "NOT NULL")}";
        i.Header.Add($"Changes column {p.Col} of {k} from {from} to {to}, as in {srcLabel}.");

        if (ctx.IsExcluded(k)) { i.Held = "matches an exclude pattern"; return; }
        if (special is not null) { i.Held = special; return; }
        if (p.IsIdentity || c.IsIdentity) { i.Held = "an IDENTITY column - change it by hand"; return; }
        if (!string.IsNullOrEmpty(c.TypeSchema) || !string.IsNullOrEmpty(p.TypeSchema) || Unprobeable.Contains(c.Dt) || Unprobeable.Contains(p.Dt))
        {
            i.Held = $"from {from} to {to}: a read-only check cannot prove the values survive this type change - change it by hand";
            return;
        }
        if (depsUnknown) { i.Held = $"could not read the statistics and schema-bound views or functions on {k}"; return; }

        var deps = cover.Of(k, p.Col);
        var blocking = ColumnCover.Blockers(deps, typeDiffers, p.Dt, c.Dt, c.MaxLength == -1).ToHashSet(StringComparer.Ordinal);
        // NULL to NOT NULL is also refused while an index, a key or user statistics cover the column (Msg 5074, measured;
        // ColumnCover's nullability rule is the NOT NULL to NULL direction, which they do not block)
        if (tighten)
            blocking.UnionWith(deps.Where(d => d.Kind is ColumnDependentKind.Index or ColumnDependentKind.Unique
                or ColumnDependentKind.PrimaryKey or ColumnDependentKind.Statistics).Select(d => d.Name));
        var drop = new List<string>();
        var add = new List<string>();
        var recreated = new List<string>();
        var n = 0;
        foreach (var d in deps.Where(d => blocking.Contains(d.Name)).DistinctBy(d => d.Name))
        {
            if (dropping(d, k) is { } by)
            {
                i.Deps.Add(new Dep($"{d.Name} depends on {p.Col}", by));   // dropped by an earlier file of this folder
                continue;
            }
            if (d.Kind == ColumnDependentKind.Default &&
                tgt.Defaults.FirstOrDefault(r => cmp.Equals(r.Table, k) && "default " + r.CName == d.Name) is { } df)
            {
                drop.Add(DropConstraint(k, df.CName));
                add.Add(Ddl.AddDefault(df));
            }
            else if (d.Kind == ColumnDependentKind.Check &&
                     tgt.Checks.FirstOrDefault(r => cmp.Equals(r.Table, k) && "check " + r.CName == d.Name) is { } ck && !ck.IsSystemNamed)
            {
                // re-added in the state it has when the file runs (05 may have changed it since this was generated)
                var v = $"@state{++n}";
                drop.Add($"DECLARE {v} tinyint = (SELECT CASE WHEN is_disabled = 1 THEN 2 WHEN is_not_trusted = 1 THEN 1 ELSE 0 END " +
                         $"FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID({ScriptText.Lit(k.Bracketed)}) AND name = {ScriptText.Lit(ck.CName)})");
                drop.Add(DropConstraint(k, ck.CName));
                var trusted = ck with { IsNotTrusted = false };
                add.Add($"IF {v} = 0 {Ddl.AddCheck(trusted)} ELSE {Ddl.AddCheck(trusted with { IsNotTrusted = true })}");
                add.Add($"IF {v} = 2 {Ddl.DisableConstraint(ck)}");
            }
            else if (d.Kind == ColumnDependentKind.Index &&
                     tgt.Indexes.FirstOrDefault(r => cmp.Equals(r.Table, k) && "index " + r.IName == d.Name) is { } ix && Ddl.IsScriptableIndex(ix))
            {
                drop.Add($"DROP INDEX {Ddl.Bracket(ix.IName)} ON {k.Bracketed}");
                add.Add(Ddl.CreateIndex(ix));
                recreated.Add(ix.IName);
            }
            else
            {
                i.Held = $"{d.Name} depends on {p.Col}, so SQL Server refuses the change (Msg 5074) - drop and recreate it around the change by hand";
                return;
            }
        }

        var probe = await ctx.Probes.ColumnFitViolationsAsync(k, p.Col, Bare(p), typeDiffers ? Bare(c) : null, tighten, ctx.Ct);
        if (probe.Count > 0)
        {
            i.Held = $"{N(probe.Count.Value)} row(s) in {tgtLabel} would not survive the change from {from} to {to} " +
                     (tighten ? "(NULL, or a value the new type cannot hold exactly)" : "(a value the new type cannot hold exactly)");
            return;
        }
        if (!probe.Checked)
        {
            i.Held = $"existing {tgtLabel} rows could not be checked against {to} ({probe.NotCheckedReason})";
            return;
        }
        i.Header.Add($"Checked when generated: every row of {tgtLabel} fits.");
        i.Statements.AddRange(drop);
        i.Statements.Add(Ddl.AlterColumn(k.Schema, k.Name, p.Col, newType, c.IsNullable));
        i.Statements.AddRange(add);
        foreach (var ix in recreated)
            i.Notes.Add(("index", ix,
                $"dropped and recreated around the change of {p.Col} (11_destructive) - recreated ON [PRIMARY] with default options " +
                "(fill factor, compression and partitioning are not kept)"));
    }

    /// <summary>04's widening rule: a longer type inside the integer family or the char family, never Unicode to non-Unicode.</summary>
    private static bool IsWidening(ColumnRow c, ColumnRow p)
    {
        if (CoreRun.Width(c) is not { } wd || CoreRun.Width(p) is not { } wp || wd <= wp) return false;
        var cd = c.Dt.ToLowerInvariant();
        var pd = p.Dt.ToLowerInvariant();
        var ints = CoreRun.IntSize.ContainsKey(cd) && CoreRun.IntSize.ContainsKey(pd);
        var chars = CoreRun.CharTypes.Contains(cd) && CoreRun.CharTypes.Contains(pd);
        return ints || (chars && !(pd[0] == 'n' && cd[0] != 'n'));
    }

    // ------------------------------------------------------------------ helpers

    private static Item ObjectDrop(ScriptContext ctx, Kind kind, ObjectName k, string word, string dropWord, string header)
    {
        var i = new Item { Kind = kind, Name = k.ToString(), Word = word, Table = k, FileWord = "drop_" + ScriptText.Safe(word) };
        if (ctx.IsExcluded(k)) i.Held = "matches an exclude pattern";
        i.Statements.Add($"DROP {dropWord} {k.Bracketed}");
        i.Header.Add(header);
        return i;
    }

    /// <summary>
    /// The text of one file after HDR_TBL: its header as <c>--</c> comments, then the statement; several statements run in
    /// one transaction (08's XACT_ABORT + TRY/CATCH wrapper), so a failure leaves the table as it was.
    /// </summary>
    private static string Body(Item i)
    {
        var sb = new StringBuilder();
        sb.Append("-- 11_destructive: CAN LOSE DATA. Back up the database before running this.\r\n");
        foreach (var h in i.Header) sb.Append("-- ").Append(Comment(h)).Append("\r\n");
        if (i.Statements.Count == 1) return sb.Append(i.Statements[0]).Append(ScriptText.Go).ToString();
        sb.Append("SET XACT_ABORT ON;\r\nBEGIN TRY\r\nBEGIN TRANSACTION;\r\n");
        foreach (var s in i.Statements) sb.Append(s).Append(";\r\n");
        sb.Append("COMMIT TRANSACTION;\r\nEND TRY\r\nBEGIN CATCH\r\nIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\r\nTHROW;\r\nEND CATCH\r\nGO\r\n");
        return sb.ToString();
    }

    /// <summary>A name or definition inside a <c>--</c> comment: line breaks and other control characters become '?', so nothing escapes the comment.</summary>
    private static string Comment(string s) =>
        string.Concat((s ?? "").Select(ch => char.IsControl(ch) || char.IsSurrogate(ch) ? '?' : ch));

    /// <summary>The rollback of a dropped module: its current text with its own SET options (the 03 per-object form).</summary>
    private static void WriteModuleRollback(ScriptContext ctx, string path, ModuleRow m)
    {
        var settings = ModuleSettings.Of(m);
        if (ScriptText.RawModuleProblem(m.Definition) is null && !ScriptText.HasBatchBreakingLine(m.Definition!))
            ctx.Writer.WriteSql(Folder, path, ScriptText.ModHdr(m) + m.Definition, WrittenFileKind.RawModule,
                m.Name, expectedModuleText: m.Definition, settings: settings);
        else
            ctx.Writer.WriteSql(Folder, path, ScriptText.ModHdr(m) + ScriptText.ExecBlock(m.Definition!) + "GO\r\n",
                WrittenFileKind.ExecBlockModules, m.Name, settings: settings,
                expectedModules: [new ExpectedModule(m.Name, m.Definition!, settings)]);
    }

    /// <summary>
    /// A dropped column that a module staying in the target names: the drop goes through (SQL Server
    /// does not check code that is not schema-bound), but that module fails when it runs. Listed as a note to check.
    /// </summary>
    private static void NoteColumnReaders(ScriptContext ctx, List<Item> kept)
    {
        var dropped = new HashSet<ObjectName>(kept.Where(i => i.IsObject).Select(i => i.Table), ctx.Target.NameComparer);
        var columns = kept.Where(i => i.Kind == Kind.DropColumn).ToList();
        if (columns.Count == 0) return;
        foreach (var m in ctx.Target.SortedModules().Select(k => ctx.Target.Modules[k]))
        {
            if (dropped.Contains(m.Name) || m.Definition is null) continue;
            var words = new HashSet<string>(TsqlTokens.Tokens(m.Definition).Where(t => t.Kind == 'I').Select(t => t.Text), StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns.Where(c => words.Contains(c.Table.Name) && words.Contains(c.Sub!)))
                ctx.Review.Note(ReviewLog.NotAppliedDestructive, "column", c.Sub!, c.Table.Name,
                    $"dropped by 11_destructive, but {FormattingGenerator.KindWord(m.OType)} {m.Name} names {c.Table.Name} and {c.Sub} - it will fail when it runs if it reads this column", Folder);
        }
    }

    private static string DropConstraint(ObjectName table, string name) => $"ALTER TABLE {table.Bracketed} DROP CONSTRAINT {Ddl.Bracket(name)}";

    /// <summary>The type alone, without collation (04's <c>bare</c>).</summary>
    private static string Bare(ColumnRow c) => Ddl.QType(c.Dt, c.MaxLength, c.Precision, c.Scale, null, null);

    private static string FkKey(ForeignKeyRow r) =>
        $"{ScriptText.Norm(r.Cols)}\0{r.RSch.ToLowerInvariant()}\0{r.RTbl.ToLowerInvariant()}\0{ScriptText.Norm(r.RCols)}";

    private static string IxKey(IndexRow r) =>
        $"{ScriptText.Norm(r.Cols)}\0{ScriptText.Norm(r.Incl)}\0{r.IsUnique}";

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}
