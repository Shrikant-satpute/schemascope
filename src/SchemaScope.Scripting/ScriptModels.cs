using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SchemaScope.Core.Engines;

namespace SchemaScope.Scripting;

/// <summary>The folders a user can tick in the "Create scripts" dialog.</summary>
public enum ScriptSection { NewColumns, TableTypes, MissingObjects, ColumnTypes, Constraints, Indexes, Triggers, Formatting, DifferentModules }

/// <summary>What to generate and where. Serialized from the dialog.</summary>
public sealed class ScriptOptions
{
    /// Default = the sections that cannot break the target's current flow (not TableTypes, Triggers or DifferentModules).
    public List<ScriptSection> Sections { get; set; } =
        [ScriptSection.NewColumns, ScriptSection.MissingObjects, ScriptSection.ColumnTypes,
         ScriptSection.Constraints, ScriptSection.Indexes, ScriptSection.Formatting];
    public string OutputFolder { get; set; } = "";          // must be fully qualified
    public string? QuarantineFolder { get; set; }           // *.sql basenames under it (recursive) are left out of 03
    public List<string> ExcludePatterns { get; set; } = []; // * and ? wildcards, matched against "schema.name" or "name", case-insensitive
    public bool CheckExistingData { get; set; } = true;     // run the data probes (COUNT queries) on the target
    public int LargeTableRows { get; set; } = 100_000;
    public int ProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Also write 11_destructive (<see cref="DestructiveGenerator"/>): drops of what only the target has, narrowing or
    /// cross-family column changes, NULL to NOT NULL, and same-name checks with another rule. Off by default; never in
    /// RUN.ps1. With it off the output is exactly what it is without this option.
    /// </summary>
    public bool IncludeDestructive { get; set; }

    /// <summary>
    /// An object script: only these objects (the one asked for, the dependencies chosen with it and their
    /// prerequisites) are scripted, and every object loop skips the rest without a review row. A missing schema
    /// is <c>(schema, "")</c>. Null = the whole database. Set by the server, never read from a request body.
    /// </summary>
    [JsonIgnore] public HashSet<ObjectName>? OnlyObjects { get; set; }

    /// <summary>The run folder's name instead of <c>label_yyyyMMdd-HHmm</c> (object scripts). Never read from a request body.</summary>
    [JsonIgnore] public string? RunFolderName { get; set; }
}

/// <summary>One side of the compare. <see cref="ConnectionString"/> never leaves the server process.</summary>
public sealed record ScriptEndpoint(string Label, string Server, string Database, string ConnectionString,
    DatabaseEngine Engine = DatabaseEngine.SqlServer);

/// <summary>What one generated folder holds, for the result panel and the README.</summary>
public sealed class FolderResult
{
    public string Folder { get; set; } = "";     // "01_columns"
    public string Title { get; set; } = "";      // "New columns"
    public int Files { get; set; }               // objects\*.sql count
    public bool InRunAll { get; set; }           // included by RUN.ps1 by default
    public bool HasRollback { get; set; }
    public List<string> Notes { get; set; } = [];
}

/// <summary>Something the generator deliberately did not script, and why.</summary>
public sealed class HeldBackItem
{
    public string Section { get; set; } = "";    // which folder it would have gone to, or "review"
    public string Object { get; set; } = "";     // schema.name (or table.column / constraint name)
    public string Reason { get; set; } = "";
}

/// <summary>The offline validation result (DESIGN.md §5).</summary>
public sealed class ValidationSummary
{
    public int Batches { get; set; }
    public int SyntaxErrors { get; set; }
    public int ModulesChecked { get; set; }
    public int ModulesExact { get; set; }
    public List<string> Problems { get; set; } = [];
    public bool Passed => SyntaxErrors == 0 && ModulesChecked == ModulesExact && Problems.Count == 0;
}

/// <summary>The API response of a generate run.</summary>
public sealed class ScriptOutcome
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Path { get; set; }            // the run root that was written
    public int FileCount { get; set; }
    public List<FolderResult> Folders { get; set; } = [];
    public List<HeldBackItem> HeldBack { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public ValidationSummary Validation { get; set; } = new();
    public long ElapsedMs { get; set; }
    /// <summary>Every file the run wrote, in write order (object scripts assemble their text from it). Not sent to the browser.</summary>
    [JsonIgnore] public IReadOnlyList<WrittenFile> Files { get; set; } = [];
}

