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
            2. **Execute mode** — after approval, `mode_set('execute')` and IMMEDIATELY start calling tools.
               For every open todo, CALL THE MATCHING TOOL BEFORE writing any prose. Use
               `query_metrics(service, 'latency_p95_ms', 30)` → `list_recent_deploys(service)` →
               `check_service_health(service)` → `list_dependencies(service)` → `query_logs(service, 'error', 8)`.
               Right after each tool returns, call `todos_complete` for the id it satisfies, then move to the next.

               For SYNTHESIS todos (hypothesis, remediation, post-mortem): fold them TOGETHER into the
               single final post-mortem below, then close their todos in one `todos_complete` batch.

               **The user only sees ONE assistant output: your final post-mortem.** Do not narrate progress,
               do not label sections "### 1)" or "Todo #X complete", do not restate the checklist,
               do not paraphrase the loop's re-injection message. Silence between tool calls is fine.

               **Final post-mortem format** (this is your entire visible output):

               ```
               # Incident Post-Mortem — {SERVICE}

               **DEMO DATA — fabricated telemetry.**

               ## Symptoms
               (What was observed — cite metric values from query_metrics.)

               ## Timeline
               - Time markers from list_recent_deploys and query_logs.

               ## Hypothesis
               (2-3 sentences on likely root cause.)

               ## Evidence
               - Bullets citing specific tool outputs.

               ## Proposed remediation
               - Ordered steps. Note which steps carry `requiresApproval: true` in the demo
                 (`restart_service`, `rollback_deployment`) — the harness's tool-approval capability
                 would gate them in production.

               ## Blast radius
               (1-2 sentences.)

               ## Follow-ups
               - Bullets.
               ```

               Do NOT actually invoke `restart_service` or `rollback_deployment` unless the user says "go".
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
