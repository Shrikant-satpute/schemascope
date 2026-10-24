using System.Text;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting.Planning;

namespace SchemaScope.Scripting.PostgreSql;

internal sealed partial class PgScriptRun
{
    // ------------------------------------------------------------------ 03_missing

    /// <summary>The kinds 03 creates, in the order they go when nothing ties them (pg_depend edges move them as needed).</summary>
    private static int Rank(DbObject o) => o.Type switch
    {
        DbObjectType.Schema => 0,
        DbObjectType.Collation => 1,
        DbObjectType.EnumType => 2,
        DbObjectType.RangeType => 3,
        DbObjectType.Domain => 4,
        DbObjectType.CompositeType => 5,
        DbObjectType.Sequence => 6,
        DbObjectType.Table => 7,
        DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction or DbObjectType.StoredProcedure => 8,
        DbObjectType.Operator => 9,
        DbObjectType.AggregateFunction => 10,
        DbObjectType.View => 11,
        DbObjectType.MaterializedView => 12,
        _ => -1
    };

    private static bool IsRoutine(DbObject o) =>
        o.Type is DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction or DbObjectType.StoredProcedure or DbObjectType.AggregateFunction;

    private static bool IsTriggerFunction(DbObject o) =>
        o.Type == DbObjectType.ScalarFunction && o.Properties.GetValueOrDefault("result") is "trigger" or "event_trigger";

    private const string NotCompileChecked = "-- Copied from the source's pg_get_*def text; not compile-checked by SchemaScope.\n";

    /// <summary>
    /// A routine is created with check_function_bodies off for its file's transaction, as pg_dump does: a SQL-language
    /// body may read a table or view that comes later in the folder, and pg_depend does not record what such a body reads.
    /// </summary>
    private const string BodiesOff = "SET LOCAL check_function_bodies = false;\n";

    /// <summary>
    /// 03 and 07 create, never replace: a leading CREATE OR REPLACE FUNCTION, PROCEDURE or VIEW (pg_get_functiondef's,
    /// and the reader's view text) becomes plain CREATE, so the file fails in its own transaction when the target has
    /// gained that object since the compare, and the target's own code stays. <c>Module</c> is the source's text after
    /// the CREATE it starts with, which the file holds exactly.
    /// </summary>
    internal static (string Text, string Module) CreateOnly(string definition)
    {
        const string replace = "CREATE OR REPLACE ";
        foreach (var kind in new[] { "FUNCTION ", "PROCEDURE ", "VIEW " })
            if (definition.StartsWith(replace + kind, StringComparison.Ordinal))
                return ("CREATE " + definition[replace.Length..], definition[replace.Length..]);
        return (definition, definition);
    }

    private IEnumerable<DbObject> MissingTriggers() =>
        Src.Objects.Where(o => o.Type == DbObjectType.Trigger && StatusOf(o) == Diff.Missing && InScope(o) && !IsExcluded(o));

    /// <summary>
    /// What of <paramref name="o"/>'s own text runs when 03 creates it, for <see cref="NotReady"/>: a table's column
    /// types, defaults, generated expressions, checks and keys (its indexes, foreign keys and rules come later); any other
    /// object's definition.
    /// </summary>
    private static string? CreatedText(DbObject o) => o.Type == DbObjectType.Table
        ? string.Join("\n", o.Columns.Select(c => $"{c.DataType} {c.ComputedDefinition} {c.Collation}")
            .Concat(o.Constraints.Select(c => c.Definition)).Concat(o.Indexes.Where(IsKey).Select(i => i.Definition)))
        : o.Type.IsModule() ? o.Definition : o.CompareText;

