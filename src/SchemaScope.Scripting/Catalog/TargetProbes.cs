using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SchemaScope.Scripting;

/// <summary>Result of a data probe. <see cref="Count"/> is null when the probe did not run or failed; <see cref="NotCheckedReason"/> says why.</summary>
public sealed record ProbeCount(long? Count, string? NotCheckedReason = null)
{
    /// <summary>True when the probe ran and <see cref="Count"/> is real.</summary>
    public bool Checked => Count is not null;
}

/// <summary>A target object that something else depends on (sys.sql_expression_dependencies).</summary>
/// <param name="Referenced">OBJECT_NAME of the referenced object (no schema - matches Python, which keyed guards by name).</param>
/// <param name="ReferencedBy">OBJECT_NAME of the referencing object.</param>
/// <param name="ReferencingTypeDesc">sys.objects.type_desc of the referencing object (e.g. SQL_SCALAR_FUNCTION, CHECK_CONSTRAINT).</param>
public sealed record ExpressionDependent(string Referenced, string ReferencedBy, string ReferencingTypeDesc);

/// <summary>A DML trigger and the table it is on.</summary>
public sealed record TriggerParent(ObjectName Trigger, ObjectName Table);

/// <summary>One edge of the module dependency graph: <see cref="Referencing"/> uses <see cref="Referenced"/>.</summary>
public sealed record ModuleDependency(ObjectName Referencing, ObjectName Referenced);

/// <summary>A row of sys.objects: name and RTRIM(type).</summary>
public sealed record CatalogObject(ObjectName Name, string Type);

/// <summary>An alias type (<c>CREATE TYPE s.n FROM base</c>); <see cref="BaseType"/> is the system type's name.</summary>
public sealed record AliasTypeRow(string Sch, string Name, string BaseType, int MaxLength, int Precision, int Scale, bool IsNullable)
{
    public ObjectName Key => new(Sch, Name);
}

/// <summary>A sequence. The values are sys.sequences' sql_variant columns as text.</summary>
public sealed record SequenceRow(string Sch, string Name, string Type, int Precision, int Scale,
    string Start, string Increment, string Min, string Max, bool IsCycling, bool IsCached, int? CacheSize)
{
    public ObjectName Key => new(Sch, Name);
}

/// <summary>A synonym and the name it stands for, as sys.synonyms holds it.</summary>
public sealed record SynonymRow(string Sch, string Name, string BaseObject)
{
    public ObjectName Key => new(Sch, Name);
}

/// <summary>A module that references a table type (as a parameter, or <c>DECLARE @t s.TT</c> in its body).</summary>
/// <param name="Type">The table type, with the TYPE's own schema.</param>
/// <param name="Module">The referencing module.</param>
public sealed record TypeReference(ObjectName Type, ObjectName Module);

/// <summary>Result of <see cref="SourceProbes.CompileCheckAsync(ObjectName, string, CancellationToken)"/>.</summary>
/// <param name="Compiles">
/// true = its CREATE would succeed; false = SQL Server would refuse the CREATE (see
/// <see cref="SourceProbes.IsCreateFailure"/>); null = not checked.
/// </param>
/// <param name="ErrorNumber">The error number when <paramref name="Compiles"/> is false, or the deferred one (208/2020) when <paramref name="Note"/> is set.</param>
/// <param name="Message">The server's message (false) or why the check could not run (null).</param>
/// <param name="Note">
/// Set when <paramref name="Compiles"/> is true but the module names an object that does not exist.
/// Procedures, scalar/multi-statement functions and triggers may do that (deferred name resolution):
/// the CREATE succeeds and the module fails only when run. Generators create it and put the note in review.
/// </param>
public sealed record CompileCheck(bool? Compiles, int? ErrorNumber = null, string? Message = null, string? Note = null);

