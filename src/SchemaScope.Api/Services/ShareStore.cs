using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Storage;

namespace SchemaScope.Api.Services;

/// <summary>Everything a shared link needs: the result, plus every object's text.</summary>
public sealed class ShareBundle
{
    public string Token { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTime ComparedUtc { get; set; }
    public CompareResult Result { get; set; } = new();
    public Dictionary<string, ObjectDetail> Objects { get; set; } = [];
}

/// <summary>One browser currently reading a shared link.</summary>
public sealed class ShareViewer
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string Token { get; init; } = "";
    public string IpAddress { get; init; } = "";
    public DateTime ConnectedUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Published comparisons.
///
/// A share is two things: a row in the local database saying it exists, and a
/// gzipped file next to it holding the comparison. Writing the file matters -
/// only the last three runs stay in memory, so without it a link would quietly
/// stop working after three more compares, and would never survive a restart.
/// </summary>
public sealed partial class ShareStore(SchemaScopeStore store, ILogger<ShareStore> log)
{
    /// <summary>Bundles are large. Two loaded at once is plenty for a desk machine.</summary>
    private const int MaxLoadedBundles = 2;

    /// <summary>A viewer that has not checked in for this long has gone.</summary>
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ConcurrentDictionary<string, (ShareBundle Bundle, DateTime Touched)> _loaded = new();
    private readonly ConcurrentDictionary<string, ShareViewer> _viewers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _revoked = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// The share list, held in memory. Every viewer request looks a token up, and doing that
    /// against SQLite would let anyone on the network turn junk requests into disk work.
    /// Refreshed whenever this process publishes, revokes or expires a share.
    /// </summary>
    private List<ShareRecord>? _records;
    private readonly Lock _recordsGate = new();

    /// <summary>Expired rows and files are swept at most this often; expiry itself is checked on every lookup.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(30);
    private DateTime _lastSweepUtc = DateTime.MinValue;

    private string Folder
    {
        get
        {
            var folder = Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "shares");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    // ---------------- publishing ----------------

    /// <summary>
    /// Turns a finished run into a link. Every object's text is baked in now, so
    /// the share never has to go back to a database - and never could.
    /// </summary>
    public ShareRecord Publish(CompareSession session, CompareService compares, TimeSpan lifetime)
    {
        if (session.Result is null) throw new InvalidOperationException("That compare has no result to share.");

        var token = NewToken();
        var bundle = new ShareBundle
        {
            Token = token,
            SourceLabel = session.Result.Source.Label,
            HostName = Environment.MachineName,
            ComparedUtc = session.Result.StartedUtc,
            Result = Scrubbed(session)
        };

        foreach (var row in session.Result.Rows)
        {
            var detail = compares.GetObject(session, row.Key);
            if (detail is not null) bundle.Objects[row.Key] = detail;
        }

        Write(token, bundle);

        var record = new ShareRecord
        {
            Token = token,
            SourceLabel = session.Result.Source.Label,
            // another engine keeps its port apart from the host: two servers on one host are told apart by it
            SourceDetail = session.Result.Source is { Engine: not DatabaseEngine.SqlServer, Port: { } port } s
                ? $"{s.Server}:{port} / {s.Database}"
                : $"{session.Result.Source.Server} / {session.Result.Source.Database}",
            TargetLabels = [.. session.Result.Targets.Select(t => t.Label)],
            ComparedUtc = session.Result.StartedUtc,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.Add(lifetime),
            ObjectCount = session.Result.TotalRows
        };

        store.SaveShare(record);
        Remember(token, bundle);
        InvalidateRecords();
        return record;
    }

    /// <summary>
    /// A copy of the result with every error and warning passed through the secret scrubber.
    /// Those strings are raw driver messages, and a share hands them to the whole network.
    /// The session's own result is left alone - the owner's window keeps the full text.
    /// </summary>
    internal static CompareResult Scrubbed(CompareSession session)
    {
        var copy = JsonSerializer.Deserialize<CompareResult>(
            JsonSerializer.SerializeToUtf8Bytes(session.Result, Json), Json) ?? new CompareResult();

        var secrets = SecretScrubber.Secrets(
            new[] { session.SourceConnection?.ConnectionString }
                .Concat(session.TargetConnections.Select(t => t.ConnectionString)));
        if (secrets.Count == 0) return copy;

        copy.Warnings = [.. copy.Warnings.Select(w => SecretScrubber.Scrub(w, secrets))];
        foreach (var target in copy.Targets)
        {
            if (target.Error is not null) target.Error = SecretScrubber.Scrub(target.Error, secrets);
        }
        return copy;
    }

    // ---------------- reading ----------------

    public List<ShareRecord> List()
    {
        Sweep(force: true);
        var now = DateTime.UtcNow;
        return [.. Records().Where(r => r.ExpiresUtc > now)];
    }

    /// <summary>
    /// The live share behind a token, or null. Expiry is checked here on every call, so a
    /// link stops working the moment it runs out, not at the next sweep. Every record is
    /// compared in constant time and the loop never exits early, so the response time says
    /// nothing about how much of a guessed token was right.
    /// </summary>
    public ShareRecord? Find(string token)
    {
        if (!IsToken(token)) return null;
        Sweep(force: false);

        var wanted = Encoding.ASCII.GetBytes(token);
        var now = DateTime.UtcNow;
        ShareRecord? match = null;
        foreach (var record in Records())
        {
            var equal = CryptographicOperations.FixedTimeEquals(wanted, Encoding.ASCII.GetBytes(record.Token));
            if (equal && record.ExpiresUtc > now) match = record;
        }
        return match;
    }

    /// <summary>
    /// Cancelled when the share is revoked or expires, so open viewer pages lose their live
    /// connection instead of being pinged forever.
    /// </summary>
    public CancellationToken Revoked(string token) =>
        _revoked.GetOrAdd(token, _ => new CancellationTokenSource()).Token;

    private List<ShareRecord> Records()
    {
        lock (_recordsGate)
        {
            return _records ??= store.GetShares();
        }
    }

    private void InvalidateRecords()
    {
        lock (_recordsGate) _records = null;
    }

    private void Sweep(bool force)
    {
        lock (_recordsGate)
        {
            if (!force && DateTime.UtcNow - _lastSweepUtc < SweepEvery) return;
            _lastSweepUtc = DateTime.UtcNow;
        }
        Expire();
    }

    /// <summary>The comparison behind a link, from memory if it is warm and from disk if not.</summary>
    public ShareBundle? Load(string token)
    {
        if (!IsToken(token)) return null;

        if (_loaded.TryGetValue(token, out var hit))
        {
            _loaded[token] = (hit.Bundle, DateTime.UtcNow);
            return hit.Bundle;
        }

        var path = PathFor(token);
        if (!File.Exists(path)) return null;

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var bundle = JsonSerializer.Deserialize<ShareBundle>(gzip, Json);
            if (bundle is null) return null;

            Remember(token, bundle);
            return bundle;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not read the shared comparison {Token}", token);
            return null;
        }
    }

