namespace SchemaScope.Core.Storage;

/// <summary>
/// A comparison that has been published for other people on the network to read.
///
/// This is the index card only - who compared what, when, and how long the link
/// lives. The comparison itself is a compressed file beside the database, because
/// it carries every object's text and has no business in a table.
/// </summary>
public sealed class ShareRecord
{
    /// <summary>The random part of the link. Whoever has it can read the comparison.</summary>
    public string Token { get; set; } = "";

    public string SourceLabel { get; set; } = "";
    /// <summary>Server and database, so a link is recognisable at a glance.</summary>
    public string SourceDetail { get; set; } = "";
    public List<string> TargetLabels { get; set; } = [];

    /// <summary>When the comparison itself ran - not when it was shared.</summary>
    public DateTime ComparedUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddDays(7);

    public int ObjectCount { get; set; }
    public int TotalViews { get; set; }
}