/// <summary>
/// Query Store execution totals on the target, over everything Query Store holds.
/// <see cref="Available"/> is false when Query Store could not be read; then every count is 0,
/// which leaves "least used first" orderings alphabetical, as in the Python.
/// </summary>
public sealed class QueryStoreUsage
{
    /// <summary>The views could be read (Query Store may still be OFF with old data, or empty).</summary>
    public bool Available { get; init; }
    /// <summary>sys.database_query_store_options.actual_state_desc, e.g. READ_WRITE, OFF; null if unknown.</summary>
    public string? State { get; init; }
    /// <summary>Earliest first_execution_time Query Store holds.</summary>
    public DateTimeOffset? First { get; init; }
    /// <summary>Latest last_execution_time Query Store holds.</summary>
    public DateTimeOffset? Last { get; init; }
    /// <summary>Executions summed per OBJECT_NAME, ignoring case and schema (08's ordering key).</summary>
    public IReadOnlyDictionary<string, long> RunsByName { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Executions per (schema, object), ignoring case (flow_safety's key).</summary>
    public IReadOnlyDictionary<ObjectName, long> RunsByObject { get; init; } = new Dictionary<ObjectName, long>(ObjectName.IgnoreCase);

    /// <summary>Executions of any object with this name (0 when unknown).</summary>
    public long RunsFor(string name) => RunsByName.TryGetValue(name, out var n) ? n : 0;
    /// <summary>Executions of this exact (schema, object), case-insensitive (0 when unknown).</summary>
    public long RunsFor(ObjectName name) => RunsByObject.TryGetValue(name, out var n) ? n : 0;
    /// <summary>
    /// "first → last UTC" for reports (Query Store stores UTC), or a sentence saying there is no window.
    /// Culture-invariant: in a custom format ':' is otherwise the culture's time separator ("19.06" under fi-FI).
    /// </summary>
    public string WindowText => First is { } f && Last is { } l
        ? $"{Utc(f)} → {Utc(l)} UTC"
        : Available ? "Query Store holds no runtime data" : "Query Store could not be read";

    private static string Utc(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-dd HH':'mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Read-only catalog probes shared by source and target. Every probe runs on its own pooled
/// connection with its own timeout, never throws for a SQL error (it returns null and records
/// a "not checked" note), and never sends anything but a SELECT.
/// </summary>
public abstract class CatalogProbes
{
    private readonly List<string> notChecked = [];
    private readonly Dictionary<string, Task> cache = [];

    protected CatalogProbes(string label, string connectionString, int timeoutSeconds)
    {
        Label = label;
        ConnectionString = SqlProbe.Tune(connectionString);
        TimeoutSeconds = timeoutSeconds <= 0 ? 60 : timeoutSeconds;
    }

    /// <summary>The endpoint label, used in notes ("C-Tech: ... not checked").</summary>
    public string Label { get; }
    /// <summary>Per-probe command timeout in seconds.</summary>
    public int TimeoutSeconds { get; }
    /// <summary>The tuned connection string. Never logged, never returned to callers outside the probes.</summary>
    protected string ConnectionString { get; }

    /// <summary>One line per probe that could not run, e.g. "C-Tech: Query Store usage not checked (...)". Thread-safe snapshot.</summary>
    public IReadOnlyList<string> NotChecked { get { lock (notChecked) return notChecked.ToList(); } }

    /// <summary>All schema names (<c>sys.schemas</c>) - 08's "known names" set uses them instead of just dbo.</summary>
    public Task<IReadOnlyList<string>?> SchemaNamesAsync(CancellationToken ct = default) =>
        Cached("schemas", () => ListAsync("schema names", "SELECT name FROM sys.schemas", r => r.GetString(0), ct));

    /// <summary>User schemas (not dbo, guest, sys, INFORMATION_SCHEMA or the fixed database-role schemas).</summary>
    public Task<IReadOnlyList<string>?> UserSchemasAsync(CancellationToken ct = default) =>
        Cached("userSchemas", () => ListAsync("schemas",
            "SELECT name FROM sys.schemas WHERE schema_id > 4 AND schema_id < 16384",
            r => Str(r, 0), ct));

    /// <summary>Alias types (not table types, not CLR types).</summary>
    public Task<IReadOnlyList<AliasTypeRow>?> AliasTypesAsync(CancellationToken ct = default) =>
        Cached("aliasTypes", () => ListAsync("alias types", """
            SELECT SCHEMA_NAME(t.schema_id) sch, t.name, TYPE_NAME(t.system_type_id) base,
                   t.max_length, t.precision, t.scale, t.is_nullable
            FROM sys.types t WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND t.is_assembly_type = 0
            """, r => new AliasTypeRow(Str(r, 0), Str(r, 1), Str(r, 2), Convert.ToInt32(r.GetValue(3)),
                Convert.ToInt32(r.GetValue(4)), Convert.ToInt32(r.GetValue(5)), r.GetBoolean(6)), ct));

    /// <summary>Sequences, with their numbers as text (sql_variant).</summary>
    public Task<IReadOnlyList<SequenceRow>?> SequencesAsync(CancellationToken ct = default) =>
        Cached("sequences", () => ListAsync("sequences", """
            SELECT SCHEMA_NAME(s.schema_id) sch, s.name, TYPE_NAME(s.system_type_id) typ, s.precision, s.scale,
                   CONVERT(nvarchar(60), s.start_value), CONVERT(nvarchar(60), s.increment),
                   CONVERT(nvarchar(60), s.minimum_value), CONVERT(nvarchar(60), s.maximum_value),
                   s.is_cycling, s.is_cached, s.cache_size
            FROM sys.sequences s
            """, r => new SequenceRow(Str(r, 0), Str(r, 1), Str(r, 2), Convert.ToInt32(r.GetValue(3)), Convert.ToInt32(r.GetValue(4)),
                Str(r, 5), Str(r, 6), Str(r, 7), Str(r, 8), r.GetBoolean(9), r.GetBoolean(10),
                r.IsDBNull(11) ? null : Convert.ToInt32(r.GetValue(11))), ct));

    /// <summary>Synonyms and their base names.</summary>
    public Task<IReadOnlyList<SynonymRow>?> SynonymsAsync(CancellationToken ct = default) =>
        Cached("synonyms", () => ListAsync("synonyms",
            "SELECT SCHEMA_NAME(schema_id) sch, name, base_object_name FROM sys.synonyms",
            r => new SynonymRow(Str(r, 0), Str(r, 1), Str(r, 2)), ct));

    /// <summary>Every sys.objects row (schema, name, type). Constraint-name collisions are checked against this, not only constraints.</summary>
    public Task<IReadOnlyList<CatalogObject>?> ObjectsAsync(CancellationToken ct = default) =>
        Cached("objects", () => ListAsync("object names", """
            SELECT s.name sch, o.name obj, RTRIM(o.type) t FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            """, r => new CatalogObject(new ObjectName(r.GetString(0), r.GetString(1)), r.GetString(2)), ct));

    /// <summary>Disabled DML triggers (schema, name). Python keyed this by name only; <see cref="ObjectName.Name"/> gives that.</summary>
    public Task<IReadOnlyList<ObjectName>?> DisabledTriggersAsync(CancellationToken ct = default) =>
        Cached("disabledTriggers", () => ListAsync("disabled triggers", """
            SELECT ISNULL(OBJECT_SCHEMA_NAME(object_id), N'') sch, name o FROM sys.triggers WHERE is_disabled = 1
            """, r => new ObjectName(r.GetString(0), r.GetString(1)), ct));

    /// <summary>
    /// Trigger names set as first or last (sp_settriggerorder). On the target, a DROP + CREATE resets the
    /// order (08 guard); on the source, a trigger 07 creates loses it (07 lists it in review).
    /// </summary>
    public Task<IReadOnlyList<string>?> OrderedTriggerNamesAsync(CancellationToken ct = default) =>
        Cached("trgOrder", () => ListAsync("trigger order", """
            SELECT DISTINCT OBJECT_NAME(object_id) FROM sys.trigger_events WHERE is_first = 1 OR is_last = 1
            """, r => Str(r, 0), ct));

    /// <summary>When each module was last changed (sys.objects.modify_date, server local time). 10 compares the two sides.</summary>
    public Task<IReadOnlyList<(ObjectName Name, DateTime Modified)>?> ModuleDatesAsync(CancellationToken ct = default) =>
        Cached("moduleDates", () => ListAsync("module change dates", """
            SELECT s.name sch, o.name obj, o.modify_date FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR')
            """, r => (new ObjectName(r.GetString(0), r.GetString(1)), r.GetDateTime(2)), ct));

    /// <summary>Each DML trigger and its parent table (check_flow.py TRIGGER_TABLE).</summary>
    public Task<IReadOnlyList<TriggerParent>?> TriggerParentsAsync(CancellationToken ct = default) =>
        Cached("triggerParents", () => ListAsync("trigger parents", """
            SELECT OBJECT_SCHEMA_NAME(tr.object_id) sch, tr.name trg,
                   OBJECT_SCHEMA_NAME(tr.parent_id) tsch, OBJECT_NAME(tr.parent_id) tbl
                FROM sys.triggers tr WHERE tr.parent_class = 1
            """, r => new TriggerParent(
                new ObjectName(Str(r, 0), Str(r, 1)), new ObjectName(Str(r, 2), Str(r, 3))), ct));

    /// <summary>
    /// Module-to-object dependencies inside this database (sys.sql_expression_dependencies, referencing
    /// and referenced class = object; type references are in <see cref="TypeReferencersAsync"/>), for ordering
    /// 03 so a view or function is created after what it reads. A resolved reference uses the object's real
    /// schema; an unresolved one the schema written in the code; an unqualified unresolved one gives an edge
    /// to the module's own schema and, when that is not dbo, one to dbo as well (SQL Server looks in both).
    /// Edges to objects nobody creates are harmless to a topological order.
    /// </summary>
    public Task<IReadOnlyList<ModuleDependency>?> ModuleDependenciesAsync(CancellationToken ct = default) =>
        Cached("moduleDeps", async () =>
        {
            var rows = await ListAsync("module dependencies", """
                SELECT OBJECT_SCHEMA_NAME(d.referencing_id) rsch, OBJECT_NAME(d.referencing_id) robj,
                       OBJECT_SCHEMA_NAME(d.referenced_id) resolved, d.referenced_schema_name written,
                       d.referenced_entity_name dobj
                FROM sys.sql_expression_dependencies d
                WHERE d.referencing_class = 1 AND d.referenced_class = 1
                  AND d.referenced_database_name IS NULL AND d.referenced_server_name IS NULL
                  AND d.referenced_entity_name IS NOT NULL AND OBJECT_NAME(d.referencing_id) IS NOT NULL
                """, r => (Rsch: Str(r, 0), Robj: Str(r, 1), Resolved: NStr(r, 2), Written: NStr(r, 3), Dobj: Str(r, 4)), ct);
            if (rows is null) return (IReadOnlyList<ModuleDependency>?)null;
            var list = new List<ModuleDependency>(rows.Count);
            foreach (var (rsch, robj, resolved, written, dobj) in rows)
            {
                var from = new ObjectName(rsch, robj);
                var schema = resolved ?? written;
                if (schema is not null)
                {
                    list.Add(new ModuleDependency(from, new ObjectName(schema, dobj)));
                    continue;
                }
                list.Add(new ModuleDependency(from, new ObjectName(rsch, dobj)));
                if (!string.Equals(rsch, "dbo", StringComparison.OrdinalIgnoreCase))
                    list.Add(new ModuleDependency(from, new ObjectName("dbo", dobj)));
            }
            return (IReadOnlyList<ModuleDependency>?)list;
        });

    /// <summary>
    /// Every module that references a user table type - as a parameter (sys.parameters) or in its body
    /// (<c>DECLARE @t s.TT</c>, sys.sql_expression_dependencies class 6) - keyed by the TYPE's own schema.
    /// DROP TYPE is refused while any of them exists, so 02's blockers should come from here rather than
    /// from <see cref="ScriptSchema.TtParams"/> alone (which misses body references).
    /// </summary>
    public Task<IReadOnlyList<TypeReference>?> TypeReferencersAsync(CancellationToken ct = default) =>
        Cached("typeRefs", () => ListAsync("table type references", """
            SELECT SCHEMA_NAME(tt.schema_id) tsch, tt.name tt,
                   OBJECT_SCHEMA_NAME(d.referencing_id) sch, OBJECT_NAME(d.referencing_id) obj
            FROM sys.sql_expression_dependencies d
            JOIN sys.table_types tt ON tt.user_type_id = d.referenced_id
            WHERE d.referenced_class = 6 AND d.referencing_class = 1 AND tt.is_user_defined = 1
              AND OBJECT_NAME(d.referencing_id) IS NOT NULL
            UNION
            SELECT SCHEMA_NAME(tt.schema_id), tt.name, OBJECT_SCHEMA_NAME(p.object_id), OBJECT_NAME(p.object_id)
            FROM sys.parameters p
            JOIN sys.table_types tt ON tt.user_type_id = p.user_type_id
            WHERE tt.is_user_defined = 1 AND OBJECT_NAME(p.object_id) IS NOT NULL
            """, r => new TypeReference(new ObjectName(Str(r, 0), Str(r, 1)), new ObjectName(Str(r, 2), Str(r, 3))), ct));

    /// <summary>Records a "not checked" note. Generators may use it for their own skipped checks.</summary>
    public void NoteNotChecked(string what, string reason)
    {
        lock (notChecked) notChecked.Add($"{Label}: {what} not checked ({reason})");
    }

    /// <summary>Runs a catalog-wide probe once per run and hands every later caller the same task.</summary>
    protected Task<T> Cached<T>(string key, Func<Task<T>> run)
    {
        lock (cache)
        {
            if (cache.TryGetValue(key, out var t)) return (Task<T>)t;
            var task = run();
            cache[key] = task;
            return task;
        }
    }

    /// <summary>Runs a SELECT and maps every row; null (and a note) on any SQL error or timeout.</summary>
    protected async Task<IReadOnlyList<T>?> ListAsync<T>(
        string what, string sql, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = TimeoutSeconds };
            cmd.Parameters.AddRange(parameters);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<T>();
            while (await r.ReadAsync(ct)) list.Add(map(r));
            return list;
        }
        catch (Exception ex) when (IsProbeFailure(ex, ct))
        {
            NoteNotChecked(what, SqlProbe.Reason(ex));
            return null;
        }
    }

    /// <summary>Runs a SELECT that returns one number; a <see cref="ProbeCount"/> with the reason on failure.</summary>
    /// <summary>
    /// Msg 207/208: the probe names a column or table the target does not have yet (01/03 add it). The
    /// caller reports that as "not testable yet" in its own report, so it is not also a "not checked" warning.
    /// </summary>
    protected static readonly IReadOnlySet<int> NotThereYet = new HashSet<int> { 207, 208 };

    /// <param name="expectedErrors">Server error numbers that are an answer for this probe, not a failure: returned as the reason, no "not checked" note.</param>
    protected async Task<ProbeCount> CountAsync(string what, string sql, CancellationToken ct, bool readUncommitted = false,
        IReadOnlySet<int>? expectedErrors = null)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            // Data probes run against a live plant database. READ UNCOMMITTED keeps a big
            // GROUP BY from holding shared locks that would block the plant's writers; the
            // count only decides whether a statement is held back for review.
            var text = readUncommitted ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n" + sql : sql;
            await using var cmd = new SqlCommand(text, conn) { CommandTimeout = TimeoutSeconds };
            var v = await cmd.ExecuteScalarAsync(ct);
            return new ProbeCount(v is null or DBNull ? 0 : Convert.ToInt64(v));
        }
        catch (Exception ex) when (IsProbeFailure(ex, ct))
        {
            var reason = SqlProbe.Reason(ex);
            if (!(ex is SqlException se && expectedErrors?.Contains(se.Number) == true)) NoteNotChecked(what, reason);
            return new ProbeCount(null, reason);
        }
    }

