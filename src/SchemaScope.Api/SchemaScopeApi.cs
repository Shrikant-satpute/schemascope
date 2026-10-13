using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.FileProviders;
using SchemaScope.Api.Services;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.Core.Storage;
using SchemaScope.Scripting;
using SchemaScope.SqlServer;

namespace SchemaScope.Api;

/// <summary>
/// Builds the SchemaScope web app. Shared by the standalone API host (used
/// during development alongside the Vite dev server) and by the desktop shell,
/// which runs the very same app in-process behind a WebView2 window.
/// </summary>
public static class SchemaScopeApi
{
    /// <param name="args">Command-line arguments for the host builder.</param>
    /// <param name="desktop">Anchor the content root to the exe folder.</param>
    /// <param name="auth">
    /// The launch token every /api call must carry. Null mints a fresh one; the shell mints its own so it
    /// can hand the token to the window, and <c>--server</c> passes the fixed dev token the Vite proxy sends (Debug builds only).
    /// </param>
    /// <param name="storePath">
    /// The settings/connections database. Null = the user's own store under %LOCALAPPDATA%. Tests pass a
    /// throwaway file (<c>--server --store &lt;file&gt;</c>) so a test run never changes the user's saved
    /// connections, history or remembered folders.
    /// </param>
    public static WebApplication Build(string[] args, bool desktop = false, LocalAuth? auth = null, string? storePath = null)
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
        builder.Services.AddSingleton(_ => new SchemaScopeStore(storePath));
        builder.Services.AddSingleton<SqlServerConnectionService>();
        builder.Services.AddSingleton<CompareSessionStore>();
        builder.Services.AddSingleton<ScriptProgressStore>();
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<CompareService>();
        builder.Services.AddSingleton<ShareStore>();
        builder.Services.AddSingleton<ViewerHost>();

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

        UseSecurityHeaders(app);

        // Serve the UI out of the assembly rather than off disk. Everything
        // downstream - default files, static files, the SPA fallback - reads
        // from WebRootFileProvider, so pointing it here is all it takes.
        var embedded = TryOpenEmbeddedUi();
        if (embedded is not null) app.Environment.WebRootFileProvider = embedded;

#if DEBUG
        app.UseCors();
#endif

        UseLocalAuth(app, auth);

        app.UseDefaultFiles();
        app.UseStaticFiles();

        MapHealth(app);
        MapConnections(app);
        MapHistory(app);
        MapShares(app, embedded);
        MapProfiles(app);
        MapCompare(app, json);
        MapExport(app);
        MapScripts(app, json);

        // Anything that is not an API call falls through to the single page app.
        app.MapFallbackToFile("index.html");

