namespace SchemaScope.Core.Model;

/// <summary>Everything SchemaScope knows about one database at one point in time.</summary>
public sealed class SchemaSnapshot
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>Friendly name shown in the UI, e.g. "Davi (prod)".</summary>
    public string Label { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    public string ProductVersion { get; set; } = "";
    public int MajorVersion { get; set; }
    public string Edition { get; set; } = "";
    public string Collation { get; set; } = "";
    public bool IsAzure { get; set; }

    public List<DbObject> Objects { get; set; } = [];

    /// <summary>
    /// Set when this database could not be read. The compare still runs for the
    /// other targets - one unreachable server never blocks the whole job.
    /// </summary>
    public string? Error { get; set; }
    public string? ErrorHint { get; set; }
    public bool Failed => !string.IsNullOrEmpty(Error);

    /// <summary>How long each stage took. Shown in the UI so slowness is never a mystery.</summary>
    public Dictionary<string, long> TimingsMs { get; set; } = [];

    /// <summary>Objects whose body could not be read (encrypted or no permission).</summary>
    public int UnreadableCount => Objects.Count(o => o.IsEncrypted || o.DefinitionUnavailable);

    public Dictionary<string, DbObject> BuildIndex()
    {
        var map = new Dictionary<string, DbObject>(Objects.Count, StringComparer.Ordinal);
        foreach (var o in Objects)
            map[o.Key] = o;
        return map;
    }
}
