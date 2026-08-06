using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using SchemaScope.Api.Services;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.SqlServer;

namespace SchemaScope.Api;

/// <summary>
/// Builds the SchemaScope web app. Shared by the standalone API host (used
/// during development alongside the Vite dev server) and by the desktop shell,
/// which runs the very same app in-process behind a WebView2 window.
/// </summary>
public static class SchemaScopeApi
{
    public static WebApplication Build(string[] args, bool desktop = false, LocalAuth? auth = null)
    {
        auth ??= LocalAuth.Mint();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // In the packaged desktop app the working directory is wherever the
            // user launched from, so anchor to the exe folder instead.
            ContentRootPath = desktop ? AppContext.BaseDirectory : null,
            WebRootPath = "wwwroot"
        });

        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(_ => new SchemaScopeStore());
        builder.Services.AddSingleton<SqlServerConnectionService>();
        builder.Services.AddSingleton<CompareSessionStore>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<CompareService>();

#if DEBUG
        // The UI is served from the same origin in the packaged app, so this is
        // only ever needed while the Vite dev server is driving the API. It is
        // compiled out of Release builds on purpose: shipping it would let
        // anything running on port 5173 - the Vite default, on a machine owned
        // by a developer - drive this API and read every saved connection.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()));
#endif

        var app = builder.Build();

        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // Everything the UI needs ships in the bundle. This header makes that a
        // rule the browser enforces rather than a promise we make: the page
        // cannot reach any outside host, so a schema can never leak out.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-eval' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "font-src 'self' data:; " +
                "img-src 'self' data: blob:; " +
                "worker-src 'self' blob:; " +
                "connect-src 'self'; " +
                "form-action 'none'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

        // Serve the UI out of the assembly rather than off disk. Everything
        // downstream - default files, static files, the SPA fallback - reads
        // from WebRootFileProvider, so pointing it here is all it takes.
        var embedded = TryOpenEmbeddedUi();
        if (embedded is not null) app.Environment.WebRootFileProvider = embedded;

#if DEBUG
        app.UseCors();
