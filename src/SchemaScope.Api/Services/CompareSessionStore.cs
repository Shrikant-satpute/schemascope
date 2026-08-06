using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;

namespace SchemaScope.Api.Services;

/// <summary>
/// One compare run: its progress stream, its result, and the snapshots it was
/// built from (kept so the diff view can serve object bodies without going back
/// to the server).
/// </summary>
public sealed class CompareSession
{
    public string Id { get; } = Guid.NewGuid().ToString("n");
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    /// <summary>running | done | failed | cancelled</summary>
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? Hint { get; set; }

    public CompareOptions Options { get; set; } = new();
    public CompareResult? Result { get; set; }
    public SchemaSnapshot? Source { get; set; }
    public List<SchemaSnapshot> Targets { get; set; } = [];

    public CancellationTokenSource Cancellation { get; } = new();

    private readonly List<ProgressEvent> _history = [];
    private readonly List<Channel<ProgressEvent>> _subscribers = [];
    private readonly Lock _gate = new();

    public void Publish(ProgressEvent evt)
    {
        lock (_gate)
        {
            evt.ElapsedMs = Clock.ElapsedMilliseconds;
            _history.Add(evt);
            foreach (var ch in _subscribers) ch.Writer.TryWrite(evt);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            foreach (var ch in _subscribers) ch.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    /// <summary>
    /// Subscribes to the progress stream. Anything already published is replayed
    /// first, so a browser that connects late never misses a step.
    /// </summary>
    public ChannelReader<ProgressEvent> Subscribe()
    {
        var ch = Channel.CreateUnbounded<ProgressEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_gate)
        {
            foreach (var e in _history) ch.Writer.TryWrite(e);
            if (Status is "running") _subscribers.Add(ch);
            else ch.Writer.TryComplete();
        }

        return ch.Reader;
    }
}

/// <summary>
/// Keeps the last few runs in memory. Snapshots hold every procedure body, so
/// old runs are dropped rather than allowed to pile up.
/// </summary>
public sealed class CompareSessionStore
{
    private const int MaxSessions = 3;
    private readonly ConcurrentDictionary<string, CompareSession> _sessions = new();
    private readonly ConcurrentQueue<string> _order = new();

    public CompareSession Create()
    {
        var session = new CompareSession();
        _sessions[session.Id] = session;
        _order.Enqueue(session.Id);
        Trim();
        return session;
    }

    public CompareSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out var s)) return false;
        s.Cancellation.Cancel();
        s.Complete();
        return true;
    }

    public IEnumerable<CompareSession> All() => _sessions.Values.OrderByDescending(s => s.StartedUtc);

    private void Trim()
    {
        while (_order.Count > MaxSessions && _order.TryDequeue(out var oldId))
        {
            if (_sessions.TryRemove(oldId, out var old))
            {
                old.Cancellation.Cancel();
                old.Complete();
            }
        }
    }
}
