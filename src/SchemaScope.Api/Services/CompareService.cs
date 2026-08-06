using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.SqlServer;

namespace SchemaScope.Api.Services;

/// <summary>Turns an API request into a running compare, and serves object detail afterwards.</summary>
public sealed class CompareService(
    CompareSessionStore sessions,
    SchemaScopeStore store,
    SqlServerConnectionService connections,
    ILogger<CompareService> log)
{
    public CompareSession Start(CompareRequest request)
    {
        var options = ResolveOptions(request);
        var source = Resolve(request.Source, "source");
        var targets = request.Targets
            .Select((t, i) => Resolve(t, $"target{i}"))
            .ToList();

        var session = sessions.Create();
        session.Options = options;

        session.Publish(new ProgressEvent
        {
            Type = "progress",
            SourceId = "*",
            Stage = "Starting",
            Percent = 0,
            Detail = $"Reading {targets.Count + 1} databases"
        });

        var history = BuildHistory(request, source, targets, options);

        _ = Task.Run(() => RunAsync(session, source, targets, options, history));
        return session;
    }

    /// <summary>
    /// What this run would need to be set up again: which saved connection each
    /// side came from, plus the names as they read today.
    /// </summary>
    private static CompareHistoryEntry BuildHistory(
        CompareRequest request, SchemaSource source, List<SchemaSource> targets, CompareOptions options)
    {
        static CompareHistoryDatabase Describe(SourceRef? reference, SchemaSource resolved) => new()
        {
            ConnectionId = reference?.ConnectionId,
            Label = resolved.DisplayName,
            Server = resolved.Server,
            Database = resolved.Database
        };

        var entry = new CompareHistoryEntry
        {
            Source = Describe(request.Source, source),
            Targets = [.. targets.Select((t, i) => Describe(request.Targets.ElementAtOrDefault(i), t))],
            Options = options
        };

        entry.Signature = CompareHistoryEntry.BuildSignature(entry.Source, entry.Targets);
        return entry;
    }

    private async Task RunAsync(
        CompareSession session, SchemaSource source, List<SchemaSource> targets, CompareOptions options,
        CompareHistoryEntry history)
    {
        var labels = targets.ToDictionary(t => t.Id, t => t.DisplayName);
        labels[source.Id] = source.DisplayName;

        var progress = new Progress<ReadProgress>(p => session.Publish(new ProgressEvent
        {
            Type = "progress",
            SourceId = p.SourceId,
            Label = labels.GetValueOrDefault(p.SourceId, ""),
            Stage = p.Stage,
            Percent = p.Percent,
            Detail = p.Detail
        }));

        try
        {
            var runner = new CompareRunner(new SqlServerSchemaReader());
            var run = await runner.RunAsync(source, targets, options, progress, session.Cancellation.Token);

            session.Result = run.Result;
            session.Source = run.Source;
            session.Targets = [.. run.Targets];
            session.Status = "done";

            Remember(history, run.Result);

            session.Publish(new ProgressEvent
            {
                Type = "done",
                SourceId = "*",
                Stage = "Done",
                Percent = 100,
                Detail = $"{run.Result.TotalRows} objects compared in {run.Result.TotalMs} ms"
            });
        }
        catch (OperationCanceledException)
        {
            session.Status = "cancelled";
            session.Publish(new ProgressEvent { Type = "error", Stage = "Cancelled", Detail = "Cancelled" });
        }
        catch (SchemaReadException ex)
        {
            session.Status = "failed";
            session.Error = ex.Message;
            session.Hint = ex.Hint;
            session.Publish(new ProgressEvent { Type = "error", Stage = "Failed", Detail = ex.Message });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Compare failed");
            session.Status = "failed";
            session.Error = ex.Message;
            session.Publish(new ProgressEvent { Type = "error", Stage = "Failed", Detail = ex.Message });
        }
        finally
        {
            session.Complete();
        }
    }

    /// <summary>
    /// Files a finished run in the history list. Wrapped, because a compare that
    /// worked must not be reported as failed just because a bookkeeping write
    /// went wrong.
    /// </summary>
    private void Remember(CompareHistoryEntry history, CompareResult result)
    {
        try
        {
            history.LastRunUtc = DateTime.UtcNow;
            history.ObjectCount = result.Source.ObjectCount;
            history.DifferenceCount = result.RowsWithDifferences;
            history.LowestMatchPercent = result.Targets.Count == 0
                ? 100
                : result.Targets.Min(t => t.MatchPercent);
            history.TotalMs = result.TotalMs;

            store.SaveHistory(history);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not write the compare to history");
        }
    }

    private CompareOptions ResolveOptions(CompareRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ProfileId))
        {
            var profile = store.GetProfiles().FirstOrDefault(p => p.Id == request.ProfileId);
            if (profile is not null) return profile.Options;
        }
        return request.Options ?? new CompareOptions();
    }

    private SchemaSource Resolve(SourceRef reference, string id)
    {
        ConnectionSettings settings;

        if (reference.Inline is not null)
        {
            settings = reference.Inline;
        }
        else if (!string.IsNullOrWhiteSpace(reference.ConnectionId))
        {
            settings = store.GetConnection(reference.ConnectionId)
                       ?? throw new SchemaReadException($"Connection '{reference.ConnectionId}' was not found.");
            store.TouchConnection(settings.Id);
        }
        else
        {
            throw new SchemaReadException("Every source needs either a saved connection or an inline connection.");
        }

        var database = string.IsNullOrWhiteSpace(reference.Database) ? settings.Database : reference.Database;

        return new SchemaSource
        {
            Id = id,
            Label = reference.Label
                    ?? (string.IsNullOrWhiteSpace(settings.Label)
                        ? $"{settings.Server}.{database}"
                        : database == settings.Database ? settings.Label : $"{settings.Label} / {database}"),
            Server = settings.Server,
            Database = database,
            ConnectionString = connections.BuildConnectionString(settings, database)
        };
    }

    // ---------------------------------------------------------------
    //  Object detail for the diff panel
    // ---------------------------------------------------------------

    public ObjectDetail? GetObject(CompareSession session, string key)
    {
        if (session.Result is null || session.Source is null) return null;

        var row = session.Result.Rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return null;

        var sourceObj = session.Source.Objects.FirstOrDefault(o => o.Key == key);

        var detail = new ObjectDetail
        {
            Key = key,
            TypeLabel = row.TypeLabel,
            Schema = row.Schema,
            Name = row.Name,
            ParentName = row.ParentName,
            IsModule = row.Type.IsModule(),
            HasStructure = row.Type is DbObjectType.Table or DbObjectType.TableType,
            SourceExists = sourceObj is not null,
            SourceText = sourceObj?.DisplayText ?? "",
            SourceLabel = session.Result.Source.Label,
            SourceModified = sourceObj?.ModifyDate
        };

        for (var i = 0; i < session.Targets.Count; i++)
        {
            var snapshot = session.Targets[i];
            var targetObj = snapshot.Objects.FirstOrDefault(o => o.Key == key);
            var cell = i < row.Cells.Count ? row.Cells[i] : new TargetCell();

            detail.Targets.Add(new TargetObjectDetail
            {
                Id = i.ToString(),
                Label = session.Result.Targets[i].Label,
                Status = cell.Status,
                Exists = targetObj is not null,
                Text = targetObj?.DisplayText ?? "",
                Note = cell.Note,
                AddedLines = cell.AddedLines,
                RemovedLines = cell.RemovedLines,
                Structure = detail.HasStructure
                    ? StructureDiffService.Build(sourceObj, targetObj, session.Options)
                    : []
            });
        }

        return detail;
    }
}
