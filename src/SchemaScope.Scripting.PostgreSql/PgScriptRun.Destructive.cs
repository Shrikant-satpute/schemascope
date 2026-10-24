using System.Globalization;
using System.Text;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting.Planning;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// 11_destructive for PostgreSQL (plan R3b), the SQL Server folder's design on PostgreSQL's catalog: written only with
/// <see cref="ScriptOptions.IncludeDestructive"/> (which the API allows only in write mode, same engine on both sides),
/// never run by RUN.ps1, applied only with the second "I understand this can lose data" tick. One file per change, in
/// a fixed order:
/// <list type="number">
/// <item>column changes no other folder makes - a type that is no widening, NULL to NOT NULL - each after a read-only
/// probe that every row fits, checked again inside the file before the change;</item>
/// <item>checks whose name both sides use for a different rule: DROP and ADD, after a probe of the rows;</item>
/// <item>drops of the target's own defaults, foreign keys, checks, unique keys and indexes on tables both sides have;</item>
/// <item>drops of the target's own views, materialized views, functions, procedures, aggregates and triggers (each with
/// a <c>rollback\</c> file holding its current text);</item>
/// <item>drops of the target's own columns (the file header gives how many values go);</item>
/// <item>drops of the target's own tables (the header gives the row count), then sequences, domains, enum and composite types.</item>
/// </list>
/// Every DROP says RESTRICT: a dependent this run does not know of makes the statement fail, and the file's transaction
/// with it, instead of cascading. A drop waits for every dependent SchemaScope knows of (pg_depend, foreign keys, the
/// table's own keys, checks and indexes) to go in an earlier file; one that would stay, a probe that could not run,
/// and a name the source has in another letter case (PostgreSQL names are exact, so those are two objects) hold it back
/// to <c>99_review\not_applied_destructive.md</c> with the reason. Names are matched exactly, as the compare matches them.
/// </summary>
internal sealed partial class PgScriptRun
{
    /// <summary>11_destructive is part of this run.</summary>
    private bool Destroys => Options.IncludeDestructive;

    /// <summary>File order: the folder runs the kinds in this order, then dependencies move an item only as far as they must.</summary>
    private enum DKind
    {
        AlterColumn, ReplaceCheck, DropDefault, DropForeignKey, DropCheck, DropUnique, DropIndex, DropModule, DropColumn, DropTable,
        DropSequence, DropType
    }

    /// <summary>Something that must be gone before an item can go: <see cref="By"/> is the item that drops it, or null when it stays.</summary>
    private sealed record DDep(string What, DItem? By, bool Unknown = false);

    private sealed class DItem
    {
        public required DKind Kind { get; init; }
        /// <summary>What the review row names: the object for object drops, the column, constraint or index otherwise.</summary>
        public required string Name { get; init; }
        /// <summary>The review's Kind column: view, table, column, check, ...</summary>
        public required string Word { get; init; }
        /// <summary>The table the change is on, or the object itself for an object drop.</summary>
        public required DbObject Table { get; init; }
        /// <summary>Column, constraint or index name.</summary>
        public string? Sub { get; init; }
        public string FileWord { get; init; } = "";
        public List<DDep> Deps { get; } = [];
        public List<string> Statements { get; } = [];
        public List<string> Header { get; } = [];
        /// <summary>Scripted-but-check rows for the review, written only when the item is kept.</summary>
        public List<(string Kind, string Name, string Reason)> Notes { get; } = [];
        public string? Held { get; set; }
        public bool IsObject => Kind is DKind.DropModule or DKind.DropTable or DKind.DropSequence or DKind.DropType;
    }

