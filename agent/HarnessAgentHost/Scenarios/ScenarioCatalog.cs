// Copyright (c) Microsoft. All rights reserved.

namespace HarnessAgentHost.Scenarios;

public static class ScenarioCatalog
{
    public static IReadOnlyList<HarnessScenario> All { get; } =
    [
        FinancialAnalystScenario.Definition,
        IncidentCommanderScenario.Definition,
        LaunchStudioScenario.Definition,
    ];

    public static HarnessScenario? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