/// <summary>
/// Everything a folder generator gets (internal, never serialized). Generators run in a fixed order
/// (<see cref="ScriptGenerator"/>), share one <see cref="ScriptWriter"/> and <see cref="ReviewLog"/>, and pass
/// facts forward through <see cref="Facts"/>.
/// </summary>
public sealed class ScriptContext
{
    public required ScriptEndpoint SourceEndpoint { get; init; }
    public required ScriptEndpoint TargetEndpoint { get; init; }
    public required ScriptSchema Source { get; init; }
    public required ScriptSchema Target { get; init; }      // AlignCase(Source) already applied
    public required ScriptSchema RawTarget { get; init; }   // target with its own spelling (09 needs it)
    public required TargetProbes Probes { get; init; }
    /// <summary>Read-only probes on the source (compile check for 03, dependency order, disabled triggers for 07).</summary>
    public required SourceProbes SourceProbes { get; init; }
    public required ScriptOptions Options { get; init; }
    public required ScriptWriter Writer { get; init; }
    public required ReviewLog Review { get; init; }
    /// Modules ("schema.name", OrdinalIgnoreCase) that SchemaScope's own compare marked FormattingOnly for this target.
    public required IReadOnlySet<string> FormattingCandidates { get; init; }
    /// <summary>
    /// Modules ("schema.name", OrdinalIgnoreCase) that SchemaScope's compare marked Different for this target, with
    /// the compare's changed-line counts - the candidates of 10_different_modules.
    /// </summary>
    public IReadOnlyDictionary<string, LineChange> DifferentCandidates { get; init; } =
        new Dictionary<string, LineChange>(StringComparer.OrdinalIgnoreCase);
    /// Facts later stages need from earlier ones (e.g. new columns added by 01, blocked columns, drifted table types).
    public CoreFacts Facts { get; } = new();
    public CancellationToken Ct { get; init; }
    /// <summary>Where the dialog's progress bar reads from; null when nobody is watching.</summary>
    public IProgress<ScriptProgress>? Progress { get; init; }
    public bool Selected(ScriptSection s) => Options.Sections.Contains(s);

    /// <summary>True for an object script (<see cref="ScriptOptions.OnlyObjects"/> set).</summary>
    public bool Scoped => Options.OnlyObjects is not null;

    /// <summary>
    /// Whether an object loop may script <paramref name="name"/>: always for a whole-database run; for an object
    /// script only the objects in <see cref="ScriptOptions.OnlyObjects"/>, matched the way the source matches names.
    /// Loops test this first, before exclusions and holds, so 99_review lists only what was asked for.
    /// </summary>
    public bool InScope(ObjectName name)
    {
        if (Options.OnlyObjects is not { } only) return true;
        scope ??= new HashSet<ObjectName>(only, Source.NameComparer);
        return scope.Contains(name);
    }

    private HashSet<ObjectName>? scope;

    /// <summary>Says which step the run has reached and how far along that is (0-100).</summary>
    public void Report(int percent, string step) => Progress?.Report(new ScriptProgress(percent, step));

    /// <summary>The source's default collation - the <c>COLL</c> every new column/table/type is written against.</summary>
    public string SourceCollation => Source.Collation;

    /// <summary>The default <c>$Server</c> generators pass to <see cref="ScriptWriter.WriteRunner"/>: the target's server.</summary>
    public string RunnerServer => ScriptGenerator.RunnerServer(TargetEndpoint);

    /// <summary>
    /// True when <paramref name="name"/> matches one of <see cref="ScriptOptions.ExcludePatterns"/>
    /// (<c>*</c>/<c>?</c> wildcards, case-insensitive, tested against "schema.name" and against "name").
    /// The patterns come from a text box, so they run on the non-backtracking engine: a pattern such as
    /// <c>*a*a*a*a*a*b</c> stays linear instead of stalling the request (no backreferences are needed).
    /// </summary>
    public bool IsExcluded(ObjectName name)
    {
        patterns ??= Options.ExcludePatterns
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            .ToList();
        return patterns.Any(rx => rx.IsMatch(name.ToString()) || rx.IsMatch(name.Name));
    }

    private List<Regex>? patterns;
}

/// <summary>Lines the compare counted as added and removed between the source's and the target's text of one module.</summary>
public sealed record LineChange(int Added, int Removed);

