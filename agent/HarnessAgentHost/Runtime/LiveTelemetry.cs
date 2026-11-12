// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace HarnessAgentHost.Runtime;

public sealed record RequestSample(DateTime Utc, string Route, int Status, double Ms);
public sealed record ResourceSample(DateTime Utc, double CpuPct, double WorkingSetMb, double GcHeapMb, int Threads);
public sealed record LogEntry(DateTime Utc, string Level, string Category, string Message);
public sealed record ProbeSample(DateTime Utc, string Service, bool Healthy, double Ms, string? Detail);
public sealed record ChangeEvent(string Id, DateTime Utc, string Author, string Summary, string Kind);

/// <summary>
/// Live measurements of this very host: request latency, process resources, captured log lines, dependency probes,
/// and the change history (git commits plus configuration changes). The SRE scenario reads from here, so
/// everything the agent reports was measured, not scripted.
/// </summary>
public sealed class LiveTelemetry
{
    private const int MaxItems = 20_000;

    private readonly ConcurrentQueue<RequestSample> _requests = new();
    private readonly ConcurrentQueue<ResourceSample> _resources = new();
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly ConcurrentQueue<ProbeSample> _probes = new();
    private readonly ConcurrentQueue<ChangeEvent> _changes = new();
    private readonly object _faultLock = new();

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public bool FaultActive { get; private set; }
    public int FaultLatencyMs { get; private set; }
    public int FaultErrorRatePct { get; private set; }
    public string? FaultChangeId { get; private set; }

    public void RecordRequest(string route, int status, double ms) => Add(this._requests, new RequestSample(DateTime.UtcNow, route, status, ms));
    public void RecordResource(ResourceSample s) => Add(this._resources, s);
    public void RecordLog(string level, string category, string message) => Add(this._logs, new LogEntry(DateTime.UtcNow, level, category, message));
    public void RecordProbe(ProbeSample s) => Add(this._probes, s);

    private static void Add<T>(ConcurrentQueue<T> q, T item)
    {
        q.Enqueue(item);
        while (q.Count > MaxItems && q.TryDequeue(out _)) { }
    }

    public (string ChangeId, DateTime Utc) InjectFault(int latencyMs, int errorRatePct)
    {
        lock (this._faultLock)
        {
            var id = $"chg-{Guid.NewGuid().ToString("N")[..6]}";
            this.FaultActive = true;
            this.FaultLatencyMs = latencyMs;
            this.FaultErrorRatePct = errorRatePct;
            this.FaultChangeId = id;
            var now = DateTime.UtcNow;
            Add(this._changes, new ChangeEvent(id, now, "config-service",
                $"session-store client override: timeout budget {latencyMs} ms, retry storm enabled (error injection {errorRatePct}%)", "config"));
            return (id, now);
        }
    }

    public bool ClearFault(string? changeId, string reason)
    {
        lock (this._faultLock)
        {
            if (!this.FaultActive) return false;
            if (changeId is not null && !string.Equals(changeId, this.FaultChangeId, StringComparison.OrdinalIgnoreCase)) return false;
            Add(this._changes, new ChangeEvent($"rb-{Guid.NewGuid().ToString("N")[..6]}", DateTime.UtcNow, "incident-commander",
                $"rollback of {this.FaultChangeId}: {reason}", "config"));
            this.FaultActive = false;
            this.FaultLatencyMs = 0;
            this.FaultErrorRatePct = 0;
            this.FaultChangeId = null;
            return true;
        }
    }

    public IReadOnlyList<ChangeEvent> ConfigChanges() => this._changes.ToArray();

    public IReadOnlyList<ProbeSample> LatestProbes()
    {
        return this._probes.ToArray().GroupBy(p => p.Service).Select(g => g.OrderByDescending(p => p.Utc).First()).ToList();
    }

    public IReadOnlyList<LogEntry> Logs(string minLevel, int limit, string? contains)
    {
        var min = Rank(minLevel);
        return this._logs.ToArray()
            .Where(l => Rank(l.Level) >= min)
            .Where(l => string.IsNullOrEmpty(contains) || l.Message.Contains(contains, StringComparison.OrdinalIgnoreCase) || l.Category.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Utc).Take(limit).ToList();
    }

    private static int Rank(string level) => level.ToLowerInvariant() switch
    {
        "error" or "critical" => 3,
        "warn" or "warning" => 2,
        "info" or "information" => 1,
        _ => 0
    };

    public sealed record Bucket(int MinutesAgo, double? Value, int Samples);

    public sealed record MetricResult(string Service, string Metric, string Unit, int WindowMinutes, IReadOnlyList<Bucket> Buckets,
        double? BaselineAvg, double? RecentAvg, double? ChangePct, double AvailableMinutes, string Source);

