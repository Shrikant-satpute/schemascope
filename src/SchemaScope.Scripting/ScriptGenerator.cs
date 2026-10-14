using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SchemaScope.Scripting;

/// <summary>
/// Runs a whole "Create scripts" job (DESIGN.md §4): reads both catalogs, runs the folder
/// generators in a fixed order, validates the output offline, and writes RUN.ps1 and README.md.
///
/// Read-only toward every database: the only statements sent are catalog SELECTs and the
/// read-only probes in <see cref="TargetProbes"/>/<see cref="SourceProbes"/>. Nothing generated is
/// ever sent to a server. Output goes to a fresh, uniquely named subfolder; nothing is deleted.
/// </summary>
public sealed class ScriptGenerator
{
    /// <summary>
    /// Folders RUN.ps1 may run by default, in run order. 04 runs first: it only changes columns the target
    /// already has and needs nothing from 01 or 03, while a computed column 01 adds or a schema-bound view 03
    /// creates over such a column would make its ALTER COLUMN fail (Msg 5074) - 04's dependency check only sees
    /// the target as it is before anything runs. Then 01 before 03 (new code reads new columns), then 05, 06.
    /// </summary>
    public static IReadOnlyList<string> RunAllFolders { get; } =
        ["04_columntypes", "01_columns", "03_missing", "05_constraints", "06_indexes"];

    /// <summary>The run order with <c>-WithTableTypes</c>: 02 after 01, before 03 (03's new modules may take the new types).</summary>
    public static IReadOnlyList<string> RunAllFoldersWithTableTypes { get; } =
        ["04_columntypes", "01_columns", "02_tabletypes", "03_missing", "05_constraints", "06_indexes"];

    /// <summary>The opt-in folder RUN.ps1 adds with -WithTableTypes.</summary>
    public const string TableTypesFolder = "02_tabletypes";

    /// <summary>
    /// The first note of every selected folder that had nothing to script (01–09 alike). Such a folder is
    /// not written at all - no directory, no _ALL.sql, no run_all.ps1 - and is listed with 0 files.
    /// </summary>
    public const string NothingToDoNote = "Nothing to do - no folder was written.";

    /// <summary>The catalog reader (replaceable for tests).</summary>
    public ScriptCatalogReader Reader { get; init; } = new();

