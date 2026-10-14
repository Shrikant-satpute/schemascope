using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>
/// Sync-script generator fixes: 05 brings an existing check / foreign key to the source's enabled-trusted state,
/// a renamed module's CREATE header is replayed under its catalog name, and Msg 213-style compile errors hold a
/// module back.
/// </summary>
public class GeneratorFixTests
{
    // ------------------------------------------------------------------ constraint state (05)

    private static ForeignKeyRow Fk(bool disabled, bool notTrusted, string name = "FK_tgt") =>
        new("dbo", "orders", name, false, "dbo", "customers", "NO_ACTION", "NO_ACTION", disabled, notTrusted, false, "[customer_id]", "[id]");

    private static CheckRow Ck(bool disabled, bool notTrusted) =>
        new("dbo", "orders", "CK__orders__qty__1A2B3C", "([qty]>(0))", true, notTrusted, disabled);

    [Theory]
    [InlineData(true, false)]   // target disabled
    [InlineData(false, true)]   // target enabled, not trusted
    public void A_trusted_source_retrusts_the_target_with_check(bool tgtDisabled, bool tgtNotTrusted)
    {
        var fix = CoreRun.StateFixFor((false, false), Fk(tgtDisabled, tgtNotTrusted));
        Assert.NotNull(fix);
        Assert.Equal("ALTER TABLE [dbo].[orders] WITH CHECK CHECK CONSTRAINT [FK_tgt]", fix.Sql);
        Assert.True(fix.Retrusts);
        Assert.False(fix.Weakens);
    }