    /// <summary>Builds a per-minute series for a metric. Returns null when the service/metric pair is unknown.</summary>
    public MetricResult? QueryMetric(string service, string metric, int windowMinutes, IReadOnlyList<SpanDto> spans)
    {
        windowMinutes = Math.Clamp(windowMinutes, 1, 120);
        var now = DateTime.UtcNow;
        var since = now.AddMinutes(-windowMinutes);
        var m = metric.ToLowerInvariant();
        var svc = service.ToLowerInvariant();

        List<(DateTime T, double V)> points;
        Func<List<double>, double?> agg;
        string unit, source;

        if (svc is "harness-host" or "agent-host" && m.StartsWith("latency"))
        {
            points = this._requests.Where(r => r.Utc >= since).Select(r => (r.Utc, r.Ms)).ToList();
            agg = m.Contains("p95") ? P95 : Avg; unit = "ms"; source = "request middleware on the public read APIs (/health, /api/scenarios, /api/artifacts, /api/session/trace)";
        }
        else if (svc is "harness-host" or "agent-host" && m is "error_rate_pct")
        {
            points = this._requests.Where(r => r.Utc >= since).Select(r => (r.Utc, r.Status >= 500 ? 100.0 : 0.0)).ToList();
            agg = Avg; unit = "%"; source = "request middleware (HTTP 5xx share)";
        }
        else if (svc is "harness-host" or "agent-host" && m is "request_count")
        {
            points = this._requests.Where(r => r.Utc >= since).Select(r => (r.Utc, 1.0)).ToList();
            agg = v => v.Sum(); unit = "requests"; source = "request middleware";
        }
        else if (svc is "harness-host" or "agent-host" && m is "cpu_pct")
        {
            points = this._resources.Where(r => r.Utc >= since).Select(r => (r.Utc, r.CpuPct)).ToList();
            agg = Avg; unit = "%"; source = "process CPU time sampled every 5 s";
        }
        else if (svc is "harness-host" or "agent-host" && m is "memory_mb")
        {
            points = this._resources.Where(r => r.Utc >= since).Select(r => (r.Utc, r.WorkingSetMb)).ToList();
            agg = Avg; unit = "MB"; source = "process working set sampled every 5 s";
        }
        else if (svc is "foundry-model" && m.StartsWith("model_call_latency"))
        {
            points = spans.Where(s => s.Kind == "model" && s.StartUtc >= since).Select(s => (s.StartUtc, s.DurationMs)).ToList();
            agg = m.Contains("p95") ? P95 : Avg; unit = "ms"; source = "OpenTelemetry chat spans of the current session";
        }
        else if (svc is "foundry-model" && m is "model_call_count")
        {
            points = spans.Where(s => s.Kind == "model" && s.StartUtc >= since).Select(s => (s.StartUtc, 1.0)).ToList();
            agg = v => v.Sum(); unit = "calls"; source = "OpenTelemetry chat spans of the current session";
        }
        else if (m is "probe_latency_ms" && svc is "ui-proxy" or "foundry-model" or "yahoo-finance")
        {
            points = this._probes.Where(p => p.Utc >= since && p.Service == svc).Select(p => (p.Utc, p.Ms)).ToList();
            agg = Avg; unit = "ms"; source = "synthetic HTTP probe every 30 s";
        }
        else
        {
            return null;
        }

        var buckets = new List<Bucket>();
        for (var i = windowMinutes; i >= 1; i--)
        {
            var from = now.AddMinutes(-i);
            var to = now.AddMinutes(-(i - 1));
            var vals = points.Where(p => p.T >= from && p.T < to).Select(p => p.V).ToList();
            var v = vals.Count == 0 ? (double?)null : agg(vals);
            buckets.Add(new Bucket(i - 1, v is null ? null : Math.Round(v.Value, 2), vals.Count));
        }

        var withData = buckets.Where(b => b.Value.HasValue).ToList();
        double? baseline = null, recent = null, change = null;
        if (withData.Count >= 2)
        {
            var half = withData.Count / 2;
            baseline = Math.Round(withData.Take(half).Average(b => b.Value!.Value), 2);
            recent = Math.Round(withData.Skip(half).Average(b => b.Value!.Value), 2);
            if (baseline > 0) change = Math.Round((recent.Value - baseline.Value) / baseline.Value * 100, 1);
        }
        var available = Math.Min(windowMinutes, (now - this.StartedUtc).TotalMinutes);
        return new MetricResult(service, metric, unit, windowMinutes, buckets, baseline, recent, change, Math.Round(available, 1), source);
    }

    private static double? Avg(List<double> v) => v.Count == 0 ? null : v.Average();

