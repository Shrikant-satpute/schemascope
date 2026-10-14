namespace SchemaScope.Scripting;

/// <summary>
/// A schema-qualified name. Equality on the struct itself is exact (ordinal), because the
/// emitted DDL must use one exact spelling; collections in <see cref="ScriptSchema"/> are
/// built with <see cref="ScriptSchema.NameComparer"/> - <see cref="IgnoreCase"/> on a CI database, because SQL Server's CI collations treat
/// <c>dbo.Foo</c> and <c>dbo.FOO</c> as one object.
/// </summary>
public readonly record struct ObjectName(string Schema, string Name) : IComparable<ObjectName>
{
    /// <summary>Case-insensitive equality for dictionaries and sets keyed by object.</summary>
    public static IEqualityComparer<ObjectName> IgnoreCase { get; } = new IgnoreCaseComparer();

    /// <summary>Exact (ordinal) equality - for a database with a case-sensitive or binary collation, where <c>dbo.Foo</c> and <c>dbo.FOO</c> are two objects.</summary>
    public static IEqualityComparer<ObjectName> Exact { get; } = EqualityComparer<ObjectName>.Default;

    /// <summary>
    /// The order Python's <c>sorted()</c> gives a <c>(sch, name)</c> tuple: schema, then name,
    /// both by code point. Every "sorted" in the Python generator means this order.
    /// </summary>
    public static IComparer<ObjectName> Ordinal { get; } = Comparer<ObjectName>.Create((a, b) => a.CompareTo(b));

    /// <inheritdoc cref="Ordinal"/>
    public int CompareTo(ObjectName other)
    {
        var c = string.CompareOrdinal(Schema, other.Schema);
        return c != 0 ? c : string.CompareOrdinal(Name, other.Name);
    }

    /// <summary><c>[schema].[name]</c> with <c>]</c> escaped, ready to drop into DDL.</summary>
    public string Bracketed => $"[{Ddl.Esc(Schema)}].[{Ddl.Esc(Name)}]";

    /// <summary><c>schema.name</c> - the display form used in reports, manifests and FormattingCandidates.</summary>
    public override string ToString() => $"{Schema}.{Name}";

    /// <summary>True when both parts match ignoring case.</summary>
    public bool SameAs(ObjectName other) =>
        string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    private sealed class IgnoreCaseComparer : IEqualityComparer<ObjectName>
    {
        public bool Equals(ObjectName x, ObjectName y) => x.SameAs(y);

        public int GetHashCode(ObjectName o) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(o.Schema ?? ""),
            StringComparer.OrdinalIgnoreCase.GetHashCode(o.Name ?? ""));
    }
}

/// <summary>A row that belongs to a table: lets one <see cref="ScriptSchema.ByTable{T}"/> group any of them.</summary>
public interface ITableRow
{
    /// <summary>Schema of the parent table (SQL alias <c>sch</c>).</summary>
    string Sch { get; }
    /// <summary>Parent table (SQL alias <c>tbl</c>).</summary>
    string Tbl { get; }
}

/// <summary>A named constraint: what <c>ddl._named</c> needs.</summary>
public interface INamedConstraint : ITableRow
{
    /// <summary>Constraint name (alias <c>cname</c>).</summary>
    string CName { get; }
    /// <summary>True when SQL Server made the name up; such names are never emitted.</summary>
    bool IsSystemNamed { get; }
}

