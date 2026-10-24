using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using SchemaScope.Core.Engines;
using SchemaScope.Scripting;

namespace SchemaScope.Api.Services;

/// <summary>One section the apply dialog lists, in run order.</summary>
public sealed class PlanSection
{
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public int Files { get; set; }
    /// <summary>Ticked at first: the RUN.ps1 set only.</summary>
    public bool DefaultSelected { get; set; }
    /// <summary>normal | changesCode | destructive</summary>
    public string Risk { get; set; } = ApplyPlans.Normal;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Warning { get; set; }
}

/// <summary>Where an apply would go, read from the connection the compare really used.</summary>
public sealed class ApplyTarget
{
    public string Label { get; set; } = "";
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;
    public string Server { get; set; } = "";
    /// <summary>The port, for engines other than SQL Server (whose port is part of <see cref="Server"/>); null for SQL Server.</summary>
    public int? Port { get; set; }
    public string Database { get; set; } = "";
    /// <summary>"Windows: DOMAIN\user", "SQL login: name" or "PostgreSQL user: name".</summary>
    public string Login { get; set; } = "";
    public bool Protected { get; set; }

    /// <summary>
    /// When <see cref="Protected"/>: true when this connection itself is marked Protected, false when another
    /// Protected connection on the same host protects it. Null when not protected.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProtectedItself { get; set; }
}

/// <summary>A script file of a plan: what to run, and the SHA-256 it had when it was generated.</summary>
public sealed record PlanFile(string Folder, string RelativePath, string FullPath, string ObjectName, string Sha256);

/// <summary>A generated run that may be applied: bound to one compare run and one target, single use, short-lived.</summary>
public sealed class ApplyPlan
{
    public string Id { get; set; } = "";
    public string RunId { get; init; } = "";
    public int TargetIndex { get; init; }
    public DateTime ExpiresUtc { get; set; }
    /// <summary>In run order.</summary>
    public IReadOnlyList<PlanFile> Files { get; init; } = [];
    public IReadOnlyList<PlanSection> Sections { get; init; } = [];
}

/// <summary>The three plan fields both script answers carry.</summary>
public sealed record PlanOffer(string? PlanId, ApplyTarget? ApplyTarget, IReadOnlyList<PlanSection> PlanSections)
{
    public static PlanOffer None { get; } = new(null, null, []);
}

/// <summary>
/// Turns a generated run into an apply plan: the objects\*.sql files of each runnable folder, in the order RUN.ps1
/// runs them (plus the opt-in folders), each with the hash the writer took, so a file edited on disk afterwards is refused.
/// </summary>
public static class ApplyPlans
{
    public const string Normal = "normal";
    public const string ChangesCode = "changesCode";
    public const string Destructive = "destructive";

    /// <summary>The destructive folder (the generator writes it only with <see cref="ScriptOptions.IncludeDestructive"/>).</summary>
    public const string DestructiveFolder = "11_destructive";
    public const string DestructiveTitle = "Changes that can lose data";

    /// <summary>Every folder that can be applied, in run order. Nothing else (99_review, rollback\) ever runs.</summary>
    public static IReadOnlyList<string> RunOrder { get; } =
    [
        CoreFoldersGenerator.ColumnTypesFolder, CoreFoldersGenerator.ColumnsFolder, CoreFoldersGenerator.TableTypesFolder,
        CoreFoldersGenerator.MissingFolder, CoreFoldersGenerator.ConstraintsFolder, CoreFoldersGenerator.IndexesFolder,
        TriggersGenerator.Folder, FormattingGenerator.ModulesFolder, FormattingGenerator.TablesFolder, DifferentModulesGenerator.Folder,
        DestructiveFolder,
    ];

    private static readonly Dictionary<string, string> Warnings = new(StringComparer.OrdinalIgnoreCase)
    {
        [CoreFoldersGenerator.TableTypesFolder] =
            "Rebuilding a table type drops and re-creates every module that uses it; the deployed application's calls can break until its new release is out.",
        [TriggersGenerator.Folder] =
            "New triggers run on every insert, update or delete of their table from now on.",
        [FormattingGenerator.ModulesFolder] =
            "Re-creates code that differs only in formatting from the source's text.",
        [FormattingGenerator.TablesFolder] =
            "Renames objects and columns to the source's letter case; code that names them with the old case on a case-sensitive database breaks.",
        [DifferentModulesGenerator.Folder] =
            "Replaces code that is different in the target with the source's version; changes made only in the target are lost.",
        [DestructiveFolder] =
            "Can lose data: these statements drop or narrow what the target has. Apply only with a backup you have checked.",
    };

