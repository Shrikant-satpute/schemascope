using System.Text.Json.Serialization;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;

namespace SchemaScope.Api;

/// <summary>A database to include in a compare: either a saved id, or an inline connection.</summary>
public sealed class SourceRef
{
    public string? ConnectionId { get; set; }
    /// <summary>Overrides the saved connection's database, so one server can be used many times.</summary>
    public string? Database { get; set; }
    public string? Label { get; set; }
    public ConnectionSettings? Inline { get; set; }
}

public sealed class CompareRequest
{
    public SourceRef Source { get; set; } = new();
    public List<SourceRef> Targets { get; set; } = [];
    public CompareOptions? Options { get; set; }
    public string? ProfileId { get; set; }
}

public sealed class CompareStarted
{
    public string RunId { get; set; } = "";
}

public sealed class ProgressEvent
{
    /// <summary>progress | done | error</summary>
    public string Type { get; set; } = "progress";
    public string SourceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Stage { get; set; } = "";
    public int Percent { get; set; }
    public string? Detail { get; set; }
    public long ElapsedMs { get; set; }
}

public sealed class TargetObjectDetail
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public ObjectStatus Status { get; set; }
    public bool Exists { get; set; }
    public string Text { get; set; } = "";
    public string? Note { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public List<StructureSection> Structure { get; set; } = [];
}

