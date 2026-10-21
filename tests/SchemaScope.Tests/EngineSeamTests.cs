using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Normalization;
using SchemaScope.Scripting;
using SchemaScope.Scripting.Planning;
using SchemaScope.SqlServer;

namespace SchemaScope.Tests;

/// <summary>R0b: the engine seam with SQL Server alone behind it - every SQL Server answer the same as before.</summary>
public class EngineSeamTests
{
    private static readonly SqlServerConnectionService Sql = new();

    [Fact]
    public void The_SQL_Server_only_registry_has_SQL_Server_and_nothing_else()
    {
        var registry = EngineRegistries.SqlServerOnly();

        var only = Assert.Single(registry.All);
        Assert.Equal(DatabaseEngine.SqlServer, only.Engine);
        Assert.Same(only, registry.Get(DatabaseEngine.SqlServer));
        foreach (var f in new[] { EngineFeature.Compare, EngineFeature.Scripts, EngineFeature.ObjectScripts, EngineFeature.Apply, EngineFeature.Destructive })
            Assert.True(registry.Supports(DatabaseEngine.SqlServer, f));
        Assert.False(registry.Supports(DatabaseEngine.SqlServer, EngineFeature.CrossCompare));
        Assert.False(registry.Supports(DatabaseEngine.PostgreSql, EngineFeature.Compare));
        Assert.False(registry.TryGet(DatabaseEngine.MySql, out _));
        Assert.Equal(DatabaseEngine.PostgreSql, Assert.Throws<EngineNotAvailableException>(() => registry.Get(DatabaseEngine.PostgreSql)).Engine);
    }

    [Fact]
    public void Two_providers_for_one_engine_are_refused() =>
        Assert.Throws<ArgumentException>(() => new EngineRegistry([new SqlServerEngineProvider(), new SqlServerEngineProvider()]));

    [Fact]
    public void The_provider_hands_out_todays_SQL_Server_classes()
    {
        var connections = new SqlServerConnectionService();
        var p = new SqlServerEngineProvider(connections);

        Assert.Same(connections, p.Connections);
        Assert.IsType<SqlServerSchemaReader>(p.CreateReader());
        Assert.Equal("master", p.Connections.BrowseDatabase);
        Assert.Equal(TransactionSemantics.PerFileTransactional, p.Capabilities.Ddl);
        Assert.False(p.Capabilities.Preview);
        Assert.Equal(Enum.GetValues<DbObjectType>().ToHashSet(), p.Capabilities.ObjectTypes);
    }

    [Theory]
    [InlineData("CREATE  PROCEDURE dbo.p AS\r\n  SELECT 1 -- note\r\n")]
    [InlineData("create view [dbo].[v] as select /* x */ 1 as a")]
    [InlineData("")]
    public void The_dialect_is_the_T_SQL_helpers_unchanged(string body)
    {
        var d = new SqlServerDialect();
        var o = new DbObject { Type = DbObjectType.StoredProcedure, Schema = "dbo", Name = "p", Definition = body };

        Assert.Equal(TSqlText.Light(body), d.LightNormalize(body));
        Assert.Equal(TSqlText.StripComments(body), d.StripComments(body));
        Assert.Equal(TSqlText.CanonicalHeaderName(body, "dbo", "p"), d.CanonicalHeader(body, o));
        Assert.Equal(SchemaNormalizer.Loosen(body), d.Loosen(body));
        Assert.Equal(SchemaNormalizer.ParsesIdentically(body, body), d.ParsesIdentically(body, body));
        Assert.Equal(CanonicalScriptWriter.Write(o, new CompareOptions()), d.CanonicalScript(o, new CompareOptions()));
    }

    [Fact]
    public void The_dialect_quotes_and_folds_as_SQL_Server_does()
    {
        var d = new SqlServerDialect();
        Assert.Equal(Ddl.Bracket("evil]; DROP TABLE x; --"), d.Quote("evil]; DROP TABLE x; --"));
        Assert.Equal(IdentifierFold.Upper, d.Fold);
        Assert.Equal("dbo", d.DefaultSchema);
        Assert.Equal(new CompareOptions().ExcludedSchemas, d.DefaultExcludedSchemas);
        Assert.True(d.CanDeepParse);
        Assert.True(d.ParsesIdentically("SELECT  1", "select 1"));
        // a T-SQL name keeps @, # and $
        Assert.DoesNotContain('@', d.WordSeparators);
        Assert.DoesNotContain('#', d.WordSeparators);
        Assert.DoesNotContain('$', d.WordSeparators);
        Assert.Contains('.', d.WordSeparators);
    }

