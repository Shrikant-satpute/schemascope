using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Engines;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.SqlServer;

namespace SchemaScope.Tests;

/// <summary>
/// The secret-release rule with engines and ports in it (MULTI-DATABASE-PLAN §6.1): a stored password goes back only
/// to the engine, host set, port, login and auth mode it was saved for - before any engine selector exists.
/// </summary>
public sealed class EngineRehydrateTests : IDisposable
{
    private readonly TempStore _temp = new();
    private const string RealPassword = "correct horse battery staple";

    public void Dispose() => _temp.Dispose();

    private ConnectionSettings Save(ConnectionSettings s) => _temp.Store.SaveConnection(s);

    private static ConnectionSettings MaskedCopy(ConnectionSettings saved) => new()
    {
        Id = saved.Id,
        Label = saved.Label,
        Engine = saved.Engine,
        Server = saved.Server,
        Port = saved.Port,
        Database = saved.Database,
        Auth = saved.Auth,
        UserName = saved.UserName,
        Password = "********",
        UseRawConnectionString = saved.UseRawConnectionString,
        RawConnectionString = saved.UseRawConnectionString ? saved.WithoutSecrets().RawConnectionString : null,
    };

    private ConnectionSettings SqlLogin() => Save(new ConnectionSettings
    {
        Server = "prod-sql.internal", Database = "Sales", Auth = AuthMode.SqlLogin, UserName = "reporting", Password = RealPassword
    });

    private ConnectionSettings PgRaw(string raw) => Save(new ConnectionSettings
    {
        Engine = DatabaseEngine.PostgreSql, UseRawConnectionString = true, RawConnectionString = raw
    });

