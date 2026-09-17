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
            2. **Execute mode — tool-first, always.**
               When `mode_set('execute')` fires, IMMEDIATELY call a tool. Do NOT write prose first.
               Walk the checklist calling `query_metrics(service, 'latency_p95_ms', 30)`, then
               `list_recent_deploys(service)`, `check_service_health(service)`, `list_dependencies(service)`,
               and `query_logs(service, 'error', 8)`. After each tool result, call `todos_complete`
               with a one-line reason.

               **HARD RULES for execute mode:**
               * Every assistant turn MUST include at least one tool call while todos are open.
               * A text-only response is a failure — do not do it.
               * Do NOT paraphrase or restate the todo list. Call tools.
               * Only after evidence is gathered, propose remediation and write the post-mortem.
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
