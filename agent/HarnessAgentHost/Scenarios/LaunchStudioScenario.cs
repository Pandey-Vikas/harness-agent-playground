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
            2. **Execute mode** — after approval, `mode_set('execute')` and IMMEDIATELY start calling tools.
               For every open todo, CALL THE MATCHING TOOL BEFORE writing any prose. Use
               `competitor_scan` → `keyword_trends` → `generate_persona` (twice, once per segment) →
               `suggest_channels` → `estimate_reach` → `draft_copy` (2-3 voices).
               Right after each tool returns, call `todos_complete` for the id it satisfies.

               For SYNTHESIS todos (positioning, calendar, brief): fold them TOGETHER into the single
               final launch brief below, then close their todos in one `todos_complete` batch.

               **The user only sees ONE assistant output: your final launch brief.** Do not narrate progress,
               do not label sections "### 1)" or "Todo #X complete", do not restate the checklist,
               do not paraphrase the loop's re-injection message. Silence between tool calls is fine.

               **Final brief format** (this is your entire visible output):

               ```
               # Launch Brief — {PRODUCT} in {MARKET}

               **DEMO DATA — fabricated research.**

               ## Audience personas
               - Primary: (from generate_persona #1)
               - Secondary: (from generate_persona #2)

               ## Positioning statement
               (1-2 sentences.)

               ## Channel mix (4-week budget split)
               | Channel | % | Est. reach |
               |---|---|---|
               | ... | ... | ... |

               ## 4-week content calendar
               - Week 1: ...
               - Week 2: ...
               - Week 3: ...
               - Week 4: ...

               ## Sample copy
               (Top 2 channels, 2-3 voices each, from draft_copy.)

               ## Success metrics
               - 3-4 KPIs.
               ```

               `estimate_reach` MUST be called at least once.
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