/// <summary>
/// The column shape <see cref="Ddl.QType(IColumnDef, string?)"/> and <see cref="Ddl.Column"/> read.
/// Table columns and table-type columns both implement it; table-type columns are never
/// computed and carry no NOT FOR REPLICATION flag.
/// </summary>
public interface IColumnDef
{
    /// <summary>Column name (alias <c>col</c>).</summary>
    string Col { get; }
    /// <summary><c>TYPE_NAME(user_type_id)</c>, case as SQL Server returned it (alias <c>dt</c>).</summary>
    string Dt { get; }
    /// <summary>Bytes; -1 for (max). nvarchar/nchar are twice the character count.</summary>
    int MaxLength { get; }
    int Precision { get; }
    int Scale { get; }
    bool IsNullable { get; }
    bool IsIdentity { get; }
    bool IsComputed { get; }
    /// <summary>Column collation, null for non-text columns.</summary>
    string? CollationName { get; }
    /// <summary>Computed column expression, null unless computed.</summary>
    string? ComputedDef { get; }
    bool IsPersisted { get; }
    /// <summary>Identity seed as <c>CONVERT(varchar(40), seed_value)</c>; null unless identity.</summary>
    string? Seed { get; }
    /// <summary>Identity increment, same form as <see cref="Seed"/>.</summary>
    string? Incr { get; }
    /// <summary>Identity is NOT FOR REPLICATION.</summary>
    bool IdentNfr { get; }
    /// <summary>
    /// The schema of a user-defined (alias or CLR) column type, null for a system type. An alias type outside
    /// dbo must be written <c>[schema].[type]</c>; a bare <c>[type]</c> would be looked up in dbo (Msg 2715).
    /// </summary>
    string? TypeSchema => null;
}

/// <summary>A row of <c>Q_MODULES</c>: one procedure, function, view or trigger.</summary>
/// <param name="Definition">
/// <c>sys.sql_modules.definition</c> byte for byte. Null for an encrypted module - such a module
/// can never be scripted and must go to review.
/// </param>
public sealed record ModuleRow(string Sch, string Obj, string OType, bool AnsiNulls, bool QuotedId, string? Definition)
{
    /// <summary>The module's key.</summary>
    public ObjectName Name => new(Sch, Obj);
}

/// <summary>A row of <c>Q_COLUMNS</c>.</summary>
public sealed record ColumnRow(
    string Sch, string Tbl, int ColumnId, string Col, string Dt, int MaxLength, int Precision, int Scale,
    bool IsNullable, bool IsIdentity, bool IsComputed, string? CollationName, string? ComputedDef,
    bool IsPersisted, string? Seed, string? Incr, bool IdentNfr, string? TypeSchema = null) : ITableRow, IColumnDef
{
    /// <summary>The parent table.</summary>
    public ObjectName Table => new(Sch, Tbl);
}

/// <summary>A row of <c>Q_DEFAULTS</c>.</summary>
public sealed record DefaultRow(string Sch, string Tbl, string Col, string CName, string Definition, bool IsSystemNamed)
    : INamedConstraint
{
    public ObjectName Table => new(Sch, Tbl);
}

/// <summary>A row of <c>Q_CHECKS</c>.</summary>
public sealed record CheckRow(
    string Sch, string Tbl, string CName, string Definition, bool IsSystemNamed, bool IsNotTrusted, bool IsDisabled)
    : INamedConstraint
{
    public ObjectName Table => new(Sch, Tbl);
}

/// <summary>A row of <c>Q_KEYS</c>. <see cref="KType"/> is <c>PK</c> or <c>UQ</c>; <see cref="Cols"/> is pre-rendered, e.g. <c>[A] ASC, [B] DESC</c>.</summary>
public sealed record KeyRow(string Sch, string Tbl, string CName, string KType, bool IsSystemNamed, string TypeDesc, string Cols)
    : INamedConstraint
{
    public ObjectName Table => new(Sch, Tbl);
}

/// <summary>
/// A row of <c>Q_FKEYS</c>. <see cref="Cols"/>/<see cref="RCols"/> are pre-rendered <c>[A], [B]</c>.
/// <see cref="OnDel"/>/<see cref="OnUpd"/> are the *_referential_action_desc values (<c>NO_ACTION</c>, <c>CASCADE</c>, ...).
/// <see cref="RSch"/>/<see cref="RTbl"/> are "" if OBJECT_SCHEMA_NAME/OBJECT_NAME returned NULL (no permission).
/// </summary>
public sealed record ForeignKeyRow(
    string Sch, string Tbl, string CName, bool IsSystemNamed, string RSch, string RTbl, string OnDel, string OnUpd,
    bool IsDisabled, bool IsNotTrusted, bool Nfr, string Cols, string RCols) : INamedConstraint
{
    public ObjectName Table => new(Sch, Tbl);
    /// <summary>The referenced table.</summary>
    public ObjectName Referenced => new(RSch, RTbl);
}