    [Fact]
    public void Swapping_the_engine_never_releases_the_password()
    {
        // the attack the plan names: a SQL Server password sent to a PostgreSQL listener on the same host
        var request = MaskedCopy(SqlLogin());
        request.Engine = DatabaseEngine.PostgreSql;

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out var error));
        Assert.NotNull(error);
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void Changing_the_port_never_releases_the_password()
    {
        var request = MaskedCopy(SqlLogin());
        request.Port = 1434;

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void The_default_port_spelled_out_is_the_same_destination()
    {
        var saved = Save(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Server = "pg.internal", Database = "app", Auth = AuthMode.Password,
            UserName = "deploy", Password = RealPassword
        });
        var request = MaskedCopy(saved);
        request.Port = 5432;
        Assert.True(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.Equal(RealPassword, request.Password);

        var other = MaskedCopy(saved);
        other.Port = 5433;
        Assert.False(SchemaScopeApi.TryRehydrate(other, _temp.Store, out _));
        Assert.NotEqual(RealPassword, other.Password);
    }

    [Fact]
    public void Changing_the_host_list_of_a_raw_string_never_releases_it()
    {
        var saved = PgRaw($"Host=pg-a,pg-b;Port=5432;Database=app;Username=deploy;Password={RealPassword}");

        var request = MaskedCopy(saved);
        request.RawConnectionString = "Host=pg-a,attacker.example;Port=5432;Database=app;Username=deploy;Password=********";
        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.DoesNotContain(RealPassword, request.RawConnectionString);

        // the same hosts in another order, on another database: the same destination
        var same = MaskedCopy(saved);
        same.RawConnectionString = "Host=pg-b,pg-a;Port=5432;Database=other;Username=deploy;Password=********";
        Assert.True(SchemaScopeApi.TryRehydrate(same, _temp.Store, out _));
        Assert.Contains(RealPassword, same.RawConnectionString);
    }

    [Fact]
    public void A_raw_npgsql_string_with_another_host_never_receives_it()
    {
        var saved = PgRaw($"Host=pg.internal;Database=app;Username=deploy;Password={RealPassword}");
        var request = MaskedCopy(saved);
        request.RawConnectionString = "Host=attacker.example;Database=app;Username=deploy;Password=********";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.DoesNotContain(RealPassword, request.RawConnectionString);
    }

    [Fact]
    public void A_raw_port_change_never_receives_it()
    {
        var saved = PgRaw($"Host=pg.internal;Database=app;Username=deploy;Password={RealPassword}");
        var request = MaskedCopy(saved);
        request.RawConnectionString = "Host=pg.internal:6543;Database=app;Username=deploy;Password=********";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.DoesNotContain(RealPassword, request.RawConnectionString);
    }

    [Fact]
    public void An_npgsql_string_on_a_sql_server_connection_is_not_the_same_destination()
    {
        var saved = Save(new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = $"Data Source=prod-sql.internal;Initial Catalog=Sales;User ID=reporting;Password={RealPassword}"
        });
        var request = MaskedCopy(saved);
        request.RawConnectionString = "Host=prod-sql.internal;Port=5432;Username=reporting;Password=********";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.DoesNotContain(RealPassword, request.RawConnectionString);
    }

    [Theory]
    [InlineData("Data Source='prod-sql.internal;Password=********")]   // unterminated quote
    [InlineData("Initial Catalog=Sales;Password=********")]           // no server at all
    [InlineData("Data Source=prod-sql.internal,notaport;Password=********")]
    [InlineData("mssql://reporting:********@prod-sql.internal/Sales")]
    public void An_unreadable_raw_string_releases_nothing(string raw)
    {
        var saved = Save(new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = $"Data Source=prod-sql.internal;Initial Catalog=Sales;User ID=reporting;Password={RealPassword}"
        });
        var request = MaskedCopy(saved);
        request.RawConnectionString = raw;

        Assert.False(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.DoesNotContain(RealPassword, request.RawConnectionString ?? "");
    }

    [Fact]
    public void The_unchanged_masked_raw_string_still_comes_back()
    {
        var saved = Save(new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = $"Data Source=prod-sql.internal;Initial Catalog=Sales;User ID=reporting;Password={RealPassword}"
        });
        var request = MaskedCopy(saved);
        Assert.Contains("********", request.RawConnectionString);

        Assert.True(SchemaScopeApi.TryRehydrate(request, _temp.Store, out _));
        Assert.Contains(RealPassword, request.RawConnectionString);
    }

    [Fact]
    public void A_password_does_not_follow_a_row_from_connections_to_engine_connections()
    {
        var saved = SqlLogin();

        // the same id saved again as PostgreSQL with the mask: the row moves tables, the secret stays behind
        var moved = MaskedCopy(saved);
        moved.Engine = DatabaseEngine.PostgreSql;
        moved.Auth = AuthMode.Password;
        _temp.Store.SaveConnection(moved);

        var now = _temp.Store.GetConnection(saved.Id)!;
        Assert.Equal(DatabaseEngine.PostgreSql, now.Engine);
        Assert.True(string.IsNullOrEmpty(now.Password));
        Assert.Single(_temp.Store.GetConnections(), c => c.Id == saved.Id);
        Assert.Empty(_temp.Store.GetConnections(DatabaseEngine.SqlServer));

        // and back again: still nothing to release
        var back = MaskedCopy(now);
        back.Engine = DatabaseEngine.SqlServer;
        back.Auth = AuthMode.SqlLogin;
        _temp.Store.SaveConnection(back);
        Assert.True(string.IsNullOrEmpty(_temp.Store.GetConnection(saved.Id)!.Password));
    }

    [Fact]
    public void A_raw_string_does_not_follow_an_engine_change_either()
    {
        var saved = Save(new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = $"Data Source=prod-sql.internal;User ID=reporting;Password={RealPassword}"
        });
        var moved = MaskedCopy(saved);
        moved.Engine = DatabaseEngine.PostgreSql;
        _temp.Store.SaveConnection(moved);

        Assert.True(string.IsNullOrEmpty(_temp.Store.GetConnection(saved.Id)!.RawConnectionString));
    }

    [Fact]
    public void Saving_on_another_port_drops_the_stored_password()
    {
        var saved = Save(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Server = "pg.internal", Auth = AuthMode.Password, UserName = "deploy", Password = RealPassword
        });
        var request = MaskedCopy(saved);
        request.Port = 6543;
        _temp.Store.SaveConnection(request);

        Assert.True(string.IsNullOrEmpty(_temp.Store.GetConnection(saved.Id)!.Password));
    }
}

/// <summary>What a connection from the UI may say before anything is saved, tested or read (R0a: SQL Server only).</summary>
public sealed class ConnectionRuleTests : IDisposable
{
    private readonly TempStore _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_ordinary_sql_server_connection_passes() =>
        Assert.Null(ConnectionRules.Problem(new ConnectionSettings { Server = "prod", Database = "Sales" }));

    [Theory]
    [InlineData(DatabaseEngine.PostgreSql)]
    [InlineData(DatabaseEngine.MySql)]
    [InlineData((DatabaseEngine)99)]
    public void Other_engines_are_refused_until_they_exist(DatabaseEngine engine) =>
        Assert.NotNull(ConnectionRules.Problem(new ConnectionSettings { Engine = engine, Server = "prod" }));

