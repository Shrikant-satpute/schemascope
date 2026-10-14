using System.Text.Json;
using System.Text.Json.Serialization;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Tests;

/// <summary>
/// The "objects by type" panel must not call a missing or extra object a
/// difference: the per-type counts are split by each row's worst status.
/// </summary>
public class TypeCountTests
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

    [Fact]
    public void Per_type_counts_split_differences_from_missing_and_extra()
    {
        var source = Snapshot("src",
            Proc("Same", "CREATE PROCEDURE dbo.Same AS SELECT 1;"),
            Proc("Changed", "CREATE PROCEDURE dbo.Changed AS SELECT a FROM dbo.T;"),
            Proc("Reformatted", "CREATE PROCEDURE dbo.Reformatted AS SELECT a FROM dbo.T;"),
            Proc("OnlySource", "CREATE PROCEDURE dbo.OnlySource AS SELECT 1;"));
        var target = Snapshot("tgt",
            Proc("Same", "CREATE PROCEDURE dbo.Same AS SELECT 1;"),
            Proc("Changed", "CREATE PROCEDURE dbo.Changed AS SELECT a FROM dbo.Other;"),
            Proc("Reformatted", "CREATE PROCEDURE [dbo].[Reformatted]\nAS\n  SELECT [a] FROM [dbo].[T];"),
            Proc("OnlyTarget", "CREATE PROCEDURE dbo.OnlyTarget AS SELECT 1;"));

        var b = new CompareEngine(new CompareOptions()).Compare(source, [target]).ByType.Single();

        Assert.Equal(5, b.Total);
        Assert.Equal(4, b.WithDifferences);
        Assert.Equal(1, b.Different);
        Assert.Equal(1, b.FormattingOnly);
        Assert.Equal(1, b.MissingInTarget);
        Assert.Equal(1, b.OnlyInTarget);
        Assert.Equal(0, b.Unavailable);
    }

    [Fact]
    public void Snapshots_saved_before_the_split_still_read_back()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        const string old = """{"byType":[{"type":"storedProcedure","total":677,"withDifferences":25}]}""";

        var b = JsonSerializer.Deserialize<CompareResult>(old, options)!.ByType.Single();

        Assert.Equal(677, b.Total);
        Assert.Equal(25, b.WithDifferences);
        Assert.Equal(0, b.Different + b.FormattingOnly + b.MissingInTarget + b.OnlyInTarget + b.Unavailable);
    }
}
