using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.PostgreSql;
using SchemaScope.SqlServer;
using NpgsqlSslMode = Npgsql.SslMode;

namespace SchemaScope.Engines.Tests;

/// <summary>The PostgreSQL lexer behind "only formatting differs": literals and quoted names are never formatting.</summary>
public class PgTextTests
{
    [Theory]
    [InlineData("SELECT 1 -- note\nFROM t", "SELECT 1 \nFROM t")]
    [InlineData("SELECT /* a /* nested */ still comment */ 1", "SELECT   1")]
    [InlineData("SELECT '-- not a comment', 'it''s /* no */'", "SELECT '-- not a comment', 'it''s /* no */'")]
    [InlineData("SELECT E'a\\'-- x' -- y", "SELECT E'a\\'-- x' ")]
    [InlineData("SELECT \"col--name\" FROM t", "SELECT \"col--name\" FROM t")]
    public void Comments_go_and_nothing_quoted_is_touched(string sql, string expected) =>
        Assert.Equal(expected, PgText.StripComments(sql));

    [Fact]
    public void A_routine_body_is_code_but_a_literal_inside_it_is_not()
    {
        const string body = "AS $function$\nBEGIN -- c\n  RETURN 'x -- y'; /* z */\nEND\n$function$";
        Assert.Equal("AS $function$\nBEGIN \n  RETURN 'x -- y';  \nEND\n$function$", PgText.StripComments(body));

        // a nested dollar quote is a string in the body: kept whole, comment-looking text and all
        const string nested = "AS $$ BEGIN EXECUTE $q$ SELECT 1 -- kept $q$; END $$";
        Assert.Contains("$q$ SELECT 1 -- kept $q$", PgText.StripComments(nested));
    }

    [Fact]
    public void Layout_comments_and_unquoted_case_are_loose()
    {
        Assert.Equal(PgText.Loosen("SELECT  a ,b\nFROM   T -- x"), PgText.Loosen("select a, b from t"));
        Assert.Equal(PgText.Loosen("AS $function$ BEGIN RETURN x * 2; END $function$"), PgText.Loosen("as $$begin\n  return x*2;\nend$$"));
        Assert.Equal(PgText.Loosen("count ( x )"), PgText.Loosen("COUNT(x)"));
    }

    [Theory]
    [InlineData("SELECT 'A'", "SELECT 'a'")]                    // a literal's case is data
    [InlineData("SELECT \"Users\".id", "SELECT users.id")]       // "Users" is not users
    [InlineData("SELECT 'a  b'", "SELECT 'a b'")]                // spaces inside a literal are data
    [InlineData("SELECT a < -1", "SELECT a <-1")]                // two operators, not one longer one
    [InlineData("SELECT $1", "SELECT $2")]
    [InlineData("SELECT E'\\n'", "SELECT E'\\t'")]
    public void A_real_difference_never_loosens_away(string a, string b) =>
        Assert.NotEqual(PgText.Loosen(a), PgText.Loosen(b));

    [Fact]
    public void Light_normalisation_only_drops_what_never_matters() =>
        Assert.Equal("CREATE VIEW v AS\n SELECT 1;", PgText.Light("﻿CREATE VIEW v AS  \r\n SELECT 1;\t\r\n\r\n"));

    [Fact]
    public void The_dialect_never_claims_a_parse()
    {
        var d = new PgDialect();
        Assert.False(d.CanDeepParse);
        Assert.False(d.ParsesIdentically("SELECT 1", "SELECT 1"));
        Assert.Equal(IdentifierFold.Exact, d.Fold);
        Assert.Equal("\"we\"\"ird\"", d.Quote("we\"ird"));
        Assert.Equal("pgsql", d.MonacoLanguage);
        Assert.DoesNotContain('$', d.WordSeparators);
    }
}

