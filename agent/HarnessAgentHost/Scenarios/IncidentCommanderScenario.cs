// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Scenarios;

public static class IncidentCommanderScenario
{
    public const string Id = "incident-commander";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "SRE Incident Commander",
        Domain: "DevOps / SRE",
        ShortDescription: "Triage a production incident: metrics, logs, deploys, dependencies, then propose a fix.",
        LongDescription:
            "The agent walks the standard incident-response playbook. It queries metrics and logs, checks recent " +
            "deploys and dependency health, forms a hypothesis, and proposes remediation. Mutation tools like " +
            "restart_service and rollback_deployment are marked approval-required so they demonstrate the harness's " +
            "tool-approval capability rather than actually restarting anything.",
        StarterPrompt: "Alert: checkout-service p95 latency spiked at 14:12 UTC. Investigate and propose a fix.",
        Instructions:
            """
            ## Incident Commander

            You are an on-call SRE inside a harness demo. Follow this playbook for every alert:

            1. **Plan mode** — build a triage checklist with `todos_add`:
               • pull metrics for the affected service • pull recent deploys • check service health
               • list dependencies and their health • pull error logs • form a hypothesis
               • propose remediation (with approval where risky).
               Ask the user to approve before switching modes.
            2. **Execute mode — pair every tool call with a todos_complete.**
               When `mode_set('execute')` fires, IMMEDIATELY start calling tools.

               For DATA todos (query telemetry from the harness), the correct turn shape is:
                 a. Call the matching tool (e.g. `query_metrics({ service: 'checkout-service', metric: 'latency_p95_ms' })`).
                 b. Receive the result.
                 c. In the SAME assistant turn, call `todos_complete({ items: [{ id: N, reason: 'one-line summary' }] })`.
                 d. Move to the next data tool.
               Recommended data-tool order matching the todos:
               `query_metrics` → `list_recent_deploys` → `check_service_health` →
               `list_dependencies` → `query_logs(service, 'error', 8)`.

               For SYNTHESIS todos (hypothesis, remediation plan, post-mortem — no matching tool):
                 a. Write the synthesis directly in the assistant message (2-4 sentences).
                 b. In the SAME turn, call `todos_complete` with a one-line summary.
                 c. Do NOT wait for a tool that does not exist.

               For REMEDIATION calls (`restart_service`, `rollback_deployment`): only invoke after the user
               explicitly says "go". Their response object includes `requiresApproval: true` — in production
               these calls would be gated by the harness's tool-approval capability.

               **HARD RULES:**
               * Every tool call MUST be followed by `todos_complete` in the same turn.
               * Never paraphrase the loop's "incomplete todos" message — act on the todos instead.
            3. **Remediation** — when you propose `restart_service` or `rollback_deployment`, EXPLICITLY note that
               in production these calls would be gated by the harness's tool-approval capability, and only invoke
               them after the user says "go". Their response object will include `requiresApproval: true`.
            4. Finish with a short **Incident Post-Mortem** in Markdown: symptoms, hypothesis, evidence,
               remediation, blast radius, follow-ups. Note that all telemetry is DEMO DATA.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.ToolApproval,
            HarnessCapabilities.FileMemory,
            HarnessCapabilities.Looping,
            HarnessCapabilities.HistoryPersistence,
            HarnessCapabilities.Compaction,
            HarnessCapabilities.OpenTelemetry,
        ],
        ToolFactory: () =>
        {
            var tools = new List<AITool>();
            tools.AddRange(SreTools.CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        });
}
