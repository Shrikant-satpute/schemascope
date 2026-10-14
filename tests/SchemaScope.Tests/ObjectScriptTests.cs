using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>
/// Two small catalogs with differences of every kind. The generator reads them from memory; every probe points at a
/// refused loopback port, so it fails at once and reports "not checked" - no database is involved.
/// </summary>
internal static class ObjectFixture
{
    public const string Coll = "SQL_Latin1_General_CP1_CI_AS";
    private const string Nowhere = "Data Source=tcp:127.0.0.1,1;Initial Catalog=nowhere;Integrated Security=true;Connect Timeout=1;Encrypt=false";

    public static ScriptEndpoint Source { get; } = new("Dev", "127.0.0.1,1", "src", Nowhere);
    public static ScriptEndpoint Target { get; } = new("Client", "127.0.0.1,1", "tgt", Nowhere);

    private static ColumnRow Col(string tbl, int id, string col, string dt = "int", int len = 4, bool nullable = true) =>
        new("dbo", tbl, id, col, dt, len, 10, 0, nullable, false, false, null, null, false, null, null, false);

    public static ModuleRow Mod(string name, string otype, string def) => new("dbo", name, otype, true, true, def);

    /// <summary>The source: dbo.A gains a column, a wider column, an index and a disabled check; dbo.B gains a column, a check and a foreign key; plus new tables and modules.</summary>
    public static ScriptSchema Src() => ScriptSchema.FromRows("src", Coll,
        [
            Mod("p_new", "P", "CREATE PROCEDURE dbo.p_new AS SELECT a_id FROM dbo.A"),
            Mod("p_renamed", "P", "CREATE PROCEDURE dbo.p_old_name AS SELECT 1"),
            Mod("v_new", "V", "CREATE VIEW dbo.v_new AS SELECT n_id FROM dbo.NewT"),
            Mod("p_diff", "P", "CREATE PROCEDURE dbo.p_diff AS SELECT 2"),
            Mod("tr_new", "TR", "CREATE TRIGGER dbo.tr_new ON dbo.A AFTER INSERT AS SET NOCOUNT ON"),
        ],
        [new("dbo", "A"), new("dbo", "B"), new("dbo", "NewT")],
        [
            Col("A", 1, "a_id", nullable: false), Col("A", 2, "a_wide", "varchar", 100), Col("A", 3, "a_name", "nvarchar", 100),
            Col("B", 1, "b_id", nullable: false), Col("B", 2, "b_a_id"), Col("B", 3, "b_new"),
            Col("NewT", 1, "n_id", nullable: false),
        ],
        [],
        [
            new CheckRow("dbo", "A", "CK_A_id", "([a_id]>(0))", false, true, true),
            new CheckRow("dbo", "B", "CK_B_id", "([b_id]>(0))", false, false, false),
        ],
        [
            new KeyRow("dbo", "A", "PK_A", "PK", false, "CLUSTERED", "[a_id] ASC"),
            new KeyRow("dbo", "NewT", "PK_NewT", "PK", false, "CLUSTERED", "[n_id] ASC"),
        ],
        [new ForeignKeyRow("dbo", "B", "FK_B_A", false, "dbo", "A", "NO_ACTION", "NO_ACTION", false, false, false, "[b_a_id]", "[a_id]")],
        [
            new IndexRow("dbo", "A", "IX_A_wide", "NONCLUSTERED", false, null, "[a_wide] ASC", null),
            new IndexRow("dbo", "B", "IX_B_new", "NONCLUSTERED", false, null, "[b_new] ASC", null),
        ],
        [], [], [], []);

    /// <summary>The target: older dbo.A and dbo.B, the old dbo.p_diff, the check on dbo.A enabled.</summary>
    public static ScriptSchema Tgt(params ModuleRow[] more) => ScriptSchema.FromRows("tgt", Coll,
        [Mod("p_diff", "P", "CREATE PROCEDURE dbo.p_diff AS SELECT 1"), .. more],
        [new("dbo", "A"), new("dbo", "B")],
        [
            Col("A", 1, "a_id", nullable: false), Col("A", 2, "a_wide", "varchar", 50),
            Col("B", 1, "b_id", nullable: false), Col("B", 2, "b_a_id"),
        ],
        [],
        [new CheckRow("dbo", "A", "CK_A_id", "([a_id]>(0))", false, false, false)],
        [new KeyRow("dbo", "A", "PK_A", "PK", false, "CLUSTERED", "[a_id] ASC")],
        [], [], [], [], [], []);