/// <summary>Object keys per fold: SQL Server's unchanged byte for byte, PostgreSQL's exact and with the overload signature.</summary>
public class KeyFoldTests
{
    [Fact]
    public void Sql_server_keys_are_todays()
    {
        var o = new DbObject { Type = DbObjectType.Table, Schema = "dbo", Name = "Users" };
        Assert.Equal("1|DBO.USERS", o.Key);
        o.Signature = "ignored for SQL Server";
        Assert.Equal("1|DBO.USERS", o.Key);
    }

    [Fact]
    public void Postgres_keys_keep_case_and_carry_the_signature()
    {
        DbObject Pg(DbObjectType t, string name, string? sig = null) =>
            new() { Type = t, Schema = "app", Name = name, Fold = IdentifierFold.Exact, Signature = sig };

        Assert.NotEqual(Pg(DbObjectType.Table, "Users").Key, Pg(DbObjectType.Table, "users").Key);
        Assert.Equal("4|app.add(integer, integer)", Pg(DbObjectType.ScalarFunction, "add", "integer, integer").Key);

        var snapshot = new SchemaSnapshot
        {
            Objects = [Pg(DbObjectType.Table, "Users"), Pg(DbObjectType.Table, "users"),
                Pg(DbObjectType.ScalarFunction, "add", "integer, integer"), Pg(DbObjectType.ScalarFunction, "add", "text, text")]
        };
        Assert.Equal(4, snapshot.BuildIndex().Count);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void Old_json_without_the_new_fields_reads_as_sql_server_and_writes_back_the_same()
    {
        const string json = """{"Type":1,"Schema":"dbo","Name":"a"}""";
        var o = System.Text.Json.JsonSerializer.Deserialize<DbObject>(json)!;
        Assert.Equal(IdentifierFold.Upper, o.Fold);
        var written = System.Text.Json.JsonSerializer.Serialize(o);
        Assert.DoesNotContain("Fold", written);
        Assert.DoesNotContain("Signature", written);
    }

    [Fact]
    public void Only_sql_server_pairs_go_through_the_t_sql_parse()
    {
        // two bodies ScriptDom would call the same code; on PostgreSQL they stay Different
        DbObject F(string body, IdentifierFold fold) => new()
        {
            Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = "p", Fold = fold, Definition = body,
            CompareText = body, Hash = body.GetHashCode().ToString(), LooseHash = body, LooseLength = 100
        };
        const string a = "CREATE PROCEDURE dbo.p AS SELECT 1";
        const string b = "CREATE PROCEDURE dbo.p AS\nSELECT 1;";

        var sql = new CompareEngine(new CompareOptions()).Compare(new SchemaSnapshot { Objects = [F(a, IdentifierFold.Upper)] },
            [new SchemaSnapshot { Objects = [F(b, IdentifierFold.Upper)] }]);
        Assert.Equal(ObjectStatus.FormattingOnly, sql.Rows[0].Cells[0].Status);

        var pg = new CompareEngine(new CompareOptions()).Compare(
            new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, Objects = [F(a, IdentifierFold.Exact)] },
            [new SchemaSnapshot { Engine = DatabaseEngine.PostgreSql, Objects = [F(b, IdentifierFold.Exact)] }]);
        Assert.Equal(ObjectStatus.Different, pg.Rows[0].Cells[0].Status);
    }
}

/// <summary>What a PostgreSQL connection from the UI may say, and how its string is built and read.</summary>
public class PgConnectionTests
{
    private static ConnectionSettings Pg(Action<ConnectionSettings>? edit = null)
    {
        var s = new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Server = "db.internal", Database = "app", Auth = AuthMode.Password,
            UserName = "deploy", Password = "hunter2"
        };
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public void An_ordinary_postgres_connection_passes() => Assert.Null(ConnectionRules.Problem(Pg()));

    [Theory]
    [InlineData(AuthMode.Windows)]
    [InlineData(AuthMode.SqlLogin)]
    [InlineData(AuthMode.AzureAdDefault)]
    public void Only_a_password_sign_in_is_offered(AuthMode auth) =>
        Assert.NotNull(ConnectionRules.Problem(Pg(s => s.Auth = auth)));

