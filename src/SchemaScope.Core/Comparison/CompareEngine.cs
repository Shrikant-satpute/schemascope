using System.Collections.Concurrent;
using System.Diagnostics;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Core.Comparison;

/// <summary>
/// Lines one source snapshot up against many target snapshots and produces the
/// matrix the UI renders.
///
/// Order of work, cheapest first:
///   1. hash equal            -> Same        (no further work, ~90% of objects)
///   2. loose hash equal      -> FormattingOnly
///   3. ScriptDom re-print    -> FormattingOnly  (only for what is left)
///   4. otherwise             -> Different, with a line count
/// </summary>
public sealed class CompareEngine(CompareOptions options)
{
    private readonly CompareOptions _options = options;

    public CompareResult Compare(SchemaSnapshot source, IReadOnlyList<SchemaSnapshot> targets)
    {
        var sw = Stopwatch.StartNew();
        var result = new CompareResult
        {
            Source = Describe(source),
            Timings = new Dictionary<string, long>()
        };

        var sourceIndex = source.BuildIndex();
        var targetIndexes = targets.Select(t => t.BuildIndex()).ToList();

        // Every key seen anywhere, so objects that exist only in a target still
        // get a row.
        var allKeys = new HashSet<string>(sourceIndex.Keys, StringComparer.Ordinal);
        foreach (var idx in targetIndexes) allKeys.UnionWith(idx.Keys);

        result.Timings["index"] = sw.ElapsedMilliseconds;
        var stage = Stopwatch.StartNew();

        var rows = new ConcurrentBag<CompareRow>();
        var summaries = targets.Select((t, i) => new TargetSummary
        {
            Id = i.ToString(),
            Label = string.IsNullOrWhiteSpace(t.Label) ? $"{t.Server}.{t.Database}" : t.Label,
            Server = t.Server,
            Database = t.Database,
            ProductVersion = t.ProductVersion,
            Collation = t.Collation,
            ReadMs = t.TimingsMs.GetValueOrDefault("total"),
            LastObjectChangeUtc = t.Objects.Count == 0 ? null : t.Objects.Max(o => o.ModifyDate),
            Error = t.Error
        }).ToList();

        var targetFailed = targets.Select(t => t.Failed).ToArray();

        // one counter set per target, kept per thread then merged
        var counters = new int[targets.Count, 6];
        var counterLock = new Lock();

        Parallel.ForEach(
            allKeys,
            () => new int[targets.Count, 6],
            (key, _, local) =>
            {
                sourceIndex.TryGetValue(key, out var src);

                var sample = src;
                if (sample is null)
                {
                    for (var i = 0; i < targetIndexes.Count && sample is null; i++)
                        targetIndexes[i].TryGetValue(key, out sample);
                }
                if (sample is null) return local;

                var row = new CompareRow
                {
                    Key = key,
                    Type = sample.Type,
                    Schema = sample.Schema,
                    Name = sample.Name,
                    ParentName = sample.ParentName
                };

                var worst = ObjectStatus.Same;
                var differing = 0;

                for (var i = 0; i < targetIndexes.Count; i++)
                {
                    TargetCell cell;
                    if (targetFailed[i])
                    {
                        cell = new TargetCell { Status = ObjectStatus.Unavailable, Note = "This database could not be read" };
                    }
                    else
                    {
                        targetIndexes[i].TryGetValue(key, out var tgt);
                        cell = Classify(src, tgt);
                    }
                    row.Cells.Add(cell);

                    local[i, (int)cell.Status]++;
                    if (cell.Status.IsDifference()) differing++;
                    if (cell.Status.Severity() > worst.Severity()) worst = cell.Status;
                }

                row.Worst = worst;
                row.DifferingTargets = differing;
                rows.Add(row);
                return local;
            },
            local =>
            {
                lock (counterLock)
                {
                    for (var i = 0; i < targets.Count; i++)
                        for (var s = 0; s < 6; s++)
                            counters[i, s] += local[i, s];
                }
            });

        result.Timings["compare"] = stage.ElapsedMilliseconds;
        stage.Restart();

        for (var i = 0; i < summaries.Count; i++)
        {
            var s = summaries[i];
            s.Same = counters[i, (int)ObjectStatus.Same];
            s.FormattingOnly = counters[i, (int)ObjectStatus.FormattingOnly];
            s.Different = counters[i, (int)ObjectStatus.Different];
            s.MissingInTarget = counters[i, (int)ObjectStatus.MissingInTarget];
            s.OnlyInTarget = counters[i, (int)ObjectStatus.OnlyInTarget];
            s.Unavailable = counters[i, (int)ObjectStatus.Unavailable];
            s.TotalObjects = targets[i].Objects.Count;

            var comparable = s.Same + s.FormattingOnly + s.Different + s.MissingInTarget + s.Unavailable;
            s.MatchPercent = comparable == 0 ? 100 : Math.Round(100.0 * s.Same / comparable, 1);
        }

        result.Targets = summaries;
        result.Rows = [.. rows
            .OrderBy(r => (int)r.Type)
            .ThenBy(r => r.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

        result.ByType = [.. result.Rows
            .GroupBy(r => r.Type)
            .Select(g => new TypeBreakdown
            {
                Type = g.Key,
                Total = g.Count(),
                WithDifferences = g.Count(r => r.HasDifference)
            })
            .OrderByDescending(b => b.Total)];

        foreach (var t in targets.Where(t => t.UnreadableCount > 0))
            result.Warnings.Add(
                $"{t.Label}: {t.UnreadableCount} object(s) could not be read (encrypted, or the login lacks VIEW DEFINITION).");

        if (source.UnreadableCount > 0)
            result.Warnings.Add(
                $"{source.Label}: {source.UnreadableCount} object(s) could not be read (encrypted, or the login lacks VIEW DEFINITION).");

        result.Timings["summarise"] = stage.ElapsedMilliseconds;
        result.TotalMs = sw.ElapsedMilliseconds;
        return result;
    }

    private TargetCell Classify(DbObject? src, DbObject? tgt)
    {
        if (src is null && tgt is null)
            return new TargetCell { Status = ObjectStatus.Same };

        if (tgt is null)
            return new TargetCell { Status = ObjectStatus.MissingInTarget, Note = "Not present in this target" };

        if (src is null)
            return new TargetCell { Status = ObjectStatus.OnlyInTarget, Note = "Only exists in this target" };

        // 1. exact
        if (string.Equals(src.Hash, tgt.Hash, StringComparison.Ordinal))
            return new TargetCell { Status = ObjectStatus.Same };

        var unreadable = src.IsEncrypted || src.DefinitionUnavailable ||
                         tgt.IsEncrypted || tgt.DefinitionUnavailable;

        if (unreadable)
            return new TargetCell
            {
                Status = ObjectStatus.Unavailable,
                Note = src.IsEncrypted || tgt.IsEncrypted
                    ? "Encrypted - the body cannot be read on either side"
                    : "No VIEW DEFINITION permission - the body cannot be read"
            };

        // 2. formatting only
        if (string.Equals(src.LooseHash, tgt.LooseHash, StringComparison.Ordinal))
            return Formatting(src, tgt, "Only whitespace, comments or casing differ");

        // 3. deep check - parse both and re-print in one style.
        //    Gated on size and similarity: two bodies whose stripped lengths are
        //    far apart cannot be the same code, and parsing them anyway is what
        //    would make a big compare slow.
        if (_options.UseSmartParseCompare && src.Type.IsModule() &&
            WorthDeepCheck(src, tgt) &&
            SchemaNormalizer.ParsesIdentically(src.CompareText, tgt.CompareText))
            return Formatting(src, tgt, "Same code, written differently (optional keywords or layout)");

        // 4. a real difference
        var counts = LineDiffService.CountChanges(src.DisplayText, tgt.DisplayText);
        return new TargetCell
        {
            Status = ObjectStatus.Different,
            AddedLines = counts.Added,
            RemovedLines = counts.Removed
        };
    }

    /// <summary>
    /// Cheap gate in front of the ScriptDom parse. Only near-identical bodies
    /// can turn out to be "same code written differently", so anything bigger
    /// than 200 KB or more than 2% apart in stripped length is reported as a
    /// real difference without paying for a parse.
    /// </summary>
    private static bool WorthDeepCheck(DbObject a, DbObject b)
    {
        const int maxSize = 200_000;
        if (a.CompareText.Length > maxSize || b.CompareText.Length > maxSize) return false;

        var max = Math.Max(a.LooseLength, b.LooseLength);
        if (max == 0) return false;

        var delta = Math.Abs(a.LooseLength - b.LooseLength);
        return (double)delta / max <= 0.02;
    }

    private static TargetCell Formatting(DbObject src, DbObject tgt, string note)
    {
        var counts = LineDiffService.CountChanges(src.DisplayText, tgt.DisplayText);
        return new TargetCell
        {
            Status = ObjectStatus.FormattingOnly,
            AddedLines = counts.Added,
            RemovedLines = counts.Removed,
            Note = note
        };
    }

    private static SnapshotInfo Describe(SchemaSnapshot s) => new()
    {
        Label = string.IsNullOrWhiteSpace(s.Label) ? $"{s.Server}.{s.Database}" : s.Label,
        Server = s.Server,
        Database = s.Database,
        ProductVersion = s.ProductVersion,
        Collation = s.Collation,
        ObjectCount = s.Objects.Count,
        CapturedAtUtc = s.CapturedAtUtc,
        ReadMs = s.TimingsMs.GetValueOrDefault("total")
    };
}
