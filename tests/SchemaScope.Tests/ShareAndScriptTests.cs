using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.Scripting;
using SchemaScope.SqlServer;

namespace SchemaScope.Tests;

/// <summary>A throwaway settings store and share folder, deleted afterwards.</summary>
public sealed class TempStore : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"schemascope-test-{Guid.NewGuid():n}");
    public SchemaScopeStore Store { get; }
    public string DbPath => Path.Combine(Folder, "schemascope.db");

    public TempStore()
    {
        Directory.CreateDirectory(Folder);
        Store = new SchemaScopeStore(DbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class Shares
{
    public const string SecretPassword = "hunter2-Sup3rSecret";
    public const string SecretConnection = $"Server=prod-sql.internal;Database=Sales;User Id=reporting;Password={SecretPassword}";

    public static ShareStore NewStore(TempStore temp) => new(temp.Store, NullLogger<ShareStore>.Instance);

    public static CompareService NewCompares(TempStore temp) => new(
        new CompareSessionStore(), temp.Store, new SqlServerConnectionService(), NullLogger<CompareService>.Instance);

    /// <summary>A finished run whose driver messages echo the connection string back, as SqlClient sometimes does.</summary>
    public static CompareSession FinishedRun()
    {
        var session = new CompareSession
        {
            Status = "done",
            SourceConnection = new SchemaSource { Label = "Prod", ConnectionString = SecretConnection, Server = "prod-sql.internal", Database = "Sales" },
            Result = new CompareResult
            {
                Source = new SnapshotInfo { Label = "Prod", Server = "prod-sql.internal", Database = "Sales" },
                Targets = [new TargetSummary { Label = "Test", Error = $"Could not open '{SecretConnection}'" }],
                Warnings = [$"Login failed for password {SecretPassword}"]
            }
        };
        session.TargetConnections = [new SchemaSource { Label = "Test", ConnectionString = "Server=test;Integrated Security=true" }];
        return session;
    }

    public static ShareRecord Publish(TempStore temp, ShareStore store, TimeSpan? lifetime = null) =>
        store.Publish(FinishedRun(), NewCompares(temp), lifetime ?? TimeSpan.FromDays(7));
}

public class ShareTokenTests
{
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("abc-DEF_0123456789xyzQ")]
    public void Well_formed_tokens_are_accepted(string token) => Assert.True(ShareStore.IsToken(token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("..\\..\\..\\Windows\\win.ini")]
    [InlineData("../../../../etc/passwd/xxxxxxxx")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA.json")]
    [InlineData("AAAAAAAAAAAAAAAA AAAAAA")]
    public void Anything_that_could_reach_the_disk_as_a_path_is_refused(string? token) =>
        Assert.False(ShareStore.IsToken(token));

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("abcd1234", true)]
    [InlineData("short", false)]
    [InlineData("..\\..\\x123456", false)]
    [InlineData("has space 12345", false)]
    public void Script_progress_ids_are_plain(string id, bool valid) =>
        Assert.Equal(valid, ScriptProgressStore.IsValidId(id));

    [Fact]
    public void Published_tokens_are_random_and_128_bit()
    {
        using var temp = new TempStore();
        var store = Shares.NewStore(temp);

        var a = Shares.Publish(temp, store);
        var b = Shares.Publish(temp, store);

        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(22, a.Token.Length); // 16 bytes, base64url, unpadded
        Assert.True(ShareStore.IsToken(a.Token));
    }

    [Fact]
    public void A_live_token_is_found_and_a_near_miss_is_not()
    {
        using var temp = new TempStore();
        var store = Shares.NewStore(temp);
        var share = Shares.Publish(temp, store);

        Assert.NotNull(store.Find(share.Token));

        var nearMiss = share.Token[..^1] + (share.Token[^1] == 'A' ? 'B' : 'A');
        Assert.Null(store.Find(nearMiss));
        Assert.Null(store.Find(share.Token + "A"));
    }

    [Fact]
    public void A_revoked_token_is_gone_and_its_live_streams_are_told()
    {
        using var temp = new TempStore();
        var store = Shares.NewStore(temp);
        var share = Shares.Publish(temp, store);
        var revoked = store.Revoked(share.Token);

        store.Revoke(share.Token);

        Assert.Null(store.Find(share.Token));
        Assert.Null(store.Load(share.Token));
        Assert.True(revoked.IsCancellationRequested);
    }

    [Fact]
    public void An_expired_token_stops_working_at_once()
    {
        using var temp = new TempStore();
        var store = Shares.NewStore(temp);
        var share = Shares.Publish(temp, store, TimeSpan.FromMilliseconds(50));

        Thread.Sleep(150);

        Assert.Null(store.Find(share.Token));
        Assert.DoesNotContain(store.List(), s => s.Token == share.Token);
    }

    [Fact]
    public void A_shared_bundle_carries_no_connection_string_or_password()
    {
        using var temp = new TempStore();
        var store = Shares.NewStore(temp);
        var share = Shares.Publish(temp, store);

        var file = Path.Combine(temp.Folder, "shares", $"{share.Token}.json.gz");
        Assert.True(File.Exists(file));

        using var gzip = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
        var json = new StreamReader(gzip).ReadToEnd();

        Assert.DoesNotContain(Shares.SecretPassword, json);
        Assert.DoesNotContain("prod-sql.internal;", json);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("********", json); // the error text survived, masked
    }

    [Fact]
    public void Scrubbing_for_a_share_leaves_the_owners_own_result_alone()
    {
        var session = Shares.FinishedRun();
        var copy = ShareStore.Scrubbed(session);

        Assert.DoesNotContain(Shares.SecretPassword, copy.Warnings[0]);
        Assert.Contains(Shares.SecretPassword, session.Result!.Warnings[0]);
    }
}

public class SecretScrubberTests
{
    [Fact]
    public void The_whole_connection_string_and_the_bare_password_are_both_masked()
    {
        var secrets = SecretScrubber.Secrets([Shares.SecretConnection, null, ""]);

        var text = SecretScrubber.Scrub($"a {Shares.SecretConnection} b {Shares.SecretPassword} c", secrets);

        Assert.DoesNotContain(Shares.SecretPassword, text);
        Assert.Equal("a ******** b ******** c", text);
    }
}

public class ViewerHostAllowListTests
{
    private static readonly HashSet<string> Mine = ["192.168.1.20", "fe80::1"];

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData("DESK-01")]
    [InlineData("desk-01.corp.example")]
    [InlineData("desk-01.local")]
    [InlineData("[fe80::1]")]
    public void This_machines_own_names_and_addresses_are_accepted(string host) =>
        Assert.True(ViewerHost.IsAllowedHost(host, Mine, "DESK-01", "corp.example"));

    [Theory]
    [InlineData("")]
    [InlineData("evil.example")]
    [InlineData("desk-01.evil.example")]
    [InlineData("10.0.0.9")]
    [InlineData("192.168.1.21")]
    [InlineData("desk-01.corp.example.evil.example")]
    public void Any_other_host_is_refused(string host) =>
        Assert.False(ViewerHost.IsAllowedHost(host, Mine, "DESK-01", "corp.example"));

    [Fact]
    public void The_firewall_rule_is_scoped_to_private_networks_the_local_subnet_and_this_program()
    {
        var args = ShareNetwork.AddRuleArguments(5280);

        Assert.Contains("localport=5280", args);
        Assert.Contains("profile=private,domain", args);
        Assert.Contains("remoteip=localsubnet", args);
        Assert.DoesNotContain("profile=any", args);
        Assert.Contains("delete rule", ShareNetwork.RemoveRuleArguments);
    }

    [Fact]
    public void Allowing_again_clears_the_old_rule_first_so_copies_never_pile_up()
    {
        Assert.Contains(ShareNetwork.RemoveRuleArguments, ShareNetwork.CleanupArguments);

        var program = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(program))
            Assert.Contains(ShareNetwork.CleanupArguments,
                a => a.Contains("name=all dir=in", StringComparison.Ordinal) &&
                     a.Contains($"program=\"{program}\"", StringComparison.Ordinal));
    }

    private const string Exe = @"C:\Users\me\AppData\Local\Programs\SchemaScope\SchemaScope.exe";

    /// <summary>The shape of <c>netsh advfirewall firewall show rule name=... verbose</c>.</summary>
    private static string Listing(string port, string program) => $"""

        Rule Name:                            SchemaScope share
        ----------------------------------------------------------------------
        Enabled:                              Yes
        Direction:                            In
        Profiles:                             Domain,Private
        LocalIP:                              Any
        RemoteIP:                             LocalSubnet
        Protocol:                             TCP
        LocalPort:                            {port}
        RemotePort:                           Any
        Program:                              {program}
        Action:                               Allow

        """;

    [Fact]
    public void A_rule_for_this_exe_and_port_counts_as_allowed() =>
        Assert.True(ShareNetwork.RuleCovers(Listing("5280", Exe) + "Ok.\r\n", 5280, Exe));

    [Fact]
    public void A_rule_pinned_to_another_copy_of_the_exe_does_not_count() =>
        Assert.False(ShareNetwork.RuleCovers(
            Listing("5280", @"C:\src\SchemaScope\dist\SchemaScope.exe"), 5280, Exe));

    [Fact]
    public void A_rule_for_another_port_does_not_count() =>
        Assert.False(ShareNetwork.RuleCovers(Listing("5281", Exe), 5280, Exe));

    [Fact]
    public void One_matching_rule_among_several_with_our_name_counts() =>
        Assert.True(ShareNetwork.RuleCovers(
            Listing("5281", @"C:\old\SchemaScope.exe") + Listing("5280", Exe), 5280, Exe));

    [Fact]
    public void Labels_in_another_language_still_match_on_their_values() =>
        Assert.True(ShareNetwork.RuleCovers(
            Listing("5280", Exe).Replace("LocalPort:", "Lokaler Port:").Replace("Program:", "Programm:"),
            5280, Exe));

    [Fact]
    public void Line_endings_from_netsh_do_not_matter() =>
        Assert.True(ShareNetwork.RuleCovers(
            Listing("5280", Exe).Replace("\r\n", "\n").Replace("\n", "\r\n"), 5280, Exe));
}