    public static ScriptGenerator Generator(ScriptSchema? src = null, ScriptSchema? tgt = null) =>
        new() { CatalogOverride = ep => ep == Source ? src ?? Src() : tgt ?? Tgt() };

    public static ScriptOptions Options(string folder, params ObjectName[] only) => new()
    {
        OutputFolder = folder,
        Sections = [.. Enum.GetValues<ScriptSection>()],
        OnlyObjects = [.. only],
        RunFolderName = "object",
    };

    // ------------------------------------------------------------------ a finished compare of the same pair

    private static DbObject Obj(DbObjectType type, string name, string? def = null, params string[] columns) => new()
    {
        Type = type,
        Schema = "dbo",
        Name = name,
        Definition = def,
        Columns = [.. columns.Select(c => new ColumnInfo { Name = c, DataType = "int" })],
    };

    /// <summary>A done compare session: (source object, target object or null, status) per row, one target "Client".</summary>
    public static CompareSession Session(params (DbObject? Source, DbObject? Target, ObjectStatus Status)[] rows)
    {
        var session = new CompareSession
        {
            Status = "done",
            Source = new SchemaSnapshot { Label = "Dev", Objects = [.. rows.Where(r => r.Source is not null).Select(r => r.Source!)] },
            Targets = [new SchemaSnapshot { Label = "Client", Objects = [.. rows.Where(r => r.Target is not null).Select(r => r.Target!)] }],
            SourceConnection = new SchemaSource { Label = "Dev", ConnectionString = Nowhere },
            TargetConnections = [new SchemaSource { Label = "Client", ConnectionString = Nowhere }],
        };
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "Dev" },
            Targets = [new TargetSummary { Label = "Client" }],
            Rows = [.. rows.Select(r =>
            {
                var o = r.Source ?? r.Target!;
                return new CompareRow { Key = o.Key, Type = o.Type, Schema = o.Schema, Name = o.Name, Cells = [new TargetCell { Status = r.Status }] };
            })],
        };
        return session;
    }

    /// <summary>The fixture catalogs as a compare would have seen them.</summary>
    public static CompareSession FixtureSession() => Session(
        (Obj(DbObjectType.Table, "A", null, "a_id", "a_wide", "a_name"), Obj(DbObjectType.Table, "A", null, "a_id", "a_wide"), ObjectStatus.Different),
        (Obj(DbObjectType.Table, "B", null, "b_id", "b_a_id", "b_new"), Obj(DbObjectType.Table, "B", null, "b_id", "b_a_id"), ObjectStatus.Different),
        (Obj(DbObjectType.Table, "NewT", null, "n_id"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.StoredProcedure, "p_new", "CREATE PROCEDURE dbo.p_new AS SELECT a_id FROM dbo.A"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.View, "v_new", "CREATE VIEW dbo.v_new AS SELECT n_id FROM dbo.NewT"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.StoredProcedure, "p_diff", "CREATE PROCEDURE dbo.p_diff AS SELECT 2"),
            Obj(DbObjectType.StoredProcedure, "p_diff", "CREATE PROCEDURE dbo.p_diff AS SELECT 1"), ObjectStatus.Different),
        (Obj(DbObjectType.StoredProcedure, "p_same", "CREATE PROCEDURE dbo.p_same AS SELECT 1"),
            Obj(DbObjectType.StoredProcedure, "p_same", "CREATE PROCEDURE dbo.p_same AS SELECT 1"), ObjectStatus.Same),
        (null, Obj(DbObjectType.StoredProcedure, "p_extra]x", "CREATE PROCEDURE dbo.[p_extra]]x] AS SELECT 1"), ObjectStatus.OnlyInTarget));

    public static string KeyOf(DbObjectType type, string name) => new DbObject { Type = type, Schema = "dbo", Name = name }.Key;
}