/// <summary>
/// A row of <c>Q_INDEXES</c> (not PK/UQ constraints, not heaps). <see cref="Cols"/> is null when the index has
/// no key columns (columnstore); <see cref="Incl"/> is null when nothing is included.
/// </summary>
/// <param name="IsHypothetical">A Database Engine Tuning Advisor placeholder, not a real index (added column; meta.py never looked).</param>
/// <param name="IsDisabled">The index is disabled (added column; meta.py never looked).</param>
public sealed record IndexRow(
    string Sch, string Tbl, string IName, string TypeDesc, bool IsUnique, string? FilterDefinition, string? Cols, string? Incl,
    bool IsHypothetical = false, bool IsDisabled = false)
    : ITableRow
{
    public ObjectName Table => new(Sch, Tbl);
}

/// <summary>A row of <c>Q_TT_COLUMNS</c>: a column of a user table type (key alias <c>tt</c>).</summary>
/// <param name="DefaultDef">The column's DEFAULT expression (added column; meta.py never read it, so a rebuilt type lost it).</param>
/// <param name="ComputedDef">The computed expression, null unless computed (added column).</param>
/// <param name="IsPersisted">The computed column is PERSISTED (added column).</param>
/// <param name="TypeSchema">See <see cref="IColumnDef.TypeSchema"/> (added column).</param>
public sealed record TtColumnRow(
    string Sch, string Tt, int ColumnId, string Col, string Dt, int MaxLength, int Precision, int Scale,
    bool IsNullable, bool IsIdentity, string? CollationName, string? Seed, string? Incr,
    string? DefaultDef = null, string? ComputedDef = null, bool IsPersisted = false, string? TypeSchema = null) : IColumnDef
{
    public ObjectName Type => new(Sch, Tt);
    public bool IsComputed => ComputedDef is not null;
    bool IColumnDef.IdentNfr => false;
}

/// <summary>A row of <c>Q_TT_INDEXES</c>.</summary>
/// <param name="IName">The index name (added column): a non-unique index is written as an inline <c>INDEX [name]</c>.</param>
public sealed record TtIndexRow(string Sch, string Tt, bool IsPrimaryKey, bool IsUnique, string TypeDesc, string? Cols, string? IName = null)
{
    public ObjectName Type => new(Sch, Tt);
}

/// <summary>A CHECK constraint of a user table type (added query; meta.py never read them, so a rebuilt type lost them).</summary>
public sealed record TtCheckRow(string Sch, string Tt, string Definition)
{
    public ObjectName Type => new(Sch, Tt);
}

/// <summary>A row of <c>Q_TT_PARAMS</c>: module <c>sch.obj</c> takes table type <c>tsch.tt</c> as a parameter.</summary>
/// <remarks>
/// meta.py did not select the type's schema and matched <c>(sch, tt)</c> - the MODULE's schema - against
/// the drifted types, which is wrong when a module takes a type from another schema. <see cref="TSch"/>
/// (an added column) is the type's own schema: match on <see cref="Type"/>. For the full blocker set
/// (body references too) see <see cref="CatalogProbes.TypeReferencersAsync"/>.
/// </remarks>
public sealed record TtParamRow(string Tt, string Sch, string Obj, string TSch)
{
    /// <summary>The module that has the parameter.</summary>
    public ObjectName Module => new(Sch, Obj);
    /// <summary>The table type, with its own schema.</summary>
    public ObjectName Type => new(TSch, Tt);
}

/// <summary>One name respelled by <see cref="ScriptSchema.AlignCase"/>.</summary>
public sealed record CaseRename(string Old, string New);

