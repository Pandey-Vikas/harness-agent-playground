// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HarnessAgentHost.Scenarios;

public static class IncidentCommanderScenario
{
    public const string Id = "incident-commander";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "SRE Incident Commander",
        Domain: "DevOps / SRE",
        ShortDescription: "Investigate a live incident on this very host, then pause for human approval before any rollback.",
        LongDescription:
            "Starting this scenario injects a real configuration fault into the running host (extra latency and 503s). " +
            "The agent then investigates with live telemetry: request latency and error rate measured by middleware, " +
            "captured log lines, git history and configuration changes, and HTTP probes of the host and its " +
            "dependencies. It writes a post-mortem, then requests approval to roll back the offending change. " +
            "restart_service and rollback_deployment are approval-gated; approving the rollback really reverts the fault " +
            "and you can watch latency recover.",
        StarterPrompt: "Alert: harness-host API latency and error rate look elevated. Investigate and propose a fix.",
        Instructions:
            """
            ## Incident Commander

            You are the on-call SRE for the Harness Playground. The system under investigation is this host and
            its dependencies: `harness-host`, `ui-proxy`, `foundry-model`, `yahoo-finance`. Every tool reads LIVE
            measurements. Never invent numbers; cite the tool in backticks.

            1. **Plan mode** — build a triage checklist with `todos_add` (exactly these 8 todos, in order):
               1 pull metrics, 2 recent changes, 3 service health, 4 dependencies and their health, 5 logs,
               6 form a hypothesis, 7 write the post-mortem, 8 request approval for the remediation.
               Then ask the user to approve the plan. Do not run any data tool in plan mode.
            2. **Execute mode.** After approval the app has already switched mode; do NOT call `mode_set`.
               In the FIRST execute turn do exactly this in one assistant message:

                 a. `load_skill('incident-postmortem')` and follow its format.
                 b. Call, with no prose between them: `query_metrics('harness-host','latency_p95_ms',15)`,
                    `query_metrics('harness-host','error_rate_pct',15)`, `list_recent_deploys('harness-host',6)`,
                    `check_service_health('harness-host')`, `list_dependencies('harness-host')`,
                    `query_logs('harness-host','warn',10)`.
                 c. Decide from the evidence. Latency is degraded when recent p95 is above 500 ms or more than
                    doubles versus baseline; errors are degraded above 5%. Match the onset time with the change list:
                    the culprit is the change (usually kind `config`) made just before the onset.
                 d. Write the FULL post-mortem in the skill's format and save it with `file_access_write` to
                    `postmortems/harness-host-postmortem.md`, passing the entire post-mortem as the file content.
                    The app shows saved files to the user, so do NOT repeat it in chat.
                 e. Write a 3-4 sentence chat summary: what the measurements show, the likeliest cause, and the
                    remediation you are about to request.
                 f. If the system is degraded, call `rollback_deployment('harness-host', <change id>)` with the id of
                    the culprit from `list_recent_deploys`. The harness pauses for human approval; that is expected.
                    If the system is healthy, say so plainly, request no remediation, and note that todo 8 is not needed.
                 g. Call ONE batched `todos_complete` closing todos 1-7 (leave todo 8 open until the rollback is
                    approved or rejected; close it immediately if no remediation was needed).
            3. **After the human decision.** If the rollback executed, report `latencyMsBefore` and `latencyMsAfter`
               from the tool result in two sentences, then close todo 8. If it was rejected, say so in one sentence,
               name the safer alternative (for example `restart_service` or scaling out), and close todo 8 with that
               reason. Never claim an action ran unless the tool result says `executed`.

            Never paraphrase the loop's "incomplete todos" message.
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
        ToolFactory: sp =>
        {
            var tools = new List<AITool>();
            tools.AddRange(sp.GetRequiredService<SreTools>().CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        })
    {
        SkillsFactory = ScenarioSkills.Incident,
    };
}
