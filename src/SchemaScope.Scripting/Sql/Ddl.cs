using System.Globalization;
using System.Text.RegularExpressions;

namespace SchemaScope.Scripting;

/// <summary>
/// Turns catalog rows into DDL text - a port of ddl.py. Output is byte-identical to the Python
/// for the same rows, including the bare <c>\n</c> inside multi-line statements and
/// <c>ON [PRIMARY]</c>, with one deliberate difference: identifiers this class wraps in
/// brackets have <c>]</c> escaped as <c>]]</c>. The pre-rendered column lists
/// (<c>cols</c>/<c>incl</c>/<c>rcols</c>) are pasted as the catalog query returns them; the query builds them
/// with <c>QUOTENAME</c>, so they arrive already escaped.
/// Module bodies are never generated here - they are copied from sys.sql_modules.definition.
/// </summary>
public static partial class Ddl
{
    private static readonly HashSet<string> FixedLen = new(StringComparer.Ordinal) { "varchar", "char", "varbinary", "binary" };
    private static readonly HashSet<string> UnicodeLen = new(StringComparer.Ordinal) { "nvarchar", "nchar" };
    private static readonly HashSet<string> PrecisionTypes = new(StringComparer.Ordinal) { "decimal", "numeric" };
    private static readonly HashSet<string> ScaleOnly = new(StringComparer.Ordinal) { "datetime2", "time", "datetimeoffset" };