    /// <summary>Generates the scripts that make <paramref name="target"/> look like <paramref name="source"/>.</summary>
    /// <param name="formattingCandidates">"schema.name" of modules SchemaScope's compare marked FormattingOnly for this target.</param>
    /// <param name="progress">Told each step as the run reaches it; the percentages follow measured run times.</param>
    /// <param name="differentCandidates">"schema.name" of modules the compare marked Different for this target, with its line counts (10).</param>
    public async Task<ScriptOutcome> GenerateAsync(
        ScriptEndpoint source, ScriptEndpoint target, ScriptOptions options,
        IReadOnlySet<string> formattingCandidates, CancellationToken ct,
        IProgress<ScriptProgress>? progress = null,
        IReadOnlyDictionary<string, LineChange>? differentCandidates = null)
    {
        void Report(int percent, string step) => progress?.Report(new ScriptProgress(percent, step));
        var sw = Stopwatch.StartNew();
        var outcome = new ScriptOutcome();
        ScriptWriter? writer = null;
        try
        {
            var problem = Check(options);
            if (problem is not null) return Fail(outcome, problem, sw);

            var review = new ReviewLog();

            // 2. both catalogs at once - before any folder is created, so a failed connection leaves nothing behind
            // Reading is about half the run, so the bar moves with each finished catalog query, not just once.
            const int totalQueries = 2 * ScriptCatalogReader.QueryCount;
            var queriesDone = 0;
            void QueryDone()
            {
                var n = Math.Min(Interlocked.Increment(ref queriesDone), totalQueries);
                Report(P.Read + (P.Prereqs - P.Read) * n / totalQueries, $"Reading both databases ({n} of {totalQueries} catalog queries)");
            }
            Report(P.Read, $"Reading both databases (0 of {totalQueries} catalog queries)");
            var srcTask = ReadSideAsync(source, "source", QueryDone, ct);
            var tgtTask = ReadSideAsync(target, "target", QueryDone, ct);
            await Task.WhenAll(srcTask, tgtTask);
            var src = srcTask.Result;
            var tgt = tgtTask.Result;
            foreach (var (ep, schema) in new[] { (source, src), (target, tgt) })
                foreach (var (m, was) in schema.FixRenamedHeaders())
                    review.Note(ReviewLog.RenamedHeaders, "module", m.ToString(), null,
                        $"{ep.Label}'s text says CREATE ... {was} (renamed with sp_rename since) - the scripts use {m.Bracketed}");
            if (review.ItemsFor(ReviewLog.RenamedHeaders).Count > 0)
                review.Describe(ReviewLog.RenamedHeaders, "Module headers rewritten to the catalog name",
                    "sp_rename changes a module's name but not its stored text, so replaying the text would create the old name. " +
                    "Every script that writes one of these modules has only the name in its CREATE header changed.");
            var rawTarget = tgt.Clone();

            writer = new ScriptWriter(NewRunRoot(options.OutputFolder, target.Label));
            outcome.Path = writer.Root;

            if (src.IsCaseInsensitive && tgt.IsCaseInsensitive)
            {
                foreach (var r in tgt.AlignCase(src))
                    review.Warn($"same object, different case: {target.Label} {r.Old} = {source.Label} {r.New}");
            }
            else
            {
                review.Warn($"A database uses a case-sensitive collation ({source.Label}: {src.Collation}, " +
                            $"{target.Label}: {tgt.Collation}); names were matched exactly, not ignoring case.");
            }

            var ctx = new ScriptContext
            {
                SourceEndpoint = source,
                TargetEndpoint = target,
                Source = src,
                Target = tgt,
                RawTarget = rawTarget,
                Probes = new TargetProbes(target, options),
                SourceProbes = new SourceProbes(source, options),
                Options = options,
                Writer = writer,
                Review = review,
                FormattingCandidates = new HashSet<string>(formattingCandidates, StringComparer.OrdinalIgnoreCase),
                DifferentCandidates = new Dictionary<string, LineChange>(
                    differentCandidates ?? new Dictionary<string, LineChange>(), StringComparer.OrdinalIgnoreCase),
                Ct = ct,
                Progress = progress,
            };

            // 3. generators, fixed order
            var folders = new List<FolderResult>();
            folders.AddRange(await new CoreFoldersGenerator().GenerateAsync(ctx));
            ct.ThrowIfCancellationRequested();
            if (ctx.Selected(ScriptSection.Triggers)) Report(P.Triggers, "07 Triggers");
            if (ctx.Selected(ScriptSection.Triggers)) folders.AddRange(await new TriggersGenerator().GenerateAsync(ctx));
            ct.ThrowIfCancellationRequested();
            if (ctx.Selected(ScriptSection.Formatting)) Report(P.Formatting, "08-09 Formatting");
            if (ctx.Selected(ScriptSection.Formatting)) folders.AddRange(await new FormattingGenerator().GenerateAsync(ctx));
            ct.ThrowIfCancellationRequested();
            if (ctx.Selected(ScriptSection.DifferentModules)) Report(P.Different, "10 Different code");
            if (ctx.Selected(ScriptSection.DifferentModules)) folders.AddRange(await new DifferentModulesGenerator().GenerateAsync(ctx));
            ct.ThrowIfCancellationRequested();
            Report(P.Review, "99_review reports");
            folders.AddRange(await new ReviewReports().GenerateAsync(ctx));
            ct.ThrowIfCancellationRequested();
            folders = folders.OrderBy(f => f.Folder, StringComparer.Ordinal).ToList();

            Report(P.Validate, "Checking the scripts offline");
            outcome.Validation = await new ScriptValidator().ValidateAsync(ctx);
            ct.ThrowIfCancellationRequested();

            Report(P.Finish, "Writing RUN.ps1 and README.md");
            foreach (var n in ctx.Probes.NotChecked.Concat(ctx.SourceProbes.NotChecked)) review.Warn(n);
            review.WriteAll(writer);

            // 4. root runner + README
            // only folders that were written: a folder with nothing to do has no run_all.ps1 to call
            // in RunAllFolders' order, not the folders' name order
            bool Written(FolderResult f) => writer.FilesIn(f.Folder).Count > 0;
            bool Runs(string folder) => folders.Any(f => string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
                                                         (f.InRunAll || string.Equals(folder, TableTypesFolder, StringComparison.OrdinalIgnoreCase)) && Written(f));
            var runAll = RunAllFolders.Where(Runs).ToList();
            var withTypes = Runs(TableTypesFolder) ? TableTypesFolder : null;
            var runWithTypes = withTypes is null ? null : RunAllFoldersWithTableTypes.Where(Runs).ToList();
            writer.WriteRootFile("RUN.ps1", ScriptText.RootRunner(RunnerServer(target), runAll, runWithTypes), bom: true, WrittenFileKind.Runner);

            outcome.Folders = folders;
            outcome.HeldBack = review.ToHeldBack();
            outcome.Warnings = review.Warnings.ToList();
            if (!outcome.Validation.Passed)
                outcome.Warnings.Add("Validation found problems - read 99_review\\validation.md before running anything.");

            writer.WriteMarkdown("", "README.md", Readme(ctx, folders, runAll, withTypes, outcome));

            outcome.FileCount = writer.Files.Count;
            outcome.Success = true;
            outcome.Message = $"Wrote {outcome.FileCount} files to {writer.Root}.";
            Report(100, "Done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome.Success = false;
            outcome.Message = "Cancelled." + PartialNote(writer, "it was cancelled");
        }
        catch (ScriptReadException ex)
        {
            outcome.Success = false;
            outcome.Message = ex.Message + PartialNote(writer, ex.Message);
        }
        catch (SqlException ex)
        {
            outcome.Success = false;
            outcome.Message = $"SQL Server error {ex.Number}: {ex.Message}" + Hint(ex);
            outcome.Message += PartialNote(writer, outcome.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.Success = false;
            outcome.Message = $"Could not write the scripts: {ex.Message}";
            outcome.Message += PartialNote(writer, outcome.Message);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.Message = $"Script generation failed: {ex.Message}";
            outcome.Message += PartialNote(writer, outcome.Message);
        }
        finally
        {
            if (writer is not null) outcome.FileCount = writer.Files.Count;
            outcome.ElapsedMs = sw.ElapsedMilliseconds;
        }
        return outcome;
    }

    /// <summary>
    /// Where each step starts on the progress bar (0-100). Set from measured runs (Platform_Core_3 against
    /// Platform_Core_2, everything ticked), so the bar moves at a steady pace rather than by step count.
    /// </summary>
    internal static class P
    {
        public const int Read = 1;
        public const int Prereqs = 52;
        public const int Columns = 53;
        public const int Missing = 54;
        public const int TableTypes = 65;
        public const int Constraints = 67;
        public const int Indexes = 68;
        public const int ColumnTypes = 69;
        public const int Write = 70;
        public const int Triggers = 76;
        public const int Formatting = 77;
        public const int Different = 84;
        public const int Review = 86;
        public const int Validate = 93;
        public const int Finish = 99;
    }

    /// <summary>The default <c>$Server</c> for every runner: the target's server, or localhost if unknown.</summary>
    public static string RunnerServer(ScriptEndpoint target) =>
        string.IsNullOrWhiteSpace(target.Server) ? "localhost" : target.Server;

    private static string? Check(ScriptOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.OutputFolder) || !Path.IsPathFullyQualified(o.OutputFolder))
            return "Choose a full output folder path, e.g. D:\\Scripts.";
        if (o.Sections.Count == 0) return "Tick at least one section.";
        if (!string.IsNullOrWhiteSpace(o.QuarantineFolder) && !Directory.Exists(o.QuarantineFolder))
            return $"The quarantine folder does not exist: {o.QuarantineFolder}";
        return null;
    }

