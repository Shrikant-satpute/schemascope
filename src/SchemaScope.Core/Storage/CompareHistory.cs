using SchemaScope.Core.Model;

namespace SchemaScope.Core.Storage;

/// <summary>
/// One database as it took part in a compare.
///
/// The name, server and database are copied in rather than looked up later, so
/// a history entry still reads properly after the saved connection behind it has
/// been renamed, pointed somewhere else, or deleted.
/// </summary>
public sealed class CompareHistoryDatabase
{
    /// <summary>The saved connection it came from. Null when an inline connection was used.</summary>
    public string? ConnectionId { get; set; }
    public string Label { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
}

/// <summary>
/// A compare that has been run: what was compared against what, and how it went.
///
/// Setting up the same comparison a second time is the most repetitive thing in
/// SchemaScope, so every finished run is remembered and can be loaded straight
/// back into the setup bar.
/// </summary>
public sealed class CompareHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>
    /// Source plus targets. Running the same pairing again updates the one row
    /// instead of pushing a near-identical copy on top of it.
    /// </summary>
    public string Signature { get; set; } = "";

    public DateTime FirstRunUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastRunUtc { get; set; } = DateTime.UtcNow;
    public int RunCount { get; set; } = 1;

    public CompareHistoryDatabase Source { get; set; } = new();
    public List<CompareHistoryDatabase> Targets { get; set; } = [];

    /// <summary>The options that run used, so loading it repeats what actually happened.</summary>
    public CompareOptions Options { get; set; } = new();

    public int ObjectCount { get; set; }
    public int DifferenceCount { get; set; }
    /// <summary>The worst target's match percentage - the headline of that run.</summary>
    public double LowestMatchPercent { get; set; }
    public long TotalMs { get; set; }

    /// <summary>
    /// Targets are sorted into the signature, so A against [B, C] and A against
    /// [C, B] are recognised as the same comparison.
    /// </summary>
    public static string BuildSignature(
        CompareHistoryDatabase source, IEnumerable<CompareHistoryDatabase> targets)
    {
        static string Key(CompareHistoryDatabase d)
        {
            var who = string.IsNullOrWhiteSpace(d.ConnectionId) ? d.Server : d.ConnectionId!;
            return $"{who.ToLowerInvariant()}/{d.Database.ToLowerInvariant()}";
        }

        var ordered = targets.Select(Key).OrderBy(k => k, StringComparer.Ordinal);
        return $"{Key(source)}=>{string.Join(",", ordered)}";
    }
}
