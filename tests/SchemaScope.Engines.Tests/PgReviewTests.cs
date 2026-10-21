using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.PostgreSql;

namespace SchemaScope.Engines.Tests;

/// <summary>Secret keys and the destination of a PostgreSQL string, read the way Npgsql reads them.</summary>
public class PgSecretKeyTests
{
    [Theory]
    [InlineData("Host=pg;Username=u;PSW=hunter2")]
    [InlineData("Host=pg;Username=u;PWD=hunter2")]
    [InlineData("Host=pg;Username=u;Password=hunter2")]
    [InlineData("Host=pg;Username=u;SSL Password=hunter2")]
    [InlineData("Host=pg;Username=u;SslPassword=hunter2")]
    public void Every_npgsql_password_key_is_masked_and_scrubbed(string cs)
    {
        var svc = new PgConnectionService();
        Assert.Equal(["hunter2"], svc.SecretValues(cs));
        Assert.DoesNotContain("hunter2", svc.Mask(cs));
        Assert.Equal(["pg"], svc.ParseEndpoint(svc.Mask(cs))!.Hosts);

        var raw = new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Auth = AuthMode.Password, UseRawConnectionString = true, RawConnectionString = cs
        };
        Assert.DoesNotContain("hunter2", raw.WithoutSecrets().RawConnectionString);
        Assert.DoesNotContain("hunter2", SecretScrubber.Scrub($"failed: {cs}", SecretScrubber.Secrets([cs])));
    }

    [Fact]
    public void Psw_is_a_secret_key() => Assert.True(SecretKeys.IsSecret("PSW"));

    [Theory]
    [InlineData("Host=good;Server=evil;Username=u")]
    [InlineData("Server=evil;Host=good;Username=u")]
    [InlineData("Host=good;Database=a;DB=b;Username=u")]
    public void Two_names_for_the_host_or_database_leave_the_destination_unknown(string cs)
    {
        // Npgsql takes the last one written; rather than guess, the string has no endpoint, so nothing is released
        // and Protected refuses
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, cs));
        Assert.Null(new PgConnectionService().ParseEndpoint(cs));
        Assert.NotNull(ConnectionRules.Problem(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Auth = AuthMode.Password, UseRawConnectionString = true, RawConnectionString = cs
        }));
    }

    [Theory]
    [InlineData("Server=good;Data Source=evil;Integrated Security=true")]
    [InlineData("Address=good;Server=evil;Integrated Security=true")]
    [InlineData("Server=s;Initial Catalog=a;Database=b;Integrated Security=true")]
    public void Sql_server_strings_with_two_names_for_one_setting_are_unknown_too(string cs) =>
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.SqlServer, cs));

    [Fact]
    public void One_name_each_still_reads()
    {
        Assert.Equal(["good"], ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Server=good;DB=a")!.Hosts);
        Assert.Equal("a", ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Server=good;DB=a")!.Database);
        Assert.Equal(["s"], ConnectionEndpoints.Parse(DatabaseEngine.SqlServer, "Data Source=s;Database=a;Integrated Security=true")!.Hosts);
    }
}

/// <summary>What the driver would send that SchemaScope never chose: PGPASSWORD, pgpass.conf, the Windows identity.</summary>
public class PgCredentialTests
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
    public void A_form_connection_without_a_password_is_not_attempted()
    {
        Assert.Null(ConnectionRules.MissingPassword(Pg()));
        Assert.NotNull(ConnectionRules.MissingPassword(Pg(s => s.Password = "")));
        Assert.NotNull(ConnectionRules.MissingPassword(Pg(s => s.Password = null)));
        Assert.Throws<ArgumentException>(() => new PgConnectionService().BuildConnectionString(Pg(s => s.Password = null)));
        // a raw string is used as typed; SQL Server is unchanged
        Assert.Null(ConnectionRules.MissingPassword(Pg(s => { s.Password = null; s.UseRawConnectionString = true; s.RawConnectionString = "Host=h"; })));
        Assert.Null(ConnectionRules.MissingPassword(new ConnectionSettings { Server = "s", Auth = AuthMode.SqlLogin, UserName = "sa" }));
    }

    /// <summary>
    /// A fake server on a loopback port that answers the startup message with an authentication request, then
    /// reports what the client sent back: null when it sent nothing before closing.
    /// </summary>
    private static (int Port, Task<byte[]?> Reply) FakeServer(int authCode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buffer = new byte[8192];
                await stream.ReadAsync(buffer);   // the startup message
                await stream.WriteAsync(new byte[] { (byte)'R', 0, 0, 0, 8, 0, 0, 0, (byte)authCode });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var n = await stream.ReadAsync(buffer, cts.Token);
                return n <= 0 ? null : buffer[..n];
            }
            catch (IOException)
            {
                return null;
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, reply);
    }

    private static string FakeString(int port) =>
        $"Host=127.0.0.1;Port={port};Username=u;Password=pw;Database=d;SSL Mode=Disable;Pooling=false;Timeout=5";

    [Theory]
    [InlineData(7)]   // AuthenticationGSS
    [InlineData(9)]   // AuthenticationSSPI
    public async Task A_gss_or_sspi_request_gets_no_windows_credentials(int authCode)
    {
        var (port, reply) = FakeServer(authCode);
        var result = await new PgConnectionService().TestAsync(FakeString(port));
        Assert.False(result.Success);
        Assert.Contains("Windows", result.Message);
        Assert.NotNull(result.Hint);
        var sent = await reply;
        Assert.True(sent is null || sent[0] != (byte)'p', "an authentication token went out");   // no NTLM or Kerberos token
    }

    [Fact]
    public async Task The_reader_refuses_gss_too()
    {
        var (port, reply) = FakeServer(7);
        var source = new SchemaSource { Id = "s", Label = "fake", Engine = DatabaseEngine.PostgreSql, ConnectionString = FakeString(port) };
        var ex = await Assert.ThrowsAsync<SchemaReadException>(() => new PgSchemaReader().ReadAsync(source, new CompareOptions()));
        Assert.Contains("Windows", ex.Message);
        var sent = await reply;
        Assert.True(sent is null || sent[0] != (byte)'p', "an authentication token went out");
    }

    [Fact]
    public async Task A_password_request_still_gets_the_typed_password()
    {
        // the control: the fake server sees an answer when it asks for a cleartext password
        var (port, reply) = FakeServer(3);
        await new PgConnectionService().TestAsync(FakeString(port));
        var sent = await reply;
        Assert.NotNull(sent);
        Assert.Equal((byte)'p', sent[0]);
    }
}