    // Cancellation by the caller must propagate; everything SQL- or network-shaped is a probe failure.
    private static bool IsProbeFailure(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested) &&
        ex is SqlException or InvalidOperationException or TimeoutException or OperationCanceledException or IOException;

    protected static string Str(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    protected static string? NStr(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    /// <summary><c>[a], [b]</c> with each name bracket-escaped.</summary>
    protected static string ColumnList(IEnumerable<string> cols) => string.Join(", ", cols.Select(Ddl.Bracket));
}

/// <summary>
/// Read-only probes on the TARGET (python.md §6B–E): duplicate keys, row counts, Query Store
/// usage, the drop/rename guards of 08/09, and the existing-data checks of flow_safety.
/// Catalog-wide probes run once per run and are cached; data probes run per call and are
/// skipped when <see cref="CheckExistingData"/> is off.
/// </summary>
public sealed partial class TargetProbes : CatalogProbes
{
    /// <param name="label">Target label, used in notes only.</param>
    /// <param name="connectionString">Target connection string - never written anywhere.</param>
    /// <param name="timeoutSeconds">Per-probe timeout (<see cref="ScriptOptions.ProbeTimeoutSeconds"/>).</param>
    /// <param name="checkExistingData">False = data probes (COUNT queries over user tables) return "not checked".</param>
    public TargetProbes(string label, string connectionString, int timeoutSeconds, bool checkExistingData)
        : base(label, connectionString, timeoutSeconds)
    {
        CheckExistingData = checkExistingData;
    }

    /// <summary>Convenience: endpoint + options.</summary>
    public TargetProbes(ScriptEndpoint target, ScriptOptions options)
        : this(target.Label, target.ConnectionString, options.ProbeTimeoutSeconds, options.CheckExistingData) { }

    /// <summary>Whether data probes (COUNTs over user tables) are allowed.</summary>
    public bool CheckExistingData { get; }

    private const string DataOff = "existing-data checks are switched off";

    /// <summary>A function call as SQL Server stores it in a CHECK definition: <c>[schema].[name](</c>.</summary>
    [GeneratedRegex(@"\]\s*\.\s*\[(?:[^\]]|\]\])*\]\s*\(")]
    private static partial Regex CallsFunctionRx();

    // ------------------------------------------------------------------ data probes (§6B, §6E)

    /// <summary>
    /// generate.py has_duplicates' SQL. With <paramref name="columns"/> empty the whole key is
    /// constant on existing rows, so the answer is "does the table have more than one row" (1/0);
    /// otherwise the number of GROUP BY … HAVING COUNT(*) &gt; 1 groups. The caller picks the
    /// columns (only those already in the target, minus the ones 01 adds).
    /// </summary>
    public Task<ProbeCount> DuplicateGroupsAsync(ObjectName table, IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (columns.Count == 0)
            return CountAsync($"duplicates on {table}",
                $"SELECT CASE WHEN COUNT(*) > 1 THEN 1 ELSE 0 END FROM {table.Bracketed}", ct, readUncommitted: true);
        var sel = ColumnList(columns);
        return CountAsync($"duplicates on {table} ({string.Join(", ", columns)})",
            $"SELECT COUNT(*) FROM (SELECT {sel} FROM {table.Bracketed} GROUP BY {sel} HAVING COUNT(*) > 1) x",
            ct, readUncommitted: true);
    }

    /// <summary>
    /// check_flow.py's FK test: rows of <paramref name="table"/> whose FK columns are all non-NULL and
    /// have no match in <paramref name="referencedTable"/>. Pass <paramref name="referencedTable"/> null
    /// when the referenced table is not in the target yet: then every non-NULL row counts.
    /// </summary>
    public Task<ProbeCount> ForeignKeyOrphansAsync(
        ObjectName table, IReadOnlyList<string> columns, ObjectName? referencedTable, IReadOnlyList<string> referencedColumns,
        CancellationToken ct = default)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (columns.Count == 0) return Task.FromResult(new ProbeCount(null, "the foreign key has no columns"));
        var nn = string.Join(" AND ", columns.Select(c => $"p.{Ddl.Bracket(c)} IS NOT NULL"));
        string sql;
        if (referencedTable is { } rt)
        {
            if (referencedColumns.Count != columns.Count)
                return Task.FromResult(new ProbeCount(null, "column lists differ in length"));
            var on = string.Join(" AND ", columns.Zip(referencedColumns, (a, b) => $"r.{Ddl.Bracket(b)} = p.{Ddl.Bracket(a)}"));
            sql = $"SELECT COUNT(*) FROM {table.Bracketed} p WHERE {nn} AND NOT EXISTS (SELECT 1 FROM {rt.Bracketed} r WHERE {on})";
        }
        else
        {
            sql = $"SELECT COUNT(*) FROM {table.Bracketed} p WHERE {nn}";
        }
        return CountAsync($"existing rows against FK on {table}", sql, ct, readUncommitted: true, expectedErrors: NotThereYet);
    }

    /// <summary>
    /// check_flow.py's CHECK test: <c>SELECT COUNT(*) FROM [s].[t] WHERE NOT {definition}</c> (UNKNOWN is
    /// not counted, matching CHECK semantics). A failure (e.g. the check names a column 01 has not
    /// added yet) returns Count null and the server's message as the reason ("not testable yet").
    /// A rule that calls a function is not run at all: the probe would execute the TARGET's version of that
    /// function once per row, and a function may call extended procedures or CLR code - more than a read.
    /// SQL Server stores a function call in a CHECK as <c>[schema].[name](</c>, which is what is matched.
    /// </summary>
    public Task<ProbeCount> CheckViolationsAsync(ObjectName table, string definition, CancellationToken ct = default)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (CallsFunctionRx().IsMatch(definition))
            return Task.FromResult(new ProbeCount(null, "not checked - the rule calls a function, which this read-only check does not run"));
        return CountAsync($"existing rows against CHECK on {table}",
            $"SELECT COUNT(*) FROM {table.Bracketed} WHERE NOT {definition.Trim()}", ct, readUncommitted: true,
            expectedErrors: NotThereYet);
    }

