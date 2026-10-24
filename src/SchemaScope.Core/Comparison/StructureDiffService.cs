using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.Core.Comparison;

public sealed class ColumnFacet
{
    public string Type { get; set; } = "";
    public string Nullable { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Default { get; set; } = "";
    public string Computed { get; set; } = "";
    public string Collation { get; set; } = "";
    public int Ordinal { get; set; }
}

public sealed class StructureRow
{
    public string Name { get; set; } = "";
    /// <summary>same | changed | missingInTarget | onlyInTarget</summary>
    public string Status { get; set; } = "same";
    public string? LeftText { get; set; }
    public string? RightText { get; set; }
    /// <summary>Column level detail. Null for index / key / constraint rows.</summary>
    public ColumnFacet? Left { get; set; }
    public ColumnFacet? Right { get; set; }
    /// <summary>Which fields differ, so the UI can highlight just those cells.</summary>
    public List<string> ChangedFields { get; set; } = [];
}

public sealed class StructureSection
{
    public string Title { get; set; } = "";
    public List<StructureRow> Rows { get; set; } = [];
    public int ChangedCount => Rows.Count(r => r.Status != "same");
}

/// <summary>
/// Field level compare for tables and table types.
///
/// A text diff of a CREATE TABLE script is hard to read: one renamed column
/// lights up the whole block. A grid shows exactly which column and which
/// property moved, which is what people actually want to know.
///
/// Names and text compare as the engine that read them does: ignoring case for SQL Server, exactly for an engine
/// whose catalog keeps names as spelled (PostgreSQL: "Users" and users are two columns, 'A' and 'a' two defaults).
/// </summary>
public static class StructureDiffService
{
    public static List<StructureSection> Build(DbObject? source, DbObject? target, CompareOptions options)
    {
        var exact = (source ?? target)?.Fold is IdentifierFold.Exact;
        var names = exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var sections = new List<StructureSection>
        {
            BuildColumns(source, target, options, exact),
            BuildNamed("Indexes & keys",
                Render(source?.Indexes, options, names), Render(target?.Indexes, options, names), names),
            BuildNamed("Foreign keys",
                Render(source?.ForeignKeys, names), Render(target?.ForeignKeys, names), names),
            BuildNamed("Check constraints",
                RenderChecks(source?.Constraints, names, exact), RenderChecks(target?.Constraints, names, exact), names)
        };
        // PostgreSQL: what a table is beyond its columns and keys (UNLOGGED, partitioning, row security, policies, ...)
        if (exact)
            sections.Add(BuildNamed("Table options", TableFacts(source, options), TableFacts(target, options), names));

        return [.. sections.Where(s => s.Rows.Count > 0)];
    }

    private static StructureSection BuildColumns(DbObject? source, DbObject? target, CompareOptions options, bool exact)
    {
        var section = new StructureSection { Title = "Columns" };

        var left = source?.Columns ?? [];
        var right = target?.Columns ?? [];

        var names = exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var rightByName = right.ToDictionary(c => c.Name, names);
        var seen = new HashSet<string>(names);

        foreach (var l in left.OrderBy(c => c.Ordinal))
        {
            seen.Add(l.Name);
            var lf = Facet(l, source!, options);

            if (!rightByName.TryGetValue(l.Name, out var r))
            {
                // Covers both "this column is gone" and "the whole table is gone".
                // In the second case the user still wants to see what would be created.
                section.Rows.Add(new StructureRow
                {
                    Name = l.Name,
                    Status = "missingInTarget",
                    Left = lf
                });
                continue;
            }

            var rf = Facet(r, target!, options);
            var changed = CompareFacets(lf, rf, options, exact);

            section.Rows.Add(new StructureRow
            {
                Name = l.Name,
                Status = changed.Count == 0 ? "same" : "changed",
                Left = lf,
                Right = rf,
                ChangedFields = changed
            });
        }

        foreach (var r in right.Where(c => !seen.Contains(c.Name)).OrderBy(c => c.Ordinal))
            section.Rows.Add(new StructureRow
            {
                Name = r.Name,
                Status = "onlyInTarget",
                Right = Facet(r, target!, options)
            });

        return section;
    }

