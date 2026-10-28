using System.Text.Json.Serialization;
using SchemaScope.Core.Engines;

namespace SchemaScope.Core.Model;

/// <summary>
/// One "Apply to target" run, as the local audit log keeps it: where it went, who it signed in as, what was
/// ticked and how each file ended. No connection string, password or script text is ever stored here.
/// </summary>
public sealed class ApplyRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTime? FinishedUtc { get; set; }
    /// <summary>The compare run the plan came from.</summary>
    public string CompareRunId { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public DatabaseEngine Engine { get; set; } = DatabaseEngine.SqlServer;
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    /// <summary>"Windows: DOMAIN\user" or "SQL login: name".</summary>
    public string Login { get; set; } = "";
    public List<string> Sections { get; set; } = [];
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? StoppedAt { get; set; }
    public List<ApplyFileRecord> Files { get; set; } = [];
}

/// <summary>How one script file ended: committed (<see cref="Ok"/>), or rolled back with <see cref="Error"/>.</summary>
public sealed class ApplyFileRecord
{
    public string Folder { get; set; } = "";
    /// <summary>The file inside its folder, e.g. "objects\001_table_dbo.T.sql".</summary>
    public string File { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public bool Ok { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Error { get; set; }
    public long ElapsedMs { get; set; }
    /// <summary>For engines that run one statement per file: which statement it was. Null for SQL Server.</summary>
    public int? StatementIndex { get; set; }
}