    // ------------------------------------------------------------ ParseEndpoint against the code it replaced

    public static TheoryData<string> Strings => new()
    {
        "Server=tcp:prod-sql,1433;Database=Sales;Integrated Security=true",
        @"Data Source=PROD\A;Failover Partner=PROD2\A;Initial Catalog=Sales;User ID=deploy;Password=pw",
        "Server=prod;Database=Sales;Authentication=Active Directory Password;User ID=a@b.example;Password=pw",
        "Server=prod;Authentication=Active Directory Default",
        "Server=prod;Authentication=Active Directory Interactive;User ID=a@b.example",
        "Server=' spaced ';Database=' D '",
        "Database=OnlyADatabase;Integrated Security=true",
        "Server=prod;Failover Partner= ;Database=x",
        "Server=prod;Bogus=1",
        "Server=prod;Password='a;b",
        "not a connection string",
        "",
    };

    /// <summary>SchemaScopeApi.Endpoint's server and database, LoginOf and SavedServers as they read before R0b.</summary>
    private static (string Server, string Database, string Login, List<string>? Saved, List<string>? Partner) Before(string cs, string formServer, string formDb)
    {
        string server = formServer, database = formDb, login;
        List<string>? saved, partner;
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            if (!string.IsNullOrWhiteSpace(b.DataSource)) server = b.DataSource;
            if (!string.IsNullOrWhiteSpace(b.InitialCatalog)) database = b.InitialCatalog;
            login = b.IntegratedSecurity ? $"Windows: {Environment.UserDomainName}\\{Environment.UserName}"
                : b.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword ? $"SQL login: {b.UserID}"
                : string.IsNullOrEmpty(b.UserID) ? "Microsoft Entra ID" : $"Microsoft Entra ID: {b.UserID}";
            saved = string.IsNullOrWhiteSpace(b.DataSource) ? null : [b.DataSource.Trim()];
            if (saved is not null && !string.IsNullOrWhiteSpace(b.FailoverPartner)) saved.Add(b.FailoverPartner.Trim());
            partner = string.IsNullOrWhiteSpace(b.FailoverPartner) ? [] : [b.FailoverPartner.Trim()];
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            login = "Unknown";
            saved = null;
            partner = null;
        }
        return (server, database, login, saved, partner);
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void ParseEndpoint_reads_every_string_as_the_old_code_did(string cs)
    {
        var before = Before(cs, "form-server", "form-db");
        var ep = Sql.ParseEndpoint(cs);

        if (before.Saved is null && before.Login == "Unknown")
        {
            Assert.Null(ep);
            return;
        }
        Assert.NotNull(ep);
        Assert.Equal(before.Login, ep.LoginDescription);
        Assert.Equal(before.Server, ep.Servers!.FirstOrDefault() ?? "form-server");
        Assert.Equal(before.Database, string.IsNullOrWhiteSpace(ep.Database) ? "form-db" : ep.Database);
        Assert.Equal(before.Saved, ep.Servers!.Count > 0 ? [.. ep.Servers.Select(s => s.Trim())] : null);
        if (ep.Servers.Count > 0) Assert.Equal(before.Partner, [.. ep.Servers.Skip(1).Select(s => s.Trim())]);
    }

    [Fact]
    public void ParseEndpoint_splits_the_data_source_into_host_instance_and_port()
    {
        var ep = Sql.ParseEndpoint(@"Data Source=tcp:prod.corp.example\A,1444;Failover Partner=prod2\A;Initial Catalog=Sales;Integrated Security=true")!;

        Assert.Equal(["prod.corp.example", "prod2"], ep.Hosts);
        Assert.Equal("A", ep.Instance);
        Assert.Equal(1444, ep.Port);
        Assert.Equal("Sales", ep.Database);
        Assert.Equal(DatabaseEngine.SqlServer, ep.Engine);
    }