/// <summary>
/// The catalog snapshot the generator works from - a port of <c>meta.Schema</c>. It holds the
/// same collections, filled by the same queries (<see cref="ScriptCatalogReader"/>), keyed with
/// <see cref="NameComparer"/>: case-insensitive for a CI database, exact for a CS/BIN one (where
/// <c>dbo.Foo</c> and <c>dbo.FOO</c> are two tables and must not be merged). Treat it as read-only;
/// only <see cref="AlignCase"/> changes it.
/// </summary>
public sealed class ScriptSchema
{
    private Dictionary<ObjectName, ModuleRow> modules = new(ObjectName.IgnoreCase);
    private HashSet<ObjectName> tables = new(ObjectName.IgnoreCase);
    private Dictionary<ObjectName, List<ColumnRow>> columns = new(ObjectName.IgnoreCase);
    private List<DefaultRow> defaults = [];
    private List<CheckRow> checks = [];
    private List<KeyRow> keys = [];
    private List<ForeignKeyRow> fkeys = [];
    private List<IndexRow> indexes = [];
    private HashSet<ObjectName> tableTypes = new(ObjectName.IgnoreCase);
    private Dictionary<ObjectName, List<TtColumnRow>> ttColumns = new(ObjectName.IgnoreCase);
    private List<TtIndexRow> ttIndexes = [];
    private List<TtCheckRow> ttChecks = [];
    private List<TtParamRow> ttParams = [];
    private List<CaseRename> caseRenames = [];

    /// <summary>Database name the snapshot was read from (display only).</summary>
    public string Database { get; init; } = "";

    /// <summary>The database default collation (<c>Q_COLLATION</c>).</summary>
    public string Collation { get; init; } = "";

