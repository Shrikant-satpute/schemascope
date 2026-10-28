using System.Data.Common;
using System.Text.RegularExpressions;
using Npgsql;
using SchemaScope.Core.Engines;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;
using SqlBatch = SchemaScope.Scripting.SqlBatch;

namespace SchemaScope.Api.Services;

/// <summary>
/// PostgreSQL (plan §5.3): the same promise as SQL Server - each file in its own transaction, all or nothing. A file
/// gets its own connection (through <see cref="PgConnectionService.OpenAsync"/>, which never signs in with Windows
/// credentials), the database it opened is checked exactly, then BEGIN; SET LOCAL lock_timeout,
/// idle_in_transaction_session_timeout and search_path; its statements one at a time, split by the
/// <see cref="PgStatementSplitter"/> the validator used; the write-mode check; COMMIT. Any error, Stop now or write
/// mode turned off rolls the file back; a running statement is cancelled through Npgsql (a PostgreSQL cancel request).
/// </summary>
public sealed partial class PgApplyExecutor : IApplyExecutor
{
    public static PgApplyExecutor Instance { get; } = new();

    /// <summary>How long a statement waits for a lock before it fails (and its file rolls back), as RUN.ps1's PGOPTIONS.</summary>
    public const string LockTimeout = "5s";

    /// <summary>If SchemaScope stops talking half-way through a file, the server ends the session and rolls it back.</summary>
    public const string IdleTimeout = "1min";

    /// <summary>
    /// The login's own search_path, as psql (and RUN.ps1) runs the files with, between pg_catalog first (so nothing can
    /// stand in for a built-in name the scripts use, as the reader deparsed them under pg_catalog alone) and pg_temp last
    /// (so a temporary object never can either). Every user object the scripts name is schema-qualified; what the
    /// login's path keeps is how a user function's body finds the objects it names without a schema when a statement here
    /// runs it (a materialized view's query, a VALIDATE, a column's default, an expression index), as the application's
    /// own sessions find them.
    /// </summary>
    public const string SearchPath =
        "SELECT set_config('search_path', 'pg_catalog, ' || CASE WHEN current_setting('search_path') = '' THEN '' " +
        "ELSE current_setting('search_path') || ', ' END || 'pg_temp', true);";

    /// <summary>The SET LOCALs every file's transaction starts with.</summary>
    public const string TransactionSetup =
        $"SET LOCAL lock_timeout = '{LockTimeout}'; SET LOCAL idle_in_transaction_session_timeout = '{IdleTimeout}'; " + SearchPath;

    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;
    public TransactionSemantics Transactions => TransactionSemantics.PerFileTransactional;
    public IStatementSplitter Splitter => PgStatementSplitter.Instance;

    /// <summary>
    /// The lock key is what the server reports (<see cref="TargetFacts.Key"/>: engine, cluster, port, database). Without
    /// an answer an apply is refused before it gets here; the fallback only keeps the contract.
    /// </summary>
    public string TargetKey(TargetFacts? facts, string server, string database) =>
        facts?.Key ?? $"{(int)DatabaseEngine.PostgreSql}|{server.Trim()}|{database.Trim()}";

    /// <summary>
    /// Who the target is: <c>pg_control_system()</c>'s system_identifier (one per cluster) with
    /// <c>inet_server_port()</c> and <c>current_database()</c>. A deploy login that may not read the system identifier
    /// (it needs superuser or pg_monitor-like rights) falls back to <c>inet_server_addr()</c> and the port. Null when
    /// the server cannot be reached or neither answer is there: the apply is then refused.
    /// </summary>
    public async Task<TargetFacts?> InspectAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var open = await PgConnectionService.OpenAsync(connectionString, ct);
            return await IdentifyAsync(open.Connection, ct);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or TimeoutException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary><see cref="InspectAsync"/> on a connection already open; null when the server is not known.</summary>
    private static async Task<TargetFacts?> IdentifyAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        string database;
        int? port;
        string? address;
        await using (var who = new NpgsqlCommand("SELECT current_database(), inet_server_port(), host(inet_server_addr())", conn))
        await using (var r = await who.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            database = r.GetString(0);
            port = r.IsDBNull(1) ? null : r.GetInt32(1);
            address = r.IsDBNull(2) ? null : r.GetString(2);
        }

        string? system = null;
        try
        {
            await using var id = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", conn);
            system = await id.ExecuteScalarAsync(ct) as string;
        }
        catch (PostgresException)
        {
            // 42501 for a login without the right: the address and port below are the identity then
        }