    [Fact]
    public void Secret_values_and_masking_never_show_the_password()
    {
        const string cs = "Server=prod;User ID=u;Password='p;w';Database=d";

        Assert.Equal(["p;w"], Sql.SecretValues(cs));
        var masked = Sql.Mask(cs);
        Assert.DoesNotContain("p;w", masked);
        Assert.Contains("Server=prod", masked);
        Assert.Equal("********", Sql.Mask("postgres://u:secret@host/db"));
    }

    [Fact]
    public void The_identity_matches_the_old_server_spelling_rule()
    {
        Assert.True(ServerNames.Same(@"tcp:LOCALHOST\X,1433", $@"{Environment.MachineName}\x"));
        Assert.Equal(SqlServerIdentity.Normalise(@"np:\\prod\pipe"), ServerNames.Normalise(@"np:\\prod\pipe"));
        var id = new SqlServerIdentity();
        var ep = Sql.ParseEndpoint(@"Server=prod.corp.example\A;Database=d")!;
        Assert.Equal(["PROD"], id.HostKeys(ep));
        Assert.Equal(id.TargetKey(ep, "d"), id.TargetKey(Sql.ParseEndpoint(@"Server=tcp:PROD\a,1433;Database=d")! with { Port = null }, "d"));
    }

    // ------------------------------------------------------------ CompareRunner, scripts, apply

    private sealed class FakeReader(string label) : ISchemaReader
    {
        public List<string> Read { get; } = [];

        public Task<SchemaSnapshot> ReadAsync(SchemaSource source, CompareOptions options, IProgress<ReadProgress>? progress = null, CancellationToken ct = default)
        {
            lock (Read) Read.Add(source.Id);
            return Task.FromResult(new SchemaSnapshot { Label = label, Server = source.Server, Database = source.Database, Engine = source.Engine });
        }
    }

    [Fact]
    public async Task The_runner_asks_the_factory_for_each_databases_reader()
    {
        var sql = new FakeReader("sql");
        var other = new FakeReader("other");
        var runner = new CompareRunner(src => src.Engine == DatabaseEngine.SqlServer ? sql : other);

        var run = await runner.RunAsync(new SchemaSource { Id = "source" },
            [new SchemaSource { Id = "t0" }, new SchemaSource { Id = "t1", Engine = DatabaseEngine.PostgreSql }], new CompareOptions());

        Assert.Equal(["source", "t0"], sql.Read.Order());
        Assert.Equal(["t1"], other.Read);
        Assert.Equal(2, run.Targets.Count);
    }

    [Fact]
    public async Task The_single_reader_constructor_still_reads_everything_with_one_reader()
    {
        var reader = new FakeReader("one");
        await new CompareRunner(reader).RunAsync(new SchemaSource { Id = "source" }, [new SchemaSource { Id = "t0" }], new CompareOptions());
        Assert.Equal(["source", "t0"], reader.Read.Order());
    }

    [Fact]
    public void Only_SQL_Server_to_SQL_Server_has_a_script_generator()
    {
        var f = new ScriptGeneratorFactory();
        Assert.IsType<SqlServerScriptGenerator>(f.For(DatabaseEngine.SqlServer, DatabaseEngine.SqlServer));
        Assert.Null(f.For(DatabaseEngine.SqlServer, DatabaseEngine.PostgreSql));
        Assert.Null(f.For(DatabaseEngine.PostgreSql, DatabaseEngine.PostgreSql));
        Assert.Null(f.For(DatabaseEngine.MySql, DatabaseEngine.SqlServer));
    }

    [Fact]
    public void The_apply_statics_forward_to_the_SQL_Server_executor()
    {
        var x = SqlServerApplyExecutor.Instance;
        Assert.Equal(DatabaseEngine.SqlServer, x.Engine);
        Assert.Equal(ApplyService.SqlcmdSession, SqlServerApplyExecutor.SqlcmdSession);
        Assert.Equal(ApplyService.SqlcmdOptions, SqlServerApplyExecutor.SqlcmdOptions);
        Assert.Equal(ApplyService.TargetKey(" prod ", " Db "), x.TargetKey(null, " prod ", " Db "));
        var facts = new TargetFacts("PROD|Db", new HashSet<string>());
        Assert.Equal("PROD|Db", x.TargetKey(facts, "alias", "Db"));
        const string text = "SELECT 1\r\nGO\r\nSELECT 2\r\n";
        Assert.Equal(ScriptText.SplitBatches(text), x.Splitter.Split(text));
    }

