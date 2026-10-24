using System.Text;
using System.Text.RegularExpressions;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// Changes to objects both sides have that only add or align, never drop data: an enum's new labels (04), a
/// sequence's options and owner, a domain's default, NOT NULL and checks, a schema's owner, a table's row level
/// security, policies, storage options and logging, a default, and a foreign key whose actions differ (05), and the
/// renames of columns whose names differ only by letter case (09). What cannot be done that way is held with why.
/// </summary>
internal sealed partial class PgScriptRun
{
    public PgFolder FormattingModules { get; } = new(PgScriptGenerator.FormattingModulesFolder, "Formatting (code)", false);
    public PgFolder FormattingTables { get; } = new(PgScriptGenerator.FormattingTablesFolder, "Formatting (names)", false);

    /// <summary>Objects (sequences, enums, domains, schemas) whose difference a folder has planned or held already.</summary>
    private readonly HashSet<string> altered = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ what a text needs that the target lacks

    /// <summary>Enum key -> the labels (as literals) 04 adds to the target's type.</summary>
    private readonly Dictionary<string, HashSet<string>> enumAdds = new(StringComparer.Ordinal);

    /// <summary>An enum's labels as quote_literal wrote them, in order.</summary>
    internal static List<string> EnumLabels(DbObject e) =>
        [.. LabelRx().Matches(e.Properties.GetValueOrDefault("labels") ?? "").Select(m => m.Value)];

    [GeneratedRegex(@"E?'(?:[^'\\]|''|\\.)*'", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRx();

    private List<(DbObject Source, string From, string To)>? moved;

    /// <summary>The extensions both sides have, installed in another schema on the target.</summary>
    private List<(DbObject Source, string From, string To)> MovedExtensions() => moved ??=
    [
        .. Src.Objects.Where(o => o.Type == DbObjectType.Extension && t.TryGetValue(o.Key, out var te) &&
                                  (o.Properties.GetValueOrDefault("schema") ?? "") != (te.Properties.GetValueOrDefault("schema") ?? ""))
            .Select(o => (o, o.Properties.GetValueOrDefault("schema") ?? "", t[o.Key].Properties.GetValueOrDefault("schema") ?? ""))
    ];

    /// <summary>Why an extension the target has is still not what <paramref name="need"/> asks for (another schema), or null.</summary>
    private string? ExtensionElsewhere(DbObject need) =>
        MovedExtensions().FirstOrDefault(m => m.Source.Key == need.Key) is { Source: not null } m
            ? $"it needs extension {need.Name} in schema {m.From}, and the target has it in schema {m.To}"
            : null;

    /// <summary>
    /// Why <paramref name="text"/> cannot run on the target even though every object it names is there: it uses an enum
    /// label the target's type lacks (and 04 does not add), or names the schema an extension sits in on the source
    /// when the target has that extension in another schema. Null when neither.
    /// </summary>
    private string? NotReady(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var e in Src.Objects.Where(o => o.Type == DbObjectType.EnumType && t.ContainsKey(o.Key)))
        {
            var have = EnumLabels(t[e.Key]).ToHashSet(StringComparer.Ordinal);
            if (enumAdds.TryGetValue(e.Key, out var adds)) have.UnionWith(adds);
            foreach (var label in EnumLabels(e).Where(l => !have.Contains(l)))
                if (MentionsLabel(text, label, e))
                    return $"it uses label {label} of enum type {Display(e)}, which the target's type does not have " +
                           (Selected(ScriptSection.ColumnTypes) ? "and 04_columntypes does not add" : "(04_columntypes, which adds labels, is not selected)");
        }
        foreach (var (ext, from, to) in MovedExtensions())
            if (from.Length > 0 && PgDdl.MentionsSchema(text, from))
                return $"it names schema {from}, where extension {ext.Name} is on the source; the target has {ext.Name} in schema {to}";
        return null;
    }

    /// <summary>'label'::schema.enum as the deparse writes a label of that type.</summary>
    private static bool MentionsLabel(string text, string label, DbObject e) =>
        new[] { $"{PgDdl.Ident(e.Schema)}.{PgDdl.Ident(e.Name)}", PgDdl.Q(e.Schema, e.Name) }.Distinct()
            .Any(type => text.Contains($"{label}::{type}", StringComparison.Ordinal));

    // ------------------------------------------------------------------ 04: enum labels

    /// <summary>
    /// ALTER TYPE ... ADD VALUE for every label the source's enum has and the target's lacks, each in its own file
    /// (a label added in a transaction cannot be used until it commits), in the source's order. Labels can only be
    /// added: one the target has and the source does not, or two in another order, holds the enum back.
    /// </summary>
    private void PlanEnums()
    {
        foreach (var e in Src.Objects.Where(o => o.Type == DbObjectType.EnumType && InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different)
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            altered.Add(e.Key);
            var want = EnumLabels(e);
            var have = EnumLabels(Target(e)!);
            var at = have.Select(l => want.IndexOf(l)).ToList();
            if (at.Any(i => i < 0) || at.Zip(at.Skip(1)).Any(p => p.Second < p.First))
            {
                var gone = have.Where(l => !want.Contains(l)).ToList();
                review.Hold(ReviewLog.NotApplied, Kind(e), Display(e), null,
                    gone.Count > 0
                        ? $"the target's type has label(s) {string.Join(", ", gone)} that the source's does not; PostgreSQL cannot remove a label"
                        : $"the labels are in another order on the target ({string.Join(", ", have)}); PostgreSQL cannot reorder them", ColumnTypes.Name);
                continue;
            }
            var present = new List<string>(have);
            var adds = enumAdds[e.Key] = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < want.Count; i++)
            {
                if (present.Contains(want[i])) continue;
                var where = i > 0 ? $" AFTER {want[i - 1]}" : have.Count > 0 ? $" BEFORE {have[0]}" : "";
                ColumnTypes.Files.Add(new PgFile($"enum_{e.Schema}.{e.Name}.{want[i].Trim('\'')}", Header(ColumnTypes, "enum label", $"{Display(e)} {want[i]}") +
                    "-- One label a file: PostgreSQL lets a new label be used only once the transaction that added it has committed.\n" +
                    $"ALTER TYPE {PgDdl.Q(e)} ADD VALUE {want[i]}{where};\n", NameOf(e)));
                present.Add(want[i]);
                adds.Add(want[i]);
            }
        }
    }