/// <summary>The real viewer app, on loopback and a free port, driven over HTTP.</summary>
public sealed class ViewerHostTests : IAsyncLifetime
{
    private readonly TempStore _temp = new();
    private ShareStore _store = null!;
    private ViewerHost _viewer = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _store = Shares.NewStore(_temp);
        _viewer = new ViewerHost(_store, NullLogger<ViewerHost>.Instance);

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
    public async Task A_live_share_can_be_read()
    {
        var share = Shares.Publish(_temp, _store);

        var response = await _http.GetAsync($"/api/view/{share.Token}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("X-Robots-Tag"));
        Assert.DoesNotContain(Shares.SecretPassword, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/connections")]
    [InlineData("/api/compare")]
    [InlineData("/api/history")]
    [InlineData("/api/shares")]
    [InlineData("/api/scripts/last-folder")]
    [InlineData("/api/health")]
    public async Task No_owner_route_exists_on_the_network_listener(string path)
    {
        var response = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_listener_has_exactly_the_routes_it_says_it_has()
    {
        var app = (IEndpointRouteBuilder)typeof(ViewerHost)
            .GetField("_app", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(_viewer)!;

        var routes = app.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'))
            .ToHashSet();

        Assert.Equal(ViewerHost.Routes.ToHashSet(), routes);

        var methods = app.DataSources
            .SelectMany(d => d.Endpoints)
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .ToHashSet();
        Assert.Equal(["GET"], methods);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Anything_but_a_read_is_refused(string method)
    {
        var share = Shares.Publish(_temp, _store);
        var response = await _http.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/view/{share.Token}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_foreign_host_name_is_refused()
    {
        var share = Shares.Publish(_temp, _store);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/view/{share.Token}");
        request.Headers.Host = "rebind.attacker.example";

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_revoked_and_expired_tokens_all_get_404()
    {
        var revoked = Shares.Publish(_temp, _store);
        _store.Revoke(revoked.Token);
        var expired = Shares.Publish(_temp, _store, TimeSpan.FromMilliseconds(50));
        await Task.Delay(150);

        foreach (var token in new[] { "AAAAAAAAAAAAAAAAAAAAAA", revoked.Token, expired.Token })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/view/{token}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/view/{token}/object?key=x")).StatusCode);
        }
    }

    [Fact]
    public async Task Repeated_wrong_guesses_are_slowed_down()
    {
        for (var i = 0; i < ViewerHost.MissesPerMinute; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/view/AAAAAAAAAAAAAAAAAAAA{i:00}")).StatusCode);

        // Over budget: even a real token is turned away for the rest of the minute.
        var share = Shares.Publish(_temp, _store);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _http.GetAsync($"/api/view/{share.Token}")).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_share_closes_its_open_live_streams()
    {
        var share = Shares.Publish(_temp, _store);

        using var response = await _http.GetAsync($"/api/view/{share.Token}/live", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        Assert.True(await stream.ReadAsync(buffer) > 0); // the first ping

        _store.Revoke(share.Token);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, timeout.Token);
        Assert.Equal(0, read); // the server ended the stream
    }

    [Fact]
    public async Task One_address_cannot_hold_open_unlimited_live_streams()
    {
        var share = Shares.Publish(_temp, _store);
        var open = new List<HttpResponseMessage>();
        try
        {
            for (var i = 0; i < ViewerHost.LiveConnectionsPerAddress; i++)
            {
                var r = await _http.GetAsync($"/api/view/{share.Token}/live", HttpCompletionOption.ResponseHeadersRead);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                open.Add(r);
            }

            var extra = await _http.GetAsync($"/api/view/{share.Token}/live", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.TooManyRequests, extra.StatusCode);
        }
        finally
        {
            foreach (var r in open) r.Dispose();
        }
    }
}

/// <summary>The owner API, started for real, proving every new route sits behind the launch token.</summary>
public sealed class OwnerRouteTests : IAsyncLifetime
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
        await _app.Services.GetRequiredService<ViewerHost>().StopAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _temp.Dispose();
    }

    public static TheoryData<string, string> NewOwnerRoutes => new()
    {
        { "GET", "/api/shares" },
        { "POST", "/api/compare/abc/share" },
        { "DELETE", "/api/shares/AAAAAAAAAAAAAAAAAAAAAA" },
        { "POST", "/api/shares/start" },
        { "POST", "/api/shares/stop" },
        { "POST", "/api/shares/firewall" },
        { "DELETE", "/api/shares/firewall" },
        { "POST", "/api/compare/abc/scripts" },
        { "GET", "/api/compare/abc/object-script/dependencies?key=x&targetIndex=0" },
        { "POST", "/api/compare/abc/object-script" },
        { "GET", "/api/compare/abc/references?name=abcd" },
        { "GET", "/api/compare/abc/uses?key=abc" },
        { "GET", "/api/scripts/last-folder" },
        { "GET", "/api/scripts/progress/abcdefgh" },
        { "GET", "/api/connections" },
    };

    [Theory]
    [MemberData(nameof(NewOwnerRoutes))]
    public async Task Every_owner_route_needs_the_launch_token(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : new StringContent("{}", Encoding.UTF8, "application/json")
        };

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(_app.Services.GetRequiredService<ViewerHost>().IsRunning);
    }

    [Theory]
    [MemberData(nameof(NewOwnerRoutes))]
    public async Task Every_owner_route_refuses_a_foreign_host_even_with_the_token(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = "rebind.attacker.example";
        request.Headers.Add(LocalAuth.HeaderName, _auth.Token);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_the_token_the_owner_routes_answer()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/scripts/last-folder");
        request.Headers.Add(LocalAuth.HeaderName, _auth.Token);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_scripts_route_refuses_a_form_post()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/compare/abc/scripts")
        {
            Content = new FormUrlEncodedContent([new("targetIndex", "0")])
        };
        request.Headers.Add(LocalAuth.HeaderName, _auth.Token);

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}

public class ScriptFolderTests
{
    private static ScriptOptions To(string output, string? quarantine = null) => new()
    {
        OutputFolder = output,
        QuarantineFolder = quarantine
    };

    [Fact]
    public void An_ordinary_folder_is_allowed() =>
        Assert.Null(SchemaScopeApi.CheckScriptFolders(To(Path.Combine(Path.GetTempPath(), "schemascope-scripts"))));

    [Theory]
    [InlineData(Environment.SpecialFolder.Startup)]
    [InlineData(Environment.SpecialFolder.CommonStartup)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    public void A_RUN_ps1_is_never_written_into_a_system_or_startup_folder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) return;

        Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(path)));
        Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(Path.Combine(path, "scripts"))));
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\scripts")]
    [InlineData(@"\\?\UNC\attacker.example\share")]
    [InlineData("//attacker.example/share")]
    [InlineData("scripts")]
    [InlineData(@"..\..\scripts")]
    [InlineData("")]
    public void Network_and_relative_paths_are_refused(string path) =>
        Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(path)));