    [Fact]
    public void The_apply_service_runs_SQL_Server_files_with_the_SQL_Server_executor()
    {
        using var temp = new TempStore();
        var service = new ApplyService(temp.Store, new WriteMode(), NullLogger<ApplyService>.Instance);

        Assert.Same(SqlServerApplyExecutor.Instance, service.ExecutorFor(DatabaseEngine.SqlServer));
        Assert.Null(service.ExecutorFor(DatabaseEngine.PostgreSql));
    }

    // ------------------------------------------------------------ the planning helpers CoreRun forwards to

    private sealed record Row(string Name, string Key, bool State);

    [Fact]
    public void Todo_keeps_the_last_row_of_a_key_and_drops_keys_the_target_has()
    {
        Row[] src = [new("b1", "b", false), new("a", "a", false), new("b2", "b", false), new("c", "c", false)];
        Row[] tgt = [new("c", "c", false)];

        var todo = PlanRows.Todo(src, tgt, r => new PlanKey(r.Key, r.Key));

        Assert.Equal(["a", "b2"], todo.Select(r => r.Name));
    }

    [Fact]
    public void Pairs_match_by_name_then_by_state()
    {
        Row[] src = [new("x", "k", true), new("same", "k", false)];
        Row[] tgt = [new("other", "k", false), new("other2", "k", true), new("same", "k", true)];

        var pairs = PlanRows.Pairs(src, tgt, r => new PlanKey(r.Key, r.Key), r => r.Name, r => r.State, StringComparer.Ordinal);

        Assert.Equal([("same", "same"), ("x", "other2")], pairs.Select(p => (p.Source.Name, p.Target.Name)).OrderBy(p => p.Item1));
    }

    [Fact]
    public void Holding_repeats_until_a_pass_holds_nothing()
    {
        // c reads b, b reads a; a is held, so b and then c are stranded - in two more passes
        var reads = new Dictionary<string, string?> { ["c"] = "b", ["b"] = "a", ["a"] = null };
        var held = new HashSet<string> { "a" };
        var passes = 0;

        PlanOrder.HoldUntilStable(new[] { "c", "b", "a" }, m =>
        {
            if (m == "c") passes++;
            if (held.Contains(m) || reads[m] is not { } r || !held.Contains(r)) return false;
            held.Add(m);
            return true;
        });

        Assert.Equal(["a", "b", "c"], held.Order());
        Assert.Equal(3, passes);
    }

    [Fact]
    public void StableOrder_is_the_same_function_through_the_forwarder()
    {
        (int, int)[] edges = [(2, 0), (1, 2), (3, 4), (4, 3)];
        List<int> f1 = [], f2 = [];
        Assert.Equal(PlanOrder.StableOrder(5, edges, f1), CoreRun.StableOrder(5, edges, f2));
        Assert.Equal(f1, f2);
        Assert.Equal([3, 4], f1);
    }
}

/// <summary>The share viewer's container has no engine, no executor: nothing that could reach a database.</summary>
public class ViewerEngineIsolationTests
{
    [Fact]
    public async Task The_viewer_resolves_no_engine_provider_registry_or_apply_executor()
    {
        using var temp = new TempStore();
        var viewer = new ViewerHost(Shares.NewStore(temp), NullLogger<ViewerHost>.Instance);
        await using var app = viewer.Build(0, null, IPAddress.Loopback);

        Assert.Null(app.Services.GetService<IEngineProvider>());
        Assert.Empty(app.Services.GetServices<IEngineProvider>());
        Assert.Null(app.Services.GetService<EngineRegistry>());
        Assert.Null(app.Services.GetService<IApplyExecutor>());
        Assert.Empty(app.Services.GetServices<IApplyExecutor>());
        Assert.Null(app.Services.GetService<IScriptGeneratorFactory>());
    }

