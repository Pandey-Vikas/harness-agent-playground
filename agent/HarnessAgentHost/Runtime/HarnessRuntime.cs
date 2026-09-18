// Copyright (c) Microsoft. All rights reserved.

using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Identity;
using HarnessAgentHost.Scenarios;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Runtime;

/// <summary>
/// Owns the shared <see cref="IChatClient"/> and one <see cref="ActiveSession"/> at a time.
/// Callers pick a scenario via <see cref="StartScenarioAsync"/>; the runtime builds a
/// scenario-specific <c>HarnessAgent</c> and streams runs over SSE.
/// </summary>
public sealed class HarnessRuntime
{
    private const string TracingSourceName = "Harness.Playground";
    private const int MaxContextWindowTokens = 128_000;
    private const int MaxOutputTokens = 16_384;

    private static readonly JsonSerializerOptions s_jsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly ILogger<HarnessRuntime> _log;
    private readonly IServiceProvider _services;

    private IChatClient? _chatClient;
    private ActiveSession? _current;

    public HarnessRuntime(ILogger<HarnessRuntime> log, IServiceProvider services)
    {
        this._log = log;
        this._services = services;
    }

    public string ModelName { get; private set; } = string.Empty;
    public string Endpoint { get; private set; } = string.Empty;

    public IReadOnlyList<HarnessScenario> Scenarios => ScenarioCatalog.All;