    [Fact]
    public void The_quarantine_folder_gets_the_same_rules()
    {
        var ok = Path.Combine(Path.GetTempPath(), "schemascope-scripts");
        Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(ok, @"\\attacker.example\q")));
        Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(ok, "relative")));

        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrEmpty(startup))
            Assert.NotNull(SchemaScopeApi.CheckScriptFolders(To(ok, startup)));
    }
}

public class ScriptTextTests
{
    [Fact]
    public void Identifiers_cannot_break_out_of_their_brackets() =>
        Assert.Equal("[evil]]; DROP TABLE x; --]", Ddl.Bracket("evil]; DROP TABLE x; --"));

    [Fact]
    public void Literals_cannot_break_out_of_their_quotes() =>
        Assert.Equal("N'O''Brien''; DROP TABLE x; --'", ScriptText.Lit("O'Brien'; DROP TABLE x; --"));

    [Theory]
    [InlineData(@"..\..\Windows\evil")]
    [InlineData("dbo/../../x")]
    [InlineData("C:evil")]
    public void File_names_built_from_object_names_cannot_leave_their_folder(string name)
    {
        var safe = ScriptText.Safe(name);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain(':', safe);
    }

    [Fact]
    public void A_GO_inside_a_module_body_cannot_start_a_batch_of_its_own()
    {
        // A procedure body is shipped inside an N'' literal. If a raw line of it could reach
        // the start of a line in the .sql file, sqlcmd would read "GO" as a batch separator
        // and run whatever follows as a statement of its own.
        var body = "CREATE PROCEDURE dbo.p AS\r\nSELECT 1\r\nGO\r\nDROP TABLE dbo.Customers\r\n";

        var script = ScriptText.ExecBlock(body);
        var lines = script.Replace("\r\n", "\n").Split('\n');

        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase));
    }
}
