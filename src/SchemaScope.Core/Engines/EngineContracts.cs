using SchemaScope.Core.Model;

namespace SchemaScope.Core.Engines;

/// <summary>What SchemaScope can do with an engine. The server checks these whatever the UI shows.</summary>
public enum EngineFeature { Compare, Scripts, ObjectScripts, Apply, Destructive, CrossCompare, CrossScripts }

/// <summary>Whether a failed script file can be rolled back: SQL Server and PostgreSQL yes, MySQL's DDL no.</summary>
public enum TransactionSemantics { PerFileTransactional, NoDdlRollback }

/// <summary>How an engine's catalog spells names, and so how object keys fold.</summary>
public enum IdentifierFold
{
    /// <summary>SQL Server today: keys upper-cased.</summary>
    Upper,
    /// <summary>PostgreSQL: the catalog already holds the folded name.</summary>
    Exact,
    /// <summary>MySQL with lower_case_table_names 1/2, and its column names.</summary>
    Lower
}

/// <summary>What one engine has and what SchemaScope offers for it. Served as is by GET /api/engines.</summary>
public sealed record EngineCapabilities(
    bool Schemas, bool Sequences, bool Synonyms, bool TableTypes, bool AliasTypes,
    bool FilteredIndexes, bool IncludeColumns, bool RoutineOverloads, bool ByteExactModuleText,
    TransactionSemantics Ddl, int MaxIdentifierBytes, IReadOnlySet<DbObjectType> ObjectTypes,
    IReadOnlySet<EngineFeature> Features, bool Preview);

/// <summary>Connection strings, connection tests and database lists for one engine, with that engine's own driver.</summary>
public interface IConnectionService
{
    DatabaseEngine Engine { get; }
    string BuildConnectionString(ConnectionSettings s, string? overrideDatabase = null);
    Task<ConnectionTestResult> TestAsync(string connectionString, CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseListItem>> ListDatabasesAsync(string connectionString, CancellationToken ct = default);

    /// <summary>The database Browse connects to: "master", "postgres", or null (MySQL needs none).</summary>
    string? BrowseDatabase { get; }

    /// <summary>Where the string really goes, read by the driver's own builder. Null: the caller treats it as Protected and releases no secret.</summary>
    EndpointInfo? ParseEndpoint(string connectionString);

    /// <summary>Every secret value in the string: Password, SSL Password, CertificatePassword, ...</summary>
    IReadOnlyList<string> SecretValues(string connectionString);

    /// <summary>The string with every secret value masked; masked whole when it cannot be read.</summary>
    string Mask(string connectionString);
}

/// <summary>One engine's SQL text rules, as the compare and the UI need them.</summary>
public interface ISqlDialect
{
    DatabaseEngine Engine { get; }
    IdentifierFold Fold { get; }

    /// <summary>dbo | public | the database.</summary>
    string DefaultSchema { get; }
    IReadOnlyList<string> DefaultExcludedSchemas { get; }
    string Quote(string identifier);
    string LightNormalize(string body);
    string CanonicalHeader(string body, DbObject o);
    string StripComments(string body);

    /// <summary>Comments, whitespace, this dialect's quote characters and case removed.</summary>
    string Loosen(string text);

    /// <summary>A real parser is available for the formatting-only check (ScriptDom: SQL Server only).</summary>
    bool CanDeepParse { get; }

    /// <summary>False when there is no parser: never claims FormattingOnly on a guess.</summary>
    bool ParsesIdentically(string a, string b);
    string CanonicalScript(DbObject o, CompareOptions opt);

    /// <summary>"sql" | "pgsql" | "mysql".</summary>
    string MonacoLanguage { get; }

    /// <summary>Characters that end a word for the editor, the characters <c>ReferenceFinder</c> does not count as part of a name.</summary>
    string WordSeparators { get; }
}

/// <summary>Who a server is, for the Protected rule (host alone, across engines) and the one-apply-per-target lock.</summary>
public interface IServerIdentity
{
    IReadOnlyList<string> HostKeys(EndpointInfo e);

    /// <summary>engine|host|port|db.</summary>
    string TargetKey(EndpointInfo e, string database);
}

/// <summary>Everything SchemaScope needs from one engine. Registered in the owner app only, never in the share viewer.</summary>
public interface IEngineProvider
{
    DatabaseEngine Engine { get; }
    string DisplayName { get; }
    EngineCapabilities Capabilities { get; }
    ISqlDialect Dialect { get; }
    IConnectionService Connections { get; }
    IServerIdentity Identity { get; }
    ISchemaReader CreateReader();
}

/// <summary>An engine this build has no provider for.</summary>
public sealed class EngineNotAvailableException(DatabaseEngine engine)
    : InvalidOperationException($"{(Enum.IsDefined(engine) ? engine.ToString() : "That database")} is not available in this version of SchemaScope.")
{
    public DatabaseEngine Engine { get; } = engine;
}