        return KeyOf(system, address, port, database) is { } key
            ? new TargetFacts(key, new HashSet<string>()) { Hosts = address is null ? [] : [address] }
            : null;
    }

    /// <summary>
    /// The same cluster, whatever database each opened: the same system identifier when both report one, else the same
    /// reported address and port. A protected connection protects its whole cluster.
    /// </summary>
    public static bool SameCluster(TargetFacts a, TargetFacts b)
    {
        static (string Kind, string Who, string Port) Of(TargetFacts f)
        {
            var parts = f.Key.Split('|', 4);
            var colon = parts.Length > 1 ? parts[1].IndexOf(':') : -1;
            return parts.Length < 3 || colon < 0 ? ("", "", "") : (parts[1][..colon], parts[1][(colon + 1)..], parts[2]);
        }
        var (x, y) = (Of(a), Of(b));
        if (x.Kind == "system" && y.Kind == "system") return x.Who == y.Who;
        return x.Port == y.Port && (a.Hosts ?? []).Any(h => (b.Hosts ?? []).Contains(h, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>"1|system:&lt;id&gt;|port|db", else "1|address:&lt;ip&gt;|port|db"; null when the server is not known.</summary>
    public static string? KeyOf(string? systemIdentifier, string? address, int? port, string database)
    {
        var engine = (int)DatabaseEngine.PostgreSql;
        if (!string.IsNullOrWhiteSpace(systemIdentifier)) return $"{engine}|system:{systemIdentifier.Trim()}|{port}|{database}";
        if (!string.IsNullOrWhiteSpace(address) && port is not null) return $"{engine}|address:{address.Trim()}|{port}|{database}";
        return null;
    }

    /// <summary>
    /// Null, or why none of these files may run: anything that cannot run inside a transaction or must never run from
    /// here (the validator's list - CONCURRENTLY, VACUUM, ALTER SYSTEM, CREATE / DROP DATABASE or TABLESPACE), a
    /// statement that would end the file's transaction itself, and an <c>ALTER TYPE ... ADD VALUE</c> whose new value
    /// the same file goes on to use (PostgreSQL refuses that until the value is committed). The generator writes none
    /// of these; this is the server's own safety net.
    /// </summary>
    public string? Preflight(IReadOnlyList<VerifiedFile> files, TargetFacts facts)
    {
        var hits = new List<string>();
        foreach (var v in files)
        {
            var reasons = new List<string>();
            var added = new List<(string Literal, int Line)>();
            foreach (var s in v.Batches)
            {
                foreach (var refused in PgScriptValidator.Refused(s.Text)) reasons.Add($"line {s.StartLine}: {refused}");
                var code = PgStatementSplitter.CodeOnly(s.Text);
                if (TransactionControlRx().IsMatch(code))
                    reasons.Add($"line {s.StartLine}: a statement that ends or splits the file's transaction");
                foreach (var (literal, line) in added)
                    if (s.Text.Contains(literal, StringComparison.Ordinal))
                        reasons.Add($"line {s.StartLine}: uses {literal}, which ALTER TYPE ... ADD VALUE on line {line} adds in the same transaction");
                if (AddValueRx().Match(code) is { Success: true } m && Literal(s.Text, m.Index + m.Length) is { } value)
                    added.Add((value, s.StartLine));
            }
            if (reasons.Count > 0) hits.Add($"{v.File.Folder}\\{v.File.RelativePath} ({reasons[0]})");
        }
        if (hits.Count == 0) return null;
        return $"{hits.Count} file(s) hold a statement Apply never runs inside a file's transaction: {string.Join(", ", hits.Take(5))}" +
               $"{(hits.Count > 5 ? ", ..." : "")}. Nothing was applied. Untick that section, or run those files by hand.";
    }

    /// <summary>The string literal ('...', quotes doubled inside) starting at the first non-space at or after <paramref name="at"/>.</summary>
    private static string? Literal(string text, int at)
    {
        var i = at;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '\'') return null;
        var j = i + 1;
        while (j < text.Length)
        {
            if (text[j] == '\'')
            {
                if (j + 1 < text.Length && text[j + 1] == '\'') { j += 2; continue; }
                return text[i..(j + 1)];
            }
            j++;
        }
        return null;
    }

    [GeneratedRegex(@"^\s*(?:BEGIN|COMMIT|END|ROLLBACK|ABORT|START\s+TRANSACTION|SAVEPOINT|RELEASE|PREPARE\s+TRANSACTION)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionControlRx();

    // no trailing \s*: the literal is blanked out in the code-only text, and Literal skips the spaces before it in the real one
    [GeneratedRegex(@"^\s*ALTER\s+TYPE\b[\s\S]*?\bADD\s+VALUE(?:\s+IF\s+NOT\s+EXISTS)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddValueRx();

    /// <summary>
    /// One file on its own connection: check the database (and, with <paramref name="expectedKey"/>, that the server
    /// is the one the gates checked - a host list may fail over to another host between files), BEGIN, <see cref="TransactionSetup"/>, every statement,
    /// <paramref name="beforeCommit"/>, COMMIT. Null when it committed, else the error; the transaction is rolled back.
    /// No command timeout, as with psql; <paramref name="abort"/> (Cancel now, or write mode turned off) cancels the
    /// running statement and the file is rolled back instead of committing.
    /// </summary>
    public async Task<string?> RunFileAsync(string connectionString, string database, VerifiedFile file, CancellationToken abort,
        Action beforeCommit, Action<int>? statementDone = null, string? expectedKey = null)
    {
        SqlBatch? current = null;
        int? index = null;
        var committing = false;
        try
        {
            await using var open = await PgConnectionService.OpenAsync(connectionString, abort);
            var conn = open.Connection;

            await using (var check = new NpgsqlCommand("SELECT current_database()", conn))
            {
                // PostgreSQL names are exact: "App" and "app" are two databases
                var opened = await check.ExecuteScalarAsync(abort) as string;
                if (!string.Equals(opened, database, StringComparison.Ordinal))
                    return $"The connection opened database {opened}, not {database}, so this file was not run.";
            }
            if (expectedKey is not null && (await IdentifyAsync(conn, abort))?.Key is var reached &&
                !string.Equals(reached, expectedKey, StringComparison.Ordinal))
                return "The connection reached another server than the one checked before the apply, so this file was not run.";
            await using (var name = new NpgsqlCommand("SET application_name = 'SchemaScope'", conn))
                await name.ExecuteNonQueryAsync(abort);

            await using var tx = await conn.BeginTransactionAsync(abort);
            try
            {
                await using (var setup = new NpgsqlCommand(TransactionSetup, conn, tx))
                    await setup.ExecuteNonQueryAsync(abort);
                for (var i = 0; i < file.Batches.Count; i++)
                {
                    current = file.Batches[i];
                    index = i;
                    await using var cmd = Raw(current.Text, conn, tx);
                    await cmd.ExecuteNonQueryAsync(abort);
                    statementDone?.Invoke(i);
                }
                current = null;
                index = null;
                beforeCommit();   // write mode turned off since the last watch tick cancels it here, not a second later
                abort.ThrowIfCancellationRequested();
                committing = true;
                await tx.CommitAsync(CancellationToken.None);
                return null;
            }
            catch
            {
                // an aborted transaction only takes ROLLBACK; a broken connection rolls back on the server when it closes
                try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception ex) when (ex is InvalidOperationException or DbException) { }
                throw;
            }
        }
        catch (Exception) when (abort.IsCancellationRequested)
        {
            return "Cancelled while it ran: this file was rolled back.";
        }
        catch (PostgresException ex)
        {
            // a deferred constraint fails at COMMIT, after every statement ran
            return committing ? $"ERROR {ex.SqlState} at COMMIT: {MessageOf(ex)}" : Describe(ex, current, index);
        }
        // any other driver's error ends the file the same way, so the run - and its audit entry - always finishes
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or TimeoutException)
        {
            return PgConnectionService.MessageOf(ex);
        }
    }

    /// <summary>
    /// A command that sends <paramref name="statement"/> exactly as the splitter cut it, with no command timeout.
    /// Without parameters Npgsql parses a command's text again to split it at semicolons - its own rules, which do not
    /// know a BEGIN ATOMIC body - and refuses or re-splits what psql would send whole. With a positional parameter it
    /// sends the text untouched (one extended-protocol statement), so the command carries one typed parameter that no
    /// statement here refers to. Two statements in the text would be refused by the server, never run.
    /// </summary>
    internal static NpgsqlCommand Raw(string statement, NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        var cmd = new NpgsqlCommand(statement, conn, tx) { CommandTimeout = 0 };
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer, Value = 0 });
        return cmd;
    }

    /// <inheritdoc cref="Describe"/>
    public string DescribeError(Exception ex, SqlBatch? statement) => Describe(ex, statement, null);

    /// <summary>
    /// "ERROR 42P07 at statement 2 (line 14): relation "t" already exists" - the line counted in the file from where the
    /// server says the error is. Only the server's message: DETAIL, WHERE and HINT can echo row values, so they are
    /// never read. Truncated.
    /// </summary>
    public static string Describe(Exception ex, SqlBatch? statement, int? index)
    {
        if (ex is not PostgresException pg) return PgConnectionService.MessageOf(ex);
        var message = MessageOf(pg);
        if (statement is null) return $"ERROR {pg.SqlState}: {message}";
        var line = statement.StartLine;
        if (pg.Position > 0)
            for (var k = 0; k < Math.Min(pg.Position - 1, statement.Text.Length); k++)
                if (statement.Text[k] == '\n') line++;
        return index is { } n
            ? $"ERROR {pg.SqlState} at statement {n + 1} (line {line}): {message}"
            : $"ERROR {pg.SqlState} (line {line}): {message}";
    }

    /// <summary>The server's message alone (never DETAIL, WHERE or HINT), truncated.</summary>
    private static string MessageOf(PostgresException pg) =>
        pg.MessageText.Length > 400 ? pg.MessageText[..400] + "..." : pg.MessageText;
}
