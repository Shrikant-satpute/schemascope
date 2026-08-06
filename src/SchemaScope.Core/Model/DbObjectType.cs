namespace SchemaScope.Core.Model;

/// <summary>
/// Top level object kinds SchemaScope compares. Indexes, foreign keys and
/// constraints are deliberately NOT in this list: they are carried as children
/// of their parent table so a table shows up as a single row in the matrix.
/// </summary>
public enum DbObjectType
{
    Schema = 0,
    Table = 1,
    View = 2,
    StoredProcedure = 3,
    ScalarFunction = 4,
    TableValuedFunction = 5,
    AggregateFunction = 6,
    Trigger = 7,
    UserDefinedType = 8,
    TableType = 9,
    Sequence = 10,
    Synonym = 11
}

public static class DbObjectTypeExtensions
{
    /// <summary>Human label used in the UI tree and reports.</summary>
    public static string Label(this DbObjectType t) => t switch
    {
        DbObjectType.Schema => "Schema",
        DbObjectType.Table => "Table",
        DbObjectType.View => "View",
        DbObjectType.StoredProcedure => "Procedure",
        DbObjectType.ScalarFunction => "Scalar function",
        DbObjectType.TableValuedFunction => "Table function",
        DbObjectType.AggregateFunction => "Aggregate",
        DbObjectType.Trigger => "Trigger",
        DbObjectType.UserDefinedType => "User type",
        DbObjectType.TableType => "Table type",
        DbObjectType.Sequence => "Sequence",
        DbObjectType.Synonym => "Synonym",
        _ => t.ToString()
    };

    /// <summary>True when the object's identity is a block of T-SQL text.</summary>
    public static bool IsModule(this DbObjectType t) => t is
        DbObjectType.View or
        DbObjectType.StoredProcedure or
        DbObjectType.ScalarFunction or
        DbObjectType.TableValuedFunction or
        DbObjectType.AggregateFunction or
        DbObjectType.Trigger;

    /// <summary>Maps a sys.objects type code to our enum. Null means "not compared".</summary>
    public static DbObjectType? FromSysType(string sysType) => sysType.Trim().ToUpperInvariant() switch
    {
        "U" => DbObjectType.Table,
        "V" => DbObjectType.View,
        "P" or "PC" => DbObjectType.StoredProcedure,
        "FN" or "FS" => DbObjectType.ScalarFunction,
        "IF" or "TF" or "FT" => DbObjectType.TableValuedFunction,
        "AF" => DbObjectType.AggregateFunction,
        "TR" or "TA" => DbObjectType.Trigger,
        "SO" => DbObjectType.Sequence,
        "SN" => DbObjectType.Synonym,
        _ => null
    };
}
