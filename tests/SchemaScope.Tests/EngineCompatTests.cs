using System.Text.Json;
using System.Text.Json.Serialization;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>
/// JSON written before engines existed - compare results, share bundles, history rows, profiles, requests from the UI -
/// still reads, as SQL Server, and round-trips (MULTI-DATABASE-PLAN §8).
/// </summary>
public class OldJsonTests
{
    /// <summary>The API's and the share files' options: camelCase, enums as camelCase strings.</summary>
    private static readonly JsonSerializerOptions Api = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string OldResult = """
        {"id":"abc","startedUtc":"2026-10-01T09:00:00Z","totalMs":12,
         "source":{"label":"Dev","server":"dev-sql","database":"Sales","productVersion":"16.0","collation":"SQL_Latin1_General_CP1_CI_AS","objectCount":2,"capturedAtUtc":"2026-10-01T09:00:00Z","readMs":5},
         "targets":[{"id":"0","label":"Client","server":"client-sql","database":"Sales","productVersion":"16.0","collation":"x","same":1,"formattingOnly":0,"different":1,"missingInTarget":0,"onlyInTarget":0,"unavailable":0,"totalObjects":2,"matchPercent":50,"readMs":4}],
         "rows":[{"key":"1|DBO.A","type":"table","schema":"dbo","name":"A","cells":[{"status":"different","addedLines":1,"removedLines":0}],"worst":"different","differingTargets":1},
                 {"key":"3|DBO.P","type":"storedProcedure","schema":"dbo","name":"P","cells":[{"status":"same"}],"worst":"same"}],
         "byType":[{"type":"table","total":1,"withDifferences":1}],
         "timings":{"read":5},"warnings":["w"]}
        """;

    [Fact]
    public void An_old_compare_result_reads_as_sql_server_and_round_trips()
    {
        var result = JsonSerializer.Deserialize<CompareResult>(OldResult, Api)!;

        Assert.Equal(DatabaseEngine.SqlServer, result.Source.Engine);
        Assert.Equal(DatabaseEngine.SqlServer, Assert.Single(result.Targets).Engine);
        Assert.Equal(["1|DBO.A", "3|DBO.P"], result.Rows.Select(r => r.Key));
        Assert.Equal(ObjectStatus.Different, result.Rows[0].Cells[0].Status);

        var again = JsonSerializer.Deserialize<CompareResult>(JsonSerializer.Serialize(result, Api), Api)!;
        Assert.Equal(JsonSerializer.Serialize(result, Api), JsonSerializer.Serialize(again, Api));
    }

    [Fact]
    public void An_old_share_bundle_reads_and_round_trips()
    {
        var json = $$$$"""
            {"token":"t0k","sourceLabel":"Dev","hostName":"PC","comparedUtc":"2026-10-01T09:00:00Z","result":{{{{OldResult}}}},
             "objects":{"1|DBO.A":{"key":"1|DBO.A","typeLabel":"Table","schema":"dbo","name":"A","isModule":false,"hasStructure":true,
                        "language":"sql","sourceExists":true,"sourceText":"CREATE TABLE [dbo].[A] ([x] int NULL);","sourceLabel":"Dev",
                        "targets":[{"id":"0","label":"Client","status":"different","exists":true,"text":"CREATE TABLE [dbo].[A] ();","structure":[]}]}}}
            """;
        var bundle = JsonSerializer.Deserialize<ShareBundle>(json, Api)!;

        Assert.Equal(DatabaseEngine.SqlServer, bundle.Result.Source.Engine);
        Assert.Equal(ObjectStatus.Different, bundle.Objects["1|DBO.A"].Targets[0].Status);
        var once = JsonSerializer.Serialize(bundle, Api);
        Assert.Equal(once, JsonSerializer.Serialize(JsonSerializer.Deserialize<ShareBundle>(once, Api), Api));
    }

