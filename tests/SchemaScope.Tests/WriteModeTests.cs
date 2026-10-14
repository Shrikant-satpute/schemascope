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

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Plan files written to a scratch folder the way the generator writes them, hashed.</summary>
internal static class PlanFiles
{
    public static WrittenFile Write(string root, string folder, string file, string text, string? obj = null)
    {
        var rel = "objects\\" + file;
        var full = Path.Combine(root, folder, "objects", file);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return new WrittenFile(folder, rel, full, WrittenFileKind.Statements)
        {
            Object = obj is null ? null : new ObjectName("dbo", obj),
            Sha256 = ApplyPlans.Hash(full),
        };
    }

    public static PlanFile Plan(WrittenFile f) =>
        new(f.Folder, f.RelativePath, f.FullPath, f.Object?.ToString() ?? "", ApplyPlans.Hash(f.FullPath));
}

public class WriteModeSwitchTests
{
    [Fact]
    public void It_starts_off_and_turns_itself_off_after_thirty_idle_minutes()
    {
        var clock = new FakeClock();
        var mode = new WriteMode(clock);
        Assert.False(mode.Enabled);
        Assert.Null(mode.State.ExpiresUtc);

        var on = mode.Enable();
        Assert.True(on.Enabled);
        Assert.Equal(clock.Now.UtcDateTime + WriteMode.IdleTimeout, on.ExpiresUtc);

        clock.Now += TimeSpan.FromMinutes(29);
        mode.Touch();   // activity pushes the expiry back
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.True(mode.Enabled);

        clock.Now += TimeSpan.FromMinutes(2);
        Assert.False(mode.Enabled);
        mode.Touch();   // activity never turns it back on
        Assert.False(mode.Enabled);
    }

    [Fact]
    public void Turning_it_off_is_immediate()
    {
        var mode = new WriteMode();
        mode.Enable();
        Assert.False(mode.Disable().Enabled);
        Assert.False(mode.Enabled);
    }
}