    private async Task PlanDestructiveAsync()
    {
        var srcLabel = req.Source.Label;
        var tgtLabel = req.Target.Label;
        var folder = Destructive.Name;
        review.Describe(ReviewLog.NotAppliedDestructive, "Changes that can lose data, held back",
            $"11_destructive would drop or change each of these in {tgtLabel}, but it would fail, leave something broken, or lose " +
            "rows - or a read-only check could not prove otherwise - so no script is written for it.");

        var items = new List<DItem>();
        void Hold(DItem i, string why) => i.Held ??= why;

        // ---- names the source has in another letter case: to PostgreSQL another object, to a person most likely the same one
        static string Folded(string schema, string name) => $"{schema.ToLowerInvariant()}\0{name.ToLowerInvariant()}";
        var srcNamed = Src.Objects.Where(o => o.Type is not (DbObjectType.Schema or DbObjectType.Trigger or DbObjectType.Extension)).ToList();
        var srcExact = srcNamed.Select(o => $"{o.Schema}\0{o.Name}").ToHashSet(StringComparer.Ordinal);
        var srcFolded = srcNamed.Select(o => Folded(o.Schema, o.Name)).ToHashSet(StringComparer.Ordinal);
        bool TwinObject(DbObject o) => o.Type == DbObjectType.Trigger
            ? Src.Objects.Any(x => x.Type == DbObjectType.Trigger && !(x.Schema == o.Schema && x.ParentName == o.ParentName && x.Name == o.Name) &&
                                   Same(x.Schema, o.Schema) && Same(x.ParentName, o.ParentName) && Same(x.Name, o.Name))
            : srcFolded.Contains(Folded(o.Schema, o.Name)) && !srcExact.Contains($"{o.Schema}\0{o.Name}");
        static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        static bool TwinName(IEnumerable<string> sourceNames, string name) => sourceNames.Any(n => n != name && Same(n, name));
        string OtherCase(string what) =>
            $"{srcLabel} has {what} spelled in another letter case. PostgreSQL names are exact, so to it they are two different objects - decide by hand which one is meant";

        DItem Part(DKind kind, string word, DbObject table, string sub, string fileWord)
        {
            var i = new DItem { Kind = kind, Name = sub, Word = word, Table = table, Sub = sub, FileWord = $"{fileWord}_{table.Schema}.{table.Name}.{sub}" };
            items.Add(i);
            if (IsExcluded(table)) Hold(i, "matches an exclude pattern");
            else if (IsChild(table)) Hold(i, ChildReason);
            return i;
        }
        string Drop(DbObject table, string constraint) => $"ALTER TABLE {PgDdl.Q(table)} DROP CONSTRAINT {PgDdl.Q(constraint)} RESTRICT";

        // tables both sides have, by exact name
        var common = Tgt.Objects.Where(o => o.Type == DbObjectType.Table && s.TryGetValue(o.Key, out var so) && InScope(so))
            .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
            .Select(o => (Src: s[o.Key], Tgt: o)).ToList();

        // ---- defaults, checks, foreign keys, keys and indexes on tables both sides have (matched by name, as 05 and 06 match them)
        foreach (var (src, tgt) in common)
        {
            ct.ThrowIfCancellationRequested();
            var srcChecks = src.Constraints.Where(c => c.Kind == ConstraintKind.Check).ToList();
            foreach (var tck in tgt.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var same = srcChecks.FirstOrDefault(c => c.Name == tck.Name);
                if (same is null)
                {
                    var d = Part(DKind.DropCheck, "check", tgt, tck.Name, "drop_check");
                    if (TwinName(srcChecks.Select(c => c.Name), tck.Name)) Hold(d, OtherCase($"check {tck.Name} on {tgt.FullName}"));
                    d.Statements.Add(Drop(tgt, tck.Name));
                    d.Header.Add($"Drops CHECK {tck.Name} on {tgt.FullName}: {srcLabel} has no such rule. Rule: {tck.Definition}");
                    continue;
                }
                if (same.Definition == tck.Definition) continue;
                var r = Part(DKind.ReplaceCheck, "check", tgt, tck.Name, "replace_check");
                r.Header.Add($"Replaces CHECK {tck.Name} on {tgt.FullName} with the {srcLabel} rule.");
                r.Header.Add($"{tgtLabel} rule: {tck.Definition}");
                r.Header.Add($"{srcLabel} rule: {same.Definition}");
                r.Statements.Add(Drop(tgt, tck.Name));
                r.Statements.Add($"ALTER TABLE {PgDdl.Q(tgt)} ADD CONSTRAINT {PgDdl.Q(same.Name)} {same.Definition}{(same.IsNotTrusted ? " NOT VALID" : "")}");
                if (r.Held is not null) continue;
                if (Unavailable(src, src.Columns.Where(c => PgDdl.MentionsColumn(same.Definition, c.Name)).Select(c => c.Name)) is { } col)
                    Hold(r, $"the {srcLabel} rule reads {col}, which the target will not have");
                else if (MissingNamed(same.Definition, before03: false) is { } needs)
                    Hold(r, $"the {srcLabel} rule uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}");
                else if (same.IsNotTrusted)
                    r.Header.Add($"Added NOT VALID, as {srcLabel} has it: the rows already there are not checked.");
                else if (CheckExpression(same.Definition) is not { } expression)
                    Hold(r, $"existing {tgtLabel} rows could not be checked against the {srcLabel} rule (its expression could not be read)");
                else
                {
                    var probe = await probes.CheckViolationsAsync(PgDdl.Q(tgt), same.Name, expression, ct);
                    if (probe.Count > 0) Hold(r, $"{N(probe.Count.Value)} row(s) in {tgtLabel} break the {srcLabel} rule {same.Definition} - adding it would fail");
                    else if (!probe.Checked) Hold(r, $"existing {tgtLabel} rows could not be checked against the {srcLabel} rule ({probe.NotCheckedReason})");
                    else r.Header.Add($"Checked when generated: no {tgtLabel} row breaks the {srcLabel} rule.");
                }
            }

            foreach (var c in tgt.Columns.OrderBy(c => c.Ordinal))
            {
                var sc = src.Columns.FirstOrDefault(x => x.Name == c.Name);
                // a column only the target has goes with its default; an identity or generated column has none of its own
                if (sc is null || c.IsComputed || sc.IsComputed || c.IsIdentity || sc.IsIdentity) continue;
                if (DefaultOf(tgt, c.Name) is not { } td || DefaultOf(src, c.Name) is not null) continue;
                var i = Part(DKind.DropDefault, "default", tgt, c.Name, "drop_default");
                i.Statements.Add($"ALTER TABLE {PgDdl.Q(tgt)} ALTER COLUMN {PgDdl.Q(c.Name)} DROP DEFAULT");
                i.Header.Add($"Drops the default of {tgt.FullName}.{c.Name} ({td.Definition}): {srcLabel} has none. New rows that leave it out get NULL from now on.");
            }

            foreach (var f in tgt.ForeignKeys.Where(x => src.ForeignKeys.All(y => y.Name != x.Name)).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var i = Part(DKind.DropForeignKey, "foreignkey", tgt, f.Name, "drop_fk");
                if (TwinName(src.ForeignKeys.Select(y => y.Name), f.Name)) Hold(i, OtherCase($"foreign key {f.Name} on {tgt.FullName}"));
                i.Statements.Add(Drop(tgt, f.Name));
                i.Header.Add($"Drops FOREIGN KEY {f.Name} on {tgt.FullName} ({Strip(f.Definition)}): {srcLabel} has no such key.");
            }

            var srcIndexNames = src.Indexes.Select(x => x.Name).ToList();
            foreach (var k in tgt.Indexes.Where(x => IsKey(x) && src.Indexes.All(i => !IsKey(i) || i.Name != x.Name)).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var what = k.IsPrimaryKey ? "PRIMARY KEY" : k.IsUniqueConstraint ? "UNIQUE" : "EXCLUDE constraint";
                var i = Part(DKind.DropUnique, k.IsPrimaryKey ? "primary key" : "key", tgt, k.Name, k.IsPrimaryKey ? "drop_primary_key" : "drop_unique");
                if (k.IsPrimaryKey) Hold(i, "the target's primary key: 11_destructive does not drop a primary key - drop it by hand if you are sure");
                if (TwinName(srcIndexNames, k.Name)) Hold(i, OtherCase($"{what} {k.Name} on {tgt.FullName}"));
                i.Statements.Add(Drop(tgt, k.Name));
                i.Header.Add($"Drops {what} {k.Name} on {tgt.FullName} ({k.Definition}): {srcLabel} has no such key." +
                             (k.IsUniqueConstraint ? " Duplicates become possible." : ""));
            }

            foreach (var x in tgt.Indexes.Where(x => !IsKey(x) && src.Indexes.All(i => IsKey(i) || i.Name != x.Name)).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var i = Part(DKind.DropIndex, "index", tgt, x.Name, "drop_index");
                if (TwinName(srcIndexNames, x.Name)) Hold(i, OtherCase($"index {x.Name} on {tgt.FullName}"));
                i.Statements.Add($"DROP INDEX {PgDdl.Q(tgt.Schema, x.Name)} RESTRICT");
                i.Header.Add($"Drops {(x.IsUnique ? "UNIQUE " : "")}INDEX {x.Name} on {tgt.FullName}: {srcLabel} has no such index. ({x.Definition})");
            }
        }

        // ---- objects only the target has
        var objects = new Dictionary<string, DItem>(StringComparer.Ordinal);
        foreach (var o in Tgt.Objects.Where(o => !s.ContainsKey(o.Key) && InScope(o))
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Signature ?? "", StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (o.Type == DbObjectType.Schema)
            {
                review.Hold(ReviewLog.NotAppliedDestructive, "schema", o.Name, null,
                    $"11_destructive does not drop schemas: drop it by hand once it is empty (DROP SCHEMA {PgDdl.Q(o.Name)} RESTRICT)", folder);
                continue;
            }
            if (o.Type == DbObjectType.Extension)
            {
                review.Hold(ReviewLog.NotAppliedDestructive, "extension", o.Name, null,
                    $"11_destructive does not drop extensions: drop it by hand if you are sure (DROP EXTENSION {PgDdl.Q(o.Name)} RESTRICT)", folder);
                continue;
            }
            var (kind, statement, fileWord) = o.Type switch
            {
                DbObjectType.Table => (DKind.DropTable, $"DROP TABLE {PgDdl.Q(o)} RESTRICT", "drop_table"),
                DbObjectType.Sequence => (DKind.DropSequence, $"DROP SEQUENCE {PgDdl.Q(o)} RESTRICT", "drop_sequence"),
                DbObjectType.Domain => (DKind.DropType, $"DROP DOMAIN {PgDdl.Q(o)} RESTRICT", "drop_domain"),
                DbObjectType.EnumType or DbObjectType.CompositeType => (DKind.DropType, $"DROP TYPE {PgDdl.Q(o)} RESTRICT", "drop_type"),
                _ => (DKind.DropModule, DropModule(o), "drop_" + Kind(o).Replace(' ', '_')),
            };
            var i = new DItem { Kind = kind, Name = Display(o), Word = Kind(o), Table = o, FileWord = $"{fileWord}_{Display(o)}" };
            items.Add(i);
            objects[o.Key] = i;
            if (IsExcluded(o)) Hold(i, "matches an exclude pattern");
            if (TwinObject(o)) Hold(i, OtherCase($"{Kind(o)} {Display(o)}"));
            if (statement is null) { Hold(i, $"{Kind(o)} {Display(o)} cannot be named for DROP from what was read - drop it by hand"); continue; }
            i.Statements.Add(statement);
            switch (kind)
            {
                case DKind.DropModule when o.DefinitionUnavailable || string.IsNullOrWhiteSpace(o.Definition):
                    Hold(i, "its text could not be read, so no rollback could be written - drop it by hand if you are sure");
                    break;
                case DKind.DropModule:
                    i.Header.Add($"Drops {Kind(o)} {Display(o)}: {srcLabel} does not have it. rollback\\ holds its current text.");
                    break;
                case DKind.DropSequence:
                    i.Header.Add($"Drops sequence {Display(o)}: {srcLabel} does not have it. Its current value is lost: {Sequence(o).TrimEnd()}");
                    break;
                case DKind.DropType:
                    i.Header.Add($"Drops {Kind(o)} {Display(o)}: {srcLabel} does not have it.");
                    break;
            }
        }

        // ---- columns only the target has, on tables both sides have
        foreach (var (src, tgt) in common)
            foreach (var c in tgt.Columns.Where(c => src.Columns.All(x => x.Name != c.Name)).OrderBy(c => c.Ordinal))
            {
                var i = Part(DKind.DropColumn, "column", tgt, c.Name, "drop_column");
                if (TwinName(src.Columns.Select(x => x.Name), c.Name)) Hold(i, OtherCase($"column {tgt.FullName}.{c.Name}"));
                if (tgt.Properties.GetValueOrDefault("partitionBy") is { } key && PgDdl.MentionsColumn(key, c.Name))
                    Hold(i, "it is part of the table's partition key, which PostgreSQL cannot drop a column from");
                i.Statements.Add($"ALTER TABLE {PgDdl.Q(tgt)} DROP COLUMN {PgDdl.Q(c.Name)} RESTRICT");
                i.Header.Add($"Drops column {c.Name} ({c.DataType} {(c.IsNullable ? "NULL" : "NOT NULL")}) of {tgt.FullName}: {srcLabel} does not have it. Its values are lost.");
            }

        // ---- what has to go first
        DItem? Find(DKind kind, DbObject table, string sub) => items.FirstOrDefault(i => i.Kind == kind && i.Table.Key == table.Key && i.Sub == sub);
        DItem? FindCheck(DbObject table, string name) => Find(DKind.DropCheck, table, name) ?? Find(DKind.ReplaceCheck, table, name);
        DItem? FindColumn(DbObject table, string column) => Find(DKind.DropColumn, table, column);
        DItem? TableItem(DbObject table) => objects.TryGetValue(table.Key, out var i) && i.Kind == DKind.DropTable ? i : null;
        var tables = Tgt.Objects.Where(o => o.Type == DbObjectType.Table).ToList();
        // who needs what, from pg_depend
        var needing = new Dictionary<string, List<(DbObject By, DbDependency Dep)>>(StringComparer.Ordinal);
        foreach (var o in Tgt.Objects)
            foreach (var d in o.DependsOn ?? [])
                (needing.TryGetValue(d.Key, out var list) ? list : needing[d.Key] = []).Add((o, d));

        // what a table does with the object being dropped: its columns of that type, its defaults and checks that name it
        DDep TablePart(DbObject table, DbDependency d, DbObject dropped, List<DDep> into)
        {
            bool Names(string? text) => PgDdl.Mentions(text, dropped.Schema, dropped.Name);
            var columns = d.Via switch
            {
                "default" => table.Columns.Where(c => Names(DefaultOf(table, c.Name)?.Definition) || Names(c.ComputedDefinition)).ToList(),
                "relation" => table.Columns.Where(c => Names(c.DataType)).ToList(),
                _ => [],
            };
            // a column that goes takes its default along; a column that stays waits for a file that drops its default
            foreach (var c in columns)
                into.Add(new DDep($"column {table.FullName}.{c.Name} uses it", FindColumn(table, c.Name) ??
                    (d.Via == "default" && !Names(c.ComputedDefinition) ? Find(DKind.DropDefault, table, c.Name) : null)));
            if (d.Via == "constraint")
                foreach (var ck in table.Constraints.Where(ck => ck.Kind == ConstraintKind.Check && Names(ck.Definition)))
                    into.Add(new DDep($"check {ck.Name} on {table.FullName} uses it", FindCheck(table, ck.Name)));
            var what = d.Via switch
            {
                "default" => $"a default or generated column of {table.FullName} uses it",
                "constraint" => $"a constraint of {table.FullName} uses it",
                "policy" => $"a row level security policy on {table.FullName} uses it",
                "view" => $"a rule on {table.FullName} uses it",
                "trigger" => $"a trigger on {table.FullName} uses it",
                _ => table.Properties.GetValueOrDefault("partitionOf") is { } || table.Properties.ContainsKey("inherits")
                    ? $"table {table.FullName} is a partition or child of it"
                    : $"table {table.FullName} uses it",
            };
            // nothing found to wait for: it stays, and so does the object
            return new DDep(what, null);
        }

        // the dependents of an object being dropped, as pg_depend has them (foreign keys are added from the tables)
        void AddDependents(DItem i)
        {
            var self = i.Table;
            foreach (var (by, d) in needing.GetValueOrDefault(self.Key) ?? [])
            {
                if (by.Key == self.Key || d.Via == "foreignkey") continue;
                if (objects.TryGetValue(by.Key, out var o)) { i.Deps.Add(new DDep($"{Kind(by)} {Display(by)} uses it", o)); continue; }
                if (by.Type == DbObjectType.Table)
                {
                    var found = new List<DDep>();
                    var stays = TablePart(by, d, self, found);
                    if (found.Count > 0) i.Deps.AddRange(found);
                    else i.Deps.Add(stays);
                    continue;
                }
                i.Deps.Add(new DDep($"{Kind(by)} {Display(by)} uses it", null));
            }
        }
        void AddFkRefs(DItem i, DbObject table, Func<ForeignKeyInfo, bool> which)
        {
            foreach (var other in tables)
                foreach (var f in other.ForeignKeys.Where(f => f.ReferencedSchema == table.Schema && f.ReferencedTable == table.Name && which(f)))
                {
                    if (other.Key == table.Key && i.Kind == DKind.DropTable) continue;   // a self-reference goes with the table
                    i.Deps.Add(new DDep($"foreign key {f.Name} on {other.FullName} references it", TableItem(other) ?? Find(DKind.DropForeignKey, other, f.Name)));
                }
        }
        static HashSet<string> Columns(IndexInfo x) => x.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var i in items.ToList())
        {
            switch (i.Kind)
            {
                case DKind.DropModule or DKind.DropSequence or DKind.DropType or DKind.DropTable:
                    AddDependents(i);
                    if (i.Kind == DKind.DropTable) AddFkRefs(i, i.Table, _ => true);
                    break;
                case DKind.DropUnique:
                {
                    var key = Columns(i.Table.Indexes.First(x => IsKey(x) && x.Name == i.Sub));
                    AddFkRefs(i, i.Table, f => f.ReferencedColumns.ToHashSet(StringComparer.Ordinal).SetEquals(key));
                    break;
                }
                case DKind.DropIndex:
                {
                    var x = i.Table.Indexes.First(x => !IsKey(x) && x.Name == i.Sub);
                    if (x.IsUnique && x.FilterDefinition is null)
                        AddFkRefs(i, i.Table, f => f.ReferencedColumns.ToHashSet(StringComparer.Ordinal).SetEquals(Columns(x)));
                    break;
                }
                case DKind.DropColumn:
                    ColumnDependents(i);
                    break;
            }
        }

