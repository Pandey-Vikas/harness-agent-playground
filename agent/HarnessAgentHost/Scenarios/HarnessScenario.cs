// Copyright (c) Microsoft. All rights reserved.

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
    Func<IList<AITool>> ToolFactory);
