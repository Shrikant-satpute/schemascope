using System.Diagnostics;
using System.Text;
using Npgsql;
using SchemaScope.Core.Engines;
using SchemaScope.PostgreSql;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>
/// PostgreSQL to PostgreSQL "Create scripts" (plan §11 R2): the numbered folders, written from the compare's two
/// snapshots with the planning helpers every engine shares (<see cref="Planning.PlanOrder"/>), <see cref="ScriptWriter"/>
/// and <see cref="ReviewLog"/>. Read-only toward both databases: the only statements sent are the probes'
/// (<see cref="PgTargetProbes"/>), in READ ONLY transactions. Each folder has psql runners; nothing is applied from here.
///
///   01 columns: only a column that is nullable or has a default (or is identity or generated)
///   03 missing: schemas, enum / domain / composite types, sequences, tables, then views, materialized views,
///      functions, procedures and aggregates in pg_depend order, copied from pg_get_*def (not compile-checked);
///      extensions are noted in 99_review only
///   04 column types: widenings and NOT NULL to NULL only; held while a view, rule, policy, trigger or generated
///      column reads the column
///   05 constraints: defaults, checks, keys and foreign keys; a check or foreign key on an existing table is added
///      NOT VALID and validated by a later file
///   06 indexes: without CONCURRENTLY (it cannot run inside the file's transaction)
///   07 triggers: each with its trigger function when the target lacks it
///   10 different code: CREATE OR REPLACE when the signature and the return type are unchanged, otherwise held
///   11 destructive: only with "Include changes that can lose data" - drops of what only the target has and column
///      changes that can lose data, each after a read-only probe, every DROP RESTRICT; never run by RUN.ps1
///   08 formatting (code): CREATE OR REPLACE for code that differs only in layout, comments or letter case
///   09 formatting (names): RENAME COLUMN for a column whose name differs only by letter case
///   02: not applicable on PostgreSQL
/// </summary>
public sealed class PgScriptGenerator : IScriptGenerator
{
    public const string ColumnsFolder = "01_columns";
    public const string TableTypesFolder = "02_tabletypes";
    public const string MissingFolder = "03_missing";
    public const string ColumnTypesFolder = "04_columntypes";
    public const string ConstraintsFolder = "05_constraints";
    public const string IndexesFolder = "06_indexes";
    public const string TriggersFolder = "07_triggers";
    public const string FormattingModulesFolder = "08_formatting_modules";
    public const string FormattingTablesFolder = "09_formatting_tables";
    public const string DifferentFolder = "10_different_modules";
    public const string DestructiveFolder = DestructiveGenerator.Folder;
    public const string DestructiveTitle = DestructiveGenerator.Title;

    /// <summary>The folders RUN.ps1 runs, in order: 04 first (it only changes what the target has), 01 before 03 (new code reads new columns), then 05 and 06.</summary>
    public static IReadOnlyList<string> RunAllFolders { get; } = [ColumnTypesFolder, ColumnsFolder, MissingFolder, ConstraintsFolder, IndexesFolder];

    /// <summary>Tests only: the probes to use instead of real ones on the target.</summary>
    internal Func<ScriptEndpoint, ScriptOptions, PgTargetProbes>? ProbesFactory { get; init; }

