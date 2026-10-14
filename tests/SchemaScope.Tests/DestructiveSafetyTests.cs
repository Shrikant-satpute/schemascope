using Microsoft.Data.SqlClient;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>
/// 11_destructive on throwaway LocalDB pairs (SchemaScope_Test_&lt;id&gt;_Src / _Tgt, dropped afterwards): what it must
/// never drop, the order implicit drops need, and the data-fit check repeated when a file runs.
/// </summary>
public sealed class DestructiveSafetyLocalDbTests
{
    private sealed class Pair : IDisposable
    {
        public string Src { get; }
        public string Tgt { get; }

        public Pair(string srcDdl, string tgtDdl, string srcCollation = "Latin1_General_CI_AS", string tgtCollation = "Latin1_General_CI_AS")
        {
            var id = Guid.NewGuid().ToString("n")[..8];
            Src = LocalDb.NewName(id, "Src");
            Tgt = LocalDb.NewName(id, "Tgt");
            try
            {
                DestructiveDb.Run("master", $"CREATE DATABASE {Ddl.Bracket(Src)} COLLATE {srcCollation}");
                DestructiveDb.Run("master", $"CREATE DATABASE {Ddl.Bracket(Tgt)} COLLATE {tgtCollation}");
                DestructiveDb.Run(Src, srcDdl);
                DestructiveDb.Run(Tgt, tgtDdl);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Task<ScriptOutcome> Generate(string folder) =>
            new ScriptGenerator().GenerateAsync(
                new ScriptEndpoint("Src", LocalDb.Server, Src, DestructiveDb.Cs(Src)),
                new ScriptEndpoint("Tgt", LocalDb.Server, Tgt, DestructiveDb.Cs(Tgt)),
                new ScriptOptions { OutputFolder = folder, IncludeDestructive = true, RunFolderName = "run" },
                new HashSet<string>(), default);

        public void Dispose()
        {
            LocalDb.Drop(Src);
            LocalDb.Drop(Tgt);
        }
    }

    private static List<WrittenFile> Destructive(ScriptOutcome o) =>
        o.Files.Where(f => f.Folder == DestructiveGenerator.Folder && f.IsObjectFile).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();

    [LocalDbFact]
    public async Task A_case_sensitive_source_and_a_case_insensitive_target_drop_nothing_the_source_has_in_another_case()
    {
        using var db = new Pair("""
            CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, Name nvarchar(50) NULL);
            CREATE TABLE dbo.Cust (Id int NOT NULL CONSTRAINT PK_Cust PRIMARY KEY, email nvarchar(50) NULL);
            GO
            CREATE PROCEDURE dbo.GetOrders AS SELECT Id FROM dbo.Orders;
            """, """
            CREATE TABLE dbo.orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, name nvarchar(50) NULL);
            CREATE TABLE dbo.Cust (Id int NOT NULL CONSTRAINT PK_Cust PRIMARY KEY, Email nvarchar(50) NULL);
            INSERT dbo.orders VALUES (1, N'a'), (2, N'b');
            GO
            CREATE PROCEDURE dbo.getorders AS SELECT Id FROM dbo.orders;
            """, srcCollation: "Latin1_General_CS_AS");
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Empty(Destructive(outcome));
        Assert.Equal(2, DestructiveDb.Scalar(db.Tgt, "SELECT COUNT(*) FROM dbo.orders"));
    }

    [LocalDbFact]
    public async Task A_case_sensitive_target_holds_a_drop_whose_name_the_source_has_in_another_case()
    {
        using var db = new Pair("""
            CREATE TABLE dbo.orders (Id int NOT NULL CONSTRAINT PK_orders PRIMARY KEY);
            CREATE TABLE dbo.Cust (Id int NOT NULL CONSTRAINT PK_Cust PRIMARY KEY, email nvarchar(50) NULL);
            """, """
            CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY);
            CREATE TABLE dbo.Cust (Id int NOT NULL CONSTRAINT PK_Cust PRIMARY KEY, Email nvarchar(50) NULL);
            INSERT dbo.Orders VALUES (1);
            """, tgtCollation: "Latin1_General_CS_AS");
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        Assert.DoesNotContain(Destructive(outcome), f => f.RelativePath.Contains("drop_table_Orders") || f.RelativePath.Contains("Email"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "dbo.Orders" && h.Reason.Contains("another letter case"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "Cust.Email" && h.Reason.Contains("another letter case"));
    }

    [LocalDbFact]
    public async Task Case_twin_sequences_and_synonyms_are_held_not_dropped()
    {
        using var db = new Pair("""
            CREATE SEQUENCE dbo.OrderSeq AS int START WITH 1;
            CREATE SYNONYM dbo.MySyn FOR dbo.Nowhere;
            """, """
            CREATE SEQUENCE dbo.orderseq AS int START WITH 5000;
            CREATE SYNONYM dbo.mysyn FOR dbo.Nowhere;
            """, tgtCollation: "Latin1_General_CS_AS");
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Empty(Destructive(outcome));
        Assert.Contains(outcome.HeldBack, h => h.Object == "dbo.orderseq" && h.Reason.Contains("another letter case"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "dbo.mysyn" && h.Reason.Contains("another letter case"));
    }

    [LocalDbFact]
    public async Task A_table_whose_trigger_is_held_is_held_too()
    {
        using var db = new Pair("""
            CREATE TABLE dbo.Keep (Id int NOT NULL CONSTRAINT PK_Keep PRIMARY KEY);
            """, """
            CREATE TABLE dbo.Keep (Id int NOT NULL CONSTRAINT PK_Keep PRIMARY KEY);
            CREATE TABLE dbo.Gone (Id int NOT NULL CONSTRAINT PK_Gone PRIMARY KEY);
            CREATE TABLE dbo.Plain (Id int NOT NULL CONSTRAINT PK_Plain PRIMARY KEY);
            GO
            CREATE TRIGGER dbo.tr_gone ON dbo.Gone WITH ENCRYPTION AFTER INSERT AS SET NOCOUNT ON;
            GO
            CREATE TRIGGER dbo.tr_plain ON dbo.Plain AFTER INSERT AS SET NOCOUNT ON;
            """);
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        var files = Destructive(outcome);
        Assert.DoesNotContain(files, f => f.RelativePath.Contains("Gone", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(outcome.HeldBack, h => h.Object == "dbo.tr_gone" && h.Reason.Contains("encrypted"));
        Assert.Contains(outcome.HeldBack, h => h.Object == "dbo.Gone" && h.Reason.Contains("trigger dbo.tr_gone"));
        // a trigger that can go goes first, then its table
        int At(string part) => files.FindIndex(f => f.RelativePath.Contains(part, StringComparison.Ordinal));
        Assert.True(At("drop_trigger_tr_plain") >= 0 && At("drop_trigger_tr_plain") < At("drop_table_Plain"),
            string.Join(", ", files.Select(f => f.RelativePath)));
    }

    [LocalDbFact]
    public async Task A_view_goes_after_its_instead_of_trigger_and_xml_index_drops_tolerate_each_other()
    {
        using var db = new Pair("""
            CREATE TABLE dbo.Doc (Id int NOT NULL CONSTRAINT PK_Doc PRIMARY KEY, Body xml NULL);
            """, """
            CREATE TABLE dbo.Doc (Id int NOT NULL CONSTRAINT PK_Doc PRIMARY KEY, Body xml NULL);
            CREATE PRIMARY XML INDEX PXML_Doc ON dbo.Doc (Body);
            CREATE XML INDEX AXML_Doc_path ON dbo.Doc (Body) USING XML INDEX PXML_Doc FOR PATH;
            GO
            CREATE VIEW dbo.a_view AS SELECT Id FROM dbo.Doc;
            GO
            CREATE TRIGGER dbo.tr_a_view ON dbo.a_view INSTEAD OF INSERT AS SET NOCOUNT ON;
            """);
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(outcome.Validation.Passed, string.Join("; ", outcome.Validation.Problems));
        var files = Destructive(outcome);
        int At(string part) => files.FindIndex(f => f.RelativePath.Contains(part, StringComparison.Ordinal));
        Assert.True(At("drop_trigger_tr_a_view") >= 0 && At("drop_trigger_tr_a_view") < At("drop_view_a_view"),
            string.Join(", ", files.Select(f => f.RelativePath)));
        Assert.True(At("PXML_Doc") >= 0 && At("AXML_Doc_path") >= 0);

        // every file runs, in name order, as run_all.ps1 runs them
        foreach (var f in files) DestructiveDb.Run(db.Tgt, ScriptWriter.ReadBack(f.FullPath));
        Assert.Null(DestructiveDb.Scalar(db.Tgt, "SELECT OBJECT_ID('dbo.a_view')"));
        Assert.Null(DestructiveDb.Scalar(db.Tgt, "SELECT 1 FROM sys.indexes WHERE type = 3"));

        // and the rollback brings the view back before its trigger
        DestructiveDb.Run(db.Tgt, ScriptWriter.ReadBack(Path.Combine(outcome.Path!, DestructiveGenerator.Folder, "rollback", "_ALL.sql")));
        Assert.NotNull(DestructiveDb.Scalar(db.Tgt, "SELECT OBJECT_ID('dbo.tr_a_view')"));
    }

    [LocalDbFact]
    public async Task A_column_change_checked_at_generation_refuses_a_row_written_since_instead_of_rounding_it()
    {
        using var db = new Pair("""
            CREATE TABLE dbo.Amounts (Id int NOT NULL CONSTRAINT PK_Amounts PRIMARY KEY, amt decimal(10,2) NULL);
            """, """
            CREATE TABLE dbo.Amounts (Id int NOT NULL CONSTRAINT PK_Amounts PRIMARY KEY, amt decimal(10,4) NULL);
            INSERT dbo.Amounts VALUES (1, 1.23);
            """);
        using var temp = new TempFolder();

        var outcome = await db.Generate(temp.Path);
        Assert.True(outcome.Success, outcome.Message);
        Assert.True(outcome.Validation.Passed, string.Join("; ", outcome.Validation.Problems));
        var file = Assert.Single(Destructive(outcome), f => f.RelativePath.Contains("alter_column_Amounts_amt"));
        var text = ScriptWriter.ReadBack(file.FullPath);
        Assert.Contains("Checked again when this file runs", text);

        // a row that would be rounded to 1.23 arrives after the scripts were generated
        DestructiveDb.Run(db.Tgt, "INSERT dbo.Amounts VALUES (2, 1.2345)");
        var ex = Assert.Throws<SqlException>(() => DestructiveDb.Run(db.Tgt, text));
        Assert.Contains("no longer fit", ex.Message);
        Assert.Equal(1.2345m, DestructiveDb.Scalar(db.Tgt, "SELECT amt FROM dbo.Amounts WHERE Id = 2"));
        Assert.Equal((byte)4, DestructiveDb.Scalar(db.Tgt, "SELECT scale FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Amounts') AND name = 'amt'"));

        // without it, the file runs as generated
        DestructiveDb.Run(db.Tgt, "DELETE dbo.Amounts WHERE Id = 2");
        DestructiveDb.Run(db.Tgt, text);
        Assert.Equal((byte)2, DestructiveDb.Scalar(db.Tgt, "SELECT scale FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Amounts') AND name = 'amt'"));
    }
}
