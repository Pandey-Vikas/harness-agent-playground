// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using HarnessAgentHost.Runtime;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Incident-response tools over this host's own live telemetry. Metrics, logs, health probes and change history are
/// measured from the running system. The two mutating tools are approval-gated and have real, bounded effects.
/// </summary>
public sealed class SreTools
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private static readonly string[] s_services = ["harness-host", "ui-proxy", "foundry-model", "yahoo-finance"];

    private readonly LiveTelemetry _telemetry;
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _http;
    private readonly YahooFinanceClient _yahoo;

    public SreTools(LiveTelemetry telemetry, IServiceProvider services, IHttpClientFactory http, YahooFinanceClient yahoo)
    {
        this._telemetry = telemetry;
        this._services = services;
        this._http = http;
        this._yahoo = yahoo;
    }

    public IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(this.QueryMetrics, new AIFunctionFactoryOptions { Name = "query_metrics" }),
        AIFunctionFactory.Create(this.QueryLogs, new AIFunctionFactoryOptions { Name = "query_logs" }),
        AIFunctionFactory.Create(this.ListRecentChanges, new AIFunctionFactoryOptions { Name = "list_recent_deploys" }),
        AIFunctionFactory.Create(this.CheckServiceHealthAsync, new AIFunctionFactoryOptions { Name = "check_service_health" }),
        AIFunctionFactory.Create(this.ListDependenciesAsync, new AIFunctionFactoryOptions { Name = "list_dependencies" }),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(this.RestartServiceAsync, new AIFunctionFactoryOptions { Name = "restart_service" })),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(this.RollbackDeploymentAsync, new AIFunctionFactoryOptions { Name = "rollback_deployment" })),
    ];

    [Description("Per-minute time series MEASURED from the running system. Services and metrics: harness-host (latency_p95_ms, latency_avg_ms, error_rate_pct, request_count, cpu_pct, memory_mb), " +
                 "foundry-model (model_call_latency_p95_ms, model_call_count, probe_latency_ms), ui-proxy and yahoo-finance (probe_latency_ms). " +
                 "The result includes baselineAvg (older half of the window), recentAvg (newer half) and changePct.")]
    public string QueryMetrics(
        [Description("Service name: 'harness-host', 'ui-proxy', 'foundry-model' or 'yahoo-finance'.")] string service,
        [Description("Metric name, for example 'latency_p95_ms' or 'error_rate_pct'.")] string metric,
        [Description("Window in minutes ending now (default 30).")] int windowMinutes = 30)
    {
        var spans = this._services.GetRequiredService<HarnessRuntime>().GetTrace();
        var result = this._telemetry.QueryMetric(service, metric, windowMinutes, spans);
        return result is null
            ? Json(new { error = $"Unknown service/metric pair '{service}'/'{metric}'.", services = s_services })
            : Json(result);
    }

    [Description("Recent log lines captured from the host (Information and above), newest first.")]
    public string QueryLogs(
        [Description("Service name; filters by text when it is not 'harness-host'.")] string service,
        [Description("Minimum level: 'error' | 'warn' | 'info' (default 'error').")] string level = "error",
        [Description("Maximum lines to return (default 8).")] int limit = 8)
    {
        var contains = string.Equals(service, "harness-host", StringComparison.OrdinalIgnoreCase) ? null : service;
        var lines = this._telemetry.Logs(level, Math.Clamp(limit, 1, 50), contains)
            .Select(l => new { minutesAgo = Math.Round((DateTime.UtcNow - l.Utc).TotalMinutes, 1), level = l.Level, source = ShortName(l.Category), message = Trim(l.Message, 300) });
        return Json(new { service, level, lines, source = "ILogger ring buffer of the running host" });
    }

    [Description("Recent changes to the system, newest first: git commits of this repository and configuration changes. Use the id of a change with rollback_deployment.")]
    public string ListRecentChanges(
        [Description("Service name (informational).")] string service,
        [Description("Max changes to return (default 6).")] int limit = 6)
    {
        var root = FindRepoRoot();
        var all = this._telemetry.ConfigChanges().Concat(LiveTelemetry.GitCommits(root, Math.Clamp(limit, 1, 20)))
            .OrderByDescending(c => c.Utc).Take(Math.Clamp(limit, 1, 20))
            .Select(c => new
            {
                deployId = c.Id,
                kind = c.Kind,
                author = c.Author,
                summary = Trim(c.Summary, 200),
                utc = c.Utc.ToString("O"),
                minutesAgo = Math.Round((DateTime.UtcNow - c.Utc).TotalMinutes, 1)
            });
        return Json(new { service, changes = all, source = "git log of this repository + configuration change log" });
    }

    [Description("Probes a service right now and compares recent latency with its baseline. Returns state up | degraded | down with the measurements behind it.")]
    public async Task<string> CheckServiceHealthAsync([Description("Service name.")] string service, CancellationToken ct = default)
    {
        var probe = await this.ProbeNowAsync(service, ct);
        var spans = this._services.GetRequiredService<HarnessRuntime>().GetTrace();
        var m = service.Equals("harness-host", StringComparison.OrdinalIgnoreCase)
            ? this._telemetry.QueryMetric(service, "latency_p95_ms", 10, spans)
            : null;

        string state;
        string reason;
        if (!probe.Healthy) { state = "down"; reason = $"Live probe failed: {probe.Detail}"; }
        else if (m is { ChangePct: > 100 } || (m?.RecentAvg ?? 0) > 500) { state = "degraded"; reason = $"p95 latency {m!.RecentAvg} ms vs {m.BaselineAvg} ms baseline ({m.ChangePct}% change)."; }
        else { state = "up"; reason = "Live probe succeeded and latency is within budget (500 ms)."; }

        return Json(new { service, state, reason, probe = new { probe.Healthy, probe.Ms, probe.Detail }, latencyP95Recent = m?.RecentAvg, latencyP95Baseline = m?.BaselineAvg, thresholds = "degraded when p95 > 500 ms or more than doubles versus baseline" });
    }

    [Description("Lists what a service depends on and probes each dependency live.")]
    public async Task<string> ListDependenciesAsync([Description("Service name.")] string service, CancellationToken ct = default)
    {
        var map = service.ToLowerInvariant() switch
        {
            "harness-host" => new[] { ("foundry-model", "https-api"), ("yahoo-finance", "https-api") },
            "ui-proxy" => new[] { ("harness-host", "http-api") },
            _ => Array.Empty<(string, string)>()
        };
        var deps = new List<object>();
        foreach (var (name, type) in map)
        {
            var p = await this.ProbeNowAsync(name, ct);
            deps.Add(new { name, type, healthy = p.Healthy, latencyMs = Math.Round(p.Ms), detail = p.Detail });
        }
        return Json(new { service, dependencies = deps, source = "live HTTP probes" });
    }

    [Description("Recycle the host's cached upstream sessions (Yahoo Finance crumb) and report a fresh latency probe. Requires human approval.")]
    public async Task<string> RestartServiceAsync([Description("Service name; only 'harness-host' can be recycled here.")] string service, CancellationToken ct = default)
    {
        if (!service.Equals("harness-host", StringComparison.OrdinalIgnoreCase))
            return Json(new { action = "restart_service", service, status = "refused", reason = "Only harness-host can be recycled from this tool; the others are external." });
        var before = await this.ProbeNowAsync("harness-host", ct);
        this._yahoo.ResetSession();
        GC.Collect();
        var after = await this.ProbeNowAsync("harness-host", ct);
        return Json(new { action = "restart_service", service, status = "executed", effect = "Cached upstream sessions dropped and garbage collected; process was not restarted.", latencyMsBefore = Math.Round(before.Ms), latencyMsAfter = Math.Round(after.Ms) });
    }

    [Description("Roll back a change by its id from list_recent_deploys. Configuration changes are really reverted; git commits are read-only and refused. Requires human approval.")]
    public async Task<string> RollbackDeploymentAsync(
        [Description("Service name.")] string service,
        [Description("Change id from list_recent_deploys, for example 'chg-1a2b3c'.")] string toDeployId,
        CancellationToken ct = default)
    {
        if (toDeployId.StartsWith("git-", StringComparison.OrdinalIgnoreCase))
            return Json(new { action = "rollback_deployment", service, toDeployId, status = "refused", reason = "Git history is read-only from this tool; only configuration changes can be reverted." });

        var before = await this.ProbeNowAsync("harness-host", ct);
        var cleared = this._telemetry.ClearFault(toDeployId, "reverted by incident commander after human approval");
        if (!cleared)
            return Json(new { action = "rollback_deployment", service, toDeployId, status = "no-op", reason = "No active configuration change with that id." });
        var after = await this.ProbeNowAsync("harness-host", ct);
        return Json(new { action = "rollback_deployment", service, toDeployId, status = "executed", effect = "Configuration change reverted.", latencyMsBefore = Math.Round(before.Ms), latencyMsAfter = Math.Round(after.Ms) });
    }

    private async Task<ProbeSample> ProbeNowAsync(string service, CancellationToken ct)
    {
        var host = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_PORT"), out var hp) ? hp : 5099;
        var ui = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var up) ? up : 3000;
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
        string? url = service.ToLowerInvariant() switch
        {
            "harness-host" => $"http://127.0.0.1:{host}/health",
            "ui-proxy" => $"http://127.0.0.1:{ui}/api/config",
            "foundry-model" when Uri.TryCreate(endpoint, UriKind.Absolute, out var u) => $"{u.Scheme}://{u.Host}/",
            "yahoo-finance" => "https://query1.finance.yahoo.com/v8/finance/chart/MSFT?range=1d&interval=1d",
            _ => null
        };
        if (url is null) return new ProbeSample(DateTime.UtcNow, service, false, 0, "unknown service");

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = this._http.CreateClient("probe");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 harness-playground-probe");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            var healthy = res.IsSuccessStatusCode || service.Equals("foundry-model", StringComparison.OrdinalIgnoreCase);
            return new ProbeSample(DateTime.UtcNow, service, healthy, sw.Elapsed.TotalMilliseconds, $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeSample(DateTime.UtcNow, service, false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NuGet.config")) && Directory.Exists(Path.Combine(dir.FullName, "ui"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    private static string ShortName(string category) => category.Split('.').Last();
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static string Json(object o) => JsonSerializer.Serialize(o, s_json);
}
