using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SchemaScope.Core.Engines;
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

/// <summary>What the target reports about itself before an apply.</summary>
/// <param name="Key">The server's own name and the opened database ("SERVER\INSTANCE|Db"), whatever spelling the connection used.</param>
/// <param name="InMemory">Its memory-optimized tables and table types and natively compiled modules, "schema.name".</param>
public sealed record TargetFacts(string Key, IReadOnlySet<string> InMemory)
{
    /// <summary>
    /// Another engine's own word for where it runs (PostgreSQL: <c>inet_server_addr()</c>), for the Protected re-check;
    /// null for SQL Server, whose server name is the first part of <see cref="Key"/>.
    /// </summary>
    public IReadOnlyList<string>? Hosts { get; init; }
}

/// <summary>
/// The only code in SchemaScope that changes a database. It runs the files of a checked plan (<see cref="ApplyJob"/>)
/// one by one, each in its own transaction - BEGIN, its batches, COMMIT; on an error ROLLBACK and stop - with the
/// session SET options sqlcmd gives run_all.ps1, so a file does here exactly what RUN.ps1 would do. Files committed
/// before a failure stay applied. Every run is written to the local audit log as it goes. Running a file is the
/// target engine's <see cref="IApplyExecutor"/>; the statics below forward to the SQL Server one.
/// </summary>
public sealed class ApplyService(SchemaScopeStore store, WriteMode writeMode, ILogger<ApplyService> log,
    IEnumerable<IApplyExecutor>? executors = null)
{
    private readonly Dictionary<DatabaseEngine, IApplyExecutor> executors =
        (executors ?? []).DefaultIfEmpty(SqlServerApplyExecutor.Instance).ToDictionary(e => e.Engine);

    /// <summary>The executor for <paramref name="engine"/>; null when this version cannot apply to it.</summary>
    public IApplyExecutor? ExecutorFor(DatabaseEngine engine) => executors.GetValueOrDefault(engine);

    /// <inheritdoc cref="SqlServerApplyExecutor.SqlcmdSession"/>
    public const string SqlcmdSession = SqlServerApplyExecutor.SqlcmdSession;

    /// <inheritdoc cref="SqlServerApplyExecutor.SqlcmdOptions"/>
    public const int SqlcmdOptions = SqlServerApplyExecutor.SqlcmdOptions;

    /// <summary>How often a running apply looks at write mode (and counts as activity while it is on).</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, byte> busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Run> running = new(StringComparer.Ordinal);

    /// <summary>Stop ends a run after its current file; Abort cancels that file too (it rolls back).</summary>
    private sealed record Run(CancellationTokenSource Stop, CancellationTokenSource Abort);

    /// <inheritdoc cref="SqlServerApplyExecutor.TargetKey(TargetFacts?, string, string)"/>
    public static string TargetKey(TargetFacts? facts, string server, string database) =>
        SqlServerApplyExecutor.Instance.TargetKey(facts, server, database);

    public static string TargetKey(string server, string database) => SqlServerApplyExecutor.TargetKey(server, database);

    public bool TryLock(string key) => busy.TryAdd(key, 0);

    public void Unlock(string key) => busy.TryRemove(key, out _);

    /// <summary>
    /// Asks the apply that reports to <paramref name="progressId"/> to stop after its current file - or, with
    /// <paramref name="now"/>, to cancel that file as well: it is rolled back (for a file stuck waiting on locks).
    /// </summary>
    public bool Stop(string progressId, bool now = false)
    {
        if (!running.TryGetValue(progressId, out var run)) return false;
        try
        {
            run.Stop.Cancel();
            if (now) run.Abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it has just finished
        }
        return true;
    }

    /// <inheritdoc cref="SqlServerApplyExecutor.InspectAsync"/>
    public static Task<TargetFacts?> InspectAsync(string connectionString) => SqlServerApplyExecutor.Instance.InspectAsync(connectionString);

    /// <inheritdoc cref="SqlServerApplyExecutor.MemoryOptimizedProblem"/>
    public static string? MemoryOptimizedProblem(IReadOnlyList<VerifiedFile> files, IReadOnlySet<string> inMemory) =>
        SqlServerApplyExecutor.MemoryOptimizedProblem(files, inMemory);

    /// <summary>
    /// Reads every file of the plan and checks it against the hash taken when it was generated - all of them, before
    /// anything runs. Null and the batches to run, or the reason nothing may run.
    /// </summary>
    public static string? Verify(IReadOnlyList<PlanFile> files, out List<VerifiedFile> verified) =>
        Verify(files, SqlServerApplyExecutor.Instance.Splitter, out verified);

    /// <inheritdoc cref="Verify(IReadOnlyList{PlanFile}, out List{VerifiedFile})"/>
    /// <param name="splitter">The target engine's statement splitter (<see cref="IApplyExecutor.Splitter"/>).</param>
    public static string? Verify(IReadOnlyList<PlanFile> files, IStatementSplitter splitter, out List<VerifiedFile> verified)
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
            verified.Add(new VerifiedFile(f, splitter.Split(Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip))));
        }
        return null;
    }

    /// <summary>Runs verified files in order until one fails, the run is stopped, or write mode goes off.</summary>
    public async Task<ApplyResult> RunAsync(ApplyJob job, IReadOnlyList<VerifiedFile> files, IProgress<ScriptProgress>? progress)
    {
        var secrets = SecretScrubber.Secrets([job.ConnectionString]);
        string Scrub(string text) => SecretScrubber.Scrub(text, secrets);
        // the route only gets here for an engine with an executor
        var executor = ExecutorFor(job.Target.Engine)
                       ?? throw new InvalidOperationException($"{job.Target.Engine} targets cannot be applied to in this version.");

        var audit = new ApplyRunRecord
        {
            CompareRunId = job.CompareRunId,
            TargetLabel = job.Target.Label,
            Engine = job.Target.Engine,
            // another engine's port is kept apart from its host (ApplyTarget.Port); SQL Server's is in the name
            Server = job.Target.Port is { } port ? $"{job.Target.Server}:{port}" : job.Target.Server,
            Database = job.Target.Database,
            Login = job.Target.Login,
            Sections = [.. job.Sections],
            Message = "Running",
        };
        // no record, no apply: the log must show everything that reached a database
        store.StartApplyRun(audit);

        using var stop = new CancellationTokenSource();
        using var abort = new CancellationTokenSource();
        var run = new Run(stop, abort);
        if (job.ProgressId is { } pid) running[pid] = run;
        // Turning write mode off cancels the file that is running too (it rolls back): nothing commits once the UI says
        // read-only. While it is on, a running apply is activity, so a long file never idles it out half-way.
        using var watch = new Timer(_ =>
        {
            try
            {
                if (writeMode.Enabled) writeMode.Touch();
                else abort.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the run has ended
            }
        }, null, WatchInterval, WatchInterval);
        var result = new ApplyResult { AuditId = audit.Id };
        string? stoppedReason = null;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i].File;
                var name = $"{f.Folder}\\{f.RelativePath}";
                if (stop.IsCancellationRequested || abort.IsCancellationRequested || !writeMode.Enabled)
                {
                    stoppedReason = stop.IsCancellationRequested ? "you stopped it" : "write mode was turned off";
                    result.StoppedAt = name;
                    break;
                }
                progress?.Report(new ScriptProgress(i * 100 / files.Count, $"Applying {i + 1} of {files.Count}: {name}"));

                var sw = Stopwatch.StartNew();
                var error = await executor.RunFileAsync(job.ConnectionString, job.Target.Database, files[i], abort.Token,
                    () => { if (!writeMode.Enabled) abort.Cancel(); });
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
                var logged = Record(audit.Id, i, record);
                writeMode.Touch();   // a running apply is activity: a long one must not time out half-way
                if (error is not null)
                {
                    if (abort.IsCancellationRequested)
                        stoppedReason = writeMode.Enabled ? "you cancelled it" : "write mode was turned off";
                    result.StoppedAt = name;
                    break;
                }
                // no record, no apply: a file the log cannot show is the last one that runs
                if (!logged && i + 1 < files.Count)
                {
                    stoppedReason = "the apply log could not be written";
                    result.StoppedAt = $"{files[i + 1].File.Folder}\\{files[i + 1].File.RelativePath}";
                    break;
                }
            }
        }
        finally
        {
            if (job.ProgressId is { } id) running.TryRemove(new KeyValuePair<string, Run>(id, run));
        }

        var ok = result.Applied.Count(a => a.Ok);
        var notRun = files.Count - result.Applied.Count;
        var rolledBack = result.Applied.Any(a => !a.Ok);
        var target = job.Target.Label;
        result.Success = ok == files.Count;
        result.Message = result.Success
            ? $"Applied all {files.Count} file(s) to {target}."
            : stoppedReason is not null
                ? $"Stopped because {stoppedReason}: {ok} file(s) were applied to {target} and stay applied; " +
                  $"{(rolledBack ? $"{result.StoppedAt} was rolled back and " : "")}{notRun} were not run."
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

    /// <summary>False when the file could not be written to the audit log.</summary>
    private bool Record(string runId, int seq, ApplyFileRecord file)
    {
        try
        {
            store.AddApplyFile(runId, seq, file);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not log an applied file: {Message}", ex.Message);
            return false;
        }
    }
}