public class ApplyPlanTests
{
    [Fact]
    public void A_plan_id_is_128_random_bits_lives_two_hours_and_is_taken_once()
    {
        var clock = new FakeClock();
        var store = new ApplyPlanStore(clock);
        var a = store.Add(new ApplyPlan { RunId = "r", TargetIndex = 0 });
        var b = store.Add(new ApplyPlan { RunId = "r", TargetIndex = 0 });

        Assert.Matches("^[0-9a-f]{32}$", a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Same(a, store.Get(a.Id));

        Assert.True(store.Take(a));
        Assert.Null(store.Get(a.Id));
        Assert.False(store.Take(a));

        clock.Now += ApplyPlanStore.Ttl;
        Assert.Null(store.Get(b.Id));
        Assert.Null(store.Get(null));
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void Build_runs_objects_files_in_run_order_and_ticks_only_the_run_ps1_set()
    {
        using var temp = new TempFolder();
        var written = new List<WrittenFile>
        {
            PlanFiles.Write(temp.Path, "03_missing", "002_b.sql", "SELECT 2", "b"),
            PlanFiles.Write(temp.Path, "03_missing", "001_a.sql", "SELECT 1", "a"),
            PlanFiles.Write(temp.Path, "10_different_modules", "001_p.sql", "SELECT 3", "p"),
            PlanFiles.Write(temp.Path, "04_columntypes", "001_t.sql", "SELECT 4", "t"),
            PlanFiles.Write(temp.Path, "11_destructive", "001_d.sql", "SELECT 5", "d"),
            PlanFiles.Write(temp.Path, "99_review", "001_r.sql", "SELECT 6"),
        };
        var rollback = Path.Combine(temp.Path, "10_different_modules", "rollback", "001_p.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
        File.WriteAllText(rollback, "x");
        written.Add(new WrittenFile("10_different_modules", "rollback\\001_p.sql", rollback, WrittenFileKind.Statements));

        var (files, sections) = ApplyPlans.Build(written, [new FolderResult { Folder = "03_missing", Title = "Missing objects" }]);

        Assert.Equal(["04_columntypes\\objects\\001_t.sql", "03_missing\\objects\\001_a.sql", "03_missing\\objects\\002_b.sql",
                      "10_different_modules\\objects\\001_p.sql", "11_destructive\\objects\\001_d.sql"],
                     files.Select(f => $"{f.Folder}\\{f.RelativePath}"));
        Assert.Equal("dbo.a", files[1].ObjectName);
        Assert.Equal(ApplyPlans.Hash(written[1].FullPath), files[1].Sha256);

        Assert.Equal(["04_columntypes", "03_missing", "10_different_modules", "11_destructive"], sections.Select(s => s.Folder));
        Assert.Equal([true, true, false, false], sections.Select(s => s.DefaultSelected));
        Assert.Equal(["normal", "normal", "changesCode", "destructive"], sections.Select(s => s.Risk));
        Assert.Equal("Missing objects", sections[1].Title);
        Assert.Equal("Changes that can lose data", sections[3].Title);
        Assert.Null(sections[0].Warning);
        Assert.NotNull(sections[2].Warning);
        Assert.Equal(2, sections[1].Files);
    }

    [Fact]
    public void The_plan_hash_is_the_one_taken_as_the_file_was_written_and_a_file_changed_since_gets_no_plan()
    {
        using var temp = new TempFolder();
        var writer = new ScriptWriter(temp.Path);
        var f = writer.WriteSql("01_columns", "objects\\001_c.sql", "ALTER TABLE dbo.A ADD b int\r\nGO\r\n");
        Assert.Equal(ApplyPlans.Hash(f.FullPath), f.Sha256);
        Assert.Equal(f.Sha256, Assert.Single(writer.Files).Sha256);
        Assert.Equal(f.Sha256, Assert.Single(ApplyPlans.Build(writer.Files, []).Files).Sha256);

        // swapped on disk while the run was still validating: the swapped text never becomes the trusted version
        File.WriteAllText(f.FullPath, "EXEC sp_addsrvrolemember 'evil', 'sysadmin'", new UTF8Encoding(true));
        Assert.Throws<IOException>(() => ApplyPlans.Build(writer.Files, []));

        using var store = new TempStore();
        var mode = new WriteMode();
        mode.Enable();
        var offer = SchemaScopeApi.Offer(Session(), 0, new ScriptOutcome { Success = true, Files = [.. writer.Files] }, true, mode, new ApplyPlanStore(), store.Store);
        Assert.Null(offer.PlanId);
    }

    [Fact]
    public void Files_sort_by_their_number_so_a_thousandth_file_runs_after_the_hundred_and_first()
    {
        using var temp = new TempFolder();
        var written = new[]
        {
            PlanFiles.Write(temp.Path, "11_destructive", "1000_drop_table_X.sql", "SELECT 1"),
            PlanFiles.Write(temp.Path, "11_destructive", "101_drop_fk_Y.sql", "SELECT 2"),
            PlanFiles.Write(temp.Path, "11_destructive", "999_drop_index_Z.sql", "SELECT 3"),
        };

        var (files, _) = ApplyPlans.Build(written, []);

        Assert.Equal(["objects\\101_drop_fk_Y.sql", "objects\\999_drop_index_Z.sql", "objects\\1000_drop_table_X.sql"], files.Select(f => f.RelativePath));
    }

    private static CompareSession Session(string targetDb = "Client")
    {
        var session = new CompareSession
        {
            Status = "done",
            SourceConnection = new SchemaSource { Label = "Dev", ConnectionString = ApplyRouteTests.Conn("Dev") },
            TargetConnections = [new SchemaSource { Label = "Client", ConnectionString = ApplyRouteTests.Conn(targetDb) }],
        };
        session.Result = new CompareResult { Source = new SnapshotInfo { Label = "Dev" }, Targets = [new TargetSummary { Label = "Client" }] };
        return session;
    }

    [Fact]
    public void A_plan_id_is_offered_only_while_write_mode_is_on_for_a_complete_validated_run()
    {
        using var temp = new TempFolder();
        using var store = new TempStore();
        var outcome = new ScriptOutcome { Success = true, Files = [PlanFiles.Write(temp.Path, "01_columns", "001_c.sql", "SELECT 1", "c")] };
        var plans = new ApplyPlanStore();
        var mode = new WriteMode();
        var session = Session();

        var off = SchemaScopeApi.Offer(session, 0, outcome, true, mode, plans, store.Store);
        Assert.Null(off.PlanId);
        Assert.Equal("Client", off.ApplyTarget!.Database);
        Assert.Equal("01_columns", Assert.Single(off.PlanSections).Folder);

        mode.Enable();
        var on = SchemaScopeApi.Offer(session, 0, outcome, true, mode, plans, store.Store);
        Assert.NotNull(plans.Get(on.PlanId));
        Assert.Equal("SQL login: deploy", on.ApplyTarget!.Login);
        Assert.False(on.ApplyTarget.Protected);

        Assert.Same(PlanOffer.None, SchemaScopeApi.Offer(session, 0, outcome, complete: false, mode, plans, store.Store));
        Assert.Null(SchemaScopeApi.Offer(Session(targetDb: "Dev"), 0, outcome, true, mode, plans, store.Store).PlanId);   // the source itself
    }
}

/// <summary>The apply and write-mode routes on the real owner app. Nothing here reaches a database.</summary>
public sealed class ApplyRouteTests : IAsyncLifetime
{
    /// <summary>A refused loopback port: an attempt to connect fails at once.</summary>
    public static string Conn(string db) =>
        $"Data Source=tcp:127.0.0.1,1;Initial Catalog={db};User ID=deploy;Password={Shares.SecretPassword};Connect Timeout=1;Encrypt=false";

    private readonly TempStore _temp = new();
    private readonly TempFolder _files = new();
    private readonly LocalAuth _auth = LocalAuth.Mint();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private CompareSession _session = null!;

    private WriteMode Mode => _app.Services.GetRequiredService<WriteMode>();
    private ApplyPlanStore Plans => _app.Services.GetRequiredService<ApplyPlanStore>();

    public async Task InitializeAsync()
    {
        _app = SchemaScopeApi.Build([], desktop: false, _auth, storePath: _temp.DbPath);
        _app.Urls.Clear();
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add(LocalAuth.HeaderName, _auth.Token);

        _session = _app.Services.GetRequiredService<CompareSessionStore>().Create();
        _session.Status = "done";
        _session.SourceConnection = new SchemaSource { Label = "Dev", Server = "tcp:127.0.0.1,1", ConnectionString = Conn("Dev") };
        _session.TargetConnections = [new SchemaSource { Label = "Client", Server = "tcp:127.0.0.1,1", ConnectionString = Conn("Client") }];
        _session.Result = new CompareResult { Source = new SnapshotInfo { Label = "Dev" }, Targets = [new TargetSummary { Label = "Client" }] };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _files.Dispose();
        _temp.Dispose();
    }

    private ApplyPlan NewPlan(string? runId = null, int targetIndex = 0)
    {
        var written = new[]
        {
            PlanFiles.Write(_files.Path, "01_columns", $"001_{Guid.NewGuid():n}.sql", "ALTER TABLE dbo.A ADD b int\r\nGO\r\n", "A"),
            PlanFiles.Write(_files.Path, "11_destructive", $"001_{Guid.NewGuid():n}.sql", "ALTER TABLE dbo.A DROP COLUMN c\r\nGO\r\n", "A"),
        };
        var (files, sections) = ApplyPlans.Build(written, []);
        return Plans.Add(new ApplyPlan { RunId = runId ?? _session.Id, TargetIndex = targetIndex, Files = files, Sections = sections });
    }

    private static object Body(ApplyPlan plan, string confirm = "Client", string[]? sections = null, bool acceptDataLoss = false) => new
    {
        planId = plan.Id,
        targetIndex = 0,
        sections = sections ?? ["01_columns"],
        confirmDatabase = confirm,
        acceptDataLoss,
    };

    private Task<HttpResponseMessage> Apply(object body) => _http.PostAsJsonAsync($"/api/compare/{_session.Id}/apply", body);

    private static async Task<string> Message(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("message").GetString() ?? "";

    [Fact]
    public async Task Write_mode_starts_off_and_needs_the_confirmation_word()
    {
        var health = JsonDocument.Parse(await _http.GetStringAsync("/api/health")).RootElement;
        Assert.True(health.GetProperty("readOnly").GetBoolean());
        var state = JsonDocument.Parse(await _http.GetStringAsync("/api/write-mode")).RootElement;
        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("expiresUtc").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/write-mode", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/write-mode", new { enabled = true, confirm = "enable" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.PostAsync("/api/write-mode",
            new FormUrlEncodedContent([new("enabled", "true"), new("confirm", "ENABLE")]))).StatusCode);
        Assert.False(Mode.Enabled);

        var on = await _http.PostAsJsonAsync("/api/write-mode", new { enabled = true, confirm = "ENABLE" });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var body = JsonDocument.Parse(await on.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.String, body.GetProperty("expiresUtc").ValueKind);
        health = JsonDocument.Parse(await _http.GetStringAsync("/api/health")).RootElement;
        Assert.False(health.GetProperty("readOnly").GetBoolean());

        var off = await _http.DeleteAsync("/api/write-mode");
        Assert.False(JsonDocument.Parse(await off.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(Mode.Enabled);
    }

    [Fact]
    public async Task The_query_token_cannot_turn_write_mode_on()
    {
        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        var r = await bare.PostAsync($"/api/write-mode?k={_auth.Token}",
            new StringContent("{\"enabled\":true,\"confirm\":\"ENABLE\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.False(Mode.Enabled);
    }

    [Fact]
    public async Task Apply_is_refused_while_write_mode_is_off()
    {
        var r = await Apply(Body(NewPlan()));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Read & write", await Message(r));
    }

    [Fact]
    public async Task Apply_refuses_a_form_post()
    {
        Mode.Enable();
        var r = await _http.PostAsync($"/api/compare/{_session.Id}/apply", new FormUrlEncodedContent([new("planId", "x")]));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public async Task An_unknown_expired_or_foreign_plan_is_refused()
    {
        Mode.Enable();
        Assert.Equal(HttpStatusCode.BadRequest, (await Apply(new { planId = "nope", targetIndex = 0, sections = new[] { "01_columns" }, confirmDatabase = "Client" })).StatusCode);

        var expired = NewPlan();
        expired.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        var r = await Apply(Body(expired));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("unknown or has expired", await Message(r));

        r = await Apply(Body(NewPlan(runId: "another-run")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("another comparison or target", await Message(r));

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync("/api/compare/nope/apply", Body(NewPlan()))).StatusCode);
    }

    [Fact]
    public async Task The_typed_database_name_must_match_the_target()
    {
        Mode.Enable();
        var r = await Apply(Body(NewPlan(), confirm: "Dev"));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Type the target database name, Client", await Message(r));
    }

    [Fact]
    public async Task A_protected_target_is_refused()
    {
        Mode.Enable();
        _session.TargetConnections[0].ProtectedServer = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await Apply(Body(NewPlan(), confirm: "CLIENT"))).StatusCode);

        // marked protected after the compare: the saved connection is read again
        _session.TargetConnections[0].ProtectedServer = false;
        var saved = _temp.Store.SaveConnection(new ConnectionSettings { Server = "elsewhere", Database = "Client", ProtectedServer = true });
        _session.TargetConnections[0].ConnectionId = saved.Id;
        var r = await Apply(Body(NewPlan()));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Protected", await Message(r));
    }

    [Fact]
    public async Task A_protected_connection_saved_as_a_raw_connection_string_protects_its_server()
    {
        Mode.Enable();
        // the Server box is hidden in raw mode, so it is empty here; the server is in the string
        _temp.Store.SaveConnection(new ConnectionSettings
        {
            Server = "",
            UseRawConnectionString = true,
            RawConnectionString = "Data Source=tcp:127.0.0.1,1;Initial Catalog=Other;Integrated Security=true",
            ProtectedServer = true,
        });

        var r = await Apply(Body(NewPlan()));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Protected", await Message(r));
    }

    [Fact]
    public async Task A_protected_connection_protects_the_server_however_the_target_spells_it()
    {
        Mode.Enable();
        // the target is "tcp:127.0.0.1,1": the same machine as "localhost"
        _temp.Store.SaveConnection(new ConnectionSettings { Server = "localhost", Database = "Other", ProtectedServer = true });

        var r = await Apply(Body(NewPlan()));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Contains("Protected", await Message(r));
    }

    [Theory]
    [InlineData("tcp:PRODSQL,1433", "prodsql")]
    [InlineData("PRODSQL.corp.local\\SQL2025", "PRODSQL\\sql2025")]
    [InlineData("PRODSQL\\MSSQLSERVER", "PRODSQL")]
    [InlineData(".\\SQL2025", "(local)\\SQL2025")]
    [InlineData("localhost,1433", "127.0.0.1")]
    [InlineData("np:.", "localhost")]
    public void Server_spellings_of_one_server_are_the_same(string a, string b) => Assert.True(ServerNames.Same(a, b));

    [Fact]
    public void Local_aliases_are_this_machine() => Assert.True(ServerNames.Same(".\\X", $"{Environment.MachineName}\\X"));

    [Theory]
    [InlineData("PRODSQL", "PRODSQL2")]
    [InlineData("PRODSQL\\A", "PRODSQL\\B")]
    [InlineData("10.0.0.5", "10.0.0.6")]
    [InlineData("10.0.0.5", "10")]
    public void Different_servers_stay_different(string a, string b) => Assert.False(ServerNames.Same(a, b));

    [Fact]
    public async Task Changes_that_can_lose_data_need_the_extra_tick_and_sections_must_be_the_plans()
    {
        Mode.Enable();
        var r = await Apply(Body(NewPlan(), sections: ["01_columns", "11_destructive"]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("I understand this can lose data", await Message(r));

        r = await Apply(Body(NewPlan(), sections: ["07_triggers"]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("not a section of this plan", await Message(r));

        Assert.Equal(HttpStatusCode.BadRequest, (await Apply(Body(NewPlan(), sections: []))).StatusCode);
    }

    [Fact]
    public async Task A_second_apply_to_the_same_target_is_a_409()
    {
        Mode.Enable();
        var applier = _app.Services.GetRequiredService<ApplyService>();
        var key = ApplyService.TargetKey("tcp:127.0.0.1,1", "Client");
        Assert.True(applier.TryLock(key));
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await Apply(Body(NewPlan()))).StatusCode);
        }
        finally
        {
            applier.Unlock(key);
        }
    }

    [Fact]
    public async Task A_file_changed_after_generation_is_refused_before_anything_runs()
    {
        Mode.Enable();
        var plan = NewPlan();
        File.AppendAllText(plan.Files[0].FullPath, "\r\nDROP TABLE dbo.A\r\n");

        var r = await Apply(Body(plan));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("was changed after it was generated", await Message(r));
        Assert.Empty(_temp.Store.GetApplyRuns());   // nothing reached the database, so nothing to log
        Assert.Null(Plans.Get(plan.Id));            // and the plan is gone
    }

    [Fact]
    public async Task A_checked_apply_runs_is_logged_scrubbed_and_cannot_be_replayed()
    {
        Mode.Enable();
        var plan = NewPlan();

        var r = await Apply(Body(plan, confirm: "client"));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Shares.SecretPassword, text);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());   // the port refuses: the first file fails to connect
        var file = Assert.Single(body.GetProperty("applied").EnumerateArray());
        Assert.False(file.GetProperty("ok").GetBoolean());
        Assert.Equal("01_columns", file.GetProperty("folder").GetString());
        Assert.StartsWith("01_columns\\objects\\", body.GetProperty("stoppedAt").GetString());

        var run = Assert.Single(_temp.Store.GetApplyRuns());
        Assert.Equal(body.GetProperty("auditId").GetString(), run.Id);
        Assert.Equal("SQL login: deploy", run.Login);
        Assert.Equal(["01_columns"], run.Sections);
        Assert.False(Assert.Single(run.Files).Ok);
        var history = await _http.GetStringAsync("/api/apply-history");
        Assert.Contains(run.Id, history);
        Assert.DoesNotContain(Shares.SecretPassword, history);

        Assert.Equal(HttpStatusCode.BadRequest, (await Apply(Body(plan, confirm: "client"))).StatusCode);   // single use
    }

    [Fact]
    public async Task Stopping_an_apply_that_is_not_running_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/apply/abcdefgh/stop", null)).StatusCode);
    }

    [Fact]
    public async Task Changes_that_can_lose_data_cannot_even_be_generated_without_write_mode()
    {
        var r = await _http.PostAsJsonAsync($"/api/compare/{_session.Id}/scripts", new
        {
            targetIndex = 0,
            options = new { outputFolder = _files.Path, sections = new[] { "newColumns" }, includeDestructive = true },
        });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Turn on Read & write to include changes that can lose data", await Message(r));
    }
}

/// <summary>The LAN viewer has none of the write routes and never shows write mode.</summary>
public sealed class ApplyViewerTests : IAsyncLifetime
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

    [Theory]
    [InlineData("GET", "/api/write-mode")]
    [InlineData("POST", "/api/write-mode")]
    [InlineData("POST", "/api/compare/abc/apply")]
    [InlineData("POST", "/api/apply/abcdefgh/stop")]
    [InlineData("GET", "/api/apply-history")]
    public async Task The_viewer_has_no_write_routes(string method, string path)
    {
        var r = await _http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        Assert.False(r.IsSuccessStatusCode);
        Assert.DoesNotContain(ViewerHost.Routes, x => x.Contains("apply", StringComparison.Ordinal) || x.Contains("write", StringComparison.Ordinal));
    }
}
