using SchemaScope.Core.Engines;

namespace SchemaScope.Core.Model;

/// <summary>Everything SchemaScope knows about one database at one point in time.</summary>
public sealed class SchemaSnapshot
{
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;
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

    /// <summary>The engine's own version string, for engines that are not SQL Server (which uses <see cref="ProductVersion"/>).</summary>
    public string? EngineVersion { get; set; }

    /// <summary>Server settings that change how names compare, e.g. lower_case_table_names or server_version_num.</summary>
    public Dictionary<string, string>? EngineFacts { get; set; }

    /// <summary>Problems with the snapshot itself, e.g. two objects that share one key (see <see cref="BuildIndex"/>).</summary>
    public List<string> Warnings { get; set; } = [];

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

    /// <summary>Module bodies taken from the cache because modify_date had not moved.</summary>
    public int ReusedBodies { get; set; }

    /// <summary>Module bodies that had to be read from the server.</summary>
    public int FetchedBodies { get; set; }

    /// <summary>Objects whose body could not be read (encrypted or no permission).</summary>
    public int UnreadableCount => Objects.Count(o => o.IsEncrypted || o.DefinitionUnavailable);

    /// <summary>
    /// Every object by its <see cref="DbObject.Key"/>. Two objects with one key are both kept - the second as
    /// "key#2", the third "key#3" - and a warning says so, rather than one silently replacing the other. Keys
    /// ignore case, so a SQL Server database only ever has two when its collation is case-sensitive.
    /// </summary>
    public Dictionary<string, DbObject> BuildIndex()
    {
        var map = new Dictionary<string, DbObject>(Objects.Count, StringComparer.Ordinal);
        var shared = new Dictionary<string, (DbObject First, int Count)>(StringComparer.Ordinal);
        foreach (var o in Objects)
        {
            var key = o.Key;
            if (map.TryAdd(key, o)) continue;

            var n = shared.TryGetValue(key, out var seen) ? seen.Count + 1 : 2;
            shared[key] = (map[key], n);
            while (!map.TryAdd($"{key}#{n}", o)) n++;
        }

        foreach (var (first, count) in shared.Values)
        {
            var warning = $"{(string.IsNullOrWhiteSpace(Label) ? Database : Label)}: {count} {first.Type.Label()} objects are named " +
                          $"{first.FullName}; they are listed separately as {first.FullName}, {first.FullName}#2" +
                          $"{(count > 2 ? $" ... #{count}" : "")}.";
            lock (Warnings)
            {
                if (!Warnings.Contains(warning)) Warnings.Add(warning);
            }
        }
        return map;
    }
}
