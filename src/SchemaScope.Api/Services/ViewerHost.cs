using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;

namespace SchemaScope.Api.Services;

/// <summary>
/// The listener other people on the network talk to.
///
/// It is a second, separate web app, and that is the whole security design. The
/// owner's API - connections, compare, history - lives on a loopback socket the
/// network cannot reach at all, behind the launch token. This one binds to every
/// interface, but only a handful of read-only routes are ever registered on it.
/// The compare endpoint is not blocked here by a check somebody could get wrong;
/// there is no route to reach. Its service container holds the share store and
/// nothing else, so there is no path from here to a saved connection or password.
///
/// On top of that it defends itself as a public-facing server would: GET only,
/// no request bodies, a Host allow-list, per-address rate limits, a small budget
/// for wrong-token guesses, capped live connections, and a pinned Production
/// environment so no stray setting can switch on a developer error page.
///
/// It does not exist until sharing is switched on, and it stops when sharing is
/// switched off, when the last share is revoked or expires, and when the window
/// closes - so a SchemaScope that is not sharing has nothing on the network.
/// </summary>
public sealed class ViewerHost(ShareStore shares, ILogger<ViewerHost> log)
{
    public const int DefaultPort = 5280;

    /// <summary>Requests per minute from one address, across everything.</summary>
    internal const int RequestsPerMinute = 300;

    /// <summary>Reference searches scan every object's text, so they get their own, tighter budget.</summary>
    internal const int SearchesPerMinute = 20;

    /// <summary>Wrong or dead tokens per minute from one address before it is told to slow down.</summary>
    internal const int MissesPerMinute = 10;

    internal const int LiveConnectionsPerAddress = 3;
    internal const int LiveConnectionsPerShare = 25;

    private const string SearchPolicy = "search";

