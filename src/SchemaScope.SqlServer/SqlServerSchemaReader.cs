using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.SqlServer;

/// <summary>
/// Reads a whole SQL Server database schema in two round trips, then hashes it.
///
/// This is the piece that makes SchemaScope fast. Tools that build a full
/// semantic model spend minutes; reading the catalog in bulk takes about a
/// second even on a five thousand object database.
/// </summary>
public sealed class SqlServerSchemaReader : ISchemaReader
{
    public int CommandTimeoutSeconds { get; init; } = 180;

    public async Task<SchemaSnapshot> ReadAsync(
        SchemaSource source,
        CompareOptions options,
        IProgress<ReadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var snapshot = new SchemaSnapshot
        {
            Label = source.DisplayName,
            Server = source.Server,
            Database = source.Database
        };

        progress?.Report(new ReadProgress(source.Id, "Connecting", 0));

        await using var conn = new SqlConnection(TuneForBulkRead(source.ConnectionString));
        try
        {
            await conn.OpenAsync(ct);
        }
        catch (SqlException ex)
        {
            throw new SchemaReadException(
                $"Could not connect to {source.DisplayName}: {ex.Message}",
                HintForSqlError(ex),
                ex);
        }

        var connectMs = total.ElapsedMilliseconds;
        snapshot.Server = string.IsNullOrEmpty(source.Server) ? conn.DataSource : source.Server;
        snapshot.Database = conn.Database;

        // --- round trip 1: what are we talking to? ---
        progress?.Report(new ReadProgress(source.Id, "Reading server info", 5));
        var stage = Stopwatch.StartNew();
        await ReadServerInfoAsync(conn, snapshot, ct);
        snapshot.TimingsMs["serverInfo"] = stage.ElapsedMilliseconds;

        // --- round trip 2: the entire catalog ---
        progress?.Report(new ReadProgress(source.Id, "Reading catalog", 15));
        stage.Restart();
        var raw = await ReadCatalogAsync(conn, snapshot.MajorVersion, ct);
        snapshot.TimingsMs["catalog"] = stage.ElapsedMilliseconds;

        progress?.Report(new ReadProgress(source.Id, "Building model", 70));
        stage.Restart();
        Assemble(snapshot, raw, options);
        snapshot.TimingsMs["assemble"] = stage.ElapsedMilliseconds;

        progress?.Report(new ReadProgress(source.Id, "Hashing", 85));
        stage.Restart();
        new SchemaNormalizer(options).Normalize(snapshot);
        snapshot.TimingsMs["hash"] = stage.ElapsedMilliseconds;

        snapshot.TimingsMs["connect"] = connectMs;
        snapshot.TimingsMs["total"] = total.ElapsedMilliseconds;

        progress?.Report(new ReadProgress(
            source.Id, "Done", 100, $"{snapshot.Objects.Count} objects in {total.ElapsedMilliseconds} ms"));

        return snapshot;
    }

