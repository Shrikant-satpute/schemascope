using Microsoft.Data.SqlClient;

namespace SchemaScope.Scripting;

/// <summary>
/// Reads a <see cref="ScriptSchema"/> with the exact queries of meta.py (python.md §6A).
///
/// SchemaScope's own reader builds a comparison model; the generator needs the raw rows
/// meta.py worked from (pre-rendered key column lists, identity seeds, byte-exact bodies),
/// so this runs those queries verbatim. Read-only: catalog SELECTs only, one connection per
/// database, one query at a time (MARS is off).
/// </summary>
public sealed class ScriptCatalogReader
{
    /// <summary>Per-query timeout. Module bodies on a big database can take a while.</summary>
    public int CommandTimeoutSeconds { get; init; } = 180;

    /// <summary>How many catalog queries one <see cref="ReadAsync"/> runs (each one calls <c>queryDone</c> once).</summary>
    public const int QueryCount = 14;

    /// <summary>Reads every collection of one database. <paramref name="queryDone"/> is called after each query, for progress.</summary>
    public async Task<ScriptSchema> ReadAsync(ScriptEndpoint endpoint, CancellationToken ct = default, Action? queryDone = null)
    {
        await using var conn = new SqlConnection(SqlProbe.Tune(endpoint.ConnectionString));
        await conn.OpenAsync(ct);

        var collation = (await QueryAsync(conn, Queries.Collation, r => Str(r, 0), ct, queryDone)).FirstOrDefault() ?? "";

        var modules = await QueryAsync(conn, Queries.Modules, r => new ModuleRow(
            Str(r, "sch"), Str(r, "obj"), Str(r, "otype"),
            Bool(r, "ansi_nulls"), Bool(r, "quoted_id"), NStr(r, "definition")), ct, queryDone);

        var tables = await QueryAsync(conn, Queries.Tables, r => new ObjectName(Str(r, "sch"), Str(r, "tbl")), ct, queryDone);

        var columns = await QueryAsync(conn, Queries.Columns, r => new ColumnRow(
            Str(r, "sch"), Str(r, "tbl"), Int(r, "column_id"), Str(r, "col"), Str(r, "dt"),
            Int(r, "max_length"), Int(r, "precision"), Int(r, "scale"),
            Bool(r, "is_nullable"), Bool(r, "is_identity"), Bool(r, "is_computed"),
            NStr(r, "collation_name"), NStr(r, "computed_def"), Bool(r, "is_persisted"),
            NStr(r, "seed"), NStr(r, "incr"), Bool(r, "ident_nfr"), NStr(r, "tsch")), ct, queryDone);

        var defaults = await QueryAsync(conn, Queries.Defaults, r => new DefaultRow(
            Str(r, "sch"), Str(r, "tbl"), Str(r, "col"), Str(r, "cname"), Str(r, "definition"),
            Bool(r, "is_system_named")), ct, queryDone);

        var checks = await QueryAsync(conn, Queries.Checks, r => new CheckRow(
            Str(r, "sch"), Str(r, "tbl"), Str(r, "cname"), Str(r, "definition"),
            Bool(r, "is_system_named"), Bool(r, "is_not_trusted"), Bool(r, "is_disabled"), Bool(r, "nfr")), ct, queryDone);

        var keys = await QueryAsync(conn, Queries.Keys, r => new KeyRow(
            Str(r, "sch"), Str(r, "tbl"), Str(r, "cname"), Str(r, "ktype"),
            Bool(r, "is_system_named"), Str(r, "type_desc"), Str(r, "cols")), ct, queryDone);

        var fkeys = await QueryAsync(conn, Queries.FKeys, r => new ForeignKeyRow(
            Str(r, "sch"), Str(r, "tbl"), Str(r, "cname"), Bool(r, "is_system_named"),
            Str(r, "rsch"), Str(r, "rtbl"), Str(r, "ondel"), Str(r, "onupd"),
            Bool(r, "is_disabled"), Bool(r, "is_not_trusted"), Bool(r, "nfr"),
            Str(r, "cols"), Str(r, "rcols")), ct, queryDone);

        var indexes = await QueryAsync(conn, Queries.Indexes, r => new IndexRow(
            Str(r, "sch"), Str(r, "tbl"), Str(r, "iname"), Str(r, "type_desc"), Bool(r, "is_unique"),
            NStr(r, "filter_definition"), NStr(r, "cols"), NStr(r, "incl"),
            Bool(r, "is_hypothetical"), Bool(r, "is_disabled")), ct, queryDone);

        var tableTypes = await QueryAsync(conn, Queries.TableTypes, r => new ObjectName(Str(r, "sch"), Str(r, "tt")), ct, queryDone);

        var ttColumns = await QueryAsync(conn, Queries.TtColumns, r => new TtColumnRow(
            Str(r, "sch"), Str(r, "tt"), Int(r, "column_id"), Str(r, "col"), Str(r, "dt"),
            Int(r, "max_length"), Int(r, "precision"), Int(r, "scale"),
            Bool(r, "is_nullable"), Bool(r, "is_identity"), NStr(r, "collation_name"),
            NStr(r, "seed"), NStr(r, "incr"), NStr(r, "default_def"), NStr(r, "computed_def"),
            Bool(r, "is_persisted"), NStr(r, "tsch")), ct, queryDone);

        var ttIndexes = await QueryAsync(conn, Queries.TtIndexes, r => new TtIndexRow(
            Str(r, "sch"), Str(r, "tt"), Bool(r, "is_primary_key"), Bool(r, "is_unique"),
            Str(r, "type_desc"), NStr(r, "cols"), NStr(r, "iname")), ct, queryDone);

        var ttChecks = await QueryAsync(conn, Queries.TtChecks, r => new TtCheckRow(
            Str(r, "sch"), Str(r, "tt"), Str(r, "definition")), ct, queryDone);

        var ttParams = await QueryAsync(conn, Queries.TtParams, r => new TtParamRow(
            Str(r, "tt"), Str(r, "sch"), Str(r, "obj"), Str(r, "tsch")), ct, queryDone);

        return ScriptSchema.FromRows(
            string.IsNullOrEmpty(endpoint.Database) ? conn.Database : endpoint.Database, collation,
            modules, tables, columns, defaults, checks, keys, fkeys, indexes, tableTypes, ttColumns, ttIndexes, ttParams, ttChecks);
    }