    [Fact]
    public void Unchecked_tls_needs_the_explicit_trust_tick()
    {
        Assert.NotNull(ConnectionRules.Problem(Pg(s => s.Ssl = SslMode.Require)));
        Assert.NotNull(ConnectionRules.Problem(Pg(s => s.Ssl = SslMode.Prefer)));
        Assert.Null(ConnectionRules.Problem(Pg(s => { s.Ssl = SslMode.Require; s.TrustServerCertificate = true; })));
        // no TLS at all is weaker still: the same explicit tick
        Assert.Contains("Disable", ConnectionRules.Problem(Pg(s => s.Ssl = SslMode.Disable)));
        Assert.Null(ConnectionRules.Problem(Pg(s => { s.Ssl = SslMode.Disable; s.TrustServerCertificate = true; })));
    }

    [Theory]
    [InlineData("postgres://deploy:hunter2@db/app")]
    [InlineData("postgresql://db/app")]
    [InlineData("Data Source=db;Initial Catalog=app")]
    [InlineData("Host=a:5432,b:5433")]
    [InlineData("Host=db;NotAKeyword=1")]
    public void Raw_strings_that_are_uris_or_unreadable_are_refused(string raw)
    {
        var problem = ConnectionRules.Problem(Pg(s => { s.UseRawConnectionString = true; s.RawConnectionString = raw; }));
        Assert.NotNull(problem);
        Assert.DoesNotContain("hunter2", problem);
    }

    [Fact]
    public void Engine_options_and_bad_ports_are_refused()
    {
        Assert.NotNull(ConnectionRules.Problem(Pg(s => s.Port = 70000)));
        Assert.Contains("secret", ConnectionRules.Problem(Pg(s => s.EngineOptions = new() { ["Password"] = "x" })));
        Assert.NotNull(ConnectionRules.Problem(Pg(s => s.EngineOptions = new() { ["Search Path"] = "x" })));
    }

    [Fact]
    public void Tls_is_verified_unless_trust_is_ticked()
    {
        Assert.Equal(NpgsqlSslMode.VerifyFull, PgConnectionService.EffectiveSslMode(Pg()));
        Assert.Equal(NpgsqlSslMode.Require, PgConnectionService.EffectiveSslMode(Pg(s => s.TrustServerCertificate = true)));
        Assert.Equal(NpgsqlSslMode.Disable, PgConnectionService.EffectiveSslMode(Pg(s => s.Ssl = SslMode.Disable)));

        var svc = new PgConnectionService();
        Assert.Contains("SSL Mode=VerifyFull", svc.BuildConnectionString(Pg()));
        // a raw string that says nothing about TLS is verified too, not left at the driver's unchecked default
        Assert.Contains("SSL Mode=VerifyFull", svc.BuildConnectionString(Pg(s => { s.UseRawConnectionString = true; s.RawConnectionString = "Host=db;Database=app"; })));
        Assert.Contains("SSL Mode=Disable", svc.BuildConnectionString(Pg(s => { s.UseRawConnectionString = true; s.RawConnectionString = "Host=db;Database=app;SSL Mode=Disable"; })));
    }

    [Fact]
    public void The_string_names_the_form_and_browse_overrides_the_database()
    {
        var svc = new PgConnectionService();
        var cs = svc.BuildConnectionString(Pg(s => s.Port = 6543), svc.BrowseDatabase);
        var ep = svc.ParseEndpoint(cs)!;
        Assert.Equal(["db.internal"], ep.Hosts);
        Assert.Equal(6543, ep.Port);
        Assert.Equal("postgres", ep.Database);
        Assert.Equal("PostgreSQL user: deploy", ep.LoginDescription);
        Assert.Contains("Application Name=SchemaScope", cs);
    }

