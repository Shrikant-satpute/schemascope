using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace SchemaScope.Core.Comparison;

public sealed record LineChangeCount(int Added, int Removed);

public sealed record DiffLine(int? LeftLine, int? RightLine, string Kind, string Text);

/// <summary>
/// Line level diffing. Only ever runs on the small set of objects that actually
/// differ - never on the whole database.
/// </summary>
public static class LineDiffService
{
    public static LineChangeCount CountChanges(string? left, string? right)
    {
        left ??= "";
        right ??= "";
        if (string.Equals(left, right, StringComparison.Ordinal)) return new LineChangeCount(0, 0);

        var result = Differ.Instance.CreateLineDiffs(left, right, ignoreWhitespace: false);

        var added = 0;
        var removed = 0;
        foreach (var block in result.DiffBlocks)
        {
            removed += block.DeleteCountA;
            added += block.InsertCountB;
        }

        return new LineChangeCount(added, removed);
    }

    /// <summary>
    /// Side by side model with real line numbers on both sides. The UI uses
    /// Monaco for the main view; this powers reports and the plain text export.
    /// </summary>
    public static (List<DiffLine> Left, List<DiffLine> Right) BuildSideBySide(string? left, string? right)
    {
        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(left ?? "", right ?? "");
        return (Map(model.OldText.Lines), Map(model.NewText.Lines));
    }

    private static List<DiffLine> Map(List<DiffPiece> pieces)
    {
        var list = new List<DiffLine>(pieces.Count);
        foreach (var p in pieces)
        {
            var kind = p.Type switch
            {
                ChangeType.Inserted => "added",
                ChangeType.Deleted => "removed",
                ChangeType.Modified => "modified",
                ChangeType.Imaginary => "blank",
                _ => "same"
            };
            list.Add(new DiffLine(p.Position, p.Position, kind, p.Text ?? ""));
        }
        return list;
    }
}