        return app;
    }

    /// <summary>
    /// Nothing under /api is reachable without the launch token, and nothing
    /// is reachable at all under a host name that is not our own loopback
    /// origin. Between them these stop a local process from driving the API
    /// and a visited web page from reaching it by DNS rebinding. Every owner
    /// route - shares, scripts, references included - lives under /api, so it
    /// is covered here. The LAN viewer (<see cref="ViewerHost"/>) is a separate
    /// app with its own checks and is not affected.
    /// </summary>
    private static void UseLocalAuth(WebApplication app, LocalAuth auth)
    {
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
    }

    /// <summary>
    /// Everything the UI needs ships in the bundle. This header makes that a rule
    /// the browser enforces rather than a promise we make: the page cannot reach
    /// any outside host, so a schema can never leak out. Both listeners use it.
    /// </summary>
    internal static void UseSecurityHeaders(WebApplication app)
    {
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
    }

    /// <summary>
    /// The embedded copy of the built React app. Returns null when the assembly
    /// has no manifest - that happens before the front end has been built once,
    /// and the on-disk wwwroot is used instead.
    /// </summary>
    internal static IFileProvider? TryOpenEmbeddedUi()
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

        // Saving is held to the same rule as testing: a masked password stays with
        // the destination it was saved for, so re-pointing a saved connection at
        // another server or login means typing the password again.
        conns.MapPost("", (ConnectionSettings settings, SchemaScopeStore store) =>
        {
            if (!TryRehydrate(settings, store, out var error))
                return Results.BadRequest(new { message = error });

            return Results.Ok(store.SaveConnection(settings).WithoutSecrets());
        });

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

    /// <summary>
    /// The owner's side of sharing. Every one of these is on the loopback
    /// listener, so nothing here is reachable from the network - only the read-only
    /// routes in <see cref="ViewerHost"/> are.
    /// </summary>
    private static void MapShares(WebApplication app, IFileProvider? ui)
    {
        var provider = ui ?? app.Environment.WebRootFileProvider;

        static object Describe(ShareRecord share, ShareStore store, ViewerHost viewer)
        {
            var viewers = store.Viewers(share.Token);
            List<ShareLink> urls = viewer.IsRunning
                ? [.. ShareNetwork.Addresses().Select(a =>
                    new ShareLink($"http://{a.Value}:{viewer.Port}/s/{share.Token}", a.Kind))]
                : [];

            return new
            {
                share.Token,
                share.SourceLabel,
                share.SourceDetail,
                share.TargetLabels,
                share.ComparedUtc,
                share.CreatedUtc,
                share.ExpiresUtc,
                share.ObjectCount,
                share.TotalViews,
                urls,
                viewersNow = viewers.Count,
                viewers = viewers.Select(v => new { v.IpAddress, v.ConnectedUtc })
            };
        }

        app.MapGet("/api/shares", (ShareStore store, ViewerHost viewer) => Results.Ok(new
        {
            sharing = viewer.IsRunning,
            port = viewer.Port,
            addresses = ShareNetwork.Addresses(),
            firewallAllowed = ShareNetwork.FirewallAllows(viewer.Port),
            firewallCommand = ShareNetwork.FirewallCommand(viewer.Port),
            error = viewer.LastError,
            shares = store.List().Select(s => Describe(s, store, viewer))
        }));

        // Publish a finished run. The whole comparison, object text and all, is
        // written to disk now, so the link keeps working after the run has fallen
        // out of memory and after a restart.
        app.MapPost("/api/compare/{runId}/share", async (
            string runId,
            CompareSessionStore sessions,
            CompareService compares,
            ShareStore store,
            ViewerHost viewer) =>
        {
            var session = sessions.Get(runId);
            if (session?.Result is null) return Results.NotFound();

            var record = store.Publish(session, compares, TimeSpan.FromDays(7));

            var (ok, error) = await viewer.StartAsync(ViewerHost.DefaultPort, provider);
            if (!ok) return Results.Ok(new { share = Describe(record, store, viewer), started = false, error });

            return Results.Ok(new { share = Describe(record, store, viewer), started = true, error = (string?)null });
        });

        app.MapDelete("/api/shares/{token}", async (string token, ShareStore store, ViewerHost viewer) =>
        {
            store.Revoke(token);

            // Nothing left to serve, so stop listening. A SchemaScope with no
            // shares should have nothing at all on the network.
            if (store.List().Count == 0) await viewer.StopAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/shares/start", async (ShareStore store, ViewerHost viewer) =>
        {
            var (ok, error) = await viewer.StartAsync(ViewerHost.DefaultPort, provider);
            return Results.Ok(new { started = ok, error });
        });

        app.MapPost("/api/shares/stop", async (ViewerHost viewer) =>
        {
            await viewer.StopAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/shares/firewall", (ViewerHost viewer) =>
        {
            var (ok, message) = ShareNetwork.AllowThroughFirewall(viewer.Port);
            return Results.Ok(new { ok, message });
        });

        // The rule outlives the app, so the user must be able to take it away again.
        app.MapDelete("/api/shares/firewall", () =>
        {
            var (ok, message) = ShareNetwork.RemoveFirewallRule();
            return Results.Ok(new { ok, message });
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

        // "Where is this used?" - scanned across every database in the run, so the
        // answer can differ between source and target, which is itself the finding.
        app.MapGet("/api/compare/{runId}/references", (
            string runId, string name, CompareSessionStore sessions) =>
        {
            var session = sessions.Get(runId);
            if (session is null) return Results.NotFound();

            name = (name ?? "").Trim();
            if (name.Length < ReferenceFinder.MinimumNameLength)
                return Results.BadRequest(new { message = $"Search for at least {ReferenceFinder.MinimumNameLength} characters." });

            return Results.Ok(ReferenceFinder.UsedBy(name, ScanScopes(session)));
        });

        app.MapGet("/api/compare/{runId}/uses", (
            string runId, string key, CompareSessionStore sessions) =>
        {
            var session = sessions.Get(runId);
            if (session is null) return Results.NotFound();

            return Results.Ok(ReferenceFinder.Uses(key, ScanScopes(session), NoiseTypes));
        });
    }

    /// <summary>Every object lives in a schema, so "uses dbo" is not a dependency worth printing.</summary>
    private static readonly HashSet<string> NoiseTypes = [DbObjectType.Schema.Label()];

    /// <summary>Every database in a run, as plain searchable text.</summary>
    private static List<ScanScope> ScanScopes(CompareSession session)
    {
        static IReadOnlyList<ScannedObject> Read(SchemaSnapshot? snapshot) =>
            snapshot is null
                ? []
                : [.. snapshot.Objects.Select(o =>
                    new ScannedObject(o.Key, o.Schema, o.Name, o.Type.Label(), o.DisplayText))];

        var scopes = new List<ScanScope>
        {
            new()
            {
                Id = "source",
                Label = session.Result?.Source.Label ?? "Source",
                Objects = Read(session.Source)
            }
        };

        for (var i = 0; i < session.Targets.Count; i++)
        {
            scopes.Add(new ScanScope
            {
                Id = i.ToString(),
                Label = session.Result is not null && i < session.Result.Targets.Count
                    ? session.Result.Targets[i].Label
                    : $"Target {i + 1}",
                Objects = Read(session.Targets[i])
            });
        }

        return scopes;
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

    private const string LastScriptsFolderKey = "scripts.lastFolder";

    /// <summary>
    /// "Create scripts": .sql files that would make one target of a finished compare look like
    /// its source. Owner app only - these routes sit on the loopback listener, and the LAN
    /// viewer (<see cref="ViewerHost"/>) is a separate app with its own router and none of the
    /// services used here, so a shared link cannot reach them. The generator only reads the two
    /// databases (catalog SELECTs and COUNT probes) and writes files into the chosen folder.
    /// </summary>
    private static void MapScripts(WebApplication app, JsonSerializerOptions json)
    {
        // Prefills the dialog, so a user who always writes to the same place picks it once.
        app.MapGet("/api/scripts/last-folder", (SchemaScopeStore store) =>
            Results.Ok(new { path = ReadSetting(store, LastScriptsFolderKey) }));

        // The dialog polls this while its POST below is still running. 404 once the run has ended.
        app.MapGet("/api/scripts/progress/{id}", (string id, ScriptProgressStore progress) =>
            progress.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());

        app.MapPost("/api/compare/{runId}/scripts", async (
            string runId,
            HttpRequest http,
            CompareSessionStore sessions,
            SchemaScopeStore store,
            ScriptProgressStore progressStore,
            ILoggerFactory loggers,
            CancellationToken ct) => // bound to HttpContext.RequestAborted: closing the dialog stops the reads
        {
            // The body is read here rather than bound by the framework: a bad body (an unknown section
            // name, a string where a number belongs) would otherwise be an empty 400 the dialog cannot show.
            // Only JSON is accepted, so a plain cross-site form post still gets 415, as before.
            if (!http.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            ScriptRequest? request;
            try
            {
                request = await http.ReadFromJsonAsync<ScriptRequest>(json, ct);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { message = BadBodyMessage(ex) });
            }

            var session = sessions.Get(runId);
            if (session is null)
                return Results.NotFound(new { message = "That comparison is no longer in memory - run the compare again." });

            var problem = CheckScriptRequest(session, request);
            if (problem is not null) return Results.BadRequest(new { message = problem });

            // CheckScriptRequest has proved all of these are present and in range.
            var result = session.Result!;
            var index = request!.TargetIndex!.Value;
            var source = Endpoint(session.SourceConnection!, result.Source.Label);
            var target = Endpoint(session.TargetConnections[index], result.Targets[index].Label);
            var options = Normalise(request.Options);

            ScriptOutcome outcome;
            using var progress = request.ProgressId is { } progressId ? progressStore.Track(progressId) : null;
            try
            {
                outcome = await new ScriptGenerator().GenerateAsync(
                    source, target, options, FormattingCandidates(result, index), ct, progress, DifferentCandidates(result, index));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty; // the browser has gone; nobody is waiting for an answer
            }
            catch (Exception ex)
            {
                // The generator turns its own failures into an outcome, so this is a bug path.
                // Log the scrubbed text only, never the exception: rule 5 covers logs too.
                var message = Scrub(ex.Message, source, target);
                loggers.CreateLogger("SchemaScope.Scripts").LogWarning("Script generation failed: {Message}", message);
                return Results.BadRequest(new { message = $"Script generation failed: {message}" });
            }

            // The generator turns a cancellation into an outcome (and marks the partial folder), so this is the
            // usual way a stopped run ends: nobody is waiting, and a stopped run must not become "last folder".
            if (ct.IsCancellationRequested) return Results.Empty;

            // A run folder was created, so the parent folder is usable: offer it next time.
            if (outcome.Path is not null) WriteSetting(store, LastScriptsFolderKey, options.OutputFolder);

            ScrubOutcome(outcome, source, target);
            return Results.Ok(outcome);
        });
    }

    /// <summary>
    /// The 400 rules of DESIGN §6, in the order a user would hit them. Null means the request
    /// can go ahead. Messages say what to do next, because they are shown as they are.
    /// </summary>
    private static string? CheckScriptRequest(CompareSession session, ScriptRequest? request)
    {
        if (session.Status != "done" || session.Result is null)
            return session.Status == "running"
                ? "That comparison is still running - wait for it to finish."
                : "That comparison did not finish - run the compare again.";

        if (session.SourceConnection is null)
            return "That comparison no longer has its connection details - run the compare again.";

        if (request is null) return "Send the target and the script options.";

        if (request.ProgressId is { } progressId && !ScriptProgressStore.IsValidId(progressId))
            return "The progress id must be 8 to 64 letters, digits or dashes.";

        if (request.TargetIndex is not { } index)
            return "Say which target to script: pick a target tile and try again.";
        if (index < 0)
            return "There is no target at that position - pick a target tile and try again.";
        if (index >= session.Result.Targets.Count || index >= session.TargetConnections.Count)
            return $"This comparison has no target number {index + 1}.";

        var summary = session.Result.Targets[index];
        if (summary.Failed)
            return $"{summary.Label} could not be read in this comparison, so there is nothing to script against. " +
                   "Fix the connection and run the compare again.";

        var o = request.Options;
        if (o is null) return "Choose a folder to write the scripts into.";

        var folderProblem = CheckScriptFolders(o);
        if (folderProblem is not null) return folderProblem;

        if (o.Sections is null || o.Sections.Count == 0)
            return "Tick at least one section.";
        // JSON may also carry a section as a number; only the named sections exist.
        var unknown = o.Sections.Where(s => !Enum.IsDefined(s)).ToList();
        if (unknown.Count > 0)
            return $"Unknown section {(int)unknown[0]}. Known sections: {KnownSections}.";

        return null;
    }

    /// <summary>
    /// Where the scripts may be written. The run writes .sql files and a RUN.ps1, so the
    /// folder gets the same refusal list as the folder export (no Windows, System, Program
    /// Files or Startup), and network paths are refused outright: a \\host\share path would
    /// both leave the machine and make Windows offer the user's NTLM credentials to that host
    /// the moment it is probed. The quarantine folder is only read, but it is probed the same
    /// way, so it gets the same rules.
    /// </summary>
    internal static string? CheckScriptFolders(ScriptOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.OutputFolder))
            return "Choose a folder to write the scripts into.";

        var output = o.OutputFolder.Trim();
        if (!Path.IsPathFullyQualified(output))
            return "Choose a full output folder path, e.g. D:\\Scripts.";
        if (!ExportService.IsWritableLocation(output, out _, out var refusal))
            return refusal;

        if (!string.IsNullOrWhiteSpace(o.QuarantineFolder))
        {
            var quarantine = o.QuarantineFolder.Trim();
            if (!Path.IsPathFullyQualified(quarantine))
                return "The quarantine folder needs a full path, e.g. D:\\Quarantine.";
            if (!ExportService.IsWritableLocation(quarantine, out _, out var quarantineRefusal))
                return quarantineRefusal;
        }

        return null;
    }

    private static readonly string KnownSections =
        string.Join(", ", Enum.GetNames<ScriptSection>().Select(JsonNamingPolicy.CamelCase.ConvertName));

    /// <summary>
    /// A readable 400 for a body that is not a script request. System.Text.Json says where it stopped
    /// (<c>$.options.sections[0]</c>), not in words a user can act on, so the common cases are named.
    /// </summary>
    private static string BadBodyMessage(JsonException ex)
    {
        var path = ex.Path ?? "";
        if (path.Contains("sections", StringComparison.OrdinalIgnoreCase))
            return $"Unknown section in the request. Known sections: {KnownSections}.";
        return path.Length > 1
            ? $"The request could not be read: the value at {path} has the wrong form."
            : "The request could not be read: send the target and the script options as JSON.";
    }

    /// <summary>
    /// Tidies what a text box sends: stray spaces around paths, empty patterns from a trailing
    /// comma, and limits that make no sense (a timeout of 0 would mean "wait forever" to SqlClient).
    /// </summary>
    private static ScriptOptions Normalise(ScriptOptions o)
    {
        var defaults = new ScriptOptions();
        o.OutputFolder = o.OutputFolder.Trim();
        o.QuarantineFolder = string.IsNullOrWhiteSpace(o.QuarantineFolder) ? null : o.QuarantineFolder.Trim();
        o.ExcludePatterns = [.. (o.ExcludePatterns ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0)];
        o.Sections = [.. o.Sections.Distinct()];
        if (o.LargeTableRows <= 0) o.LargeTableRows = defaults.LargeTableRows;
        if (o.ProbeTimeoutSeconds <= 0) o.ProbeTimeoutSeconds = defaults.ProbeTimeoutSeconds;
        return o;
    }

    /// <summary>
    /// The label is the one the dashboard shows for that database, so the run folder and the
    /// README use the same name the user clicked on.
    /// </summary>
    /// <remarks>
    /// Server and database come from the connection string that was really used, not from the form's
    /// Server/Database boxes: with a raw connection string those boxes are ignored when connecting and may
    /// hold stale text (or nothing, which would become localhost), and RUN.ps1 and the README must name the
    /// server that was compared.
    /// </remarks>
    private static ScriptEndpoint Endpoint(SchemaSource s, string? label)
    {
        string server = s.Server, database = s.Database;
        try
        {
            var b = new SqlConnectionStringBuilder(s.ConnectionString);
            if (!string.IsNullOrWhiteSpace(b.DataSource)) server = b.DataSource;
            if (!string.IsNullOrWhiteSpace(b.InitialCatalog)) database = b.InitialCatalog;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            // keep the form's values; the generator reports a connection failure on its own
        }
        return new(string.IsNullOrWhiteSpace(label) ? s.DisplayName : label, server, database, s.ConnectionString);
    }

    /// <summary>Modules the compare marked Different for this target, with its line counts: 10_different_modules' candidates.</summary>
    private static Dictionary<string, LineChange> DifferentCandidates(CompareResult result, int targetIndex)
    {
        var names = new Dictionary<string, LineChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            if (row.Type.IsModule() &&
                targetIndex < row.Cells.Count &&
                row.Cells[targetIndex].Status == ObjectStatus.Different)
            {
                names[row.FullName] = new LineChange(row.Cells[targetIndex].AddedLines, row.Cells[targetIndex].RemovedLines);
            }
        }
        return names;
    }

    /// <summary>
    /// Modules the compare already judged "formatting only" for this target - the only ones
    /// folder 08 may consider re-creating from the source text.
    /// </summary>
    private static HashSet<string> FormattingCandidates(CompareResult result, int targetIndex)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            if (row.Type.IsModule() &&
                targetIndex < row.Cells.Count &&
                row.Cells[targetIndex].Status == ObjectStatus.FormattingOnly)
            {
                names.Add(row.FullName);
            }
        }
        return names;
    }

    /// <summary>
    /// Last line of defence for "connection strings and passwords never appear": a driver or
    /// IO message that echoed one back is masked before anything reaches the browser.
    /// </summary>
    private static void ScrubOutcome(ScriptOutcome outcome, params ScriptEndpoint[] endpoints)
    {
        var secrets = SecretScrubber.Secrets(endpoints.Select(e => e.ConnectionString));
        if (secrets.Count == 0) return;

        string S(string text) => SecretScrubber.Scrub(text, secrets);

        if (outcome.Message is not null) outcome.Message = S(outcome.Message);
        outcome.Warnings = [.. outcome.Warnings.Select(S)];
        outcome.Validation.Problems = [.. outcome.Validation.Problems.Select(S)];
        foreach (var item in outcome.HeldBack)
        {
            item.Object = S(item.Object);
            item.Reason = S(item.Reason);
        }
        foreach (var folder in outcome.Folders) folder.Notes = [.. folder.Notes.Select(S)];
    }

    private static string Scrub(string text, params ScriptEndpoint[] endpoints) =>
        SecretScrubber.Scrub(text, SecretScrubber.Secrets(endpoints.Select(e => e.ConnectionString)));

    /// <summary>Settings are a convenience; a locked or missing store must not fail the request.</summary>
    private static string? ReadSetting(SchemaScopeStore store, string key)
    {
        try { return store.GetSetting(key); }
        catch (Exception) { return null; }
    }

    private static void WriteSetting(SchemaScopeStore store, string key, string value)
    {
        try { store.SetSetting(key, value); }
        catch (Exception) { /* bookkeeping only - the scripts were written either way */ }
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

        // Only a password login has a password to put back. Windows and the
        // interactive Azure modes never stored one, so an edited server there
        // releases nothing and needs nothing typed.
        var needsPassword = incoming.UsesPassword &&
                            (string.IsNullOrEmpty(incoming.Password) || incoming.Password == Masked);
        var needsRaw = incoming.UseRawConnectionString &&
                       (string.IsNullOrEmpty(incoming.RawConnectionString) ||
                        incoming.RawConnectionString.Contains(Masked));

        if (!needsPassword && !needsRaw) return true;
        if (string.IsNullOrWhiteSpace(incoming.Id)) return true;

        var saved = store.GetConnection(incoming.Id);
        if (saved is null) return true;

        if (!ConnectionSettings.SameDestination(incoming, saved))
        {
            error = "The server or login was changed, so the saved password no longer applies. " +
                    "Type the password again.";
            return false;
        }

        if (needsPassword) incoming.Password = saved.Password;
        if (needsRaw) incoming.RawConnectionString = saved.RawConnectionString;
        return true;
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