/// <summary>A scratch folder, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"schemascope-obj-{Guid.NewGuid():n}");

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class ObjectScriptGeneratorTests
{
    private static readonly ObjectName A = new("dbo", "A");

    private static async Task<ScriptOutcome> Run(string folder, params ObjectName[] only) =>
        await ObjectFixture.Generator().GenerateAsync(ObjectFixture.Source, ObjectFixture.Target,
            ObjectFixture.Options(folder, only), new HashSet<string>(), CancellationToken.None);

    [Fact]
    public async Task A_scoped_run_writes_nothing_about_any_other_object()
    {
        using var temp = new TempFolder();
        var outcome = await Run(temp.Path, A);

        Assert.True(outcome.Success, outcome.Message);
        var sql = outcome.Files.Where(f => f.Kind is not (WrittenFileKind.Markdown or WrittenFileKind.Runner)).ToList();
        Assert.NotEmpty(sql);
        Assert.All(sql.Where(f => f.Object is not null), f => Assert.True(f.Object!.Value.SameAs(A), $"{f.Folder}\\{f.RelativePath} is about {f.Object}"));
        Assert.Equal(["01_columns", "04_columntypes", "05_constraints", "06_indexes"],
            outcome.Files.Where(f => f.IsObjectFile).Select(f => f.Folder).Distinct().Order(StringComparer.Ordinal));

        // the run had B's column, check and foreign key, NewT, four modules and a trigger to script: none of them is
        // written, held back, listed in 99_review or summed up in a README / RUN.ps1
        Assert.All(outcome.HeldBack, h => Assert.StartsWith("A", h.Object.Replace("dbo.", "")));
        foreach (var f in outcome.Files.Where(f => f.Folder == ReviewLog.Folder && f.RelativePath != ScriptValidator.ReportFile))
        {
            var text = File.ReadAllText(f.FullPath);
            foreach (var other in new[] { "p_new", "v_new", "p_diff", "tr_new", "NewT", "b_new", "CK_B_id", "FK_B_A", "IX_B_new", "p_old_name" })
                Assert.DoesNotContain(other, text);
        }
        Assert.DoesNotContain(outcome.Files, f => f.RelativePath is "RUN.ps1" or "README.md");
        // flow_safety stays (it warns what this script breaks); the whole-database reports do not
        Assert.Contains(outcome.Files, f => f.RelativePath.EndsWith(ReviewReports.FlowSafetyFile, StringComparison.Ordinal));
        foreach (var whole in new[] { ReviewReports.ColumnTypesFile, ReviewReports.DriftedModulesFile, ReviewReports.TargetOnlyFile })
            Assert.DoesNotContain(outcome.Files, f => f.RelativePath.EndsWith(whole, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scoped_table_script_warns_about_target_code_its_new_column_breaks()
    {
        using var temp = new TempFolder();
        var tgt = ObjectFixture.Tgt(ObjectFixture.Mod("p_ins", "P", "CREATE PROCEDURE dbo.p_ins AS INSERT INTO dbo.B SELECT 1, 2"));
        var outcome = await ObjectFixture.Generator(tgt: tgt).GenerateAsync(ObjectFixture.Source, ObjectFixture.Target,
            ObjectFixture.Options(temp.Path, new ObjectName("dbo", "B")), new HashSet<string>(), CancellationToken.None);

        Assert.Contains(outcome.Files, f => f.Folder == "01_columns" && f.IsObjectFile);
        Assert.Contains(outcome.Warnings, w => w.Contains("dbo.p_ins inserts into dbo.B") && w.Contains("Msg 213"));
    }

    [Fact]
    public async Task A_missing_procedure_whose_missing_table_type_is_left_out_is_held_back_not_scripted()
    {
        var src = ScriptSchema.FromRows("src", ObjectFixture.Coll,
            [ObjectFixture.Mod("p_tt", "P", "CREATE PROCEDURE dbo.p_tt @rows dbo.TT1 READONLY AS SELECT 1")],
            [], [], [], [], [], [], [],
            [new ObjectName("dbo", "TT1")],
            [new TtColumnRow("dbo", "TT1", 1, "id", "int", 4, 10, 0, false, false, null, null, null)],
            [], [new TtParamRow("TT1", "dbo", "p_tt", "dbo")]);
        var tgt = ScriptSchema.FromRows("tgt", ObjectFixture.Coll, [], [], [], [], [], [], [], [], [], [], [], []);
        async Task<ScriptOutcome> Run(string folder, params ObjectName[] only) =>
            await ObjectFixture.Generator(src, tgt).GenerateAsync(ObjectFixture.Source, ObjectFixture.Target,
                ObjectFixture.Options(folder, only), new HashSet<string>(), CancellationToken.None);

        using (var temp = new TempFolder())
        {
            var alone = await Run(temp.Path, new ObjectName("dbo", "p_tt"));
            Assert.DoesNotContain(alone.Files, f => f.IsObjectFile);
            var held = Assert.Single(alone.HeldBack);
            Assert.Contains("p_tt", held.Object);
            Assert.Contains("Msg 2715", held.Reason);
        }
        using (var temp = new TempFolder())
        {
            var both = await Run(temp.Path, new ObjectName("dbo", "p_tt"), new ObjectName("dbo", "TT1"));
            Assert.Equal(2, both.Files.Count(f => f.IsObjectFile && f.Folder == CoreFoldersGenerator.MissingFolder));
            Assert.Empty(both.HeldBack);
        }
    }

    [Fact]
    public async Task Scoped_to_a_procedure_only_that_procedure_is_created()
    {
        using var temp = new TempFolder();
        var outcome = await Run(temp.Path, new ObjectName("dbo", "p_new"));

        var part = Assert.Single(outcome.Files, f => f.IsObjectFile);
        Assert.Equal(CoreFoldersGenerator.MissingFolder, part.Folder);
        Assert.Equal(new ObjectName("dbo", "p_new"), part.Object);
        Assert.Empty(outcome.HeldBack);
    }

    [Fact]
    public async Task A_renamed_module_is_created_under_its_catalog_name_and_reported_as_done_not_held()
    {
        using var temp = new TempFolder();
        var outcome = await Run(temp.Path, new ObjectName("dbo", "p_renamed"));

        var part = Assert.Single(outcome.Files, f => f.IsObjectFile);
        Assert.Contains("CREATE PROCEDURE [dbo].[p_renamed]", ScriptWriter.ReadBack(part.FullPath));
        var report = File.ReadAllText(Assert.Single(outcome.Files, f => f.RelativePath == ReviewLog.RenamedHeaders).FullPath);
        Assert.Contains("p_old_name", report);
        Assert.Contains("The scripts create", report);
        Assert.DoesNotContain("held back |", report);
    }

    [Fact]
    public async Task Scripted_constraint_state_changes_are_not_counted_as_held_back()
    {
        using var temp = new TempFolder();
        var outcome = await Run(temp.Path, A);

        // A's check is disabled in the source: 05 disables it in the target too, and lists that for a look
        var state = Assert.Single(outcome.Files, f => f.IsObjectFile && f.RelativePath.Contains("_state_"));
        Assert.Contains("NOCHECK CONSTRAINT [CK_A_id]", ScriptWriter.ReadBack(state.FullPath));
        var report = File.ReadAllText(Assert.Single(outcome.Files, f => f.RelativePath == ReviewLog.NotAppliedConstraints).FullPath);
        Assert.Contains("Nothing was held back.", report);
        Assert.DoesNotContain("no script is generated for them", report);
        Assert.Contains("## Scripted, but check these", report);
        Assert.Contains("CK_A_id", report);
        Assert.Contains(outcome.Warnings, w => w.Contains("CK_A_id") && w.Contains("scripted anyway"));
    }
}

public class ObjectScriptPlanTests
{
    private static int Index => 0;

    private static CompareRow Row(CompareSession s, DbObjectType type, string name) =>
        s.RowIndex[ObjectFixture.KeyOf(type, name)];

    private static Task<ObjectScriptOutcome> Run(CompareSession s, CompareRow row, string root, params CompareRow[] deps) =>
        ObjectScripts.RunAsync(s, row, Index, [.. deps], ObjectFixture.Source, ObjectFixture.Target,
            new HashSet<string>(), new Dictionary<string, LineChange>(), ObjectFixture.Generator(), root, null, CancellationToken.None);

    [Fact]
    public async Task A_missing_procedure_is_created_by_03_alone()
    {
        var s = ObjectFixture.FixtureSession();
        var row = Row(s, DbObjectType.StoredProcedure, "p_new");
        Assert.Equal("create", ObjectScripts.Plan(s.Result!, row, Index, out _));

        using var temp = new TempFolder();
        var outcome = await Run(s, row, temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        var part = Assert.Single(outcome.Parts);
        Assert.Equal("03_missing", part.Folder);
        Assert.Equal("dbo.p_new", part.ObjectName);
        Assert.Equal("rawModule", part.Kind);
        Assert.True(File.Exists(part.File));
        Assert.StartsWith(temp.Path, outcome.Path);
        Assert.StartsWith("-- 03_missing: dbo.p_new\r\n", outcome.Script);
        Assert.Contains("CREATE PROCEDURE dbo.p_new", outcome.Script);
        Assert.EndsWith("GO\r\n", outcome.Script);
        Assert.Null(outcome.Rollback);
        Assert.Null(outcome.DropPreview);
    }

    [Fact]
    public async Task A_different_table_gets_its_column_constraint_state_and_index_changes_in_run_order()
    {
        var s = ObjectFixture.FixtureSession();
        var row = Row(s, DbObjectType.Table, "A");
        Assert.Equal("tableChanges", ObjectScripts.Plan(s.Result!, row, Index, out _));

        using var temp = new TempFolder();
        var outcome = await Run(s, row, temp.Path);

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(["04_columntypes", "01_columns", "05_constraints", "06_indexes"], outcome.Parts.Select(p => p.Folder));
        Assert.All(outcome.Parts, p => Assert.Equal("dbo.A", p.ObjectName));
        Assert.Contains("ALTER TABLE [dbo].[A] NOCHECK CONSTRAINT [CK_A_id]", outcome.Script);
        Assert.Contains("-- 05_constraints: dbo.A", outcome.Script);
        Assert.True(outcome.Script.IndexOf("04_columntypes", StringComparison.Ordinal) < outcome.Script.IndexOf("01_columns", StringComparison.Ordinal));
        Assert.DoesNotContain("b_new", outcome.Script);
    }

    [Fact]
    public async Task A_view_with_its_missing_table_ticked_creates_both_table_first()
    {
        var s = ObjectFixture.FixtureSession();
        using var temp = new TempFolder();
        var outcome = await Run(s, Row(s, DbObjectType.View, "v_new"), temp.Path, Row(s, DbObjectType.Table, "NewT"));

        Assert.Equal(["dbo.NewT", "dbo.v_new"], outcome.Parts.Select(p => p.ObjectName));
        Assert.Equal([ObjectFixture.KeyOf(DbObjectType.Table, "NewT")], outcome.DependenciesIncluded);
        Assert.Empty(outcome.HeldBack);
    }

    [Fact]
    public void An_object_only_the_target_has_gets_a_bracket_safe_drop_to_copy_and_nothing_else()
    {
        var s = ObjectFixture.FixtureSession();
        var row = Row(s, DbObjectType.StoredProcedure, "p_extra]x");
        Assert.Equal("extra", ObjectScripts.Plan(s.Result!, row, Index, out _));

        var outcome = ObjectScripts.ExtraOutcome(row, "Client");

        Assert.Contains("DROP PROCEDURE [dbo].[p_extra]]x];", outcome.DropPreview);
        Assert.Contains("Generate script does not run this", outcome.DropPreview);
        Assert.Empty(outcome.Parts);
        Assert.Equal("", outcome.Script);
        Assert.Equal("", outcome.Path);
    }

    [Fact]
    public async Task A_line_break_in_an_object_name_never_ends_a_comment_line()
    {
        const string evil = "p\r\nDROP TABLE dbo.A --";
        var row = new CompareRow { Key = "x", Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = evil };
        var drop = ObjectScripts.DropPreview(row, "Client");
        // the comment line stays one line; the name's line break survives only inside the DROP's brackets
        Assert.Contains($"-- {row.TypeLabel} dbo.p DROP TABLE dbo.A -- exists only in Client.\r\nDROP PROCEDURE [dbo].[p\r\nDROP TABLE dbo.A --];\r\n", drop);

        var proc = new DbObject { Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = evil };
        var s = ObjectFixture.Session((proc, null, ObjectStatus.MissingInTarget));
        var src = ScriptSchema.FromRows("src", ObjectFixture.Coll,
            [ObjectFixture.Mod(evil, "P", "CREATE PROCEDURE [dbo].[p\r\nDROP TABLE dbo.A --] AS SELECT 1")],
            [], [], [], [], [], [], [], [], [], [], []);
        using var temp = new TempFolder();
        var outcome = await ObjectScripts.RunAsync(s, s.Result!.Rows[0], Index, [], ObjectFixture.Source, ObjectFixture.Target,
            new HashSet<string>(), new Dictionary<string, LineChange>(), ObjectFixture.Generator(src, ObjectFixture.Tgt()), temp.Path, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
        Assert.NotEmpty(outcome.Parts);
        // the module's own text keeps the name inside its brackets; the separator above it is one comment line
        Assert.StartsWith("-- 03_missing: dbo.p DROP TABLE dbo.A --\r\n", outcome.Script);
    }

    [Theory]
    [InlineData(ObjectStatus.Same, "identical")]
    [InlineData(ObjectStatus.Unavailable, "unavailable")]
    public void Nothing_to_script_says_why(ObjectStatus status, string plan)
    {
        var o = new DbObject { Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = "p" };
        var s = ObjectFixture.Session((o, o, status));
        Assert.Equal(plan, ObjectScripts.Plan(s.Result!, s.Result!.Rows[0], Index, out var why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void A_target_that_failed_to_read_is_unavailable()
    {
        var s = ObjectFixture.FixtureSession();
        s.Result!.Targets[0].Error = "Login failed";
        Assert.Equal("unavailable", ObjectScripts.Plan(s.Result, Row(s, DbObjectType.StoredProcedure, "p_new"), Index, out var why));
        Assert.Contains("could not be read", why);
    }

    [Fact]
    public void Old_run_folders_are_pruned_and_nothing_else()
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "object-scripts");
        var old = Directory.CreateDirectory(Path.Combine(root, "20200101-000000_old")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(root, "20990101-000000_new")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(old, "x.sql"), "SELECT 1");
        foreach (var d in new[] { old, outside }) Directory.SetCreationTimeUtc(d, DateTime.UtcNow.AddDays(-30));

        ObjectScripts.Prune(root, DateTime.UtcNow, ObjectScripts.KeepFor);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(outside));
    }
}

public class ObjectDependencyTests
{
    private static DbObject Obj(DbObjectType type, string name, string? def = null, params string[] columns) => new()
    {
        Type = type, Schema = "dbo", Name = name, Definition = def,
        Columns = [.. columns.Select(c => new ColumnInfo { Name = c, DataType = "int" })],
    };

    private static CompareSession Session() => ObjectFixture.Session(
        (Obj(DbObjectType.View, "v_dep", "CREATE VIEW dbo.v_dep AS SELECT x FROM dbo.T_for_view"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.Table, "T_for_view", null, "x"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.StoredProcedure, "p_dep", "CREATE PROCEDURE dbo.p_dep AS SELECT y FROM dbo.T_for_proc -- not T_comment"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.Table, "T_for_proc", null, "y"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.Table, "T_comment", null, "z"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.StoredProcedure, "p_col", "CREATE PROCEDURE dbo.p_col AS SELECT new_col FROM dbo.T_changed"), null, ObjectStatus.MissingInTarget),
        (Obj(DbObjectType.Table, "T_changed", null, "id", "new_col"), Obj(DbObjectType.Table, "T_changed", null, "id"), ObjectStatus.Different));

    private static ObjectDependencies Deps(CompareSession s, DbObjectType type, string name) =>
        ObjectScripts.Dependencies(s, s.RowIndex[ObjectFixture.KeyOf(type, name)], 0);

    [Fact]
    public void A_view_needs_the_missing_table_it_reads_and_it_is_ticked()
    {
        var deps = Deps(Session(), DbObjectType.View, "v_dep");

        Assert.Equal("create", deps.Plan);
        var dep = Assert.Single(deps.Dependencies);
        Assert.Equal("dbo.T_for_view", dep.DisplayName);
        Assert.False(dep.Required);   // not a prerequisite: ticked by default, the user may untick it
        Assert.True(dep.DefaultIncluded);
    }

    [Fact]
    public void A_procedure_is_created_without_the_missing_table_it_reads_so_it_is_not_ticked_but_warned()
    {
        var deps = Deps(Session(), DbObjectType.StoredProcedure, "p_dep");

        var dep = Assert.Single(deps.Dependencies);   // T_comment is only named in a comment
        Assert.Equal("dbo.T_for_proc", dep.DisplayName);
        Assert.False(dep.Required);
        Assert.False(dep.DefaultIncluded);
        Assert.Contains("fails when it runs", dep.Reason);
        // the pre-check does not warn in a fixed text what the dialog's ticks can change; the run's answer does
        Assert.DoesNotContain(deps.Warnings, w => w.Contains("is created without"));
    }

    [Fact]
    public async Task A_procedure_run_without_its_unticked_missing_table_warns_it_fails_when_it_runs()
    {
        var s = Session();
        using var temp = new TempFolder();
        var outcome = await ObjectScripts.RunAsync(s, s.RowIndex[ObjectFixture.KeyOf(DbObjectType.StoredProcedure, "p_dep")], 0, [],
            ObjectFixture.Source, ObjectFixture.Target, new HashSet<string>(), new Dictionary<string, LineChange>(),
            ObjectFixture.Generator(), temp.Path, null, CancellationToken.None);

        Assert.Contains(outcome.Warnings, w => w.Contains("is created without dbo.T_for_proc") && w.Contains("fails when it runs"));
    }

    [Fact]
    public void Plan_risks_are_left_to_the_dialog_so_they_are_not_said_twice()
    {
        var s = ObjectFixture.FixtureSession();
        var deps = ObjectScripts.Dependencies(s, s.RowIndex[ObjectFixture.KeyOf(DbObjectType.StoredProcedure, "p_diff")], 0);

        Assert.Equal("alter", deps.Plan);
        Assert.Empty(deps.Warnings);
    }

    [Fact]
    public void A_column_the_target_table_lacks_makes_that_table_required()
    {
        var dep = Assert.Single(Deps(Session(), DbObjectType.StoredProcedure, "p_col").Dependencies);

        Assert.Equal("dbo.T_changed", dep.DisplayName);
        Assert.True(dep.DefaultIncluded);
        Assert.Contains("new_col", dep.Reason);
    }

    [Fact]
    public void Names_neither_database_has_are_warned_about()
    {
        var s = ObjectFixture.Session(
            (Obj(DbObjectType.StoredProcedure, "p_gone", "CREATE PROCEDURE dbo.p_gone AS\nWITH c AS (SELECT 1 a) SELECT * FROM dbo.Gone_Table g JOIN c ON 1=1 JOIN #t ON 1=1 " +
                                                         "CROSS APPLY OPENJSON(@j) x; DELETE FROM g; EXEC dbo.Gone_Proc; SELECT * FROM master..spt_values"),
                null, ObjectStatus.MissingInTarget));

        var deps = Deps(s, DbObjectType.StoredProcedure, "p_gone");

        var warning = Assert.Single(deps.Warnings, w => w.Contains("which neither"));
        Assert.Contains("Gone_Proc, Gone_Table", warning);
        foreach (var local in new[] { " c,", "#t", "OPENJSON", " g,", "master", "spt_values" })
            Assert.DoesNotContain(local, warning);
    }
}

/// <summary>The two routes on the real owner app: token, viewer, 404 / 400 / 415 and the extra / identical answers.</summary>
public sealed class ObjectScriptRouteTests : IAsyncLifetime
{
    private readonly TempStore _temp = new();
    private readonly LocalAuth _auth = LocalAuth.Mint();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _runId = "";

    public async Task InitializeAsync()
    {
        _app = SchemaScopeApi.Build([], desktop: false, _auth, storePath: _temp.DbPath);
        _app.Urls.Clear();
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add(LocalAuth.HeaderName, _auth.Token);

        var session = _app.Services.GetRequiredService<CompareSessionStore>().Create();
        var fixture = ObjectFixture.FixtureSession();
        session.Status = "done";
        session.Result = fixture.Result;
        session.Source = fixture.Source;
        session.Targets = fixture.Targets;
        _runId = session.Id;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _temp.Dispose();
    }

    private static string Key(DbObjectType type, string name) => ObjectFixture.KeyOf(type, name);

    private Task<HttpResponseMessage> Post(object body, string? runId = null) =>
        _http.PostAsJsonAsync($"/api/compare/{runId ?? _runId}/object-script", body);

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Without_the_token_both_routes_are_refused()
    {
        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        var get = await bare.GetAsync($"/api/compare/{_runId}/object-script/dependencies?key=x&targetIndex=0");
        var post = await bare.PostAsync($"/api/compare/{_runId}/object-script", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task The_dependency_check_answers_from_memory()
    {
        var key = Uri.EscapeDataString(Key(DbObjectType.View, "v_new"));
        var r = await _http.GetAsync($"/api/compare/{_runId}/object-script/dependencies?key={key}&targetIndex=0");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.Equal("create", body.GetProperty("plan").GetString());
        Assert.Equal("missingInTarget", body.GetProperty("status").GetString());
        var dep = Assert.Single(body.GetProperty("dependencies").EnumerateArray());
        Assert.Equal("dbo.NewT", dep.GetProperty("displayName").GetString());
        Assert.False(dep.GetProperty("required").GetBoolean());   // only prerequisites are required
        Assert.True(dep.GetProperty("defaultIncluded").GetBoolean());
    }

    [Fact]
    public async Task An_identical_object_is_a_400()
    {
        var r = await Post(new { targetIndex = 0, key = Key(DbObjectType.StoredProcedure, "p_same") });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Already the same in Client", (await Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_extra_object_gets_only_a_drop_preview()
    {
        var r = await Post(new { targetIndex = 0, key = Key(DbObjectType.StoredProcedure, "p_extra]x") });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await Json(r);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("DROP PROCEDURE [dbo].[p_extra]]x];", body.GetProperty("dropPreview").GetString());
        Assert.Empty(body.GetProperty("parts").EnumerateArray());
        Assert.Equal("", body.GetProperty("script").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rollback").ValueKind);
    }

    [Theory]
    [InlineData("nope", 0)]
    [InlineData(null, 0)]
    [InlineData("3|DBO.P_NEW", 7)]
    [InlineData("3|DBO.P_NEW", -1)]
    public async Task A_bad_key_or_target_is_a_400(string? key, int targetIndex)
    {
        var r = await Post(new { targetIndex, key });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(string.IsNullOrEmpty((await Json(r)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Dependencies_must_be_missing_or_different_rows_of_the_run()
    {
        var p = Key(DbObjectType.StoredProcedure, "p_new");
        foreach (var deps in new[] { new[] { "1|DBO.NOPE" }, [Key(DbObjectType.StoredProcedure, "p_same")],
                     Enumerable.Range(0, ObjectScripts.MaxDependencies + 1).Select(i => $"1|DBO.T{i}").ToArray() })
        {
            var r = await Post(new { targetIndex = 0, key = p, includeDependencies = deps });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Fact]
    public async Task An_unknown_run_is_a_404()
    {
        var get = await _http.GetAsync("/api/compare/nope/object-script/dependencies?key=x&targetIndex=0");
        var post = await Post(new { targetIndex = 0, key = "x" }, runId: "nope");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Contains("no longer in memory", (await Json(post)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_form_post_is_a_415()
    {
        var r = await _http.PostAsync($"/api/compare/{_runId}/object-script",
            new FormUrlEncodedContent([new("targetIndex", "0"), new("key", "x")]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public async Task A_bad_progress_id_is_a_400()
    {
        var r = await Post(new { targetIndex = 0, key = Key(DbObjectType.StoredProcedure, "p_new"), progressId = "..\\x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}

/// <summary>The LAN viewer has neither route.</summary>
public sealed class ObjectScriptViewerTests : IAsyncLifetime
{
    private readonly TempStore _temp = new();
    private ViewerHost _viewer = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _viewer = new ViewerHost(Shares.NewStore(_temp), NullLogger<ViewerHost>.Instance);
        var (ok, error) = await _viewer.StartAsync(0, null, IPAddress.Loopback);
        Assert.True(ok, error);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_viewer.Port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _viewer.StopAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task The_viewer_has_no_object_script_routes()
    {
        var get = await _http.GetAsync("/api/compare/abc/object-script/dependencies?key=x&targetIndex=0");
        var post = await _http.PostAsync("/api/compare/abc/object-script", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.False(post.IsSuccessStatusCode);
        Assert.DoesNotContain(ViewerHost.Routes, r => r.Contains("object-script", StringComparison.Ordinal));
    }
}
