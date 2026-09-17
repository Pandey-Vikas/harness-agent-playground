// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Scenarios;
using Microsoft.Agents.AI;

namespace HarnessAgentHost.Runtime;

/// <summary>
/// Mutable session state for the currently active scenario. Only one runs at a time in this demo.
/// The capability counters are updated as the runtime observes tool calls during streaming.
/// </summary>
internal sealed class ActiveSession
{
    public HarnessScenario Scenario { get; }
    public AIAgent Agent { get; }
    public AgentSession Session { get; set; }
    public TodoProvider? Todos { get; }
    public AgentModeProvider? Modes { get; }

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public HashSet<string> ExercisedCapabilities { get; } = new(StringComparer.Ordinal);
    public HashSet<string> DistinctToolsCalled { get; } = new(StringComparer.Ordinal);
    public int ToolCallCount { get; set; }
    public int RunCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    public ActiveSession(HarnessScenario scenario, AIAgent agent, AgentSession session,
                        TodoProvider? todos, AgentModeProvider? modes)
    {
        this.Scenario = scenario;
        this.Agent = agent;
        this.Session = session;
        this.Todos = todos;
        this.Modes = modes;

        // Harness-enabled defaults are exercised as soon as the scenario starts, because
        // the runtime pipeline runs them for every model call.
        this.ExercisedCapabilities.Add(HarnessCapabilities.HistoryPersistence);
        this.ExercisedCapabilities.Add(HarnessCapabilities.Compaction);
        this.ExercisedCapabilities.Add(HarnessCapabilities.ToolApproval);
        this.ExercisedCapabilities.Add(HarnessCapabilities.OpenTelemetry);
        this.ExercisedCapabilities.Add(HarnessCapabilities.Looping);
    }
}
