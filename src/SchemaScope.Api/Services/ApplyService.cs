using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.Scripting;
using SqlBatch = SchemaScope.Scripting.SqlBatch;

namespace SchemaScope.Api.Services;

/// <summary>What one apply runs: the checked plan files, against the session's own target connection.</summary>
/// <param name="ConnectionString">The compare session's target connection string, used unmodified.</param>
public sealed record ApplyJob(
    string CompareRunId, string ConnectionString, ApplyTarget Target,
    IReadOnlyList<PlanFile> Files, IReadOnlyList<string> Sections, string? ProgressId);

/// <summary>A plan file read back and proved unchanged since it was generated.</summary>
public sealed record VerifiedFile(PlanFile File, IReadOnlyList<SqlBatch> Batches);

/// <summary>
/// The only code in SchemaScope that changes a database. It runs the files of a checked plan (<see cref="ApplyJob"/>)
/// one by one, each in its own transaction - BEGIN, its batches, COMMIT; on an error ROLLBACK and stop - with the
/// session SET options sqlcmd gives run_all.ps1, so a file does here exactly what RUN.ps1 would do. Files committed
/// before a failure stay applied. Every run is written to the local audit log as it goes.
/// </summary>
public sealed class ApplyService(SchemaScopeStore store, WriteMode writeMode, ILogger<ApplyService> log)
{
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

    private readonly ConcurrentDictionary<string, byte> busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    /// <summary>One apply per target database at a time.</summary>
    public static string TargetKey(string server, string database) => $"{server.Trim()}|{database.Trim()}";

    public bool TryLock(string key) => busy.TryAdd(key, 0);

    public void Unlock(string key) => busy.TryRemove(key, out _);

