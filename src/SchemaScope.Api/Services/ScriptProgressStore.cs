using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SchemaScope.Scripting;

namespace SchemaScope.Api.Services;

/// <summary>
/// How far each running "Create scripts" job is, keyed by an id the dialog makes up and sends with its
/// request, then polls. Only the percentage and the step name are kept - never a path or a connection.
/// An entry lives exactly as long as its request: the run removes it when it ends, however it ends.
/// </summary>
public sealed partial class ScriptProgressStore
{
    private readonly ConcurrentDictionary<string, Sink> runs = new(StringComparer.Ordinal);

    /// <summary>The ids the dialog sends: a GUID or similar, nothing that could be a path.</summary>
    public static bool IsValidId(string id) => IdRx().IsMatch(id);

    /// <summary>Starts tracking <paramref name="id"/>; dispose the result when the run ends.</summary>
    public Sink Track(string id)
    {
        var sink = new Sink(this, id);
        runs[id] = sink;
        return sink;
    }

    public ScriptProgress? Get(string id) => runs.TryGetValue(id, out var sink) ? sink.Current : null;

    /// <summary>
    /// Reports land in order on the run's own thread, so this is a plain field write, not
    /// <see cref="Progress{T}"/> (which posts to the thread pool and can deliver out of order).
    /// The percentage never goes backwards.
    /// </summary>
    public sealed class Sink(ScriptProgressStore store, string id) : IProgress<ScriptProgress>, IDisposable
    {
        private volatile ScriptProgress current = new(0, "Starting");

        public ScriptProgress Current => current;

        public void Report(ScriptProgress value)
        {
            var percent = Math.Clamp(value.Percent, 0, 100);
            current = new ScriptProgress(Math.Max(percent, current.Percent), value.Step);
        }

        // only this run's entry: a second request reusing the id has replaced it
        public void Dispose() => store.runs.TryRemove(new KeyValuePair<string, Sink>(id, this));
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdRx();
}
