// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace HarnessAgentHost.Runtime;

public sealed record SpanDto(
    string Id,
    string? ParentId,
    string Source,
    string Name,
    string Kind,
    double StartMs,
    double DurationMs,
    string? Detail);

/// <summary>
/// In-process OpenTelemetry sink: listens to the harness's own <see cref="ActivitySource"/>s and keeps the
/// finished spans so the UI can render a real trace of the current session (no external collector needed).
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private const int MaxSpans = 2000;

    private readonly ConcurrentQueue<(Activity Activity, DateTime Stopped)> _spans = new();
    private readonly ActivityListener _listener;
    private DateTime _epoch = DateTime.UtcNow;

    public TraceCollector(string primarySourceName)
    {
        this._listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == primarySourceName ||
                src.Name.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal) ||
                src.Name.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                this._spans.Enqueue((a, DateTime.UtcNow));
                while (this._spans.Count > MaxSpans && this._spans.TryDequeue(out _)) { }
            }
        };
        ActivitySource.AddActivityListener(this._listener);
    }

    public void Reset()
    {
        while (this._spans.TryDequeue(out _)) { }
        this._epoch = DateTime.UtcNow;
    }

    public int Count => this._spans.Count;

    public IReadOnlyList<SpanDto> Snapshot()
    {
        var list = new List<SpanDto>();
        foreach (var (a, _) in this._spans)
        {
            if (a.StartTimeUtc < this._epoch) continue;
            list.Add(new SpanDto(
                a.Id ?? string.Empty,
                a.ParentId,
                a.Source.Name,
                a.DisplayName,
                Classify(a),
                (a.StartTimeUtc - this._epoch).TotalMilliseconds,
                a.Duration.TotalMilliseconds,
                Describe(a)));
        }
        return list.OrderBy(s => s.StartMs).ToList();
    }

    private static string Classify(Activity a)
    {
        var op = a.GetTagItem("gen_ai.operation.name") as string;
        return op switch
        {
            "chat" => "model",
            "execute_tool" => "tool",
            "invoke_agent" => "agent",
            _ => a.DisplayName.Contains("tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "other"
        };
    }

    private static string? Describe(Activity a)
    {
        var parts = new List<string>();
        if (a.GetTagItem("gen_ai.tool.name") is string tool) parts.Add(tool);
        if (a.GetTagItem("gen_ai.request.model") is string model) parts.Add(model);
        if (a.GetTagItem("gen_ai.usage.input_tokens") is { } i) parts.Add($"in {i}");
        if (a.GetTagItem("gen_ai.usage.output_tokens") is { } o) parts.Add($"out {o}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public void Dispose() => this._listener.Dispose();
}
