using Microsoft.AspNetCore.Http;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;

namespace SchemaScope.Tests;

/// <summary>
/// Regression cover for the things that would be worth attacking: the loopback
/// API's front door, the stored passwords behind it, and the one endpoint that
/// writes to disk.
/// </summary>
public class LocalAuthTests
{
    private static HttpRequest RequestWith(Action<HttpRequest> setup)
    {
        var ctx = new DefaultHttpContext();
        setup(ctx.Request);
        return ctx.Request;
    }

    [Fact]
    public void Minted_tokens_are_unpredictable_and_unique()
    {
        var a = LocalAuth.Mint();
        var b = LocalAuth.Mint();

        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(64, a.Token.Length); // 32 bytes, hex
    }

    [Fact]
    public void The_right_token_in_the_header_is_accepted()
    {
        var auth = LocalAuth.Mint();
        Assert.True(auth.IsAuthorised(RequestWith(r =>
            r.Headers[LocalAuth.HeaderName] = auth.Token)));
    }

    [Fact]
    public void The_right_token_in_the_query_is_accepted_so_sse_can_connect()
    {
        var auth = LocalAuth.Mint();
        Assert.True(auth.IsAuthorised(RequestWith(r =>
            r.QueryString = new QueryString($"?{LocalAuth.QueryName}={auth.Token}"))));
    }

    [Fact]
    public void A_request_with_no_token_is_refused()
    {
        Assert.False(LocalAuth.Mint().IsAuthorised(RequestWith(_ => { })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void A_request_with_the_wrong_token_is_refused(string candidate)
    {
        var auth = LocalAuth.Mint();
        Assert.False(auth.IsAuthorised(RequestWith(r => r.Headers[LocalAuth.HeaderName] = candidate)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void Our_own_loopback_origin_is_allowed(string host) =>
        Assert.True(LocalAuth.IsLoopbackHost(new HostString(host, 5199)));

    [Theory]
    // What a DNS rebinding attack looks like when it lands: the request really
    // does arrive on 127.0.0.1, but it is addressed to somebody else's name.
    [InlineData("rebind.attacker.example")]
    [InlineData("schemascope.evil.test")]
    [InlineData("192.168.1.20")]
    public void A_request_addressed_to_another_host_is_refused(string host) =>
        Assert.False(LocalAuth.IsLoopbackHost(new HostString(host, 5199)));
}

public sealed class RehydrateTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"schemascope-test-{Guid.NewGuid():n}.db");

    private readonly SchemaScopeStore _store;
    private readonly ConnectionSettings _saved;

    private const string RealPassword = "correct horse battery staple";

    public RehydrateTests()
    {
        _store = new SchemaScopeStore(_dbPath);
        _saved = _store.SaveConnection(new ConnectionSettings
        {
            Label = "Production",
            Server = "prod-sql.internal",
            Database = "Sales",
            Auth = AuthMode.SqlLogin,
            UserName = "reporting",
            Password = RealPassword
        });
    }

    /// <summary>What the UI sends back after loading a saved connection.</summary>
    private ConnectionSettings Masked() => new()
    {
        Id = _saved.Id,
        Label = _saved.Label,
        Server = _saved.Server,
        Database = _saved.Database,
        Auth = _saved.Auth,
        UserName = _saved.UserName,
        Password = "********"
    };

    [Fact]
    public void The_stored_password_comes_back_for_the_connection_it_belongs_to()
    {
        var request = Masked();

        Assert.True(SchemaScopeApi.TryRehydrate(request, _store, out var error));
        Assert.Null(error);
        Assert.Equal(RealPassword, request.Password);
    }

    [Fact]
    public void Switching_to_another_database_on_the_same_server_still_works()
    {
        var request = Masked();
        request.Database = "Archive";

        Assert.True(SchemaScopeApi.TryRehydrate(request, _store, out _));
        Assert.Equal(RealPassword, request.Password);
    }

    [Fact]
    public void A_redirected_server_never_receives_the_stored_password()
    {
        // The attack: name a saved connection, point it somewhere else, and let
        // the tool hand the password to a server you control.
        var request = Masked();
        request.Server = "attacker.example.com";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _store, out var error));
        Assert.NotNull(error);
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void A_swapped_login_never_receives_the_stored_password()
    {
        var request = Masked();
        request.UserName = "sa";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _store, out _));
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void A_swapped_auth_mode_never_receives_the_stored_password()
    {
        var request = Masked();
        request.Auth = AuthMode.AzureAdPassword;

        Assert.False(SchemaScopeApi.TryRehydrate(request, _store, out _));
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void Asking_for_a_raw_connection_string_does_not_smuggle_the_password_out()
    {
        var request = Masked();
        request.UseRawConnectionString = true;
        request.RawConnectionString = "Server=attacker.example.com;User Id=sa;Password=********";

        Assert.False(SchemaScopeApi.TryRehydrate(request, _store, out _));
        Assert.NotEqual(RealPassword, request.Password);
    }

    [Fact]
    public void An_unknown_connection_id_yields_nothing()
    {
        var request = Masked();
        request.Id = Guid.NewGuid().ToString("n");

        SchemaScopeApi.TryRehydrate(request, _store, out _);
        Assert.NotEqual(RealPassword, request.Password);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(path); } catch (IOException) { /* left behind in temp */ }
    }
}

public class ExportFolderTests
{
    [Fact]
    public void An_ordinary_folder_is_allowed()
    {
        var target = Path.Combine(Path.GetTempPath(), "schemascope-export");
        Assert.True(ExportService.IsWritableLocation(target, out var root, out var refusal));
        Assert.Null(refusal);
        Assert.Equal(Path.GetFullPath(target), root);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.Startup)]
    public void System_folders_are_refused(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path)) return; // not defined on this machine

        Assert.False(ExportService.IsWritableLocation(path, out _, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Writing_into_a_subfolder_of_a_system_folder_is_refused_too()
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrEmpty(startup)) return;

        Assert.False(ExportService.IsWritableLocation(
            Path.Combine(startup, "innocent-looking"), out _, out var refusal));
        Assert.NotNull(refusal);
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.False(ExportService.IsWritableLocation("..\\..\\somewhere", out _, out var refusal));
        Assert.NotNull(refusal);
    }
}