    [Fact]
    public void An_old_history_row_reads_as_sql_server_and_keeps_its_signature()
    {
        // compare_history.source_json / targets_json: the default serializer, as SchemaScopeStore writes them
        var source = JsonSerializer.Deserialize<CompareHistoryDatabase>("""{"ConnectionId":"c1","Label":"Dev","Server":"dev-sql","Database":"Sales"}""")!;
        var targets = JsonSerializer.Deserialize<List<CompareHistoryDatabase>>("""[{"ConnectionId":null,"Label":"Client","Server":"Client-SQL","Database":"Sales"}]""")!;

        Assert.Equal(DatabaseEngine.SqlServer, source.Engine);
        // exactly the signature older builds stored, so the row is still found and updated
        Assert.Equal("c1/sales=>client-sql/sales", CompareHistoryEntry.BuildSignature(source, targets));

        targets[0].Engine = DatabaseEngine.PostgreSql;
        Assert.Equal("c1/sales=>1:client-sql/sales", CompareHistoryEntry.BuildSignature(source, targets));

        var json = JsonSerializer.Serialize(source);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<CompareHistoryDatabase>(json)));
    }

    [Fact]
    public void An_old_profile_round_trips()
    {
        var options = new CompareOptions();
        var json = JsonSerializer.Serialize(options);   // as profiles.options_json holds it
        var back = JsonSerializer.Deserialize<CompareOptions>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back));
    }

    [Fact]
    public void The_history_and_profile_tables_read_back_through_the_store()
    {
        using var temp = new TempStore();
        temp.Store.SaveProfile(new CompareProfile { Name = "p", Options = new CompareOptions() });
        temp.Store.SaveHistory(new CompareHistoryEntry
        {
            Source = new CompareHistoryDatabase { Server = "a", Database = "x" },
            Targets = [new CompareHistoryDatabase { Server = "b", Database = "x" }],
        });

        Assert.Single(temp.Store.GetProfiles());
        var h = Assert.Single(temp.Store.GetHistory());
        Assert.Equal("a/x=>b/x", h.Signature);
        Assert.Equal(DatabaseEngine.SqlServer, h.Source.Engine);
    }

    [Fact]
    public void A_connection_from_todays_ui_is_sql_server_and_an_engine_name_reads_as_that_engine()
    {
        var old = JsonSerializer.Deserialize<ConnectionSettings>(
            """{"id":"x","server":"prod","database":"Sales","auth":"sqlLogin","userName":"u","password":"********"}""", Api)!;
        Assert.Equal(DatabaseEngine.SqlServer, old.Engine);
        Assert.Null(old.Port);
        Assert.Null(old.Ssl);

        var pg = JsonSerializer.Deserialize<ConnectionSettings>(
            """{"engine":"postgreSql","server":"pg","port":5433,"ssl":"verifyFull","auth":"password"}""", Api)!;
        Assert.Equal(DatabaseEngine.PostgreSql, pg.Engine);
        Assert.Equal(5433, pg.Port);
        Assert.Equal(SslMode.VerifyFull, pg.Ssl);
        Assert.Equal(AuthMode.Password, pg.Auth);
    }

    [Fact]
    public void Persisted_enum_numbers_never_move()
    {
        Assert.Equal(0, (int)DatabaseEngine.SqlServer);
        Assert.Equal(1, (int)DatabaseEngine.PostgreSql);
        Assert.Equal(2, (int)DatabaseEngine.MySql);
        Assert.Equal(3, (int)DatabaseEngine.MariaDb);
        Assert.Equal(4, (int)AuthMode.AzureAdDefault);
        Assert.Equal(5, (int)AuthMode.Password);
        Assert.Equal(7, (int)AuthMode.ClientCertificate);
    }

    [Fact]
    public void Object_keys_are_byte_for_byte_what_history_and_shares_hold()
    {
        Assert.Equal("1|DBO.ORDER DETAILS", new DbObject { Type = DbObjectType.Table, Schema = "dbo", Name = "Order Details" }.Key);
        Assert.Equal("3|SALES.P_X", new DbObject { Type = DbObjectType.StoredProcedure, Schema = "Sales", Name = "p_x" }.Key);
    }
}

