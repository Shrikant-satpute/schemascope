using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SchemaScope.Core.Storage;

/// <summary>One module body as it was last seen, plus the stamps that prove it is still current.</summary>
public sealed class CachedDefinition
{
    /// <summary>sys.objects.modify_date. A body cannot change without this changing.</summary>
    public long ModifyTicks { get; set; }

    /// <summary>schema.name, so a recycled object_id can never hand back the wrong body.</summary>
    public string Name { get; set; } = "";

    public string Definition { get; set; } = "";
}

/// <summary>Everything remembered about one database's module bodies.</summary>
public sealed class DefinitionCacheFile
{
    public int Version { get; set; } = DefinitionCache.FormatVersion;
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public DateTime WrittenUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<int, CachedDefinition> Modules { get; set; } = [];
}

/// <summary>
/// Procedure and view bodies, kept between runs.
///
/// Reading every body out of <c>sys.sql_modules</c> is by far the most expensive
/// thing SchemaScope asks a server to do - it is the one query that touches LOB
/// pages, and on a server whose buffer pool is under pressure it is the whole
/// cost of a compare. Almost none of those bodies change between two runs.
///
/// So we keep them, keyed on the only thing that can prove a body is unchanged:
/// <c>sys.objects.modify_date</c>, which SQL Server moves on every ALTER. The
/// object's schema.name is stored alongside it, so a dropped-and-recreated object
/// that happens to land on the same object_id can never be mistaken for the old
/// one. Anything that fails either check is read from the server as before.
///
/// A cache that cannot be read or written is not an error - it just means the
/// next run pays full price. Correctness never depends on it.
/// </summary>
public sealed class DefinitionCache
{
    internal const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _folder;
    private readonly Lock _gate = new();

    public DefinitionCache(string? folder = null) => _folder = folder ?? DefaultFolder();

    public static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SchemaScope",
        "cache");

    /// <summary>What we remember about this database. Never null, never throws.</summary>
    public DefinitionCacheFile Load(string server, string database)
    {
        var empty = new DefinitionCacheFile { Server = server, Database = database };

        try
        {
            var path = PathFor(server, database);
            if (!File.Exists(path)) return empty;

            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var loaded = JsonSerializer.Deserialize<DefinitionCacheFile>(gzip, Json);

            // A file written by an older shape of this class is not worth guessing at.
            if (loaded is null || loaded.Version != FormatVersion) return empty;

            loaded.Server = server;
            loaded.Database = database;
            return loaded;
        }
        catch
        {
            return empty;
        }
    }

    /// <summary>
    /// Writes the bodies back. Written to a temporary file and moved into place,
    /// so a crash halfway through leaves the previous cache intact rather than a
    /// truncated one that would have to be thrown away.
    /// </summary>
    public void Save(DefinitionCacheFile file)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var path = PathFor(file.Server, file.Database);
            var temp = path + ".tmp";

            file.Version = FormatVersion;
            file.WrittenUtc = DateTime.UtcNow;

            lock (_gate)
            {
                using (var stream = File.Create(temp))
                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                {
                    JsonSerializer.Serialize(gzip, file, Json);
                }

                File.Move(temp, path, overwrite: true);
            }
        }
        catch
        {
            // Losing the cache costs time on the next run and nothing else.
        }
    }

    /// <summary>Forgets everything. The next compare reads every body from the server.</summary>
    public void Clear()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            lock (_gate)
            {
                foreach (var f in Directory.EnumerateFiles(_folder, "*.json.gz")) File.Delete(f);
            }
        }
        catch
        {
            // Nothing to do about it, and nothing depends on it.
        }
    }

    /// <summary>
    /// A server name can hold a port, a backslash and an IP address, none of which
    /// belong in a file name - so the identity is hashed rather than spelled out.
    /// </summary>
    private string PathFor(string server, string database)
    {
        var identity = $"{server.ToLowerInvariant()}|{database.ToLowerInvariant()}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(_folder, $"{hash[..32]}.json.gz");
    }
}