    /// <summary>True when the database default collation is case-insensitive (<c>_CI_</c>); AlignCase is only meaningful then.</summary>
    public bool IsCaseInsensitive => Collation.Contains("_CI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How this database matches object names: <see cref="ObjectName.IgnoreCase"/> when <see cref="IsCaseInsensitive"/>,
    /// else <see cref="ObjectName.Exact"/>. Every collection of the snapshot uses it; a generator building its own
    /// set or dictionary of this database's names should too (e.g. <c>ByTable(rows, schema.NameComparer)</c>).
    /// </summary>
    public IEqualityComparer<ObjectName> NameComparer => IsCaseInsensitive ? ObjectName.IgnoreCase : ObjectName.Exact;

    /// <summary>String form of <see cref="NameComparer"/>, for column, constraint and index names.</summary>
    public StringComparer NameStringComparer => IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Every non-shipped module with a sys.sql_modules row (meta: <c>modules</c>). Key = (sch, obj), case-insensitive.</summary>
    public IReadOnlyDictionary<ObjectName, ModuleRow> Modules => modules;

    /// <summary>User tables (meta: <c>tables</c>).</summary>
    public IReadOnlySet<ObjectName> Tables => tables;

    /// <summary>Columns per table, each list in column_id order (meta: <c>columns</c>).</summary>
    public IReadOnlyDictionary<ObjectName, List<ColumnRow>> Columns => columns;

    /// <summary>Default constraints (meta: <c>defaults</c>), in query order.</summary>
    public IReadOnlyList<DefaultRow> Defaults => defaults;

    /// <summary>Check constraints (meta: <c>checks</c>).</summary>
    public IReadOnlyList<CheckRow> Checks => checks;

    /// <summary>PRIMARY KEY / UNIQUE constraints (meta: <c>keys</c>).</summary>
    public IReadOnlyList<KeyRow> Keys => keys;

    /// <summary>Foreign keys (meta: <c>fkeys</c>).</summary>
    public IReadOnlyList<ForeignKeyRow> FKeys => fkeys;

    /// <summary>Indexes that are not PK/UQ constraints and not heaps (meta: <c>indexes</c>).</summary>
    public IReadOnlyList<IndexRow> Indexes => indexes;

    /// <summary>User table types (meta: <c>tabletypes</c>).</summary>
    public IReadOnlySet<ObjectName> TableTypes => tableTypes;

    /// <summary>Columns per table type, in column_id order (meta: <c>tt_columns</c>).</summary>
    public IReadOnlyDictionary<ObjectName, List<TtColumnRow>> TtColumns => ttColumns;

    /// <summary>Table-type PK/UQ indexes (meta: <c>tt_indexes</c>).</summary>
    public IReadOnlyList<TtIndexRow> TtIndexes => ttIndexes;

    /// <summary>Table-type CHECK constraints (not in meta.py).</summary>
    public IReadOnlyList<TtCheckRow> TtChecks => ttChecks;

    /// <summary>Which modules take which table type as a parameter (meta: <c>tt_params</c>).</summary>
    public IReadOnlyList<TtParamRow> TtParams => ttParams;

    /// <summary>The renames the last <see cref="AlignCase"/> made, sorted ordinally (meta: <c>case_renames</c>).</summary>
    public IReadOnlyList<CaseRename> CaseRenames => caseRenames;

    /// <summary>Builds a snapshot from rows. <see cref="ScriptCatalogReader"/> is the normal caller.</summary>
    public static ScriptSchema FromRows(
        string database, string collation,
        IEnumerable<ModuleRow> modules, IEnumerable<ObjectName> tables, IEnumerable<ColumnRow> columns,
        IEnumerable<DefaultRow> defaults, IEnumerable<CheckRow> checks, IEnumerable<KeyRow> keys,
        IEnumerable<ForeignKeyRow> fkeys, IEnumerable<IndexRow> indexes, IEnumerable<ObjectName> tableTypes,
        IEnumerable<TtColumnRow> ttColumns, IEnumerable<TtIndexRow> ttIndexes, IEnumerable<TtParamRow> ttParams,
        IEnumerable<TtCheckRow>? ttChecks = null)
    {
        var s = new ScriptSchema { Database = database, Collation = collation };
        var cmp = s.NameComparer;
        s.modules = new(cmp);
        s.tables = new(cmp);
        s.columns = new(cmp);
        s.tableTypes = new(cmp);
        s.ttColumns = new(cmp);
        foreach (var m in modules) s.modules[m.Name] = m;          // dict: a repeated key keeps the last row
        foreach (var t in tables) s.tables.Add(t);
        foreach (var g in columns.GroupBy(c => c.Table, cmp))
            s.columns[g.Key] = g.OrderBy(c => c.ColumnId).ToList(); // stable, like list.sort
        s.defaults = defaults.ToList();
        s.checks = checks.ToList();
        s.keys = keys.ToList();
        s.fkeys = fkeys.ToList();
        s.indexes = indexes.ToList();
        foreach (var t in tableTypes) s.tableTypes.Add(t);
        foreach (var g in ttColumns.GroupBy(c => c.Type, cmp))
            s.ttColumns[g.Key] = g.OrderBy(c => c.ColumnId).ToList();
        s.ttIndexes = ttIndexes.ToList();
        s.ttChecks = ttChecks?.ToList() ?? [];
        s.ttParams = ttParams.ToList();
        return s;
    }

    /// <summary>Columns of a table in column_id order, or an empty list when the table is unknown.</summary>
    public IReadOnlyList<ColumnRow> ColumnsOf(ObjectName table) =>
        columns.TryGetValue(table, out var v) ? v : [];

    /// <summary>A table's column by name: the exact spelling first, then (CI database only) ignoring case; null when absent.</summary>
    public ColumnRow? FindColumn(ObjectName table, string column)
    {
        var cols = ColumnsOf(table);
        return cols.FirstOrDefault(c => string.Equals(c.Col, column, StringComparison.Ordinal))
               ?? (IsCaseInsensitive ? cols.FirstOrDefault(c => string.Equals(c.Col, column, StringComparison.OrdinalIgnoreCase)) : null);
    }

    /// <summary>The module, or null.</summary>
    public ModuleRow? FindModule(ObjectName name) => modules.TryGetValue(name, out var m) ? m : null;

    /// <summary>Columns of a table type in column_id order, or empty.</summary>
    public IReadOnlyList<TtColumnRow> ColumnsOfType(ObjectName type) =>
        ttColumns.TryGetValue(type, out var v) ? v : [];

    /// <summary>CHECK constraints of a table type, in query order.</summary>
    public IEnumerable<TtCheckRow> ChecksOfType(ObjectName type) => ttChecks.Where(r => NameComparer.Equals(r.Type, type));

    /// <summary>Indexes (PK, UNIQUE and plain) of a table type, in query order.</summary>
    public IEnumerable<TtIndexRow> IndexesOfType(ObjectName type) => ttIndexes.Where(r => NameComparer.Equals(r.Type, type));

    /// <summary>Tables in Python <c>sorted()</c> order.</summary>
    public List<ObjectName> SortedTables() => tables.Order(ObjectName.Ordinal).ToList();

    /// <summary>Module keys in Python <c>sorted()</c> order.</summary>
    public List<ObjectName> SortedModules() => modules.Keys.Order(ObjectName.Ordinal).ToList();

    /// <summary>Table types in Python <c>sorted()</c> order.</summary>
    public List<ObjectName> SortedTableTypes() => tableTypes.Order(ObjectName.Ordinal).ToList();

    /// <summary>
    /// Port of <c>meta.Schema.by_table</c>: rows grouped by parent table, query order kept inside each group.
    /// <paramref name="comparer"/> defaults to <see cref="ObjectName.IgnoreCase"/>; pass the database's
    /// <see cref="NameComparer"/> so a case-sensitive database keeps <c>Foo</c> and <c>FOO</c> apart.
    /// </summary>
    public static Dictionary<ObjectName, List<T>> ByTable<T>(IEnumerable<T> rows, IEqualityComparer<ObjectName>? comparer = null)
        where T : ITableRow
    {
        var d = new Dictionary<ObjectName, List<T>>(comparer ?? ObjectName.IgnoreCase);
        foreach (var r in rows)
        {
            var k = new ObjectName(r.Sch, r.Tbl);
            if (!d.TryGetValue(k, out var list)) d[k] = list = [];
            list.Add(r);
        }
        return d;
    }

    /// <summary>
    /// Puts each module's catalog name into its CREATE header where sp_rename left the old one
    /// (<see cref="ModuleHeader.WithCatalogName"/>), so every folder that replays a definition creates the object
    /// under the name it has. Call right after reading, before <see cref="AlignCase"/> and <see cref="Clone"/>.
    /// Returns each rewritten module with the name its header had, sorted.
    /// </summary>
    public IReadOnlyList<(ObjectName Module, string Was)> FixRenamedHeaders()
    {
        var fixedHeaders = new List<(ObjectName, string)>();
        foreach (var (k, m) in modules.ToList())
        {
            if (ModuleHeader.WithCatalogName(m) is not { } f) continue;
            modules[k] = m with { Definition = f.Definition };
            fixedHeaders.Add((k, f.Was));
        }
        return fixedHeaders.OrderBy(h => h.Item1, ObjectName.Ordinal).ToList();
    }

    /// <summary>
    /// Port of <c>meta.Schema.align_case</c>: respells every name in this snapshot the way
    /// <paramref name="reference"/> spells it, wherever the two differ only by case.
    /// Called as <c>target.AlignCase(source)</c>. The namespace is one flat map built from the
    /// reference's module, table and table-type keys (schema and name) plus every constraint
    /// <c>cname</c> and index <c>iname</c>; column names are not in it (they are always compared
    /// ignoring case). Returns the (old, new) pairs, sorted.
    /// </summary>
    /// <remarks>
    /// Differences from Python, none of which change a result: module rows and table-type column
    /// rows get their <c>Sch</c>/<c>Obj</c>/<c>Tt</c> respelled too (Python only rewrote their dict keys),
    /// the added <see cref="TtParamRow.TSch"/> is respelled with the rest of its row, and the reference's
    /// tables/types are walked in query order where Python walked a set. Only meaningful when both
    /// databases are case-insensitive; the orchestrator calls it only then.
    /// </remarks>
    public IReadOnlyList<CaseRename> AlignCase(ScriptSchema reference)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? n) { if (!string.IsNullOrEmpty(n)) names[n] = n; }
        foreach (var k in reference.modules.Keys) { names[k.Schema] = k.Schema; names[k.Name] = k.Name; }
        foreach (var k in reference.tables) { names[k.Schema] = k.Schema; names[k.Name] = k.Name; }
        foreach (var k in reference.tableTypes) { names[k.Schema] = k.Schema; names[k.Name] = k.Name; }
        foreach (var r in reference.defaults) Add(r.CName);
        foreach (var r in reference.checks) Add(r.CName);
        foreach (var r in reference.keys) Add(r.CName);
        foreach (var r in reference.fkeys) Add(r.CName);
        foreach (var r in reference.indexes) Add(r.IName);

