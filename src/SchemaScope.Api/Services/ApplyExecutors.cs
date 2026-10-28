using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Normalization;
using SchemaScope.Scripting;
using SqlBatch = SchemaScope.Scripting.SqlBatch;

namespace SchemaScope.Api.Services;

/// <summary>
/// One engine's side of "Apply to target": who the target is, what may not run there, and running one checked file.
/// <see cref="ApplyService"/> keeps the order of the checks, the audit log and the stop switches for every engine.
/// </summary>
public interface IApplyExecutor
{
    DatabaseEngine Engine { get; }
    TransactionSemantics Transactions { get; }

    /// <summary>The same splitter the script validator uses, so what was validated is what runs.</summary>
    IStatementSplitter Splitter { get; }

    /// <summary>What the target reports about itself; null when it cannot be asked.</summary>
    Task<TargetFacts?> InspectAsync(string connectionString, CancellationToken ct = default);

    /// <summary>Null, or why none of these files may run on this target.</summary>
    string? Preflight(IReadOnlyList<VerifiedFile> files, TargetFacts facts);

    /// <summary>The one-apply-at-a-time key.</summary>
    string TargetKey(TargetFacts? facts, string server, string database);

    /// <summary>
    /// One file on its own connection, all or nothing where the engine allows it. Null when it committed, else the
    /// error; <paramref name="abort"/> cancels it (rolled back), <paramref name="beforeCommit"/> runs just before COMMIT.
    /// <paramref name="expectedKey"/>, when given, is the <see cref="TargetFacts.Key"/> the gates checked: a file whose
    /// connection reaches another server is not run.
    /// </summary>
    Task<string?> RunFileAsync(string connectionString, string database, VerifiedFile file, CancellationToken abort,
        Action beforeCommit, Action<int>? statementDone = null, string? expectedKey = null);

    /// <summary>A driver error, worded with where in the file it happened.</summary>
    string DescribeError(Exception ex, SqlBatch? statement);
}

/// <summary>
/// SQL Server: each file in its own transaction - BEGIN, its batches, COMMIT; on an error ROLLBACK - with the session
/// SET options sqlcmd gives run_all.ps1, so a file does here exactly what RUN.ps1 would do. These are ApplyService's
/// bodies as they were; ApplyService keeps static forwarders to them.
/// </summary>
public sealed partial class SqlServerApplyExecutor : IApplyExecutor
{
    public static SqlServerApplyExecutor Instance { get; } = new();

    /// <summary>
    /// The session options of <c>sqlcmd -S -E -d -b -x -f 65001</c> (measured: @@OPTIONS 5176 - ANSI_WARNINGS,
    /// ANSI_PADDING, ANSI_NULLS, ANSI_NULL_DFLT_ON and CONCAT_NULL_YIELDS_NULL on; QUOTED_IDENTIFIER and ARITHABORT
    /// off). SqlClient starts with QUOTED_IDENTIFIER on, so they are set before every file; each file's own header
    /// (SET ANSI_NULLS / QUOTED_IDENTIFIER) then gives every module the options it had in the source.
    /// </summary>
    public const string SqlcmdSession =
        "SET ANSI_WARNINGS ON; SET ANSI_PADDING ON; SET ANSI_NULLS ON; SET ANSI_NULL_DFLT_ON ON; " +
        "SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER OFF; SET ARITHABORT OFF; SET ARITHIGNORE OFF; " +
        "SET NUMERIC_ROUNDABORT OFF; SET XACT_ABORT OFF; SET IMPLICIT_TRANSACTIONS OFF; " +
        "SET CURSOR_CLOSE_ON_COMMIT OFF; SET NOCOUNT OFF;";

    /// <summary>@@OPTIONS after <see cref="SqlcmdSession"/>, as sqlcmd reports it.</summary>
    public const int SqlcmdOptions = 5176;

    public DatabaseEngine Engine => DatabaseEngine.SqlServer;
    public TransactionSemantics Transactions => TransactionSemantics.PerFileTransactional;
    public IStatementSplitter Splitter => SqlServerStatementSplitter.Instance;

    /// <summary>
    /// One apply per target database at a time, keyed on what the server reports (<see cref="TargetFacts.Key"/>) so two
    /// spellings of one server ('.', 'localhost', the machine name) share a lock; the connection's text when it could not be asked.
    /// </summary>
    public string TargetKey(TargetFacts? facts, string server, string database) => facts?.Key ?? TargetKey(server, database);

    public static string TargetKey(string server, string database) => $"{server.Trim()}|{database.Trim()}";

    public string? Preflight(IReadOnlyList<VerifiedFile> files, TargetFacts facts) => MemoryOptimizedProblem(files, facts.InMemory);

    /// <summary>
    /// Asks the target who it is and which of its objects are memory-optimized, on the session's own connection string.
    /// Null when it cannot be reached: the files then report that themselves.
    /// </summary>
    public async Task<TargetFacts?> InspectAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            string key;
            await using (var who = new SqlCommand("SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) + N'|' + DB_NAME()", conn))
                key = (string)(await who.ExecuteScalarAsync(ct))!;