    /// <summary>The plan's files and sections from what the generator wrote.</summary>
    public static (List<PlanFile> Files, List<PlanSection> Sections) Build(IEnumerable<WrittenFile> written, IEnumerable<FolderResult> folders)
    {
        var titles = folders.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        int Order(string folder) => RunOrder.ToList().FindIndex(r => string.Equals(r, folder, StringComparison.OrdinalIgnoreCase));

        // run_all.ps1 runs objects\*.sql (not subfolders) by leading number, then name
        var runnable = written
            .Where(f => Order(f.Folder) >= 0 && f.IsObjectFile && !f.IsRollback &&
                        f.RelativePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                        f.RelativePath.Count(c => c == '\\') == 1)
            .OrderBy(f => Order(f.Folder))
            .ThenBy(f => NumberOf(f.RelativePath))   // "1000_x" after "999_x", not before "101_x"
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = runnable.Select(f => new PlanFile(
            RunOrder[Order(f.Folder)], f.RelativePath, f.FullPath,
            // a schema's file names the schema alone (its object is schema.""), not "name."
            f.Object is { } o ? (o.Name.Length == 0 ? o.Schema : o.ToString()) : Path.GetFileNameWithoutExtension(f.RelativePath),
            TrustedHash(f))).ToList();

        var sections = files.GroupBy(f => f.Folder).Select(g =>
        {
            var risk = RiskOf(g.Key);
            return new PlanSection
            {
                Folder = g.Key,
                Title = g.Key == DestructiveFolder ? DestructiveTitle : titles.GetValueOrDefault(g.Key, g.Key),
                Files = g.Count(),
                DefaultSelected = risk == Normal,
                Risk = risk,
                Warning = Warnings.GetValueOrDefault(g.Key),
            };
        }).ToList();
        return (files, sections);
    }

    /// <summary>The RUN.ps1 folders are normal; the opt-in folders change code; 11 can lose data.</summary>
    public static string RiskOf(string folder) =>
        folder == DestructiveFolder ? Destructive
        : ScriptGenerator.RunAllFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) ? Normal
        : ChangesCode;

    /// <summary>
    /// The hash the writer took from the bytes it wrote. A file whose disk content no longer matches it (changed while
    /// the run was still validating and writing reports) is refused here, so it never gets a plan.
    /// </summary>
    private static string TrustedHash(WrittenFile f)
    {
        if (f.Sha256 is not { } written)
            throw new IOException($"{f.Folder}\\{f.RelativePath} has no hash from when it was written.");
        if (!string.Equals(Hash(f.FullPath), written, StringComparison.Ordinal))
            throw new IOException($"{f.Folder}\\{f.RelativePath} was changed after it was written.");
        return written;
    }

    /// <summary>The file name's leading number ("objects\1000_x.sql" is 1000); files without one sort last.</summary>
    private static long NumberOf(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        var digits = name.TakeWhile(char.IsAsciiDigit).Count();
        return digits is > 0 and <= 18 ? long.Parse(name.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture) : long.MaxValue;
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>
/// Plans waiting to be applied, in memory only. A plan id is 128 random bits, belongs to one compare run and one
/// target, lives <see cref="Ttl"/>, and is taken (removed) by the apply that uses it.
/// </summary>
public sealed class ApplyPlanStore(TimeProvider? clock = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(2);
    private const int MaxPlans = 50;

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ApplyPlan> plans = new(StringComparer.Ordinal);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public ApplyPlan Add(ApplyPlan plan)
    {
        plan.Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        plan.ExpiresUtc = Now + Ttl;
        foreach (var old in plans.Values.Where(p => p.ExpiresUtc <= Now)) plans.TryRemove(old.Id, out _);
        foreach (var old in plans.Values.OrderBy(p => p.ExpiresUtc).Take(Math.Max(0, plans.Count - MaxPlans + 1)))
            plans.TryRemove(old.Id, out _);
        plans[plan.Id] = plan;
        return plan;
    }

    /// <summary>The live plan with this id, or null (unknown or expired).</summary>
    public ApplyPlan? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || !plans.TryGetValue(id, out var plan)) return null;
        if (plan.ExpiresUtc > Now) return plan;
        plans.TryRemove(id, out _);
        return null;
    }

    /// <summary>Removes the plan: it is applied at most once.</summary>
    public bool Take(ApplyPlan plan) => plans.TryRemove(new KeyValuePair<string, ApplyPlan>(plan.Id, plan));
}