    public void CountView(string token)
    {
        if (IsToken(token)) store.CountShareView(token);
    }

    // ---------------- revoking ----------------

    public void Revoke(string token)
    {
        if (!IsToken(token)) return;

        store.DeleteShare(token);
        _loaded.TryRemove(token, out _);
        foreach (var v in _viewers.Values.Where(v => v.Token == token))
            _viewers.TryRemove(v.Id, out _);

        Delete(token);
        InvalidateRecords();
        SignalRevoked(token);
    }

    public void RevokeAll()
    {
        foreach (var share in store.GetShares()) Revoke(share.Token);
    }

    /// <summary>Clears out links that have run out of time, payload files and all.</summary>
    public void Expire()
    {
        var expired = store.DeleteExpiredShares(DateTime.UtcNow);
        foreach (var token in expired)
        {
            _loaded.TryRemove(token, out _);
            Delete(token);
            SignalRevoked(token);
        }
        if (expired.Count > 0) InvalidateRecords();
    }

    private void SignalRevoked(string token)
    {
        if (_revoked.TryRemove(token, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    // ---------------- who is reading ----------------

    public ShareViewer AddViewer(string token, string ipAddress)
    {
        var viewer = new ShareViewer { Token = token, IpAddress = ipAddress };
        _viewers[viewer.Id] = viewer;
        return viewer;
    }

    public void TouchViewer(string id)
    {
        if (_viewers.TryGetValue(id, out var viewer)) viewer.LastSeenUtc = DateTime.UtcNow;
    }

    public void RemoveViewer(string id) => _viewers.TryRemove(id, out _);

    /// <summary>Open live connections, for the per-address and per-link caps.</summary>
    public int ViewerCount(string? token = null, string? ipAddress = null) =>
        _viewers.Values.Count(v => (token is null || v.Token == token) &&
                                   (ipAddress is null || v.IpAddress == ipAddress));

    /// <summary>Who is reading a link right now. A laptop that slept still counts until it times out.</summary>
    public List<ShareViewer> Viewers(string token)
    {
        var cutoff = DateTime.UtcNow - ViewerTimeout;
        foreach (var stale in _viewers.Values.Where(v => v.LastSeenUtc < cutoff))
            _viewers.TryRemove(stale.Id, out _);

        return [.. _viewers.Values.Where(v => v.Token == token).OrderBy(v => v.ConnectedUtc)];
    }

    // ---------------- plumbing ----------------

    private void Write(string token, ShareBundle bundle)
    {
        using var file = File.Create(PathFor(token));
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        JsonSerializer.Serialize(gzip, bundle, Json);
    }

    private void Delete(string token)
    {
        try
        {
            var path = PathFor(token);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete the shared comparison file for {Token}", token);
        }
    }

    private void Remember(string token, ShareBundle bundle)
    {
        lock (_gate)
        {
            _loaded[token] = (bundle, DateTime.UtcNow);

            while (_loaded.Count > MaxLoadedBundles)
            {
                var coldest = _loaded.OrderBy(e => e.Value.Touched).First().Key;
                _loaded.TryRemove(coldest, out _);
            }
        }
    }

    private string PathFor(string token) => Path.Combine(Folder, $"{token}.json.gz");

    /// <summary>128 bits of randomness. Guessing one is not a practical attack; losing the link is.</summary>
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    /// <summary>Tokens become file names, so nothing but our own alphabet is ever allowed near the disk.</summary>
    public static bool IsToken(string? token) => !string.IsNullOrEmpty(token) && TokenShape().IsMatch(token);

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex TokenShape();
}
