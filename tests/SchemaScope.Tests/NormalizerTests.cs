using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Tests;

/// <summary>
/// The hashes are the whole compare: if two objects hash alike they are never
/// looked at again, so a wrong hash silently hides a real schema difference.
/// </summary>
public class NormalizerTests
{
    private static DbObject Proc(string body) => new()
    {
        Type = DbObjectType.StoredProcedure,
        Schema = "dbo",
        Name = "DoThing",
        Definition = body
    };

    private static DbObject Normalized(string body)
    {
        var o = Proc(body);
        new SchemaNormalizer(new CompareOptions()).NormalizeObject(o);
        return o;
    }

    [Fact]
    public void Identical_bodies_hash_alike()
    {
        const string body = "CREATE PROCEDURE dbo.DoThing AS SELECT 1;";
        Assert.Equal(Normalized(body).Hash, Normalized(body).Hash);
    }

    [Fact]
    public void Different_bodies_hash_differently()
    {
        var a = Normalized("CREATE PROCEDURE dbo.DoThing AS SELECT 1;");
        var b = Normalized("CREATE PROCEDURE dbo.DoThing AS SELECT 2;");

        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.LooseHash, b.LooseHash);
    }

    [Theory]
    // whitespace and line breaks
    [InlineData("CREATE PROCEDURE dbo.DoThing AS SELECT 1;",
                "CREATE   PROCEDURE\n  dbo.DoThing\nAS\n    SELECT 1;")]
    // bracket quoting
    [InlineData("CREATE PROCEDURE dbo.DoThing AS SELECT a FROM dbo.T;",
                "CREATE PROCEDURE [dbo].[DoThing] AS SELECT [a] FROM [dbo].[T];")]
    // keyword casing
    [InlineData("CREATE PROCEDURE dbo.DoThing AS SELECT 1;",
                "create procedure dbo.DoThing as select 1;")]
    // trailing comment
    [InlineData("CREATE PROCEDURE dbo.DoThing AS SELECT 1;",
                "CREATE PROCEDURE dbo.DoThing AS SELECT 1; -- reviewed 2024")]
    public void Formatting_changes_survive_the_loose_hash(string left, string right)
    {
        var a = Normalized(left);
        var b = Normalized(right);

        Assert.Equal(a.LooseHash, b.LooseHash);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Loose_hash_still_separates_a_real_change_behind_reformatting()
    {
        var a = Normalized("CREATE PROCEDURE dbo.DoThing AS SELECT a FROM dbo.T;");
        var b = Normalized("CREATE   PROCEDURE [dbo].[DoThing]\nAS\n  SELECT a FROM dbo.OtherTable;");

        Assert.NotEqual(a.LooseHash, b.LooseHash);
    }

    [Theory]
    // Bracket quoting is deliberately absent here: the loose hash strips
    // brackets a tier earlier, so those never reach the parser at all. What is
    // left for this check is the optional syntax that survives loosening.
    [InlineData("SELECT a FROM dbo.T;", "SELECT a FROM dbo.T")]
    [InlineData("SELECT a FROM dbo.T AS x;", "SELECT a FROM dbo.T x;")]
    [InlineData("SELECT a FROM dbo.T INNER JOIN dbo.U ON 1 = 1;",
                "SELECT a FROM dbo.T JOIN dbo.U ON 1 = 1;")]
    [InlineData("SELECT a, b FROM dbo.T WHERE a = 1;",
                "select   a,\n       b\nfrom dbo.T\nwhere a = 1")]
    public void Parses_identically_sees_through_optional_syntax(string left, string right)
    {
        Assert.True(SchemaNormalizer.ParsesIdentically(left, right));
    }

    [Fact]
    public void Parses_identically_rejects_a_real_difference()
    {
        Assert.False(SchemaNormalizer.ParsesIdentically(
            "SELECT a FROM dbo.T WHERE a = 1;",
            "SELECT a FROM dbo.T WHERE a = 2;"));
    }

    [Fact]
    public void Parses_identically_never_guesses_when_the_text_will_not_parse()
    {
        Assert.False(SchemaNormalizer.ParsesIdentically("SELECT ((( FROM", "SELECT ((( FROM"));
        Assert.False(SchemaNormalizer.ParsesIdentically(null, "SELECT 1"));
        Assert.False(SchemaNormalizer.ParsesIdentically("  ", "SELECT 1"));
    }
}
