using System.Text;

namespace SchemaScope.SqlServer;

/// <summary>
/// The whole schema is read with two round trips: one tiny version probe, then
/// one batch that returns every result set below. Reading per object - which is
/// what makes other tools slow - never happens.
/// </summary>
internal static class CatalogQueries
{
    /// <summary>Result set order produced by <see cref="BuildBatch"/>.</summary>
    internal enum Set
    {
        Schemas = 0,
        Objects,
        Modules,
        Columns,
        Indexes,
        IndexColumns,
        ForeignKeys,
        ForeignKeyColumns,
        CheckConstraints,
        DefaultConstraints,
        Parameters,
        Synonyms,
        UserTypes,
        TableTypes,
        Sequences   // only present when the server supports them (2012+)
    }

    public const string ServerInfo = """
        SELECT
            CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))                AS product_version,
            CAST(SERVERPROPERTY('Edition')        AS nvarchar(128))                AS edition,
            CAST(SERVERPROPERTY('EngineEdition')  AS int)                          AS engine_edition,
            DB_NAME()                                                              AS database_name,
            CAST(SERVERPROPERTY('ServerName')     AS nvarchar(256))                AS server_name,
            CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))      AS db_collation;
        """;

    private const string ComparedObjectTypes = "'U','V','P','PC','FN','FS','IF','TF','FT','AF','TR','TA','SO','SN'";
    private const string ModuleTypes = "'V','P','PC','FN','FS','IF','TF','FT','TR','TA'";
    private const string ColumnBearingTypes = "'U','V','TT'";

