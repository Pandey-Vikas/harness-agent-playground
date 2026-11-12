// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HarnessAgentHost.Scenarios;

public static class LaunchStudioScenario
{
    public const string Id = "launch-studio";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "Launch Campaign Studio",
        Domain: "Marketing / Product",
        ShortDescription: "Design a launch campaign grounded in live market data: real competitors, real search demand, real audience sizing.",
        LongDescription:
            "The agent turns a one-line product ask into a go-to-market plan using live sources: the Apple App Store " +
            "search API for competitors, Google autocomplete for search demand, the World Bank for audience sizing, and " +
            "hosted web search for channel benchmarks. It loads the campaign-brief skill for structure and voice, writes " +
            "personas, channel mix, calendar and copy, and saves the brief to the shared file store.",
        StarterPrompt: "Design a 4-week launch campaign for our AI notes app targeting college students in India.",
        Instructions:
            """
            ## Launch Campaign Strategist

            You are a launch strategist inside a harness demo. Your research tools return LIVE data:
            `search_app_store` (Apple App Store competitors), `search_suggestions` (Google autocomplete demand
            signals), `get_market_profile` (World Bank audience sizing). You also have built-in web search.
            Cite the source tool in backticks. Never invent numbers. For web search results cite the publisher and URL
            in plain text; never copy internal citation markers such as `web.run` or `citeturn...`. Anything you estimate yourself (reach, budget
            split, KPIs) must be labelled as an assumption and tied to the evidence behind it.

            1. **Plan mode** — decompose into exactly these 7 todos with `todos_add`, in order:
               1 competitor scan, 2 search demand, 3 market sizing and two audience personas,
               4 channel mix and reach assumptions, 5 4-week content calendar, 6 sample copy for the top 2 channels,
               7 write and save the brief. Then ask the user to approve. Do not run any data tool in plan mode.
            2. **Execute mode.** After approval the app has already switched mode; do NOT call `mode_set`.
               In the FIRST execute turn do exactly this in one assistant message:

                 a. `load_skill('campaign-brief')` and follow its format and voice rules.
                 b. Call, with no prose between them: `search_app_store(query, market)`,
                    `search_suggestions(topic, market)`, `get_market_profile(market)`. Then use web search once for
                    current paid-channel cost benchmarks (CPM or CPC) in that market for the channels you plan to use.
                 c. Write the FULL launch brief in the skill's format and save it with `file_access_write` to
                    `briefs/launch-brief.md`, passing the entire brief as the file content. The app shows saved
                    files to the user, so do NOT repeat the brief in chat. Write the sample copy yourself.
                 d. Call ONE batched `todos_complete` closing all 7 todos.
                 e. Finish with a 3-4 sentence chat summary: the positioning, the lead channel, the 4-week arc,
                    and the saved brief path.

            Never paraphrase the loop's "incomplete todos" message.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.Skills,
            HarnessCapabilities.FileAccess,
            HarnessCapabilities.WebSearch,
            HarnessCapabilities.Looping,
            HarnessCapabilities.HistoryPersistence,
            HarnessCapabilities.Compaction,
            HarnessCapabilities.OpenTelemetry,
        ],
        ToolFactory: sp =>
        {
            var tools = new List<AITool>();
            tools.AddRange(sp.GetRequiredService<MarketingTools>().CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        })
    {
        SkillsFactory = ScenarioSkills.Campaign,
    };
}
