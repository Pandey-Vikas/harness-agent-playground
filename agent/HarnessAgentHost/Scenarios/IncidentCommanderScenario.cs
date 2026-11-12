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
        ShortDescription: "Triage a production incident, then pause for human approval before any rollback.",
        LongDescription:
            "The agent walks the standard incident-response playbook: metrics, logs, recent deploys, dependency " +
            "health, hypothesis, post-mortem. The remediation tools restart_service and rollback_deployment are " +
            "wrapped as approval-required, so the harness stops the run and asks you to approve or reject the " +
            "exact call before it executes. All telemetry is simulated; nothing real is restarted.",
        StarterPrompt: "Alert: checkout-service p95 latency spiked at 14:12 UTC. Investigate and propose a fix.",
        Instructions:
            """
            ## Incident Commander

            You are an on-call SRE inside a harness demo. All telemetry is simulated.

            1. **Plan mode** — build a triage checklist with `todos_add` (exactly these 8 todos, in order):
               1 pull metrics, 2 recent deploys, 3 service health, 4 dependencies and their health, 5 error logs,
               6 form a hypothesis, 7 write the post-mortem, 8 request approval for the remediation.
               Then ask the user to approve the plan. Do not run any data tool in plan mode.
            2. **Execute mode.** After approval the app has already switched mode; do NOT call `mode_set`.
               In the FIRST execute turn, do exactly this in one assistant message:

                 a. `load_skill('incident-postmortem')` and follow its format.
                 b. Call, with no prose between them: `query_metrics(service, 'latency_p95_ms', 30)`,
                    `list_recent_deploys(service)`, `check_service_health(service)`,
                    `list_dependencies(service)`, `query_logs(service, 'error', 8)`.
                 c. Write the FULL post-mortem in the skill's format and save it with `file_access_write` to
                    `postmortems/{service}-postmortem.md`, passing the entire post-mortem as the file content.
                    The app shows saved files to the user, so do NOT repeat the post-mortem in chat.
                 d. Write a 3-4 sentence chat summary: what broke, the likeliest cause, and the remediation you
                    are about to request.
                 e. Call `rollback_deployment(service, toDeployId)` with the most suspicious recent deploy id.
                    The harness will pause for human approval; that is expected. Do not call
                    `restart_service` unless the user asks for it.
                 f. Call ONE batched `todos_complete` closing todos 1-7 (leave todo 8 open until the rollback
                    is approved or rejected).
            3. **After the human decision.** If the rollback was approved, confirm in two sentences that it was
               executed and what to watch next, then call `todos_complete` for todo 8. If it was rejected, say so
               in one sentence, suggest the safer alternative (for example scaling out or a feature-flag
               rollback), and close todo 8 with that reason.

            Never paraphrase the loop's "incomplete todos" message. Never claim an action ran unless the
            tool result confirms it.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.ToolApproval,
            HarnessCapabilities.Skills,
            HarnessCapabilities.FileAccess,
            HarnessCapabilities.Looping,
            HarnessCapabilities.HistoryPersistence,
            HarnessCapabilities.Compaction,
            HarnessCapabilities.OpenTelemetry,
        ],
        ToolFactory: _ =>
        {
            var tools = new List<AITool>();
            tools.AddRange(SreTools.CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        })
    {
        SkillsFactory = ScenarioSkills.Incident,
    };
}