        var renamed = new HashSet<CaseRename>();
        string Fix(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            if (!names.TryGetValue(n, out var m)) return n;
            if (!string.Equals(m, n, StringComparison.Ordinal)) renamed.Add(new CaseRename(n, m));
            return m;
        }
        ObjectName Key(ObjectName k) => new(Fix(k.Schema), Fix(k.Name));

        var cmp = NameComparer;
        var newModules = new Dictionary<ObjectName, ModuleRow>(cmp);
        foreach (var (k, v) in modules)
        {
            var nk = Key(k);
            newModules[nk] = v with { Sch = nk.Schema, Obj = nk.Name };
        }
        modules = newModules;

        tables = new HashSet<ObjectName>(tables.Select(Key), cmp);
        tableTypes = new HashSet<ObjectName>(tableTypes.Select(Key), cmp);

        var newColumns = new Dictionary<ObjectName, List<ColumnRow>>(cmp);
        foreach (var (k, v) in columns)
            newColumns[Key(k)] = v.Select(r => r with { Sch = Fix(r.Sch), Tbl = Fix(r.Tbl) }).ToList();
        columns = newColumns;

        var newTt = new Dictionary<ObjectName, List<TtColumnRow>>(cmp);
        foreach (var (k, v) in ttColumns)
        {
            var nk = Key(k);
            newTt[nk] = v.Select(r => r with { Sch = nk.Schema, Tt = nk.Name }).ToList();
        }
        ttColumns = newTt;