    private async Task PlanMissingAsync()
    {
        var quarantine = QuarantineNames();
        var usedByNewTriggers = Selected(ScriptSection.Triggers)
            ? MissingTriggers().SelectMany(tr => tr.DependsOn ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal)
            : [];
        var targetMajor = Tgt.MajorVersion;

        var set = new Dictionary<string, DbObject>(StringComparer.Ordinal);
        foreach (var o in Src.Objects.Where(o => StatusOf(o) == Diff.Missing && InScope(o)))
        {
            void Hold(string file, string why)
            {
                notCreated[o.Key] = why;
                review.Hold(file, Kind(o), Display(o), null, why, Missing.Name);
            }

            if (o.Type == DbObjectType.Extension)
            {
                var schema = o.Properties.GetValueOrDefault("schema") ?? "public";
                Hold(ReviewLog.NotApplied,
                    $"extensions are noted only: install it on the target as a user allowed to - CREATE EXTENSION {PgDdl.Q(o.Name)} WITH SCHEMA " +
                    $"{PgDdl.Q(schema)} VERSION {PgDdl.Lit(o.Properties.GetValueOrDefault("version") ?? "")}; (its files must be on the server)");
                continue;
            }
            if (Rank(o) < 0) continue;   // triggers: 07
            if (IsExcluded(o)) { Hold(ReviewLog.QuarantinedNotApplied, "it matches an exclude pattern"); continue; }
            if (quarantine.Contains(o.Name.ToLowerInvariant())) { Hold(ReviewLog.QuarantinedNotApplied, "a .sql file of this name is in the quarantine folder"); continue; }
            if (o.Type.IsModule() && (o.IsEncrypted || o.DefinitionUnavailable || string.IsNullOrWhiteSpace(o.Definition)))
            {
                Hold(ReviewLog.NotApplied, "its text could not be read from the source");
                continue;
            }
            if (o.Type == DbObjectType.RangeType && o.Properties.ContainsKey("canonical"))
            {
                Hold(ReviewLog.NotApplied, "its CANONICAL function takes the range type itself, so the type has to be made as a shell first; create it by hand");
                continue;
            }
            if (o.Type == DbObjectType.RangeType && o.Properties.ContainsKey("multirange") && targetMajor is > 0 and < 14)
            {
                Hold(ReviewLog.NotApplied, $"it names its multirange type, which PostgreSQL {targetMajor} does not have (14 and later)");
                continue;
            }
            if (o.Type == DbObjectType.Collation && o.Properties.ContainsKey("rules") && targetMajor is > 0 and < 16)
            {
                Hold(ReviewLog.NotApplied, $"it has ICU rules, which PostgreSQL {targetMajor} does not have (16 and later)");
                continue;
            }
            if (o.Type == DbObjectType.Collation && o.Properties.GetValueOrDefault("provider") == "builtin" && targetMajor is > 0 and < 17)
            {
                Hold(ReviewLog.NotApplied, $"it uses the builtin provider, which PostgreSQL {targetMajor} does not have (17 and later)");
                continue;
            }
            if (IsTriggerFunction(o) && usedByNewTriggers.Contains(o.Key))
            {
                withTrigger.Add(o.Key);   // 07 creates it with its trigger
                continue;
            }
            set[o.Key] = o;
        }

        // pg_depend edges 03 does not wait for: a foreign key (05, once every table exists), a table's own rule (07) and
        // a table's plain index (06); the index behind a key is the table's own and is waited for
        static bool Later(DbObject o, DbDependency d) =>
            d.Via is "foreignkey" or "rule" || (d.Via == "index" && o.Type == DbObjectType.Table);

        // what each object needs must be on the target, or created before it here
        string? Blocker(DbObject o)
        {
            foreach (var d in o.DependsOn ?? [])
            {
                if (Later(o, d) || d.Key == o.Key) continue;
                if (t.TryGetValue(d.Key, out var have))
                {
                    if (d.Column is { } col && have.Type == DbObjectType.Table && !HasColumn(d.Key, col))
                        return $"it needs column {have.FullName}.{col}, which the target does not have and 01 does not add";
                    if (have.Type == DbObjectType.Extension && ExtensionElsewhere(have) is { } elsewhere) return elsewhere;
                    continue;
                }
                if (set.ContainsKey(d.Key) || withTrigger.Contains(d.Key)) continue;
                if (s.TryGetValue(d.Key, out var need))
                    return need.Type == DbObjectType.Extension
                        ? $"it needs extension {need.Name}, which the target does not have - install it first"
                        : $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
            }
            return NotReady(CreatedText(o));
        }
        bool HoldIfBlocked(DbObject o)
        {
            if (!set.ContainsKey(o.Key) || Blocker(o) is not { } why) return false;
            set.Remove(o.Key);
            notCreated[o.Key] = why;
            review.Hold(ReviewLog.NotApplied, Kind(o), Display(o), null, why, Missing.Name);
            return true;
        }

        List<DbObject> items;
        while (true)
        {
            PlanOrder.HoldUntilStable(set.Values.ToList(), HoldIfBlocked);
            items = [.. set.Values.OrderBy(Rank).ThenBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                .ThenBy(o => o.Signature ?? "", StringComparer.Ordinal)];
            var at = items.Select((o, i) => (o.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
            var edges = new List<(int, int)>();
            for (var i = 0; i < items.Count; i++)
                foreach (var d in items[i].DependsOn ?? [])
                    if (!Later(items[i], d) && at.TryGetValue(d.Key, out var j) && j != i) edges.Add((j, i));
            var forced = new List<int>();
            var order = PlanOrder.StableOrder(items.Count, edges, forced);
            if (forced.Count == 0)
            {
                items = [.. order.Select(i => items[i])];
                break;
            }
            // no order creates a cycle's members one by one: hold them, and so whatever needs them
            var cycle = string.Join(", ", forced.Select(i => Display(items[i])));
            foreach (var i in forced)
            {
                set.Remove(items[i].Key);
                notCreated[items[i].Key] = $"it is part of a dependency cycle ({cycle})";
                review.Hold(ReviewLog.NotApplied, Kind(items[i]), Display(items[i]), null, notCreated[items[i].Key], Missing.Name);
            }
        }
        foreach (var o in items)
        {
            created.Add(o.Key);
            Take(o, created: true);
        }

        // a schema keeps its owner, and a new table its policies, when the roles they name exist on the target
        var owners = items.Where(o => o.Type == DbObjectType.Schema).Select(o => o.Properties.GetValueOrDefault("owner")).OfType<string>();
        var wanted = owners.Concat(RolesOf(items.Where(o => o.Type == DbObjectType.Table))).Distinct(StringComparer.Ordinal).ToList();
        var roles = wanted.Count > 0 ? await probes.ExistingRolesAsync(wanted, ct) : [];

        var ownedBy = OwnedByColumns(items);
        foreach (var o in items)
        {
            var text = new StringBuilder(Header(Missing, Kind(o), Display(o)));
            string? module = null;
            switch (o.Type)
            {
                case DbObjectType.Schema:
                    var owner = o.Properties.GetValueOrDefault("owner");
                    if (owner is not null && roles is not null && roles.Contains(owner)) text.Append($"CREATE SCHEMA {PgDdl.Q(o.Name)} AUTHORIZATION {PgDdl.Q(owner)};\n");
                    else
                    {
                        text.Append($"CREATE SCHEMA {PgDdl.Q(o.Name)};\n");
                        if (owner is not null)
                            review.Note(ReviewLog.NotApplied, "schema", o.Name, null,
                                $"its owner {owner} {(roles is null ? "could not be looked up" : "does not exist")} on the target, so whoever runs the script owns it",
                                Missing.Name);
                    }
                    break;
                case DbObjectType.Collation:
                    text.Append(PgCanonicalWriter.WriteCollation(o)).Append('\n');
                    break;
                case DbObjectType.EnumType:
                    text.Append($"CREATE TYPE {PgDdl.Q(o)} AS ENUM ({o.Properties.GetValueOrDefault("labels")});\n");
                    break;
                case DbObjectType.RangeType:
                    text.Append(PgCanonicalWriter.WriteRange(o)).Append('\n');
                    break;
                case DbObjectType.Operator:
                    text.Append(PgCanonicalWriter.WriteOperator(o)).Append('\n');
                    break;
                case DbObjectType.Domain:
                    text.Append(Domain(o));
                    break;
                case DbObjectType.CompositeType:
                    text.Append($"CREATE TYPE {PgDdl.Q(o)} AS (\n")
                        .Append(string.Join(",\n", o.Columns.OrderBy(c => c.Ordinal).Select(c =>
                            $"    {PgDdl.Q(c.Name)} {c.DataType}{(string.IsNullOrEmpty(c.Collation) ? "" : " COLLATE " + c.Collation)}")))
                        .Append("\n);\n");
                    break;
                case DbObjectType.Sequence:
                    text.Append(Sequence(o));
                    if (o.Properties.GetValueOrDefault("ownedBy") is { Length: > 0 } owned)
                    {
                        if (ownedBy.TryGetValue(owned, out var column))
                        {
                            if (!created.Contains(column.Table.Key))
                                text.Append($"ALTER SEQUENCE {PgDdl.Q(o)} OWNED BY {PgDdl.Q(column.Table)}.{PgDdl.Q(column.Column)};\n");
                        }
                        else
                            review.Hold(ReviewLog.NotApplied, "sequence owner", Display(o), null,
                                $"OWNED BY {owned} is left out: that column is not there when 03 runs. Run the scripts, then create them again: " +
                                "05_constraints sets it once the column is there", Missing.Name);
                    }
                    break;
                case DbObjectType.Table:
                    text.Append(Table(o, items.Where(q => q.Type == DbObjectType.Sequence &&
                                                          ownedBy.TryGetValue(q.Properties.GetValueOrDefault("ownedBy") ?? "", out var c) && c.Table.Key == o.Key)
                        .Select(q => (q, ownedBy[q.Properties["ownedBy"]!].Column)), roles));
                    break;
                default:   // views, materialized views, functions, procedures, aggregates
                    (var create, module) = CreateOnly(o.Definition!.TrimEnd());
                    text.Append(NotCompileChecked);
                    if (IsRoutine(o)) text.Append(BodiesOff);
                    text.Append(PgDdl.Statement(create));
                    break;
            }
            Missing.Files.Add(new PgFile($"{Kind(o).Replace(' ', '_')}_{Display(o)}", text.ToString(), NameOf(o), module));
        }
    }

    /// <summary>The ownedBy spelling (schema.table.column, quote_ident style) of every column a sequence created here may belong to.</summary>
    private Dictionary<string, (DbObject Table, string Column)> OwnedByColumns(List<DbObject> items)
    {
        var map = new Dictionary<string, (DbObject, string)>(StringComparer.Ordinal);
        foreach (var table in items.Where(o => o.Type == DbObjectType.Table).Concat(Tgt.Objects.Where(o => o.Type == DbObjectType.Table)))
        {
            var columns = table.Columns.Select(c => c.Name)
                .Concat(created.Contains(table.Key) ? [] : added.GetValueOrDefault(table.Key) ?? []);
            foreach (var c in columns)
                map.TryAdd($"{PgDdl.Ident(table.Schema)}.{PgDdl.Ident(table.Name)}.{PgDdl.Ident(c)}", (table, c));
        }
        return map;
    }

    private static string Sequence(DbObject o)
    {
        var p = o.Properties;
        return $"CREATE SEQUENCE {PgDdl.Q(o)} AS {p.GetValueOrDefault("type")} START WITH {p.GetValueOrDefault("startValue")} " +
               $"INCREMENT BY {p.GetValueOrDefault("increment")} MINVALUE {p.GetValueOrDefault("minValue")} MAXVALUE {p.GetValueOrDefault("maxValue")} " +
               $"CACHE {p.GetValueOrDefault("cacheSize")}{(p.GetValueOrDefault("isCycling") == "1" ? " CYCLE" : " NO CYCLE")};\n";
    }

    private static string Domain(DbObject o)
    {
        var p = o.Properties;
        var sb = new StringBuilder($"CREATE DOMAIN {PgDdl.Q(o)} AS {p.GetValueOrDefault("baseType")}");
        if (p.GetValueOrDefault("collation") is { Length: > 0 } collation) sb.Append($" COLLATE {collation}");
        if (p.GetValueOrDefault("default") is { Length: > 0 } def) sb.Append($" DEFAULT {def}");
        if (p.GetValueOrDefault("notNull") == "1") sb.Append(" NOT NULL");
        var checks = o.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        foreach (var c in checks.Where(c => !c.IsNotTrusted)) sb.Append($"\n    CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition}");
        sb.Append(";\n");
        foreach (var c in checks.Where(c => c.IsNotTrusted))
            sb.Append($"ALTER DOMAIN {PgDdl.Q(o)} ADD CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition} NOT VALID;\n");
        return sb.ToString();
    }

    /// <summary>
    /// CREATE TABLE with its columns, keys and validated checks inline (as the source names them), then what CREATE TABLE
    /// cannot say: NOT VALID checks, the sequences it owns, its policies and row level security. A partition is made
    /// PARTITION OF its parent with its bounds (PostgreSQL gives it the parent's columns, defaults, keys, checks and
    /// indexes) and what is its own - a NOT NULL, a default, a key, a check - in its list; a typed table OF its type; an
    /// inheritance child lists every column with INHERITS (PostgreSQL merges those it inherits). Foreign keys come in 05,
    /// indexes in 06, triggers and rules in 07. A policy that names a role the target lacks is held, and row level
    /// security is then not switched on, since with it on and the policy missing nobody but a superuser sees a row.
    /// </summary>
    private string Table(DbObject o, IEnumerable<(DbObject Sequence, string Column)> owned, HashSet<string>? roles)
    {
        var p = o.Properties;
        var sb = new StringBuilder();
        sb.Append(p.ContainsKey("persistence") ? "CREATE UNLOGGED TABLE " : "CREATE TABLE ").Append(PgDdl.Q(o));
        List<ConstraintInfo> checks = [];
        var dropDefaults = new List<string>();
        if (p.GetValueOrDefault("partitionOf") is { Length: > 0 } parent)
        {
            // PostgreSQL gives a partition its parent's columns, NOT NULLs, defaults, checks and keys; what is the
            // partition's own is written in its column and constraint list
            var parentTable = ParentOf(o);
            var local = Local(o, Src);
            var lines = new List<string>();
            foreach (var c in o.Columns.OrderBy(c => c.Ordinal))
            {
                var theirs = parentTable?.Columns.FirstOrDefault(x => x.Name == c.Name);
                var def = c.IsComputed ? null : DefaultOf(o, c.Name)?.Definition;
                var parentDef = parentTable is null ? null : DefaultOf(parentTable, c.Name)?.Definition;
                var options = new List<string>();
                if (!c.IsNullable && theirs is { IsNullable: true }) options.Add("NOT NULL");
                if (def is not null && def != parentDef) options.Add($"DEFAULT {def}");
                if (def is null && parentDef is not null && !c.IsComputed) dropDefaults.Add(c.Name);
                if (options.Count > 0) lines.Add($"    {PgDdl.Q(c.Name)} WITH OPTIONS {string.Join(" ", options)}");
            }
            lines.AddRange(LocalConstraints(local, out checks));
            sb.Append($" PARTITION OF {parent}").Append(lines.Count > 0 ? $" (\n{string.Join(",\n", lines)}\n)" : "").Append($" {p.GetValueOrDefault("partitionBound")}");
        }
        else if (p.GetValueOrDefault("ofType") is { Length: > 0 } ofType)
        {
            // a typed table's columns are its type's; its own are NOT NULL, a default, keys and checks
            var lines = new List<string>();
            foreach (var c in o.Columns.OrderBy(c => c.Ordinal))
            {
                var options = new List<string>();
                if (!c.IsNullable) options.Add("NOT NULL");
                if (DefaultOf(o, c.Name) is { } def) options.Add($"DEFAULT {def.Definition}");
                if (options.Count > 0) lines.Add($"    {PgDdl.Q(c.Name)} WITH OPTIONS {string.Join(" ", options)}");
            }
            lines.AddRange(LocalConstraints(o, out checks));
            sb.Append($" OF {ofType}").Append(lines.Count > 0 ? $" (\n{string.Join(",\n", lines)}\n)" : "");
        }
        else
        {
            var lines = o.Columns.OrderBy(c => c.Ordinal).Select(c => "    " + ColumnDefinition(o, c)).ToList();
            foreach (var k in o.Indexes.Where(IsKey).OrderBy(i => i.IsPrimaryKey ? 0 : i.IsUniqueConstraint ? 1 : 2).ThenBy(i => i.Name, StringComparer.Ordinal))
                lines.Add($"    CONSTRAINT {PgDdl.Q(k.Name)} {k.Definition}");
            checks = [.. o.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal)];
            foreach (var c in checks.Where(c => !c.IsNotTrusted)) lines.Add($"    CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition}");
            sb.Append(" (\n").Append(string.Join(",\n", lines)).Append("\n)");
            if (p.GetValueOrDefault("inherits") is { Length: > 0 } inherits) sb.Append($" INHERITS ({inherits})");
        }
        if (p.GetValueOrDefault("partitionBy") is { Length: > 0 } partitionBy) sb.Append($" PARTITION BY {partitionBy}");
        if (p.GetValueOrDefault("options") is { Length: > 0 } storage) sb.Append($" WITH ({storage})");
        sb.Append(";\n");
        foreach (var c in checks.Where(c => c.IsNotTrusted))
            sb.Append($"ALTER TABLE {PgDdl.Q(o)} ADD CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition} NOT VALID;\n");
        // a column whose parent has a default the partition has not
        foreach (var c in dropDefaults) sb.Append($"ALTER TABLE {PgDdl.Q(o)} ALTER COLUMN {PgDdl.Q(c)} DROP DEFAULT;\n");
        foreach (var c in o.Columns.Where(c => c.NotNullNotValid))
            review.Hold(ReviewLog.NotApplied, "column", c.Name, o.FullName,
                "its NOT NULL is NOT VALID on the source, so the table is created without it; add it by hand if you want it " +
                "(ALTER TABLE ... ADD CONSTRAINT ... NOT NULL ... NOT VALID)", Missing.Name);
        foreach (var (seq, column) in owned)
            sb.Append($"ALTER SEQUENCE {PgDdl.Q(seq)} OWNED BY {PgDdl.Q(o)}.{PgDdl.Q(column)};\n");

        var held = false;
        foreach (var (key, policy) in PoliciesOf(o))
        {
            if (PolicyBlocked(policy, roles) is { } why)
            {
                held = true;
                review.Hold(ReviewLog.NotApplied, "policy", key[PgCanonicalWriter.PolicyPrefix.Length..], o.FullName, why, Missing.Name);
            }
            else sb.Append(policy.TrimEnd()).Append('\n');
        }
        foreach (var (key, verb) in new[] { ("rowSecurity", "ENABLE"), ("forceRowSecurity", "FORCE") })
        {
            if (!p.ContainsKey(key)) continue;
            if (held) review.Hold(ReviewLog.NotApplied, "table option", key, o.FullName, RlsHeld, Missing.Name);
            else sb.Append($"ALTER TABLE {PgDdl.Q(o)} {verb} ROW LEVEL SECURITY;\n");
        }
        return sb.ToString();
    }

    /// <summary>A table's keys and validated checks as CONSTRAINT lines of CREATE TABLE; <paramref name="checks"/> gets every check (NOT VALID ones are added after).</summary>
    private static List<string> LocalConstraints(DbObject table, out List<ConstraintInfo> checks)
    {
        var lines = table.Indexes.Where(IsKey).OrderBy(i => i.IsPrimaryKey ? 0 : i.IsUniqueConstraint ? 1 : 2).ThenBy(i => i.Name, StringComparer.Ordinal)
            .Select(k => $"    CONSTRAINT {PgDdl.Q(k.Name)} {k.Definition}").ToList();
        checks = [.. table.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal)];
        lines.AddRange(checks.Where(c => !c.IsNotTrusted).Select(c => $"    CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition}"));
        return lines;
    }