    /// <summary>
    /// Procedure and view bodies are the bulk of what we transfer, so a bigger
    /// network packet is worth real time on a large database. Anything the user
    /// set explicitly is left alone.
    /// </summary>
    private static string TuneForBulkRead(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            if (b.PacketSize == 8000) b.PacketSize = 32767;
            if (string.IsNullOrEmpty(b.ApplicationName) || b.ApplicationName == "Core .Net SqlClient Data Provider")
                b.ApplicationName = "SchemaScope";
            return b.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static string? HintForSqlError(SqlException ex) => ex.Number switch
    {
        4060 or 18456 => "Check the login name, password and that this login has access to the database.",
        53 or 40615 => "Check the server name and that the SQL Server is reachable from this machine (firewall, port).",
        -2 => "The server took too long to answer. Try again, or raise the timeout.",
        // certificate chain error - very common on internal servers
        -2146893019 => "The server certificate is not trusted. Tick 'Trust server certificate' on the connection.",
        _ => null
    };

    private static async Task ReadServerInfoAsync(SqlConnection conn, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(CatalogQueries.ServerInfo, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return;

        snapshot.ProductVersion = r.IsDBNull(0) ? "" : r.GetString(0);
        snapshot.Edition = r.IsDBNull(1) ? "" : r.GetString(1);
        var engineEdition = r.IsDBNull(2) ? 0 : r.GetInt32(2);
        snapshot.Database = r.IsDBNull(3) ? snapshot.Database : r.GetString(3);
        if (string.IsNullOrEmpty(snapshot.Server) && !r.IsDBNull(4)) snapshot.Server = r.GetString(4);
        snapshot.Collation = r.IsDBNull(5) ? "" : r.GetString(5);

        // 5 = Azure SQL Database, 8 = Azure SQL Managed Instance, 9 = Azure Synapse
        snapshot.IsAzure = engineEdition is 5 or 8 or 9 or 11;

        var dot = snapshot.ProductVersion.IndexOf('.');
        snapshot.MajorVersion = dot > 0 && int.TryParse(snapshot.ProductVersion[..dot], out var mv) ? mv : 0;
    }

    // -----------------------------------------------------------------
    //  Raw catalog rows, straight out of the reader
    // -----------------------------------------------------------------

    private sealed class RawCatalog
    {
        public List<(string Name, string Owner)> Schemas = [];
        public List<RawObject> Objects = [];
        public Dictionary<int, (string? Definition, bool Missing, bool Encrypted)> Modules = [];
        public Dictionary<int, List<ColumnInfo>> Columns = [];
        public Dictionary<int, Dictionary<int, IndexInfo>> Indexes = [];
        public Dictionary<int, List<ForeignKeyInfo>> ForeignKeys = [];
        public Dictionary<int, List<ConstraintInfo>> Constraints = [];
        public Dictionary<int, List<ParameterInfo>> Parameters = [];
        public Dictionary<string, string> SynonymBases = [];
        public List<RawUserType> UserTypes = [];
        public List<(string Schema, string Name, int ObjectId)> TableTypes = [];
        public Dictionary<string, Dictionary<string, string?>> SequenceProps = [];
    }

    private sealed record RawObject(
        int ObjectId, string Schema, string Name, string Type,
        DateTime ModifyDate, bool IsMsShipped, string ParentSchema, string ParentName);

    private sealed record RawUserType(
        string Schema, string Name, string BaseType, int MaxLength,
        byte Precision, byte Scale, bool IsNullable, string Collation);

    private async Task<RawCatalog> ReadCatalogAsync(SqlConnection conn, int majorVersion, CancellationToken ct)
    {
        var raw = new RawCatalog();

        await using var cmd = new SqlCommand(CatalogQueries.BuildBatch(majorVersion), conn)
        {
            CommandTimeout = CommandTimeoutSeconds,
            CommandType = CommandType.Text
        };

        await using var r = await cmd.ExecuteReaderAsync(ct);

        // 0 - schemas
        while (await r.ReadAsync(ct))
            raw.Schemas.Add((r.GetString(0), r.GetString(1)));

        // 1 - objects
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Objects.Add(new RawObject(
                r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetDateTime(4), r.GetBoolean(5), r.GetString(6), r.GetString(7)));

        // 2 - module bodies
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.Modules[r.GetInt32(0)] = (
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetBoolean(2),
                r.GetBoolean(3));

        // 3 - columns
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var objectId = r.GetInt32(0);
            var col = new ColumnInfo
            {
                Ordinal = r.GetInt32(1),
                Name = r.GetString(2),
                DataType = SqlTypeFormatter.Format(
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetBoolean(5),
                    r.GetInt16(6),
                    r.GetByte(7),
                    r.GetByte(8)),
                IsNullable = r.GetBoolean(9),
                IsIdentity = r.GetBoolean(10),
                IsComputed = r.GetBoolean(11),
                IsRowGuidCol = r.GetBoolean(12),
                IsSparse = r.GetBoolean(13),
                Collation = r.IsDBNull(14) ? null : r.GetString(14),
                ComputedDefinition = r.IsDBNull(15) ? null : r.GetString(15),
                IsPersisted = r.GetBoolean(16),
                IdentitySeed = r.IsDBNull(17) ? null : r.GetString(17),
                IdentityIncrement = r.IsDBNull(18) ? null : r.GetString(18)
            };

            if (!raw.Columns.TryGetValue(objectId, out var list))
                raw.Columns[objectId] = list = [];
            list.Add(col);
        }

        // 4 - indexes
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var objectId = r.GetInt32(0);
            var indexId = r.GetInt32(1);
            var ix = new IndexInfo
            {
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                TypeDesc = r.IsDBNull(3) ? "NONCLUSTERED" : r.GetString(3),
                IsUnique = r.GetBoolean(4),
                IsPrimaryKey = r.GetBoolean(5),
                IsUniqueConstraint = r.GetBoolean(6),
                IsDisabled = r.GetBoolean(7),
                FillFactor = r.GetByte(8),
                IsPadded = r.GetBoolean(9),
                IgnoreDupKey = r.GetBoolean(10),
                FilterDefinition = r.IsDBNull(11) ? null : r.GetString(11),
                DataSpace = r.IsDBNull(12) ? null : r.GetString(12)
            };

            if (!raw.Indexes.TryGetValue(objectId, out var map))
                raw.Indexes[objectId] = map = [];
            map[indexId] = ix;
        }

        // 5 - index columns
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var objectId = r.GetInt32(0);
            var indexId = r.GetInt32(1);
            if (!raw.Indexes.TryGetValue(objectId, out var map) || !map.TryGetValue(indexId, out var ix))
                continue;

            var included = r.GetBoolean(4);
            var name = r.GetString(5);

            if (included) ix.IncludedColumns.Add(name);
            else ix.KeyColumns.Add(new IndexColumnInfo
            {
                Name = name,
                Ordinal = r.GetByte(2),
                Descending = r.GetBoolean(3)
            });
        }

