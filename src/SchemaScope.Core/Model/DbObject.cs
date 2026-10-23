using System.Text.Json.Serialization;
using SchemaScope.Core.Engines;

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

    /// <summary>How the engine that read this spells names, and so how <see cref="Key"/> folds. SQL Server: Upper.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IdentifierFold Fold { get; set; } = IdentifierFold.Upper;

    /// <summary>
    /// What tells two objects of one name apart where the engine allows that: a PostgreSQL routine's argument types
    /// ("integer, text"), or a PostgreSQL trigger's table (trigger names are per table). Null for SQL Server.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; set; }

    /// <summary>
    /// Identity used to line objects up between databases. Two objects match when their keys match. Upper (SQL
    /// Server): case-insensitive, exactly the string it always was. Exact (PostgreSQL, whose catalog already holds the
    /// folded name): as spelled, plus the <see cref="Signature"/>. Lower: lower-cased.
    /// </summary>
    public string Key => Fold switch
    {
        IdentifierFold.Exact => Signature is null
            ? $"{(int)Type}|{Schema}.{Name}"
            : $"{(int)Type}|{Schema}.{Name}({Signature})",
        IdentifierFold.Lower => Signature is null
            ? $"{(int)Type}|{Schema.ToLowerInvariant()}.{Name.ToLowerInvariant()}"
            : $"{(int)Type}|{Schema.ToLowerInvariant()}.{Name.ToLowerInvariant()}({Signature.ToLowerInvariant()})",
        _ => $"{(int)Type}|{Schema.ToUpperInvariant()}.{Name.ToUpperInvariant()}"
    };

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

    /// <summary>
    /// The objects of this snapshot (and the extensions) this one needs, as the engine records it (PostgreSQL: pg_depend).
    /// Scripts use it to create objects in an order that works; the compare never reads it. Null for SQL Server.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DbDependency>? DependsOn { get; set; }

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

/// <summary>One thing a <see cref="DbObject"/> needs.</summary>
/// <param name="Key">The needed object's <see cref="DbObject.Key"/> (an extension's when it belongs to one).</param>
/// <param name="Column">The column of that table or view it names, when the dependency is on one column.</param>
/// <param name="Via">What records the need: view (a view's or rule's query), default (a default or generated column),
/// constraint, trigger, policy, routine, type or relation.</param>
public sealed record DbDependency(string Key, string? Column = null, string Via = "relation");