#endif

        // Nothing under /api is reachable without the launch token, and nothing
        // is reachable at all under a host name that is not our own loopback
        // origin. Between them these stop a local process from driving the API
        // and a visited web page from reaching it by DNS rebinding.
        app.Use(async (ctx, next) =>
        {
            if (!LocalAuth.IsLoopbackHost(ctx.Request.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            if (ctx.Request.Path.StartsWithSegments("/api") && !auth.IsAuthorised(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapHealth(app);
        MapConnections(app);
        MapHistory(app);
        MapProfiles(app);
        MapCompare(app, json);
        MapExport(app);

        // Anything that is not an API call falls through to the single page app.
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// The embedded copy of the built React app. Returns null when the assembly
    /// has no manifest - that happens before the front end has been built once,
    /// and the on-disk wwwroot is used instead.
    /// </summary>
    private static IFileProvider? TryOpenEmbeddedUi()
    {
        try
        {
            var provider = new ManifestEmbeddedFileProvider(typeof(SchemaScopeApi).Assembly, "wwwroot");
            return provider.GetFileInfo("index.html").Exists ? provider : null;
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------

    private static void MapHealth(WebApplication app)
    {
        app.MapGet("/api/health", (SchemaScopeStore store) => Results.Ok(new
        {
            ok = true,
            version = typeof(SchemaScopeApi).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            readOnly = true,
            storePath = store.DatabasePath,
            secretsProtected = SecretProtector.IsSupported
        }));

        app.MapGet("/api/options/defaults", () => Results.Ok(new CompareOptions()));

        app.MapGet("/api/object-types", () => Results.Ok(
            Enum.GetValues<DbObjectType>().Select(t => new { value = t.ToString(), label = t.Label() })));
    }

    private static void MapConnections(WebApplication app)
    {
        var conns = app.MapGroup("/api/connections");

        conns.MapGet("", (SchemaScopeStore store) =>
            Results.Ok(store.GetConnections().Select(c => c.WithoutSecrets())));

        conns.MapPost("", (ConnectionSettings settings, SchemaScopeStore store) =>
            Results.Ok(store.SaveConnection(settings).WithoutSecrets()));

        conns.MapDelete("/{id}", (string id, SchemaScopeStore store) =>
        {
            store.DeleteConnection(id);
            return Results.NoContent();
        });

        conns.MapPost("/reorder", (List<string> ids, SchemaScopeStore store) =>
        {
            store.ReorderConnections(ids);
            return Results.NoContent();
        });

        conns.MapPost("/test", async (
            ConnectionSettings settings,
            SchemaScopeStore store,
            SqlServerConnectionService svc,
            CancellationToken ct) =>
        {
            if (!TryRehydrate(settings, store, out var error))
                return Results.BadRequest(new { message = error });

            var cs = svc.BuildConnectionString(settings);
            return Results.Ok(await svc.TestAsync(cs, ct));
        });

        conns.MapPost("/databases", async (
            ConnectionSettings settings,
            SchemaScopeStore store,
            SqlServerConnectionService svc,
            CancellationToken ct) =>
        {
            if (!TryRehydrate(settings, store, out var error))
                return Results.BadRequest(new { message = error });

            try
            {
                var cs = svc.BuildConnectionString(settings, "master");
                return Results.Ok(await svc.ListDatabasesAsync(cs, ct));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }

    /// <summary>
    /// Past comparisons. Read, forget one, or forget the lot - there is nothing
    /// to create here, because every finished compare files itself.
    /// </summary>
    private static void MapHistory(WebApplication app)
    {
        var history = app.MapGroup("/api/history");

        history.MapGet("", (SchemaScopeStore store) => Results.Ok(store.GetHistory()));

        history.MapDelete("/{id}", (string id, SchemaScopeStore store) =>
        {
            store.DeleteHistory(id);
            return Results.NoContent();
        });

        history.MapDelete("", (SchemaScopeStore store) =>
        {
            store.ClearHistory();
            return Results.NoContent();
        });
    }

    private static void MapProfiles(WebApplication app)
    {
        var profiles = app.MapGroup("/api/profiles");

        profiles.MapGet("", (SchemaScopeStore store) => Results.Ok(store.GetProfiles()));
        profiles.MapPost("", (CompareProfile p, SchemaScopeStore store) => Results.Ok(store.SaveProfile(p)));
        profiles.MapDelete("/{id}", (string id, SchemaScopeStore store) =>
        {
            store.DeleteProfile(id);
            return Results.NoContent();
        });
    }

    private static void MapCompare(WebApplication app, JsonSerializerOptions json)
    {
        app.MapPost("/api/compare", (CompareRequest request, CompareService svc) =>
        {
            if (request.Targets.Count == 0)
                return Results.BadRequest(new { message = "Add at least one target database to compare against." });

            var session = svc.Start(request);
            return Results.Ok(new CompareStarted { RunId = session.Id });
        });

        app.MapGet("/api/compare/{runId}", (string runId, CompareSessionStore sessions) =>
        {
            var session = sessions.Get(runId);
            if (session is null) return Results.NotFound();

            return Results.Ok(new RunSummary
            {
                RunId = session.Id,
                Status = session.Status,
                StartedUtc = session.StartedUtc,
                Error = session.Error,
                Hint = session.Hint,
                Result = session.Result
            });
        });

        // Server sent events. The browser gets each stage as it happens instead
        // of staring at a spinner until the whole compare finishes.
        app.MapGet("/api/compare/{runId}/events", async (
            string runId, CompareSessionStore sessions, HttpContext ctx, CancellationToken ct) =>
        {
            var session = sessions.Get(runId);
            if (session is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = session.Subscribe();

            try
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(evt, json)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }

                var final = JsonSerializer.Serialize(new ProgressEvent
                {
                    Type = session.Status == "done" ? "done" : "error",
                    Stage = session.Status,
                    Percent = 100,
                    Detail = session.Error
                }, json);
                await ctx.Response.WriteAsync($"data: {final}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // browser navigated away
            }
        });

        app.MapPost("/api/compare/{runId}/cancel", (string runId, CompareSessionStore sessions) =>
        {
            var session = sessions.Get(runId);
            if (session is null) return Results.NotFound();
            session.Cancellation.Cancel();
            return Results.NoContent();
        });

        app.MapDelete("/api/compare/{runId}", (string runId, CompareSessionStore sessions) =>
            sessions.Remove(runId) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/compare/{runId}/object", (
            string runId, string key, CompareSessionStore sessions, CompareService svc) =>
        {
            var session = sessions.Get(runId);
            if (session is null) return Results.NotFound();

            var detail = svc.GetObject(session, key);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
    }

    private static void MapExport(WebApplication app)
    {
        app.MapPost("/api/compare/{runId}/export", (
            string runId, ExportRequest request, CompareSessionStore sessions, ExportService export) =>
        {
            var session = sessions.Get(runId);
            if (session?.Result is null) return Results.NotFound();

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var baseName = $"schemascope-{Sanitise(session.Result.Source.Database)}-{stamp}";

            switch (request.Format.ToLowerInvariant())
            {
                case "html":
                    return Results.File(
                        Encoding.UTF8.GetBytes(export.BuildHtml(session.Result, request)),
                        "text/html", $"{baseName}.html");

                case "csv":
                    return Results.File(
                        Encoding.UTF8.GetBytes(export.BuildCsv(session.Result, request)),
                        "text/csv", $"{baseName}.csv");

                case "md":
                case "markdown":
                    return Results.File(
                        Encoding.UTF8.GetBytes(export.BuildMarkdown(session.Result, request)),
                        "text/markdown", $"{baseName}.md");

                case "sql":
                    return Results.Ok(export.WriteSqlFiles(session, request));

                default:
                    return Results.BadRequest(new { message = $"Unknown export format '{request.Format}'." });
            }
        });
    }

    // -----------------------------------------------------------------

    private const string Masked = "********";

    /// <summary>
    /// A saved connection comes back from the UI with "********" instead of the
    /// real password. Swap the stored secret in before using it.
    ///
    /// The secret is only ever released for the exact destination it was saved
    /// against. If the caller changed the server, the login or the auth mode,
    /// the password has to be typed again - otherwise anything able to reach
    /// this API could name a saved connection, point it at a host of its own
    /// choosing, and harvest the password from the other end.
    /// </summary>
    internal static bool TryRehydrate(ConnectionSettings incoming, SchemaScopeStore store, out string? error)
    {
        error = null;

        var needsPassword = string.IsNullOrEmpty(incoming.Password) || incoming.Password == Masked;
        var needsRaw = incoming.UseRawConnectionString &&
                       (string.IsNullOrEmpty(incoming.RawConnectionString) ||
                        incoming.RawConnectionString.Contains(Masked));

        if (!needsPassword && !needsRaw) return true;
        if (string.IsNullOrWhiteSpace(incoming.Id)) return true;

        var saved = store.GetConnection(incoming.Id);
        if (saved is null) return true;

        if (!SameDestination(incoming, saved))
        {
            error = "The server or login was changed, so the saved password no longer applies. " +
                    "Type the password again to test this connection.";
            return false;
        }

        if (needsPassword) incoming.Password = saved.Password;
        if (needsRaw) incoming.RawConnectionString = saved.RawConnectionString;
        return true;
    }

    /// <summary>
    /// Whether a request still points at the same place the secret was stored
    /// for. The database may differ - one server is often compared against
    /// itself - because a database name cannot redirect a credential elsewhere.
    /// </summary>
    private static bool SameDestination(ConnectionSettings incoming, ConnectionSettings saved) =>
        string.Equals(incoming.Server, saved.Server, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(incoming.UserName ?? "", saved.UserName ?? "", StringComparison.OrdinalIgnoreCase) &&
        incoming.Auth == saved.Auth &&
        incoming.UseRawConnectionString == saved.UseRawConnectionString;

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