    [Theory]
    [InlineData("postgres://u:pw@h/db")]
    [InlineData("Data Source=h;Initial Catalog=db")]
    [InlineData("Host=h;NotAKeyword=1")]
    [InlineData("Host=a:1,b:2")]
    [InlineData("")]
    public void Unreadable_strings_have_no_endpoint(string cs) => Assert.Null(new PgConnectionService().ParseEndpoint(cs));

    [Fact]
    public void Masking_and_secret_values_cover_npgsql_keys()
    {
        var svc = new PgConnectionService();
        const string cs = "Host=h;Username=u;Password=p4ss;SSL Password=ssl-pw;Database=d";
        Assert.Equal(["p4ss", "ssl-pw"], svc.SecretValues(cs).Order());
        var masked = svc.Mask(cs);
        Assert.DoesNotContain("p4ss", masked);
        Assert.DoesNotContain("ssl-pw", masked);
        Assert.Equal(["h"], svc.ParseEndpoint(masked)!.Hosts);
    }

    [Fact]
    public void The_identity_keys_hosts_and_targets_with_engine_and_port()
    {
        var id = new PgIdentity();
        var ep = new EndpointInfo(DatabaseEngine.PostgreSql, ["localhost", "db.corp.local"], null, null, "app", "");
        Assert.Equal([HostNames.Normalise("localhost"), "DB"], id.HostKeys(ep));
        Assert.Equal($"1|{HostNames.Normalise("localhost")}|5432|app", id.TargetKey(ep, "app"));
    }

    [Fact]
    public void Error_messages_carry_no_server_detail()
    {
        var ex = new Npgsql.PostgresException("duplicate key", "ERROR", "ERROR", "23505", detail: "Key (email)=(secret@example.com) exists.");
        var message = PgConnectionService.MessageOf(ex);
        Assert.Equal("23505: duplicate key", message);
        Assert.DoesNotContain("secret@example.com", message);
    }
}

/// <summary>The preview switch: off by default, enforced by the server, kept in the store.</summary>
public class EngineCatalogTests
{
    private static IEngineProvider[] Providers() => [new SqlServerEngineProvider(), new PgEngineProvider()];

    [Fact]
    public void Preview_engines_are_off_until_switched_on_and_the_choice_is_kept()
    {
        using var temp = new TempStore();
        var catalog = new EngineCatalog(Providers(), temp.Store);
        Assert.False(catalog.PreviewEnabled);
        Assert.Equal([DatabaseEngine.SqlServer], catalog.Enabled.All.Select(p => p.Engine));
        Assert.True(catalog.Carries(DatabaseEngine.PostgreSql));
        Assert.Equal(["PostgreSQL"], catalog.PreviewEngines.Select(p => p.DisplayName));

        catalog.SetPreview(true);
        Assert.Equal([DatabaseEngine.SqlServer, DatabaseEngine.PostgreSql], catalog.Enabled.All.Select(p => p.Engine));
        Assert.True(new EngineCatalog(Providers(), temp.Store).PreviewEnabled);

        catalog.SetPreview(false);
        Assert.False(catalog.Enabled.TryGet(DatabaseEngine.PostgreSql, out _));
    }

    [Fact]
    public void Postgres_offers_compare_and_scripts_but_not_apply()
    {
        var p = new PgEngineProvider();
        Assert.True(p.Capabilities.Preview);
        Assert.Equal([EngineFeature.Compare, EngineFeature.Scripts, EngineFeature.ObjectScripts], p.Capabilities.Features.Order());
        Assert.DoesNotContain(EngineFeature.Apply, p.Capabilities.Features);
        Assert.DoesNotContain(EngineFeature.Destructive, p.Capabilities.Features);
        Assert.Equal("postgres", p.Connections.BrowseDatabase);
        Assert.IsType<PgSchemaReader>(p.CreateReader());
        Assert.Equal(63, p.Capabilities.MaxIdentifierBytes);
        Assert.DoesNotContain(DbObjectType.Synonym, p.Capabilities.ObjectTypes);
    }
}