        defaults = defaults.Select(r => r with { Sch = Fix(r.Sch), Tbl = Fix(r.Tbl), CName = Fix(r.CName) }).ToList();
        checks = checks.Select(r => r with { Sch = Fix(r.Sch), Tbl = Fix(r.Tbl), CName = Fix(r.CName) }).ToList();
        keys = keys.Select(r => r with { Sch = Fix(r.Sch), Tbl = Fix(r.Tbl), CName = Fix(r.CName) }).ToList();
        fkeys = fkeys.Select(r => r with
        {
            Sch = Fix(r.Sch), Tbl = Fix(r.Tbl), RSch = Fix(r.RSch), RTbl = Fix(r.RTbl), CName = Fix(r.CName)
        }).ToList();
        indexes = indexes.Select(r => r with { Sch = Fix(r.Sch), Tbl = Fix(r.Tbl), IName = Fix(r.IName) }).ToList();
        ttIndexes = ttIndexes.Select(r => r with { Sch = Fix(r.Sch), Tt = Fix(r.Tt) }).ToList();
        ttChecks = ttChecks.Select(r => r with { Sch = Fix(r.Sch), Tt = Fix(r.Tt) }).ToList();
        ttParams = ttParams.Select(r => r with { Sch = Fix(r.Sch), Tt = Fix(r.Tt), Obj = Fix(r.Obj), TSch = Fix(r.TSch) }).ToList();

        caseRenames = renamed
            .OrderBy(r => r.Old, StringComparer.Ordinal)
            .ThenBy(r => r.New, StringComparer.Ordinal)
            .ToList();
        return caseRenames;
    }

    /// <summary>
    /// A deep copy: new collections throughout. Rows are immutable records, so sharing them is safe.
    /// The orchestrator keeps one un-aligned copy of the target (RawTarget) for 09's renames.
    /// </summary>
    public ScriptSchema Clone()
    {
        var s = new ScriptSchema { Database = Database, Collation = Collation };
        var cmp = NameComparer;
        s.modules = new Dictionary<ObjectName, ModuleRow>(modules, cmp);
        s.tables = new HashSet<ObjectName>(tables, cmp);
        s.columns = new Dictionary<ObjectName, List<ColumnRow>>(cmp);
        foreach (var (k, v) in columns) s.columns[k] = [.. v];
        s.defaults = [.. defaults];
        s.checks = [.. checks];
        s.keys = [.. keys];
        s.fkeys = [.. fkeys];
        s.indexes = [.. indexes];
        s.tableTypes = new HashSet<ObjectName>(tableTypes, cmp);
        s.ttColumns = new Dictionary<ObjectName, List<TtColumnRow>>(cmp);
        foreach (var (k, v) in ttColumns) s.ttColumns[k] = [.. v];
        s.ttIndexes = [.. ttIndexes];
        s.ttChecks = [.. ttChecks];
        s.ttParams = [.. ttParams];
        s.caseRenames = [.. caseRenames];
        return s;
    }
}