    private static readonly object RunRootLock = new();

    /// <summary>
    /// <c>OutputFolder\Safe(label_yyyyMMdd-HHmm)</c>, with _2, _3 ... appended while that name is taken.
    /// Directory.CreateDirectory does not fail on an existing folder, so the test and the create must be
    /// one step: two runs for the same target in the same minute (double click, two tabs) would otherwise
    /// both get the same folder and the second would overwrite the first. A process-wide lock covers the
    /// API host; a named mutex covers a second SchemaScope process on the same machine.
    /// </summary>
    private static string NewRunRoot(string outputFolder, string label)
    {
        Directory.CreateDirectory(outputFolder);
        var baseName = ScriptText.Safe($"{label}_{DateTime.Now:yyyyMMdd-HHmm}");
        lock (RunRootLock)
        {
            using var mutex = new Mutex(false, @"Local\SchemaScope.Scripting.RunRoot");
            var owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { owned = true; }
                var candidate = Path.Combine(outputFolder, baseName);
                for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
                    candidate = Path.Combine(outputFolder, $"{baseName}_{n}");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            finally
            {
                if (owned) mutex.ReleaseMutex();
            }
        }
    }

    private async Task<ScriptSchema> ReadSideAsync(ScriptEndpoint ep, string side, Action queryDone, CancellationToken ct)
    {
        try
        {
            return await Reader.ReadAsync(ep, ct, queryDone);
        }
        catch (SqlException ex)
        {
            throw new ScriptReadException($"Could not read the {side} ({ep.Label}): {ex.Message}{Hint(ex)}", ex);
        }
    }

