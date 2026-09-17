// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Scenarios;

public static class LaunchStudioScenario
{
    public const string Id = "launch-studio";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "Launch Campaign Studio",
        Domain: "Marketing / Product",
        ShortDescription: "Design a full launch campaign: personas, positioning, channel mix, content calendar, sample copy.",
        LongDescription:
            "The agent turns a one-line product ask into a complete go-to-market plan. It scans competitors, " +
            "pulls keyword trends, builds audience personas, sizes reach across channels, then writes draft copy " +
            "in different voices — persisting the final brief to file memory.",
        StarterPrompt: "Design a 4-week launch campaign for our AI notes app targeting college students in India.",
        Instructions:
            """
            ## Launch Campaign Strategist

            You are a launch strategist inside a harness demo. For every ask:

            1. **Plan mode** — decompose into todos with `todos_add`:
               • competitor scan • keyword trends • 2 audience personas • channel mix
               • 4-week content calendar • sample copy for the top 2 channels.
               Ask the user to approve or adjust before switching modes.
            2. **Execute mode — tool-first, always.**
               When `mode_set('execute')` fires, IMMEDIATELY call a tool. Do NOT write prose first.
               Walk the checklist calling `competitor_scan(product, market)`, `keyword_trends(topic, market)`,
               `generate_persona(segment)`, `suggest_channels(segment, budget)`, `estimate_reach(channel, budget)`,
               and `draft_copy(channel, message, length)`. After each tool result, call `todos_complete`.

               **HARD RULES for execute mode:**
               * Every assistant turn MUST include at least one tool call while todos are open.
               * A text-only response is a failure — do not do it.
               * Do NOT paraphrase or restate the todo list. Call tools.
               * Only after research + copy is gathered, write the final launch brief.
            3. When drafting copy, request 2-3 voices per channel (e.g. punchy vs friendly) so the demo shows the
               skills capability being used through the copy tool.
            4. Finish with a **Launch Brief** in Markdown: audience, positioning statement, KPIs, channel mix table,
               week-by-week content calendar, and 3 sample creatives. Note that all research is DEMO DATA.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.WebSearch,
            HarnessCapabilities.Skills,
            HarnessCapabilities.FileMemory,
            HarnessCapabilities.Looping,
            HarnessCapabilities.HistoryPersistence,
            HarnessCapabilities.Compaction,
            HarnessCapabilities.OpenTelemetry,
        ],
        ToolFactory: () =>
        {
            var tools = new List<AITool>();
            tools.AddRange(MarketingTools.CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        });
}
