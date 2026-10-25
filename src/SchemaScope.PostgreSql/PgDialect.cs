using System.Text;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.PostgreSql;

/// <summary>
/// PostgreSQL text rules. There is no PostgreSQL parser in this build, so <see cref="CanDeepParse"/> is false and
/// <see cref="ParsesIdentically"/> always says no: "same code, written differently" is never claimed on a guess. The
/// loose form behind "only formatting differs" (<see cref="PgText.Loosen"/>) keeps literals and quoted names exact.
/// </summary>
public sealed class PgDialect : ISqlDialect
{
    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;

    /// <summary>The catalog already holds each name as PostgreSQL folded it: "Users" and users are two tables.</summary>
    public IdentifierFold Fold => IdentifierFold.Exact;

    public string DefaultSchema => "public";

    /// <summary>The reader always leaves these out (with pg_temp_* and pg_toast_temp_*); listed here for the options screen.</summary>
    public IReadOnlyList<string> DefaultExcludedSchemas { get; } = ["pg_catalog", "information_schema", "pg_toast"];

    public string Quote(string identifier) => PgCanonicalWriter.Q(identifier);
    public string LightNormalize(string body) => PgText.Light(body);
    public string CanonicalHeader(string body, DbObject o) => body;
    public string StripComments(string body) => PgText.StripComments(body);
    public string Loosen(string text) => PgText.Loosen(text);
    public bool CanDeepParse => false;
    public bool ParsesIdentically(string a, string b) => false;
    public string CanonicalScript(DbObject o, CompareOptions opt) => PgCanonicalWriter.Write(o, opt);
    public string MonacoLanguage => "pgsql";

    /// <summary>Monaco's default separators less $, which may be part of a PostgreSQL name.</summary>
    public string WordSeparators => "`~!@#%^&*()-=+[{]}\\|;:'\",.<>/?";
}

/// <summary>
/// Structural PostgreSQL objects (tables, sequences, types, schemas, extensions) as one deterministic script, built
/// from catalog facts and the server's own deparse (format_type, pg_get_expr, pg_get_indexdef, pg_get_constraintdef).
/// Both sides come out of this same code, so a text difference is a real difference.
/// </summary>
public static partial class PgCanonicalWriter
{
    /// <summary>Marks an exclusion constraint among a table's <see cref="DbObject.Indexes"/>.</summary>
    public const string ExcludeConstraint = "EXCLUDE";

    /// <summary>A table's row security policies sit in its <see cref="DbObject.Properties"/> under this prefix and their name.</summary>
    public const string PolicyPrefix = "policy:";

    /// <summary>A table's rules (CREATE RULE) sit in its <see cref="DbObject.Properties"/> under this prefix and their name.</summary>
    public const string RulePrefix = "rule:";

    /// <summary>A foreign table's column options (OPTIONS (...) of one column) sit in its <see cref="DbObject.Properties"/> under this prefix and the column's name.</summary>
    public const string ColumnOptionsPrefix = "columnOptions:";

    /// <summary>A table's extended statistics (CREATE STATISTICS) sit in its <see cref="DbObject.Properties"/> under this prefix and schema.name.</summary>
    public const string StatisticsPrefix = "statistics:";

    /// <summary>A text search configuration's mappings sit in its <see cref="DbObject.Properties"/> under this prefix and the token type.</summary>
    public const string MappingPrefix = "mapping:";

    /// <summary>
    /// What an object needs that SchemaScope does not read or create (a text search parser or template of the user's own,
    /// which is C code), under this prefix: never part of its text, read by the scripts to hold it with why.
    /// </summary>
    public const string NeedsPrefix = "needs:";

