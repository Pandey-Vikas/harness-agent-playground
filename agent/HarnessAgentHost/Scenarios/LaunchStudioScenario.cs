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
            "The agent turns a one-line product ask into a complete go-to-market plan. It loads the campaign-brief " +
            "skill for structure and voice, scans competitors, pulls keyword trends, builds audience personas, sizes " +
            "reach across channels, writes draft copy in different voices, and saves the final brief to the shared " +
            "file store. Research tools return simulated data.",
        StarterPrompt: "Design a 4-week launch campaign for our AI notes app targeting college students in India.",
        Instructions:
            """
            ## Launch Campaign Strategist

            You are a launch strategist inside a harness demo. Research tools return simulated data.

            1. **Plan mode** — decompose into exactly these 7 todos with `todos_add`, in order:
               1 competitor scan, 2 keyword trends, 3 two audience personas, 4 channel mix and reach,
               5 4-week content calendar, 6 sample copy for the top 2 channels, 7 write and save the brief.
               Then ask the user to approve. Do not run any data tool in plan mode.
            2. **Execute mode.** After approval the app has already switched mode; do NOT call `mode_set`.
               In the FIRST execute turn, do exactly this in one assistant message:

                 a. `load_skill('campaign-brief')` and follow its format and voice rules.
                 b. Call, with no prose between them: `competitor_scan(product, market)`,
                    `keyword_trends(topic, market)`, `generate_persona(primary_segment)`,
                    `generate_persona(secondary_segment)`, `suggest_channels(segment, budget)`,
                    `estimate_reach(channel, budget)` at least once, and `draft_copy(channel, message, length)`
                    for 2-3 voices.
                 c. Write the FULL launch brief in the skill's format and save it with `file_access_write` to
                    `briefs/launch-brief.md`, passing the entire brief as the file content. The app shows saved
                    files to the user, so do NOT repeat the brief in chat.
                 d. Call ONE batched `todos_complete` closing all 7 todos.
                 e. Finish with a 3-4 sentence chat summary: the positioning, the lead channel, the 4-week arc,
                    and the saved brief path.

            Never paraphrase the loop's "incomplete todos" message. Do not invent research numbers.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
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
            tools.AddRange(MarketingTools.CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        })
    {
        SkillsFactory = ScenarioSkills.Campaign,
    };
}