/// <summary>A stored PostgreSQL password never goes out over weaker TLS than it was saved with.</summary>
public sealed class PgTlsDowngradeTests : IDisposable
{
    private readonly TempStore _temp = new();
    public void Dispose() => _temp.Dispose();

    private ConnectionSettings Saved(SslMode? ssl, bool trust)
    {
        var s = _temp.Store.SaveConnection(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Server = "db.internal", Port = 5432, Database = "app",
            Auth = AuthMode.Password, UserName = "deploy", Password = "s3cret", Ssl = ssl, TrustServerCertificate = trust
        });
        return s;
    }

    private static ConnectionSettings Incoming(ConnectionSettings saved, SslMode? ssl, bool trust) => new()
    {
        Id = saved.Id, Engine = saved.Engine, Server = saved.Server, Port = saved.Port, Database = saved.Database,
        Auth = saved.Auth, UserName = saved.UserName, Password = SecretRelease.Masked, Ssl = ssl, TrustServerCertificate = trust
    };

    [Theory]
    [InlineData(SslMode.Disable, true)]
    [InlineData(SslMode.Prefer, true)]
    [InlineData(SslMode.Require, true)]
    [InlineData(null, true)]              // Verify full with the trust tick is Require
    [InlineData(SslMode.VerifyCa, false)]
    public void A_weaker_ssl_mode_means_typing_the_password_again(SslMode? ssl, bool trust)
    {
        if (!SecretProtector.IsSupported) return;
        var saved = Saved(null, false);
        var incoming = Incoming(saved, ssl, trust);
        Assert.False(SecretRelease.TryRehydrate(incoming, _temp.Store, out var error));
        Assert.Contains("SSL mode", error);
        Assert.Equal(SecretRelease.Masked, incoming.Password);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(SslMode.VerifyFull, false)]
    public void The_same_or_stronger_tls_gets_the_password(SslMode? ssl, bool trust)
    {
        if (!SecretProtector.IsSupported) return;
        foreach (var saved in new[] { Saved(null, false), Saved(SslMode.Disable, true), Saved(SslMode.Require, true) })
        {
            var incoming = Incoming(saved, ssl, trust);
            Assert.True(SecretRelease.TryRehydrate(incoming, _temp.Store, out var error), error);
            Assert.Equal("s3cret", incoming.Password);
        }
    }

    [Fact]
    public void Saving_with_weaker_tls_drops_the_stored_password()
    {
        if (!SecretProtector.IsSupported) return;
        var saved = Saved(null, false);
        _temp.Store.SaveConnection(Incoming(saved, SslMode.Disable, true));
        Assert.True(string.IsNullOrEmpty(_temp.Store.GetConnection(saved.Id)!.Password));

        // SQL Server is unchanged: its Trust server certificate switch is not an SSL mode
        var sql = _temp.Store.SaveConnection(new ConnectionSettings
            { Server = "s", Auth = AuthMode.SqlLogin, UserName = "sa", Password = "pw" });
        var again = new ConnectionSettings
        {
            Id = sql.Id, Server = "s", Auth = AuthMode.SqlLogin, UserName = "sa", Password = SecretRelease.Masked, TrustServerCertificate = true
        };
        Assert.True(SecretRelease.TryRehydrate(again, _temp.Store, out _));
        Assert.Equal("pw", again.Password);
    }
}