    public static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Write(DbObject o, CompareOptions opt) => o.Type switch
    {
        DbObjectType.TextSearchDictionary => WriteTextSearchDictionary(o),
        DbObjectType.TextSearchConfiguration => WriteTextSearchConfiguration(o),
        DbObjectType.OperatorClass => WriteOperatorClass(o),
        DbObjectType.Cast => WriteCast(o),
        DbObjectType.Table => WriteTable(o, opt),
        DbObjectType.Sequence => WriteSequence(o),
        DbObjectType.Schema => WriteSchema(o),
        DbObjectType.Domain => WriteDomain(o, opt),
        DbObjectType.EnumType => $"CREATE TYPE {Q(o.Schema)}.{Q(o.Name)} AS ENUM ({o.Properties.GetValueOrDefault("labels")});",
        DbObjectType.CompositeType => WriteComposite(o, opt),
        DbObjectType.Extension => WriteExtension(o),
        DbObjectType.RangeType => WriteRange(o),
        DbObjectType.Collation => WriteCollation(o),
        DbObjectType.Operator => WriteOperator(o),
        DbObjectType.ForeignTable => WriteForeignTable(o, opt),
        _ => o.Definition ?? ""
    };

    /// <summary>
    /// CREATE FOREIGN TABLE with its columns (each with its own OPTIONS), checks, server and options, and where it hangs
    /// (PARTITION OF or INHERITS). Its rows live on the other server and are never compared.
    /// </summary>
    public static string WriteForeignTable(DbObject o, CompareOptions opt)
    {
        var p = o.Properties;
        var lines = o.Columns.OrderBy(c => c.Ordinal).Select(c => "    " + ColumnLine(c, o, opt) +
            (p.GetValueOrDefault(ColumnOptionsPrefix + c.Name) is { Length: > 0 } options ? $" OPTIONS ({options})" : "")).ToList();
        lines.AddRange(o.Constraints.Where(x => x.Kind == ConstraintKind.Check).OrderBy(x => Named(x.IsSystemNamed, opt) ? x.Name : x.Definition, StringComparer.Ordinal)
            .Select(ck => "    " + (Named(ck.IsSystemNamed, opt) ? $"CONSTRAINT {Q(ck.Name)} " : "") + ck.Definition + (ck.IsNotTrusted ? " NOT VALID" : "")));
        var sb = new StringBuilder($"CREATE FOREIGN TABLE {Q(o.Schema)}.{Q(o.Name)} (\n{string.Join(",\n", lines)}\n)");
        if (p.GetValueOrDefault("inherits") is { Length: > 0 } inherits) sb.Append($" INHERITS ({inherits})");
        sb.Append($" SERVER {p.GetValueOrDefault("server")}");
        if (p.GetValueOrDefault("options") is { Length: > 0 } tableOptions) sb.Append($" OPTIONS ({tableOptions})");
        sb.Append(';');
        if (p.GetValueOrDefault("partitionOf") is { Length: > 0 } parent)
            sb.Append($"\nALTER TABLE ONLY {parent} ATTACH PARTITION {Q(o.Schema)}.{Q(o.Name)} {p.GetValueOrDefault("partitionBound")};");
        return sb.ToString();
    }

    /// <summary>
    /// CREATE OPERATOR with its function, argument types, commutator, negator, estimators, HASHES and MERGES. The name
    /// is written schema.op, as CREATE and DROP OPERATOR take it (an operator's own name is never quoted).
    /// </summary>
    public static string WriteOperator(DbObject o)
    {
        var p = o.Properties;
        var options = new List<string> { $"FUNCTION = {p.GetValueOrDefault("function")}" };
        if (p.GetValueOrDefault("leftArg") is { Length: > 0 } left) options.Add($"LEFTARG = {left}");
        if (p.GetValueOrDefault("rightArg") is { Length: > 0 } right) options.Add($"RIGHTARG = {right}");
        if (p.GetValueOrDefault("commutator") is { Length: > 0 } commutator) options.Add($"COMMUTATOR = {commutator}");
        if (p.GetValueOrDefault("negator") is { Length: > 0 } negator) options.Add($"NEGATOR = {negator}");
        if (p.GetValueOrDefault("restrict") is { Length: > 0 } restrict) options.Add($"RESTRICT = {restrict}");
        if (p.GetValueOrDefault("join") is { Length: > 0 } join) options.Add($"JOIN = {join}");
        if (p.ContainsKey("hashes")) options.Add("HASHES");
        if (p.ContainsKey("merges")) options.Add("MERGES");
        return $"CREATE OPERATOR {Q(o.Schema)}.{o.Name} ({string.Join(", ", options)});";
    }

