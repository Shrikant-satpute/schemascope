using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SchemaScope.Api;
using SchemaScope.Core.Storage;

// SchemaScopeApi's static helpers read connection strings with the engines of the app built last; two owner apps
// started at once by two test collections (one with preview engines on, one not yet) would race on it. The desktop
// app builds one owner app per process, so the tests run one collection at a time, as it does.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SchemaScope.Engines.Tests;

/// <summary>A throwaway store, so a test never touches the user's saved connections.</summary>
public sealed class TempStore : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"schemascope-engine-test-{Guid.NewGuid():n}");
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

/// <summary>The owner API started for real on a loopback port, with its own store, and a client carrying the token.</summary>
public sealed class OwnerApp : IAsyncDisposable
{
    public TempStore Temp { get; } = new();
    public LocalAuth Auth { get; } = LocalAuth.Mint();
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<OwnerApp> StartAsync()
    {
        var o = new OwnerApp();
        o.App = SchemaScopeApi.Build([], desktop: false, o.Auth, storePath: o.Temp.DbPath);
        o.App.Urls.Clear();
        o.App.Urls.Add("http://127.0.0.1:0");
        await o.App.StartAsync();
        var address = o.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        o.Http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
        o.Http.DefaultRequestHeaders.Add(LocalAuth.HeaderName, o.Auth.Token);
        return o;
    }

    public Task<HttpResponseMessage> PostJson(string path, string json) =>
        Http.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

    public Task<HttpResponseMessage> Post<T>(string path, T body) => Http.PostAsJsonAsync(path, body, Json);

    public async Task SetPreviewAsync(bool on)
    {
        var r = await PostJson("/api/engines/preview", $$"""{ "enabled": {{(on ? "true" : "false")}} }""");
        r.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Temp.Dispose();
    }
}