    /// <summary>11_destructive: the rows a DROP TABLE would delete, <c>SELECT COUNT_BIG(*)</c> (READ UNCOMMITTED).</summary>
    public Task<ProbeCount> RowCountProbeAsync(ObjectName table, CancellationToken ct = default)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        return CountAsync($"rows in {table}", $"SELECT COUNT_BIG(*) FROM {table.Bracketed}", ct, readUncommitted: true);
    }

    /// <summary>
    /// 11_destructive's data-fit test for <c>ALTER COLUMN</c>: rows that hold NULL when <paramref name="notNull"/>, and,
    /// when <paramref name="newType"/> is given, rows whose value does not come back byte for byte after a round trip
    /// <c>TRY_CONVERT(oldType, TRY_CONVERT(newType, col))</c> - a failed conversion, an overflow, a cut string, a rounded
    /// number or a character the new type cannot hold. Types are bare (no COLLATE). A conversion SQL Server does not
    /// allow at all fails the probe, which the caller treats as "not checked".
    /// </summary>
    public Task<ProbeCount> ColumnFitViolationsAsync(ObjectName table, string column, string oldType, string? newType, bool notNull,
        CancellationToken ct = default)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (ColumnFitPredicate(column, oldType, newType, notNull) is not { } misfit) return Task.FromResult(new ProbeCount(0));
        return CountAsync($"rows of {table}.{column} against its new type",
            $"SELECT COUNT_BIG(*) FROM {table.Bracketed} WHERE {misfit}", ct, readUncommitted: true);
    }

    /// <summary>
    /// The WHERE condition of <see cref="ColumnFitViolationsAsync"/>: true for a row that would not survive the change.
    /// Null when there is nothing to check. 11_destructive also writes it into the file, to run again when the file runs.
    /// </summary>
    public static string? ColumnFitPredicate(string column, string oldType, string? newType, bool notNull)
    {
        var c = Ddl.Bracket(column);
        var parts = new List<string>();
        if (notNull) parts.Add($"{c} IS NULL");
        if (newType is not null)
        {
            var back = $"TRY_CONVERT({oldType}, TRY_CONVERT({newType}, {c}))";
            parts.Add($"({c} IS NOT NULL AND ({back} IS NULL OR CONVERT(varbinary(max), {back}) <> CONVERT(varbinary(max), {c})))");
        }
        return parts.Count == 0 ? null : string.Join(" OR ", parts);
    }

    // ------------------------------------------------------------------ catalog probes (§6C, §6D)

    /// <summary>Rows per table from sys.partitions (heap or clustered index). Metadata, not a scan, so it runs even with data checks off.</summary>
    public Task<IReadOnlyDictionary<ObjectName, long>?> RowCountsAsync(CancellationToken ct = default) =>
        Cached("rowCounts", async () =>
        {
            var rows = await ListAsync("table row counts", """
                SELECT s.name sch, t.name tbl, SUM(p.rows) n FROM sys.tables t
                    JOIN sys.schemas s ON s.schema_id = t.schema_id
                    JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
                    GROUP BY s.name, t.name
                """, r => (new ObjectName(r.GetString(0), r.GetString(1)), r.IsDBNull(2) ? 0L : Convert.ToInt64(r.GetValue(2))), ct);
            if (rows is null) return (IReadOnlyDictionary<ObjectName, long>?)null;
            var d = new Dictionary<ObjectName, long>(ObjectName.IgnoreCase);
            foreach (var (k, n) in rows) d[k] = n;
            return d;
        });

    /// <summary>Rows in one table; 0 when unknown (table missing or the probe failed).</summary>
    public async Task<long> RowCountAsync(ObjectName table, CancellationToken ct = default)
    {
        var all = await RowCountsAsync(ct);
        return all is not null && all.TryGetValue(table, out var n) ? n : 0;
    }

    /// <summary>
    /// Query Store executions per object over everything Query Store holds, plus its window.
    /// Never fails: Query Store off, empty, or unreadable (VIEW DATABASE STATE) gives
    /// <see cref="QueryStoreUsage.Available"/> false or empty totals, and a note.
    /// </summary>
    public Task<QueryStoreUsage> QueryStoreAsync(CancellationToken ct = default) =>
        Cached("queryStore", async () =>
        {
            var state = (await ListAsync("Query Store state",
                "SELECT actual_state_desc FROM sys.database_query_store_options", r => Str(r, 0), ct))?.FirstOrDefault();
            var rows = await ListAsync("Query Store usage", """
                SELECT OBJECT_SCHEMA_NAME(q.object_id) sch, OBJECT_NAME(q.object_id) obj, SUM(rs.count_executions) n
                    FROM sys.query_store_query q
                    JOIN sys.query_store_plan p ON p.query_id = q.query_id
                    JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
                    WHERE q.object_id > 0
                    GROUP BY q.object_id
                """, r => (Sch: Str(r, 0), Obj: Str(r, 1), N: r.IsDBNull(2) ? 0L : Convert.ToInt64(r.GetValue(2))), ct);
            if (rows is null) return new QueryStoreUsage { Available = false, State = state };

            var window = (await ListAsync("Query Store window", """
                SELECT MIN(first_execution_time), MAX(last_execution_time) FROM sys.query_store_runtime_stats
                """, r => (F: r.IsDBNull(0) ? (DateTimeOffset?)null : r.GetDateTimeOffset(0),
                          L: r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetDateTimeOffset(1)), ct))?.FirstOrDefault();

            var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var byObj = new Dictionary<ObjectName, long>(ObjectName.IgnoreCase);
            foreach (var (sch, obj, n) in rows)
            {
                if (obj.Length == 0) continue;                      // dropped since: OBJECT_NAME is NULL
                byName[obj] = byName.GetValueOrDefault(obj) + n;
                var k = new ObjectName(sch, obj);
                byObj[k] = byObj.GetValueOrDefault(k) + n;
            }
            return new QueryStoreUsage
            {
                Available = true, State = state, First = window?.F, Last = window?.L,
                RunsByName = byName, RunsByObject = byObj
            };
        });

    /// <summary>OBJECT_NAMEs with an object-level permission (sys.database_permissions class 1). DROP would lose the grant.</summary>
    public Task<IReadOnlyList<string>?> ObjectPermissionNamesAsync(CancellationToken ct = default) =>
        Cached("perms", () => ListAsync("object permissions",
            "SELECT OBJECT_NAME(major_id) o FROM sys.database_permissions WHERE class = 1 AND major_id > 0",
            r => Str(r, 0), ct));

    /// <summary>Table types (schema, name) that carry a permission (class 6). DROP TYPE in 02 would lose it.</summary>
    public Task<IReadOnlyList<ObjectName>?> TypePermissionsAsync(CancellationToken ct = default) =>
        Cached("typePerms", () => ListAsync("table type permissions", """
            SELECT DISTINCT SCHEMA_NAME(t.schema_id) sch, t.name FROM sys.database_permissions p
            JOIN sys.types t ON t.user_type_id = p.major_id WHERE p.class = 6
            """, r => new ObjectName(Str(r, 0), Str(r, 1)), ct));

    /// <summary>
    /// Column dependents the catalog rows do not carry, for 04's ALTER COLUMN rule (<see cref="ColumnCover"/>):
    /// user-created statistics (CREATE STATISTICS; auto-created ones are dropped by ALTER COLUMN itself, index
    /// statistics come with the index rows) and views or functions WITH SCHEMABINDING that read the column.
    /// </summary>
    public Task<IReadOnlyList<CatalogColumnDependent>?> ColumnDependentsAsync(CancellationToken ct = default) =>
        Cached("columnDeps", () => ListAsync("column statistics and schema-bound references", """
            SELECT OBJECT_SCHEMA_NAME(s.object_id) sch, OBJECT_NAME(s.object_id) tbl, c.name col, 0 k, N'statistics ' + s.name what
                FROM sys.stats s
                JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
                JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                JOIN sys.tables t ON t.object_id = s.object_id
                WHERE s.user_created = 1
            UNION ALL
            SELECT OBJECT_SCHEMA_NAME(d.referenced_id), OBJECT_NAME(d.referenced_id), c.name, 1,
                   N'schema-bound ' + OBJECT_SCHEMA_NAME(d.referencing_id) + N'.' + OBJECT_NAME(d.referencing_id)
                FROM sys.sql_expression_dependencies d
                JOIN sys.objects o ON o.object_id = d.referencing_id
                JOIN sys.columns c ON c.object_id = d.referenced_id AND c.column_id = d.referenced_minor_id
                WHERE d.is_schema_bound_reference = 1 AND d.referencing_class = 1 AND d.referenced_minor_id > 0
                  AND o.type IN ('V', 'FN', 'IF', 'TF')
            """, r => new CatalogColumnDependent(new ObjectName(Str(r, 0), Str(r, 1)), Str(r, 2),
                r.GetInt32(3) == 0 ? ColumnDependentKind.Statistics : ColumnDependentKind.SchemaBound, Str(r, 4)), ct));

    /// <summary>OBJECT_NAMEs with an extended property (class 1). DROP would lose it.</summary>
    public Task<IReadOnlyList<string>?> ExtendedPropertyNamesAsync(CancellationToken ct = default) =>
        Cached("xprops", () => ListAsync("extended properties",
            "SELECT OBJECT_NAME(major_id) o FROM sys.extended_properties WHERE class = 1",
            r => Str(r, 0), ct));

    /// <summary>Objects referenced schema-bound, or by a check/default/computed column (C, D, U). DROP of those is refused.</summary>
    public Task<IReadOnlyList<ExpressionDependent>?> ExpressionDependentsAsync(CancellationToken ct = default) =>
        Cached("exprDeps", () => ListAsync("schema-bound dependencies", """
            SELECT OBJECT_NAME(d.referenced_id) o, OBJECT_NAME(d.referencing_id) by_obj, o.type_desc t
                FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id
                WHERE d.referenced_id IS NOT NULL AND (d.is_schema_bound_reference = 1 OR o.type IN ('C', 'D', 'U'))
            """, r => new ExpressionDependent(Str(r, 0), Str(r, 1), Str(r, 2)), ct));

    /// <summary>OBJECT_NAMEs with a forced Query Store plan. Empty (not null) when Query Store holds nothing.</summary>
    public Task<IReadOnlyList<string>?> ForcedPlanNamesAsync(CancellationToken ct = default) =>
        Cached("forced", () => ListAsync("forced Query Store plans", """
            SELECT DISTINCT OBJECT_NAME(q.object_id) o FROM sys.query_store_plan p
                JOIN sys.query_store_query q ON q.query_id = p.query_id WHERE p.is_forced_plan = 1
            """, r => Str(r, 0), ct));

    /// <summary>09: tables (referenced name) that a schema-bound view or function references - sp_rename is refused for them.</summary>
    public Task<IReadOnlyList<ExpressionDependent>?> SchemaBoundReferencersAsync(CancellationToken ct = default) =>
        Cached("schemaBound", () => ListAsync("schema-bound referencers", """
            SELECT OBJECT_NAME(d.referenced_id) o, OBJECT_NAME(d.referencing_id) by_obj, o.type_desc t
                FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id
                WHERE d.is_schema_bound_reference = 1 AND d.referenced_id IS NOT NULL
                  AND o.type IN ('V', 'FN', 'IF', 'TF')
            """, r => new ExpressionDependent(Str(r, 0), Str(r, 1), Str(r, 2)), ct));

    /// <summary>09: table names that are CDC-tracked, replicated or temporal - renames are skipped for them.</summary>
    public Task<IReadOnlyList<string>?> SpecialTableNamesAsync(CancellationToken ct = default) =>
        Cached("special", () => ListAsync("CDC / replicated / temporal tables",
            "SELECT name FROM sys.tables WHERE is_tracked_by_cdc = 1 OR is_replicated = 1 OR temporal_type <> 0",
            r => Str(r, 0), ct));

    /// <summary>View names that have an index (an indexed view loses its indexes on DROP).</summary>
    public Task<IReadOnlyList<string>?> IndexedViewNamesAsync(CancellationToken ct = default) =>
        Cached("indexedViews", () => ListAsync("indexed views", """
            SELECT DISTINCT v.name FROM sys.views v JOIN sys.indexes i ON i.object_id = v.object_id WHERE i.index_id > 0
            """, r => Str(r, 0), ct));

    /// <summary>Module names that are signed (sys.crypt_properties) - DROP loses the signature.</summary>
    public Task<IReadOnlyList<string>?> SignedModuleNamesAsync(CancellationToken ct = default) =>
        Cached("signed", () => ListAsync("signed modules",
            "SELECT DISTINCT OBJECT_NAME(major_id) FROM sys.crypt_properties WHERE class = 1",
            r => Str(r, 0), ct));

    /// <summary>
    /// The 08 drop-safety guards of generate_formatting.py, keyed by lower-cased object name (schema
    /// ignored - the conservative direction), each with its reasons verbatim. With
    /// <paramref name="includeExtraGuards"/> also: indexed views, signed modules, ordered triggers.
    /// A guard whose probe failed contributes nothing and leaves a "not checked" note; callers that
    /// must be strict can test <see cref="CatalogProbes.NotChecked"/>.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> FormattingGuardsAsync(bool includeExtraGuards = true, CancellationToken ct = default)
    {
        var g = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? name, string reason)
        {
            var k = (name ?? "").ToLowerInvariant();
            if (!g.TryGetValue(k, out var l)) g[k] = l = [];
            l.Add(reason);
        }
        foreach (var o in await ObjectPermissionNamesAsync(ct) ?? []) Add(o, "has an object-level permission");
        foreach (var o in await ExtendedPropertyNamesAsync(ct) ?? []) Add(o, "has an extended property");
        foreach (var d in await ExpressionDependentsAsync(ct) ?? [])
            Add(d.Referenced, $"used by {d.ReferencedBy} ({d.ReferencingTypeDesc}) - DROP would be refused");
        foreach (var o in await ForcedPlanNamesAsync(ct) ?? []) Add(o, "has a forced Query Store plan");
        foreach (var t in await DisabledTriggersAsync(ct) ?? []) Add(t.Name, "trigger is disabled - CREATE would enable it");
        if (includeExtraGuards)
        {
            foreach (var v in await IndexedViewNamesAsync(ct) ?? []) Add(v, "is an indexed view - DROP would drop its indexes");
            foreach (var s in await SignedModuleNamesAsync(ct) ?? []) Add(s, "is signed - DROP would lose the signature");
            foreach (var t in await OrderedTriggerNamesAsync(ct) ?? []) Add(t, "has a first/last trigger order - CREATE would reset it");
        }
        return g;
    }
}

