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
    /// Every object by its <see cref="DbObject.Key"/>. Keys ignore case, so two objects can share one - in a SQL Server
    /// database only when its collation is case-sensitive. Both are then kept, and a warning says so, rather than one
    /// silently replacing the other: every object under a shared key is indexed as "key#Schema.Name", its exact name, so
    /// dbo.USERS pairs with dbo.USERS and never with dbo.Users. <paramref name="shared"/> is the <see cref="SharedKeys"/>
    /// of every snapshot in the compare, so a side holding only one of the objects is keyed the same way; left out, it is
    /// this snapshot's own.
    /// </summary>
    public Dictionary<string, DbObject> BuildIndex(IReadOnlySet<string>? shared = null)
    {
        shared ??= SharedKeys([this]);
        var map = new Dictionary<string, DbObject>(Objects.Count, StringComparer.Ordinal);
        foreach (var o in Objects)
        {
            if (!shared.Contains(o.Key))
            {
                map[o.Key] = o;
                continue;
            }

            // one exact name twice (an engine with overloads): the later ones numbered
            var key = $"{o.Key}#{o.FullName}";
            var unique = key;
            for (var n = 2; !map.TryAdd(unique, o); n++) unique = $"{key}#{n}";
        }

        foreach (var group in Objects.GroupBy(o => o.Key, StringComparer.Ordinal).Where(g => g.Skip(1).Any()))
        {
            var first = group.First();
            var warning = $"{(string.IsNullOrWhiteSpace(Label) ? Database : Label)}: {group.Count()} {first.Type.Label()} objects " +
                          $"have one name when case is ignored ({string.Join(", ", group.Select(o => o.FullName))}); " +
                          "each is compared only with an object of exactly its name.";
            lock (Warnings)
            {
                if (!Warnings.Contains(warning)) Warnings.Add(warning);
            }
        }
        return map;
    }

    /// <summary>The keys that two or more objects share in any one of <paramref name="snapshots"/> (see <see cref="BuildIndex"/>).</summary>
    public static HashSet<string> SharedKeys(IEnumerable<SchemaSnapshot?> snapshots)
    {
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in snapshots)
        {
            if (s is null) continue;
            var seen = new HashSet<string>(s.Objects.Count, StringComparer.Ordinal);
            foreach (var o in s.Objects)
                if (!seen.Add(o.Key)) shared.Add(o.Key);
        }
        return shared;
    }
}