    private static string Hint(SqlException ex) => ex.Number switch
    {
        4060 or 18456 => " Check the login and that it can open this database.",
        53 or 40615 => " Check the server name and that it is reachable from this machine.",
        -2 => " The server took too long to answer. Try again.",
        229 or 300 => " The login needs VIEW DEFINITION on this database.",
        -2146893019 => " The server certificate is not trusted. Tick 'Trust server certificate' on the connection.",
        _ => ""
    };

    /// <summary>The file a run that stopped early leaves at its root, so nobody runs a half-written folder.</summary>
    public const string IncompleteMarker = "INCOMPLETE - DO NOT RUN.txt";

    /// <summary>
    /// For a run that stopped after its folder was created: writes <see cref="IncompleteMarker"/> at the run root
    /// (folders 01-06 may be complete on disk, with their run_all.ps1, although nothing was validated and RUN.ps1,
    /// README.md and 99_review are missing), and returns the sentence for the message - or "" when no script was written.
    /// </summary>
    private static string PartialNote(ScriptWriter? w, string why)
    {
        if (w is null) return "";
        try
        {
            File.WriteAllText(Path.Combine(w.Root, IncompleteMarker),
                $"This folder is incomplete: {why.ReplaceLineEndings(" ")}\r\n\r\n" +
                "SchemaScope stopped before it finished writing and checking these scripts. There is no RUN.ps1, README.md or " +
                "99_review, and a folder in here may be cut short or depend on one that was never written. " +
                "Do not run anything from this folder - generate the scripts again.\r\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the message below still says not to run it
        }
        return w.Files.Count == 0
            ? ""
            : $" Partial output ({w.Files.Count} files) is in {w.Root}; it is marked incomplete - do not run it.";
    }

    private static ScriptOutcome Fail(ScriptOutcome o, string message, Stopwatch sw)
    {
        o.Success = false;
        o.Message = message;
        o.ElapsedMs = sw.ElapsedMilliseconds;
        return o;
    }

    // ------------------------------------------------------------------ README

    private static readonly Dictionary<string, string> FolderText = new(StringComparer.OrdinalIgnoreCase)
    {
        ["01_columns"] = "Adds columns the source has on tables both sides have. NOT NULL columns with no default are not added (they cannot be added to a table that has rows) - see 99_review.",
        ["02_tabletypes"] = "Rebuilds table types whose columns differ. It drops and recreates the target modules that use them, from the TARGET's own text. Opt-in: the deployed application sends table-valued parameters with the OLD column count and SqlClient refuses a mismatch, so this belongs with the new application release.",
        ["03_missing"] = "Creates the table types, tables, functions, views and procedures the target lacks, and the schemas, alias types, sequences and synonyms it lacks. Module text is copied byte for byte from the source. No triggers (see 07).",
        ["04_columntypes"] = "Widens columns (bigger size, int to bigint, varchar to nvarchar) and relaxes NOT NULL to NULL where the source does. Never narrows, never tightens.",
        ["05_constraints"] = "Adds defaults, checks, unique/primary keys and foreign keys the target lacks. Anything that would clash with an existing rule or with existing data is held back in 99_review.",
        ["06_indexes"] = "Adds indexes the target lacks. Unique indexes that existing data would break are held back.",
        ["07_triggers"] = "Creates source triggers the target lacks, least-busy table first. A trigger changes every write to its table, so this folder is never run by RUN.ps1.",
        ["08_formatting_modules"] = "Replaces modules that differ only in formatting with the source text (DROP + CREATE in one transaction). Replaces existing code.",
        ["09_formatting_tables"] = "Renames tables, columns, constraints and indexes whose names differ only in letter case (sp_rename; no data moves).",
        ["10_different_modules"] = "Replaces procedures, views, functions and triggers whose code really differs with the source text (DROP + CREATE in one transaction). Replaces the target's own code, which may hold a fix made only there - read 99_review\\different_modules.md first.",
        ["99_review"] = "Reports only - no runnable scripts. Everything held back, and why.",
    };

    private static string Readme(ScriptContext ctx, List<FolderResult> folders, List<string> runAll, string? withTypes, ScriptOutcome outcome)
    {
        var src = ctx.SourceEndpoint;
        var tgt = ctx.TargetEndpoint;
        var sb = new StringBuilder();
        sb.Append($"# Scripts to make {tgt.Label} look like {src.Label}\n\n");
        sb.Append($"Generated by SchemaScope on {DateTime.Now:yyyy-MM-dd HH:mm}.\n\n");
        sb.Append($"- Source: **{src.Label}** ({src.Server} / {src.Database}, collation {ctx.Source.Collation})\n");
        sb.Append($"- Target: **{tgt.Label}** ({tgt.Server} / {tgt.Database}, collation {ctx.Target.Collation})\n\n");
        sb.Append("> **Run on a rehearsal copy of the target first.** Restore a backup of the target under another name, ");
        sb.Append("run the scripts there, test the application against it, then re-run the SchemaScope compare. ");
        sb.Append("Only then run them on the real target, in a maintenance window.\n\n");

        sb.Append("## How to run\n\n");
        sb.Append("```powershell\n");
        sb.Append($"# -Server defaults to {RunnerServer(tgt)}\n");
        sb.Append("powershell -ExecutionPolicy Bypass -File .\\RUN.ps1 -Database <TargetDatabase>\n");
        if (withTypes is not null)
            sb.Append("# also runs 02_tabletypes - only together with the new application release (see below)\n")
              .Append("powershell -ExecutionPolicy Bypass -File .\\RUN.ps1 -Database <TargetDatabase> -WithTableTypes\n");
        sb.Append("```\n\n");
        sb.Append("`-ExecutionPolicy Bypass -File` is needed because Windows blocks downloaded and unsigned scripts by default; ");
        sb.Append("it covers the folders' `run_all.ps1` files that RUN.ps1 calls. Typing `.\\RUN.ps1` directly only works if your execution policy already allows it.\n\n");
        sb.Append(runAll.Count == 0
            ? "RUN.ps1 runs no folder by default for this selection; run the folders below one by one.\n\n"
            : $"RUN.ps1 runs, in this order: {string.Join(", ", runAll)}. Each folder's `run_all.ps1` runs its `objects\\*.sql` files with sqlcmd (Windows login, `-x` so `$(...)` in code is left alone) and reports failures.\n\n");
        sb.Append("Each folder also has `_ALL.sql`, the same scripts in one file for SSMS. Module bodies in `_ALL.sql` are built ");
        sb.Append("inside the batch so SSMS cannot change their line endings: the stored definition comes out byte for byte the same as the source's.\n\n");
        sb.Append("Run `_ALL.sql` in SSMS with **SQLCMD mode off**, or with `sqlcmd -x`. In SQLCMD mode, or with sqlcmd without `-x`, ");
        sb.Append("a `$(name)` inside module code is treated as a sqlcmd variable and the batch fails or changes.\n\n");

        sb.Append("## Folders\n\n");
        sb.Append("| Folder | What it does | Files | In RUN.ps1 | Rollback |\n|---|---|---:|---|---|\n");
        foreach (var f in folders)
        {
            var what = FolderText.TryGetValue(f.Folder, out var t) ? t : f.Title;
            if (f.Notes.Count > 0) what += " " + string.Join(" ", f.Notes);
            sb.Append($"| {f.Folder} | {ReviewLog.Cell(what)} | {f.Files} | {(f.InRunAll ? "yes" : "no")} | {(f.HasRollback ? "yes" : "-")} |\n");
        }
        sb.Append($"| 99_review | {FolderText["99_review"]} | - | no | - |\n\n");

        sb.Append("## Run order rules\n\n");
        sb.Append("- **04 first**: it only changes columns the target already has. Run before 01 and 03, so a computed column or schema-bound view they add cannot pin a column it widens, and before 05 and 06, so a new key or index cannot either (Msg 5074).\n");
        sb.Append("- **01 before 03**: new procedures read the new columns.\n");
        sb.Append("- **02 is opt-in** (`-WithTableTypes`, after 01 and before 03): it breaks the currently deployed application's table-valued parameters until the application is updated.\n");
        sb.Append("- **07, 08, 09 and 10 are never run by RUN.ps1.** Run each folder's `run_all.ps1` (or `_ALL.sql`) on its own when you have decided to.\n");
        sb.Append("- **10 after RUN.ps1**: the replaced code may read the columns and objects 01 and 03 add. Each file is one transaction, so a file that fails leaves the old version in place.\n\n");

        sb.Append("## Undo\n\n");
        bool Has(string folder) => folders.Any(f => f.Files > 0 && string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase));
        var withRollback = folders.Where(f => f.HasRollback).Select(f => f.Folder).ToList();
        if (withRollback.Count > 0)
        {
            sb.Append($"- {string.Join(", ", withRollback)} {(withRollback.Count == 1 ? "has" : "have")} a `rollback\\` folder: ");
            sb.Append("the target's current text byte for byte, or the DROP of what was created. Run that folder's `rollback\\_ALL.sql`");
            sb.Append(withRollback.Contains("09_formatting_tables") ? " (09's is already in reverse order).\n" : ".\n");
            if (withRollback.Contains(TableTypesFolder))
                sb.Append("- 02_tabletypes' rollback drops the modules it recreated, puts the target's original table types back and recreates " +
                          "those modules from the target's text again. It stops before changing anything while another module uses a rebuilt " +
                          "type (for example one 03_missing created) - drop or change that module first.\n");
        }
        var additive = folders.Where(f => f.Files > 0).Select(f => f.Folder)
            .Where(f => f is "01_columns" or "03_missing" or "05_constraints" or "06_indexes").ToList();
        if (additive.Count > 0)
            sb.Append($"- {string.Join(", ", additive)} only add objects; to undo them, drop what was added or restore the backup.\n");
        if (Has("04_columntypes"))
            sb.Append("- 04_columntypes widens columns; narrowing back can lose data, so undo it by restoring the backup.\n");
        sb.Append(Has(TableTypesFolder)
            ? "- Nothing in these scripts drops an object that only the target has, except 02_tabletypes: it drops and recreates the target's own modules that use a changed table type (recreated from their current text).\n\n"
            : "- Nothing in these scripts drops an object that only the target has.\n\n");

        sb.Append("## Held back\n\n");
        if (outcome.HeldBack.Count == 0) sb.Append("Nothing was held back.\n\n");
        else
        {
            sb.Append($"{outcome.HeldBack.Count} items were not scripted because each needs a decision. Details are in `99_review\\`.\n\n");
            sb.Append("| Would have gone to | Items |\n|---|---:|\n");
            foreach (var g in outcome.HeldBack.GroupBy(h => h.Section).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.Append($"| {g.Key} | {g.Count()} |\n");
            sb.Append('\n');
        }

        var v = outcome.Validation;
        sb.Append("## Validation\n\n");
        sb.Append($"{(v.Passed ? "Passed" : "**Problems found**")}: {v.Batches} batches parsed offline, {v.SyntaxErrors} syntax errors; ");
        sb.Append($"{v.ModulesExact} of {v.ModulesChecked} module texts rebuild byte for byte. See `99_review\\validation.md`.\n\n");

        if (outcome.Warnings.Count > 0)
        {
            sb.Append("## Warnings\n\n");
            foreach (var w in outcome.Warnings) sb.Append("- ").Append(w.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>A catalog read failed; the message names the side and carries a hint, never a connection string.</summary>
public sealed class ScriptReadException(string message, Exception inner) : Exception(message, inner);