    /// <summary>Lower-cased base names of the *.sql files under the quarantine folder.</summary>
    private HashSet<string> QuarantineNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(Options.QuarantineFolder)) return names;
        foreach (var f in Directory.EnumerateFiles(Options.QuarantineFolder, "*.sql", SearchOption.AllDirectories))
            names.Add(Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
        return names;
    }

    // ------------------------------------------------------------------ 07_triggers

    /// <summary>The key of what a trigger is on: its table, or the view an INSTEAD OF trigger is on.</summary>
    private string ParentKey(DbObject trg)
    {
        foreach (var type in new[] { DbObjectType.Table, DbObjectType.View, DbObjectType.MaterializedView })
        {
            var key = KeyOf(type, trg.Schema, trg.ParentName ?? "");
            if (s.ContainsKey(key) || t.ContainsKey(key)) return key;
        }
        return KeyOf(DbObjectType.Table, trg.Schema, trg.ParentName ?? "");
    }

    private void PlanTriggers()
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trg in MissingTriggers().OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.ParentName, StringComparer.Ordinal)
                     .ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            var parentKey = ParentKey(trg);
            var onTable = parentKey == KeyOf(DbObjectType.Table, trg.Schema, trg.ParentName ?? "");
            string? why = null;
            DbObject? function = null;
            if (!t.ContainsKey(parentKey) && !created.Contains(parentKey))
                why = $"its {(onTable ? "table" : "view")} {trg.Schema}.{trg.ParentName} is not on the target and this run does not create it";
            if (string.IsNullOrWhiteSpace(trg.Definition)) why ??= "its text could not be read from the source";
            foreach (var d in trg.DependsOn ?? [])
            {
                if (why is not null) break;
                if (d.Key == parentKey)
                {
                    if (onTable && d.Column is { } col && !HasColumn(parentKey, col)) why = $"it names column {col}, which the target will not have";
                    continue;
                }
                if (t.TryGetValue(d.Key, out var have))
                {
                    if (have.Type == DbObjectType.Extension && ExtensionElsewhere(have) is { } elsewhere) why = elsewhere;
                    continue;
                }
                if (created.Contains(d.Key)) continue;
                if (withTrigger.Contains(d.Key))
                {
                    // the first trigger that needs it creates it; the others come after that file
                    if (!written.Contains(d.Key)) function = s[d.Key];
                    continue;
                }
                if (s.TryGetValue(d.Key, out var need))
                    why = $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
            }
            why ??= NotReady(trg.Definition) ?? NotReady(function?.Definition);
            if (why is not null)
            {
                review.Hold(ReviewLog.NotApplied, "trigger", Display(trg), null, why, Triggers.Name);
                continue;
            }

            var text = new StringBuilder(Header(Triggers, "trigger", Display(trg))).Append(NotCompileChecked);
            if (function is not null)
            {
                text.Append($"-- with its trigger function {PgDdl.OneLine(Display(function))}, which the target does not have yet\n")
                    .Append(BodiesOff).Append(PgDdl.Statement(CreateOnly(function.Definition!.TrimEnd()).Text));
                written.Add(function.Key);
            }
            var module = trg.Definition!.TrimEnd();
            text.Append(PgDdl.Statement(module));
            Triggers.Files.Add(new PgFile($"trigger_{trg.Schema}.{trg.ParentName}.{trg.Name}", text.ToString(), NameOf(trg), module));
        }
        foreach (var key in withTrigger.Where(k => !written.Contains(k)))
        {
            var fn = s[key];
            notCreated[key] = "a trigger function: it is created in 07_triggers with the trigger that uses it, and that trigger is held back";
            review.Hold(ReviewLog.NotApplied, Kind(fn), Display(fn), null, notCreated[key], Missing.Name);
        }
        PlanRules();
    }

    /// <summary>
    /// The rules of tables (CREATE RULE): one the target's table lacks is created, one that differs is replaced with
    /// CREATE OR REPLACE RULE, each once what its action names is there. A rule changes what every write to its table
    /// does, as a trigger does, so it is 07's. One only the target has is held.
    /// </summary>
    private void PlanRules()
    {
        var tables = ChangedTables().Select(p => (p.Src, Tgt: (DbObject?)p.Tgt))
            .Concat(Src.Objects.Where(o => o.Type == DbObjectType.Table && created.Contains(o.Key))
                .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                .Select(o => (Src: o, Tgt: (DbObject?)null)));
        foreach (var (src, tgt) in tables)
        {
            foreach (var (key, rule) in src.Properties.Where(kv => kv.Key.StartsWith(PgCanonicalWriter.RulePrefix, StringComparison.Ordinal) && kv.Value is not null)
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, Rule: kv.Value!)))
            {
                var name = key[PgCanonicalWriter.RulePrefix.Length..];
                var theirs = tgt?.Properties.GetValueOrDefault(key);
                if (theirs == rule) continue;
                var why = MissingNamed(rule, before03: false) is { } needs
                    ? $"its action uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}"
                    : NotReady(rule);
                if (why is null && src.Columns.Where(c => PgDdl.MentionsColumn(rule, c.Name)).Select(c => c.Name) is var cols && Unavailable(src, cols) is { } col)
                    why = $"it names {col}, which the target will not have";
                if (why is not null)
                {
                    review.Hold(ReviewLog.NotApplied, "rule", name, src.FullName, why, Triggers.Name);
                    continue;
                }
                var sql = new StringBuilder();
                if (theirs is null) sql.Append(rule.TrimEnd()).Append('\n');
                else
                {
                    sql.Append($"-- The target's rule of this name is different: {PgDdl.OneLine(theirs)}\n");
                    var text = rule.TrimEnd();
                    sql.Append(text.StartsWith("CREATE RULE ", StringComparison.Ordinal) ? "CREATE OR REPLACE RULE " + text["CREATE RULE ".Length..] : text).Append('\n');
                    // the source's rule is enabled the default way and the target's is not
                    if (!text.Contains(" RULE " + PgDdl.Q(name) + ";", StringComparison.Ordinal) && theirs.Contains("ALTER TABLE ", StringComparison.Ordinal))
                        sql.Append($"ALTER TABLE {PgDdl.Q(src)} ENABLE RULE {PgDdl.Q(name)};\n");
                }
                Triggers.Files.Add(new PgFile($"rule_{src.Schema}.{src.Name}.{name}", Header(Triggers, "rule", $"{src.FullName}.{name}") + sql,
                    new ObjectName(src.Schema, src.Name)));
            }
            if (tgt is not null)
                foreach (var key in tgt.Properties.Keys.Where(k => k.StartsWith(PgCanonicalWriter.RulePrefix, StringComparison.Ordinal) && !src.Properties.ContainsKey(k)))
                    review.Hold(ReviewLog.NotAppliedDestructive, "rule", key[PgCanonicalWriter.RulePrefix.Length..], src.FullName,
                        $"only the target has it; dropping it changes what writes to the table do. Drop it by hand if you are sure (DROP RULE ... ON {PgDdl.Q(src)})",
                        Triggers.Name);
        }
    }

    // ------------------------------------------------------------------ 10_different_modules and 08_formatting_modules

    private void PlanDifferent() => PlanReplace(Diff.Different, Different, ReviewLog.DifferentNotApplied);

    /// <summary>
    /// 08: code whose text differs only in layout, comments or letter case is replaced with the source's, the same way
    /// 10 replaces code that differs (CREATE OR REPLACE with an unchanged signature), so both end up with one text.
    /// </summary>
    private void PlanFormatting() => PlanReplace(Diff.Formatting, FormattingModules, ReviewLog.FormattingNotApplied);

    /// <summary>
    /// Replaces each module of <paramref name="status"/> with the source's text in <paramref name="folder"/> when the
    /// signature, the return type (and a view's columns) allow CREATE OR REPLACE; anything else is held in
    /// <paramref name="heldFile"/>.
    /// </summary>
    private void PlanReplace(Diff status, PgFolder folder, string heldFile)
    {
        var targetMajor = Tgt.MajorVersion;
        var planned = new Dictionary<string, (DbObject Obj, string Text, string? Module)>(StringComparer.Ordinal);
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in Src.Objects.Where(o => o.Type.IsModule() && StatusOf(o) == status && InScope(o) && !IsExcluded(o))
                     .OrderBy(Rank).ThenBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                     .ThenBy(o => o.Signature ?? "", StringComparer.Ordinal))
        {
            var tgt = Target(o)!;
            void Hold(string why)
            {
                held[o.Key] = why;
                review.Hold(heldFile, Kind(o), Display(o), null, why, folder.Name);
            }
            if (o.DefinitionUnavailable || tgt.DefinitionUnavailable || string.IsNullOrWhiteSpace(o.Definition))
            {
                Hold("its text could not be read on both sides");
                continue;
            }
            var def = o.Definition!.TrimEnd();
            string text;
            switch (o.Type)
            {
                case DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction or DbObjectType.StoredProcedure or DbObjectType.AggregateFunction:
                    if (o.Properties.GetValueOrDefault("result") != tgt.Properties.GetValueOrDefault("result") ||
                        o.Properties.GetValueOrDefault("arguments") != tgt.Properties.GetValueOrDefault("arguments"))
                    {
                        Hold($"its return type or parameters differ (source: ({o.Properties.GetValueOrDefault("arguments")}) {o.Properties.GetValueOrDefault("result")}; " +
                             $"target: ({tgt.Properties.GetValueOrDefault("arguments")}) {tgt.Properties.GetValueOrDefault("result")}): CREATE OR REPLACE cannot " +
                             "change a return type, a parameter name or a default, so it needs DROP and CREATE");
                        continue;
                    }
                    if (o.Type == DbObjectType.AggregateFunction)
                    {
                        if (targetMajor < 12) { Hold($"PostgreSQL {targetMajor} has no CREATE OR REPLACE AGGREGATE (12 and later)"); continue; }
                        def = "CREATE OR REPLACE AGGREGATE " + def["CREATE AGGREGATE ".Length..];
                    }
                    text = BodiesOff + PgDdl.Statement(def);
                    break;
                case DbObjectType.View:
                    var before = Lines(tgt.Properties.GetValueOrDefault("columns"));
                    var after = Lines(o.Properties.GetValueOrDefault("columns"));
                    if (before.Count > after.Count || before.Where((c, i) => c != after[i]).Any())
                    {
                        Hold($"its columns change ({string.Join(", ", before)} to {string.Join(", ", after)}): CREATE OR REPLACE VIEW can only add columns at the end");
                        continue;
                    }
                    text = PgDdl.Statement(def);
                    break;
                case DbObjectType.Trigger:
                    if (targetMajor < 14) { Hold($"PostgreSQL {targetMajor} has no CREATE OR REPLACE TRIGGER (14 and later)"); continue; }
                    if (!def.StartsWith("CREATE TRIGGER ", StringComparison.Ordinal)) { Hold("a constraint trigger cannot be replaced in place"); continue; }
                    text = PgDdl.Statement("CREATE OR REPLACE TRIGGER " + def["CREATE TRIGGER ".Length..]);
                    if (!def.Contains(" DISABLE TRIGGER ", StringComparison.Ordinal) && tgt.Definition?.Contains(" DISABLE TRIGGER ", StringComparison.Ordinal) == true)
                        text += $"ALTER TABLE {PgDdl.Q(o.Schema, o.ParentName ?? "")} ENABLE TRIGGER {PgDdl.Q(o.Name)};\n";
                    def = text.TrimEnd();   // the module text the file carries
                    break;
                default:
                    Hold("a materialized view cannot be replaced in place: DROP and CREATE would empty it and break what uses it");
                    continue;
            }
            // the source's text the file carries exactly: an aggregate's after the CREATE that became CREATE OR REPLACE
            planned[o.Key] = (o, text, o.Type switch
            {
                DbObjectType.Trigger => null,
                DbObjectType.AggregateFunction => o.Definition!.TrimEnd()["CREATE ".Length..],
                _ => o.Definition!.TrimEnd(),
            });
        }

        // what each replacement reads must be there when it runs: on the target, created or added by this run, or replaced before it here
        string? Blocker(DbObject o)
        {
            foreach (var d in o.DependsOn ?? [])
            {
                if (d.Via == "foreignkey" || d.Key == o.Key || planned.ContainsKey(d.Key)) continue;
                if (t.TryGetValue(d.Key, out var have))
                {
                    if (have.Type == DbObjectType.Extension && ExtensionElsewhere(have) is { } elsewhere) return elsewhere;
                    if (d.Column is not { } col) continue;
                    if (have.Type == DbObjectType.Table && !HasColumn(d.Key, col))
                        return $"it reads column {have.FullName}.{col}, which the target does not have and 01 does not add";
                    if (have.Type is DbObjectType.View or DbObjectType.MaterializedView && !Lines(have.Properties.GetValueOrDefault("columns")).Any(l => l.StartsWith(PgDdl.Q(col) + " ", StringComparison.Ordinal)))
                        return $"it reads column {col} of {Kind(have)} {Display(have)}, which the target's copy does not have" +
                               (held.TryGetValue(d.Key, out var why) ? $", and its replacement is held ({why})" : "");
                    continue;
                }
                if (created.Contains(d.Key) || withTrigger.Contains(d.Key)) continue;
                if (s.TryGetValue(d.Key, out var need))
                    return $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
            }
            return NotReady(o.Definition);
        }
        void HoldPlanned(DbObject o, string why)
        {
            planned.Remove(o.Key);
            held[o.Key] = why;
            review.Hold(heldFile, Kind(o), Display(o), null, why, folder.Name);
        }

        // in pg_depend order, as 03 creates: a view that reads another changed view's new column comes after it
        List<DbObject> items;
        while (true)
        {
            PlanOrder.HoldUntilStable(planned.Values.Select(p => p.Obj).ToList(), o =>
            {
                if (!planned.ContainsKey(o.Key) || Blocker(o) is not { } why) return false;
                HoldPlanned(o, why);
                return true;
            });
            items = [.. planned.Values.Select(p => p.Obj)];
            var at = items.Select((o, i) => (o.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
            var edges = new List<(int, int)>();
            for (var i = 0; i < items.Count; i++)
                foreach (var d in items[i].DependsOn ?? [])
                    if (d.Via != "foreignkey" && at.TryGetValue(d.Key, out var j) && j != i) edges.Add((j, i));
            var forced = new List<int>();
            var order = PlanOrder.StableOrder(items.Count, edges, forced);
            if (forced.Count == 0)
            {
                items = [.. order.Select(i => items[i])];
                break;
            }
            var cycle = string.Join(", ", forced.Select(i => Display(items[i])));
            foreach (var i in forced) HoldPlanned(items[i], $"it is part of a dependency cycle ({cycle}): no order replaces them one by one");
        }

        foreach (var o in items)
        {
            var (_, text, module) = planned[o.Key];
            review.Note(status == Diff.Different ? ReviewLog.DifferentModulesFile : ReviewLog.FormattingNotApplied, Kind(o), Display(o), null,
                status == Diff.Different
                    ? "replaces the target's code with the source's; a change made only on the target is lost"
                    : "replaces the target's text with the source's: the same code, laid out (and commented) as the source has it", folder.Name);
            folder.Files.Add(new PgFile($"{Kind(o).Replace(' ', '_')}_{Display(o)}",
                Header(folder, Kind(o), Display(o)) + NotCompileChecked + text, NameOf(o), module));
        }
    }

    private static List<string> Lines(string? text) => [.. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    // ------------------------------------------------------------------ what is left

    /// <summary>The differences no folder scripts, each with why, so every one the compare shows is explained.</summary>
    private void PlanLeftOver()
    {
        foreach (var o in Src.Objects.Where(o => InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different && !altered.Contains(o.Key) &&
                                                 o.Type is DbObjectType.Schema or DbObjectType.EnumType or DbObjectType.Domain or DbObjectType.CompositeType
                                                     or DbObjectType.Sequence or DbObjectType.Extension or DbObjectType.RangeType or DbObjectType.Collation
                                                     or DbObjectType.Operator))
            review.Hold(ReviewLog.NotApplied, Kind(o), Display(o), null, o.Type switch
            {
                DbObjectType.EnumType => EnumCannotAdd(o) ?? "04_columntypes adds a missing enum label, and it is not selected",
                DbObjectType.Schema or DbObjectType.Domain or DbObjectType.Sequence or DbObjectType.CompositeType =>
                    $"05_constraints changes an existing {Kind(o)}, and it is not selected",
                DbObjectType.Operator =>
                    "an operator cannot be changed in place: it would have to be dropped and created again, with everything that uses it. Do it by hand",
                DbObjectType.Extension =>
                    $"the extension differs (source: version {o.Properties.GetValueOrDefault("version")} in schema {o.Properties.GetValueOrDefault("schema")}; target: " +
                    $"version {Target(o)!.Properties.GetValueOrDefault("version")} in schema {Target(o)!.Properties.GetValueOrDefault("schema")}). Extensions are " +
                    $"changed by hand, as a user allowed to: ALTER EXTENSION {PgDdl.Q(o.Name)} UPDATE, or SET SCHEMA",
                DbObjectType.RangeType or DbObjectType.Collation =>
                    $"a {Kind(o)} cannot be changed in place: it would have to be dropped and created again, with everything that uses it. Do it by hand",
                _ => $"changes to an existing {Kind(o)} are not scripted in this version (ALTER TYPE ... ADD, DROP or ALTER ATTRIBUTE by hand)",
            });

        foreach (var (src, tgt) in ChangedTables())
        {
            foreach (var key in src.Properties.Keys.Union(tgt.Properties.Keys).Order(StringComparer.Ordinal))
            {
                var a = src.Properties.GetValueOrDefault(key);
                var b = tgt.Properties.GetValueOrDefault(key);
                if (key == "options")
                {
                    // as the compare sees them: no fillfactor when it is ignored
                    a = PgCanonicalWriter.StorageOptions(a, compare.IgnoreFillFactor) is { Length: > 0 } sa ? sa : null;
                    b = PgCanonicalWriter.StorageOptions(b, compare.IgnoreFillFactor) is { Length: > 0 } sb ? sb : null;
                }
                if (a == b) continue;
                var isPolicy = key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal);
                var isRule = key.StartsWith(PgCanonicalWriter.RulePrefix, StringComparison.Ordinal);
                // what 05 (policies, row level security, logging, storage options) and 07 (rules) plan or hold themselves
                var folder = isRule ? ScriptSection.Triggers
                    : isPolicy || key is "rowSecurity" or "forceRowSecurity" or "persistence" or "options" or "ofType" ? ScriptSection.Constraints
                    : (ScriptSection?)null;
                if (folder is { } f && Selected(f)) continue;
                var name = isPolicy ? $"policy {key[PgCanonicalWriter.PolicyPrefix.Length..]}" : isRule ? $"rule {key[PgCanonicalWriter.RulePrefix.Length..]}" : key;
                static string Text(string v) => PgDdl.OneLine(v).TrimEnd().TrimEnd(';');
                var what = a is null ? $"only the target has it ({Text(b!)})"
                    : b is null ? $"the target does not have it (source: {Text(a)})"
                    : $"it differs (source: {Text(a)}; target: {Text(b)})";
                review.Hold(ReviewLog.NotApplied, "table option", name, src.FullName, folder is { } g
                    ? $"{what}; {(g == ScriptSection.Triggers ? "07_triggers" : "05_constraints")} changes it, and it is not selected"
                    : $"{what}; changing it is not scripted in this version");
            }
            foreach (var c in tgt.Columns.Where(c => src.Columns.All(x => x.Name != c.Name)))
            {
                if (KeptTargetColumn(src, c.Name) is { } kept)
                {
                    if (!kept.StartsWith("09_", StringComparison.Ordinal)) review.Hold(ReviewLog.NotApplied, "column", c.Name, src.FullName, kept);
                    continue;
                }
                if (!Destroys) review.Hold(ReviewLog.NotAppliedDestructive, "column", c.Name, src.FullName, TargetOnly);
            }

            // PostgreSQL cannot move a column: the order the target ends with, against the source's (a column 09 renames keeps its place)
            if (compare.IgnoreColumnOrder) continue;
            var renamed = renames.GetValueOrDefault(src.Key) ?? [];
            var after = tgt.Columns.OrderBy(c => c.Ordinal).Select(c => renamed.FirstOrDefault(r => r.Value == c.Name).Key ?? c.Name)
                .Concat(addedInOrder.Where(a => a.Table == src.Key).Select(a => a.Column)).ToList();
            var wanted = src.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).Where(after.Contains).ToList();
            if (!wanted.SequenceEqual(after.Where(wanted.Contains)))
                review.Hold(ReviewLog.NotApplied, "column order", src.FullName, null,
                    $"the columns end up in another order than the source's ({string.Join(", ", wanted)}): PostgreSQL adds a column at the end " +
                    "and cannot move one. Turn on \"Ignore column order\" in the compare options, or rebuild the table by hand.");
        }
    }
}
