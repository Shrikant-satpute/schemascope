namespace SchemaScope.SqlServer;

/// <summary>
/// Renders a catalog type row as the text a human would write:
/// nvarchar(200), decimal(18,4), datetime2(3), varbinary(max).
/// Both sides go through this, so type formatting can never cause a false diff.
/// </summary>
public static class SqlTypeFormatter
{
    public static string Format(
        string typeName,
        string? typeSchema,
        bool isUserDefined,
        int maxLength,
        byte precision,
        byte scale)
    {
        if (isUserDefined)
        {
            var schema = string.IsNullOrEmpty(typeSchema) ? "dbo" : typeSchema;
            return $"[{schema}].[{typeName}]";
        }

        var t = typeName.ToLowerInvariant();

        switch (t)
        {
            case "char":
            case "varchar":
            case "binary":
            case "varbinary":
                return maxLength == -1 ? $"{t}(max)" : $"{t}({maxLength})";

            case "nchar":
            case "nvarchar":
                return maxLength == -1 ? $"{t}(max)" : $"{t}({maxLength / 2})";

            case "decimal":
            case "numeric":
                return $"{t}({precision},{scale})";

            case "datetime2":
            case "time":
            case "datetimeoffset":
                return $"{t}({scale})";

            case "float":
                // 53 is the default and is normally written bare
                return precision == 53 ? t : $"{t}({precision})";

            case "xml":
            case "sql_variant":
            case "hierarchyid":
            case "geometry":
            case "geography":
            default:
                return t;
        }
    }
}