    private static readonly TimeSpan IdleCheckEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private CancellationTokenSource? _watch;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; } = DefaultPort;
    public string? LastError { get; private set; }

    /// <summary>Every interface, both IPv4 and IPv6 - the whole point is that others can reach it.</summary>
    public Task<(bool Ok, string? Error)> StartAsync(int port, IFileProvider? ui) =>
        StartAsync(port, ui, IPAddress.IPv6Any);

    /// <summary>
    /// Starts on one address. Tests use loopback and port 0; the app always goes through the
    /// overload above. The whole start runs under one lock, so two clicks cannot race to bind.
    /// </summary>
    internal async Task<(bool Ok, string? Error)> StartAsync(int port, IFileProvider? ui, IPAddress bind)
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return (true, null);

            WebApplication? app = null;
            try
            {
                app = Build(port, ui, bind);
                await app.StartAsync();

                _app = app;
                Port = BoundPort(app) ?? port;
                IsRunning = true;
                LastError = null;

                _watch = new CancellationTokenSource();
                _ = WatchAsync(_watch.Token);
                return (true, null);
            }
            catch (Exception ex)
            {
                if (app is not null)
                {
                    try { await app.DisposeAsync(); } catch { /* it never fully started */ }
                }

                LastError = ex.Message;
                log.LogWarning(ex, "The viewer listener could not start on port {Port}", port);
                return (false, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        WebApplication? app;
        await _gate.WaitAsync();
        try
        {
            app = _app;
            _app = null;
            IsRunning = false;
            _watch?.Cancel();
            _watch?.Dispose();
            _watch = null;
        }
        finally
        {
            _gate.Release();
        }

        if (app is null) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The viewer listener did not stop cleanly");
        }
    }

    /// <summary>
    /// Revoking the last share stops the listener straight away. Expiry has no click to hang
    /// that on, so this checks once a minute and stops listening when nothing is left to serve.
    /// </summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(IdleCheckEvery);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (shares.List().Count > 0) continue;

                log.LogInformation("No shares left; the viewer listener is stopping");
                _ = StopAsync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The viewer idle check failed");
        }
    }

    private static int? BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var first = addresses?.FirstOrDefault();
        return first is not null && Uri.TryCreate(first.Replace("[::]", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    /// <summary>Every object lives in a schema, so "uses dbo" is not a dependency worth printing.</summary>
    private static readonly HashSet<string> NoiseTypes = [DbObjectType.Schema.Label()];

    /// <summary>A shared comparison, as plain searchable text - one scope per database.</summary>
    private static List<ScanScope> ScanScopes(ShareBundle bundle)
    {
        var details = bundle.Objects.Values.ToList();

        var scopes = new List<ScanScope>
        {
            new()
            {
                Id = "source",
                Label = bundle.Result.Source.Label,
                Objects = [.. details.Select(d =>
                    new ScannedObject(d.Key, d.Schema, d.Name, d.TypeLabel, d.SourceText))]
            }
        };

        for (var i = 0; i < bundle.Result.Targets.Count; i++)
        {
            var index = i;
            scopes.Add(new ScanScope
            {
                Id = index.ToString(),
                Label = bundle.Result.Targets[index].Label,
                Objects = [.. details.Select(d => new ScannedObject(
                    d.Key, d.Schema, d.Name, d.TypeLabel,
                    index < d.Targets.Count ? d.Targets[index].Text : ""))]
            });
        }

        return scopes;
    }

    internal WebApplication Build(int port, IFileProvider? ui, IPAddress bind)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Pinned: a machine-wide ASPNETCORE_ENVIRONMENT=Development would otherwise
            // put the developer exception page, stack traces and all, on the network.
            EnvironmentName = Environments.Production,
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot"
        });

        // Nothing from appsettings.json, environment variables or the command line: those
        // could add Kestrel endpoints or URLs. The one explicit Listen below is the only bind.
        builder.Configuration.Sources.Clear();

        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxConcurrentConnections = 64;
            k.Limits.MaxConcurrentUpgradedConnections = 64;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            k.Limits.MaxRequestLineSize = 8 * 1024;
            // Every route is a GET. Nobody has any business sending a body.
            k.Limits.MaxRequestBodySize = 0;

            if (bind.Equals(IPAddress.IPv6Any)) k.ListenAnyIP(port);
            else k.Listen(bind, port);
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ClientAddress(ctx), _ => PerMinute(RequestsPerMinute)));
            o.AddPolicy(SearchPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ClientAddress(ctx), _ => PerMinute(SearchesPerMinute)));
        });

        // The same instance the desktop side uses, so a link revoked in the Share
        // tab stops working here immediately. Deliberately the only app service.
        builder.Services.AddSingleton(shares);
        builder.Services.AddSingleton<MissCounter>();

        var app = builder.Build();
        if (ui is not null) app.Environment.WebRootFileProvider = ui;

        UseViewerGuards(app);
        SchemaScopeApi.UseSecurityHeaders(app);
        app.UseRateLimiter();
        app.UseStaticFiles();

        MapRoutes(app);
        return app;
    }

    private static FixedWindowRateLimiterOptions PerMinute(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    };

    /// <summary>
    /// Kestrel listens dual stack, so an IPv4 caller arrives looking like
    /// ::ffff:192.168.2.89. Nobody wants to read that in a list, and the rate
    /// limits must see one caller as one caller.
    /// </summary>
    internal static string ClientAddress(HttpContext ctx)
    {
        var address = ctx.Connection.RemoteIpAddress;
        if (address is not null && address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Runs before anything else: a request with a method we never serve, or addressed to a
    /// host name that is not this machine, gets nowhere near a route. Share pages and their
    /// data are also marked never-cache and never-index.
    /// </summary>
    private static void UseViewerGuards(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                ctx.Response.Headers.Allow = "GET, HEAD";
                return;
            }

            if (!IsAllowedHost(ctx.Request.Host.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/s") || path.StartsWithSegments("/api/view"))
            {
                if (ctx.RequestServices.GetRequiredService<MissCounter>().IsOverBudget(ClientAddress(ctx)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }

                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            }

            await next();
        });
    }

    // ---------------- Host allow-list ----------------

    private static readonly Lock HostCacheGate = new();
    private static HashSet<string>? _localAddresses;
    private static DateTime _localAddressesAtUtc = DateTime.MinValue;

    /// <summary>
    /// The names a genuine link can carry: an IP address this machine really has, loopback,
    /// or this computer's own name. A browser that has been DNS-rebound, or a proxy that
    /// forwards somebody else's traffic here, arrives with a different Host and is refused.
    /// The share token is still required on top; this is defence in depth.
    /// </summary>
    internal static bool IsAllowedHost(string host)
    {
        string? domain = null;
        try { domain = IPGlobalProperties.GetIPGlobalProperties().DomainName; } catch { /* no domain */ }
        return IsAllowedHost(host, LocalAddresses(), Environment.MachineName, domain);
    }

    internal static bool IsAllowedHost(string host, IReadOnlySet<string> localAddresses, string machineName, string? domain)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var h = host.Trim().TrimEnd('.');
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];

        if (IPAddress.TryParse(h, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) ip.ScopeId = 0;
            return localAddresses.Contains(ip.ToString());
        }

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(machineName)) return false;
        if (h.Equals(machineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (h.Equals($"{machineName}.local", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(domain) &&
               h.Equals($"{machineName}.{domain.Trim('.')}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every unicast address on every adapter, held briefly - DHCP can change them under us.</summary>
    private static HashSet<string> LocalAddresses()
    {
        lock (HostCacheGate)
        {
            if (_localAddresses is not null && DateTime.UtcNow - _localAddressesAtUtc < TimeSpan.FromSeconds(15))
                return _localAddresses;
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                    ip = new IPAddress(ip.GetAddressBytes()); // drops any scope id
                    found.Add(ip.ToString());
                }
            }
        }
        catch
        {
            // No adapter list: loopback and the machine name still work.
        }

        lock (HostCacheGate)
        {
            _localAddresses = found;
            _localAddressesAtUtc = DateTime.UtcNow;
        }
        return found;
    }

    // ---------------- routes ----------------

    private const int MaxKeyLength = 1024;
    private const int MaxSearchLength = 256;

    /// <summary>
    /// The routes this listener has, by path. <see cref="MapRoutes"/> registers exactly these
    /// and a test holds the two lists together, so adding a route means changing both.
    /// </summary>
    internal static readonly string[] Routes =
    [
        "/s/{token}",
        "/api/view/{token}",
        "/api/view/{token}/object",
        "/api/view/{token}/references",
        "/api/view/{token}/uses",
        "/api/view/{token}/live",
        "/"
    ];

    /// <summary>
    /// Six routes, every one of them a read, plus a landing page. Read the list
    /// and you have read the entire attack surface: the page, the comparison,
    /// one object's text, the two reference searches, and a heartbeat.
    /// </summary>
    private static void MapRoutes(WebApplication app)
    {
        // A wrong or dead token. Counted, so guessing is slowed to a crawl.
        static IResult Miss(HttpContext ctx)
        {
            ctx.RequestServices.GetRequiredService<MissCounter>().Record(ClientAddress(ctx));
            return Results.NotFound();
        }

        // The page itself. Any /s/<token> URL serves the app, which reads the
        // token back out of the address bar.
        // Only the shape is checked: the page itself holds no data, and a dead link should
        // still load the app so it can say "this link has expired" in words.
        app.MapGet("/s/{token}", (string token, IWebHostEnvironment env, HttpContext ctx) =>
        {
            if (!ShareStore.IsToken(token)) return Miss(ctx);

            var index = env.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists) return Results.NotFound();

            using var stream = index.CreateReadStream();
            using var reader = new StreamReader(stream);
            return Results.Content(reader.ReadToEnd(), "text/html");
        });

        // The comparison: dashboard numbers and every row. Object text is not in
        // here - that is fetched one object at a time, when somebody clicks.
        app.MapGet("/api/view/{token}", (string token, ShareStore store, HttpContext ctx) =>
        {
            var record = store.Find(token);
            var bundle = record is null ? null : store.Load(token);
            if (record is null || bundle is null) return Miss(ctx);

            store.CountView(token);

            return Results.Ok(new
            {
                token,
                sourceLabel = bundle.SourceLabel,
                hostName = bundle.HostName,
                comparedUtc = bundle.ComparedUtc,
                sharedUtc = record.CreatedUtc,
                expiresUtc = record.ExpiresUtc,
                result = bundle.Result
            });
        });

        app.MapGet("/api/view/{token}/object", (string token, string key, ShareStore store, HttpContext ctx) =>
        {
            if (store.Find(token) is null) return Miss(ctx);
            if (key.Length > MaxKeyLength) return Results.BadRequest();

            var bundle = store.Load(token);
            if (bundle is null || !bundle.Objects.TryGetValue(key, out var detail)) return Results.NotFound();

            return Results.Ok(detail);
        });

        // Reading a shared comparison raises the same question as reading your own -
        // who calls this? Both directions, still read-only, still scoped to the token.
        app.MapGet("/api/view/{token}/references", (string token, string name, ShareStore store, HttpContext ctx) =>
        {
            if (store.Find(token) is null) return Miss(ctx);

            var bundle = store.Load(token);
            if (bundle is null) return Results.NotFound();

            name = (name ?? "").Trim();
            if (name.Length < ReferenceFinder.MinimumNameLength)
                return Results.BadRequest(new { message = $"Search for at least {ReferenceFinder.MinimumNameLength} characters." });
            if (name.Length > MaxSearchLength)
                return Results.BadRequest(new { message = $"Search for at most {MaxSearchLength} characters." });

            return Results.Ok(ReferenceFinder.UsedBy(name, ScanScopes(bundle)));
        }).RequireRateLimiting(SearchPolicy);

        app.MapGet("/api/view/{token}/uses", (string token, string key, ShareStore store, HttpContext ctx) =>
        {
            if (store.Find(token) is null) return Miss(ctx);
            if (key.Length > MaxKeyLength) return Results.BadRequest();

            var bundle = store.Load(token);
            return bundle is null
                ? Results.NotFound()
                : Results.Ok(ReferenceFinder.Uses(key, ScanScopes(bundle), NoiseTypes));
        }).RequireRateLimiting(SearchPolicy);

        // An open connection for as long as somebody has the page open. It is how
        // the Share tab knows who is reading, and it clears the moment they close
        // the tab - or the moment the share is revoked or runs out.
        app.MapGet("/api/view/{token}/live", async (
            string token, ShareStore store, HttpContext ctx, CancellationToken ct) =>
        {
            if (store.Find(token) is null)
            {
                Miss(ctx);
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var ip = ClientAddress(ctx);
            if (store.ViewerCount(ipAddress: ip) >= LiveConnectionsPerAddress ||
                store.ViewerCount(token: token) >= LiveConnectionsPerShare)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, store.Revoked(token));
            var viewer = store.AddViewer(token, ip);

            try
            {
                while (!linked.Token.IsCancellationRequested && store.Find(token) is not null)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", linked.Token);
                    await ctx.Response.Body.FlushAsync(linked.Token);
                    store.TouchViewer(viewer.Id);
                    await Task.Delay(PingEvery, linked.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // they closed the tab, or the share was revoked
            }
            finally
            {
                store.RemoveViewer(viewer.Id);
            }
        });

        // Somebody trimmed the link back to the host name. Say something useful (see below)
        // rather than showing them the app with no comparison in it.
        app.MapGet("/", () => Results.Content(
            """
            <!doctype html><meta charset="utf-8"><title>SchemaScope</title>
            <style>body{font:15px/1.5 system-ui,Segoe UI,sans-serif;margin:0;display:grid;
            place-items:center;height:100vh;background:#f4f5f8;color:#14171f}
            div{max-width:30rem;text-align:center;padding:2rem}
            p{color:#5a6377}</style>
            <div><h1>SchemaScope</h1>
            <p>You need the full link to read a shared comparison.
            Ask whoever sent it to you for the whole address.</p></div>
            """, "text/html"));
    }
}

/// <summary>
/// Wrong-token answers per address, per minute. A 128-bit token cannot be guessed, but each
/// guess still costs a lookup, so an address that keeps missing is turned away early.
/// </summary>
internal sealed class MissCounter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxTracked = 10_000;

    private readonly ConcurrentDictionary<string, (DateTime Start, int Count)> _misses = new();

    public bool IsOverBudget(string address) =>
        _misses.TryGetValue(address, out var entry) &&
        DateTime.UtcNow - entry.Start < Window &&
        entry.Count >= ViewerHost.MissesPerMinute;

    public void Record(string address)
    {
        var now = DateTime.UtcNow;
        if (_misses.Count > MaxTracked)
        {
            foreach (var stale in _misses.Where(m => now - m.Value.Start >= Window).Select(m => m.Key).ToList())
                _misses.TryRemove(stale, out _);
        }

        _misses.AddOrUpdate(address,
            _ => (now, 1),
            (_, e) => now - e.Start >= Window ? (now, 1) : (e.Start, e.Count + 1));
    }
}
