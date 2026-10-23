using System.Data;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaScope.PostgreSql;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>One check of the target, as the README lists it.</summary>
public sealed record PgProbeNote(string What, ProbeResultKind Kind, string Detail);

/// <summary>
/// Read-only probes on a PostgreSQL target: duplicate keys, NULLs under a new primary key, rows a new check or foreign
/// key would refuse, row estimates, and which roles exist. Each runs in its own READ ONLY transaction with a statement
/// and a lock timeout, so a probe can neither write nor wait long, and one that fails leaves the others unharmed. Data
/// probes (anything that reads rows) run only with "Check the data already in the target"; when one cannot run, what
/// it guarded is said to be not checked - never taken as fine. SQL Server's own checks that have no PostgreSQL
/// counterpart are reported as not applicable and hold nothing back.
/// </summary>
public partial class PgTargetProbes(string label, string connectionString, int timeoutSeconds, bool checkExistingData) : IAsyncDisposable
{
    private const string DataOff = "existing-data checks are switched off";

    private readonly List<PgProbeNote> notes = [];
    private PgOpenConnection? open;
    private string? connectFailure;

    /// <summary>SQL Server checks that do not exist here (reported, never a hold).</summary>
    public static IReadOnlyList<PgProbeNote> NotApplicable { get; } =
    [
        new("Memory-optimized (In-Memory OLTP) tables", ProbeResultKind.NotApplicable, "PostgreSQL has no memory-optimized tables."),
        new("Modules that use a changed table type", ProbeResultKind.NotApplicable, "PostgreSQL has no table types (02_tabletypes)."),
        new("Forced Query Store plans on replaced code", ProbeResultKind.NotApplicable, "PostgreSQL has no Query Store."),
        new("Renames by letter case", ProbeResultKind.NotApplicable, "PostgreSQL names are exact; nothing is renamed (09_formatting_tables)."),
    ];

    public bool CheckExistingData { get; } = checkExistingData;

    /// <summary>Every probe that did not run, worded for the warnings ("not checked: ...").</summary>
    public IReadOnlyList<string> NotChecked
    {
        get { lock (notes) return [.. notes.Where(n => n.Kind == ProbeResultKind.NotChecked).Select(n => $"{n.What}: not checked ({n.Detail})")]; }
    }

    /// <summary>Every probe result kept for the README, the not applicable ones first.</summary>
    public IReadOnlyList<PgProbeNote> Notes { get { lock (notes) return [.. NotApplicable, .. notes]; } }

    private void Note(string what, ProbeResultKind kind, string detail)
    {
        lock (notes) notes.Add(new PgProbeNote(what, kind, detail));
    }

    /// <summary>Records a check that could not be made for a reason of the caller's own.</summary>
    public void NoteNotChecked(string what, string reason) => Note(what, ProbeResultKind.NotChecked, reason);

    // ------------------------------------------------------------------ data probes

