// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Demo SRE / incident-response tools. Read tools return fabricated telemetry;
/// mutation tools (<c>restart_service</c>, <c>rollback_deployment</c>) mark themselves as
/// "would require approval in production" in their responses so the model can narrate the guard.
/// </summary>
public static class SreTools
{
    public static IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(QueryMetrics),
        AIFunctionFactory.Create(QueryLogs),
        AIFunctionFactory.Create(ListRecentDeploys),
        AIFunctionFactory.Create(CheckServiceHealth),
        AIFunctionFactory.Create(ListDependencies),
        AIFunctionFactory.Create(RestartService),
        AIFunctionFactory.Create(RollbackDeployment),
    ];

    [Description("Returns fabricated time-series values for a service metric over a window (e.g. p95 latency ms, error rate %).")]
    public static string QueryMetrics(
        [Description("Service name, e.g. 'checkout-service'.")] string service,
        [Description("Metric name, e.g. 'latency_p95_ms' or 'error_rate_pct'.")] string metric,
        [Description("Window in minutes ending now (default 30).")] int windowMinutes = 30)
    {
        var rng = SeededRng($"{service}:{metric}:{windowMinutes}");
        var isLatency = metric.Contains("latency", StringComparison.OrdinalIgnoreCase);
        var isError = metric.Contains("error", StringComparison.OrdinalIgnoreCase);
        var baseline = isLatency ? 120 : isError ? 0.4 : 60;
        var samples = new List<object>();
        var spikeStart = rng.Next(windowMinutes / 3, windowMinutes / 2);
        for (int m = 0; m < windowMinutes; m++)
        {
            var spike = (m >= spikeStart && m <= spikeStart + 6) ? (isLatency ? 6.0 : isError ? 12.0 : 2.5) : 1.0;
            var val = Math.Round(baseline * spike * (0.85 + rng.NextDouble() * 0.3), 2);
            samples.Add(new { minutesAgo = windowMinutes - m, value = val });
        }
        return JsonSerializer.Serialize(new { service, metric, windowMinutes, samples, note = "DEMO DATA." });
    }

    [Description("Returns fabricated recent log lines for a service filtered by level. Meant to surface obvious error strings.")]
    public static string QueryLogs(
        [Description("Service name.")] string service,
        [Description("Log level filter: 'error' | 'warn' | 'info' (default 'error').")] string level = "error",
        [Description("Maximum lines to return (default 8).")] int limit = 8)
    {
        var rng = SeededRng($"{service}:{level}:{limit}");
        var templates = level.Equals("error", StringComparison.OrdinalIgnoreCase)
            ? new[]
              {
                  "ERROR PaymentGateway timeout after 5000ms",
                  "ERROR Circuit-breaker OPEN for downstream 'inventory-svc'",
                  "ERROR NullReferenceException in OrderValidator.Validate(order)",
                  "ERROR DbConnectionPool exhausted (max=100, active=100)",
                  "ERROR HTTP 503 from feature-flags at /flags/checkout",
              }
            : level.Equals("warn", StringComparison.OrdinalIgnoreCase)
              ? new[]
                {
                    "WARN slow query on orders table (1240 ms)",
                    "WARN retry #3 for inventory-svc /reserve",
                    "WARN cache miss rate 42% (threshold 20%)",
                }
              : new[]
                {
                    "INFO cart-checkout completed in 187 ms",
                    "INFO deploy #482 applied by ci-bot",
                    "INFO warm-up cache hydrated in 3.1s",
                };
        var count = Math.Min(limit, templates.Length * 3);
        var lines = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            var minsAgo = rng.Next(0, 30);
            lines.Add(new { minutesAgo = minsAgo, line = templates[rng.Next(templates.Length)] });
        }
        return JsonSerializer.Serialize(new { service, level, lines, note = "DEMO DATA." });
    }

    [Description("Returns fabricated recent deployments for a service (deploy id, commit, author, minutes ago).")]
    public static string ListRecentDeploys(
        [Description("Service name.")] string service,
        [Description("Max deploys to return (default 5).")] int limit = 5)
    {
        var rng = SeededRng($"{service}:deploys:{limit}");
        var deploys = new List<object>();
        int minutesAgo = 5 + rng.Next(0, 15);
        for (int i = 0; i < limit; i++)
        {
            deploys.Add(new
            {
                deployId = $"deploy-{rng.Next(1000, 9999)}",
                commit = Convert.ToHexString(new byte[] { (byte)rng.Next(0, 255), (byte)rng.Next(0, 255), (byte)rng.Next(0, 255), (byte)rng.Next(0, 255) }).ToLowerInvariant(),
                author = new[] { "ana@contoso", "priya@contoso", "kenji@contoso", "sam@contoso" }[rng.Next(4)],
                minutesAgo,
                summary = new[] { "bump SDK v3.4", "refactor cart total", "add feature flag", "db pool tuning", "retry policy" }[rng.Next(5)]
            });
            minutesAgo += rng.Next(30, 240);
        }
        return JsonSerializer.Serialize(new { service, deploys, note = "DEMO DATA." });
    }

    [Description("Returns the current fabricated health state of a service (up | degraded | down) with a short reason.")]
    public static string CheckServiceHealth(
        [Description("Service name.")] string service)
    {
        var rng = SeededRng($"{service}:health");
        var roll = rng.Next(0, 100);
        var state = roll < 55 ? "up" : roll < 90 ? "degraded" : "down";
        var reason = state switch
        {
            "up" => "All probes green.",
            "degraded" => "Elevated p95 latency, 3xx error rate stable.",
            _ => "Multiple synthetic probes failing for 4+ minutes."
        };
        return JsonSerializer.Serialize(new { service, state, reason, note = "DEMO DATA." });
    }

    [Description("Returns the fabricated downstream dependencies of a service (name, type, healthy).")]
    public static string ListDependencies(
        [Description("Service name.")] string service)
    {
        var rng = SeededRng($"{service}:deps");
        var pool = new[]
        {
            ("payment-gateway", "http-api"),
            ("inventory-svc", "grpc"),
            ("feature-flags", "http-api"),
            ("user-profile-db", "postgres"),
            ("cart-cache", "redis"),
            ("shipping-svc", "http-api"),
        };
        var count = rng.Next(3, pool.Length + 1);
        var deps = pool.OrderBy(_ => rng.Next()).Take(count).Select(p => new
        {
            name = p.Item1,
            type = p.Item2,
            healthy = rng.Next(0, 100) < 80
        });
        return JsonSerializer.Serialize(new { service, dependencies = deps, note = "DEMO DATA." });
    }

    [Description("Restart the specified service. WOULD REQUIRE APPROVAL IN PRODUCTION — the harness's tool-approval capability guards mutations like this.")]
    public static string RestartService(
        [Description("Service name.")] string service)
    {
        return JsonSerializer.Serialize(new
        {
            action = "restart_service",
            service,
            status = "acknowledged",
            requiresApproval = true,
            note = "DEMO ONLY — no real restart. In production, harness UseToolApproval() would gate this call."
        });
    }

    [Description("Roll back a service to a previous deploy id. WOULD REQUIRE APPROVAL IN PRODUCTION.")]
    public static string RollbackDeployment(
        [Description("Service name.")] string service,
        [Description("Deploy id to roll back to, e.g. 'deploy-4821'.")] string toDeployId)
    {
        return JsonSerializer.Serialize(new
        {
            action = "rollback_deployment",
            service,
            toDeployId,
            status = "acknowledged",
            requiresApproval = true,
            note = "DEMO ONLY — no real rollback. In production, harness UseToolApproval() would gate this call."
        });
    }

    private static Random SeededRng(string key)
    {
        int seed = 0;
        foreach (var c in key) seed = unchecked(seed * 31 + c);
        return new Random(seed);
    }
}