    private static double? P95(List<double> v)
    {
        if (v.Count == 0) return null;
        var sorted = v.OrderBy(x => x).ToList();
        var idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    /// <summary>Recent git commits of this repository, newest first. Empty when git is unavailable.</summary>
    public static IReadOnlyList<ChangeEvent> GitCommits(string repoRoot, int limit)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{repoRoot}\" log -n {limit} --pretty=format:%h|%cI|%an|%s")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return [];
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var list = new List<ChangeEvent>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|', 4);
                if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[1], out var when)) continue;
                list.Add(new ChangeEvent($"git-{parts[0]}", when.UtcDateTime, parts[2], parts[3], "commit"));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Feeds every log line the host writes into <see cref="LiveTelemetry"/>.</summary>
public sealed class RingLoggerProvider : ILoggerProvider
{
    private readonly LiveTelemetry _telemetry;
    public RingLoggerProvider(LiveTelemetry telemetry) => this._telemetry = telemetry;
    public ILogger CreateLogger(string categoryName) => new RingLogger(categoryName, this._telemetry);
    public void Dispose() { }

    private sealed class RingLogger : ILogger
    {
        private readonly string _category;
        private readonly LiveTelemetry _telemetry;
        public RingLogger(string category, LiveTelemetry telemetry) { this._category = category; this._telemetry = telemetry; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!this.IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += $" | {exception.GetType().Name}: {exception.Message}";
            this._telemetry.RecordLog(logLevel.ToString(), this._category, msg);
        }
    }
}

/// <summary>Samples process resources and probes the host and its dependencies on a timer.</summary>
public sealed class TelemetryWorker : BackgroundService
{
    private readonly LiveTelemetry _telemetry;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TelemetryWorker> _log;
    private readonly int _hostPort;
    private readonly int _uiPort;

    public TelemetryWorker(LiveTelemetry telemetry, IHttpClientFactory http, ILogger<TelemetryWorker> log)
    {
        this._telemetry = telemetry;
        this._http = http;
        this._log = log;
        this._hostPort = int.TryParse(Environment.GetEnvironmentVariable("HARNESS_PORT"), out var hp) ? hp : 5099;
        this._uiPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var up) ? up : 3000;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastWall = DateTime.UtcNow;
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                proc.Refresh();
                var cpu = proc.TotalProcessorTime;
                var wall = DateTime.UtcNow;
                var cpuPct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (wall - lastWall).TotalMilliseconds) / Environment.ProcessorCount * 100;
                lastCpu = cpu; lastWall = wall;
                this._telemetry.RecordResource(new ResourceSample(wall, Math.Round(cpuPct, 2),
                    Math.Round(proc.WorkingSet64 / 1048576.0, 1), Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1), proc.Threads.Count));

                await this.ProbeAsync("harness-host", $"http://127.0.0.1:{this._hostPort}/health", record: false, stoppingToken).ConfigureAwait(false);
                if (tick++ % 6 == 0)
                {
                    await this.ProbeAsync("ui-proxy", $"http://127.0.0.1:{this._uiPort}/api/config", record: true, stoppingToken).ConfigureAwait(false);
                    var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT");
                    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var u))
                        await this.ProbeAsync("foundry-model", $"{u.Scheme}://{u.Host}/", record: true, stoppingToken, anyResponseIsHealthy: true).ConfigureAwait(false);
                    await this.ProbeAsync("yahoo-finance", "https://query1.finance.yahoo.com/v8/finance/chart/MSFT?range=1d&interval=1d", record: true, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { this._log.LogWarning(ex, "Telemetry sampling failed"); }
        }
    }

    private async Task ProbeAsync(string service, string url, bool record, CancellationToken ct, bool anyResponseIsHealthy = false)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = this._http.CreateClient("probe");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 harness-playground-probe");
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            var healthy = anyResponseIsHealthy || res.IsSuccessStatusCode;
            if (record) this._telemetry.RecordProbe(new ProbeSample(DateTime.UtcNow, service, healthy, sw.Elapsed.TotalMilliseconds, $"HTTP {(int)res.StatusCode}"));
            if (!healthy) this._log.LogWarning("Probe of {Service} returned HTTP {Status} in {Ms} ms", service, (int)res.StatusCode, Math.Round(sw.Elapsed.TotalMilliseconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            sw.Stop();
            if (record) this._telemetry.RecordProbe(new ProbeSample(DateTime.UtcNow, service, false, sw.Elapsed.TotalMilliseconds, ex.Message));
            this._log.LogError("Probe of {Service} failed after {Ms} ms: {Error}", service, Math.Round(sw.Elapsed.TotalMilliseconds), ex.Message);
        }
    }
}
