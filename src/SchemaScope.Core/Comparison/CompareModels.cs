using SchemaScope.Core.Model;

namespace SchemaScope.Core.Comparison;

public enum ObjectStatus
{
    /// <summary>Byte for byte the same after light normalization.</summary>
    Same = 0,
    /// <summary>Only layout, comments or bracket quoting moved. No behaviour change.</summary>
    FormattingOnly = 1,
    /// <summary>A real difference.</summary>
    Different = 2,
    /// <summary>Exists in the source, not in this target.</summary>
    MissingInTarget = 3,
    /// <summary>Exists in this target but not in the source. Usually a local customisation.</summary>
    OnlyInTarget = 4,
    /// <summary>Encrypted or no VIEW DEFINITION permission, so we cannot say.</summary>
    Unavailable = 5
}

public static class ObjectStatusExtensions
{
    /// <summary>Higher wins when rolling several targets up into one row verdict.</summary>
    public static int Severity(this ObjectStatus s) => s switch
    {
        ObjectStatus.Same => 0,
        ObjectStatus.FormattingOnly => 1,
        ObjectStatus.Unavailable => 2,
        ObjectStatus.Different => 3,
        ObjectStatus.OnlyInTarget => 4,
        ObjectStatus.MissingInTarget => 5,
        _ => 0
    };

    public static bool IsDifference(this ObjectStatus s) => s != ObjectStatus.Same;
}

public sealed class TargetCell
{
    public ObjectStatus Status { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public int ChangedLines => AddedLines + RemovedLines;
    /// <summary>Short plain-English reason, shown on hover.</summary>
    public string? Note { get; set; }
}

public sealed class CompareRow
{
    public string Key { get; set; } = "";
    public DbObjectType Type { get; set; }
    public string TypeLabel => Type.Label();
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName => $"{Schema}.{Name}";
    public string? ParentName { get; set; }
    /// <summary>One cell per target, in the same order as CompareResult.Targets.</summary>
    public List<TargetCell> Cells { get; set; } = [];
    public ObjectStatus Worst { get; set; }
    public bool HasDifference => Worst != ObjectStatus.Same;
    /// <summary>Number of targets where this object is not identical to the source.</summary>
    public int DifferingTargets { get; set; }
}

public sealed class TargetSummary
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string Collation { get; set; } = "";

    public int Same { get; set; }
    public int FormattingOnly { get; set; }
    public int Different { get; set; }
    public int MissingInTarget { get; set; }
    public int OnlyInTarget { get; set; }
    public int Unavailable { get; set; }

    public int TotalObjects { get; set; }
    public int TotalDifferences => FormattingOnly + Different + MissingInTarget + OnlyInTarget + Unavailable;

    /// <summary>Share of compared objects that are identical, 0-100.</summary>
    public double MatchPercent { get; set; }

    public long ReadMs { get; set; }
    public DateTime? LastObjectChangeUtc { get; set; }
    public string? Error { get; set; }
    public bool Failed => !string.IsNullOrEmpty(Error);
}

public sealed class SnapshotInfo
{
    public string Label { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public string Collation { get; set; } = "";
    public int ObjectCount { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public long ReadMs { get; set; }
}

public sealed class TypeBreakdown
{
    public DbObjectType Type { get; set; }
    public string Label => Type.Label();
    public int Total { get; set; }
    public int WithDifferences { get; set; }
}

public sealed class CompareResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public long TotalMs { get; set; }

    public SnapshotInfo Source { get; set; } = new();
    public List<TargetSummary> Targets { get; set; } = [];
    public List<CompareRow> Rows { get; set; } = [];
    public List<TypeBreakdown> ByType { get; set; } = [];

    /// <summary>Stage timings, so the user can always see where the seconds went.</summary>
    public Dictionary<string, long> Timings { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public int TotalRows => Rows.Count;
    public int RowsWithDifferences => Rows.Count(r => r.HasDifference);

    /// <summary>
    /// Objects that differ in exactly one target. Almost always a local
    /// customisation rather than a missed deployment - worth calling out.
    /// </summary>
    public int SingleTargetDrift => Rows.Count(r => r.DifferingTargets == 1);
}