/// <summary>
/// Read-only probes on the SOURCE. Replaces the Python's hand-kept BROKEN_IN_DEV list with
/// an automatic check (DESIGN.md §7, refined by <see cref="IsCreateFailure"/>: a bare 2020 is not a CREATE failure).
/// </summary>
public sealed class SourceProbes : CatalogProbes
{
    /// <summary>The errors <c>sys.dm_sql_referenced_entities</c> raises for unbound references.</summary>
    public static IReadOnlySet<int> BindingErrors { get; } = new HashSet<int> { 207, 208, 2020 };

    public SourceProbes(string label, string connectionString, int timeoutSeconds)
        : base(label, connectionString, timeoutSeconds) { }

    /// <summary>Convenience: endpoint + options.</summary>
    public SourceProbes(ScriptEndpoint source, ScriptOptions options)
        : this(source.Label, source.ConnectionString, options.ProbeTimeoutSeconds) { }

    /// <summary>
    /// Whether a binding error means the module's CREATE would fail:
    /// <list type="bullet">
    /// <item>207 (invalid column name): always - a column of an existing table is bound at CREATE for every module kind.</item>
    /// <item>208 (invalid object name) or 2020 (dependencies incomplete): only for views and inline functions
    /// (<c>V</c>, <c>IF</c>), which bind every reference at CREATE. Procedures, scalar/multi-statement functions
    /// and triggers may name objects that do not exist (deferred name resolution); the DMV raises 2020 for them
    /// but their CREATE succeeds.</item>
    /// </list>
    /// Measured on Platform_Core: 47 modules raise a bare 2020 (e.g. dbo.get_shift reads a table "shifts"
    /// that does not exist) and the Python created them; only kn_ZN_COM_FMEA_Functional_Items_Crud (207) is
    /// really broken - exactly the Python's BROKEN_IN_DEV.
    /// Also always a failure: the compile errors in <see cref="CompileErrors"/> (213 insert column count,
    /// 4104 unbound multi-part identifier, 102/156 syntax) - SQL Server raises them for a statement whose
    /// tables all exist, which is exactly when CREATE compiles it too.
    /// </summary>
    public static bool IsCreateFailure(int errorNumber, string objectType) =>
        errorNumber == 207 || CompileErrors.Contains(errorNumber) ||
        (errorNumber is 208 or 2020 && (objectType ?? "").Trim().ToUpperInvariant() is "V" or "IF");

