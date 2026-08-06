using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Tests;

/// <summary>
/// Six statuses, and the promise that nothing is ever hidden: an object the
/// tool cannot read has to say so rather than be reported as a difference.
/// </summary>
public class CompareEngineTests
{
    private static DbObject Proc(string name, string body) => new()
    {
        Type = DbObjectType.StoredProcedure,
        Schema = "dbo",
        Name = name,
        Definition = body
    };

    private static SchemaSnapshot Snapshot(string label, params DbObject[] objects)
    {
        var snapshot = new SchemaSnapshot { Label = label, Server = "srv", Database = label };
        snapshot.Objects.AddRange(objects);
        new SchemaNormalizer(new CompareOptions()).Normalize(snapshot);
        return snapshot;
    }

    private static ObjectStatus StatusOf(SchemaSnapshot source, SchemaSnapshot target, string name)
    {
        var result = new CompareEngine(new CompareOptions()).Compare(source, [target]);
        return result.Rows.Single(r => r.Name == name).Cells[0].Status;
    }

    [Fact]
    public void Matching_objects_are_same()
    {
        const string body = "CREATE PROCEDURE dbo.P AS SELECT 1;";
        Assert.Equal(ObjectStatus.Same,
            StatusOf(Snapshot("src", Proc("P", body)), Snapshot("tgt", Proc("P", body)), "P"));
    }

    [Fact]
    public void Reformatted_objects_are_formatting_only()
    {
        Assert.Equal(ObjectStatus.FormattingOnly, StatusOf(
            Snapshot("src", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT a FROM dbo.T;")),
            Snapshot("tgt", Proc("P", "CREATE PROCEDURE [dbo].[P]\nAS\n  SELECT [a] FROM [dbo].[T];")),
            "P"));
    }

    [Fact]
    public void Changed_objects_are_different()
    {
        Assert.Equal(ObjectStatus.Different, StatusOf(
            Snapshot("src", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT a FROM dbo.T;")),
            Snapshot("tgt", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT a FROM dbo.SomewhereElse;")),
            "P"));
    }

    [Fact]
    public void An_object_the_target_lacks_is_missing()
    {
        Assert.Equal(ObjectStatus.MissingInTarget, StatusOf(
            Snapshot("src", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT 1;")),
            Snapshot("tgt"),
            "P"));
    }

    [Fact]
    public void An_object_only_the_target_has_still_gets_a_row()
    {
        Assert.Equal(ObjectStatus.OnlyInTarget, StatusOf(
            Snapshot("src"),
            Snapshot("tgt", Proc("Local", "CREATE PROCEDURE dbo.Local AS SELECT 1;")),
            "Local"));
    }

    [Fact]
    public void An_unreadable_body_is_reported_as_unreadable_not_as_a_difference()
    {
        var encrypted = Proc("P", "CREATE PROCEDURE dbo.P AS SELECT 1;");
        encrypted.IsEncrypted = true;

        Assert.Equal(ObjectStatus.Unavailable, StatusOf(
            Snapshot("src", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT 2;")),
            Snapshot("tgt", encrypted),
            "P"));
    }

    [Fact]
    public void A_target_that_could_not_be_read_does_not_sink_the_run()
    {
        var source = Snapshot("src", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT 1;"));
        var dead = new SchemaSnapshot { Label = "down", Error = "Could not connect" };
        var alive = Snapshot("live", Proc("P", "CREATE PROCEDURE dbo.P AS SELECT 1;"));

        var result = new CompareEngine(new CompareOptions()).Compare(source, [dead, alive]);
        var row = result.Rows.Single(r => r.Name == "P");

        Assert.Equal(ObjectStatus.Unavailable, row.Cells[0].Status);
        Assert.Equal(ObjectStatus.Same, row.Cells[1].Status);
    }
}