            var inMemory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await using var cmd = new SqlCommand("""
                    SELECT SCHEMA_NAME(schema_id) + N'.' + name FROM sys.tables WHERE is_memory_optimized = 1
                    UNION ALL SELECT SCHEMA_NAME(schema_id) + N'.' + name FROM sys.table_types WHERE is_memory_optimized = 1
                    UNION ALL SELECT OBJECT_SCHEMA_NAME(object_id) + N'.' + OBJECT_NAME(object_id) FROM sys.sql_modules WHERE uses_native_compilation = 1
                    """, conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (!r.IsDBNull(0)) inMemory.Add(r.GetString(0));
            }
            catch (SqlException)
            {
                // before SQL Server 2014 these columns do not exist - and neither do memory-optimized objects
            }
            return new TargetFacts(key, inMemory);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or TimeoutException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// SQL Server refuses CREATE, ALTER and DROP on memory-optimized tables and natively compiled modules inside a user
    /// transaction (Msg 12331), and every file here runs in one. Null, or why nothing may run: the files that touch one.
    /// </summary>
    public static string? MemoryOptimizedProblem(IReadOnlyList<VerifiedFile> files, IReadOnlySet<string> inMemory)
    {
        // the DDL options themselves, comments ignored: is_memory_optimized / uses_native_compilation in a catalog query are not
        static bool Names(string text) => InMemoryDdl().IsMatch(TSqlText.StripComments(text));
        var hit = files.Where(v => inMemory.Contains(v.File.ObjectName) || v.Batches.Any(b => Names(b.Text)))
            .Select(v => $"{v.File.Folder}\\{v.File.RelativePath}").ToList();
        if (hit.Count == 0) return null;
        return $"{hit.Count} file(s) change a memory-optimized table or a natively compiled module, which SQL Server does not allow " +
               $"inside a transaction (Msg 12331) - and Apply runs every file in one: {string.Join(", ", hit.Take(5))}" +
               $"{(hit.Count > 5 ? ", ..." : "")}. Nothing was applied. Untick that section, or run those files with the folder's run_all.ps1.";
    }

    [GeneratedRegex(@"\bMEMORY_OPTIMIZED\s*=\s*ON\b|\bNATIVE_COMPILATION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InMemoryDdl();

    /// <summary>
    /// One file on its own connection (sqlcmd opens one per file too): check the database, set sqlcmd's options,
    /// then BEGIN TRAN, every batch, COMMIT. Null when it committed, else the error; the transaction is rolled back.
    /// No command timeout, as with sqlcmd; <paramref name="abort"/> (Cancel now, or write mode turned off) cancels the
    /// running batch, and the file is rolled back instead of committing. <paramref name="expectedKey"/> is not used:
    /// a SQL Server connection names one server (a failover partner is checked by the Protected rule).
    /// </summary>
    public async Task<string?> RunFileAsync(string connectionString, string database, VerifiedFile file, CancellationToken abort,
        Action beforeCommit, Action<int>? statementDone = null, string? expectedKey = null)
    {
        SqlBatch? current = null;
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(abort);

            await using (var check = new SqlCommand("SELECT DB_NAME()", conn))
            {
                var opened = await check.ExecuteScalarAsync(abort) as string;
                if (!string.Equals(opened, database, StringComparison.OrdinalIgnoreCase))
                    return $"The connection opened database {opened}, not {database}, so this file was not run.";
            }
            await using (var set = new SqlCommand(SqlcmdSession, conn))
                await set.ExecuteNonQueryAsync(abort);

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(abort);
            try
            {
                for (var i = 0; i < file.Batches.Count; i++)
                {
                    current = file.Batches[i];
                    await using var cmd = new SqlCommand(current.Text, conn, tx) { CommandTimeout = 0 };
                    await cmd.ExecuteNonQueryAsync(abort);
                    statementDone?.Invoke(i);
                }
                beforeCommit();   // write mode turned off since the last watch tick cancels it here, not a second later
                abort.ThrowIfCancellationRequested();
                await tx.CommitAsync(CancellationToken.None);
                return null;
            }
            catch
            {
                // a severe error may already have ended the transaction; disposing it rolls back what is left
                try { await tx.RollbackAsync(); } catch (Exception ex) when (ex is InvalidOperationException or DbException) { }
                throw;
            }
        }
        catch (Exception) when (abort.IsCancellationRequested)
        {
            return "Cancelled while it ran: this file was rolled back.";
        }
        catch (SqlException ex)
        {
            return DescribeError(ex, current);
        }
        // any other driver's error ends the file the same way, so the run - and its audit entry - always finishes
        catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or TimeoutException)
        {
            return ex.Message;
        }
    }

    /// <summary>"Msg 2714, line 12: ..." per error, the line counted in the file as sqlcmd would report it.</summary>
    public string DescribeError(Exception ex, SqlBatch? batch)
    {
        if (ex is not SqlException sql) return ex.Message;
        var lines = sql.Errors.Cast<SqlError>()
            .Where(e => e.Class > 10)
            .Select(e => $"Msg {e.Number}, line {(batch is null ? e.LineNumber : batch.StartLine + Math.Max(e.LineNumber, 1) - 1)}: {e.Message}")
            .ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : sql.Message;
    }
}