    private static List<string> CompareFacets(ColumnFacet a, ColumnFacet b, CompareOptions options, bool exact)
    {
        var changed = new List<string>();
        if (!Eq(a.Type, b.Type)) changed.Add("type");
        if (!Eq(a.Nullable, b.Nullable)) changed.Add("nullable");
        if (!Eq(a.Identity, b.Identity)) changed.Add("identity");
        // an expression's literals keep their case where the engine's do
        if (!Text(a.Default, b.Default)) changed.Add("default");
        if (!Text(a.Computed, b.Computed)) changed.Add("computed");
        if (!options.IgnoreCollation && !Text(a.Collation, b.Collation)) changed.Add("collation");
        if (!options.IgnoreColumnOrder && a.Ordinal != b.Ordinal) changed.Add("ordinal");
        return changed;

        static bool Eq(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        bool Text(string x, string y) => string.Equals(x, y, exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static ColumnFacet Facet(ColumnInfo c, DbObject owner, CompareOptions options)
    {
        var names = owner.Fold is IdentifierFold.Exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var def = owner.Constraints.FirstOrDefault(x =>
            x.Kind == ConstraintKind.Default &&
            string.Equals(x.ColumnName, c.Name, names));

        return new ColumnFacet
        {
            Ordinal = c.Ordinal,
            Type = c.DataType,
            Nullable = c.IsNullable ? "NULL" : "NOT NULL",
            Identity = !c.IsIdentity ? ""
                : c.IdentityKind is { } kind
                    // PostgreSQL: GENERATED ALWAYS / BY DEFAULT AS IDENTITY
                    ? options.IgnoreIdentitySeed ? $"GENERATED {kind} AS IDENTITY"
                        : $"GENERATED {kind} AS IDENTITY({c.IdentitySeed},{c.IdentityIncrement}{(c.IdentityOptions is { } limits ? ", " + limits : "")})"
                    : options.IgnoreIdentitySeed ? "IDENTITY" : $"IDENTITY({c.IdentitySeed},{c.IdentityIncrement})",
            Default = def?.Definition ?? "",
            Computed = c.IsComputed ? $"AS {c.ComputedDefinition}{(c.IsPersisted ? " PERSISTED" : "")}" : "",
            Collation = c.Collation ?? ""
        };
    }

    private static StructureSection BuildNamed(
        string title,
        Dictionary<string, string> left,
        Dictionary<string, string> right,
        StringComparer comparer)
    {
        var section = new StructureSection { Title = title };
        var names = new SortedSet<string>(left.Keys, comparer);
        names.UnionWith(right.Keys);
        var text = comparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var name in names)
        {
            var hasL = left.TryGetValue(name, out var l);
            var hasR = right.TryGetValue(name, out var r);

            var status = hasL && hasR
                ? string.Equals(l, r, text) ? "same" : "changed"
                : hasL ? "missingInTarget" : "onlyInTarget";

            section.Rows.Add(new StructureRow
            {
                Name = name,
                Status = status,
                LeftText = l,
                RightText = r
            });
        }

        return section;
    }

    // fillfactor is left out of a table's options when the compare ignores it, as the compare's own text leaves it out
    private static Dictionary<string, string> TableFacts(DbObject? o, CompareOptions options) =>
        o is { Type: DbObjectType.Table }
            ? o.Properties.Where(kv => kv.Value is not null)
                .Select(kv => kv.Key == "options" && options.IgnoreFillFactor
                    ? new KeyValuePair<string, string?>(kv.Key, string.Join(", ", kv.Value!.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                        .Where(x => !x.StartsWith("fillfactor=", StringComparison.Ordinal))))
                    : kv)
                .Where(kv => kv.Value!.Length > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static Dictionary<string, string> Render(List<IndexInfo>? indexes, CompareOptions options, StringComparer names)
    {
        var map = new Dictionary<string, string>(names);
        if (indexes is null) return map;

        foreach (var ix in indexes)
        {
            if (ix.Definition is { } own)
            {
                // the engine's own text (PostgreSQL), keyed as below: a default-named key by kind and columns
                var ownKind = ix.IsPrimaryKey ? "PRIMARY KEY" : ix.IsUniqueConstraint ? "UNIQUE" : ix.TypeDesc;
                var ownCols = string.Join(", ", ix.KeyColumns.OrderBy(k => k.Ordinal).Select(k => k.Name));
                map[options.IgnoreSystemNamedConstraints && ix.IsSystemNamed ? $"{ownKind} ({ownCols})" : ix.Name] = own;
                continue;
            }

            var kind = ix.IsPrimaryKey ? "PRIMARY KEY" : ix.IsUniqueConstraint ? "UNIQUE" : ix.TypeDesc;
            var cols = string.Join(", ", ix.KeyColumns.OrderBy(k => k.Ordinal)
                .Select(k => k.Name + (k.Descending ? " DESC" : "")));
            var inc = ix.IncludedColumns.Count == 0
                ? ""
                : $" INCLUDE ({string.Join(", ", ix.IncludedColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))})";
            var filter = string.IsNullOrEmpty(ix.FilterDefinition) ? "" : $" WHERE {ix.FilterDefinition}";
            var ff = options.IgnoreFillFactor || ix.FillFactor == 0 ? "" : $" FILLFACTOR={ix.FillFactor}";
            var uq = ix.IsUnique && !ix.IsPrimaryKey && !ix.IsUniqueConstraint ? "UNIQUE " : "";
            // System-named PK/UQ names are random per server - match those by kind + columns.
            var key = options.IgnoreSystemNamedConstraints && ix.IsSystemNamed ? $"{kind} ({cols})" : ix.Name;
            map[key] = $"{uq}{kind} ({cols}){inc}{filter}{ff}{(ix.IsDisabled ? " [disabled]" : "")}";
        }

        return map;
    }

    private static Dictionary<string, string> Render(List<ForeignKeyInfo>? fks, StringComparer names)
    {
        var map = new Dictionary<string, string>(names);
        if (fks is null) return map;

        foreach (var fk in fks)
        {
            if (fk.Definition is { } own)
            {
                map[fk.Name] = own;
                continue;
            }

            var onDelete = fk.DeleteAction == "NO_ACTION" ? "" : $" ON DELETE {fk.DeleteAction.Replace('_', ' ')}";
            var onUpdate = fk.UpdateAction == "NO_ACTION" ? "" : $" ON UPDATE {fk.UpdateAction.Replace('_', ' ')}";
            map[fk.Name] =
                $"({string.Join(", ", fk.Columns)}) -> {fk.ReferencedSchema}.{fk.ReferencedTable} " +
                $"({string.Join(", ", fk.ReferencedColumns)}){onDelete}{onUpdate}" +
                (fk.IsDisabled ? " [disabled]" : "") + (fk.IsNotTrusted ? " [not trusted]" : "");
        }

        return map;
    }

    private static Dictionary<string, string> RenderChecks(List<ConstraintInfo>? constraints, StringComparer names, bool exact)
    {
        var map = new Dictionary<string, string>(names);
        if (constraints is null) return map;

        foreach (var c in constraints.Where(c => c.Kind == ConstraintKind.Check))
            map[c.Name] = c.Definition + (c.IsDisabled ? " [disabled]" : "") + (exact && c.IsNotTrusted ? " NOT VALID" : "");

        return map;
    }
}
