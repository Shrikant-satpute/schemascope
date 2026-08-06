using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;

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
