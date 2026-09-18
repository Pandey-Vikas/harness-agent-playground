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
            2. **Execute mode — pair every tool call with a todos_complete.**
               When `mode_set('execute')` fires, IMMEDIATELY start calling tools.

               For DATA todos (research from the harness), the correct turn shape is:
                 a. Call the matching tool (e.g. `competitor_scan({ product: '...', market: '...' })`).
                 b. Receive the result.
                 c. In the SAME assistant turn, call `todos_complete({ items: [{ id: N, reason: 'one-line summary' }] })`.
                 d. Move to the next data tool.

               **Explicit tool → todo mapping** you should keep to (adapt ids to the real plan):
                 * `competitor_scan`             → closes the "competitor scan" todo.
                 * `keyword_trends`              → closes the "keyword trends" todo.
                 * `generate_persona` call #1    → closes persona todo #1 (primary segment).
                 * `generate_persona` call #2    → closes persona todo #2 (secondary segment).
                 * `suggest_channels` + `estimate_reach` (call both) → closes the "channel mix" todo.
                 * `draft_copy` (2-3 voices)     → closes the "sample copy" todo.

               For SYNTHESIS todos (positioning statement, 4-week content calendar, final launch brief —
               no matching tool):
                 a. Write the synthesis directly in the assistant message.
                 b. In the SAME turn, call `todos_complete` with a one-line summary.
                 c. Do NOT wait for a tool that does not exist.

               **HARD RULES:**
               * Every tool call MUST be followed by `todos_complete` in the same turn — do NOT batch tool
                 calls and then close their todos in a later turn.
               * Never paraphrase the loop's "incomplete todos" message — act on the todos instead.
               * `estimate_reach` MUST be called at least once (partners need to see the reach math).
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