/// <summary>ScriptEndpoint's trailing engine (§2.4): the existing four-argument constructions and record equality are unchanged.</summary>
public class ScriptEndpointEngineTests
{
    [Fact]
    public void The_engine_defaults_to_sql_server_and_takes_part_in_equality()
    {
        var a = new ScriptEndpoint("Dev", "s", "d", "cs");
        Assert.Equal(DatabaseEngine.SqlServer, a.Engine);
        Assert.Equal(a, new ScriptEndpoint("Dev", "s", "d", "cs", DatabaseEngine.SqlServer));
        Assert.NotEqual(a, a with { Engine = DatabaseEngine.PostgreSql });
    }
}

/// <summary>BuildIndex keeps both objects that share a key, with a suffix and a warning, instead of dropping one (§2.4).</summary>
public class BuildIndexTests
{
    private static DbObject Table(string name, string text) => new()
    {
        Type = DbObjectType.Table, Schema = "public", Name = name, CompareText = text, Hash = text
    };

    [Fact]
    public void Duplicates_are_kept_under_their_exact_names_with_one_warning()
    {
        var snapshot = new SchemaSnapshot
        {
            Label = "pg",
            Objects = [Table("Order", "a"), Table("order", "b"), Table("ORDER", "c"), Table("order", "b2"), Table("x", "d")]
        };

        var index = snapshot.BuildIndex();
        snapshot.BuildIndex();   // built again by the session: still one warning

        Assert.Equal(5, index.Count);
        Assert.False(index.ContainsKey("1|PUBLIC.ORDER"));
        Assert.Equal("a", index["1|PUBLIC.ORDER#public.Order"].CompareText);
        Assert.Equal("b", index["1|PUBLIC.ORDER#public.order"].CompareText);
        Assert.Equal("b2", index["1|PUBLIC.ORDER#public.order#2"].CompareText);
        Assert.Equal("c", index["1|PUBLIC.ORDER#public.ORDER"].CompareText);
        Assert.Equal("d", index["1|PUBLIC.X"].CompareText);
        var warning = Assert.Single(snapshot.Warnings);
        Assert.Contains("4 Table objects have one name when case is ignored (public.Order, public.order, public.ORDER, public.order)", warning);
    }

    [Fact]
    public void No_duplicates_no_warning_and_the_compare_reports_them()
    {
        var clean = new SchemaSnapshot { Label = "src", Objects = [Table("x", "d")] };
        Assert.Single(clean.BuildIndex());
        Assert.Empty(clean.Warnings);

        var twins = new SchemaSnapshot { Label = "tgt", Objects = [Table("x", "d"), Table("X", "e")] };
        var result = new CompareEngine(new CompareOptions()).Compare(clean, [twins]);

        Assert.Contains(result.Warnings, w => w.StartsWith("tgt: 2 Table objects", StringComparison.Ordinal));
        Assert.Equal(ObjectStatus.OnlyInTarget, result.Rows.Single(r => r.Key == "1|PUBLIC.X#public.X").Cells[0].Status);
        Assert.Equal(ObjectStatus.Same, result.Rows.Single(r => r.Key == "1|PUBLIC.X#public.x").Cells[0].Status);
    }

