namespace SchemaScope.Scripting;

/// <summary>What can depend on a column, for the ALTER TABLE … ALTER COLUMN rules (Msg 5074 / 4922).</summary>
public enum ColumnDependentKind { PrimaryKey, Unique, ForeignKey, Index, Check, Default, Computed, Statistics, SchemaBound }

/// <summary>One thing that depends on a column, e.g. (Index, "index IX_Orders_Date").</summary>
public sealed record ColumnDependent(ColumnDependentKind Kind, string Name);

/// <summary>
/// A column dependent the catalog rows do not carry - user-created statistics and schema-bound views or
/// functions - read by <see cref="TargetProbes.ColumnDependentsAsync"/>.
/// </summary>
public sealed record CatalogColumnDependent(ObjectName Table, string Column, ColumnDependentKind Kind, string Name);

/// <summary>
/// Everything in one database that depends on each column, and the rule that says which of them make SQL
/// Server refuse an ALTER COLUMN. 04 uses it to hold a change back before it is written; the validator uses
/// the same rule on the files, so the two can never disagree.
/// </summary>
/// <remarks>
/// The rule follows the ALTER TABLE documentation:
/// <list type="bullet">
/// <item>Primary keys, foreign keys (either side), computed columns and schema-bound modules block any type change.</item>
/// <item>Indexes, unique constraints, CHECK constraints and user statistics block a type change, except a longer
/// varchar / nvarchar / varbinary of the same type (an index or unique key cannot take a (max) column, so that stays blocked).</item>
/// <item>A DEFAULT blocks only a change of the type name; length, precision and scale may change.</item>
/// <item>A nullability-only change is blocked by computed columns and schema-bound modules. A primary-key column
/// can never become nullable (Msg 8111); callers test that themselves because it is a different message.</item>
/// </list>
/// </remarks>
public sealed class ColumnCover
{
    private readonly Dictionary<ObjectName, Dictionary<string, List<ColumnDependent>>> map;

    private ColumnCover(Dictionary<ObjectName, Dictionary<string, List<ColumnDependent>>> map) => this.map = map;

    /// <summary>Builds the cover of <paramref name="t"/> from its keys, indexes, checks, foreign keys, defaults and
    /// computed columns, plus <paramref name="extra"/> (statistics and schema-bound modules from the probe).</summary>
    public static ColumnCover Build(ScriptSchema t, IEnumerable<CatalogColumnDependent>? extra = null)
    {
        var map = new Dictionary<ObjectName, Dictionary<string, List<ColumnDependent>>>(t.NameComparer);
        void Add(ObjectName table, string column, ColumnDependentKind kind, string name)
        {
            if (!map.TryGetValue(table, out var byCol)) map[table] = byCol = new Dictionary<string, List<ColumnDependent>>(StringComparer.OrdinalIgnoreCase);
            if (!byCol.TryGetValue(column, out var list)) byCol[column] = list = [];
            list.Add(new ColumnDependent(kind, name));
        }
        void AddAll(ObjectName table, string? text, ColumnDependentKind kind, string name)
        {
            foreach (var c in Ddl.BracketedNames(text)) Add(table, c, kind, name);
        }

        foreach (var k in t.Keys)
            AddAll(k.Table, k.Cols, k.KType == "PK" ? ColumnDependentKind.PrimaryKey : ColumnDependentKind.Unique,
                $"{(k.KType == "PK" ? "primary key" : "unique key")} {k.CName}");
        foreach (var i in t.Indexes)
        {
            AddAll(i.Table, i.Cols, ColumnDependentKind.Index, $"index {i.IName}");
            AddAll(i.Table, i.Incl, ColumnDependentKind.Index, $"index {i.IName}");
            AddAll(i.Table, i.FilterDefinition, ColumnDependentKind.Index, $"index {i.IName}");
        }
        foreach (var c in t.Checks) AddAll(c.Table, c.Definition, ColumnDependentKind.Check, $"check {c.CName}");
        foreach (var f in t.FKeys)
        {
            AddAll(f.Table, f.Cols, ColumnDependentKind.ForeignKey, $"foreign key {f.CName}");
            if (f.RTbl.Length > 0) AddAll(f.Referenced, f.RCols, ColumnDependentKind.ForeignKey, $"foreign key {f.CName} on {f.Sch}.{f.Tbl}");
        }
        foreach (var d in t.Defaults) Add(d.Table, d.Col, ColumnDependentKind.Default, $"default {d.CName}");
        foreach (var (table, cols) in t.Columns)
        foreach (var c in cols.Where(c => c.IsComputed))
            AddAll(table, c.ComputedDef, ColumnDependentKind.Computed, $"computed column {c.Col}");
        foreach (var e in extra ?? [])
            Add(e.Table, e.Column, e.Kind, e.Name);
        return new ColumnCover(map);
    }

    /// <summary>Everything that depends on <paramref name="column"/> of <paramref name="table"/> (column name ignoring case).</summary>
    public IReadOnlyList<ColumnDependent> Of(ObjectName table, string column) =>
        map.TryGetValue(table, out var byCol) && byCol.TryGetValue(column, out var list) ? list : [];

    /// <summary>
    /// The names of the dependents that make SQL Server refuse this ALTER COLUMN (sorted, distinct), empty when
    /// it is allowed. See the class remarks for the rule; the primary-key nullability rule is not included.
    /// </summary>
    /// <param name="deps">From <see cref="Of"/>.</param>
    /// <param name="typeChanges">True when the type itself changes (name, length, precision or scale), false for a nullability-only change.</param>
    /// <param name="oldType">The column's current type name, e.g. "varchar".</param>
    /// <param name="newType">The new type name.</param>
    /// <param name="newIsMax">True when the new type is (max).</param>
    public static List<string> Blockers(IEnumerable<ColumnDependent> deps, bool typeChanges, string oldType, string newType, bool newIsMax)
    {
        var sameType = string.Equals(oldType, newType, StringComparison.OrdinalIgnoreCase);
        var variable = sameType && newType.ToLowerInvariant() is "varchar" or "nvarchar" or "varbinary";
        return deps.Where(d => typeChanges
                ? d.Kind switch
                {
                    ColumnDependentKind.Index or ColumnDependentKind.Unique => !variable || newIsMax,
                    ColumnDependentKind.Check or ColumnDependentKind.Statistics => !variable,
                    ColumnDependentKind.Default => !sameType,
                    _ => true,       // primary key, foreign key, computed column, schema-bound module
                }
                : d.Kind is ColumnDependentKind.Computed or ColumnDependentKind.SchemaBound)
            .Select(d => d.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
