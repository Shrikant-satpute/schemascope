namespace SchemaScope.Core.Model;

public sealed class ColumnInfo
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Fully rendered type, e.g. "nvarchar(200)", "decimal(18,4)", "int".</summary>
    public string DataType { get; set; } = "";
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public string? IdentitySeed { get; set; }
    public string? IdentityIncrement { get; set; }
    public bool IsComputed { get; set; }
    public string? ComputedDefinition { get; set; }
    public bool IsPersisted { get; set; }
    public string? Collation { get; set; }
    public string? DefaultName { get; set; }
    public string? DefaultDefinition { get; set; }
    public bool IsRowGuidCol { get; set; }
    public bool IsSparse { get; set; }
}

public sealed class IndexColumnInfo
{
    public string Name { get; set; } = "";
    public bool Descending { get; set; }
    public int Ordinal { get; set; }
}

public sealed class IndexInfo
{
    public string Name { get; set; } = "";
    /// <summary>CLUSTERED, NONCLUSTERED, CLUSTERED COLUMNSTORE, XML, SPATIAL...</summary>
    public string TypeDesc { get; set; } = "NONCLUSTERED";
    public bool IsUnique { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsUniqueConstraint { get; set; }
    public bool IsDisabled { get; set; }
    public int FillFactor { get; set; }
    public bool IsPadded { get; set; }
    public bool IgnoreDupKey { get; set; }
    public string? FilterDefinition { get; set; }
    public string? DataSpace { get; set; }
    public List<IndexColumnInfo> KeyColumns { get; set; } = [];
    public List<string> IncludedColumns { get; set; } = [];
}

public sealed class ForeignKeyInfo
{
    public string Name { get; set; } = "";
    public string ReferencedSchema { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<string> ReferencedColumns { get; set; } = [];
    /// <summary>NO_ACTION, CASCADE, SET_NULL, SET_DEFAULT</summary>
    public string DeleteAction { get; set; } = "NO_ACTION";
    public string UpdateAction { get; set; } = "NO_ACTION";
    public bool IsDisabled { get; set; }
    public bool IsNotTrusted { get; set; }
}

public enum ConstraintKind { Check, Default }

public sealed class ConstraintInfo
{
    public string Name { get; set; } = "";
    public ConstraintKind Kind { get; set; }
    public string Definition { get; set; } = "";
    /// <summary>Only set for DEFAULT constraints.</summary>
    public string? ColumnName { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsNotTrusted { get; set; }
    /// <summary>True when SQL Server generated the name (DF__Table__Col__1A2B3C).</summary>
    public bool IsSystemNamed { get; set; }
}

public sealed class ParameterInfo
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsOutput { get; set; }
    public bool IsReadOnly { get; set; }
    public bool HasDefault { get; set; }
}