    // ------------------------------------------------------------------ 05: what a table has beyond its columns

    /// <summary>The roles a policy's TO names (PUBLIC is every role and always there).</summary>
    internal static List<string> PolicyRoles(string policy)
    {
        var m = PolicyRolesRx().Match(policy);
        if (!m.Success) return [];
        var roles = new List<string>();
        foreach (var part in SplitNames(m.Groups["roles"].Value))
            roles.Add(part.StartsWith('"') ? part[1..^1].Replace("\"\"", "\"") : part);
        return roles;
    }

    /// <summary>"a, \"B, c\", d" as a, "B, c", d: commas inside quotes stay.</summary>
    private static IEnumerable<string> SplitNames(string text)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (c == ',' && !quoted)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString().Trim();
    }

    [GeneratedRegex(@" FOR (?:ALL|SELECT|INSERT|UPDATE|DELETE) TO (?<roles>.*?)(?: USING \(| WITH CHECK \(|;\s*$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyRolesRx();

    /// <summary>The roles every policy of these tables names, PUBLIC left out (it always exists).</summary>
    private static List<string> RolesOf(IEnumerable<DbObject> tables) =>
        [.. tables.SelectMany(o => o.Properties.Where(kv => kv.Key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal)))
            .SelectMany(kv => PolicyRoles(kv.Value ?? "")).Where(r => r != "PUBLIC").Distinct(StringComparer.Ordinal)];

    /// <summary>Why a policy cannot be created on the target (a role it names is not there, or could not be looked up), or null.</summary>
    private static string? PolicyBlocked(string policy, HashSet<string>? roles)
    {
        var missing = PolicyRoles(policy).Where(r => r != "PUBLIC" && (roles is null || !roles.Contains(r))).ToList();
        if (missing.Count == 0) return null;
        return roles is null
            ? $"the target's roles could not be looked up, and it names {string.Join(", ", missing)}; create it by hand: {PgDdl.OneLine(policy)}"
            : $"it names role(s) {string.Join(", ", missing)}, which the target does not have; create the role, then the policy: {PgDdl.OneLine(policy)}";
    }

    private static IEnumerable<KeyValuePair<string, string>> PoliciesOf(DbObject table) =>
        table.Properties.Where(kv => kv.Key.StartsWith(PgCanonicalWriter.PolicyPrefix, StringComparison.Ordinal) && kv.Value is not null)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!));

    private const string RlsHeld =
        "row level security is left off: a policy of this table is held back, and with it on and that policy missing, every role without " +
        "BYPASSRLS (with FORCE, the owner too) would see and write no rows. Switch it on by hand once the policy is there";

    /// <summary>
    /// A table both sides have: its policies (a missing one created, a different one replaced in one file), row level
    /// security switched on as the source has it once every policy is there, its storage options and logging.
    /// </summary>
    private async Task PlanTableOptionsAsync(DbObject src, DbObject tgt, HashSet<string>? roles)
    {
        var table = PgDdl.Q(src);
        var policiesReady = true;
        foreach (var (key, policy) in PoliciesOf(src))
        {
            var name = key[PgCanonicalWriter.PolicyPrefix.Length..];
            var theirs = tgt.Properties.GetValueOrDefault(key);
            if (theirs == policy) continue;
            string? why = PolicyBlocked(policy, roles);
            if (why is null && src.Columns.Where(c => PgDdl.MentionsColumn(policy, c.Name)).Select(c => c.Name) is var cols && Unavailable(src, cols) is { } col)
                why = $"it reads {col}, which the target will not have";
            if (why is null && MissingNamed(policy, before03: false) is { } needs)
                why = $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create{Why(needs)}";
            why ??= NotReady(policy);
            if (why is not null)
            {
                policiesReady = false;
                review.Hold(ReviewLog.NotAppliedConstraints, "policy", name, src.FullName, why, Constraints.Name);
                continue;
            }
            var sql = theirs is null
                ? policy.TrimEnd() + "\n"
                : $"-- The target's policy of this name is different: {PgDdl.OneLine(theirs)}\nDROP POLICY {PgDdl.Q(name)} ON {table};\n{policy.TrimEnd()}\n";
            Constraints.Files.Add(new PgFile($"policy_{src.Schema}.{src.Name}.{name}", Header(Constraints, "policy", $"{src.FullName}.{name}") + sql,
                new ObjectName(src.Schema, src.Name)));
        }
        foreach (var (key, _) in PoliciesOf(tgt).Where(kv => !src.Properties.ContainsKey(kv.Key)))
            review.Hold(ReviewLog.NotAppliedDestructive, "policy", key[PgCanonicalWriter.PolicyPrefix.Length..], src.FullName,
                $"only the target has it; dropping it changes who sees which rows. Drop it by hand if you are sure (DROP POLICY ... ON {table})", Constraints.Name);

        foreach (var (key, verb) in new[] { ("rowSecurity", "ENABLE"), ("forceRowSecurity", "FORCE") })
        {
            var want = src.Properties.ContainsKey(key);
            if (want == tgt.Properties.ContainsKey(key)) continue;
            if (!want)
                review.Hold(ReviewLog.NotApplied, "table option", key, src.FullName,
                    $"the target has {verb} ROW LEVEL SECURITY and the source does not; turning it off lets every role see every row, so it is not scripted", Constraints.Name);
            else if (!policiesReady)
                review.Hold(ReviewLog.NotApplied, "table option", key, src.FullName, RlsHeld, Constraints.Name);
            else
                Constraints.Files.Add(new PgFile($"rls_{src.Schema}.{src.Name}.{verb.ToLowerInvariant()}", Header(Constraints, "row level security", src.FullName) +
                    $"ALTER TABLE {table} {verb} ROW LEVEL SECURITY;\n", new ObjectName(src.Schema, src.Name)));
        }

        // UNLOGGED: making a table logged again is safe (it writes the whole table once); the other way it loses its rows on a crash
        var srcUnlogged = src.Properties.ContainsKey("persistence");
        if (srcUnlogged != tgt.Properties.ContainsKey("persistence"))
        {
            if (srcUnlogged)
                review.Hold(ReviewLog.NotApplied, "table option", "UNLOGGED", src.FullName,
                    "the source's table is UNLOGGED and the target's is not; an unlogged table is emptied after a crash, so SET UNLOGGED is left to you", Constraints.Name);
            else
            {
                var rows = await probes.RowEstimateAsync(src.Schema, src.Name, ct);
                if (rows >= Options.LargeTableRows)
                    review.Note(ReviewLog.NotApplied, "table option", "LOGGED", src.FullName,
                        $"SET LOGGED writes the whole table (about {rows:N0} rows) to the WAL under an ACCESS EXCLUSIVE lock", Constraints.Name);
                Constraints.Files.Add(new PgFile($"logged_{src.Schema}.{src.Name}", Header(Constraints, "table", src.FullName) +
                    $"ALTER TABLE {table} SET LOGGED;\n", new ObjectName(src.Schema, src.Name)));
            }
        }

        // WITH (...) storage options, one by one (a partitioned table has none of its own)
        static Dictionary<string, string> StorageOf(DbObject o) => (o.Properties.GetValueOrDefault("options") ?? "")
            .Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
            .ToDictionary(x => x[0], x => x.Length > 1 ? x[1] : "", StringComparer.Ordinal);
        var so = StorageOf(src);
        var to = StorageOf(tgt);
        var set = so.Where(kv => to.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => $"{kv.Key}={kv.Value}").ToList();
        var reset = to.Keys.Where(k => !so.ContainsKey(k)).ToList();
        if (set.Count + reset.Count > 0)
        {
            var sql = new StringBuilder();
            if (set.Count > 0) sql.Append($"ALTER TABLE {table} SET ({string.Join(", ", set)});\n");
            if (reset.Count > 0) sql.Append($"ALTER TABLE {table} RESET ({string.Join(", ", reset)});\n");
            Constraints.Files.Add(new PgFile($"options_{src.Schema}.{src.Name}", Header(Constraints, "storage options", src.FullName) + sql,
                new ObjectName(src.Schema, src.Name)));
        }
    }

    // ------------------------------------------------------------------ 05: sequences, domains, schemas

    /// <summary>
    /// A sequence both sides have, as the source has it: one ALTER SEQUENCE for its type, start, step, limits, cache and
    /// cycle, and OWNED BY once the column it belongs to is there (on the target, or added or created by this run).
    /// </summary>
    private void PlanSequences()
    {
        foreach (var src in Src.Objects.Where(o => o.Type == DbObjectType.Sequence && InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different)
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            altered.Add(src.Key);
            var tgt = Target(src)!;
            var p = src.Properties;
            var q = tgt.Properties;
            var parts = new List<string>();
            void Part(string key, Func<string, string> sql)
            {
                if (p.GetValueOrDefault(key) is { } v && v != q.GetValueOrDefault(key)) parts.Add(sql(v));
            }
            Part("type", v => $"AS {v}");
            Part("increment", v => $"INCREMENT BY {v}");
            Part("minValue", v => $"MINVALUE {v}");
            Part("maxValue", v => $"MAXVALUE {v}");
            Part("startValue", v => $"START WITH {v}");
            Part("cacheSize", v => $"CACHE {v}");
            Part("isCycling", v => v == "1" ? "CYCLE" : "NO CYCLE");

            string? owned = null;
            var ownedBy = p.GetValueOrDefault("ownedBy");
            if (ownedBy != q.GetValueOrDefault("ownedBy"))
            {
                if (ownedBy is null) owned = "NONE";
                else if (ColumnOf(ownedBy) is { } column && HasColumn(column.Table.Key, column.Column)) owned = $"{PgDdl.Q(column.Table)}.{PgDdl.Q(column.Column)}";
                else
                    review.Hold(ReviewLog.NotApplied, "sequence owner", Display(src), null,
                        $"OWNED BY {ownedBy} is left out: that column is not on the target and this run does not add it", Constraints.Name);
            }
            if (parts.Count == 0 && owned is null) continue;
            var sql = new StringBuilder();
            if (parts.Count > 0)
                sql.Append("-- Changing MINVALUE, MAXVALUE or AS fails, and changes nothing, if the sequence's current value is outside the new range.\n")
                   .Append($"ALTER SEQUENCE {PgDdl.Q(src)} {string.Join(" ", parts)};\n");
            if (owned is not null) sql.Append($"ALTER SEQUENCE {PgDdl.Q(src)} OWNED BY {owned};\n");
            Constraints.Files.Add(new PgFile($"sequence_{src.Schema}.{src.Name}", Header(Constraints, "sequence", Display(src)) + sql, NameOf(src)));
        }
    }

    /// <summary>The table and column an ownedBy spelling (schema.table.column, quote_ident style) names, on either side.</summary>
    private (DbObject Table, string Column)? ColumnOf(string ownedBy)
    {
        foreach (var table in Src.Objects.Concat(Tgt.Objects).Where(o => o.Type == DbObjectType.Table))
            foreach (var c in table.Columns)
                if ($"{PgDdl.Ident(table.Schema)}.{PgDdl.Ident(table.Name)}.{PgDdl.Ident(c.Name)}" == ownedBy) return (table, c.Name);
        return null;
    }

    /// <summary>
    /// A domain both sides have: a default set or changed, NOT NULL relaxed, a missing check added NOT VALID and
    /// validated by a later file, a check whose rule differs replaced the same way. A base type or collation that
    /// differs, NOT NULL the target lacks, and what only the target has are held.
    /// </summary>
    private void PlanDomains()
    {
        foreach (var src in Src.Objects.Where(o => o.Type == DbObjectType.Domain && InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different)
                     .OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            altered.Add(src.Key);
            var tgt = Target(src)!;
            var name = PgDdl.Q(src);
            var p = src.Properties;
            var q = tgt.Properties;
            void Hold(string what, string why) => review.Hold(ReviewLog.NotAppliedConstraints, what, Display(src), null, why, Constraints.Name);
            void File(string word, string sql) => Constraints.Files.Add(new PgFile($"domain_{src.Schema}.{src.Name}.{word}",
                Header(Constraints, "domain", Display(src)) + sql, NameOf(src)));

            if (p.GetValueOrDefault("baseType") != q.GetValueOrDefault("baseType"))
            {
                Hold("domain", $"its base type differs (source: {p.GetValueOrDefault("baseType")}, target: {q.GetValueOrDefault("baseType")}); a domain's type cannot be changed");
                continue;
            }
            if (!compare.IgnoreCollation && p.GetValueOrDefault("collation") != q.GetValueOrDefault("collation"))
                Hold("domain", "its collation differs; a domain's collation cannot be changed");
            if (p.GetValueOrDefault("default") is { } def && def != q.GetValueOrDefault("default"))
            {
                if ((MissingNamed(def, before03: false) is { } needs ? $"its default uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create" : NotReady(def)) is { } why)
                    Hold("default", why);
                else File("default", $"ALTER DOMAIN {name} SET DEFAULT {def};\n");
            }
            else if (p.GetValueOrDefault("default") is null && q.ContainsKey("default"))
                Hold("default", $"only the target's domain has a default ({q["default"]}); dropping it changes what new rows get, so it is left to you (ALTER DOMAIN {name} DROP DEFAULT)");
            var notNull = p.GetValueOrDefault("notNull") == "1";
            if (notNull != (q.GetValueOrDefault("notNull") == "1"))
            {
                if (notNull) Hold("not null", "the source's domain is NOT NULL and the target's is not: that fails on any NULL already stored, and is not scripted");
                else File("null", $"ALTER DOMAIN {name} DROP NOT NULL;\n");
            }

            var theirs = tgt.Constraints.Where(c => c.Kind == ConstraintKind.Check).ToDictionary(c => c.Name, StringComparer.Ordinal);
            foreach (var ck in src.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                var other = theirs.GetValueOrDefault(ck.Name);
                if (other is not null && other.Definition == ck.Definition)
                {
                    if (!ck.IsNotTrusted && other.IsNotTrusted) ValidateDomain(src, ck.Name);
                    continue;
                }
                if ((MissingNamed(ck.Definition, before03: false) is { } needs ? $"it uses {Kind(needs)} {Display(needs)}, which the target lacks and this run does not create" : NotReady(ck.Definition)) is { } why)
                {
                    review.Hold(ReviewLog.NotAppliedConstraints, "check", ck.Name, Display(src), why, Constraints.Name);
                    continue;
                }
                var sql = new StringBuilder("-- Added NOT VALID: checks new values at once; the values already stored are checked by VALIDATE in a later file.\n");
                if (other is not null)
                    sql.Append($"-- The target's check of this name is different: {PgDdl.OneLine(other.Definition)}\n")
                       .Append($"ALTER DOMAIN {name} DROP CONSTRAINT {PgDdl.Q(ck.Name)} RESTRICT;\n");
                sql.Append($"ALTER DOMAIN {name} ADD CONSTRAINT {PgDdl.Q(ck.Name)} {ck.Definition} NOT VALID;\n");
                Constraints.Files.Add(new PgFile($"domain_{src.Schema}.{src.Name}.{ck.Name}", Header(Constraints, "domain check", $"{Display(src)}.{ck.Name}") + sql,
                    NameOf(src)));
                if (!ck.IsNotTrusted) ValidateDomain(src, ck.Name);
            }
            foreach (var other in theirs.Values.Where(c => src.Constraints.All(x => x.Kind != ConstraintKind.Check || x.Name != c.Name)))
                review.Hold(ReviewLog.NotAppliedDestructive, "check", other.Name, Display(src),
                    $"only the target's domain has it; dropping it changes what the database accepts, so it is left to you (ALTER DOMAIN {name} DROP CONSTRAINT {PgDdl.Q(other.Name)})",
                    Constraints.Name);
        }
    }

    private void ValidateDomain(DbObject domain, string constraint) =>
        validations.Add(new PgFile($"validate_{domain.Schema}.{domain.Name}.{constraint}",
            Header(Constraints, "validate", $"{Display(domain)}.{constraint}") +
            "-- Checks the values already stored in columns of this domain; new values were checked from the moment it was added.\n" +
            $"ALTER DOMAIN {PgDdl.Q(domain)} VALIDATE CONSTRAINT {PgDdl.Q(constraint)};\n", NameOf(domain)));

    /// <summary>A schema both sides have whose owner differs: ALTER SCHEMA ... OWNER TO when that role is on the target.</summary>
    private void PlanSchemaOwners(HashSet<string>? roles)
    {
        foreach (var src in Src.Objects.Where(o => o.Type == DbObjectType.Schema && InScope(o) && !IsExcluded(o) && StatusOf(o) == Diff.Different)
                     .OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            altered.Add(src.Key);
            if (src.Properties.GetValueOrDefault("owner") is not { Length: > 0 } owner)
            {
                review.Hold(ReviewLog.NotApplied, "schema", src.Name, null,
                    $"the target's schema has another owner ({Target(src)!.Properties.GetValueOrDefault("owner")}); the source's is the default one, so nothing is changed", Constraints.Name);
                continue;
            }
            if (roles is null || !roles.Contains(owner))
            {
                review.Hold(ReviewLog.NotApplied, "schema", src.Name, null,
                    $"its owner is {owner} on the source, and that role {(roles is null ? "could not be looked up" : "does not exist")} on the target", Constraints.Name);
                continue;
            }
            Constraints.Files.Add(new PgFile($"schema_{src.Name}", Header(Constraints, "schema owner", src.Name) +
                $"ALTER SCHEMA {PgDdl.Q(src.Name)} OWNER TO {PgDdl.Q(owner)};\n", NameOf(src)));
        }
    }

    // ------------------------------------------------------------------ 09: column names that differ only by letter case

    /// <summary>Table key -> (source column -> the target's column of the same name in another letter case) that 09 renames.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> renames = new(StringComparer.Ordinal);

    /// <summary>Table key -> (source column -> the target's look-alike column) held back, so neither is added nor dropped.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> lookAlikes = new(StringComparer.Ordinal);

    /// <summary>
    /// On a table both sides have, a column only the source has paired one to one with a column only the target has
    /// whose name differs only by letter case (09 renames it, keeping its values), or that looks like the same name
    /// (letter case and underscores aside: created_by and "CreatedBy"), which is held so its values are never lost to an
    /// add and a drop.
    /// </summary>
    private void PlanRenames()
    {
        static string Fold(string name) => name.Replace("_", "").ToLowerInvariant();
        foreach (var (src, tgt) in ChangedTables())
        {
            var srcOnly = src.Columns.Where(c => tgt.Columns.All(x => x.Name != c.Name)).ToList();
            var tgtOnly = tgt.Columns.Where(c => src.Columns.All(x => x.Name != c.Name)).ToList();
            foreach (var c in srcOnly)
            {
                var theirs = tgtOnly.Where(x => Fold(x.Name) == Fold(c.Name)).ToList();
                if (theirs.Count != 1 || srcOnly.Count(x => Fold(x.Name) == Fold(c.Name)) != 1) continue;
                var other = theirs[0].Name;
                if (string.Equals(other, c.Name, StringComparison.OrdinalIgnoreCase) && Selected(ScriptSection.Formatting) && !IsChild(src))
                {
                    (renames.TryGetValue(src.Key, out var map) ? map : renames[src.Key] = new(StringComparer.Ordinal))[c.Name] = other;
                    FormattingTables.Files.Add(new PgFile($"rename_{src.Schema}.{src.Name}.{c.Name}", Header(FormattingTables, "column", $"{src.FullName}.{other}") +
                        $"-- The target's column {PgDdl.OneLine(other)} is the source's {PgDdl.OneLine(c.Name)} in another letter case; renaming keeps its values.\n" +
                        "-- Code that names it in the old spelling must be changed with it: PostgreSQL names are exact.\n" +
                        $"ALTER TABLE {PgDdl.Q(src)} RENAME COLUMN {PgDdl.Q(other)} TO {PgDdl.Q(c.Name)};\n", new ObjectName(src.Schema, src.Name)));
                }
                else (lookAlikes.TryGetValue(src.Key, out var map) ? map : lookAlikes[src.Key] = new(StringComparer.Ordinal))[c.Name] = other;
            }
        }
    }

    /// <summary>Why 01 does not add <paramref name="column"/> of <paramref name="table"/>: 09 renames the target's twin, or a look-alike is held; else null.</summary>
    private string? RenameReason(DbObject table, string column)
    {
        if (renames.GetValueOrDefault(table.Key)?.GetValueOrDefault(column) is { } twin)
            return $"the target has it as {twin}, in another letter case: 09_formatting_tables renames that column, keeping its values";
        if (lookAlikes.GetValueOrDefault(table.Key)?.GetValueOrDefault(column) is { } like)
            return LookAlike(table, like, column);
        return null;
    }

    private static string LookAlike(DbObject table, string theirs, string ours) =>
        $"the target has column {theirs} where the source has {ours}, most likely the same column under another spelling. Adding {ours} and " +
        $"dropping {theirs} would lose its values, so neither is scripted: rename it by hand if it is the same column " +
        $"(ALTER TABLE {PgDdl.Q(table)} RENAME COLUMN {PgDdl.Q(theirs)} TO {PgDdl.Q(ours)})";

    /// <summary>The target-only column <paramref name="column"/> is 09's rename or a held look-alike: the reason it is not dropped, or null.</summary>
    private string? KeptTargetColumn(DbObject table, string column)
    {
        if (renames.GetValueOrDefault(table.Key)?.FirstOrDefault(kv => kv.Value == column) is { Key: not null } r)
            return $"09_formatting_tables renames it to {r.Key}";
        if (lookAlikes.GetValueOrDefault(table.Key)?.FirstOrDefault(kv => kv.Value == column) is { Key: not null } l)
            return LookAlike(table, column, l.Key);
        return null;
    }
}
