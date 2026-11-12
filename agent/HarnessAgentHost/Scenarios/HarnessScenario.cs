// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Scenarios;

/// <summary>
/// Immutable definition of one demo scenario. The runtime uses this to build a scenario-specific
/// <c>HarnessAgent</c> on demand.
/// </summary>
public sealed record HarnessScenario(
    string Id,
    string Title,
    string Domain,
    string ShortDescription,
    string LongDescription,
    string StarterPrompt,
    string Instructions,
    IReadOnlyList<string> HighlightedCapabilities,
    Func<IServiceProvider, IList<AITool>> ToolFactory)
{
    /// <summary>Code-defined Agent Skills exposed through the harness skills provider.</summary>
    public Func<IReadOnlyList<AgentSkill>>? SkillsFactory { get; init; }

    /// <summary>Named child agents the parent can delegate to through the background-agents provider.</summary>
    public Func<IChatClient, IServiceProvider, IReadOnlyList<AIAgent>>? BackgroundAgentsFactory { get; init; }
}