    public Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT is not set. Run the setup wizard (ui/setup) or set it manually to " +
                "https://<foundry-account>.services.ai.azure.com/api/projects/<project-name>.");
        var deploymentName = Environment.GetEnvironmentVariable("FOUNDRY_MODEL") ?? "gpt-4o-mini";
        this.Endpoint = endpoint;
        this.ModelName = deploymentName;

        var project = new AIProjectClient(
            new Uri(endpoint),
            new DefaultAzureCredential(),
            new AIProjectClientOptions { RetryPolicy = new ClientRetryPolicy(3) });

        this._chatClient = project
            .GetProjectOpenAIClient()
            .GetResponsesClient()
            .AsIChatClient(deploymentName);

        this._log.LogInformation("HarnessRuntime ready. Model={Model}, Endpoint={Endpoint}", deploymentName, endpoint);
        return Task.CompletedTask;
    }

    public async Task<StatusSnapshot> StartScenarioAsync(string scenarioId, CancellationToken ct)
    {
        var scenario = ScenarioCatalog.Find(scenarioId)
            ?? throw new ArgumentException($"Unknown scenario '{scenarioId}'.");
        var chatClient = this._chatClient ?? throw new InvalidOperationException("Runtime not initialized.");

        await this._sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
            {
                Name = scenario.Title,
                Description = scenario.ShortDescription,
                OpenTelemetrySourceName = TracingSourceName,
                MaxContextWindowTokens = MaxContextWindowTokens,
                MaxOutputTokens = MaxOutputTokens,
                AgentModeProviderOptions = new AgentModeProviderOptions { DefaultMode = "plan" },
                LoopEvaluators =
                [
                    new TodoCompletionLoopEvaluator(new TodoCompletionLoopEvaluatorOptions { Modes = ["execute"] })
                ],
                LoopAgentOptions = new LoopAgentOptions { MaxIterations = 4 },
                ChatOptions = new ChatOptions
                {
                    Instructions = scenario.Instructions,
                    MaxOutputTokens = MaxOutputTokens,
                    Tools = scenario.ToolFactory(this._services),
                }
            });

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            this._current = new ActiveSession(
                scenario,
                agent,
                session,
                agent.GetService<TodoProvider>(),
                agent.GetService<AgentModeProvider>());

            this._log.LogInformation("Scenario '{Scenario}' started.", scenario.Id);
            return await BuildStatusAsync(this._current, ct).ConfigureAwait(false);
        }
        finally
        {
            this._sessionLock.Release();
        }
    }

    public async Task ResetSessionAsync(CancellationToken ct)
    {
        var current = this._current ?? throw new InvalidOperationException("No scenario is active.");
        await this._sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current.Session = await current.Agent.CreateSessionAsync(ct).ConfigureAwait(false);
            current.ExercisedCapabilities.Clear();
            current.DistinctToolsCalled.Clear();
            current.ToolCallCount = 0;
            current.RunCount = 0;
            current.InputTokens = 0;
            current.OutputTokens = 0;

            current.ExercisedCapabilities.Add(HarnessCapabilities.HistoryPersistence);
            current.ExercisedCapabilities.Add(HarnessCapabilities.Compaction);
            current.ExercisedCapabilities.Add(HarnessCapabilities.ToolApproval);
            current.ExercisedCapabilities.Add(HarnessCapabilities.OpenTelemetry);
            current.ExercisedCapabilities.Add(HarnessCapabilities.Looping);
        }
        finally
        {
            this._sessionLock.Release();
        }
    }

    public async Task SetModeAsync(string mode, CancellationToken ct)
    {
        var current = this._current ?? throw new InvalidOperationException("No scenario is active.");
        var modes = current.Modes ?? throw new InvalidOperationException("AgentModeProvider unavailable.");
        await modes.SetModeAsync(current.Session, mode, ct).ConfigureAwait(false);
        current.ExercisedCapabilities.Add(HarnessCapabilities.Modes);
    }

    public async Task<StatusSnapshot?> GetStatusAsync(CancellationToken ct)
    {
        var current = this._current;
        return current is null ? null : await BuildStatusAsync(current, ct).ConfigureAwait(false);
    }

    public Task<ExecutionSummary?> GetSummaryAsync(CancellationToken ct)
    {
        var current = this._current;
        if (current is null) return Task.FromResult<ExecutionSummary?>(null);
        return BuildSummaryAsync(current, ct).ContinueWith(t => (ExecutionSummary?)t.Result, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async Task StreamRunAsync(string prompt, HttpResponse response, CancellationToken ct)
    {
        var current = this._current;
        if (current is null)
        {
            await WriteEventAsync(response, "error", new { message = "No scenario is active. Call POST /api/session/start first." }, ct);
            return;
        }
        if (!await this._runLock.WaitAsync(0, ct))
        {
            await WriteEventAsync(response, "error", new { message = "A run is already in progress." }, ct);
            return;
        }

        try
        {
            current.RunCount++;
            var startMode = await SafeModeAsync(current, ct);
            await WriteEventAsync(response, "start", new { prompt, mode = startMode, scenarioId = current.Scenario.Id }, ct);

            string? currentMessageId = null;
            await foreach (var update in current.Agent.RunStreamingAsync(prompt, current.Session, cancellationToken: ct))
            {
                // Every LoopAgent iteration produces a new AgentRunResponseUpdate.MessageId; the UI uses this
                // to render each iteration as its own chat bubble instead of concatenating them.
                if (!string.IsNullOrEmpty(update.MessageId) &&
                    !string.Equals(update.MessageId, currentMessageId, StringComparison.Ordinal))
                {
                    currentMessageId = update.MessageId;
                    await WriteEventAsync(response, "message_start", new { messageId = currentMessageId }, ct);
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            await WriteEventAsync(response, "text", new { text = tc.Text }, ct);
                            break;

                        case FunctionCallContent fc:
                            current.ToolCallCount++;
                            current.DistinctToolsCalled.Add(fc.Name);
                            var cap = HarnessCapabilities.MapToolNameToCapability(fc.Name);
                            current.ExercisedCapabilities.Add(cap);
                            await WriteEventAsync(response, "tool_call", new
                            {
                                id = fc.CallId,
                                name = fc.Name,
                                arguments = fc.Arguments,
                                capability = cap
                            }, ct);
                            break;

                        case FunctionResultContent fr:
                            await WriteEventAsync(response, "tool_result", new
                            {
                                id = fr.CallId,
                                result = fr.Result?.ToString()
                            }, ct);
                            break;

                        case UsageContent uc:
                            current.InputTokens += uc.Details.InputTokenCount ?? 0;
                            current.OutputTokens += uc.Details.OutputTokenCount ?? 0;
                            await WriteEventAsync(response, "usage", new
                            {
                                inputTokens = uc.Details.InputTokenCount,
                                outputTokens = uc.Details.OutputTokenCount,
                                totalTokens = uc.Details.TotalTokenCount
                            }, ct);
                            break;
                    }
                }
            }

            var status = await BuildStatusAsync(current, ct);
            await WriteEventAsync(response, "done", status, ct);
        }
        catch (OperationCanceledException)
        {
            await WriteEventAsync(response, "cancelled", new { message = "Run cancelled." }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            this._log.LogError(ex, "Run failed");
            // Surface the actual exception on the wire so the UI can display it truthfully.
            await WriteEventAsync(response, "error", new
            {
                message = ex.Message,
                type = ex.GetType().FullName,
                detail = ex.InnerException?.Message,
                stack = ex.StackTrace
            }, CancellationToken.None);
        }
        finally
        {
            this._runLock.Release();
        }
    }

    private async Task<StatusSnapshot> BuildStatusAsync(ActiveSession current, CancellationToken ct)
    {
        var mode = await SafeModeAsync(current, ct);
        var todos = new List<TodoDto>();
        if (current.Todos is not null)
        {
            var items = await current.Todos.GetAllTodosAsync(current.Session, ct);
            foreach (var t in items) todos.Add(new TodoDto(t.Id, t.Title, t.Description, t.IsComplete));
        }
        return new StatusSnapshot(
            ScenarioId: current.Scenario.Id,
            ScenarioTitle: current.Scenario.Title,
            Mode: mode,
            Todos: todos,
            ExercisedCapabilities: current.ExercisedCapabilities.ToList(),
            HighlightedCapabilities: current.Scenario.HighlightedCapabilities,
            Model: this.ModelName,
            Endpoint: this.Endpoint);
    }

    private async Task<ExecutionSummary> BuildSummaryAsync(ActiveSession current, CancellationToken ct)
    {
        var status = await BuildStatusAsync(current, ct);
        var elapsed = (DateTime.UtcNow - current.StartedUtc).TotalSeconds;
        var highlighted = current.Scenario.HighlightedCapabilities;
        var breakdown = HarnessCapabilities.All.Select(c => new CapabilityRow(
            c.Id,
            c.Name,
            c.Description,
            Highlighted: highlighted.Contains(c.Id),
            Exercised: current.ExercisedCapabilities.Contains(c.Id))).ToList();

        return new ExecutionSummary(
            ScenarioId: current.Scenario.Id,
            ScenarioTitle: current.Scenario.Title,
            Domain: current.Scenario.Domain,
            DurationSeconds: (int)elapsed,
            RunCount: current.RunCount,
            ToolCallCount: current.ToolCallCount,
            DistinctToolsCalled: current.DistinctToolsCalled.OrderBy(s => s).ToList(),
            InputTokens: current.InputTokens,
            OutputTokens: current.OutputTokens,
            Todos: status.Todos,
            Capabilities: breakdown);
    }

    private static async Task<string> SafeModeAsync(ActiveSession current, CancellationToken ct)
    {
        try { return current.Modes is null ? "unknown" : await current.Modes.GetModeAsync(current.Session, ct); }
        catch { return "unknown"; }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, s_jsonOpts);
        var sb = new StringBuilder();
        sb.Append("event: ").Append(eventName).Append('\n');
        sb.Append("data: ").Append(json).Append("\n\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}

public sealed record TodoDto(int Id, string Title, string? Description, bool IsComplete);

public sealed record StatusSnapshot(
    string ScenarioId,
    string ScenarioTitle,
    string Mode,
    IReadOnlyList<TodoDto> Todos,
    IReadOnlyList<string> ExercisedCapabilities,
    IReadOnlyList<string> HighlightedCapabilities,
    string Model,
    string Endpoint);

public sealed record CapabilityRow(
    string Id,
    string Name,
    string Description,
    bool Highlighted,
    bool Exercised);

public sealed record ExecutionSummary(
    string ScenarioId,
    string ScenarioTitle,
    string Domain,
    int DurationSeconds,
    int RunCount,
    int ToolCallCount,
    IReadOnlyList<string> DistinctToolsCalled,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<TodoDto> Todos,
    IReadOnlyList<CapabilityRow> Capabilities);