    /// <summary>CREATE TEXT SEARCH DICTIONARY with its template and its options as PostgreSQL keeps them.</summary>
    public static string WriteTextSearchDictionary(DbObject o)
    {
        var p = o.Properties;
        var options = p.GetValueOrDefault("options") is { Length: > 0 } init ? $", {init}" : "";
        return $"CREATE TEXT SEARCH DICTIONARY {Q(o.Schema)}.{Q(o.Name)} (TEMPLATE = {p.GetValueOrDefault("template")}{options});";
    }

    /// <summary>CREATE TEXT SEARCH CONFIGURATION with its parser, then one ADD MAPPING a token type, in token order.</summary>
    public static string WriteTextSearchConfiguration(DbObject o)
    {
        var name = $"{Q(o.Schema)}.{Q(o.Name)}";
        var sb = new StringBuilder($"CREATE TEXT SEARCH CONFIGURATION {name} (PARSER = {o.Properties.GetValueOrDefault("parser")});");
        foreach (var (key, dicts) in o.Properties.Where(kv => kv.Key.StartsWith(MappingPrefix, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append($"\nALTER TEXT SEARCH CONFIGURATION {name} ADD MAPPING FOR {key[MappingPrefix.Length..]} WITH {dicts};");
        return sb.ToString();
    }

    /// <summary>
    /// CREATE OPERATOR CLASS as pg_dump writes it: DEFAULT, the type and access method, the family when it is not the
    /// class's own, each operator and support function bound to it, STORAGE when it is not the type.
    /// </summary>
    public static string WriteOperatorClass(DbObject o)
    {
        var p = o.Properties;
        var items = (p.GetValueOrDefault("items") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (p.GetValueOrDefault("storage") is { Length: > 0 } storage) items.Add($"STORAGE {storage}");
        return $"CREATE OPERATOR CLASS {Q(o.Schema)}.{Q(o.Name)} {(p.ContainsKey("default") ? "DEFAULT " : "")}FOR TYPE {p.GetValueOrDefault("type")} " +
               $"USING {Q(p.GetValueOrDefault("method") ?? "")}{(p.GetValueOrDefault("family") is { Length: > 0 } family ? $" FAMILY {family}" : "")} AS\n    " +
               string.Join(",\n    ", items) + ";";
    }

    /// <summary>CREATE CAST with its function (or WITH INOUT, WITHOUT FUNCTION) and when it applies.</summary>
    public static string WriteCast(DbObject o)
    {
        var p = o.Properties;
        var how = p.GetValueOrDefault("method") switch
        {
            "INOUT" => "WITH INOUT",
            "BINARY" => "WITHOUT FUNCTION",
            _ => $"WITH FUNCTION {p.GetValueOrDefault("function")}",
        };
        var when = p.GetValueOrDefault("context") switch { "ASSIGNMENT" => " AS ASSIGNMENT", "IMPLICIT" => " AS IMPLICIT", _ => "" };
        return $"CREATE CAST ({p.GetValueOrDefault("source")} AS {p.GetValueOrDefault("target")}) {how}{when};";
    }

    private static string WriteSchema(DbObject o)
    {
        var owner = o.Properties.GetValueOrDefault("owner");
        return string.IsNullOrEmpty(owner) ? $"CREATE SCHEMA {Q(o.Name)};" : $"CREATE SCHEMA {Q(o.Name)} AUTHORIZATION {Q(owner)};";
    }

    private static string WriteExtension(DbObject o) =>
        $"CREATE EXTENSION {Q(o.Name)} WITH SCHEMA {Q(o.Properties.GetValueOrDefault("schema") ?? "")} VERSION '{o.Properties.GetValueOrDefault("version")?.Replace("'", "''")}';";

    /// <summary>CREATE TYPE ... AS RANGE with every option that is set (the multirange's name only when it is not the default).</summary>
    public static string WriteRange(DbObject o)
    {
        var p = o.Properties;
        var options = new List<string> { $"SUBTYPE = {p.GetValueOrDefault("subtype")}" };
        if (p.GetValueOrDefault("subtypeOpClass") is { Length: > 0 } opclass) options.Add($"SUBTYPE_OPCLASS = {opclass}");
        if (p.GetValueOrDefault("collation") is { Length: > 0 } collation) options.Add($"COLLATION = {collation}");
        if (p.GetValueOrDefault("canonical") is { Length: > 0 } canonical) options.Add($"CANONICAL = {canonical}");
        if (p.GetValueOrDefault("subtypeDiff") is { Length: > 0 } diff) options.Add($"SUBTYPE_DIFF = {diff}");
        if (p.GetValueOrDefault("multirange") is { Length: > 0 } multi) options.Add($"MULTIRANGE_TYPE_NAME = {Q(o.Schema)}.{Q(multi)}");
        return $"CREATE TYPE {Q(o.Schema)}.{Q(o.Name)} AS RANGE ({string.Join(", ", options)});";
    }

    /// <summary>CREATE COLLATION with its provider, locale (or lc_collate and lc_ctype for libc), determinism and ICU rules.</summary>
    public static string WriteCollation(DbObject o)
    {
        static string Lit(string? v) => "'" + (v ?? "").Replace("'", "''") + "'";
        var p = o.Properties;
        var options = new List<string> { $"provider = {p.GetValueOrDefault("provider")}" };
        if (p.TryGetValue("locale", out var locale)) options.Add($"locale = {Lit(locale)}");
        if (p.TryGetValue("lc_collate", out var collate)) options.Add($"lc_collate = {Lit(collate)}");
        if (p.TryGetValue("lc_ctype", out var ctype)) options.Add($"lc_ctype = {Lit(ctype)}");
        if (p.GetValueOrDefault("deterministic") == "false") options.Add("deterministic = false");
        if (p.TryGetValue("rules", out var rules)) options.Add($"rules = {Lit(rules)}");
        return $"CREATE COLLATION {Q(o.Schema)}.{Q(o.Name)} ({string.Join(", ", options)});";
    }

    private static string WriteSequence(DbObject o)
    {
        var p = o.Properties;
        var sb = new StringBuilder($"CREATE SEQUENCE {Q(o.Schema)}.{Q(o.Name)}");
        sb.Append($" AS {p.GetValueOrDefault("type")}");
        sb.Append($" START WITH {p.GetValueOrDefault("startValue")}");
        sb.Append($" INCREMENT BY {p.GetValueOrDefault("increment")}");
        sb.Append($" MINVALUE {p.GetValueOrDefault("minValue")}");
        sb.Append($" MAXVALUE {p.GetValueOrDefault("maxValue")}");
        sb.Append($" CACHE {p.GetValueOrDefault("cacheSize")}");
        sb.Append(p.GetValueOrDefault("isCycling") == "1" ? " CYCLE" : " NO CYCLE");
        if (p.GetValueOrDefault("ownedBy") is { Length: > 0 } owner) sb.Append($" OWNED BY {owner}");
        return sb.Append(';').ToString();
    }

    private static string WriteDomain(DbObject o, CompareOptions opt)
    {
        var p = o.Properties;
        var sb = new StringBuilder($"CREATE DOMAIN {Q(o.Schema)}.{Q(o.Name)} AS {p.GetValueOrDefault("baseType")}");
        if (!opt.IgnoreCollation && p.GetValueOrDefault("collation") is { Length: > 0 } collation) sb.Append($" COLLATE {collation}");
        if (p.GetValueOrDefault("default") is { Length: > 0 } def) sb.Append($" DEFAULT {def}");
        if (p.GetValueOrDefault("notNull") == "1") sb.Append(" NOT NULL");
        foreach (var c in o.Constraints.Where(c => c.Kind == ConstraintKind.Check).OrderBy(c => c.Name, StringComparer.Ordinal))
            sb.Append($"\n    CONSTRAINT {Q(c.Name)} {c.Definition}{(c.IsNotTrusted ? " NOT VALID" : "")}");
        return sb.Append(';').ToString();
    }

    private static string WriteComposite(DbObject o, CompareOptions opt)
    {
        var columns = o.Columns.OrderBy(c => c.Ordinal)
            .Select(c => $"    {Q(c.Name)} {c.DataType}{(!opt.IgnoreCollation && !string.IsNullOrEmpty(c.Collation) ? $" COLLATE {c.Collation}" : "")}");
        return $"CREATE TYPE {Q(o.Schema)}.{Q(o.Name)} AS (\n{string.Join(",\n", columns)}\n);";
    }

    private static string WriteTable(DbObject o, CompareOptions opt)
    {
        var p = o.Properties;
        var table = $"{Q(o.Schema)}.{Q(o.Name)}";
        var sb = new StringBuilder(512);
        sb.Append(p.ContainsKey("persistence") ? "CREATE UNLOGGED TABLE " : "CREATE TABLE ").Append(table)
          .Append(p.GetValueOrDefault("ofType") is { Length: > 0 } ofType ? $" OF {ofType}" : "").Append(" (\n");

        var columns = opt.IgnoreColumnOrder
            ? o.Columns.OrderBy(c => c.Name, StringComparer.Ordinal).ToList()
            : o.Columns.OrderBy(c => c.Ordinal).ToList();

        var lines = new List<string>(columns.Count + 8);
        foreach (var c in columns) lines.Add("    " + ColumnLine(c, o, opt));

        // keys and checks live inside the CREATE TABLE body, a default-named one unnamed when told to ignore those
        foreach (var ix in o.Indexes
                     .Where(i => i.IsPrimaryKey || i.IsUniqueConstraint || i.TypeDesc == ExcludeConstraint)
                     .OrderBy(i => i.IsPrimaryKey ? 0 : i.IsUniqueConstraint ? 1 : 2)
                     .ThenBy(i => Named(i.IsSystemNamed, opt) ? i.Name : i.Definition, StringComparer.Ordinal))
            lines.Add("    " + (Named(ix.IsSystemNamed, opt) ? $"CONSTRAINT {Q(ix.Name)} " : "") + KeyText(ix.Definition, opt.IgnoreFillFactor));

        foreach (var ck in o.Constraints
                     .Where(x => x.Kind == ConstraintKind.Check)
                     .OrderBy(x => Named(x.IsSystemNamed, opt) ? x.Name : x.Definition, StringComparer.Ordinal))
            lines.Add("    " + (Named(ck.IsSystemNamed, opt) ? $"CONSTRAINT {Q(ck.Name)} " : "") + ck.Definition +
                      (ck.IsNotTrusted ? " NOT VALID" : ""));

        sb.Append(string.Join(",\n", lines)).Append("\n)");
        if (p.GetValueOrDefault("inherits") is { Length: > 0 } inherits) sb.Append($" INHERITS ({inherits})");
        if (p.GetValueOrDefault("partitionBy") is { Length: > 0 } partitionBy) sb.Append($" PARTITION BY {partitionBy}");
        if (StorageOptions(p.GetValueOrDefault("options"), opt.IgnoreFillFactor) is { Length: > 0 } storage) sb.Append($" WITH ({storage})");
        sb.Append(";\n");

        // a partition: where it hangs and which rows it takes
        if (p.GetValueOrDefault("partitionOf") is { Length: > 0 } parent)
            sb.Append($"ALTER TABLE ONLY {parent} ATTACH PARTITION {table} {p.GetValueOrDefault("partitionBound")};\n");

        foreach (var ix in o.Indexes
                     .Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint && i.TypeDesc != ExcludeConstraint)
                     .OrderBy(i => i.Name, StringComparer.Ordinal))
            sb.Append(IndexText(ix.Definition, opt.IgnoreCollation)).Append(";\n");

        foreach (var fk in o.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append($"ALTER TABLE {Q(o.Schema)}.{Q(o.Name)} ADD CONSTRAINT {Q(fk.Name)} {fk.Definition};\n");

        // row level security: whether it is on, whether it binds the owner too, and every policy
        if (p.ContainsKey("rowSecurity")) sb.Append($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;\n");
        if (p.ContainsKey("forceRowSecurity")) sb.Append($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;\n");
        if (p.GetValueOrDefault("replicaIdentity") is { Length: > 0 } replica) sb.Append($"ALTER TABLE {table} REPLICA IDENTITY {replica};\n");
        foreach (var policy in p.Where(kv => kv.Key.StartsWith(PolicyPrefix, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(policy.Value).Append('\n');
        // rules: what an INSERT, UPDATE or DELETE on the table really does
        foreach (var rule in p.Where(kv => kv.Key.StartsWith(RulePrefix, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(rule.Value).Append('\n');
        // extended statistics: what the planner knows of how its columns go together
        foreach (var statistics in p.Where(kv => kv.Key.StartsWith(StatisticsPrefix, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(statistics.Value).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    private static bool Named(bool systemNamed, CompareOptions opt) => !(opt.IgnoreSystemNamedConstraints && systemNamed);

    /// <summary>
    /// An index's pg_get_indexdef as the compare sees it: with collations ignored, without its COLLATE clauses.
    /// pg_get_indexdef writes COLLATE only where the index's collation is not its column's, so with the columns'
    /// collations ignored, the same index reads differently on two sides whose columns differ in collation alone.
    /// </summary>
    public static string IndexText(string? definition, bool ignoreCollation) =>
        ignoreCollation ? WithoutCollate(definition ?? "") : definition ?? "";

    /// <summary>
    /// A key's definition as the compare sees it: with fill factor ignored, without fillfactor in its WITH (...) (the
    /// options of the index behind it), as a table's own fillfactor is left out.
    /// </summary>
    public static string KeyText(string? definition, bool ignoreFillFactor) =>
        ignoreFillFactor ? WithoutFillFactor(definition ?? "") : definition ?? "";

    /// <summary>A key's WITH (...) index options, "name=value" each with the quotes taken off the value; none when it has none.</summary>
    public static Dictionary<string, string> KeyOptions(string? definition)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (KeyWithRx().Match(definition ?? "") is { Success: true } m)
            foreach (var part in m.Groups["o"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)))
                options[part[0]] = part.Length > 1 && part[1].Length >= 2 && part[1][0] == '\'' ? part[1][1..^1].Replace("''", "'") : part.Length > 1 ? part[1] : "";
        return options;
    }

    /// <summary>A key's definition without its WITH (...) index options.</summary>
    public static string WithoutKeyOptions(string? definition) => KeyWithRx().Replace(definition ?? "", "");

    private static string WithoutFillFactor(string definition) => KeyWithRx().Replace(definition, m =>
    {
        var kept = m.Groups["o"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("fillfactor=", StringComparison.Ordinal)).ToList();
        return kept.Count == 0 ? "" : $" WITH ({string.Join(", ", kept)})";
    });

    /// <summary>A key's WITH (...): "(c WITH =)" of an exclusion constraint is no options list, a "(" never follows that WITH.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@" WITH \((?<o>[^()]*)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex KeyWithRx();

    /// <summary>Every COLLATE name (bare, quoted, or schema-qualified) outside literals and quoted names taken out.</summary>
    internal static string WithoutCollate(string text)
    {
        if (!text.Contains("COLLATE", StringComparison.OrdinalIgnoreCase)) return text;
        static bool Ident(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
        var sb = new StringBuilder(text.Length);
        var i = 0;
        // a quoted name or a bare word starting at i, and where it ends
        int Name(int at)
        {
            if (at < text.Length && text[at] == '"')
            {
                for (var j = at + 1; j < text.Length; j++)
                    if (text[j] == '"') { if (j + 1 < text.Length && text[j + 1] == '"') j++; else return j + 1; }
                return text.Length;
            }
            var end = at;
            while (end < text.Length && Ident(text[end])) end++;
            return end;
        }
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                // a literal: '' doubles a quote, and in E'...' a backslash escapes the next character
                var escapes = i > 0 && text[i - 1] is 'E' or 'e' && (i < 2 || !Ident(text[i - 2]));
                var j = i + 1;
                for (; j < text.Length; j++)
                {
                    if (escapes && text[j] == '\\') { j++; continue; }
                    if (text[j] != '\'') continue;
                    if (j + 1 < text.Length && text[j + 1] == '\'') { j++; continue; }
                    break;
                }
                var stop = Math.Min(j + 1, text.Length);
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }
            if (c == '"')
            {
                var stop = Name(i);
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }
            if (string.CompareOrdinal(text, i, "COLLATE", 0, 7) == 0 && (i == 0 || !Ident(text[i - 1])) && (i + 7 >= text.Length || !Ident(text[i + 7])))
            {
                var at = i + 7;
                while (at < text.Length && text[at] == ' ') at++;
                var end = Name(at);
                if (end < text.Length && text[end] == '.') end = Name(end + 1);
                if (end > at)
                {
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    i = end;
                    continue;
                }
            }
            if (Ident(c))
            {
                var stop = i;
                while (stop < text.Length && Ident(text[stop])) stop++;
                sb.Append(text, i, stop - i);
                i = stop;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A table's WITH (...) options as the reader keeps them ("a=1, b=2", in order), fillfactor left out when
    /// <paramref name="ignoreFillFactor"/>: the compare does not see it then, while what 03 creates keeps it.
    /// </summary>
    public static string StorageOptions(string? options, bool ignoreFillFactor) =>
        string.Join(", ", (options ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !(ignoreFillFactor && x.StartsWith("fillfactor=", StringComparison.Ordinal))));

    private static string ColumnLine(ColumnInfo c, DbObject o, CompareOptions opt)
    {
        var sb = new StringBuilder(96);
        sb.Append(Q(c.Name)).Append(' ').Append(c.DataType);

        if (!opt.IgnoreCollation && !string.IsNullOrEmpty(c.Collation)) sb.Append(" COLLATE ").Append(c.Collation);

        if (c.IsIdentity)
        {
            sb.Append($" GENERATED {c.IdentityKind} AS IDENTITY");
            if (!opt.IgnoreIdentitySeed)
                sb.Append($" (START WITH {c.IdentitySeed} INCREMENT BY {c.IdentityIncrement}{(c.IdentityOptions is { } limits ? " " + limits : "")})");
        }

        if (c.IsComputed) sb.Append($" GENERATED ALWAYS AS ({c.ComputedDefinition}) {(c.IsPersisted ? "STORED" : "VIRTUAL")}");

        // PostgreSQL 18: a NOT NULL that is NOT VALID still lets the rows already there hold NULLs
        sb.Append(c.NotNullNotValid ? " NOT NULL NOT VALID" : c.IsNullable ? " NULL" : " NOT NULL");

        var def = o.Constraints.FirstOrDefault(x => x.Kind == ConstraintKind.Default && x.ColumnName == c.Name);
        if (def is not null) sb.Append(" DEFAULT ").Append(def.Definition);
        return sb.ToString();
    }
}