    public async Task<ScriptOutcome> GenerateAsync(ScriptRequestContext req, CancellationToken ct, IProgress<ScriptProgress>? progress)
    {
        void Report(int percent, string step) => progress?.Report(new ScriptProgress(percent, step));
        var sw = Stopwatch.StartNew();
        var outcome = new ScriptOutcome();
        ScriptWriter? writer = null;
        var options = req.Options;
        try
        {
            if (Check(req) is { } problem)
            {
                outcome.Message = problem;
                return outcome;
            }

            Report(2, "Planning from the compare's snapshots");
            var review = new ReviewLog();
            writer = new ScriptWriter(ScriptGenerator.NewRunRoot(options.OutputFolder,
                options.RunFolderName ?? $"{req.Target.Label}_{DateTime.Now:yyyyMMdd-HHmm}"), sqlBom: false);
            outcome.Path = writer.Root;

            await using var probes = ProbesFactory?.Invoke(req.Target, options) ??
                                     new PgTargetProbes(req.Target.Label, req.Target.ConnectionString, options.ProbeTimeoutSeconds, options.CheckExistingData);
            var run = new PgScriptRun(req, review, probes, ct);
            await run.PlanAsync(Report);
            ct.ThrowIfCancellationRequested();

            Report(80, "Writing the folders");
            var runner = PgRunnerTarget.From(req.Target);
            var folders = new List<FolderResult>();
            void Add(PgFolder f, ScriptSection section, params string[] notes)
            {
                if (!options.Sections.Contains(section)) return;
                folders.Add(Write(writer, f, runner, notes));
            }
            Add(run.Columns, ScriptSection.NewColumns);
            if (options.Sections.Contains(ScriptSection.TableTypes))
                folders.Add(NotApplicable(TableTypesFolder, "Table types", "PostgreSQL has no table types."));
            Add(run.Missing, ScriptSection.MissingObjects,
                "Code is copied from the source's pg_get_*def text in pg_depend order and is not compile-checked; extensions are noted in 99_review only.");
            Add(run.ColumnTypes, ScriptSection.ColumnTypes);
            Add(run.Constraints, ScriptSection.Constraints,
                "Checks and foreign keys on existing tables are added NOT VALID, then validated by later files of this folder.");
            Add(run.Indexes, ScriptSection.Indexes, run.Indexes.Files.Any(f => f.Name.StartsWith("foreignkey_", StringComparison.Ordinal))
                ? ["No CONCURRENTLY: each index is built inside its file's transaction.", "A foreign key that needs a unique index of this folder comes after the indexes."]
                : ["No CONCURRENTLY: each index is built inside its file's transaction."]);
            Add(run.Triggers, ScriptSection.Triggers, "A trigger's file also creates its trigger function when the target lacks it.");
            Add(run.FormattingModules, ScriptSection.Formatting,
                "Code whose text differs only in layout, comments or letter case is re-created with the source's text (CREATE OR REPLACE, the signature unchanged).");
            Add(run.FormattingTables, ScriptSection.Formatting,
                "A column whose name differs from the source's only by letter case is renamed (RENAME COLUMN), keeping its values.");
            Add(run.Different, ScriptSection.DifferentModules);
            if (options.IncludeDestructive) folders.Add(Write(writer, run.Destructive, runner, [.. run.Destructive.Notes]));
            folders = [.. folders.OrderBy(f => f.Folder, StringComparer.Ordinal)];
            ct.ThrowIfCancellationRequested();

            Report(90, "Checking the scripts offline");
            outcome.Validation = new PgScriptValidator().Validate(writer.Files, review, run.Src.MajorVersion, run.Tgt.MajorVersion);
            ct.ThrowIfCancellationRequested();

            Report(97, "Writing RUN.ps1 and README.md");
            foreach (var n in probes.NotChecked) review.Warn(n);
            if (!options.CheckExistingData)
                review.Warn("The data already in the target was not checked (switched off): keys, checks and foreign keys were written " +
                            "without counting the rows that would break them.");
            review.WriteAll(writer);

            var runAll = RunAllFolders.Where(f => folders.Any(r => r.Folder == f && r.InRunAll && writer.FilesIn(f).Count > 0)).ToList();
            if (!run.Scoped) writer.WriteRootFile("RUN.ps1", PgRunners.Root(runner, runAll), bom: true, WrittenFileKind.Runner);

            outcome.Folders = folders;
            outcome.HeldBack = review.ToHeldBack();
            outcome.Warnings = [.. review.Warnings];
            if (run.Scoped) outcome.Warnings.AddRange(review.ToHeldBack(notes: true).Select(n => $"{n.Object}: {n.Reason}"));
            if (!outcome.Validation.Level!.Parsed) outcome.Warnings.Add(outcome.Validation.Level.NotValidatedReason!);
            else if (!outcome.Validation.Passed)
                outcome.Warnings.Add("Validation found problems - read 99_review\\validation.md before running anything.");
            if (!run.Scoped) writer.WriteMarkdown("", "README.md", Readme(req, run, folders, runAll, outcome, probes.Notes, runner));

            outcome.FileCount = writer.Files.Count;
            outcome.Success = true;
            outcome.Message = $"Wrote {outcome.FileCount} files to {writer.Root}.";
            Report(100, "Done");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome.Success = false;
            outcome.Message = "Cancelled." + ScriptGenerator.PartialNote(writer, "it was cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.Success = false;
            outcome.Message = $"Could not write the scripts: {ex.Message}";
            outcome.Message += ScriptGenerator.PartialNote(writer, outcome.Message);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.Message = $"Script generation failed: {(ex is NpgsqlException ? PgConnectionService.MessageOf(ex) : ex.Message)}";
            outcome.Message += ScriptGenerator.PartialNote(writer, outcome.Message);
        }
        finally
        {
            if (writer is not null)
            {
                outcome.FileCount = writer.Files.Count;
                outcome.Files = writer.Files;
            }
            outcome.ElapsedMs = sw.ElapsedMilliseconds;
        }
        return outcome;
    }

    /// <summary>Why the request cannot run, or null. Nothing is written before these pass.</summary>
    private static string? Check(ScriptRequestContext req)
    {
        var o = req.Options;
        if (string.IsNullOrWhiteSpace(o.OutputFolder) || !Path.IsPathFullyQualified(o.OutputFolder))
            return "Choose a full output folder path, e.g. D:\\Scripts.";
        if (o.Sections.Count == 0) return "Tick at least one section.";
        if (!string.IsNullOrWhiteSpace(o.QuarantineFolder) && !Directory.Exists(o.QuarantineFolder))
            return $"The quarantine folder does not exist: {o.QuarantineFolder}";
        if (req.Source.Engine != DatabaseEngine.PostgreSql || req.Target.Engine != DatabaseEngine.PostgreSql)
            return "PostgreSQL scripts need a PostgreSQL source and target.";
        if (req.SourceSnap is not { Failed: false } || req.TargetSnap is not { Failed: false })
            return "One of the two databases could not be read in this comparison - fix the connection and run the compare again.";
        return null;
    }

    /// <summary>Writes a folder's objects\NNN_*.sql, _ALL.sql and run_all.ps1, or nothing when it has no file.</summary>
    private static FolderResult Write(ScriptWriter writer, PgFolder folder, PgRunnerTarget runner, string[] notes)
    {
        var result = new FolderResult { Folder = folder.Name, Title = folder.Title, InRunAll = folder.InRunAll };
        if (folder.Files.Count == 0)
        {
            result.Notes.Add(ScriptGenerator.NothingToDoNote);
            result.Notes.AddRange(notes);
            return result;
        }
        var all = new StringBuilder($"-- {folder.Name}: every file of objects\\ in order, for one manual run:\n" +
                                    "--   psql -X -v ON_ERROR_STOP=1 --single-transaction -f _ALL.sql\n");
        var n = 0;
        var rollbacks = new List<string>();
        foreach (var f in folder.Files)
        {
            var name = ScriptText.Safe(f.Name);
            if (name.Length > 100) name = name[..100];
            var file = $"{++n:000}_{name}.sql";
            writer.WriteSql(folder.Name, "objects\\" + file, f.Text, f.Module is null ? WrittenFileKind.Statements : WrittenFileKind.RawModule, f.Object,
                expectedModuleText: f.Module);
            all.Append('\n').Append(f.Text);
            // 11_destructive: the dropped code as the target has it, under the same file name; never part of a plan
            if (f.Rollback is null) continue;
            writer.WriteSql(folder.Name, "rollback\\" + file, f.Rollback, WrittenFileKind.RawModule, f.Object, expectedModuleText: f.RollbackModule);
            rollbacks.Add(f.Rollback);
        }
        writer.WriteSql(folder.Name, "_ALL.sql", all.ToString());
        if (rollbacks.Count > 0)
        {
            // undo in reverse: what was dropped last (what the others used) comes back first
            var back = new StringBuilder("-- rollback\\: every file in reverse order, for one manual run:\n" +
                                         "--   psql -X -v ON_ERROR_STOP=1 --single-transaction -f _ALL.sql\n");
            foreach (var r in Enumerable.Reverse(rollbacks)) back.Append('\n').Append(r);
            writer.WriteSql(folder.Name, "rollback\\_ALL.sql", back.ToString());
            result.HasRollback = true;
        }
        writer.WriteRunnerText(folder.Name, PgRunners.Folder(runner));
        result.Files = writer.ObjectFileCount(folder.Name);
        result.Notes.AddRange(notes);
        return result;
    }

    private static FolderResult NotApplicable(string folder, string title, string why) =>
        new() { Folder = folder, Title = title, Notes = [$"Not applicable for PostgreSQL: {why}"] };

    // ------------------------------------------------------------------ README

    private static readonly Dictionary<string, string> FolderText = new(StringComparer.Ordinal)
    {
        [ColumnsFolder] = "Adds columns the source has on tables both sides have: only those that are nullable or have a default (or are identity or generated). A NOT NULL column with no default cannot be added to a table that has rows - see 99_review.",
        [TableTypesFolder] = "Not applicable: PostgreSQL has no table types.",
        [MissingFolder] = "Creates the schemas, collations, enum, range, domain and composite types, sequences, tables (partitions with PARTITION OF their parent, inheritance children with INHERITS), views, materialized views, functions, procedures and aggregates the target lacks, in pg_depend order. Code is copied from the source's pg_get_*def text and is not compile-checked; it is created with plain CREATE, never OR REPLACE, so a file fails and changes nothing when the target already has that object. Extensions are only noted in 99_review. No triggers (see 07), no foreign keys (05) and no indexes (06).",
        [ColumnTypesFolder] = "Widens columns (longer varchar, numeric with more digits, int to bigint) and relaxes NOT NULL to NULL where the source does, and adds the labels an enum type lacks (one file each). Never narrows, never tightens; held while a view or similar reads the column.",
        [ConstraintsFolder] = "Adds defaults, checks, primary and unique keys and foreign keys the target lacks, and brings existing objects in line where nothing can be lost: a changed default, a foreign key whose actions differ, row level security and its policies, storage options and logging, sequence options and owners, domain defaults and checks, schema owners. On an existing table a check or foreign key is added NOT VALID (new rows are checked at once, without a long lock) and validated by a later file.",
        [IndexesFolder] = "Adds indexes the target lacks, without CONCURRENTLY (it cannot run inside the file's transaction), so writes to the table wait while each one builds. An index of a partitioned table is built on every partition. A foreign key whose referenced columns only a unique index of this folder covers comes after the indexes.",
        [TriggersFolder] = "Creates source triggers the target lacks, each with its trigger function when the target lacks that too, and the tables' rules (CREATE RULE). A trigger or a rule changes every write to its table, so this folder is never run by RUN.ps1.",
        [FormattingModulesFolder] = "Re-creates functions, procedures and views whose text differs from the source's only in layout, comments or letter case, with CREATE OR REPLACE and the source's text (the signature is unchanged, so nothing that uses them breaks). Never run by RUN.ps1.",
        [FormattingTablesFolder] = "Renames columns whose names differ from the source's only by letter case (ALTER TABLE ... RENAME COLUMN), which keeps their values. PostgreSQL names are exact, so code that names a column the old way must change with it. Never run by RUN.ps1.",
        [DifferentFolder] = "Replaces functions, procedures, aggregates, views and triggers whose code really differs with CREATE OR REPLACE, when the signature, the return type (and a view's columns) allow it; anything else is held. Replaces the target's own code - read 99_review\\different_modules.md first.",
        [DestructiveFolder] = "**CAN LOSE DATA.** Drops what only the target has (tables with their rows, columns with their values, views, materialized views, functions, procedures, aggregates, triggers, sequences, domains and types, and defaults, checks, foreign keys, unique keys and indexes), narrows columns, turns NULL into NOT NULL, and replaces checks whose rule differs. Each change was checked against the data when generated; what could not be proven safe is held back in 99_review\\not_applied_destructive.md. Every DROP is RESTRICT. Never run by RUN.ps1 - see the caution below.",
    };

    private static string Readme(ScriptRequestContext req, PgScriptRun run, List<FolderResult> folders, List<string> runAll, ScriptOutcome outcome,
        IReadOnlyList<PgProbeNote> checks, PgRunnerTarget runner)
    {
        var src = req.Source;
        var tgt = req.Target;
        var sb = new StringBuilder();
        sb.Append($"# Scripts to make {tgt.Label} look like {src.Label}\n\n");
        sb.Append($"Generated by SchemaScope on {DateTime.Now:yyyy-MM-dd HH:mm}, for PostgreSQL.\n\n");
        sb.Append($"- Source: **{src.Label}** ({src.Server} / {src.Database}, {run.Src.EngineVersion})\n");
        sb.Append($"- Target: **{tgt.Label}** ({tgt.Server} / {tgt.Database}, {run.Tgt.EngineVersion})\n\n");
        sb.Append("> **Run on a rehearsal copy of the target first.** Restore a backup of the target under another name, ");
        sb.Append("run the scripts there, test the application against it, then re-run the SchemaScope compare. ");
        sb.Append("Only then run them on the real target, in a maintenance window.\n\n");

        sb.Append("## How to run\n\n```powershell\n");
        sb.Append($"# -Server defaults to {runner.Host}, -Port to {runner.Port}, -User to {runner.User}, -SslMode to {runner.SslMode}\n");
        sb.Append("powershell -ExecutionPolicy Bypass -File .\\RUN.ps1 -Database <TargetDatabase>\n");
        sb.Append("# or with a password file kept outside this folder\n");
        sb.Append("powershell -ExecutionPolicy Bypass -File .\\RUN.ps1 -Database <TargetDatabase> -PassFile C:\\Users\\me\\pgpass.conf\n");
        sb.Append("```\n\n");
        sb.Append("The runners need `psql` on the PATH. No password is written anywhere in this folder: psql reads it from the PGPASSFILE you name ");
        sb.Append("(`-PassFile`, or the PGPASSFILE environment variable), which must be outside this folder, or the runner asks for it once and keeps ");
        sb.Append("it only in its own process for psql.\n\n");
        sb.Append(runAll.Count == 0
            ? "RUN.ps1 runs no folder by default for this selection; run the folders below one by one.\n\n"
            : $"RUN.ps1 runs, in this order: {string.Join(", ", runAll)}. Each folder's `run_all.ps1` runs its `objects\\*.sql` files by number with " +
              "`psql -X -v ON_ERROR_STOP=1 --single-transaction -f`: each file is one transaction, so a file that fails changes nothing.\n\n");
        sb.Append("Each folder also has `_ALL.sql`, the same files in one, for one manual run: `psql -X -v ON_ERROR_STOP=1 --single-transaction -f _ALL.sql`.\n\n");

        sb.Append("## Folders\n\n| Folder | What it does | Files | In RUN.ps1 |\n|---|---|---:|---|\n");
        foreach (var f in folders)
        {
            var what = FolderText.TryGetValue(f.Folder, out var text) ? text : f.Title;
            if (f.Files == 0 && f.Notes.Count > 0 && !f.Notes[0].StartsWith("Not applicable", StringComparison.Ordinal)) what += " " + string.Join(" ", f.Notes);
            sb.Append($"| {f.Folder} | {ReviewLog.Cell(what)} | {f.Files} | {(f.InRunAll ? "yes" : "no")} |\n");
        }
        sb.Append("| 99_review | Reports only - no runnable scripts. Everything held back, and why. | - | no |\n\n");

        sb.Append("## Run order rules\n\n");
        sb.Append("- **04 first**: it only changes columns the target already has, before anything new can read them.\n");
        sb.Append("- **01 before 03**: new code and views read the new columns.\n");
        sb.Append("- **05 after 03**: foreign keys of new tables need every table to exist; a VALIDATE file comes after the file that adds its constraint.\n");
        sb.Append("- **07, 08, 09 and 10 are never run by RUN.ps1.** Run each folder's `run_all.ps1` on its own when you have decided to, after RUN.ps1.\n");
        var destructive = folders.Any(f => f.Folder == DestructiveFolder && f.Files > 0);
        if (destructive) sb.Append("- **11 is never run by RUN.ps1**, and comes last: after every other folder, and only once you have read the caution below.\n");
        sb.Append('\n');

        if (destructive)
        {
            sb.Append("## 11_destructive - changes that can lose data\n\n");
            sb.Append($"> **11_destructive deletes things from {tgt.Label}.** Its files drop tables (with every row), columns (with every value) and ");
            sb.Append("code, narrow columns and make them NOT NULL. Back up the target and check the backup restores before running any of them. ");
            sb.Append("Each file says in its header what it drops and how many rows or values it held when this script was generated.\n\n");
            sb.Append("- Every change was checked against the target's data when generated (read-only); a narrowing is checked again inside its file, ");
            sb.Append("which stops and changes nothing if a row written since no longer fits.\n");
            sb.Append("- Every DROP is RESTRICT: if anything SchemaScope did not see depends on what it drops, that file fails and its transaction changes nothing.\n");
            sb.Append("- `rollback\\` recreates the views, materialized views, functions, procedures, aggregates and triggers 11 drops, as the target had them. ");
            sb.Append("Nothing brings back a dropped table, column or sequence value except the backup.\n");
            sb.Append("- In SchemaScope it is applied only in Read & write mode, with \"I understand this can lose data\" ticked.\n\n");
        }

        sb.Append(destructive
            ? "## Undo\n\n- 01, 03, 05, 06 and 07 only add; to undo them, drop what was added or restore the backup. "
            : "## Undo\n\n- Nothing in these scripts drops anything. 01, 03, 05, 06 and 07 only add; to undo them, drop what was added or restore the backup. ");
        sb.Append("04 widens columns; narrowing back can lose data, so restore the backup. 10 replaces code: the target's text before is in the compare.\n");
        if (destructive)
            sb.Append("- 11 drops tables, columns and code that only the target has and changes column types: only the backup brings their data back; " +
                      "`11_destructive\\rollback\\` recreates the dropped code.\n");
        sb.Append('\n');

        sb.Append("## Held back\n\n");
        if (outcome.HeldBack.Count == 0) sb.Append("Nothing was held back.\n\n");
        else
        {
            sb.Append($"{outcome.HeldBack.Count} items were not scripted because each needs a decision. Details, each with its reason, are in `99_review\\`.\n\n");
            sb.Append("| Would have gone to | Items |\n|---|---:|\n");
            foreach (var g in outcome.HeldBack.GroupBy(h => h.Section).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.Append($"| {g.Key} | {g.Count()} |\n");
            sb.Append('\n');
        }

        var v = outcome.Validation;
        sb.Append("## Validation\n\n");
        sb.Append(v.Level!.Parsed
            ? $"{(v.Passed ? "Passed" : "**Problems found**")}: {v.Batches} statements parsed with {v.Level.Parser}, {v.SyntaxErrors} syntax errors; "
            : $"**Lexical check only - not validated**: {v.Batches} statements, {v.SyntaxErrors} lexical errors; ");
        sb.Append($"{v.ModulesExact} of {v.ModulesChecked} code bodies are the source's text exactly. {PgScriptValidator.NotCompileChecked} See `99_review\\validation.md`.\n\n");

        sb.Append("## Checks on the target\n\n| Check | Result |\n|---|---|\n");
        foreach (var c in checks)
        {
            var kind = c.Kind switch { ProbeResultKind.Ok => "checked", ProbeResultKind.NotChecked => "**not checked**", _ => "not applicable" };
            sb.Append($"| {ReviewLog.Cell(c.What)} | {kind}: {ReviewLog.Cell(c.Detail)} |\n");
        }
        sb.Append('\n');

        if (outcome.Warnings.Count > 0)
        {
            sb.Append("## Warnings\n\n");
            foreach (var w in outcome.Warnings) sb.Append("- ").Append(w.ReplaceLineEndings(" ")).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