    [Fact]
    public async Task The_owner_app_resolves_them()
    {
        using var temp = new TempStore();
        await using var app = SchemaScopeApi.Build([], desktop: false, LocalAuth.Mint(), storePath: temp.DbPath);

        var registry = app.Services.GetRequiredService<EngineRegistry>();
        Assert.Equal(DatabaseEngine.SqlServer, Assert.Single(registry.All).Engine);
        Assert.Same(app.Services.GetRequiredService<SqlServerConnectionService>(), registry.Get(DatabaseEngine.SqlServer).Connections);
        Assert.Same(SqlServerApplyExecutor.Instance, app.Services.GetRequiredService<IApplyExecutor>());
        Assert.NotNull(app.Services.GetRequiredService<CompareService>());
        Assert.NotNull(app.Services.GetRequiredService<ApplyService>().ExecutorFor(DatabaseEngine.SqlServer));
    }
}

/// <summary>GET /api/engines on the owner API, started for real.</summary>
public sealed class EnginesRouteTests : IAsyncLifetime
{
    private readonly TempStore _temp = new();
    private readonly LocalAuth _auth = LocalAuth.Mint();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _app = SchemaScopeApi.Build([], desktop: false, _auth, storePath: _temp.DbPath);
        _app.Urls.Clear();
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _temp.Dispose();
    }

    [Fact]
    public async Task With_the_token_it_describes_SQL_Server_and_nothing_secret()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/engines");
        request.Headers.Add(LocalAuth.HeaderName, _auth.Token);

        var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var engine = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("sqlServer", engine.GetProperty("engine").GetString());
        Assert.Equal("SQL Server", engine.GetProperty("displayName").GetString());
        Assert.False(engine.GetProperty("preview").GetBoolean());
        Assert.Equal("master", engine.GetProperty("browseDatabase").GetString());
        Assert.Equal("perFileTransactional", engine.GetProperty("transactions").GetString());
        Assert.Equal(["windows", "sqlLogin", "azureAdPassword", "azureAdInteractive", "azureAdDefault"],
            engine.GetProperty("auth").EnumerateArray().Select(a => a.GetProperty("mode").GetString()));
        Assert.Contains("apply", engine.GetProperty("capabilities").GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain(_temp.DbPath.Replace("\\", "\\\\"), body);
    }

    [Fact]
    public async Task Without_the_token_it_is_refused()
    {
        var response = await _http.GetAsync("/api/engines");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A finished run whose one target is another engine (a session no request can make yet).</summary>
    private string OtherEngineRun()
    {
        var session = _app.Services.GetRequiredService<CompareSessionStore>().Create();
        session.Status = "done";
        session.Result = new CompareResult
        {
            Source = new SnapshotInfo { Label = "Dev", Server = "dev", Database = "app" },
            Targets = [new TargetSummary { Label = "Pg", Engine = DatabaseEngine.PostgreSql, Server = "pg", Database = "app" }]
        };
        session.SourceConnection = new SchemaSource { Label = "Dev", Server = "dev", Database = "app", ConnectionString = "Server=dev;Database=app;Integrated Security=true" };
        session.TargetConnections = [new SchemaSource { Label = "Pg", Engine = DatabaseEngine.PostgreSql, Server = "pg", Database = "app", ConnectionString = "Host=pg;Database=app" }];
        return session.Id;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string json)
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(LocalAuth.HeaderName, _auth.Token);
        return _http.SendAsync(request);
    }

    [Fact]
    public async Task Scripts_for_a_pair_with_no_generator_are_a_400()
    {
        var folder = JsonSerializer.Serialize(Path.Combine(_temp.Folder, "scripts"));
        var response = await Send(HttpMethod.Post, $"/api/compare/{OtherEngineRun()}/scripts",
            $$"""{ "targetIndex": 0, "options": { "outputFolder": {{folder}}, "sections": ["newColumns"] } }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not available for SqlServer to PostgreSql", await response.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Path.Combine(_temp.Folder, "scripts")));
    }

    [Fact]
    public async Task Apply_to_an_engine_without_an_executor_is_refused_before_anything_else()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/write-mode", """{ "enabled": true, "confirm": "ENABLE" }""")).StatusCode);

        var response = await Send(HttpMethod.Post, $"/api/compare/{OtherEngineRun()}/apply",
            """{ "planId": "nope", "targetIndex": 0, "sections": ["01_columns"], "confirmDatabase": "app" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Applying scripts to PostgreSql is not available", await response.Content.ReadAsStringAsync());
        Assert.Empty(_app.Services.GetRequiredService<SchemaScope.Core.Storage.SchemaScopeStore>().GetApplyRuns());
    }
}