    [Fact]
    public void An_enabled_untrusted_source_enables_a_disabled_target_without_revalidating()
    {
        var fix = CoreRun.StateFixFor((false, true), Fk(true, true));
        Assert.NotNull(fix);
        Assert.Equal("ALTER TABLE [dbo].[orders] CHECK CONSTRAINT [FK_tgt]", fix.Sql);
        Assert.False(fix.Retrusts);
        Assert.False(fix.Weakens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_disabled_source_disables_an_enabled_target(bool tgtNotTrusted)
    {
        var fix = CoreRun.StateFixFor((true, true), Ck(false, tgtNotTrusted));
        Assert.NotNull(fix);
        Assert.Equal("ALTER TABLE [dbo].[orders] NOCHECK CONSTRAINT [CK__orders__qty__1A2B3C]", fix.Sql);
        Assert.False(fix.Retrusts);
        Assert.True(fix.Weakens);   // the target stops enforcing it: listed in review
    }

    [Fact]
    public void An_untrusted_source_is_mirrored_onto_a_trusted_target_and_flagged_as_weakening()
    {
        var fix = CoreRun.StateFixFor((false, true), Fk(false, false));
        Assert.NotNull(fix);
        Assert.Equal("ALTER TABLE [dbo].[orders] NOCHECK CONSTRAINT [FK_tgt]\r\nGO\r\nALTER TABLE [dbo].[orders] CHECK CONSTRAINT [FK_tgt]", fix.Sql);
        Assert.False(fix.Retrusts);
        Assert.True(fix.Weakens);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Matching_states_need_nothing(bool disabled, bool notTrusted) =>
        Assert.Null(CoreRun.StateFixFor((disabled, notTrusted), Fk(disabled, notTrusted)));

    [Fact]
    public void The_statement_names_the_targets_constraint_not_the_sources()
    {
        var fix = CoreRun.StateFixFor((true, true), Fk(false, false, name: "FK_orders_customers_client"));
        Assert.Contains("[FK_orders_customers_client]", fix!.Sql);
    }

    private static CheckRow NamedCk(string name, bool disabled, bool nfr = false) =>
        new("dbo", "T", name, "([a]>(0))", false, disabled, disabled, nfr);

    [Fact]
    public void Same_definition_constraints_pair_by_name_and_only_the_differing_one_is_fixed()
    {
        CheckRow[] src = [NamedCk("CK_a", false), NamedCk("CK_b", true)];
        CheckRow[] tgt = [NamedCk("CK_a", false), NamedCk("CK_b", false)];
        var pairs = CoreRun.Pairs(src, tgt, r => new CoreRun.RowKey(r.Definition, r.Definition), StringComparer.Ordinal);

        Assert.Equal(2, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(p.Source.CName, p.Target.CName));
        var fixes = pairs.Select(p => CoreRun.StateFixFor((p.Source.IsDisabled, p.Source.IsNotTrusted), p.Target)).OfType<CoreRun.StateFix>().ToList();
        Assert.Equal("ALTER TABLE [dbo].[T] NOCHECK CONSTRAINT [CK_b]", Assert.Single(fixes).Sql);
    }

    [Fact]
    public void Unmatched_names_pair_with_a_target_in_the_same_state_first()
    {
        CheckRow[] src = [NamedCk("CK_src", true)];
        CheckRow[] tgt = [NamedCk("CK_x", false), NamedCk("CK_y", true)];
        var pair = Assert.Single(CoreRun.Pairs(src, tgt, r => new CoreRun.RowKey(r.Definition, r.Definition), StringComparer.Ordinal));
        Assert.Equal("CK_y", pair.Target.CName);
    }

    [Fact]
    public void A_not_for_replication_target_check_is_never_retrusted()
    {
        var target = NamedCk("CK_nfr", false, nfr: true) with { IsNotTrusted = true };
        var fix = CoreRun.StateFixFor((false, false), target)!;
        Assert.True(fix.Retrusts);
        Assert.NotNull(CoreRun.NfrHoldReason(target, fix, "Client_main"));
    }

    [Fact]
    public void A_not_for_replication_source_is_not_mirrored_as_untrusted()
    {
        var source = NamedCk("CK_nfr", false, nfr: true) with { IsNotTrusted = true };
        var fix = CoreRun.StateFixFor((false, true), NamedCk("CK_nfr", false))!;
        Assert.True(fix.Weakens);
        Assert.True(CoreRun.MirrorsNfr(source, fix));
        Assert.False(CoreRun.MirrorsNfr(source with { Nfr = false }, fix));
    }

    [Theory]
    [InlineData("ALTER TABLE [s].[t] NOCHECK CONSTRAINT [c]", false)]
    [InlineData("ALTER TABLE [s].[t] CHECK CONSTRAINT [c]", false)]
    [InlineData("ALTER TABLE [s].[t] WITH CHECK CHECK CONSTRAINT [c]", true)]
    public void Only_state_statements_that_retrust_are_listed_as_table_scans(string sql, bool scans) =>
        Assert.Equal(scans, ReviewReports.ScansTable(new EmittedStatement(1, "001_state_t.sql", "state", new("s", "t"), "c", sql)));

    [Fact]
    public void A_retrust_with_breaking_rows_is_held_back_with_the_count()
    {
        var why = CoreRun.RetrustHoldReason(new ProbeCount(3), "Client_main");
        Assert.NotNull(why);
        Assert.Contains("3 row(s)", why);
    }

    [Fact]
    public void A_retrust_whose_probe_failed_is_held_back_with_the_reason()
    {
        var why = CoreRun.RetrustHoldReason(new ProbeCount(null, "existing-data checks are switched off"), "Client_main");
        Assert.NotNull(why);
        Assert.Contains("existing-data checks are switched off", why);
    }

    [Fact]
    public void A_retrust_with_no_breaking_rows_is_emitted() =>
        Assert.Null(CoreRun.RetrustHoldReason(new ProbeCount(0), "Client_main"));

    // ------------------------------------------------------------------ renamed module headers

    private static ModuleRow Module(string obj, string definition, string otype = "P", string sch = "dbo") =>
        new(sch, obj, otype, true, true, definition);

    [Fact]
    public void A_renamed_procedure_gets_its_catalog_name_in_the_header()
    {
        var r = ModuleHeader.WithCatalogName(Module("kn_EY_COM_TRN_Get_X", "CREATE PROCEDURE dbo.kn_EY_COM_TRAN_Get_X\nAS\nSELECT 1"));
        Assert.NotNull(r);
        Assert.Equal("CREATE PROCEDURE [dbo].[kn_EY_COM_TRN_Get_X]\nAS\nSELECT 1", r.Value.Definition);
        Assert.Equal("dbo.kn_EY_COM_TRAN_Get_X", r.Value.Was);
    }

    [Fact]
    public void A_bracketed_header_is_rewritten()
    {
        var r = ModuleHeader.WithCatalogName(Module("new_p", "CREATE PROC [dbo].[old_p] @a int AS SELECT @a"));
        Assert.Equal("CREATE PROC [dbo].[new_p] @a int AS SELECT @a", r!.Value.Definition);
    }

    [Fact]
    public void Comments_and_strings_are_not_mistaken_for_the_header()
    {
        const string body = "/* CREATE PROCEDURE dbo.fake */\r\n-- CREATE PROCEDURE dbo.other\r\nCREATE PROCEDURE dbo.old_p AS SELECT 'CREATE PROCEDURE dbo.old_p'";
        var r = ModuleHeader.WithCatalogName(Module("new_p", body));
        Assert.Equal("/* CREATE PROCEDURE dbo.fake */\r\n-- CREATE PROCEDURE dbo.other\r\nCREATE PROCEDURE [dbo].[new_p] AS SELECT 'CREATE PROCEDURE dbo.old_p'",
            r!.Value.Definition);
    }

    [Fact]
    public void Create_or_alter_is_handled()
    {
        var r = ModuleHeader.WithCatalogName(Module("new_f", "CREATE OR ALTER FUNCTION dbo.old_f() RETURNS int AS BEGIN RETURN 1 END", "FN"));
        Assert.Equal("CREATE OR ALTER FUNCTION [dbo].[new_f]() RETURNS int AS BEGIN RETURN 1 END", r!.Value.Definition);
    }

    [Fact]
    public void A_schema_less_header_gets_the_catalog_schema_only_when_the_name_differs()
    {
        var r = ModuleHeader.WithCatalogName(Module("new_v", "CREATE VIEW old_v AS SELECT 1 x", "V", sch: "rpt"));
        Assert.Equal("CREATE VIEW [rpt].[new_v] AS SELECT 1 x", r!.Value.Definition);
        Assert.Null(ModuleHeader.WithCatalogName(Module("same_v", "CREATE VIEW same_v AS SELECT 1 x", "V")));
    }

    [Fact]
    public void A_name_with_spaces_is_compared_and_written_bracketed()
    {
        Assert.Null(ModuleHeader.WithCatalogName(Module("my proc", "CREATE PROCEDURE [dbo].[my proc] AS SELECT 1")));
        var r = ModuleHeader.WithCatalogName(Module("my new]proc", "CREATE PROCEDURE [dbo].[my proc] AS SELECT 1"));
        Assert.Equal("CREATE PROCEDURE [dbo].[my new]]proc] AS SELECT 1", r!.Value.Definition);
    }

    [Fact]
    public void Only_the_trigger_name_changes_not_its_table()
    {
        var r = ModuleHeader.WithCatalogName(Module("trg_new", "CREATE TRIGGER dbo.trg_old ON dbo.orders AFTER INSERT AS SELECT 1", "TR"));
        Assert.Equal("CREATE TRIGGER [dbo].[trg_new] ON dbo.orders AFTER INSERT AS SELECT 1", r!.Value.Definition);
    }

    [Fact]
    public void A_header_that_already_names_the_module_is_left_byte_for_byte() =>
        Assert.Null(ModuleHeader.WithCatalogName(Module("p", "create   procedure [DBO].p\nas select 1")));

    [Fact]
    public void The_schema_rewrites_its_modules_and_reports_them()
    {
        var schema = ScriptSchema.FromRows("db", "SQL_Latin1_General_CP1_CI_AS",
            [Module("renamed", "CREATE PROCEDURE dbo.original AS SELECT 1"), Module("kept", "CREATE PROCEDURE dbo.kept AS SELECT 1")],
            [], [], [], [], [], [], [], [], [], [], []);
        var fixedHeaders = schema.FixRenamedHeaders();
        Assert.Equal([(new ObjectName("dbo", "renamed"), "dbo.original")], fixedHeaders);
        Assert.Equal("CREATE PROCEDURE [dbo].[renamed] AS SELECT 1", schema.Modules[new ObjectName("dbo", "renamed")].Definition);
        Assert.Equal("CREATE PROCEDURE dbo.kept AS SELECT 1", schema.Modules[new ObjectName("dbo", "kept")].Definition);
    }

    // ------------------------------------------------------------------ compile check (03 / 07 / 10)

    [Fact]
    public void Msg_213_holds_a_procedure_back()
    {
        var r = SourceProbes.Judge([(213, "Column name or number of supplied values does not match table definition.")], "P");
        Assert.NotNull(r);
        Assert.False(r.Compiles);
        Assert.Equal(213, r.ErrorNumber);
        Assert.Contains("does not match table definition", r.Message);
    }

    [Theory]
    [InlineData(102, "P")]
    [InlineData(156, "FN")]
    [InlineData(4104, "TF")]
    [InlineData(207, "IF")]
    [InlineData(208, "V")]
    public void Errors_that_fail_the_create_are_create_failures(int number, string type) =>
        Assert.True(SourceProbes.IsCreateFailure(number, type));

    [Theory]
    [InlineData(208, "P")]
    [InlineData(2020, "FN")]
    [InlineData(2020, "TR")]
    public void Deferred_name_resolution_is_not_a_create_failure(int number, string type)
    {
        Assert.False(SourceProbes.IsCreateFailure(number, type));
        var r = SourceProbes.Judge([(number, "Invalid object name 'x'.")], type);
        Assert.True(r!.Compiles);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Any_other_error_stays_not_checked() =>
        Assert.Null(SourceProbes.Judge([(1205, "deadlock victim")], "P"));
}
