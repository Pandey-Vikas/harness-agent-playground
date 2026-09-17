// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Runtime;
using HarnessAgentHost.Scenarios;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
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

var port = Environment.GetEnvironmentVariable("HARNESS_PORT") ?? "5099";
app.Run($"http://127.0.0.1:{port}");

public sealed record StartRequest(string ScenarioId);
public sealed record SetModeRequest(string Mode);
public sealed record RunRequest(string Prompt);