        // 6 - foreign keys
        await r.NextResultAsync(ct);
        var fkById = new Dictionary<int, ForeignKeyInfo>();
        while (await r.ReadAsync(ct))
        {
            var fkId = r.GetInt32(0);
            var parentId = r.GetInt32(2);
            var fk = new ForeignKeyInfo
            {
                Name = r.GetString(1),
                ReferencedSchema = r.GetString(3),
                ReferencedTable = r.GetString(4),
                DeleteAction = r.IsDBNull(5) ? "NO_ACTION" : r.GetString(5),
                UpdateAction = r.IsDBNull(6) ? "NO_ACTION" : r.GetString(6),
                IsDisabled = r.GetBoolean(7),
                IsNotTrusted = r.GetBoolean(8)
            };
            fkById[fkId] = fk;

            if (!raw.ForeignKeys.TryGetValue(parentId, out var list))
                raw.ForeignKeys[parentId] = list = [];
            list.Add(fk);
        }

        // 7 - foreign key columns
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!fkById.TryGetValue(r.GetInt32(0), out var fk)) continue;
            fk.Columns.Add(r.GetString(2));
            fk.ReferencedColumns.Add(r.GetString(3));
        }

        // 8 - check constraints
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var parentId = r.GetInt32(0);
            var c = new ConstraintInfo
            {
                Kind = ConstraintKind.Check,
                Name = r.GetString(1),
                Definition = r.IsDBNull(2) ? "" : r.GetString(2),
                IsDisabled = r.GetBoolean(3),
                IsNotTrusted = r.GetBoolean(4),
                IsSystemNamed = r.GetBoolean(5)
            };
            if (!raw.Constraints.TryGetValue(parentId, out var list))
                raw.Constraints[parentId] = list = [];
            list.Add(c);
        }

        // 9 - default constraints
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var parentId = r.GetInt32(0);
            var c = new ConstraintInfo
            {
                Kind = ConstraintKind.Default,
                Name = r.GetString(1),
                Definition = r.IsDBNull(2) ? "" : r.GetString(2),
                IsSystemNamed = r.GetBoolean(3),
                ColumnName = r.GetString(4)
            };
            if (!raw.Constraints.TryGetValue(parentId, out var list))
                raw.Constraints[parentId] = list = [];
            list.Add(c);
        }

        // 10 - parameters
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var objectId = r.GetInt32(0);
            var p = new ParameterInfo
            {
                Ordinal = r.GetInt32(1),
                Name = r.IsDBNull(2) ? "" : r.GetString(2),
                DataType = SqlTypeFormatter.Format(
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetBoolean(5),
                    r.GetInt16(6),
                    r.GetByte(7),
                    r.GetByte(8)),
                IsOutput = r.GetBoolean(9),
                IsReadOnly = r.GetBoolean(10),
                HasDefault = r.GetBoolean(11)
            };
            if (!raw.Parameters.TryGetValue(objectId, out var list))
                raw.Parameters[objectId] = list = [];
            list.Add(p);
        }

        // 11 - synonyms
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.SynonymBases[$"{r.GetString(0)}.{r.GetString(1)}"] = r.IsDBNull(2) ? "" : r.GetString(2);

        // 12 - user defined scalar types
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.UserTypes.Add(new RawUserType(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                r.GetInt16(3), r.GetByte(4), r.GetByte(5), r.GetBoolean(6), r.GetString(7)));

        // 13 - table types
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
            raw.TableTypes.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));

        // 14 - sequences (2012+)
        if (majorVersion >= 11 && await r.NextResultAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                raw.SequenceProps[$"{r.GetString(0)}.{r.GetString(1)}"] = new Dictionary<string, string?>
                {
                    ["type"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["startValue"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["increment"] = r.IsDBNull(4) ? null : r.GetString(4),
                    ["minValue"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["maxValue"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["isCycling"] = r.GetBoolean(7) ? "1" : "0",
                    ["isCached"] = r.GetBoolean(8) ? "1" : "0",
                    ["cacheSize"] = r.GetInt32(9).ToString()
                };
            }
        }

        return raw;
    }

    // -----------------------------------------------------------------
    //  Raw rows -> DbObject graph
    // -----------------------------------------------------------------

    private static void Assemble(SchemaSnapshot snapshot, RawCatalog raw, CompareOptions options)
    {
        var objects = new List<DbObject>(raw.Objects.Count + 64);

        // schemas
        foreach (var (name, owner) in raw.Schemas)
        {
            if (options.IsExcluded(DbObjectType.Schema, name, name)) continue;
            objects.Add(new DbObject
            {
                Type = DbObjectType.Schema,
                Schema = name,
                Name = name,
                Properties = { ["owner"] = owner }
            });
        }

        foreach (var ro in raw.Objects)
        {
            if (ro.IsMsShipped && !options.IncludeSystemObjects) continue;

            var type = DbObjectTypeExtensions.FromSysType(ro.Type);
            if (type is null) continue;
            if (options.IsExcluded(type.Value, ro.Schema, ro.Name)) continue;

            var o = new DbObject
            {
                Type = type.Value,
                Schema = ro.Schema,
                Name = ro.Name,
                ModifyDate = ro.ModifyDate,
                ParentName = string.IsNullOrEmpty(ro.ParentName) ? null : $"{ro.ParentSchema}.{ro.ParentName}"
            };

            if (raw.Modules.TryGetValue(ro.ObjectId, out var m))
            {
                o.Definition = m.Definition;
                o.IsEncrypted = m.Encrypted;
                // A missing body with no encryption flag means the login cannot
                // see the code. Say so plainly instead of reporting a difference.
                o.DefinitionUnavailable = m.Definition is null && !m.Encrypted;
            }
            else if (type.Value.IsModule())
            {
                o.DefinitionUnavailable = true;
            }

            AttachChildren(o, ro.ObjectId, raw);

            if (type.Value == DbObjectType.Synonym &&
                raw.SynonymBases.TryGetValue($"{ro.Schema}.{ro.Name}", out var baseObj))
                o.Properties["baseObject"] = baseObj;

            if (type.Value == DbObjectType.Sequence &&
                raw.SequenceProps.TryGetValue($"{ro.Schema}.{ro.Name}", out var props))
                o.Properties = props;

            objects.Add(o);
        }

        // user defined scalar types
        foreach (var ut in raw.UserTypes)
        {
            if (options.IsExcluded(DbObjectType.UserDefinedType, ut.Schema, ut.Name)) continue;
            objects.Add(new DbObject
            {
                Type = DbObjectType.UserDefinedType,
                Schema = ut.Schema,
                Name = ut.Name,
                Properties =
                {
                    ["baseType"] = SqlTypeFormatter.Format(ut.BaseType, null, false, ut.MaxLength, ut.Precision, ut.Scale),
                    ["isNullable"] = ut.IsNullable ? "1" : "0",
                    ["collation"] = ut.Collation
                }
            });
        }

        // table types - their columns hang off the internal type_table_object_id
        foreach (var (schema, name, objectId) in raw.TableTypes)
        {
            if (options.IsExcluded(DbObjectType.TableType, schema, name)) continue;
            var o = new DbObject
            {
                Type = DbObjectType.TableType,
                Schema = schema,
                Name = name
            };
            AttachChildren(o, objectId, raw);
            objects.Add(o);
        }

        snapshot.Objects = objects;
    }

    private static void AttachChildren(DbObject o, int objectId, RawCatalog raw)
    {
        if (raw.Columns.TryGetValue(objectId, out var cols))
            o.Columns = [.. cols.OrderBy(c => c.Ordinal)];

        if (raw.Indexes.TryGetValue(objectId, out var ixMap))
            o.Indexes = [.. ixMap.Values];

        if (raw.ForeignKeys.TryGetValue(objectId, out var fks))
            o.ForeignKeys = fks;

        if (raw.Constraints.TryGetValue(objectId, out var cons))
            o.Constraints = cons;

        if (raw.Parameters.TryGetValue(objectId, out var pars))
            o.Parameters = [.. pars.OrderBy(p => p.Ordinal)];
    }
}