/// <summary>How far a run is, for the dialog's progress bar: 0-100 and the step it is on.</summary>
public sealed record ScriptProgress(int Percent, string Step);

/// <summary>One folder generator. It writes through <see cref="ScriptContext.Writer"/> only and returns one result per folder it built.</summary>
public interface IFolderGenerator
{
    Task<IReadOnlyList<FolderResult>> GenerateAsync(ScriptContext ctx);
}

// ---------------------------------------------------------------------------------- facts

/// <summary>A column 01 adds (generate.py new_cols): source column plus its source default, if any.</summary>
public sealed record NewColumn(ObjectName Table, ColumnRow Column, DefaultRow? Default);

/// <summary>
/// A source column 01 cannot add, with the reason shown in review: NOT NULL with no default, or something the
/// column needs that the target does not have yet when 01 runs (its alias type, or a function or sequence its
/// default or computed expression uses), or a default whose name the target already uses.
/// </summary>
public sealed record BlockedColumn(ObjectName Table, ColumnRow Column, string Reason);

/// <summary>
/// A source column the target will not have after 01: blocked (<see cref="BlockedColumn"/>) or on an excluded
/// table. Anything that names it - a constraint, an index, a module 03 creates, a trigger 07 creates - would fail.
/// </summary>
public sealed record UnavailableColumn(ObjectName Table, string Column, string Reason);

/// <summary>An object left out of 03 because of the quarantine folder or an exclude pattern; <see cref="Type"/> is the source otype or "U" for a table.</summary>
public sealed record HeldObject(ObjectName Name, string Type, string Reason);

/// <summary>One entry of the 03 manifest (generate.py manifest.json "manifest").</summary>
/// <param name="Number">The NNN of the file.</param>
/// <param name="File">File name inside objects\.</param>
/// <param name="Kind">type, table, function, view or proc.</param>
/// <param name="Object">The object.</param>
/// <param name="Module">The source module row for function/view/proc, null for type/table.</param>
public sealed record MissingItem(int Number, string File, string Kind, ObjectName Object, ModuleRow? Module);

/// <summary>One 04 ALTER COLUMN that was written (the structured plan check_scripts §5 re-verifies).</summary>
/// <param name="Group">plain, family, widen_int or relax.</param>
public sealed record IntendedAlter(int Number, string File, ObjectName Table, string Column, ColumnRow Source, ColumnRow Target,
    string Group, string NewType, bool Nullable, string Sql);

/// <summary>
/// One statement 05 or 06 wrote. Exactly one of the row properties is set, matching <see cref="Kind"/>
/// (default, check, key, foreignkey, index); none for a "state" statement, which changes an existing check or
/// foreign key of the target.
/// </summary>
public sealed record EmittedStatement(int Number, string File, string Kind, ObjectName Table, string? Name, string Sql)
{
    public DefaultRow? Default { get; init; }
    public CheckRow? Check { get; init; }
    public KeyRow? Key { get; init; }
    public ForeignKeyRow? ForeignKey { get; init; }
    public IndexRow? Index { get; init; }
}

/// <summary>One 09 rename (generate_formatting.py renames tuple).</summary>
/// <param name="Kind">table, column, index or object.</param>
public sealed record CaseRenameItem(int Number, string File, string Kind, string Schema, string Table, string Old, string New);

/// <summary>
/// Facts later stages need from earlier ones. Filled by CoreFoldersGenerator (B1) even for folders
/// that are not selected - e.g. 05 needs 01's inline defaults, flow_safety needs 01's new columns -
/// and by TriggersGenerator / FormattingGenerator (B2) for their own folders. ReviewReports (B3) and
/// ScriptValidator (B4) only read it.
/// </summary>
public sealed class CoreFacts
{
    // ---- 01_columns (B1)
    /// <summary>Columns 01 adds (written or not), in emit order.</summary>
    public List<NewColumn> NewColumns { get; } = [];
    /// <summary>Source columns 01 cannot add.</summary>
    public List<BlockedColumn> BlockedColumns { get; } = [];
    /// <summary>Every source column of a common table the target will not have after 01 (blocked or excluded).</summary>
    public List<UnavailableColumn> UnavailableColumns { get; } = [];
    /// <summary>True when 01 files were written.</summary>
    public bool ColumnsWritten { get; set; }