    /// <summary>Errors besides <see cref="BindingErrors"/> that mean the CREATE fails for every module kind (see <see cref="IsCreateFailure"/>).</summary>
    public static IReadOnlySet<int> CompileErrors { get; } = new HashSet<int> { 102, 156, 213, 4104 };

    /// <summary>
    /// Judges the errors <c>sys.dm_sql_referenced_entities</c> raised: a CREATE failure gives Compiles false, a
    /// deferred-resolution binding error gives true with a note, and null means none of them is an answer (not checked).
    /// </summary>
    internal static CompileCheck? Judge(IReadOnlyList<(int Number, string Message)> errors, string objectType)
    {
        var fatal = errors.FirstOrDefault(e => IsCreateFailure(e.Number, objectType));
        if (fatal.Message is not null) return new CompileCheck(false, fatal.Number, fatal.Message);
        var deferred = errors.FirstOrDefault(e => BindingErrors.Contains(e.Number));
        if (deferred.Message is null) return null;
        return new CompileCheck(true, deferred.Number, null,
            "references an object that does not exist - its CREATE succeeds (deferred name resolution), but it fails when run");
    }

    /// <summary>
    /// Asks SQL Server to bind the module's references:
    /// <c>SELECT referenced_entity_name FROM sys.dm_sql_referenced_entities(N'[s].[n]', 'OBJECT')</c>.
    /// It reads metadata only. The errors are judged by <see cref="IsCreateFailure"/>: a CREATE failure gives
    /// <see cref="CompileCheck.Compiles"/> false (hold the module back); a deferred-resolution error gives true
    /// with a <see cref="CompileCheck.Note"/> for review; any other failure (timeout, permission, another error
    /// number) is "not checked" (null).
    /// </summary>
    /// <param name="objectType">The sys.objects type (<see cref="ModuleRow.OType"/>): P, FN, IF, TF, V or TR.</param>
    public async Task<CompileCheck> CompileCheckAsync(ObjectName module, string objectType, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT referenced_entity_name FROM sys.dm_sql_referenced_entities(@name, 'OBJECT')", conn)
            { CommandTimeout = TimeoutSeconds };
            cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 600) { Value = module.Bracketed });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) { }
            // errors raised after the first row arrive when the reader is drained/closed
            await r.CloseAsync();
            return new CompileCheck(true);
        }
        catch (SqlException ex) when (Judge(ex.Errors.Cast<SqlError>().Select(e => (e.Number, e.Message)).ToList(), objectType) is { } judged)
        {
            return judged;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var reason = SqlProbe.Reason(ex);
            NoteNotChecked($"compile check of {module}", reason);
            return new CompileCheck(null, null, reason);
        }
    }

    /// <inheritdoc cref="CompileCheckAsync(ObjectName, string, CancellationToken)"/>
    public Task<CompileCheck> CompileCheckAsync(ModuleRow module, CancellationToken ct = default) =>
        CompileCheckAsync(module.Name, module.OType, ct);
}

/// <summary>Connection-string tuning and error text shared by the reader and the probes.</summary>
internal static class SqlProbe
{
    /// <summary>Names the connection "SchemaScope" so a DBA can see whose sessions these are.</summary>
    public static string Tune(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrEmpty(b.ApplicationName) ||
                b.ApplicationName.EndsWith("SqlClient Data Provider", StringComparison.OrdinalIgnoreCase))
                b.ApplicationName = "SchemaScope";
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    /// <summary>A short, credential-free reason for a note. SqlException messages never carry the connection string.</summary>
    public static string Reason(Exception ex)
    {
        var msg = ex switch
        {
            SqlException { Number: -2 } => "timed out",
            SqlException s => $"Msg {s.Number}: {s.Message}",
            OperationCanceledException => "timed out",
            _ => ex.Message
        };
        msg = msg.ReplaceLineEndings(" ");
        return msg.Length > 200 ? msg[..200] + "..." : msg;
    }
}
