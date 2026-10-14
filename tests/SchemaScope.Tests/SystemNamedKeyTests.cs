using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;

namespace SchemaScope.Tests;

/// <summary>
/// PK__Table__3213E83F... names are random per server, and sp_rename leaves the
/// old name in a module's CREATE header. Neither may count as a difference on
/// its own - but a real name or a real body change still must.
/// </summary>
public class SystemNamedKeyTests
{
    private static IndexInfo Key(string name, bool systemNamed, bool primary = true, params string[] cols) => new()
    {
        Name = name,
        TypeDesc = "CLUSTERED",
        IsUnique = true,
        IsPrimaryKey = primary,
        IsUniqueConstraint = !primary,
        IsSystemNamed = systemNamed,
        KeyColumns = [.. (cols.Length == 0 ? ["id"] : cols).Select((c, i) => new IndexColumnInfo { Name = c, Ordinal = i + 1 })]
    };

    private static DbObject Table(params IndexInfo[] keys) => new()
    {
        Type = DbObjectType.Table,
        Schema = "dbo",
        Name = "audit_log",
        Columns =
        [
            new ColumnInfo { Ordinal = 1, Name = "id", DataType = "int" },
            new ColumnInfo { Ordinal = 2, Name = "code", DataType = "int" }
        ],
        Indexes = [.. keys]
    };

    private static string Script(DbObject o, bool ignore = true) =>
        CanonicalScriptWriter.Write(o, new CompareOptions { IgnoreSystemNamedConstraints = ignore });

    [Fact]
    public void System_named_primary_key_name_is_ignored_when_option_on()
    {
        var a = Table(Key("PK__audit_lo__3213E83F1A2B3C4D", true));
        var b = Table(Key("PK__audit_lo__3213E83F99887766", true));

        Assert.Equal(Script(a), Script(b));
        Assert.DoesNotContain("PK__", Script(a));
        Assert.Contains("PRIMARY KEY CLUSTERED ([id] ASC)", Script(a));
    }

    [Fact]
    public void System_named_primary_key_name_counts_when_option_off()
    {
        var a = Table(Key("PK__audit_lo__3213E83F1A2B3C4D", true));
        var b = Table(Key("PK__audit_lo__3213E83F99887766", true));

        Assert.NotEqual(Script(a, ignore: false), Script(b, ignore: false));
    }

    [Fact]
    public void User_named_key_names_are_still_compared()
    {
        var a = Table(Key("PK_audit_log", false));
        var b = Table(Key("PK_audit_log_v2", false));

        Assert.NotEqual(Script(a), Script(b));
        Assert.Contains("CONSTRAINT [PK_audit_log] PRIMARY KEY", Script(a));
    }

    [Fact]
    public void System_named_vs_user_named_key_is_a_difference()
    {
        var a = Table(Key("PK__audit_lo__3213E83F1A2B3C4D", true));
        var b = Table(Key("PK_audit_log", false));

        Assert.NotEqual(Script(a), Script(b));
    }

    [Fact]
    public void Unique_key_order_does_not_follow_the_random_name()
    {
        // UQ__...AAAA sorts before UQ__...ZZZZ on one side, after it on the other
        var a = Table(Key("UQ__audit_lo__AAAA", true, false, "code"), Key("UQ__audit_lo__ZZZZ", true, false, "id"));
        var b = Table(Key("UQ__audit_lo__ZZZZ", true, false, "code"), Key("UQ__audit_lo__AAAA", true, false, "id"));

        Assert.Equal(Script(a), Script(b));
    }

    [Fact]
    public void Table_type_primary_key_name_is_ignored()
    {
        var a = Table(Key("PK__TT_ids__3213E83F0001", true));
        var b = Table(Key("PK__TT_ids__3213E83F0002", true));
        a.Type = b.Type = DbObjectType.TableType;

        Assert.Equal(Script(a), Script(b));
    }

    [Fact]
    public void Structure_grid_matches_system_named_keys_by_columns()
    {
        var a = Table(Key("PK__audit_lo__3213E83F1A2B3C4D", true));
        var b = Table(Key("PK__audit_lo__3213E83F99887766", true));

        var keys = StructureDiffService.Build(a, b, new CompareOptions())
            .Single(s => s.Title == "Indexes & keys");

        var row = Assert.Single(keys.Rows);
        Assert.Equal("same", row.Status);
    }