public sealed class ObjectDetail
{
    public string Key { get; set; } = "";
    public string TypeLabel { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName => $"{Schema}.{Name}";
    public string? ParentName { get; set; }
    public bool IsModule { get; set; }
    /// <summary>True when the grid view is more useful than the text view.</summary>
    public bool HasStructure { get; set; }
    public string Language { get; set; } = "sql";

    public bool SourceExists { get; set; }
    public string SourceText { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public DateTime? SourceModified { get; set; }

    public List<TargetObjectDetail> Targets { get; set; } = [];
}

public sealed class RunSummary
{
    public string RunId { get; set; } = "";
    public string Status { get; set; } = "running";
    public DateTime StartedUtc { get; set; }
    public string? Error { get; set; }
    public string? Hint { get; set; }
    public CompareResult? Result { get; set; }
}

public sealed class ExportRequest
{
    public string Format { get; set; } = "html";
    /// <summary>Only export rows that differ. Defaults to true.</summary>
    public bool DifferencesOnly { get; set; } = true;
    /// <summary>Limit to these object keys. Empty means everything.</summary>
    public List<string> Keys { get; set; } = [];
    /// <summary>Folder for the .sql file export.</summary>
    public string? OutputFolder { get; set; }
    /// <summary>Which target the .sql export should be written from. Default is the source.</summary>
    public int? TargetIndex { get; set; }
}

public sealed class ExportResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Path { get; set; }
    public int FileCount { get; set; }
}

/// <summary>
/// "Create scripts" for one target of a finished compare. The databases come from the
/// compare session, never from the request, so the scripts always describe the pair that
/// was actually compared. The response is a <see cref="ScriptOutcome"/>.
/// </summary>
public sealed class ScriptRequest
{
    /// <summary>Index into the run's targets, the same index as TargetSummary.Id. Required: null (left out) is a 400, not target 0.</summary>
    public int? TargetIndex { get; set; }
    public ScriptOptions Options { get; set; } = new();
    /// <summary>Optional id the caller makes up, to poll GET /api/scripts/progress/{id} while the run is going.</summary>
    public string? ProgressId { get; set; }
}

/// <summary>
/// "Generate script" for one object of a finished compare against one target. Like <see cref="ScriptRequest"/>,
/// the databases come from the compare session, never from the request.
/// </summary>
public sealed class ObjectScriptRequest
{
    /// <summary>Index into the run's targets. Required.</summary>
    public int? TargetIndex { get; set; }
    /// <summary>The row key the UI fetches object details with (<see cref="CompareRow.Key"/>).</summary>
    public string? Key { get; set; }
    /// <summary>Row keys of dependencies to script with it (from the dependency check). Prerequisites are added anyway.</summary>
    public List<string>? IncludeDependencies { get; set; }
    /// <summary>Optional, as for <see cref="ScriptRequest.ProgressId"/>.</summary>
    public string? ProgressId { get; set; }
}

/// <summary>What scripting one object would do, and which objects it needs that the target lacks or has differently.</summary>
public sealed class ObjectDependencies
{
    public string Key { get; set; } = "";
    public ObjectStatus Status { get; set; }
    /// <summary>create | alter | tableChanges | tableType | trigger | formatting | extra | identical | unavailable</summary>
    public string Plan { get; set; } = "";
    public List<ObjectDependency> Dependencies { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ObjectDependency
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>The type label the grid shows ("Table", "Procedure", ...).</summary>
    public string Type { get; set; } = "";
    public ObjectStatus Status { get; set; }
    /// <summary>A prerequisite (schema, alias type, sequence, synonym): the server scripts it whether or not it is sent.</summary>
    public bool Required { get; set; }
    /// <summary>The object's CREATE fails without it, so the UI ticks it by default; the user may untick it.</summary>
    public bool DefaultIncluded { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>The answer to POST /api/compare/{runId}/object-script.</summary>
public sealed class ObjectScriptOutcome
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Every part in run order, each after a "-- folder: object" line, GO kept.</summary>
    public string Script { get; set; } = "";
    /// <summary>The rollback\ files of the parts that replace or create code (10, 08, 07, 02), or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Rollback { get; set; }
    /// <summary>For an object only the target has: the DROP statement, to copy. Generate script never runs it (Create scripts can put it in 11_destructive).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? DropPreview { get; set; }
    public List<ObjectScriptPart> Parts { get; set; } = [];
    public List<ObjectScriptHeld> HeldBack { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public ObjectScriptValidation Validation { get; set; } = new();
    public List<string> DependenciesIncluded { get; set; } = [];
    public long ElapsedMs { get; set; }
    /// <summary>The run folder, or "" when nothing was written.</summary>
    public string Path { get; set; } = "";
    /// <summary>Set only while write mode is on and the script is complete and validated: the plan POST .../apply takes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? PlanId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public ApplyTarget? ApplyTarget { get; set; }
    public IReadOnlyList<PlanSection> PlanSections { get; set; } = [];
    /// <summary>The generator's own outcome (its files and folders), for the apply plan. Not sent to the browser.</summary>
    [JsonIgnore] public ScriptOutcome? Generated { get; set; }
}

public sealed class ObjectScriptPart
{
    public string Folder { get; set; } = "";
    /// <summary>Absolute path of the objects\*.sql file.</summary>
    public string File { get; set; } = "";
    public string ObjectName { get; set; } = "";
    /// <summary>How the file runs: statements | rawModule (the whole file is one batch, with no GO at the end) | execBlockModules.</summary>
    public string Kind { get; set; } = "";
}

public sealed class ObjectScriptHeld
{
    public string ObjectName { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ObjectScriptValidation
{
    public bool Passed { get; set; } = true;
    public string Summary { get; set; } = "";
}

/// <summary>
/// POST /api/compare/{runId}/apply: run a generated plan against its target. Owner app only, and only while
/// write mode is on. The database comes from the compare session, never from the request; the request names
/// the plan, the sections to run, and the target database name the user typed to confirm it.
/// </summary>
public sealed class ApplyRequest
{
    public string? PlanId { get; set; }
    public int? TargetIndex { get; set; }
    /// <summary>Folder names of the plan ("04_columntypes", ...). Must be a subset of the plan's sections.</summary>
    public List<string>? Sections { get; set; }
    /// <summary>The target database name as the user typed it; compared case-insensitively.</summary>
    public string? ConfirmDatabase { get; set; }
    /// <summary>Required for "11_destructive".</summary>
    public bool AcceptDataLoss { get; set; }
    /// <summary>Optional, as for <see cref="ScriptRequest.ProgressId"/>; also the id POST /api/apply/{id}/stop takes.</summary>
    public string? ProgressId { get; set; }
}

/// <summary>The answer to an apply: how each file ended, where it stopped, and the audit log entry.</summary>
public sealed class ApplyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<ApplyFileRecord> Applied { get; set; } = [];
    /// <summary>"folder\objects\file.sql" of the file that failed, or of the first one not run after a stop.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? StoppedAt { get; set; }
    public string AuditId { get; set; } = "";
}