    public static string BuildBatch(int majorVersion)
    {
        var sb = new StringBuilder(8192);

        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");

        // 0 - schemas
        sb.AppendLine("""
            SELECT s.name AS schema_name, ISNULL(dp.name, N'') AS owner_name
            FROM sys.schemas s
            LEFT JOIN sys.database_principals dp ON dp.principal_id = s.principal_id;
            """);

        // 1 - objects
        sb.AppendLine($"""
            SELECT o.object_id,
                   s.name        AS schema_name,
                   o.name        AS object_name,
                   o.type        AS object_type,
                   o.modify_date,
                   o.is_ms_shipped,
                   ISNULL(ps.name, N'') AS parent_schema,
                   ISNULL(po.name, N'') AS parent_name
            FROM sys.objects o
            JOIN sys.schemas s        ON s.schema_id = o.schema_id
            LEFT JOIN sys.objects po  ON po.object_id = o.parent_object_id
            LEFT JOIN sys.schemas ps  ON ps.schema_id = po.schema_id
            WHERE o.type IN ({ComparedObjectTypes});
            """);

        // 2 - module bodies
        sb.AppendLine($"""
            SELECT o.object_id,
                   m.definition,
                   CAST(CASE WHEN m.object_id IS NULL THEN 1 ELSE 0 END AS bit) AS body_missing,
                   CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit) AS is_encrypted
            FROM sys.objects o
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE o.type IN ({ModuleTypes});
            """);

        // 3 - columns
        sb.AppendLine($"""
            SELECT c.object_id,
                   c.column_id,
                   c.name              AS column_name,
                   tp.name             AS type_name,
                   SCHEMA_NAME(tp.schema_id) AS type_schema,
                   tp.is_user_defined,
                   c.max_length,
                   c.precision,
                   c.scale,
                   c.is_nullable,
                   c.is_identity,
                   c.is_computed,
                   c.is_rowguidcol,
                   c.is_sparse,
                   c.collation_name,
                   cc.definition       AS computed_definition,
                   CAST(ISNULL(cc.is_persisted, 0) AS bit) AS is_persisted,
                   CONVERT(nvarchar(40), ic.seed_value)      AS seed_value,
                   CONVERT(nvarchar(40), ic.increment_value) AS increment_value
            FROM sys.columns c
            JOIN sys.objects o           ON o.object_id = c.object_id AND o.type IN ({ColumnBearingTypes})
            JOIN sys.types tp            ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            ORDER BY c.object_id, c.column_id;
            """);

        // 4 - indexes
        sb.AppendLine($"""
            SELECT i.object_id,
                   i.index_id,
                   i.name AS index_name,
                   i.type_desc,
                   i.is_unique,
                   i.is_primary_key,
                   i.is_unique_constraint,
                   i.is_disabled,
                   i.fill_factor,
                   i.is_padded,
                   i.ignore_dup_key,
                   i.filter_definition,
                   ISNULL(ds.name, N'') AS data_space
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id AND o.type IN ({ColumnBearingTypes})
            LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
            WHERE i.type <> 0 AND i.name IS NOT NULL;
            """);

        // 5 - index columns
        sb.AppendLine($"""
            SELECT ic.object_id,
                   ic.index_id,
                   ic.key_ordinal,
                   ic.is_descending_key,
                   ic.is_included_column,
                   c.name AS column_name
            FROM sys.index_columns ic
            JOIN sys.objects o ON o.object_id = ic.object_id AND o.type IN ({ColumnBearingTypes})
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            ORDER BY ic.object_id, ic.index_id, ic.is_included_column, ic.key_ordinal, c.name;
            """);

        // 6 - foreign keys
        sb.AppendLine("""
            SELECT fk.object_id,
                   fk.name AS fk_name,
                   fk.parent_object_id,
                   SCHEMA_NAME(rt.schema_id) AS ref_schema,
                   rt.name                   AS ref_table,
                   fk.delete_referential_action_desc,
                   fk.update_referential_action_desc,
                   fk.is_disabled,
                   fk.is_not_trusted
            FROM sys.foreign_keys fk
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id;
            """);

        // 7 - foreign key columns
        sb.AppendLine("""
            SELECT fkc.constraint_object_id,
                   fkc.constraint_column_id,
                   pc.name AS parent_column,
                   rc.name AS referenced_column
            FROM sys.foreign_key_columns fkc
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id     AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;
            """);

        // 8 - check constraints
        sb.AppendLine("""
            SELECT cc.parent_object_id,
                   cc.name AS constraint_name,
                   cc.definition,
                   cc.is_disabled,
                   cc.is_not_trusted,
                   cc.is_system_named
            FROM sys.check_constraints cc;
            """);

        // 9 - default constraints
        sb.AppendLine("""
            SELECT dc.parent_object_id,
                   dc.name AS constraint_name,
                   dc.definition,
                   dc.is_system_named,
                   c.name  AS column_name
            FROM sys.default_constraints dc
            JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id;
            """);

        // 10 - parameters
        sb.AppendLine("""
            SELECT p.object_id,
                   p.parameter_id,
                   p.name AS parameter_name,
                   tp.name AS type_name,
                   SCHEMA_NAME(tp.schema_id) AS type_schema,
                   tp.is_user_defined,
                   p.max_length,
                   p.precision,
                   p.scale,
                   p.is_output,
                   p.is_readonly,
                   p.has_default_value
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            JOIN sys.types tp  ON tp.user_type_id = p.user_type_id
            WHERE o.type IN ('P','PC','FN','FS','IF','TF','FT','AF')
            ORDER BY p.object_id, p.parameter_id;
            """);

        // 11 - synonyms
        sb.AppendLine("""
            SELECT SCHEMA_NAME(sn.schema_id) AS schema_name,
                   sn.name,
                   sn.base_object_name,
                   sn.modify_date
            FROM sys.synonyms sn;
            """);

        // 12 - user defined scalar types
        sb.AppendLine("""
            SELECT SCHEMA_NAME(t.schema_id) AS schema_name,
                   t.name,
                   TYPE_NAME(t.system_type_id) AS base_type,
                   t.max_length,
                   t.precision,
                   t.scale,
                   t.is_nullable,
                   ISNULL(t.collation_name, N'') AS collation_name
            FROM sys.types t
            WHERE t.is_user_defined = 1 AND t.is_table_type = 0;
            """);

        // 13 - table types
        sb.AppendLine("""
            SELECT SCHEMA_NAME(tt.schema_id) AS schema_name,
                   tt.name,
                   tt.type_table_object_id
            FROM sys.table_types tt
            WHERE tt.is_user_defined = 1;
            """);

        // 14 - sequences (SQL Server 2012 and later only)
        if (majorVersion >= 11)
        {
            sb.AppendLine("""
                SELECT SCHEMA_NAME(sq.schema_id) AS schema_name,
                       sq.name,
                       TYPE_NAME(sq.user_type_id) AS type_name,
                       CONVERT(nvarchar(40), sq.start_value)   AS start_value,
                       CONVERT(nvarchar(40), sq.increment)     AS increment,
                       CONVERT(nvarchar(40), sq.minimum_value) AS min_value,
                       CONVERT(nvarchar(40), sq.maximum_value) AS max_value,
                       sq.is_cycling,
                       sq.is_cached,
                       ISNULL(sq.cache_size, 0) AS cache_size,
                       sq.modify_date
                FROM sys.sequences sq;
                """);
        }

        return sb.ToString();
    }

    public const string ListDatabases = """
        SELECT d.name,
               d.state_desc,
               CAST(CASE WHEN HAS_DBACCESS(d.name) = 1 THEN 1 ELSE 0 END AS bit) AS has_access
        FROM sys.databases d
        WHERE d.state = 0
        ORDER BY CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END, d.name;
        """;
}