    // ---- 02_tabletypes (B1)
    /// <summary>Table types on both sides whose column-name sets differ, sorted.</summary>
    public List<ObjectName> DriftedTableTypes { get; } = [];
    /// <summary>Target modules that take a drifted type as a parameter (dropped and recreated from the TARGET's text by 02).</summary>
    public List<ObjectName> TableTypeBlockers { get; } = [];
    /// <summary>True when 02 files were written.</summary>
    public bool TableTypesWritten { get; set; }

    // ---- 03_missing (B1)
    /// <summary>Source tables the target lacks (after quarantine/exclusions), sorted.</summary>
    public List<ObjectName> MissingTables { get; } = [];
    /// <summary>Source modules the target lacks (after quarantine/exclusions), sorted - triggers included; 03 only creates FN/IF/TF/V/P.</summary>
    public List<ObjectName> MissingModules { get; } = [];
    /// <summary>Source table types the target lacks, sorted.</summary>
    public List<ObjectName> MissingTypes { get; } = [];
    /// <summary>Lower-cased base names of the *.sql files under the quarantine folder (10 leaves these alone too).</summary>
    public HashSet<string> QuarantineNames { get; } = new(StringComparer.Ordinal);
    /// <summary>Objects left out of 03 by the quarantine folder or an exclude pattern.</summary>
    public List<HeldObject> HeldQuarantine { get; } = [];
    /// <summary>Missing modules that do not compile on the source (SourceProbes.CompileCheck) - not created.</summary>
    public List<HeldObject> BrokenInSource { get; } = [];
    /// <summary>The 03 items, in file order.</summary>
    public List<MissingItem> MissingItems { get; } = [];
    /// <summary>
    /// Checks of new tables deferred from 03's CREATE TABLE to 05: those that call a function (03 creates
    /// functions after tables) and those disabled in the source (a CHECK inside CREATE TABLE is always enabled).
    /// </summary>
    public List<CheckRow> DeferredChecks { get; } = [];
    /// <summary>Defaults of new tables that use a function the target lacks, deferred from 03's CREATE TABLE to 05.</summary>
    public List<DefaultRow> DeferredDefaults { get; } = [];
    /// <summary>True when 03 files were written.</summary>
    public bool MissingWritten { get; set; }

    // ---- 04_columntypes (B1)
    /// <summary>Every ALTER COLUMN 04 planned (written or not).</summary>
    public List<IntendedAlter> IntendedAlters { get; } = [];

    // ---- 05_constraints / 06_indexes (B1)
    /// <summary>Statements 05 planned, in file order.</summary>
    public List<EmittedStatement> Constraints { get; } = [];
    /// <summary>Statements 06 planned, in file order.</summary>
    public List<EmittedStatement> Indexes { get; } = [];

    // ---- 07_triggers (B2)
    /// <summary>Source triggers 07 creates, in file order.</summary>
    public List<ObjectName> Triggers { get; } = [];

    // ---- 08 / 09 (B2)
    /// <summary>Modules 08 replaces with the source text, in file order.</summary>
    public List<ObjectName> FormattingModules { get; } = [];
    /// <summary>Renames 09 performs, in file order.</summary>
    public List<CaseRenameItem> Renames { get; } = [];

    // ---- 10
    /// <summary>Modules 10 replaces with the source text, in file order.</summary>
    public List<ObjectName> DifferentModules { get; } = [];

    // ---- 11_destructive
    /// <summary>Target-only tables, modules, table types, synonyms and sequences 11 drops, in file order.</summary>
    public List<ObjectName> DestructiveDrops { get; } = [];
    /// <summary>Columns of tables both sides have whose type or nullability 11 changes, in file order.</summary>
    public List<(ObjectName Table, string Column)> DestructiveAlters { get; } = [];

    // ---- helpers shared by B1/B3/B4
    /// <summary>(table, lower-cased column) of every 01 column, for "is this column new?" tests.</summary>
    public bool IsNewColumn(ObjectName table, string column) =>
        NewColumns.Any(n => n.Table.SameAs(table) && string.Equals(n.Column.Col, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the column is one 01 could not add.</summary>
    public bool IsBlockedColumn(ObjectName table, string column) =>
        BlockedColumns.Any(b => b.Table.SameAs(table) && string.Equals(b.Column.Col, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when 03 creates this table.</summary>
    public bool IsNewTable(ObjectName table) => MissingTables.Any(t => t.SameAs(table));
}