    private async Task<List<T>> QueryAsync<T>(SqlConnection conn, string sql, Func<SqlDataReader, T> map, CancellationToken ct, Action? done)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        done?.Invoke();
        return list;
    }

    private static string Str(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string Str(SqlDataReader r, string name) => Str(r, r.GetOrdinal(name));

    private static string? NStr(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    // Python reads bit NULL as None, which every caller treats as False.
    private static bool Bool(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return !r.IsDBNull(i) && Convert.ToBoolean(r.GetValue(i));
    }

    // smallint / tinyint / int all arrive as their own CLR type.
    private static int Int(SqlDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    }

    /// <summary>
    /// The meta.py queries, verbatim (python.md §6A), with documented additions that only append
    /// columns (no filter changes; the added joins are LEFT JOINs on a column's own type, default or computed
    /// definition, so every Python row is still read, once, and nothing else is):
    /// <see cref="Indexes"/> also selects <c>is_hypothetical</c> and <c>is_disabled</c> (see <see cref="Ddl.UnscriptableIndexReason"/>),
    /// <see cref="TtParams"/> also selects the type's own schema as <c>tsch</c> (see <see cref="TtParamRow.TSch"/>),
    /// <see cref="Columns"/> and <see cref="TtColumns"/> select the schema of a user-defined column type as <c>tsch</c>
    /// (see <see cref="IColumnDef.TypeSchema"/>), <see cref="TtColumns"/> also a column's default and computed
    /// definition, and <see cref="TtIndexes"/> the index name - so a rebuilt table type keeps them.
    /// <see cref="TtChecks"/> is new.
    /// One documented change: the pre-rendered column lists (<c>cols</c>, <c>incl</c>, <c>rcols</c>) bracket each name
    /// with <c>QUOTENAME</c> instead of <c>'[' + name + ']'</c>. The text is identical for every name without a
    /// <c>]</c>; a name with one comes out escaped (<c>[a]]b]</c>), so the lists can be pasted into DDL as they are
    /// and a column name can never close the bracket and add SQL of its own.
    /// </summary>
    public static class Queries
    {
        public const string Modules = """
            SELECT s.name AS sch, o.name AS obj, RTRIM(o.type) AS otype,
                   m.uses_ansi_nulls AS ansi_nulls, m.uses_quoted_identifier AS quoted_id,
                   m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            """;

        public const string Tables = """
            SELECT s.name AS sch, t.name AS tbl
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE t.is_ms_shipped = 0
            """;

        public const string Columns = """
            SELECT s.name AS sch, t.name AS tbl, c.column_id, c.name AS col,
                   TYPE_NAME(c.user_type_id) AS dt, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed, c.collation_name,
                   cc.definition AS computed_def, cc.is_persisted,
                   CONVERT(varchar(40), ic.seed_value)      AS seed,
                   CONVERT(varchar(40), ic.increment_value) AS incr,
                   ic.is_not_for_replication AS ident_nfr,
                   CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) END AS tsch
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            LEFT JOIN sys.computed_columns  cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.identity_columns  ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.types             ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            """;

        public const string Defaults = """
            SELECT s.name AS sch, t.name AS tbl, c.name AS col,
                   dc.name AS cname, dc.definition, dc.is_system_named
            FROM sys.default_constraints dc
            JOIN sys.tables   t ON t.object_id  = dc.parent_object_id
            JOIN sys.schemas  s ON s.schema_id  = t.schema_id
            JOIN sys.columns  c ON c.object_id  = dc.parent_object_id AND c.column_id = dc.parent_column_id
            """;

        public const string Checks = """
            SELECT s.name AS sch, t.name AS tbl, ck.name AS cname, ck.definition,
                   ck.is_system_named, ck.is_not_trusted, ck.is_disabled, ck.is_not_for_replication AS nfr
            FROM sys.check_constraints ck
            JOIN sys.tables  t ON t.object_id = ck.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            """;

        public const string Keys = """
            SELECT s.name AS sch, t.name AS tbl, kc.name AS cname, RTRIM(kc.type) AS ktype,
                   kc.is_system_named, i.type_desc,
                   STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ixc.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
                          FROM sys.index_columns ixc
                          JOIN sys.columns col ON col.object_id = ixc.object_id AND col.column_id = ixc.column_id
                          WHERE ixc.object_id = i.object_id AND ixc.index_id = i.index_id AND ixc.is_included_column = 0
                          ORDER BY ixc.key_ordinal FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS cols
            FROM sys.key_constraints kc
            JOIN sys.tables  t ON t.object_id  = kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id  = t.schema_id
            JOIN sys.indexes i ON i.object_id  = kc.parent_object_id AND i.index_id = kc.unique_index_id
            """;

        public const string FKeys = """
            SELECT s.name AS sch, t.name AS tbl, fk.name AS cname, fk.is_system_named,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS rsch,
                   OBJECT_NAME(fk.referenced_object_id)        AS rtbl,
                   fk.delete_referential_action_desc AS ondel,
                   fk.update_referential_action_desc AS onupd,
                   fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication AS nfr,
                   STUFF((SELECT ', ' + QUOTENAME(pc.name) FROM sys.foreign_key_columns fkc
                          JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                          WHERE fkc.constraint_object_id = fk.object_id
                          ORDER BY fkc.constraint_column_id FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS cols,
                   STUFF((SELECT ', ' + QUOTENAME(rc.name) FROM sys.foreign_key_columns fkc
                          JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                          WHERE fkc.constraint_object_id = fk.object_id
                          ORDER BY fkc.constraint_column_id FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS rcols
            FROM sys.foreign_keys fk
            JOIN sys.tables  t ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            """;

        public const string Indexes = """
            SELECT s.name AS sch, t.name AS tbl, i.name AS iname, i.type_desc, i.is_unique,
                   i.filter_definition,
                   STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ixc.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
                          FROM sys.index_columns ixc
                          JOIN sys.columns col ON col.object_id = ixc.object_id AND col.column_id = ixc.column_id
                          WHERE ixc.object_id = i.object_id AND ixc.index_id = i.index_id AND ixc.is_included_column = 0
                          ORDER BY ixc.key_ordinal FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS cols,
                   STUFF((SELECT ', ' + QUOTENAME(col.name)
                          FROM sys.index_columns ixc
                          JOIN sys.columns col ON col.object_id = ixc.object_id AND col.column_id = ixc.column_id
                          WHERE ixc.object_id = i.object_id AND ixc.index_id = i.index_id AND ixc.is_included_column = 1
                          ORDER BY ixc.index_column_id FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS incl,
                   i.is_hypothetical, i.is_disabled
            FROM sys.indexes i
            JOIN sys.tables  t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND i.type_desc <> 'HEAP' AND t.is_ms_shipped = 0
            """;

        public const string TableTypes = """
            SELECT s.name AS sch, tt.name AS tt FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id WHERE tt.is_user_defined = 1
            """;

        public const string TtColumns = """
            SELECT s.name AS sch, tt.name AS tt, c.column_id, c.name AS col,
                   TYPE_NAME(c.user_type_id) AS dt, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.collation_name,
                   CONVERT(varchar(40), ic.seed_value) AS seed,
                   CONVERT(varchar(40), ic.increment_value) AS incr,
                   dc.definition AS default_def, cc.definition AS computed_def, cc.is_persisted,
                   CASE WHEN ty.is_user_defined = 1 THEN SCHEMA_NAME(ty.schema_id) END AS tsch
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.columns c ON c.object_id = tt.type_table_object_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE tt.is_user_defined = 1
            """;

        public const string TtIndexes = """
            SELECT s.name AS sch, tt.name AS tt, i.is_primary_key, i.is_unique, i.type_desc, i.name AS iname,
                   STUFF((SELECT ', ' + QUOTENAME(col.name) + CASE WHEN ixc.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
                          FROM sys.index_columns ixc
                          JOIN sys.columns col ON col.object_id = ixc.object_id AND col.column_id = ixc.column_id
                          WHERE ixc.object_id = i.object_id AND ixc.index_id = i.index_id AND ixc.is_included_column = 0
                          ORDER BY ixc.key_ordinal FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, '') AS cols
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.indexes i ON i.object_id = tt.type_table_object_id
            WHERE tt.is_user_defined = 1 AND i.type_desc <> 'HEAP'
            """;

        public const string TtChecks = """
            SELECT s.name AS sch, tt.name AS tt, ck.definition
            FROM sys.table_types tt
            JOIN sys.schemas s ON s.schema_id = tt.schema_id
            JOIN sys.check_constraints ck ON ck.parent_object_id = tt.type_table_object_id
            WHERE tt.is_user_defined = 1
            ORDER BY ck.object_id
            """;

        public const string TtParams = """
            SELECT tt.name AS tt, s.name AS sch, o.name AS obj, SCHEMA_NAME(tt.schema_id) AS tsch
            FROM sys.parameters p
            JOIN sys.objects o    ON o.object_id = p.object_id
            JOIN sys.schemas s    ON s.schema_id = o.schema_id
            JOIN sys.table_types tt ON tt.user_type_id = p.user_type_id
            WHERE tt.is_user_defined = 1
            """;

        public const string Collation = "SELECT CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS c";
    }
}
