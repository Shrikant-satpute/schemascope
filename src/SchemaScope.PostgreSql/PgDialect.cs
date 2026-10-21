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
public static class PgCanonicalWriter
{
    /// <summary>Marks an exclusion constraint among a table's <see cref="DbObject.Indexes"/>.</summary>
    public const string ExcludeConstraint = "EXCLUDE";

    /// <summary>A table's row security policies sit in its <see cref="DbObject.Properties"/> under this prefix and their name.</summary>
    public const string PolicyPrefix = "policy:";

    public static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Write(DbObject o, CompareOptions opt) => o.Type switch
    {
        DbObjectType.Table => WriteTable(o, opt),
        DbObjectType.Sequence => WriteSequence(o),
        DbObjectType.Schema => WriteSchema(o),
        DbObjectType.Domain => WriteDomain(o, opt),
        DbObjectType.EnumType => $"CREATE TYPE {Q(o.Schema)}.{Q(o.Name)} AS ENUM ({o.Properties.GetValueOrDefault("labels")});",
        DbObjectType.CompositeType => WriteComposite(o, opt),
        DbObjectType.Extension => WriteExtension(o),
        _ => o.Definition ?? ""
    };

    private static string WriteSchema(DbObject o)
    {
        var owner = o.Properties.GetValueOrDefault("owner");
        return string.IsNullOrEmpty(owner) ? $"CREATE SCHEMA {Q(o.Name)};" : $"CREATE SCHEMA {Q(o.Name)} AUTHORIZATION {Q(owner)};";
    }

    private static string WriteExtension(DbObject o) =>
        $"CREATE EXTENSION {Q(o.Name)} WITH SCHEMA {Q(o.Properties.GetValueOrDefault("schema") ?? "")} VERSION '{o.Properties.GetValueOrDefault("version")?.Replace("'", "''")}';";

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
        sb.Append(p.ContainsKey("persistence") ? "CREATE UNLOGGED TABLE " : "CREATE TABLE ").Append($"{table} (\n");

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
            lines.Add("    " + (Named(ix.IsSystemNamed, opt) ? $"CONSTRAINT {Q(ix.Name)} " : "") + ix.Definition);

        foreach (var ck in o.Constraints
                     .Where(x => x.Kind == ConstraintKind.Check)
                     .OrderBy(x => Named(x.IsSystemNamed, opt) ? x.Name : x.Definition, StringComparer.Ordinal))
            lines.Add("    " + (Named(ck.IsSystemNamed, opt) ? $"CONSTRAINT {Q(ck.Name)} " : "") + ck.Definition +
                      (ck.IsNotTrusted ? " NOT VALID" : ""));

        sb.Append(string.Join(",\n", lines)).Append("\n)");
        if (p.GetValueOrDefault("inherits") is { Length: > 0 } inherits) sb.Append($" INHERITS ({inherits})");
        if (p.GetValueOrDefault("partitionBy") is { Length: > 0 } partitionBy) sb.Append($" PARTITION BY {partitionBy}");
        if (p.GetValueOrDefault("options") is { Length: > 0 } storage) sb.Append($" WITH ({storage})");
        sb.Append(";\n");

        // a partition: where it hangs and which rows it takes
        if (p.GetValueOrDefault("partitionOf") is { Length: > 0 } parent)
            sb.Append($"ALTER TABLE ONLY {parent} ATTACH PARTITION {table} {p.GetValueOrDefault("partitionBound")};\n");

        foreach (var ix in o.Indexes
                     .Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint && i.TypeDesc != ExcludeConstraint)
                     .OrderBy(i => i.Name, StringComparer.Ordinal))
            sb.Append(ix.Definition).Append(";\n");

        foreach (var fk in o.ForeignKeys.OrderBy(f => f.Name, StringComparer.Ordinal))
            sb.Append($"ALTER TABLE {Q(o.Schema)}.{Q(o.Name)} ADD CONSTRAINT {Q(fk.Name)} {fk.Definition};\n");

        // row level security: whether it is on, whether it binds the owner too, and every policy
        if (p.ContainsKey("rowSecurity")) sb.Append($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;\n");
        if (p.ContainsKey("forceRowSecurity")) sb.Append($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;\n");
        foreach (var policy in p.Where(kv => kv.Key.StartsWith(PolicyPrefix, StringComparison.Ordinal))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(policy.Value).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    private static bool Named(bool systemNamed, CompareOptions opt) => !(opt.IgnoreSystemNamedConstraints && systemNamed);

    private static string ColumnLine(ColumnInfo c, DbObject o, CompareOptions opt)
    {
        var sb = new StringBuilder(96);
        sb.Append(Q(c.Name)).Append(' ').Append(c.DataType);

        if (!opt.IgnoreCollation && !string.IsNullOrEmpty(c.Collation)) sb.Append(" COLLATE ").Append(c.Collation);

        if (c.IsIdentity)
        {
            sb.Append($" GENERATED {c.IdentityKind} AS IDENTITY");
            if (!opt.IgnoreIdentitySeed) sb.Append($" (START WITH {c.IdentitySeed} INCREMENT BY {c.IdentityIncrement})");
        }

        if (c.IsComputed) sb.Append($" GENERATED ALWAYS AS ({c.ComputedDefinition}) {(c.IsPersisted ? "STORED" : "VIRTUAL")}");

        sb.Append(c.IsNullable ? " NULL" : " NOT NULL");

        var def = o.Constraints.FirstOrDefault(x => x.Kind == ConstraintKind.Default && x.ColumnName == c.Name);
        if (def is not null) sb.Append(" DEFAULT ").Append(def.Definition);
        return sb.ToString();
    }
}