/// <summary>The owner API refuses an empty PostgreSQL password and SSL Disable without the tick, before connecting.</summary>
public sealed class PgRouteRefusalTests : IAsyncLifetime
{
    private OwnerApp _o = null!;
    public async Task InitializeAsync()
    {
        _o = await OwnerApp.StartAsync();
        await _o.SetPreviewAsync(true);
    }
    public async Task DisposeAsync() => await _o.DisposeAsync();

    private static async Task<string> Message(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString() ?? "";

    [Theory]
    [InlineData("/api/connections")]
    [InlineData("/api/connections/test")]
    [InlineData("/api/connections/databases")]
    public async Task An_empty_password_is_refused(string path)
    {
        var r = await _o.PostJson(path, """
            { "engine": "postgreSql", "server": "127.0.0.1", "port": 1, "database": "app", "auth": "password",
              "userName": "u", "password": "", "ssl": "disable", "trustServerCertificate": true }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Type the password", await Message(r));
    }

    [Fact]
    public async Task An_empty_password_is_refused_for_a_compare()
    {
        var r = await _o.PostJson("/api/compare", """
            { "source": { "inline": { "engine": "postgreSql", "server": "127.0.0.1", "port": 1, "database": "a",
                "auth": "password", "userName": "u", "password": "", "ssl": "disable", "trustServerCertificate": true } },
              "targets": [ { "inline": { "engine": "postgreSql", "server": "127.0.0.1", "port": 1, "database": "b",
                "auth": "password", "userName": "u", "password": "pw", "ssl": "disable", "trustServerCertificate": true } } ] }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Type the password", await Message(r));
    }

    [Fact]
    public async Task Ssl_disable_without_the_tick_is_refused()
    {
        var r = await _o.PostJson("/api/connections/test", """
            { "engine": "postgreSql", "server": "127.0.0.1", "port": 1, "database": "app", "auth": "password",
              "userName": "u", "password": "pw", "ssl": "disable" }
            """);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Disable", await Message(r));
    }
}

/// <summary>Options written before the PostgreSQL types existed, and SQL Server's schema defaults on PostgreSQL.</summary>
public class PgOptionsTests
{
    private static readonly DbObjectType[] PgTypes =
        [DbObjectType.MaterializedView, DbObjectType.Domain, DbObjectType.EnumType, DbObjectType.CompositeType, DbObjectType.Extension];

    [Fact]
    public void Options_saved_before_the_new_types_include_them()
    {
        // what a 1.3 profile holds: every type it knew, and no TypeSet
        var old = $$"""{"IgnoreComments":true,"IncludedTypes":[{{string.Join(",", Enumerable.Range(0, 12))}}]}""";
        var options = JsonSerializer.Deserialize<CompareOptions>(old)!;
        Assert.All(PgTypes, t => Assert.Contains(t, options.IncludedTypes));
        Assert.False(options.IsExcluded(DbObjectType.MaterializedView, "app", "mv"));
        Assert.Equal(CompareOptions.CurrentTypeSet, options.TypeSet);

        // a choice made knowing the types is kept
        var current = new CompareOptions();
        current.IncludedTypes.Remove(DbObjectType.Domain);
        var back = JsonSerializer.Deserialize<CompareOptions>(JsonSerializer.Serialize(current))!;
        Assert.DoesNotContain(DbObjectType.Domain, back.IncludedTypes);
        // the API's camelCase too
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain(DbObjectType.Domain, JsonSerializer.Deserialize<CompareOptions>(JsonSerializer.Serialize(current, web), web)!.IncludedTypes);
    }

    [Fact]
    public void An_old_profile_in_the_store_reads_back_with_the_new_types()
    {
        using var temp = new TempStore();
        var p = temp.Store.SaveProfile(new CompareProfile { Name = "structure", Options = new CompareOptions() });
        using (var conn = new SqliteConnection($"Data Source={temp.DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE profiles SET options_json = $json WHERE id = $id";
            cmd.Parameters.AddWithValue("$json", """{"IncludedTypes":[1,2,9,8,10]}""");
            cmd.Parameters.AddWithValue("$id", p.Id);
            cmd.ExecuteNonQuery();
        }
        var options = temp.Store.GetProfiles().Single().Options;
        Assert.All(PgTypes, t => Assert.Contains(t, options.IncludedTypes));
        Assert.DoesNotContain(DbObjectType.StoredProcedure, options.IncludedTypes);
    }

    [Fact]
    public void Postgres_starts_from_its_own_excluded_schemas_and_matches_exactly()
    {
        var options = new CompareOptions();
        options.ExcludedSchemas.Add("audit");
        options.ExcludedNamePatterns.Add("App.*");
        var pg = options.ForEngine(new PgDialect().DefaultExcludedSchemas, exactNames: true);

        Assert.False(pg.IsExcluded(DbObjectType.Table, "sys", "t"));
        Assert.False(pg.IsExcluded(DbObjectType.Table, "guest", "t"));
        Assert.True(pg.IsExcluded(DbObjectType.Table, "audit", "t"));
        Assert.False(pg.IsExcluded(DbObjectType.Table, "Audit", "t"));
        Assert.True(pg.IsExcluded(DbObjectType.Table, "pg_catalog", "t"));
        Assert.True(pg.IsExcluded(DbObjectType.Table, "App", "t"));
        Assert.False(pg.IsExcluded(DbObjectType.Table, "app", "t"));

        // the options themselves, and SQL Server's reading of them, are unchanged
        Assert.True(options.IsExcluded(DbObjectType.Table, "sys", "t"));
        Assert.True(options.IsExcluded(DbObjectType.Table, "app", "t"));
        Assert.False(options.ExactNames);
    }
}

/// <summary>Real differences the reader used to call Same, each against a real PostgreSQL 16.</summary>
[Collection(PgCollection.Name)]
public sealed class PgHiddenDifferenceTests(PgServer pg)
{
    private async Task<CompareRun> PairAsync(string source, string target, CompareOptions? options = null)
    {
        var a = await pg.CreateDatabaseAsync("a");
        var b = await pg.CreateDatabaseAsync("b");
        try
        {
            await pg.ExecAsync(a, "CREATE SCHEMA app;\n" + source);
            await pg.ExecAsync(b, "CREATE SCHEMA app;\n" + target);
            var run = await new CompareRunner(_ => new PgSchemaReader())
                .RunAsync(pg.Source("source", a), [pg.Source("t0", b)], options ?? new CompareOptions());
            Assert.False(run.Targets[0].Failed, run.Targets[0].Error);
            return run;
        }
        finally
        {
            await pg.DropAsync(a);
            await pg.DropAsync(b);
        }
    }

    private static ObjectStatus Status(CompareRun run, DbObjectType type, string name) =>
        run.Result.Rows.Single(x => x.Type == type && x.Schema == "app" && x.Name == name).Cells[0].Status;

    public static TheoryData<string, string, string, string> Pairs => new()
    {
        { "row level security", "t",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY;",
            "CREATE TABLE app.t (id int);" },
        { "force row level security", "t",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY; ALTER TABLE app.t FORCE ROW LEVEL SECURITY;",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY;" },
        { "a policy's condition", "t",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY; CREATE POLICY p ON app.t USING (id > 0);",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY; CREATE POLICY p ON app.t USING (id > 1);" },
        { "a policy at all", "t",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY; CREATE POLICY p ON app.t FOR SELECT USING (true);",
            "CREATE TABLE app.t (id int); ALTER TABLE app.t ENABLE ROW LEVEL SECURITY;" },
        { "unlogged", "t",
            "CREATE UNLOGGED TABLE app.t (id int);",
            "CREATE TABLE app.t (id int);" },
        { "plain inheritance", "t",
            "CREATE TABLE app.base (id int); CREATE TABLE app.t (x int) INHERITS (app.base);",
            "CREATE TABLE app.base (id int); CREATE TABLE app.t (id int, x int);" },
        { "range vs list partitioning", "p",
            "CREATE TABLE app.p (id int, k int) PARTITION BY RANGE (k);",
            "CREATE TABLE app.p (id int, k int) PARTITION BY LIST (k);" },
        { "partition bounds", "c",
            "CREATE TABLE app.p (id int, k int) PARTITION BY RANGE (k); CREATE TABLE app.c PARTITION OF app.p FOR VALUES FROM (0) TO (10);",
            "CREATE TABLE app.p (id int, k int) PARTITION BY RANGE (k); CREATE TABLE app.c PARTITION OF app.p FOR VALUES FROM (0) TO (20);" },
        { "range vs list bounds", "c",
            "CREATE TABLE app.p (id int, k int) PARTITION BY RANGE (k); CREATE TABLE app.c PARTITION OF app.p FOR VALUES FROM (1) TO (2);",
            "CREATE TABLE app.p (id int, k int) PARTITION BY LIST (k); CREATE TABLE app.c PARTITION OF app.p FOR VALUES IN (1);" },
        { "a partition vs a plain table without its key", "c",
            "CREATE TABLE app.p (id int PRIMARY KEY) PARTITION BY RANGE (id); CREATE TABLE app.c PARTITION OF app.p FOR VALUES FROM (0) TO (10);",
            "CREATE TABLE app.p (id int PRIMARY KEY) PARTITION BY RANGE (id); CREATE TABLE app.c (id int NOT NULL);" },
        { "storage options", "t",
            "CREATE TABLE app.t (id int) WITH (autovacuum_enabled = false);",
            "CREATE TABLE app.t (id int);" },
    };

    [PostgresTheory]
    [MemberData(nameof(Pairs))]
    public async Task A_table_difference_is_never_same(string what, string name, string source, string target)
    {
        var run = await PairAsync(source, target);
        Assert.True(Status(run, DbObjectType.Table, name) == ObjectStatus.Different, what);
        // and the structure grid says where
        var left = run.Source.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == name);
        var right = run.Targets[0].Objects.Single(o => o.Type == DbObjectType.Table && o.Name == name);
        var sections = StructureDiffService.Build(left, right, new CompareOptions());
        Assert.True(sections.Any(s => s.ChangedCount > 0), what);
    }

    [PostgresTheory]
    [InlineData("WITH CASCADED CHECK OPTION", "")]
    [InlineData("WITH LOCAL CHECK OPTION", "WITH CASCADED CHECK OPTION")]
    public async Task A_view_check_option_is_never_same(string a, string b)
    {
        const string table = "CREATE TABLE app.b (id int);";
        var run = await PairAsync($"{table} CREATE VIEW app.v AS SELECT id FROM app.b {a};", $"{table} CREATE VIEW app.v AS SELECT id FROM app.b {b};");
        Assert.Equal(ObjectStatus.Different, Status(run, DbObjectType.View, "v"));
        if (a.Length > 0)
            Assert.Contains(a.Replace("WITH ", "").Replace(" CHECK OPTION", ""), run.Source.Objects.Single(o => o.Name == "v").Definition);
    }

    [PostgresFact]
    public async Task A_security_barrier_view_is_never_same()
    {
        const string table = "CREATE TABLE app.b (id int);";
        var run = await PairAsync($"{table} CREATE VIEW app.v WITH (security_barrier) AS SELECT id FROM app.b;",
            $"{table} CREATE VIEW app.v AS SELECT id FROM app.b;");
        Assert.Equal(ObjectStatus.Different, Status(run, DbObjectType.View, "v"));
    }

    [PostgresFact]
    public async Task Identical_partitions_and_keys_are_same_with_no_duplicate_keys()
    {
        const string schema = """
            CREATE TABLE app.p (id int PRIMARY KEY, k int) PARTITION BY RANGE (id);
            CREATE TABLE app.p1 PARTITION OF app.p FOR VALUES FROM (0) TO (10);
            CREATE TABLE app.r (pid int REFERENCES app.p (id));
            CREATE TABLE app.q (pid int REFERENCES app.p (id)) PARTITION BY RANGE (pid);
            CREATE TABLE app.q1 PARTITION OF app.q FOR VALUES FROM (0) TO (10);
            CREATE UNLOGGED TABLE app.u (id int);
            ALTER TABLE app.u ENABLE ROW LEVEL SECURITY;
            CREATE POLICY own ON app.u FOR SELECT TO PUBLIC USING (id > 0);
            CREATE VIEW app.v WITH (security_barrier) AS SELECT id FROM app.u WITH LOCAL CHECK OPTION;
            """;
        var run = await PairAsync(schema, schema);
        Assert.All(run.Result.Rows, row => Assert.True(row.Cells[0].Status == ObjectStatus.Same, $"{row.Name}: {row.Cells[0].Status}"));

        DbObject Of(string name) => run.Source.Objects.Single(o => o.Type == DbObjectType.Table && o.Name == name);
        // a partition shows the key it has from its parent; a foreign key to a partitioned table is listed once
        Assert.Single(Of("p1").Indexes, i => i.IsPrimaryKey);
        Assert.Single(Of("r").ForeignKeys);
        Assert.Single(Of("q").ForeignKeys);
        Assert.Single(Of("q1").ForeignKeys);
        Assert.Equal("app.p", Of("p1").Properties["partitionOf"]);
        Assert.Equal("FOR VALUES FROM (0) TO (10)", Of("p1").Properties["partitionBound"]);
        Assert.Equal("RANGE (id)", Of("p").Properties["partitionBy"]);

        var u = Of("u");
        var script = new PgDialect().CanonicalScript(u, new CompareOptions());
        Assert.StartsWith("CREATE UNLOGGED TABLE \"app\".\"u\"", script);
        Assert.Contains("ALTER TABLE \"app\".\"u\" ENABLE ROW LEVEL SECURITY;", script);
        Assert.Contains("CREATE POLICY \"own\" ON \"app\".\"u\" AS PERMISSIVE FOR SELECT TO PUBLIC USING ((id > 0));", script);
        var v = run.Source.Objects.Single(o => o.Name == "v").Definition!;
        Assert.StartsWith("CREATE OR REPLACE VIEW \"app\".\"v\" WITH (security_barrier=true) AS", v);
        Assert.EndsWith("WITH LOCAL CHECK OPTION;", v);
    }

    [PostgresFact]
    public async Task Sql_server_schema_names_are_compared_on_postgres_and_exclusions_match_exactly()
    {
        const string schema = """
            CREATE SCHEMA sys; CREATE TABLE sys.t (id int);
            CREATE SCHEMA guest; CREATE TABLE guest.t (id int);
            CREATE SCHEMA "App"; CREATE TABLE "App".t (id int);
            CREATE TABLE app.t (id int);
            """;
        var options = new CompareOptions();
        options.ExcludedSchemas.Add("App");
        var run = await PairAsync(schema, schema, options);
        var tables = run.Source.Objects.Where(o => o.Type == DbObjectType.Table).Select(o => o.FullName).Order(StringComparer.Ordinal);
        Assert.Equal(["app.t", "guest.t", "sys.t"], tables);
    }
}
