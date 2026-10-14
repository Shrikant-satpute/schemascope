namespace SchemaScope.Core.Model;

/// <summary>One database to read, plus how to reach it.</summary>
public sealed class SchemaSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    /// <summary>Friendly name shown everywhere in the UI, e.g. "Davi (prod)".</summary>
    public string Label { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";

    /// <summary>The saved connection this came from, if any: applying re-reads its "Protected" flag.</summary>
    public string? ConnectionId { get; set; }

    /// <summary>The connection was marked "Protected - never apply scripts here" when the compare started.</summary>
    public bool ProtectedServer { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) ? $"{Server}.{Database}" : Label;
}

public sealed record ReadProgress(string SourceId, string Stage, int Percent, string? Detail = null);

public interface ISchemaReader
{
    Task<SchemaSnapshot> ReadAsync(
        SchemaSource source,
        CompareOptions options,
        IProgress<ReadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Thrown when we can reach the server but cannot do the job properly.</summary>
public sealed class SchemaReadException(string message, string? hint = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Plain-English next step for the user, e.g. the GRANT they need.</summary>
    public string? Hint { get; } = hint;
}