    /// <summary>The DROP keyword per sys.objects type (generate_formatting.py DROPWORD), plus PC/FS/FT/TA for CLR kinds.</summary>
    public static IReadOnlyDictionary<string, string> DropWords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["P"] = "PROCEDURE", ["PC"] = "PROCEDURE",
        ["FN"] = "FUNCTION", ["IF"] = "FUNCTION", ["TF"] = "FUNCTION", ["FS"] = "FUNCTION", ["FT"] = "FUNCTION",
        ["V"] = "VIEW",
        ["TR"] = "TRIGGER", ["TA"] = "TRIGGER",
    };

    /// <summary>The DROP keyword for an otype, or null when the type cannot be dropped this way.</summary>
    public static string? DropWord(string otype) => DropWords.TryGetValue(otype.Trim(), out var w) ? w : null;

    /// <summary>Escapes <c>]</c> as <c>]]</c> for use inside <c>[...]</c>.</summary>
    public static string Esc(string? name) => (name ?? "").Replace("]", "]]");

    /// <summary><c>[name]</c> with <c>]</c> escaped.</summary>
    public static string Bracket(string? name) => "[" + Esc(name) + "]";

    /// <summary><c>[schema].[name]</c> with <c>]</c> escaped.</summary>
    public static string Bracket(string schema, string name) => Bracket(schema) + "." + Bracket(name);

    /// <summary>
    /// The bracketed names in a pre-rendered list or a definition, in order, as used by uses_blocked /
    /// has_duplicates / check_flow. The Python's regex was <c>\[([^\]]+)\]</c>; this one also reads an escaped
    /// <c>]]</c> inside the brackets and returns the real name (<c>[a]]b]</c> gives <c>a]b</c>), so a name with a
    /// <c>]</c> is still matched against the catalog. Same result for every name without one. Null or empty gives
    /// an empty list.
    /// </summary>
    public static List<string> BracketedNames(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (Match m in BracketedRx().Matches(text)) list.Add(m.Groups[1].Value.Replace("]]", "]"));
        return list;
    }

    [GeneratedRegex(@"\[((?:[^\]]|\]\])+)\]")]
    private static partial Regex BracketedRx();

    /// <summary>
    /// Port of <c>ddl.qtype</c> for a column row. A user-defined (alias or CLR) type is written with its schema,
    /// <c>[s].[type]</c>: the Python wrote <c>[type]</c>, which SQL Server looks up in dbo only (Msg 2715 for a type in
    /// another schema). Same text as before for every system type.
    /// </summary>
    public static string QType(IColumnDef c, string? dbCollation)
    {
        var t = QType(c.Dt, c.MaxLength, c.Precision, c.Scale, c.CollationName, dbCollation);
        return string.IsNullOrEmpty(c.TypeSchema) ? t : Bracket(c.TypeSchema) + "." + t;
    }

    /// <summary>
    /// Port of <c>ddl.qtype</c>: the bracketed type with its length/precision/scale, then
    /// <c> COLLATE x</c> only when the column has a collation that differs from
    /// <paramref name="dbCollation"/>. 04 calls this with an overridden collation (the
    /// "bare" type passes null; target_type passes the target column's collation).
    /// </summary>
    public static string QType(string dt, int maxLength, int precision, int scale, string? collationName, string? dbCollation)
    {
        var t = dt.ToLowerInvariant();
        string output;
        if (FixedLen.Contains(t))
            output = $"[{Esc(dt)}]({(maxLength == -1 ? "max" : I(maxLength))})";
        else if (UnicodeLen.Contains(t))
            output = $"[{Esc(dt)}]({(maxLength == -1 ? "max" : I(FloorDiv(maxLength, 2)))})";
        else if (PrecisionTypes.Contains(t))
            output = $"[{Esc(dt)}]({I(precision)}, {I(scale)})";
        else if (ScaleOnly.Contains(t))
            output = $"[{Esc(dt)}]({I(scale)})";
        else if (t == "float")
            output = precision == 53 ? "[float]" : $"[float]({I(precision)})";
        else
            output = $"[{Esc(dt)}]";
        if (!string.IsNullOrEmpty(collationName) && collationName != dbCollation)
            output += " COLLATE " + collationName;
        return output;
    }

    /// <summary>
    /// Port of <c>ddl.column</c>: one column definition. Computed: <c>[c] AS expr[ PERSISTED[ NOT NULL]]</c>.
    /// Otherwise <c>[c] type[ IDENTITY(s,i)[ NOT FOR REPLICATION]] NULL|NOT NULL[ default]</c>, where the
    /// default is <c>DEFAULT x</c> when system-named, else <c>CONSTRAINT [n] DEFAULT x</c>.
    /// </summary>
    public static string Column(IColumnDef c, string? dbCollation, DefaultRow? @default = null)
    {
        if (c.IsComputed)
        {
            var output = $"[{Esc(c.Col)}] AS {c.ComputedDef}";
            if (c.IsPersisted)
            {
                output += " PERSISTED";
                if (!c.IsNullable) output += " NOT NULL";
            }
            return output;
        }
        var parts = new List<string> { $"[{Esc(c.Col)}]", QType(c, dbCollation) };
        if (c.IsIdentity)
        {
            parts.Add($"IDENTITY({Py(c.Seed)},{Py(c.Incr)})");
            if (c.IdentNfr) parts.Add("NOT FOR REPLICATION");
        }
        parts.Add(c.IsNullable ? "NULL" : "NOT NULL");
        if (@default is not null)
        {
            parts.Add(@default.IsSystemNamed
                ? $"DEFAULT {@default.Definition}"
                : $"CONSTRAINT [{Esc(@default.CName)}] DEFAULT {@default.Definition}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Port of <c>ddl._named</c>: "" for a system-named constraint, else <c>CONSTRAINT [name] </c>.</summary>
    public static string Named(INamedConstraint r) => r.IsSystemNamed ? "" : $"CONSTRAINT [{Esc(r.CName)}] ";

    /// <summary>
    /// Port of <c>ddl.create_table</c>. <paramref name="defaults"/> maps the source column name to its default
    /// (Python looked it up with the exact column name). Keys are sorted by (ktype, cols), checks by
    /// definition, both ordinally. Foreign keys and indexes are never inline.
    /// </summary>
    public static string CreateTable(
        string sch, string tbl, IEnumerable<IColumnDef> cols, IReadOnlyDictionary<string, DefaultRow> defaults,
        IEnumerable<KeyRow> keys, IEnumerable<CheckRow> checks, string? dbCollation)
    {
        var body = cols.Select(c => Column(c, dbCollation, defaults.TryGetValue(c.Col, out var d) ? d : null)).ToList();
        foreach (var k in keys.OrderBy(r => r.KType, StringComparer.Ordinal).ThenBy(r => r.Cols, StringComparer.Ordinal))
            body.Add($"{Named(k)}{(k.KType == "PK" ? "PRIMARY KEY" : "UNIQUE")} {k.TypeDesc} ({k.Cols})");
        foreach (var ck in checks.OrderBy(r => r.Definition, StringComparer.Ordinal))
            body.Add($"{Named(ck)}CHECK {ck.Definition}");
        return $"CREATE TABLE [{Esc(sch)}].[{Esc(tbl)}](\n    {string.Join(",\n    ", body)}\n) ON [PRIMARY]";
    }

    /// <summary>
    /// Port of <c>ddl.create_type</c>: the columns, then PRIMARY KEY / UNIQUE per table-type index. Added, so a
    /// rebuilt or rolled-back type is the type it was: a column's DEFAULT (table-type constraints cannot be named),
    /// a non-unique index as an inline <c>INDEX [name]</c>, and the type's CHECK constraints. The Python wrote none
    /// of them; the text is the Python's for a type that has none.
    /// </summary>
    public static string CreateType(string sch, string tt, IEnumerable<IColumnDef> cols, IEnumerable<TtIndexRow> idxs, string? dbCollation,
        IEnumerable<TtCheckRow>? checks = null)
    {
        var body = cols.Select(c => Column(c, dbCollation,
            c is TtColumnRow { DefaultDef: { } d } ? new DefaultRow(sch, tt, c.Col, "", d, IsSystemNamed: true) : null)).ToList();
        foreach (var i in idxs)
        {
            if (i.IsPrimaryKey) body.Add($"PRIMARY KEY {i.TypeDesc} ({Py(i.Cols)})");
            else if (i.IsUnique) body.Add($"UNIQUE {i.TypeDesc} ({Py(i.Cols)})");
            else if (!string.IsNullOrEmpty(i.IName)) body.Add($"INDEX [{Esc(i.IName)}] {i.TypeDesc} ({Py(i.Cols)})");
        }
        foreach (var ck in checks ?? []) body.Add($"CHECK {ck.Definition}");
        return $"CREATE TYPE [{Esc(sch)}].[{Esc(tt)}] AS TABLE(\n    {string.Join(",\n    ", body)}\n)";
    }

    /// <summary><c>CREATE SCHEMA [s] AUTHORIZATION [dbo]</c> - owned by dbo, whoever runs it. Must start its batch.</summary>
    public static string CreateSchema(string schema) => $"CREATE SCHEMA [{Esc(schema)}] AUTHORIZATION [dbo]";

    /// <summary><c>CREATE TYPE [s].[n] FROM base NULL|NOT NULL</c> for an alias type.</summary>
    public static string CreateAliasType(AliasTypeRow r) =>
        $"CREATE TYPE [{Esc(r.Sch)}].[{Esc(r.Name)}] FROM {QType(r.BaseType, r.MaxLength, r.Precision, r.Scale, null, null)} {(r.IsNullable ? "NULL" : "NOT NULL")}";

    /// <summary><c>CREATE SEQUENCE</c> with the source's type, start, increment, bounds, cycle and cache.</summary>
    public static string CreateSequence(SequenceRow r)
    {
        var cache = !r.IsCached ? "NO CACHE" : r.CacheSize is { } n ? $"CACHE {I(n)}" : "CACHE";
        return $"CREATE SEQUENCE [{Esc(r.Sch)}].[{Esc(r.Name)}] AS {QType(r.Type, 0, r.Precision, r.Scale, null, null)}" +
               $" START WITH {r.Start} INCREMENT BY {r.Increment} MINVALUE {r.Min} MAXVALUE {r.Max}" +
               $" {(r.IsCycling ? "CYCLE" : "NO CYCLE")} {cache}";
    }

    /// <summary><c>CREATE SYNONYM [s].[n] FOR base</c>; the base name is pasted as sys.synonyms holds it (bracketed by SQL Server).</summary>
    public static string CreateSynonym(SynonymRow r) => $"CREATE SYNONYM [{Esc(r.Sch)}].[{Esc(r.Name)}] FOR {r.BaseObject}";

    /// <summary>Port of <c>ddl.add_column</c>: <c>ALTER TABLE [s].[t] ADD {column}</c>.</summary>
    public static string AddColumn(string sch, string tbl, IColumnDef c, string? dbCollation, DefaultRow? @default = null) =>
        $"ALTER TABLE [{Esc(sch)}].[{Esc(tbl)}] ADD {Column(c, dbCollation, @default)}";

    /// <summary>Port of <c>ddl.add_default</c>: <c>ALTER TABLE [s].[t] ADD {named}DEFAULT x FOR [col]</c>.</summary>
    public static string AddDefault(DefaultRow r) =>
        $"ALTER TABLE [{Esc(r.Sch)}].[{Esc(r.Tbl)}] ADD {Named(r)}DEFAULT {r.Definition} FOR [{Esc(r.Col)}]";

    /// <summary>Port of <c>ddl.add_check</c>: <c>WITH NOCHECK</c> iff the source check is not trusted.</summary>
    /// <remarks>
    /// Like the Python, this ignores <see cref="CheckRow.IsDisabled"/>: <c>WITH NOCHECK ADD</c> leaves the
    /// constraint untrusted but ENABLED, so the target would start enforcing a rule the source does not.
    /// For a disabled source constraint, follow the ADD with <see cref="DisableConstraint"/>; a system-named
    /// one cannot be named in that statement, so send it to review instead. Same for <see cref="AddForeignKey"/>.
    /// </remarks>
    public static string AddCheck(CheckRow r) =>
        $"ALTER TABLE [{Esc(r.Sch)}].[{Esc(r.Tbl)}] WITH {(r.IsNotTrusted ? "NOCHECK" : "CHECK")} ADD {Named(r)}CHECK {r.Definition}";

    /// <summary>Port of <c>ddl.add_key</c>.</summary>
    public static string AddKey(KeyRow r) =>
        $"ALTER TABLE [{Esc(r.Sch)}].[{Esc(r.Tbl)}] ADD {Named(r)}{(r.KType == "PK" ? "PRIMARY KEY" : "UNIQUE")} {r.TypeDesc} ({r.Cols})";

    /// <summary>Port of <c>ddl.add_foreign_key</c> (bare <c>\n</c> before REFERENCES and each tail clause).</summary>
    public static string AddForeignKey(ForeignKeyRow r)
    {
        var tail = "";
        if (r.OnDel != "NO_ACTION") tail += "\nON DELETE " + r.OnDel.Replace('_', ' ');
        if (r.OnUpd != "NO_ACTION") tail += "\nON UPDATE " + r.OnUpd.Replace('_', ' ');
        if (r.Nfr) tail += "\nNOT FOR REPLICATION";
        return $"ALTER TABLE [{Esc(r.Sch)}].[{Esc(r.Tbl)}] WITH {(r.IsNotTrusted ? "NOCHECK" : "CHECK")} ADD {Named(r)}FOREIGN KEY ({r.Cols})\n" +
               $"REFERENCES [{Esc(r.RSch)}].[{Esc(r.RTbl)}] ({r.RCols}){tail}";
    }

    /// <summary>Port of <c>ddl.trust_constraint</c> (unused by the Python).</summary>
    public static string TrustConstraint(string sch, string tbl, string cname, bool isNotTrusted) =>
        $"ALTER TABLE [{Esc(sch)}].[{Esc(tbl)}] {(isNotTrusted ? "NOCHECK" : "CHECK")} CONSTRAINT [{Esc(cname)}]";

    /// <summary>
    /// <c>ALTER TABLE [s].[t] NOCHECK CONSTRAINT [c]</c>: disables a check or foreign key (same text as
    /// <see cref="TrustConstraint"/> with isNotTrusted). Emit it right after <see cref="AddCheck"/> /
    /// <see cref="AddForeignKey"/> when the source constraint <c>IsDisabled</c>, so the target ends up as the source is.
    /// </summary>
    public static string DisableConstraint(INamedConstraint r) => TrustConstraint(r.Sch, r.Tbl, r.CName, isNotTrusted: true);

    /// <summary>
    /// Why <see cref="CreateIndex"/> must not be used for this source index, or null when it can.
    /// <see cref="CreateIndex"/> only writes valid DDL for rowstore CLUSTERED / NONCLUSTERED indexes with key
    /// columns; a hypothetical (tuning advisor) index is not a real index, and a disabled one would be created enabled.
    /// Generators send every non-null answer to review.
    /// </summary>
    public static string? UnscriptableIndexReason(IndexRow r) =>
        r.IsHypothetical ? "hypothetical index (Database Engine Tuning Advisor) - not a real index"
        : r.TypeDesc is not ("CLUSTERED" or "NONCLUSTERED") ? $"{r.TypeDesc} index - not scripted, create it by hand"
        : string.IsNullOrEmpty(r.Cols) ? "index has no key columns - not scripted"
        : r.IsDisabled ? "disabled on the source - creating it would make it enabled"
        : null;

    /// <summary>True when <see cref="UnscriptableIndexReason"/> is null.</summary>
    public static bool IsScriptableIndex(IndexRow r) => UnscriptableIndexReason(r) is null;

    /// <summary>
    /// Port of <c>ddl.create_index</c>:
    /// <c>CREATE [UNIQUE ]{type_desc} INDEX [i] ON [s].[t] (cols)[\nINCLUDE (incl)][\nWHERE filter]\nON [PRIMARY]</c>.
    /// Only rowstore CLUSTERED / NONCLUSTERED give valid DDL: check <see cref="UnscriptableIndexReason"/> first.
    /// </summary>
    public static string CreateIndex(IndexRow r)
    {
        var output = $"CREATE {(r.IsUnique ? "UNIQUE " : "")}{r.TypeDesc} INDEX [{Esc(r.IName)}] ON [{Esc(r.Sch)}].[{Esc(r.Tbl)}] ({Py(r.Cols)})";
        if (!string.IsNullOrEmpty(r.Incl)) output += $"\nINCLUDE ({r.Incl})";
        if (!string.IsNullOrEmpty(r.FilterDefinition)) output += $"\nWHERE {r.FilterDefinition}";
        return output + "\nON [PRIMARY]";
    }

    /// <summary>
    /// 04's statement: <c>ALTER TABLE [s].[t] ALTER COLUMN [col] {type} NULL|NOT NULL</c>
    /// (generate.py builds it inline; the shape is what check_scripts §5 parses).
    /// </summary>
    public static string AlterColumn(string sch, string tbl, string col, string type, bool nullable) =>
        $"ALTER TABLE [{Esc(sch)}].[{Esc(tbl)}] ALTER COLUMN [{Esc(col)}] {type} {(nullable ? "NULL" : "NOT NULL")}";

    // '%s' % None renders "None" in Python; kept so a malformed catalog row produces the same text.
    private static string Py(string? s) => s ?? "None";

    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
