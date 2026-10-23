using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;

namespace SchemaScope.PostgreSql;

/// <summary>
/// PostgreSQL behind the engine seam, as a preview: read, compare, and scripts written to a folder (whole database and
/// single object). Apply and changes that can lose data are not offered yet (the server refuses them from these
/// capabilities, whatever the UI shows).
/// </summary>
public sealed class PgEngineProvider(PgConnectionService connections) : IEngineProvider
{
    public PgEngineProvider() : this(new PgConnectionService()) { }

    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;
    public string DisplayName => "PostgreSQL";

    public EngineCapabilities Capabilities { get; } = new(
        Schemas: true, Sequences: true, Synonyms: false, TableTypes: false, AliasTypes: true,
        FilteredIndexes: true, IncludeColumns: true, RoutineOverloads: true,
        // pg_get_functiondef rebuilds a routine's header; only the body is the text as written
        ByteExactModuleText: false,
        Ddl: TransactionSemantics.PerFileTransactional, MaxIdentifierBytes: 63,
        ObjectTypes: new HashSet<DbObjectType>
        {
            DbObjectType.Schema, DbObjectType.Table, DbObjectType.View, DbObjectType.MaterializedView,
            DbObjectType.StoredProcedure, DbObjectType.ScalarFunction, DbObjectType.TableValuedFunction,
            DbObjectType.AggregateFunction, DbObjectType.Trigger, DbObjectType.Sequence, DbObjectType.Domain,
            DbObjectType.EnumType, DbObjectType.CompositeType, DbObjectType.Extension
        },
        Features: new HashSet<EngineFeature> { EngineFeature.Compare, EngineFeature.Scripts, EngineFeature.ObjectScripts },
        Preview: true);

    public ISqlDialect Dialect { get; } = new PgDialect();
    public IConnectionService Connections { get; } = connections;
    public IServerIdentity Identity { get; } = new PgIdentity();
    public ISchemaReader CreateReader() => new PgSchemaReader();
}

/// <summary>Who a PostgreSQL server is: its hosts as <see cref="HostNames"/> spells them, and engine|host|port|db.</summary>
public sealed class PgIdentity : IServerIdentity
{
    public IReadOnlyList<string> HostKeys(EndpointInfo e) => [.. e.Hosts.Select(HostNames.Normalise).Distinct()];

    public string TargetKey(EndpointInfo e, string database) =>
        $"{(int)DatabaseEngine.PostgreSql}|{(e.Hosts.Count > 0 ? HostNames.Normalise(e.Hosts[0]) : "")}|" +
        $"{e.Port ?? DatabaseEngines.DefaultPort(DatabaseEngine.PostgreSql)}|{database.Trim()}";
}
