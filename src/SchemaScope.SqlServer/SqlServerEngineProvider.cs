using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.SqlServer;

/// <summary>
/// SQL Server behind the engine seam. Every member delegates to the classes SQL Server has always used
/// (<see cref="SqlServerConnectionService"/>, <see cref="SqlServerSchemaReader"/>, <see cref="TSqlText"/>,
/// <see cref="CanonicalScriptWriter"/>, <see cref="SchemaNormalizer"/>), so nothing it does differs from before.
/// </summary>
public sealed class SqlServerEngineProvider(SqlServerConnectionService connections) : IEngineProvider
{
    public SqlServerEngineProvider() : this(new SqlServerConnectionService()) { }

    public DatabaseEngine Engine => DatabaseEngine.SqlServer;
    public string DisplayName => "SQL Server";

    public EngineCapabilities Capabilities { get; } = new(
        Schemas: true, Sequences: true, Synonyms: true, TableTypes: true, AliasTypes: true,
        FilteredIndexes: true, IncludeColumns: true, RoutineOverloads: false, ByteExactModuleText: true,
        Ddl: TransactionSemantics.PerFileTransactional, MaxIdentifierBytes: 256,   // sysname: nvarchar(128)
        ObjectTypes: Enum.GetValues<DbObjectType>().ToHashSet(),
        Features: new HashSet<EngineFeature>
            { EngineFeature.Compare, EngineFeature.Scripts, EngineFeature.ObjectScripts, EngineFeature.Apply, EngineFeature.Destructive },
        Preview: false);

    public ISqlDialect Dialect { get; } = new SqlServerDialect();
    public IConnectionService Connections { get; } = connections;
    public IServerIdentity Identity { get; } = new SqlServerIdentity();
    public ISchemaReader CreateReader() => new SqlServerSchemaReader();
}

/// <summary>T-SQL text rules: the static helpers the compare has always called.</summary>
public sealed class SqlServerDialect : ISqlDialect
{
    public DatabaseEngine Engine => DatabaseEngine.SqlServer;
    public IdentifierFold Fold => IdentifierFold.Upper;
    public string DefaultSchema => "dbo";
    public IReadOnlyList<string> DefaultExcludedSchemas { get; } = new CompareOptions().ExcludedSchemas.AsReadOnly();
    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    public string LightNormalize(string body) => TSqlText.Light(body);
    public string CanonicalHeader(string body, DbObject o) => TSqlText.CanonicalHeaderName(body, o.Schema, o.Name);
    public string StripComments(string body) => TSqlText.StripComments(body);
    public string Loosen(string text) => SchemaNormalizer.Loosen(text);
    public bool CanDeepParse => true;
    public bool ParsesIdentically(string a, string b) => SchemaNormalizer.ParsesIdentically(a, b);
    public string CanonicalScript(DbObject o, CompareOptions opt) => CanonicalScriptWriter.Write(o, opt);
    public string MonacoLanguage => "sql";

    /// <summary>Monaco's default separators less @, # and $, which are part of a T-SQL name (as ReferenceFinder reads them).</summary>
    public string WordSeparators => "`~!%^&*()-=+[{]}\\|;:'\",.<>/?";
}

