using System.Diagnostics;
using SchemaScope.Core.Model;

namespace SchemaScope.Core.Comparison;

public sealed record CompareRun(
    CompareResult Result,
    SchemaSnapshot Source,
    IReadOnlyList<SchemaSnapshot> Targets);

/// <summary>
/// Reads the source and every target at the same time, then runs the compare.
///
/// Reading in parallel is why four databases cost about the same wall clock time
/// as one. A target that cannot be reached is recorded and skipped - it never
/// takes the whole run down with it.
/// </summary>
public sealed class CompareRunner
{
    private readonly Func<SchemaSource, ISchemaReader> _readerFor;

    /// <summary>One reader for every database (SQL Server only, and the Spike).</summary>
    public CompareRunner(ISchemaReader reader) => _readerFor = _ => reader;

    /// <summary>A reader per database, picked by its engine (the API: <c>src => registry.Get(src.Engine).CreateReader()</c>).</summary>
    public CompareRunner(Func<SchemaSource, ISchemaReader> readerFor) => _readerFor = readerFor;

    public async Task<CompareRun> RunAsync(
        SchemaSource source,
        IReadOnlyList<SchemaSource> targets,
        CompareOptions options,
        IProgress<ReadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var sourceTask = ReadSafeAsync(source, options, progress, ct);
        var targetTasks = targets.Select(t => ReadSafeAsync(t, options, progress, ct)).ToArray();

        await Task.WhenAll(targetTasks.Prepend(sourceTask));

        var sourceSnapshot = await sourceTask;
        if (sourceSnapshot.Failed)
            throw new SchemaReadException(
                $"The source database could not be read: {sourceSnapshot.Error}",
                sourceSnapshot.ErrorHint);

        var targetSnapshots = targetTasks.Select(t => t.Result).ToList();
        var readMs = sw.ElapsedMilliseconds;

        progress?.Report(new ReadProgress("*", "Comparing", 90));

        var engine = new CompareEngine(options);
        var result = engine.Compare(sourceSnapshot, targetSnapshots);

        result.Timings["read"] = readMs;
        result.Timings["total"] = sw.ElapsedMilliseconds;
        result.TotalMs = sw.ElapsedMilliseconds;

        progress?.Report(new ReadProgress("*", "Done", 100,
            $"{result.TotalRows} objects, {result.RowsWithDifferences} with differences"));

        return new CompareRun(result, sourceSnapshot, targetSnapshots);
    }

    private async Task<SchemaSnapshot> ReadSafeAsync(
        SchemaSource src, CompareOptions options, IProgress<ReadProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await _readerFor(src).ReadAsync(src, options, progress, ct);
        }
        catch (SchemaReadException ex)
        {
            progress?.Report(new ReadProgress(src.Id, "Failed", 100, ex.Message));
            return Failed(src, ex.Message, ex.Hint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new ReadProgress(src.Id, "Failed", 100, ex.Message));
            return Failed(src, ex.Message, null);
        }
    }

    private static SchemaSnapshot Failed(SchemaSource src, string error, string? hint) => new()
    {
        Engine = src.Engine,
        Label = src.DisplayName,
        Server = src.Server,
        Database = src.Database,
        Error = error,
        ErrorHint = hint
    };
}
