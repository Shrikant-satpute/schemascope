namespace SchemaScope.Core.Model;

/// <summary>
/// One comparable database object. Tables carry their columns / indexes /
/// constraints as children; modules carry their T-SQL text.
/// </summary>
public sealed class DbObject
{
    public DbObjectType Type { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = "";

    public string FullName => $"{Schema}.{Name}";

    /// <summary>
    /// Case-insensitive identity used to line objects up between databases.
    /// Two objects match when their keys match.
    /// </summary>
    public string Key => $"{(int)Type}|{Schema.ToUpperInvariant()}.{Name.ToUpperInvariant()}";

    /// <summary>Raw T-SQL from sys.sql_modules. Null for tables and non-module types.</summary>
    public string? Definition { get; set; }

    /// <summary>Table or view a trigger belongs to.</summary>
    public string? ParentName { get; set; }

    public DateTime ModifyDate { get; set; }

    /// <summary>WITH ENCRYPTION - the body cannot be read by anyone.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>The login lacks VIEW DEFINITION, so we can see the name but not the body.</summary>
    public bool DefinitionUnavailable { get; set; }

    public List<ColumnInfo> Columns { get; set; } = [];
    public List<IndexInfo> Indexes { get; set; } = [];
    public List<ForeignKeyInfo> ForeignKeys { get; set; } = [];
    public List<ConstraintInfo> Constraints { get; set; } = [];
    public List<ParameterInfo> Parameters { get; set; } = [];

    /// <summary>
    /// Odds and ends that only apply to one object kind: sequence start/increment,
    /// synonym base object, user-type base type, schema owner.
    /// </summary>
    public Dictionary<string, string?> Properties { get; set; } = [];

    // ---- filled in by the normalizer, not by the reader ----

    /// <summary>
    /// The text we actually hash and diff. For modules this is the lightly
    /// normalized definition; for tables it is a canonical CREATE TABLE script
    /// we generate ourselves, so formatting can never cause a false difference.
    /// </summary>
    public string CompareText { get; set; } = "";

    /// <summary>SHA-256 of <see cref="CompareText"/>. Equal hash means equal object.</summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// SHA-256 of an aggressively stripped form (no comments, no whitespace, no
    /// bracket quoting, upper case). Used to tell "only formatting changed"
    /// from a real change.
    /// </summary>
    public string LooseHash { get; set; } = "";

    /// <summary>
    /// Length of the stripped form. Two objects whose stripped lengths are far
    /// apart cannot be the same code, so the expensive parse check is skipped.
    /// </summary>
    public int LooseLength { get; set; }

    /// <summary>What we show in the diff editor. Modules show their true original text.</summary>
    public string DisplayText => Type.IsModule() ? (Definition ?? CompareText) : CompareText;

    public int LineCount { get; set; }
}
