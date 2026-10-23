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
        DbObjectType.EnumType => 1,
        DbObjectType.Domain => 2,
        DbObjectType.CompositeType => 3,
        DbObjectType.Sequence => 4,
        DbObjectType.Table => 5,
        DbObjectType.ScalarFunction or DbObjectType.TableValuedFunction or DbObjectType.StoredProcedure => 6,
        DbObjectType.AggregateFunction => 7,
        DbObjectType.View => 8,
        DbObjectType.MaterializedView => 9,
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

    private async Task PlanMissingAsync()
    {
        var quarantine = QuarantineNames();
        var usedByNewTriggers = Selected(ScriptSection.Triggers)
            ? MissingTriggers().SelectMany(tr => tr.DependsOn ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal)
            : [];

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
            if (o.Type == DbObjectType.Table && IsChild(o)) { Hold(ReviewLog.NotApplied, ChildReason); continue; }
            if (o.Type.IsModule() && (o.IsEncrypted || o.DefinitionUnavailable || string.IsNullOrWhiteSpace(o.Definition)))
            {
                Hold(ReviewLog.NotApplied, "its text could not be read from the source");
                continue;
            }
            if (IsTriggerFunction(o) && usedByNewTriggers.Contains(o.Key))
            {
                withTrigger.Add(o.Key);   // 07 creates it with its trigger
                continue;
            }
            set[o.Key] = o;
        }

        // what each object needs must be on the target, or created before it here
        string? Blocker(DbObject o)
        {
            foreach (var d in o.DependsOn ?? [])
            {
                if (d.Via == "foreignkey" || d.Key == o.Key) continue;   // foreign keys: 05, once every table exists
                if (t.TryGetValue(d.Key, out var have))
                {
                    if (d.Column is { } col && have.Type == DbObjectType.Table && !HasColumn(d.Key, col))
                        return $"it needs column {have.FullName}.{col}, which the target does not have and 01 does not add";
                    continue;
                }
                if (set.ContainsKey(d.Key) || withTrigger.Contains(d.Key)) continue;
                if (s.TryGetValue(d.Key, out var need))
                    return need.Type == DbObjectType.Extension
                        ? $"it needs extension {need.Name}, which the target does not have - install it first"
                        : $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
            }
            return null;
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
                    if (d.Via != "foreignkey" && at.TryGetValue(d.Key, out var j) && j != i) edges.Add((j, i));
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

        // a schema keeps its owner when that role exists on the target
        var owners = items.Where(o => o.Type == DbObjectType.Schema).Select(o => o.Properties.GetValueOrDefault("owner")).OfType<string>().Distinct().ToList();
        var roles = owners.Count > 0 ? await probes.ExistingRolesAsync(owners, ct) : [];

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
                case DbObjectType.EnumType:
                    text.Append($"CREATE TYPE {PgDdl.Q(o)} AS ENUM ({o.Properties.GetValueOrDefault("labels")});\n");
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
                                $"OWNED BY {owned} is left out: that column is not there when 03 runs", Missing.Name);
                    }
                    break;
                case DbObjectType.Table:
                    text.Append(Table(o, items.Where(q => q.Type == DbObjectType.Sequence &&
                                                          ownedBy.TryGetValue(q.Properties.GetValueOrDefault("ownedBy") ?? "", out var c) && c.Table.Key == o.Key)
                        .Select(q => (q, ownedBy[q.Properties["ownedBy"]!].Column))));
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
    /// cannot say: NOT VALID checks, the sequences it owns, row level security. Foreign keys come in 05, indexes in 06,
    /// triggers in 07; a policy names roles, so it is held for review.
    /// </summary>
    private string Table(DbObject o, IEnumerable<(DbObject Sequence, string Column)> owned)
    {
        var p = o.Properties;
        var lines = o.Columns.OrderBy(c => c.Ordinal).Select(c => "    " + ColumnDefinition(o, c)).ToList();
        foreach (var k in o.Indexes.Where(IsKey).OrderBy(i => i.IsPrimaryKey ? 0 : i.IsUniqueConstraint ? 1 : 2).ThenBy(i => i.Name, StringComparer.Ordinal))
            lines.Add($"    CONSTRAINT {PgDdl.Q(k.Name)} {k.Definition}");
        var checks = o.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        foreach (var c in checks.Where(c => !c.IsNotTrusted)) lines.Add($"    CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition}");

        var sb = new StringBuilder();
        sb.Append(p.ContainsKey("persistence") ? "CREATE UNLOGGED TABLE " : "CREATE TABLE ").Append(PgDdl.Q(o)).Append(" (\n")
          .Append(string.Join(",\n", lines)).Append("\n)");
        if (p.GetValueOrDefault("partitionBy") is { Length: > 0 } partitionBy) sb.Append($" PARTITION BY {partitionBy}");
        if (p.GetValueOrDefault("options") is { Length: > 0 } storage) sb.Append($" WITH ({storage})");
        sb.Append(";\n");
        foreach (var c in checks.Where(c => c.IsNotTrusted))
            sb.Append($"ALTER TABLE {PgDdl.Q(o)} ADD CONSTRAINT {PgDdl.Q(c.Name)} {c.Definition} NOT VALID;\n");
        foreach (var (seq, column) in owned)
            sb.Append($"ALTER SEQUENCE {PgDdl.Q(seq)} OWNED BY {PgDdl.Q(o)}.{PgDdl.Q(column)};\n");
        if (p.ContainsKey("rowSecurity")) sb.Append($"ALTER TABLE {PgDdl.Q(o)} ENABLE ROW LEVEL SECURITY;\n");
        if (p.ContainsKey("forceRowSecurity")) sb.Append($"ALTER TABLE {PgDdl.Q(o)} FORCE ROW LEVEL SECURITY;\n");
        foreach (var (key, policy) in p.Where(kv => kv.Key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            review.Hold(ReviewLog.NotApplied, "policy", key[PgCanonicalWriter.PolicyPrefix.Length..], o.FullName,
                $"a row level security policy names roles that may not exist on the target; create it by hand: {policy}", Missing.Name);
        return sb.ToString();
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

    private void PlanTriggers()
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trg in MissingTriggers().OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.ParentName, StringComparer.Ordinal)
                     .ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            var tableKey = KeyOf(DbObjectType.Table, trg.Schema, trg.ParentName ?? "");
            string? why = null;
            DbObject? function = null;
            if (!t.ContainsKey(tableKey) && !created.Contains(tableKey))
                why = $"its table {trg.Schema}.{trg.ParentName} is not on the target and this run does not create it";
            if (string.IsNullOrWhiteSpace(trg.Definition)) why ??= "its text could not be read from the source";
            foreach (var d in trg.DependsOn ?? [])
            {
                if (why is not null) break;
                if (d.Key == tableKey)
                {
                    if (d.Column is { } col && !HasColumn(tableKey, col)) why = $"it names column {col}, which the target will not have";
                    continue;
                }
                if (t.ContainsKey(d.Key) || created.Contains(d.Key)) continue;
                if (withTrigger.Contains(d.Key)) { function = s[d.Key]; continue; }
                if (s.TryGetValue(d.Key, out var need))
                    why = $"it needs {Kind(need)} {Display(need)}, which the target does not have and this run does not create{Why(need)}";
            }
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
    }

    // ------------------------------------------------------------------ 10_different_modules

    private void PlanDifferent()
    {
        var targetMajor = Tgt.MajorVersion;
        var planned = new Dictionary<string, (DbObject Obj, string Text, string? Module)>(StringComparer.Ordinal);
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in Src.Objects.Where(o => o.Type.IsModule() && StatusOf(o) == Diff.Different && InScope(o) && !IsExcluded(o))
                     .OrderBy(Rank).ThenBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
                     .ThenBy(o => o.Signature ?? "", StringComparer.Ordinal))
        {
            var tgt = Target(o)!;
            void Hold(string why)
            {
                held[o.Key] = why;
                review.Hold(ReviewLog.DifferentNotApplied, Kind(o), Display(o), null, why, Different.Name);
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
            planned[o.Key] = (o, text, o.Type == DbObjectType.Trigger ? null : o.Definition!.TrimEnd());
        }

        // what each replacement reads must be there when it runs: on the target, created or added by this run, or replaced before it here
        string? Blocker(DbObject o)
        {
            foreach (var d in o.DependsOn ?? [])
            {
                if (d.Via == "foreignkey" || d.Key == o.Key || planned.ContainsKey(d.Key)) continue;
                if (t.TryGetValue(d.Key, out var have))
                {
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
            return null;
        }
        void HoldPlanned(DbObject o, string why)
        {
            planned.Remove(o.Key);
            held[o.Key] = why;
            review.Hold(ReviewLog.DifferentNotApplied, Kind(o), Display(o), null, why, Different.Name);
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
            review.Note(ReviewLog.DifferentModulesFile, Kind(o), Display(o), null,
                "replaces the target's code with the source's; a change made only on the target is lost", Different.Name);
            Different.Files.Add(new PgFile($"{Kind(o).Replace(' ', '_')}_{Display(o)}",
                Header(Different, Kind(o), Display(o)) + NotCompileChecked + text, NameOf(o), module));
        }
    }

    private static List<string> Lines(string? text) => [.. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    // ------------------------------------------------------------------ 08 and what is left

    private void PlanFormatting()
    {
        foreach (var o in Src.Objects.Where(o => o.Type.IsModule() && StatusOf(o) == Diff.Formatting && InScope(o) && !IsExcluded(o)))
            review.Hold(ReviewLog.FormattingNotApplied, Kind(o), Display(o), null,
                "only layout, comments or letter case differ; 08_formatting_modules is not applicable for PostgreSQL, so the target keeps its own text",
                PgScriptGenerator.FormattingModulesFolder);
    }

    /// <summary>The differences no folder scripts, each with why, so every one the compare shows is explained.</summary>
    private void PlanLeftOver()
    {
        foreach (var o in Src.Objects.Where(o => InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different &&
                                                 o.Type is DbObjectType.Schema or DbObjectType.EnumType or DbObjectType.Domain
                                                     or DbObjectType.CompositeType or DbObjectType.Sequence or DbObjectType.Extension))
            review.Hold(ReviewLog.NotApplied, Kind(o), Display(o), null,
                $"changes to an existing {Kind(o)} are not scripted in this version; compare the two definitions and change it by hand");

        foreach (var (src, tgt) in ChangedTables())
        {
            foreach (var key in src.Properties.Keys.Union(tgt.Properties.Keys).Order(StringComparer.Ordinal))
            {
                var a = src.Properties.GetValueOrDefault(key);
                var b = tgt.Properties.GetValueOrDefault(key);
                if (a == b) continue;
                var name = key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal) ? $"policy {key[PgCanonicalWriter.PolicyPrefix.Length..]}" : key;
                review.Hold(ReviewLog.NotApplied, "table option", name, src.FullName,
                    $"differs (source: {a ?? "not set"}; target: {b ?? "not set"}); changing it is not scripted in this version");
            }
            foreach (var c in tgt.Columns.Where(c => src.Columns.All(x => x.Name != c.Name)))
                review.Hold(ReviewLog.NotAppliedDestructive, "column", c.Name, src.FullName, TargetOnly);

            // PostgreSQL cannot move a column: the order the target ends with, against the source's
            if (compare.IgnoreColumnOrder) continue;
            var after = tgt.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name)
                .Concat(addedInOrder.Where(a => a.Table == src.Key).Select(a => a.Column)).ToList();
            var wanted = src.Columns.OrderBy(c => c.Ordinal).Select(c => c.Name).Where(after.Contains).ToList();
            if (!wanted.SequenceEqual(after.Where(wanted.Contains)))
                review.Hold(ReviewLog.NotApplied, "column order", src.FullName, null,
                    $"the columns end up in another order than the source's ({string.Join(", ", wanted)}): PostgreSQL adds a column at the end " +
                    "and cannot move one. Turn on \"Ignore column order\" in the compare options, or rebuild the table by hand.");
        }
    }
}
