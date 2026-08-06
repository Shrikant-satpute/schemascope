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
/// </summary>
public static class StructureDiffService
{
    public static List<StructureSection> Build(DbObject? source, DbObject? target, CompareOptions options)
    {
        var sections = new List<StructureSection>
        {
            BuildColumns(source, target, options),
            BuildNamed("Indexes & keys",
                Render(source?.Indexes, options), Render(target?.Indexes, options)),
            BuildNamed("Foreign keys",
                Render(source?.ForeignKeys), Render(target?.ForeignKeys)),
            BuildNamed("Check constraints",
                RenderChecks(source?.Constraints), RenderChecks(target?.Constraints))
        };

        return [.. sections.Where(s => s.Rows.Count > 0)];
    }

    private static StructureSection BuildColumns(DbObject? source, DbObject? target, CompareOptions options)
    {
        var section = new StructureSection { Title = "Columns" };

        var left = source?.Columns ?? [];
        var right = target?.Columns ?? [];

        var rightByName = right.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            var changed = CompareFacets(lf, rf, options);

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

    private static List<string> CompareFacets(ColumnFacet a, ColumnFacet b, CompareOptions options)
    {
        var changed = new List<string>();
        if (!Eq(a.Type, b.Type)) changed.Add("type");
        if (!Eq(a.Nullable, b.Nullable)) changed.Add("nullable");
        if (!Eq(a.Identity, b.Identity)) changed.Add("identity");
        if (!Eq(a.Default, b.Default)) changed.Add("default");
        if (!Eq(a.Computed, b.Computed)) changed.Add("computed");
        if (!options.IgnoreCollation && !Eq(a.Collation, b.Collation)) changed.Add("collation");
        if (!options.IgnoreColumnOrder && a.Ordinal != b.Ordinal) changed.Add("ordinal");
        return changed;

        static bool Eq(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static ColumnFacet Facet(ColumnInfo c, DbObject owner, CompareOptions options)
    {
        var def = owner.Constraints.FirstOrDefault(x =>
            x.Kind == ConstraintKind.Default &&
            string.Equals(x.ColumnName, c.Name, StringComparison.OrdinalIgnoreCase));

        return new ColumnFacet
        {
            Ordinal = c.Ordinal,
            Type = c.DataType,
            Nullable = c.IsNullable ? "NULL" : "NOT NULL",
            Identity = c.IsIdentity
                ? options.IgnoreIdentitySeed ? "IDENTITY" : $"IDENTITY({c.IdentitySeed},{c.IdentityIncrement})"
                : "",
            Default = def?.Definition ?? "",
            Computed = c.IsComputed ? $"AS {c.ComputedDefinition}{(c.IsPersisted ? " PERSISTED" : "")}" : "",
            Collation = c.Collation ?? ""
        };
    }

    private static StructureSection BuildNamed(
        string title,
        Dictionary<string, string> left,
        Dictionary<string, string> right)
    {
        var section = new StructureSection { Title = title };
        var names = new SortedSet<string>(left.Keys, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(right.Keys);

        foreach (var name in names)
        {
            var hasL = left.TryGetValue(name, out var l);
            var hasR = right.TryGetValue(name, out var r);

            var status = hasL && hasR
                ? string.Equals(l, r, StringComparison.OrdinalIgnoreCase) ? "same" : "changed"
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

    private static Dictionary<string, string> Render(List<IndexInfo>? indexes, CompareOptions options)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (indexes is null) return map;

        foreach (var ix in indexes)
        {
            var kind = ix.IsPrimaryKey ? "PRIMARY KEY" : ix.IsUniqueConstraint ? "UNIQUE" : ix.TypeDesc;
            var cols = string.Join(", ", ix.KeyColumns.OrderBy(k => k.Ordinal)
                .Select(k => k.Name + (k.Descending ? " DESC" : "")));
            var inc = ix.IncludedColumns.Count == 0
                ? ""
                : $" INCLUDE ({string.Join(", ", ix.IncludedColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))})";
            var filter = string.IsNullOrEmpty(ix.FilterDefinition) ? "" : $" WHERE {ix.FilterDefinition}";
            var ff = options.IgnoreFillFactor || ix.FillFactor == 0 ? "" : $" FILLFACTOR={ix.FillFactor}";
            var uq = ix.IsUnique && !ix.IsPrimaryKey && !ix.IsUniqueConstraint ? "UNIQUE " : "";
            map[ix.Name] = $"{uq}{kind} ({cols}){inc}{filter}{ff}{(ix.IsDisabled ? " [disabled]" : "")}";
        }

        return map;
    }

    private static Dictionary<string, string> Render(List<ForeignKeyInfo>? fks)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (fks is null) return map;

        foreach (var fk in fks)
        {
            var onDelete = fk.DeleteAction == "NO_ACTION" ? "" : $" ON DELETE {fk.DeleteAction.Replace('_', ' ')}";
            var onUpdate = fk.UpdateAction == "NO_ACTION" ? "" : $" ON UPDATE {fk.UpdateAction.Replace('_', ' ')}";
            map[fk.Name] =
                $"({string.Join(", ", fk.Columns)}) -> {fk.ReferencedSchema}.{fk.ReferencedTable} " +
                $"({string.Join(", ", fk.ReferencedColumns)}){onDelete}{onUpdate}" +
                (fk.IsDisabled ? " [disabled]" : "") + (fk.IsNotTrusted ? " [not trusted]" : "");
        }

        return map;
    }

    private static Dictionary<string, string> RenderChecks(List<ConstraintInfo>? constraints)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (constraints is null) return map;

        foreach (var c in constraints.Where(c => c.Kind == ConstraintKind.Check))
            map[c.Name] = c.Definition + (c.IsDisabled ? " [disabled]" : "");

        return map;
    }
}