    [Theory]
    [InlineData("postgres://deploy:hunter2@pg.internal/app")]
    [InlineData("  mysql://root:pw@db:3306/app")]
    [InlineData("sqlserver://sa:pw@prod")]
    public void Uri_form_strings_are_refused(string raw)
    {
        var problem = ConnectionRules.Problem(new ConnectionSettings { UseRawConnectionString = true, RawConnectionString = raw });
        Assert.NotNull(problem);
        Assert.Contains("URI", problem);
        Assert.DoesNotContain("hunter2", problem);
    }

    [Fact]
    public void Engine_fields_that_sql_server_does_not_use_are_refused()
    {
        Assert.NotNull(ConnectionRules.Problem(new ConnectionSettings { Server = "prod", Port = 1433 }));
        Assert.NotNull(ConnectionRules.Problem(new ConnectionSettings { Server = "prod", Ssl = SslMode.Require }));
        Assert.NotNull(ConnectionRules.Problem(new ConnectionSettings { Server = "prod", Auth = AuthMode.Password }));
        Assert.Contains("secret", ConnectionRules.Problem(new ConnectionSettings
        {
            Server = "prod", EngineOptions = new() { ["SSL Password"] = "x" }
        }));
    }

    private CompareService Compares() =>
        new(new CompareSessionStore(), _temp.Store, new SqlServerConnectionService(), NullLogger<CompareService>.Instance);

    [Fact]
    public void An_inline_connection_goes_through_the_same_secret_rule()
    {
        var saved = _temp.Store.SaveConnection(new ConnectionSettings
        {
            Server = "prod-sql.internal", Database = "Sales", Auth = AuthMode.SqlLogin, UserName = "reporting", Password = "hunter2"
        });
        var request = new CompareRequest
        {
            Source = new SourceRef
            {
                Inline = new ConnectionSettings
                {
                    Id = saved.Id, Server = "attacker.example", Database = "Sales", Auth = AuthMode.SqlLogin,
                    UserName = "reporting", Password = "********"
                }
            },
            Targets = [new SourceRef { Inline = new ConnectionSettings { Server = "127.0.0.1,1", Database = "x" } }],
        };

        var ex = Assert.Throws<SchemaReadException>(() => Compares().Start(request));
        Assert.Contains("password", ex.Message);
        Assert.Equal("********", request.Source.Inline!.Password);
    }

    [Fact]
    public void A_saved_connection_to_another_engine_is_never_read_with_sql_client()
    {
        var pg = _temp.Store.SaveConnection(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, Server = "pg.internal", Database = "app", Auth = AuthMode.Password, UserName = "u", Password = "pw"
        });
        var request = new CompareRequest
        {
            Source = new SourceRef { ConnectionId = pg.Id },
            Targets = [new SourceRef { Inline = new ConnectionSettings { Server = "127.0.0.1,1", Database = "x" } }],
        };

        Assert.Throws<SchemaReadException>(() => Compares().Start(request));
    }
}

/// <summary>Masking and scrubbing cover every driver's secret keys (§6.2).</summary>
public class BroadSecretTests
{
    [Fact]
    public void The_scrubber_masks_every_kind_of_secret_key()
    {
        const string cs = "Host=pg;Username=u;Password=p4ss-one;SSL Password=ssl-two;CertificatePassword=cert-three;" +
                          "Passphrase=phrase-four;Client Secret=secret-five";
        var secrets = SecretScrubber.Secrets([cs]);
        var text = SecretScrubber.Scrub("p4ss-one ssl-two cert-three phrase-four secret-five", secrets);

        Assert.Equal("******** ******** ******** ******** ********", text);
    }

    [Fact]
    public void A_uri_form_password_is_scrubbed_too()
    {
        var secrets = SecretScrubber.Secrets(["postgres://deploy:hunter%402@pg.internal/app"]);
        Assert.DoesNotContain("hunter@2", SecretScrubber.Scrub("auth failed for hunter@2", secrets));
        Assert.DoesNotContain("hunter%402", SecretScrubber.Scrub("auth failed for hunter%402", secrets));
    }

    [Fact]
    public void Masking_a_raw_string_covers_the_other_secret_keys()
    {
        var masked = new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = "Data Source=prod;User ID=u;Password=one;SSL Password=two;Passphrase=three"
        }.WithoutSecrets().RawConnectionString!;

        Assert.DoesNotContain("one", masked);
        Assert.DoesNotContain("two", masked);
        Assert.DoesNotContain("three", masked);
        Assert.Contains("Data Source=prod", masked);
    }
}

/// <summary>Protected is matched on the host for every engine and port, and fails closed (§6.4).</summary>
public sealed class ProtectedHostTests : IDisposable
{
    private readonly TempStore _temp = new();

    public void Dispose() => _temp.Dispose();

