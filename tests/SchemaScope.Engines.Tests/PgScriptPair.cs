using Npgsql;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.Scripting;
using SchemaScope.Scripting.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>
/// A source and a target database on one test container, for "Create scripts" scenarios: compare, generate, run the
/// files with psql inside the container (every file, failures collected rather than stopping at the first), compare
/// again. Both databases and the output folder go when it is disposed.
/// </summary>
public sealed class PgScriptPair(PgServer pg, string src, string tgt, string output) : IAsyncDisposable
{
    public static readonly ScriptSection[] Every =
        [ScriptSection.NewColumns, ScriptSection.TableTypes, ScriptSection.MissingObjects, ScriptSection.ColumnTypes, ScriptSection.Constraints,
         ScriptSection.Indexes, ScriptSection.Triggers, ScriptSection.Formatting, ScriptSection.DifferentModules];

    /// <summary>RUN.ps1's folders, then the two it leaves for a person to run.</summary>
    public static readonly string[] RunOrder = [.. PgScriptGenerator.RunAllFolders, PgScriptGenerator.TriggersFolder, PgScriptGenerator.DifferentFolder];

    public PgServer Pg { get; } = pg;
    public string Src { get; } = src;
    public string Tgt { get; } = tgt;

    public static async Task<PgScriptPair> CreateAsync(PgServer pg, string sourceSql, string targetSql)
    {
        var pair = new PgScriptPair(pg, await pg.CreateDatabaseAsync("src"), await pg.CreateDatabaseAsync("tgt"),
            Path.Combine(Path.GetTempPath(), $"schemascope-pg-scripts-{Guid.NewGuid():n}"));
        await pg.ExecAsync(pair.Src, sourceSql);
        await pg.ExecAsync(pair.Tgt, targetSql);
        return pair;
    }

    public Task<CompareRun> CompareAsync(CompareOptions? options = null) =>
        new CompareRunner(_ => new PgSchemaReader()).RunAsync(Pg.Source("source", Src), [Pg.Source("t0", Tgt)], options ?? new CompareOptions());

    public async Task<ScriptOutcome> GenerateAsync(CompareRun before, IEnumerable<ScriptSection>? sections = null, CompareOptions? compare = null,
        bool destructive = false)
    {
        Assert.False(before.Targets[0].Failed, before.Targets[0].Error);
        var options = new ScriptOptions { Sections = [.. sections ?? Every], OutputFolder = output, IncludeDestructive = destructive };
        var source = new ScriptEndpoint("Source", Pg.Host, Src, Pg.ConnectionString(Src), DatabaseEngine.PostgreSql);
        var target = new ScriptEndpoint("Target", Pg.Host, Tgt, Pg.ConnectionString(Tgt), DatabaseEngine.PostgreSql);
        var outcome = await new PgScriptGenerator().GenerateAsync(new ScriptRequestContext(source, target, before.Source, before.Targets[0],
            before.Result, 0, options, new HashSet<string>(), new Dictionary<string, LineChange>(), compare ?? new CompareOptions()), default, null);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(outcome.Validation.Passed, string.Join("\n", outcome.Validation.Problems));
        Assert.All(outcome.HeldBack, h => Assert.False(string.IsNullOrWhiteSpace(h.Reason), h.Object));
        return outcome;
    }

    /// <summary>The object files of one folder, in the order its run_all.ps1 runs them.</summary>
    public static List<WrittenFile> FilesOf(ScriptOutcome outcome, string folder) =>
        [.. outcome.Files.Where(f => f.Folder == folder && f.IsObjectFile && f.RelativePath.Count(c => c == '\\') == 1)
            .OrderBy(f => int.Parse(FileName(f)[..3]))];

    /// <summary>The file's own name. RelativePath always uses '\' (the app is Windows-only), which Path.GetFileName does
    /// not split on Linux, where CI runs these tests.</summary>
    public static string FileName(WrittenFile f) => f.RelativePath[(f.RelativePath.LastIndexOf('\\') + 1)..];

    /// <summary>Text of a folder's object files, in order.</summary>
    public static string Folder(ScriptOutcome outcome, string folder) =>
        string.Join("\n", FilesOf(outcome, folder).Select(f => File.ReadAllText(f.FullPath)));

    /// <summary>One file, the way run_all.ps1 runs it: psql, ON_ERROR_STOP, one transaction. The error text, or null.</summary>
    public async Task<string?> RunFileAsync(ScriptOutcome outcome, WrittenFile f)
    {
        var inContainer = $"/tmp/{Path.GetFileName(outcome.Path!)}/{f.Folder}/{FileName(f)}";
        await Pg.CopyToContainerAsync(await File.ReadAllBytesAsync(f.FullPath), inContainer);
        var (code, stdout, stderr) = await Pg.ExecInContainerAsync("psql", "-X", "-v", "ON_ERROR_STOP=1", "--single-transaction",
            "-U", PgServer.User, "-d", Tgt, "-f", inContainer);
        return code == 0 ? null : $"{f.Folder}\\{f.RelativePath} failed ({code}): {stderr.Trim()} {stdout.Trim()}";
    }

    /// <summary>Every file of <paramref name="folders"/> in run order; each failure is collected and the run goes on.</summary>
    public async Task<List<string>> RunAsync(ScriptOutcome outcome, IEnumerable<string>? folders = null)
    {
        var failures = new List<string>();
        foreach (var folder in folders ?? RunOrder)
            foreach (var f in FilesOf(outcome, folder))
                if (await RunFileAsync(outcome, f) is { } failure) failures.Add(failure);
        return failures;
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Pg.ConnectionString(Tgt));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>Rows that are neither Same nor only in the target, as "Type schema.name(signature)".</summary>
    public static List<string> NotSame(CompareRun run) =>
        [.. run.Result.Rows.Where(r => r.Cells[0].Status is not (ObjectStatus.Same or ObjectStatus.OnlyInTarget))
            .Select(r => $"{r.TypeLabel} {r.FullName}{(r.Signature is { } s ? $" {s}" : "")}: {r.Cells[0].Status}").Order(StringComparer.Ordinal)];

    public async ValueTask DisposeAsync()
    {
        await Pg.DropAsync(Src);
        await Pg.DropAsync(Tgt);
        try { Directory.Delete(output, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