    /// <summary>Groups of rows sharing one value of <paramref name="columns"/> (NULLs never clash), optionally under a partial index's predicate.</summary>
    public Task<ProbeCount> DuplicateGroupsAsync(string table, IReadOnlyList<string> columns, string? predicate, CancellationToken ct)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (columns.Count == 0) return Task.FromResult(new ProbeCount(null, "the key has no plain columns to check"));
        if (predicate is { Length: > 0 } && NamesAnObject(predicate))
            return NotRun($"duplicates on {table} ({string.Join(", ", columns)})", "the index's WHERE names a function, operator or type, which is not run on the target");
        var cols = string.Join(", ", columns.Select(PgDdl.Q));
        var notNull = string.Join(" AND ", columns.Select(c => $"{PgDdl.Q(c)} IS NOT NULL"));
        var where = predicate is { Length: > 0 } p ? $"({p}) AND {notNull}" : notNull;
        return CountAsync($"duplicates on {table} ({string.Join(", ", columns)})",
            $"SELECT count(*) FROM (SELECT 1 FROM {table} WHERE {where} GROUP BY {cols} HAVING count(*) > 1) x", ct);
    }

    /// <summary>Rows with a NULL in any of <paramref name="columns"/> (a primary key makes them NOT NULL).</summary>
    public Task<ProbeCount> NullsAsync(string table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (columns.Count == 0) return Task.FromResult(new ProbeCount(0));
        return CountAsync($"NULLs in {table} ({string.Join(", ", columns)})",
            $"SELECT count(*) FROM {table} WHERE {string.Join(" OR ", columns.Select(c => $"{PgDdl.Q(c)} IS NULL"))}", ct);
    }

    /// <summary>
    /// Rows a foreign key would refuse (MATCH SIMPLE: every column set and no match). <paramref name="referenced"/> null:
    /// the referenced table is new and empty, so every row with all columns set counts.
    /// </summary>
    public Task<ProbeCount> OrphansAsync(string table, IReadOnlyList<string> columns, string? referenced, IReadOnlyList<string> referencedColumns,
        CancellationToken ct)
    {
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (columns.Count == 0 || columns.Count != referencedColumns.Count)
            return Task.FromResult(new ProbeCount(null, "the column lists do not line up"));
        var set = string.Join(" AND ", columns.Select(c => $"p.{PgDdl.Q(c)} IS NOT NULL"));
        var sql = referenced is null
            ? $"SELECT count(*) FROM {table} p WHERE {set}"
            : $"SELECT count(*) FROM {table} p WHERE {set} AND NOT EXISTS (SELECT 1 FROM {referenced} r WHERE " +
              string.Join(" AND ", columns.Zip(referencedColumns, (a, b) => $"r.{PgDdl.Q(b)} = p.{PgDdl.Q(a)}")) + ")";
        return CountAsync($"rows of {table} without a match for its new foreign key", sql, ct);
    }

    /// <summary>
    /// Rows a CHECK would refuse. Only an expression that names no user object is run on the target: a function, an
    /// operator or a domain's own check could do anything, and READ ONLY does not stop it reaching another server.
    /// </summary>
    public Task<ProbeCount> CheckViolationsAsync(string table, string check, string expression, CancellationToken ct)
    {
        var what = $"rows of {table} that check {check} refuses";
        if (!CheckExistingData) return Task.FromResult(new ProbeCount(null, DataOff));
        if (NamesAnObject(expression)) return NotRun(what, "the check names a function, operator or type, which is not run on the target");
        return CountAsync(what, $"SELECT count(*) FROM {table} WHERE NOT ({expression})", ct);
    }

    /// <summary>
    /// True when a deparsed expression names a schema-qualified object (under search_path pg_catalog every user function,
    /// operator and type is qualified; a built-in one is not): such an expression is never run by a probe.
    /// </summary>
    internal static bool NamesAnObject(string expression) => QualifiedRx().IsMatch(PgStatementSplitter.CodeOnly(expression));

    private Task<ProbeCount> NotRun(string what, string reason)
    {
        Note(what, ProbeResultKind.NotChecked, reason);
        return Task.FromResult(new ProbeCount(null, reason));
    }

    // ------------------------------------------------------------------ catalog probes (always run)

    /// <summary>The planner's row estimate of a table (pg_class.reltuples), or null.</summary>
    public async Task<long?> RowEstimateAsync(string schema, string table, CancellationToken ct)
    {
        var r = await ScalarAsync($"row estimate of {schema}.{table}",
            "SELECT GREATEST(c.reltuples, 0)::bigint FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = $1 AND c.relname = $2",
            [schema, table], data: false, ct);
        return r.Count;
    }

    /// <summary>Which of <paramref name="roles"/> exist on the target; null when that could not be asked.</summary>
    public async Task<HashSet<string>?> ExistingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return [];
        var conn = await ConnectAsync(ct);
        if (conn is null)
        {
            Note("roles on the target", ProbeResultKind.NotChecked, connectFailure ?? "no connection");
            return null;
        }
        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var setup = new NpgsqlCommand(PgSchemaReader.SessionSetup(timeoutSeconds), conn, tx)) await setup.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT rolname::text FROM pg_roles WHERE rolname = ANY($1)", conn, tx);
            cmd.Parameters.AddWithValue(roles.ToArray());
            var found = new HashSet<string>(StringComparer.Ordinal);
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) found.Add(r.GetString(0));
            await tx.RollbackAsync(ct);
            return found;
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            Note("roles on the target", ProbeResultKind.NotChecked, PgConnectionService.MessageOf(ex));
            return null;
        }
    }

    // ------------------------------------------------------------------ plumbing

    private Task<ProbeCount> CountAsync(string what, string sql, CancellationToken ct) => ScalarAsync(what, sql, [], data: true, ct);

    private async Task<ProbeCount> ScalarAsync(string what, string sql, object[] args, bool data, CancellationToken ct)
    {
        var conn = await ConnectAsync(ct);
        if (conn is null)
        {
            Note(what, ProbeResultKind.NotChecked, connectFailure ?? "no connection");
            return new ProbeCount(null, connectFailure);
        }
        try
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var setup = new NpgsqlCommand(PgSchemaReader.SessionSetup(timeoutSeconds), conn, tx)) await setup.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = timeoutSeconds + 5 };
            foreach (var a in args) cmd.Parameters.AddWithValue(a);
            var value = await cmd.ExecuteScalarAsync(ct);
            await tx.RollbackAsync(ct);
            var count = value is null or DBNull ? 0 : Convert.ToInt64(value);
            if (data) Note(what, ProbeResultKind.Ok, count == 0 ? "none" : count.ToString("N0"));
            return new ProbeCount(count);
        }
        catch (Exception ex) when (Failed(ex, ct))
        {
            var reason = PgConnectionService.MessageOf(ex);
            Note(what, ProbeResultKind.NotChecked, reason);
            return new ProbeCount(null, reason);
        }
    }

    /// <summary>A probe error to report, not a cancellation to pass on.</summary>
    private static bool Failed(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is NpgsqlException or TimeoutException or InvalidOperationException or InvalidCastException or OverflowException;

    private readonly SemaphoreSlim gate = new(1, 1);

    private async Task<NpgsqlConnection?> ConnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (open is not null) return open.Connection;
            if (connectFailure is not null) return null;
            try
            {
                open = await PgConnectionService.OpenAsync(connectionString, ct);
                return open.Connection;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is NpgsqlException or TimeoutException or ArgumentException or InvalidOperationException)
            {
                connectFailure = $"could not connect to {label}: {PgConnectionService.MessageOf(ex)}";
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (open is not null) await open.DisposeAsync();
        gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// On <see cref="PgStatementSplitter.CodeOnly"/> text: a dot that is not a decimal point (schema.name, or a quoted name,
    /// which CodeOnly blanks down to its dot), or OPERATOR(...).
    /// </summary>
    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)|\bOPERATOR\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedRx();
}