    private void Protect(ConnectionSettings c)
    {
        c.ProtectedServer = true;
        _temp.Store.SaveConnection(c);
    }

    [Fact]
    public void A_protected_postgres_connection_on_localhost_port_5432_protects_sql_server_on_localhost()
    {
        Protect(new ConnectionSettings { Engine = DatabaseEngine.PostgreSql, Server = "localhost", Port = 5432 });
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "localhost"));
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "tcp:127.0.0.1,1"));
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, $"{Environment.MachineName}\\SQL2025"));
        Assert.False(SchemaScopeApi.OnProtectedServer(_temp.Store, "prod-sql"));
    }

    [Fact]
    public void A_multi_host_string_is_protected_if_any_host_is()
    {
        Protect(new ConnectionSettings
        {
            Engine = DatabaseEngine.PostgreSql, UseRawConnectionString = true, RawConnectionString = "Host=prod,localhost;Database=app"
        });
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "localhost"));
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "prod.corp.local"));
        Assert.False(SchemaScopeApi.OnProtectedServer(_temp.Store, "staging"));
    }

    [Fact]
    public void A_sql_server_failover_partner_is_protected_too()
    {
        Protect(new ConnectionSettings
        {
            UseRawConnectionString = true,
            RawConnectionString = "Data Source=prod-a;Failover Partner=prod-b;Initial Catalog=Sales;Integrated Security=true"
        });
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "prod-b"));
        Assert.False(SchemaScopeApi.OnProtectedServer(_temp.Store, "staging"));
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer, "Data Source='unterminated")]
    [InlineData(DatabaseEngine.SqlServer, "Initial Catalog=Sales;Integrated Security=true")]
    [InlineData(DatabaseEngine.SqlServer, "Host=prod;Port=5432")]
    [InlineData(DatabaseEngine.PostgreSql, "Database=app")]
    public void A_protected_connection_whose_server_cannot_be_read_protects_everything(DatabaseEngine engine, string raw)
    {
        // today's fail-open fallback to the (hidden, possibly stale) Server box is gone
        Protect(new ConnectionSettings { Engine = engine, Server = "stale-box", UseRawConnectionString = true, RawConnectionString = raw });
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "anything-at-all"));
    }

    [Fact]
    public void An_unprotected_unreadable_connection_protects_nothing()
    {
        _temp.Store.SaveConnection(new ConnectionSettings { UseRawConnectionString = true, RawConnectionString = "Data Source='unterminated" });
        Assert.False(SchemaScopeApi.OnProtectedServer(_temp.Store, "anything-at-all"));
    }

    [Fact]
    public void Sql_server_instances_keep_todays_matching()
    {
        Protect(new ConnectionSettings { Server = "PRODSQL\\A" });
        Assert.True(SchemaScopeApi.OnProtectedServer(_temp.Store, "prodsql.corp.local\\a,1433"));
        Assert.False(SchemaScopeApi.OnProtectedServer(_temp.Store, "PRODSQL\\B"));
    }
}

/// <summary>The driver-free endpoint reader behind the secret and Protected rules.</summary>
public class EndpointParseTests
{
    [Theory]
    [InlineData("Data Source=tcp:prod.corp.local\\SQL2025,1444;Initial Catalog=Sales", "prod.corp.local", "SQL2025", 1444, "Sales")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=x;Integrated Security=true", "(localdb)", "MSSQLLocalDB", null, "x")]
    [InlineData("Address=np:\\\\prod\\pipe\\sql\\query", "prod", null, null, "")]
    public void Sql_server_strings(string cs, string host, string? instance, int? port, string database)
    {
        var e = ConnectionEndpoints.Parse(DatabaseEngine.SqlServer, cs)!;
        Assert.Equal([host], e.Hosts);
        Assert.Equal(instance, e.Instance);
        Assert.Equal(port, e.Port);
        Assert.Equal(database, e.Database);
    }

    [Fact]
    public void Postgres_multi_host_strings()
    {
        var e = ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Host=a,b:5433,[::1]:5433;Database=app")!;
        Assert.Equal(["a", "b", "::1"], e.Hosts);
        Assert.Equal(5433, e.Port);
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Host=a:5432,b:5433"));   // two ports: refuse
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.PostgreSql, "Data Source=a"));          // a SqlClient string
    }

    [Theory]
    [InlineData("")]
    [InlineData("postgres://u:p@h/db")]
    [InlineData("Host=pg;Username=u")]
    [InlineData("Data Source=")]
    public void Unreadable_sql_server_strings_are_null(string cs) =>
        Assert.Null(ConnectionEndpoints.Parse(DatabaseEngine.SqlServer, cs));
}