    /// <summary>
    /// The review case: the source has only USERS, the case-sensitive target has Users and USERS, read in that order.
    /// Pairing by position compared USERS with Users (Different) and called the identical USERS extra - a wrong ALTER
    /// and a wrong DROP. Pairing by exact name finds USERS the same and only Users extra, whichever order either side reads in.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shared_key_pairs_objects_by_exact_name_across_sides(bool reversed)
    {
        var src = new SchemaSnapshot { Label = "src", Objects = [Table("USERS", "same")] };
        List<DbObject> objects = [Table("Users", "other"), Table("USERS", "same")];
        if (reversed) objects.Reverse();
        var tgt = new SchemaSnapshot { Label = "tgt", Objects = objects };

        var result = new CompareEngine(new CompareOptions()).Compare(src, [tgt]);

        Assert.Equal(2, result.Rows.Count);
        var users = result.Rows.Single(r => r.Name == "USERS");
        Assert.Equal(ObjectStatus.Same, users.Cells[0].Status);
        var extra = result.Rows.Single(r => r.Name == "Users");
        Assert.Equal(ObjectStatus.OnlyInTarget, extra.Cells[0].Status);

        // the session that serves the UI keys its indexes exactly as the rows are keyed
        var session = new CompareSession { Result = result, Source = src, Targets = [tgt] };
        Assert.Same(src.Objects[0], session.SourceIndex[users.Key]);
        Assert.Equal("same", session.TargetIndexes[0][users.Key].CompareText);
        Assert.Equal("other", session.TargetIndexes[0][extra.Key].CompareText);
        Assert.False(session.SourceIndex.ContainsKey(extra.Key));
    }

    /// <summary>The same case read from a real case-sensitive database by the SQL Server reader.</summary>
    [LocalDbFact]
    public async Task A_case_sensitive_sql_server_database_pairs_by_exact_name()
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        var srcDb = LocalDb.NewName(id, "CsSrc");
        var tgtDb = LocalDb.NewName(id, "CsTgt");
        try
        {
            LocalDb.Exec("master", $"CREATE DATABASE [{srcDb}] COLLATE Latin1_General_CS_AS");
            LocalDb.Exec("master", $"CREATE DATABASE [{tgtDb}] COLLATE Latin1_General_CS_AS");
            LocalDb.Exec(srcDb, "CREATE TABLE dbo.USERS (Id int NOT NULL);");
            LocalDb.Exec(tgtDb, "CREATE TABLE dbo.Users (Id bigint NULL);\nGO\nCREATE TABLE dbo.USERS (Id int NOT NULL);");

            var reader = new SchemaScope.SqlServer.SqlServerSchemaReader { Cache = null };
            async Task<SchemaSnapshot> Read(string db) => await reader.ReadAsync(new SchemaSource
            {
                Id = db, Label = db, Server = LocalDb.Server, Database = db, ConnectionString = LocalDb.Conn(db),
            }, new CompareOptions());
            var src = await Read(srcDb);
            var tgt = await Read(tgtDb);
            Assert.False(src.Failed, src.Error);
            Assert.False(tgt.Failed, tgt.Error);

            var result = new CompareEngine(new CompareOptions()).Compare(src, [tgt]);
            var tables = result.Rows.Where(r => r.Type == DbObjectType.Table).ToList();
            Assert.Equal(2, tables.Count);
            Assert.Equal(ObjectStatus.Same, tables.Single(r => r.Name == "USERS").Cells[0].Status);
            Assert.Equal(ObjectStatus.OnlyInTarget, tables.Single(r => r.Name == "Users").Cells[0].Status);
            Assert.Contains(result.Warnings, w => w.Contains("(dbo.Users, dbo.USERS)") || w.Contains("(dbo.USERS, dbo.Users)"));
        }
        finally
        {
            LocalDb.Drop(srcDb);
            LocalDb.Drop(tgtDb);
        }
    }

    [Fact]
    public void Engines_flow_into_the_result()
    {
        var src = new SchemaSnapshot { Label = "src", Objects = [Table("x", "d")] };
        var tgt = new SchemaSnapshot { Label = "tgt", Engine = DatabaseEngine.PostgreSql, Objects = [Table("x", "d")] };
        var result = new CompareEngine(new CompareOptions()).Compare(src, [tgt]);

        Assert.Equal(DatabaseEngine.SqlServer, result.Source.Engine);
        Assert.Equal(DatabaseEngine.PostgreSql, result.Targets[0].Engine);
        Assert.Empty(result.Warnings);
    }
}