    [Fact]
    public void Structure_grid_keys_by_name_when_option_off()
    {
        var a = Table(Key("PK__audit_lo__3213E83F1A2B3C4D", true));
        var b = Table(Key("PK__audit_lo__3213E83F99887766", true));

        var keys = StructureDiffService.Build(a, b, new CompareOptions { IgnoreSystemNamedConstraints = false })
            .Single(s => s.Title == "Indexes & keys");

        Assert.Equal(2, keys.ChangedCount);
    }

    // ---- module header name ----

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.kn_EY_COM_TRAN_Get AS SELECT 1;")]
    [InlineData("CREATE PROCEDURE [dbo].[kn_EY_COM_TRAN_Get] AS SELECT 1;")]
    [InlineData("CREATE PROCEDURE kn_EY_COM_TRAN_Get AS SELECT 1;")]
    [InlineData("CREATE PROC [dbo] . kn_EY_COM_TRAN_Get AS SELECT 1;")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.kn_EY_COM_TRAN_Get AS SELECT 1;")]
    [InlineData("ALTER PROCEDURE \"dbo\".\"kn_EY_COM_TRAN_Get\" AS SELECT 1;")]
    public void Header_name_is_rewritten_to_catalog_name(string sql)
    {
        var result = TSqlText.CanonicalHeaderName(sql, "dbo", "kn_EY_COM_TRN_Get");

        Assert.EndsWith(" [dbo].[kn_EY_COM_TRN_Get] AS SELECT 1;", result);
        Assert.StartsWith("CREATE PROC", result);
    }

    [Fact]
    public void Comments_before_create_are_kept_and_skipped()
    {
        const string sql = "-- renamed\n/* old: kn_EY_COM_TRAN_Get */\nCREATE PROCEDURE dbo.kn_EY_COM_TRAN_Get AS SELECT 1;";

        Assert.Equal(
            "-- renamed\n/* old: kn_EY_COM_TRAN_Get */\nCREATE PROCEDURE [dbo].[kn_EY_COM_TRN_Get] AS SELECT 1;",
            TSqlText.CanonicalHeaderName(sql, "dbo", "kn_EY_COM_TRN_Get"));
    }

    [Fact]
    public void Only_the_header_name_is_touched()
    {
        const string sql = "CREATE TRIGGER dbo.trOld ON dbo.T AFTER INSERT AS UPDATE dbo.trOld SET a = 1;";

        Assert.Equal(
            "CREATE TRIGGER [dbo].[trNew] ON dbo.T AFTER INSERT AS UPDATE dbo.trOld SET a = 1;",
            TSqlText.CanonicalHeaderName(sql, "dbo", "trNew"));
    }

    [Fact]
    public void Non_module_text_is_left_alone()
    {
        const string sql = "CREATE TABLE dbo.T (a int);";
        Assert.Equal(sql, TSqlText.CanonicalHeaderName(sql, "dbo", "X"));
    }

    [Fact]
    public void Renamed_proc_with_stale_header_compares_same()
    {
        var dev = new DbObject
        {
            Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = "kn_EY_COM_TRN_Get",
            Definition = "CREATE PROCEDURE [dbo].[kn_EY_COM_TRAN_Get]\nAS\nSELECT 1;"
        };
        var client = new DbObject
        {
            Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = "kn_EY_COM_TRN_Get",
            Definition = "CREATE PROCEDURE [dbo].[kn_EY_COM_TRN_Get]\nAS\nSELECT 1;"
        };
        var n = new SchemaNormalizer(new CompareOptions());
        n.NormalizeObject(dev);
        n.NormalizeObject(client);

        Assert.Equal(dev.Hash, client.Hash);
        // the diff view still shows the real text
        Assert.Contains("kn_EY_COM_TRAN_Get", dev.DisplayText);
    }

    [Fact]
    public void Body_change_still_differs_after_header_rewrite()
    {
        var a = new DbObject { Type = DbObjectType.View, Schema = "dbo", Name = "v", Definition = "CREATE VIEW dbo.vOld AS SELECT 1 AS x;" };
        var b = new DbObject { Type = DbObjectType.View, Schema = "dbo", Name = "v", Definition = "CREATE VIEW dbo.v AS SELECT 2 AS x;" };
        var n = new SchemaNormalizer(new CompareOptions());
        n.NormalizeObject(a);
        n.NormalizeObject(b);

        Assert.NotEqual(a.Hash, b.Hash);
        Assert.NotEqual(a.LooseHash, b.LooseHash);
    }
}