    /// <summary>Asks the apply that reports to <paramref name="progressId"/> to stop after its current file.</summary>
    public bool Stop(string progressId)
    {
        if (!running.TryGetValue(progressId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Reads every file of the plan and checks it against the hash taken when it was generated - all of them, before
    /// anything runs. Null and the batches to run, or the reason nothing may run.
    /// </summary>
    public static string? Verify(IReadOnlyList<PlanFile> files, out List<VerifiedFile> verified)
    {
        verified = [];
        foreach (var f in files)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(f.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"{f.Folder}\\{f.RelativePath} can no longer be read, so nothing was applied. Generate the scripts again.";
            }
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), f.Sha256, StringComparison.Ordinal))
                return $"{f.Folder}\\{f.RelativePath} was changed after it was generated, so nothing was applied. " +
                       "Generate the scripts again.";

            // as sqlcmd -f 65001 reads it: UTF-8, the BOM dropped (the same bytes that were hashed)
            var skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            verified.Add(new VerifiedFile(f, ScriptText.SplitBatches(Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip))));
        }
        return null;
    }

    /// <summary>Runs verified files in order until one fails, the run is stopped, or write mode goes off.</summary>
    public async Task<ApplyResult> RunAsync(ApplyJob job, IReadOnlyList<VerifiedFile> files, IProgress<ScriptProgress>? progress)
    {
        var secrets = SecretScrubber.Secrets([job.ConnectionString]);
        string Scrub(string text) => SecretScrubber.Scrub(text, secrets);

        var audit = new ApplyRunRecord
        {
            CompareRunId = job.CompareRunId,
            TargetLabel = job.Target.Label,
            Server = job.Target.Server,
            Database = job.Target.Database,
            Login = job.Target.Login,
            Sections = [.. job.Sections],
            Message = "Running",
        };
        // no record, no apply: the log must show everything that reached a database
        store.StartApplyRun(audit);

        using var stop = new CancellationTokenSource();
        if (job.ProgressId is { } pid) running[pid] = stop;
        var result = new ApplyResult { AuditId = audit.Id };
        string? stoppedReason = null;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i].File;
                var name = $"{f.Folder}\\{f.RelativePath}";
                if (stop.IsCancellationRequested || !writeMode.Enabled)
                {
                    stoppedReason = stop.IsCancellationRequested ? "you stopped it" : "write mode was turned off";
                    result.StoppedAt = name;
                    break;
                }
                progress?.Report(new ScriptProgress(i * 100 / files.Count, $"Applying {i + 1} of {files.Count}: {name}"));

                var sw = Stopwatch.StartNew();
                var error = await RunFileAsync(job.ConnectionString, job.Target.Database, files[i].Batches);
                var record = new ApplyFileRecord
                {
                    Folder = f.Folder,
                    File = f.RelativePath,
                    ObjectName = f.ObjectName,
                    Ok = error is null,
                    Error = error is null ? null : Scrub(error),
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
                result.Applied.Add(record);
                Record(audit.Id, i, record);
                writeMode.Touch();   // a running apply is activity: a long one must not time out half-way
                if (error is not null)
                {
                    result.StoppedAt = name;
                    break;
                }
            }
        }
        finally
        {
            if (job.ProgressId is { } id) running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, stop));
        }

        var ok = result.Applied.Count(a => a.Ok);
        var notRun = files.Count - result.Applied.Count;
        var target = job.Target.Label;
        result.Success = ok == files.Count;
        result.Message = result.Success
            ? $"Applied all {files.Count} file(s) to {target}."
            : stoppedReason is not null
                ? $"Stopped because {stoppedReason}: {ok} file(s) were applied to {target} and stay applied; {notRun} were not run."
                : $"{result.StoppedAt} failed and was rolled back. The {ok} file(s) before it stay applied to {target}; " +
                  $"the {notRun} after it were not run.";
        progress?.Report(new ScriptProgress(100, result.Success ? "Done" : "Stopped"));

        audit.Success = result.Success;
        audit.Message = result.Message;
        audit.StoppedAt = result.StoppedAt;
        audit.FinishedUtc = DateTime.UtcNow;
        try { store.FinishApplyRun(audit); }
        catch (Exception ex) { log.LogWarning("Could not finish the apply log entry: {Message}", Scrub(ex.Message)); }
        return result;
    }

    private void Record(string runId, int seq, ApplyFileRecord file)
    {
        try { store.AddApplyFile(runId, seq, file); }
        catch (Exception ex) { log.LogWarning("Could not log an applied file: {Message}", ex.Message); }
    }

    /// <summary>
    /// One file on its own connection (sqlcmd opens one per file too): check the database, set sqlcmd's options,
    /// then BEGIN TRAN, every batch, COMMIT. Null when it committed, else the error; the transaction is rolled back.
    /// No command timeout and no cancellation, as with sqlcmd: a file is never cut off half-way.
    /// </summary>
    private static async Task<string?> RunFileAsync(string connectionString, string database, IReadOnlyList<SqlBatch> batches)
    {
        SqlBatch? current = null;
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using (var check = new SqlCommand("SELECT DB_NAME()", conn))
            {
                var opened = await check.ExecuteScalarAsync() as string;
                if (!string.Equals(opened, database, StringComparison.OrdinalIgnoreCase))
                    return $"The connection opened database {opened}, not {database}, so this file was not run.";
            }
            await using (var set = new SqlCommand(SqlcmdSession, conn))
                await set.ExecuteNonQueryAsync();

            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                foreach (var batch in batches)
                {
                    current = batch;
                    await using var cmd = new SqlCommand(batch.Text, conn, tx) { CommandTimeout = 0 };
                    await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return null;
            }
            catch
            {
                // a severe error may already have ended the transaction; disposing it rolls back what is left
                try { await tx.RollbackAsync(); } catch (Exception ex) when (ex is InvalidOperationException or SqlException) { }
                throw;
            }
        }
        catch (SqlException ex)
        {
            return Describe(ex, current);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return ex.Message;
        }
    }

    /// <summary>"Msg 2714, line 12: ..." per error, the line counted in the file as sqlcmd would report it.</summary>
    private static string Describe(SqlException ex, SqlBatch? batch)
    {
        var lines = ex.Errors.Cast<SqlError>()
            .Where(e => e.Class > 10)
            .Select(e => $"Msg {e.Number}, line {(batch is null ? e.LineNumber : batch.StartLine + Math.Max(e.LineNumber, 1) - 1)}: {e.Message}")
            .ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : ex.Message;
    }
}