        // PostgreSQL drops a table's own keys, checks and indexes on a dropped column without asking: each must go by a file of its own first
        void ColumnDependents(DItem i)
        {
            var k = i.Table;
            var col = i.Sub!;
            bool Uses(string? text) => PgDdl.MentionsColumn(text, col);
            foreach (var ck in k.Constraints.Where(c => c.Kind == ConstraintKind.Check && Uses(c.Definition)))
                i.Deps.Add(new DDep($"check {ck.Name} uses it", FindCheck(k, ck.Name)));
            foreach (var x in k.Indexes)
            {
                if (!(x.KeyColumns.Any(c => c.Name == col) || x.IncludedColumns.Contains(col) || Uses(x.Definition))) continue;
                if (x.IsPrimaryKey) i.Deps.Add(new DDep($"primary key {x.Name} is built on it", null));
                else if (IsKey(x)) i.Deps.Add(new DDep($"{(x.IsUniqueConstraint ? "unique key" : "exclusion constraint")} {x.Name} is built on it", Find(DKind.DropUnique, k, x.Name)));
                else i.Deps.Add(new DDep($"index {x.Name} is built on it", Find(DKind.DropIndex, k, x.Name)));
            }
            foreach (var f in k.ForeignKeys.Where(f => f.Columns.Contains(col)))
                i.Deps.Add(new DDep($"foreign key {f.Name} is built on it", Find(DKind.DropForeignKey, k, f.Name)));
            AddFkRefs(i, k, f => f.ReferencedColumns.Contains(col));
            foreach (var c in k.Columns.Where(c => c.IsComputed && c.Name != col && Uses(c.ComputedDefinition)))
                i.Deps.Add(new DDep($"generated column {c.Name} reads it", FindColumn(k, c.Name)));
            foreach (var (key, policy) in k.Properties.Where(p => p.Key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal)))
                if (Uses(policy)) i.Deps.Add(new DDep($"row level security policy {key[PgCanonicalWriter.PolicyPrefix.Length..]} reads it", null));
            foreach (var (by, d) in needing.GetValueOrDefault(k.Key) ?? [])
            {
                if (d.Column != col || d.Via == "foreignkey") continue;
                if (by.Key == k.Key)
                {
                    // its own generated columns and checks are above; a rule on the table stays
                    if (d.Via is "view" or "policy") i.Deps.Add(new DDep(d.Via == "view" ? $"a rule on {k.FullName} reads it" : "a row level security policy reads it", null));
                    continue;
                }
                i.Deps.Add(new DDep($"{Kind(by)} {Display(by)} reads it", objects.GetValueOrDefault(by.Key)));
            }
        }

        // ---- column changes on tables both sides have, planned last: a dependent another file drops is waited for
        foreach (var (src, tgt) in common)
            foreach (var c in src.Columns.OrderBy(c => c.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var tc = tgt.Columns.FirstOrDefault(x => x.Name == c.Name);
                if (tc is null || c.IsComputed || tc.IsComputed) continue;
                var narrowing = c.DataType != tc.DataType && Widening(tc.DataType, c.DataType) is null;
                var tighten = !c.IsNullable && tc.IsNullable;
                if (!narrowing && !tighten) continue;
                var i = Part(DKind.AlterColumn, "column", tgt, c.Name, "alter_column");
                await PlanAlterAsync(i, tgt, c, tc, narrowing, tighten, objects);
            }

        // ---- hold everything whose dependents stay; then order, holding any dependency cycle that is not code
        List<DItem> kept;
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
            kept = [.. items.Where(i => i.Held is null)
                .OrderBy(i => i.Kind)
                .ThenBy(i => i.Table.Schema, StringComparer.Ordinal).ThenBy(i => i.Table.Name, StringComparer.Ordinal)
                .ThenBy(i => i.Table.Signature ?? "", StringComparer.Ordinal)
                .ThenBy(i => i.Sub ?? "", StringComparer.Ordinal)];
            var index = kept.Select((i, n) => (i, n)).ToDictionary(x => x.i, x => x.n);
            var edges = kept.SelectMany(i => i.Deps.Where(d => d.By is not null && index.ContainsKey(d.By)).Select(d => (index[d.By!], index[i]))).ToList();
            var forced = new List<int>();
            var order = PlanOrder.StableOrder(kept.Count, edges, forced);
            var stuck = forced.Select(n => kept[n]).Where(i => i.Kind != DKind.DropModule).ToList();
            if (stuck.Count == 0)
            {
                kept = [.. order.Select(n => kept[n])];
                break;
            }
            foreach (var i in stuck) i.Held = "in a dependency cycle (for example tables whose foreign keys reference each other) - drop it by hand";
        }

        // ---- holds, scripted-with-a-note rows
        foreach (var i in items.Where(i => i.Held is not null))
            review.Hold(ReviewLog.NotAppliedDestructive, i.Word, i.IsObject ? i.Name : i.Sub ?? i.Name, i.IsObject ? null : i.Table.FullName, i.Held!, folder);
        foreach (var i in kept)
            foreach (var (kind, name, reason) in i.Notes)
                review.Note(ReviewLog.NotAppliedDestructive, kind, name, i.Table.FullName, reason, folder);
        NoteReaders(kept);

        var heldCount = items.Count(i => i.Held is not null);
        if (kept.Count == 0)
        {
            Destructive.Notes.Add(heldCount == 0
                ? $"{tgtLabel} has nothing that only it holds, and no column change that could lose data."
                : $"{heldCount} change(s) held back - see 99_review\\{ReviewLog.NotAppliedDestructive}.");
            return;
        }

        // ---- what goes with each drop: the rows of a table, the values of a column; a sequence its table or column takes with it
        foreach (var i in kept)
        {
            ct.ThrowIfCancellationRequested();
            if (i.Kind == DKind.DropTable)
            {
                var probe = await probes.RowCountAsync(PgDdl.Q(i.Table), ct);
                i.Header.Insert(0, probe.Checked
                    ? $"Drops table {i.Table.FullName} and its {N(probe.Count!.Value)} row(s) (count(*) when this script was generated): {srcLabel} does not have it."
                    : $"Drops table {i.Table.FullName} and all its rows: {srcLabel} does not have it. Rows not counted ({probe.NotCheckedReason}); " +
                      $"pg_class says about {(await probes.RowEstimateAsync(i.Table.Schema, i.Table.Name, ct) is { } about ? N(about) : "an unknown number")}.");
            }
            else if (i.Kind == DKind.DropColumn)
            {
                var probe = await probes.ValuesAsync(PgDdl.Q(i.Table), i.Sub!, ct);
                i.Header.Add(probe.Checked
                    ? $"{N(probe.Count!.Value)} row(s) hold a value in it (counted when this script was generated)."
                    : $"Its values were not counted ({probe.NotCheckedReason}).");
            }
            else if (i.Kind == DKind.DropSequence && i.Table.Properties.GetValueOrDefault("ownedBy") is { Length: > 0 } owner &&
                     kept.Any(k => (k.Kind == DKind.DropTable && owner.StartsWith($"{PgDdl.Ident(k.Table.Schema)}.{PgDdl.Ident(k.Table.Name)}.", StringComparison.Ordinal)) ||
                                   (k.Kind == DKind.DropColumn && owner == $"{PgDdl.Ident(k.Table.Schema)}.{PgDdl.Ident(k.Table.Name)}.{PgDdl.Ident(k.Sub!)}")))
            {
                // OWNED BY: the table or column an earlier file drops takes it along (tables and columns go before sequences)
                i.Statements[0] = $"DROP SEQUENCE IF EXISTS {PgDdl.Q(i.Table)} RESTRICT";
                i.Header.Add($"It belongs to {owner} (OWNED BY), which an earlier file drops and takes it with; this file then finds it gone and does nothing.");
            }
        }

        // ---- the files
        foreach (var i in kept)
        {
            var body = Body(i);
            string? rollback = null, rollbackModule = null;
            if (i.Kind == DKind.DropModule)
            {
                var m = i.Table;
                rollbackModule = m.Definition!.TrimEnd();
                rollback = $"-- {folder} rollback: recreates {Kind(m)} {PgDdl.OneLine(Display(m))} as {PgDdl.OneLine(tgtLabel)} had it when this script was generated.\n" +
                           "-- The target's own pg_get_*def text; not compile-checked by SchemaScope.\n" +
                           (IsRoutine(m) ? BodiesOff : "") + PgDdl.Statement(rollbackModule);
            }
            Destructive.Files.Add(new PgFile(i.FileWord, body, i.Kind == DKind.DropModule && i.Table.Type == DbObjectType.Trigger
                ? new ObjectName(i.Table.Schema, i.Table.ParentName ?? "") : new ObjectName(i.Table.Schema, i.Table.Name), Rollback: rollback, RollbackModule: rollbackModule));
        }

        var kinds = kept.GroupBy(i => i.Kind switch
            {
                DKind.AlterColumn => "column change",
                DKind.ReplaceCheck => "replaced check",
                DKind.DropColumn => "dropped column",
                DKind.DropDefault => "dropped default",
                _ => "dropped " + i.Word,
            })
            .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}");
        Destructive.Notes.Add($"{string.Join(", ", kinds)}.");
        Destructive.Notes.Add($"CAN LOSE DATA: never run by RUN.ps1. Back up {tgtLabel} first; a dropped table or column cannot be rolled back " +
                              "(rollback\\ only recreates dropped views, materialized views, functions, procedures, aggregates and triggers).");
        Destructive.Notes.Add("Every DROP is RESTRICT: anything else that depends on what it drops makes that file fail and change nothing.");
        if (heldCount > 0) Destructive.Notes.Add($"{heldCount} held back (99_review\\{ReviewLog.NotAppliedDestructive}).");
        review.Warn($"11_destructive: {kept.Count} file(s) drop or change things in {tgtLabel} that can lose data - read the README before running any of them.");
    }

    /// <summary>
    /// Plans one column change to the source's type (keeping the target column's collation) and NOT NULL, or holds it:
    /// identity columns, the partition key, a type change a read-only check cannot prove safe, a view, rule, policy,
    /// trigger or generated column that reads the column and stays (PostgreSQL refuses the type change while one does),
    /// and any row the probe finds - or a probe that could not run. A widening is 04's; with NOT NULL as well, only the
    /// NOT NULL is made here.
    /// </summary>
    private async Task PlanAlterAsync(DItem i, DbObject tgt, ColumnInfo c, ColumnInfo tc, bool narrowing, bool tighten, Dictionary<string, DItem> objects)
    {
        var srcLabel = req.Source.Label;
        var tgtLabel = req.Target.Label;
        var q = PgDdl.Q(tgt);
        var col = PgDdl.Q(c.Name);
        var from = $"{tc.DataType} {(tc.IsNullable ? "NULL" : "NOT NULL")}";
        var to = $"{(narrowing ? c.DataType : tc.DataType)} {(tighten || !tc.IsNullable ? "NOT NULL" : "NULL")}";
        i.Header.Add($"Changes column {c.Name} of {tgt.FullName} from {from} to {to}, as in {srcLabel}.");
        if (!narrowing && c.DataType != tc.DataType)
            i.Header.Add($"Its type change to {c.DataType} is a widening, made by 04_columntypes; this file only makes it NOT NULL.");
        if (i.Held is not null) return;
        if (c.IsIdentity || tc.IsIdentity) { i.Held = "an identity column - change it by hand"; return; }

        string? fit = null;
        if (narrowing)
        {
            if (tgt.Properties.GetValueOrDefault("partitionBy") is { } key && PgDdl.MentionsColumn(key, c.Name))
            {
                i.Held = "it is part of the table's partition key, whose type PostgreSQL cannot change";
                return;
            }
            fit = FitPredicate(tc.DataType, c.DataType, col);
            if (fit is null)
            {
                i.Held = $"from {from} to {to}: a read-only check cannot prove the values survive this type change - change it by hand";
                return;
            }
            // PostgreSQL refuses to change a column's type while a view, rule, generated column, policy or trigger reads it;
            // one an earlier file of this folder drops is waited for
            var stays = new List<string>();
            foreach (var (by, what) in Readers(tgt, c.Name))
            {
                if (by.Key != tgt.Key && objects.TryGetValue(by.Key, out var item)) i.Deps.Add(new DDep($"{what} reads {c.Name}", item));
                else if (!stays.Contains(what)) stays.Add(what);
            }
            if (stays.Count > 0)
            {
                i.Held = $"{string.Join(", ", stays)} {(stays.Count == 1 ? "reads" : "read")} {c.Name} on the target, and PostgreSQL cannot change its type " +
                         $"while {(stays.Count == 1 ? "it does" : "they do")} - drop and recreate {(stays.Count == 1 ? "it" : "them")} around the change by hand";
                return;
            }
        }

        var misfit = string.Join(" OR ", new[] { fit is null ? null : $"({fit})", tighten ? $"{col} IS NULL" : null }.OfType<string>());
        var probe = await probes.ColumnMisfitsAsync(q, c.Name, misfit, ct);
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
        if (fit is not null)
        {
            // PostgreSQL rounds some values without an error (numeric scale, time precision): the same check runs again inside
            // the file, with the table locked, and stops it - changing nothing - if a row written since no longer fits
            i.Header.Add("Checked again when this file runs: it stops, changing nothing, if a row no longer fits.");
            i.Statements.Add($"LOCK TABLE {q} IN ACCESS EXCLUSIVE MODE");
            var message = PgDdl.Lit($"Rows of {tgt.FullName}.{c.Name} no longer fit {c.DataType}; nothing was changed. Generate the scripts again.");
            var check = $"BEGIN IF EXISTS (SELECT 1 FROM {q} WHERE {fit}) THEN RAISE EXCEPTION USING MESSAGE = {message}; END IF; END";
            var tag = "$schemascope$";
            while (check.Contains(tag, StringComparison.Ordinal)) tag = tag.Insert(tag.Length - 1, "_");
            i.Statements.Add($"DO {tag} {check} {tag}");
            var collate = !string.IsNullOrEmpty(tc.Collation) && ParseType(c.DataType).Base is "character varying" or "text" ? $" COLLATE {tc.Collation}" : "";
            i.Statements.Add($"ALTER TABLE {q} ALTER COLUMN {col} TYPE {c.DataType}{collate}");
            if (await probes.RowEstimateAsync(tgt.Schema, tgt.Name, ct) is { } rows && rows >= Options.LargeTableRows)
                i.Notes.Add(("column", c.Name, $"{tc.DataType} to {c.DataType} rewrites the whole table (about {N(rows)} rows) under an ACCESS EXCLUSIVE lock"));
        }
        if (tighten) i.Statements.Add($"ALTER TABLE {q} ALTER COLUMN {col} SET NOT NULL");
    }

    /// <summary>
    /// The rows of <paramref name="column"/> (already quoted) that a change from <paramref name="from"/> to
    /// <paramref name="to"/> would refuse or alter, as a predicate that only reads the row; null when no such predicate
    /// is known and the change is held. Only a narrowing inside one family: varchar or text to a shorter varchar, a
    /// smaller integer, numeric to fewer digits or a smaller scale, a time or timestamp to a lower precision. NULLs fit.
    /// </summary>
    internal static string? FitPredicate(string from, string to, string column)
    {
        var (fb, fa) = ParseType(from);
        var (tb, ta) = ParseType(to);
        var ints = new Dictionary<string, (string Min, string Max)>(StringComparer.Ordinal)
        {
            ["smallint"] = ("-32768", "32767"),
            ["integer"] = ("-2147483648", "2147483647"),
            ["bigint"] = ("-9223372036854775808", "9223372036854775807"),
        };
        string[] intOrder = ["smallint", "integer", "bigint"];
        if (ints.TryGetValue(tb, out var range) && ints.ContainsKey(fb) && fa.Length == 0 && ta.Length == 0 &&
            Array.IndexOf(intOrder, tb) < Array.IndexOf(intOrder, fb))
            return $"{column} NOT BETWEEN {range.Min} AND {range.Max}";
        if (tb == "character varying" && ta.Length == 1 && fb is "character varying" or "text")
            return $"char_length({column}) > {ta[0]}";
        if (tb == "numeric" && ta.Length >= 1 && fb == "numeric")
        {
            var scale = ta.Length > 1 ? ta[1] : 0;
            // NaN and infinity never fit a numeric with a precision
            return $"{column} <> round({column}, {scale}) OR abs({column}) >= power(10::numeric, {ta[0] - scale})";
        }
        if (tb == fb && ta.Length == 1 && tb is "timestamp without time zone" or "timestamp with time zone" or "time without time zone" or "time with time zone")
            return $"{column} IS DISTINCT FROM CAST({column} AS {to})";
        return null;
    }

    /// <summary>The DROP for a code object only the target has, or null when it cannot be named from what was read.</summary>
    private static string? DropModule(DbObject o) => o.Type switch
    {
        DbObjectType.View => $"DROP VIEW {PgDdl.Q(o)} RESTRICT",
        DbObjectType.MaterializedView => $"DROP MATERIALIZED VIEW {PgDdl.Q(o)} RESTRICT",
        DbObjectType.StoredProcedure when o.Signature is { } sig => $"DROP PROCEDURE {PgDdl.Q(o)}({sig}) RESTRICT",
        DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction when o.Signature is { } sig => $"DROP FUNCTION {PgDdl.Q(o)}({sig}) RESTRICT",
        // an ordered-set aggregate's argument list needs its ORDER BY, which the overload signature does not keep
        DbObjectType.AggregateFunction when o.Signature is { } sig && !(o.Properties.GetValueOrDefault("arguments") ?? "").Contains("ORDER BY", StringComparison.Ordinal) =>
            $"DROP AGGREGATE {PgDdl.Q(o)}({(sig.Length == 0 ? "*" : sig)}) RESTRICT",
        DbObjectType.Trigger when o.ParentName is { } table => $"DROP TRIGGER {PgDdl.Q(o.Name)} ON {PgDdl.Q(o.Schema, table)} RESTRICT",
        _ => null,
    };

    /// <summary>One file: its header as -- comments, then its statements; the file is one transaction (Apply and psql --single-transaction).</summary>
    private string Body(DItem i)
    {
        var sb = new StringBuilder($"-- {Destructive.Name}: CAN LOSE DATA. Back up the database before running this.\n");
        foreach (var h in i.Header) sb.Append("-- ").Append(CommentLine(h)).Append('\n');
        foreach (var statement in i.Statements) sb.Append(PgDdl.Statement(statement));
        return sb.ToString();
    }

    /// <summary>A name or definition inside a -- comment: line breaks and other control characters become '?', so nothing escapes the comment.</summary>
    private static string CommentLine(string s) => string.Concat((s ?? "").Select(ch => char.IsControl(ch) || char.IsSurrogate(ch) ? '?' : ch));

    /// <summary>
    /// Code that stays on the target and names something this folder drops without pg_depend knowing (a PL/pgSQL or
    /// quoted-SQL body): the drop goes through, but that code fails when it runs. Listed as a note to check.
    /// </summary>
    private void NoteReaders(List<DItem> kept)
    {
        var dropped = kept.Where(i => i.IsObject).Select(i => i.Table.Key).ToHashSet(StringComparer.Ordinal);
        var gone = kept.Where(i => i.IsObject && i.Table.Type != DbObjectType.Trigger).ToList();
        var columns = kept.Where(i => i.Kind == DKind.DropColumn).ToList();
        if (gone.Count == 0 && columns.Count == 0) return;
        foreach (var m in Tgt.Objects.Where(o => o.Type.IsModule() && !dropped.Contains(o.Key) && !string.IsNullOrEmpty(o.Definition)))
        {
            var known = (m.DependsOn ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var g in gone.Where(g => !known.Contains(g.Table.Key) && PgDdl.MentionsColumn(m.Definition, g.Table.Name)))
                review.Note(ReviewLog.NotAppliedDestructive, g.Word, g.Name, null,
                    $"dropped by 11_destructive, but {Kind(m)} {Display(m)} names {g.Table.Name} - it fails when it runs if it uses it", Destructive.Name);
            foreach (var c in columns.Where(c => PgDdl.MentionsColumn(m.Definition, c.Table.Name) && PgDdl.MentionsColumn(m.Definition, c.Sub!)))
                review.Note(ReviewLog.NotAppliedDestructive, "column", c.Sub!, c.Table.FullName,
                    $"dropped by 11_destructive, but {Kind(m)} {Display(m)} names {c.Table.Name} and {c.Sub} - it fails when it runs if it reads this column", Destructive.Name);
        }
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}