/// <summary>
/// Who a SQL Server is. One spelling for one server, so a protected connection is matched however a target names
/// its server: "tcp:" / "np:" / "lpc:" / "admin:" and ",port" dropped, '.', "(local)", "localhost" and the loopback
/// addresses read as this machine, a DNS suffix dropped (SERVERPROPERTY('ServerName') has none), and the default
/// instance's name left out. Another machine's IP address is not resolved: it only matches the same address.
/// </summary>
public sealed class SqlServerIdentity : IServerIdentity
{
    public static string Normalise(string? server)
    {
        var s = (server ?? "").Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..].Trim(); break; }
        // a named pipe, \\host\pipe\path: the machine, and the instance its path names
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var pipeHost = HostNames.Normalise(s[2..].Split('\\')[0]);
            if (pipeHost.Length == 0) return "";
            var pipe = PipeInstance(s);
            return (pipe is { Length: > 0 } named ? $"{pipeHost}\\{named}" : pipeHost).ToUpperInvariant();
        }
        if (s.IndexOf(',') is var comma and >= 0) s = s[..comma];

        var slash = s.IndexOf('\\');
        var host = HostNames.Normalise(slash >= 0 ? s[..slash] : s);
        var instance = slash >= 0 ? s[(slash + 1)..].Trim() : "";

        if (instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)) instance = "";
        return (instance.Length > 0 ? $"{host}\\{instance}" : host).ToUpperInvariant();
    }

    /// <summary>
    /// The instance a named pipe (\\host\pipe\path) names: "" for the default instance's sql\query, NAME for
    /// MSSQL$NAME\sql\query, and a LocalDB pipe's own LOCALDB#... (as that server names itself). Null when the path has
    /// none of those shapes, so which instance it reaches cannot be told.
    /// </summary>
    private static string? PipeInstance(string pipe)
    {
        var parts = pipe.TrimStart('\\').Split('\\');
        if (parts.Length < 3 || !parts[1].Equals("pipe", StringComparison.OrdinalIgnoreCase)) return null;
        var path = parts[2..];
        static bool SqlQuery(string[] rest) => rest.Length == 2 && rest[0].Equals("sql", StringComparison.OrdinalIgnoreCase) &&
                                               rest[1].Equals("query", StringComparison.OrdinalIgnoreCase);
        if (SqlQuery(path)) return "";
        if (path.Length == 3 && path[0].StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase) && path[0].Length > 6 && SqlQuery(path[1..]))
            return path[0][6..].Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "" : path[0][6..];
        if (path.Length >= 1 && path[0].StartsWith("LOCALDB#", StringComparison.OrdinalIgnoreCase) && path[0].Length > 8) return path[0];
        return null;
    }

    /// <summary>
    /// True when <paramref name="server"/> cannot be pinned to one instance: no machine can be read from it, or it is a
    /// named pipe whose path names no instance SchemaScope knows. A protected one covers its whole machine (fail closed).
    /// </summary>
    public static bool InstanceUnknown(string? server)
    {
        var s = (server ?? "").Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..].Trim(); break; }
        return Host(server).Length == 0 || (s.StartsWith(@"\\", StringComparison.Ordinal) && PipeInstance(s) is null);
    }

    public static bool Same(string? a, string? b) => Normalise(a) == Normalise(b);

    /// <summary>The machine alone, instance and port dropped: what the Protected rule matches across engines.</summary>
    public static string Host(string? server)
    {
        var s = Normalise(server);
        var slash = s.IndexOf('\\');
        return slash >= 0 ? s[..slash] : s;
    }

    public IReadOnlyList<string> HostKeys(EndpointInfo e) => [.. e.Hosts.Select(HostNames.Normalise).Distinct()];

    /// <summary>
    /// The server's normalised name and the database. Apply keeps its own key (<c>ApplyService.TargetKey</c>, what the
    /// server reports about itself); this one is for callers that only have the connection string.
    /// </summary>
    public string TargetKey(EndpointInfo e, string database)
    {
        var server = e.Servers is { Count: > 0 } s ? Normalise(s[0])
            : e.Hosts.Count > 0 ? Normalise(e.Instance is null ? e.Hosts[0] : $"{e.Hosts[0]}\\{e.Instance}") : "";
        return $"{(int)DatabaseEngine.SqlServer}|{server}|{e.Port}|{database.Trim()}";
    }
}

/// <summary>Registries built without DI.</summary>
public static class EngineRegistries
{
    /// <summary>SQL Server alone: what the legacy constructors and the tests use.</summary>
    public static EngineRegistry SqlServerOnly(SqlServerConnectionService? connections = null) =>
        new([new SqlServerEngineProvider(connections ?? new SqlServerConnectionService())]);
}
