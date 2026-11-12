// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Runtime;
using HarnessAgentHost.Scenarios;
using HarnessAgentHost.Tools;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

// Live Yahoo Finance client for the FinancialAnalystScenario. Cookies are enabled so the crumb dance works.
builder.Services.AddHttpClient<YahooFinanceClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new System.Net.CookieContainer(),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    });
builder.Services.AddSingleton<FinanceTools>();

// Live telemetry of this host feeds the SRE scenario; marketing tools call public research APIs.
var telemetry = new LiveTelemetry();
builder.Services.AddSingleton(telemetry);
builder.Logging.AddProvider(new RingLoggerProvider(telemetry));
builder.Services.AddHttpClient("probe");
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHttpClient<MarketingTools>();
builder.Services.AddSingleton<SreTools>();
builder.Services.AddSingleton<HarnessRuntime>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000", "http://localhost:3100", "http://127.0.0.1:3100")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Return the actual exception message and type on any uncaught error so the UI can display truth.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = ex?.Message ?? "Internal server error.",
            type = ex?.GetType().FullName,
            detail = ex?.InnerException?.Message,
            stack = ex?.StackTrace
        });
    });
});

var runtime = app.Services.GetRequiredService<HarnessRuntime>();
await runtime.InitializeAsync();

// Measures every non-streaming request; applies the injected fault (if any) so the SRE scenario sees a real incident.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    // Only the public read APIs are monitored and fault-eligible; run/mode/approval control traffic must stay reliable.
    var monitored = path is "/health" or "/api/scenarios" or "/api/artifacts" or "/api/session/trace";
    if (!monitored) { await next(); return; }

    var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("HarnessAgentHost.Http");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (telemetry.FaultActive)
    {
        await Task.Delay(telemetry.FaultLatencyMs);
        if (Random.Shared.Next(100) < telemetry.FaultErrorRatePct)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "session-store dependency timed out" });
            sw.Stop();
            log.LogError("Request {Route} failed with 503: session-store client timed out after {Ms} ms", path, Math.Round(sw.Elapsed.TotalMilliseconds));
            telemetry.RecordRequest(path, 503, sw.Elapsed.TotalMilliseconds);
            return;
        }
    }
    await next();
    sw.Stop();
    telemetry.RecordRequest(path, ctx.Response.StatusCode, sw.Elapsed.TotalMilliseconds);
    if (sw.Elapsed.TotalMilliseconds > 500)
        log.LogWarning("Slow request {Route} took {Ms} ms (budget 500 ms)", path, Math.Round(sw.Elapsed.TotalMilliseconds));
});

app.MapGet("/api/chaos", () => Results.Ok(new
{
    active = telemetry.FaultActive,
    latencyMs = telemetry.FaultLatencyMs,
    errorRatePct = telemetry.FaultErrorRatePct,
    changeId = telemetry.FaultChangeId
}));

app.MapPost("/api/chaos", (ChaosRequest req) =>
{
    if (req.Enable)
    {
        if (!telemetry.FaultActive) telemetry.InjectFault(Math.Clamp(req.LatencyMs ?? 1500, 100, 5000), Math.Clamp(req.ErrorRatePct ?? 25, 0, 90));
    }
    else telemetry.ClearFault(null, "cleared from the demo UI");
    return Results.Ok(new { active = telemetry.FaultActive, latencyMs = telemetry.FaultLatencyMs, errorRatePct = telemetry.FaultErrorRatePct, changeId = telemetry.FaultChangeId });
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    model = runtime.ModelName,
    endpoint = runtime.Endpoint,
    scenarios = runtime.Scenarios.Count
}));

app.MapGet("/api/scenarios", () => Results.Ok(new
{
    capabilities = HarnessCapabilities.All,
    scenarios = runtime.Scenarios.Select(s => new
    {
        s.Id,
        s.Title,
        s.Domain,
        s.ShortDescription,
        s.LongDescription,
        s.StarterPrompt,
        s.HighlightedCapabilities
    })
}));

app.MapPost("/api/session/start", async (StartRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ScenarioId)) return Results.BadRequest(new { error = "scenarioId is required" });
    try { return Results.Ok(await runtime.StartScenarioAsync(req.ScenarioId, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/session/status", async (CancellationToken ct) =>
{
    var s = await runtime.GetStatusAsync(ct);
    return s is null ? Results.Ok(new { active = false }) : Results.Ok(s);
});

app.MapPost("/api/session/reset", async (CancellationToken ct) =>
{
    try { await runtime.ResetSessionAsync(ct); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    var s = await runtime.GetStatusAsync(ct);
    return Results.Ok(s);
});

app.MapGet("/api/session/summary", async (CancellationToken ct) =>
{
    var s = await runtime.GetSummaryAsync(ct);
    return s is null ? Results.Ok(new { active = false }) : Results.Ok(s);
});

app.MapPost("/api/mode", async (SetModeRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Mode)) return Results.BadRequest(new { error = "mode required" });
    try
    {
        await runtime.SetModeAsync(req.Mode, ct);
        var s = await runtime.GetStatusAsync(ct);
        return Results.Ok(s);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/run", async (HttpContext ctx, RunRequest req, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Prompt))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "prompt is required" }, ct);
        return;
    }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await runtime.StreamRunAsync(req.Prompt, ctx.Response, ct);
});

app.MapPost("/api/approval", async (HttpContext ctx, ApprovalRequest req, CancellationToken ct) =>
{
    if (req.Decisions is null || req.Decisions.Count == 0)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "decisions are required" }, ct);
        return;
    }
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await runtime.StreamApprovalAsync(req.Decisions, ctx.Response, ct);
});

app.MapGet("/api/approval-mode", () => Results.Ok(new { auto = runtime.AutoApprove }));

app.MapPost("/api/approval-mode", (ApprovalModeRequest req) =>
{
    runtime.AutoApprove = req.Auto;
    return Results.Ok(new { auto = runtime.AutoApprove });
});

app.MapGet("/api/session/trace", () => Results.Ok(new { spans = runtime.GetTrace() }));

app.MapGet("/api/artifacts", () => Results.Ok(new { files = runtime.ListArtifacts() }));

app.MapGet("/api/artifacts/file", (string path) =>
{
    var full = runtime.ResolveArtifact(path);
    if (full is null) return Results.NotFound(new { error = "artifact not found" });
    return Results.File(full, "text/plain; charset=utf-8", Path.GetFileName(full));
});

var port = Environment.GetEnvironmentVariable("HARNESS_PORT") ?? "5099";
app.Run($"http://127.0.0.1:{port}");

public sealed record StartRequest(string ScenarioId);
public sealed record SetModeRequest(string Mode);
public sealed record RunRequest(string Prompt);
public sealed record ChaosRequest(bool Enable, int? LatencyMs, int? ErrorRatePct);
public sealed record ApprovalModeRequest(bool Auto);
public sealed record ApprovalRequest(List<ApprovalDecision>? Decisions);

