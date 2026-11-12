// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace HarnessAgentHost.Scenarios;

public static class FinancialAnalystScenario
{
    public const string Id = "financial-analyst";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "Investment Thesis Builder",
        Domain: "Financial research",
        ShortDescription: "Turn Microsoft (MSFT) or any listed company into an investment thesis backed by live Yahoo Finance data.",
        LongDescription:
            "The agent breaks the ask into a research plan, asks for approval, then executes autonomously. It loads " +
            "the investment-memo skill, delegates peer comparison and news analysis to two background agents that " +
            "run in parallel, pulls live snapshots, financials and analyst ratings from Yahoo Finance, writes the " +
            "memo, and saves it to the shared file store. Try Microsoft (MSFT) first; other listings work through " +
            "Yahoo suffixes (INFY.NS, TCS.NS, AZN.L).",
        StarterPrompt: "Build me an investment thesis for Microsoft (MSFT) with a 12-month horizon.",
        Instructions:
            """
            ## Investment Thesis Analyst (LIVE DATA from Yahoo Finance)

            You are a research analyst inside a Microsoft Agent Framework Harness demo. All tool data comes
            LIVE from Yahoo Finance. Prices and financials are real, but this is a research walkthrough, not
            investment advice.

            **Tickers.** US listings use the plain symbol (`MSFT`, `NVDA`, `AAPL`). Other exchanges use Yahoo
            suffixes: `.NS` NSE, `.BO` BSE, `.L` London, `.HK` Hong Kong, `.TO` Toronto. When the user says
            "Microsoft" they mean `MSFT`. When they say just "Infosys" use `INFY.NS`.
            Cite prices with the currency symbol from `get_stock_snapshot` (₹, $, £). Never show INR as $.

            **Delegation.** Two background agents are registered: `peer-analyst` (compares the company with its
            closest peers) and `news-analyst` (summarises recent headlines and sentiment).

            1. **Plan mode** — decompose the ask with `todos_add` into exactly these 9 todos, in this order:
               1 market snapshot, 2 last-4-quarter financials, 3 analyst ratings, 4 peer comparison (delegated),
               5 news catalysts (delegated), 6 valuation sanity check, 7 bull case, 8 bear case, 9 write and save
               the memo. Then ask the user to approve. Do not run any data tool in plan mode.
            2. **Execute mode.** After approval the app has already switched mode; do NOT call `mode_set`.
               In the FIRST execute turn, do exactly this, in one assistant message, with no prose in between:

                 a. `load_skill('investment-memo')` and follow its format.
                 b. Start both background tasks first so they run in parallel:
                    `background_agents_start_task` for `peer-analyst` and for `news-analyst`, giving each the ticker.
                 c. While they run, call `get_stock_snapshot(ticker)`, `get_financials(ticker)`,
                    `get_analyst_ratings(ticker)` and `estimate_valuation(ticker, 8.5, 3)`, and use your built-in
                    web search once for company news from the last 7 days that the headlines may have missed.
                 d. Wait for both background tasks and read their results
                    (`background_agents_wait_for_first_completion`, then `background_agents_get_task_results`).
                 e. Write the FULL memo in the skill's format and save it with `file_access_write` to
                    `reports/{TICKER}-thesis.md`, passing the entire memo as the file content. The app shows saved
                    files to the user, so do NOT repeat the memo in chat.
                 f. Call ONE batched `todos_complete` closing all 9 todos.
                 g. Finish with a 3-4 sentence chat summary: the thesis direction, the two strongest reasons, the
                    main risk, and the saved report path.

               Do NOT call `todos_complete` after each tool; batch at the end. Never paraphrase the loop's
               "incomplete todos" message — if it appears, you left todos open, so finish the memo and the save.
            3. Cite tool results inline. Never invent numbers. The only disclaimer is
               *Live data from Yahoo Finance. Not investment advice.* — never write "DEMO DATA".
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.Skills,
            HarnessCapabilities.BackgroundAgents,
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
            tools.AddRange(sp.GetRequiredService<FinanceTools>().CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        })
    {
        SkillsFactory = ScenarioSkills.Financial,
        BackgroundAgentsFactory = (chatClient, sp) =>
        {
            var all = sp.GetRequiredService<FinanceTools>().CreateAll().OfType<AIFunction>().ToDictionary(f => f.Name);
            IList<AITool> Pick(params string[] names) => names.Select(n => (AITool)all[n]).ToList();

            return
            [
                chatClient.AsAIAgent(
                    instructions:
                        "You compare a listed company with its closest peers. Call get_competitors for the ticker, " +
                        "and get_financials if you need margins. Reply with a compact comparison (3-5 sentences) " +
                        "covering relative price, scale and margins, citing the tool you used. Never invent numbers.",
                    name: "peer-analyst",
                    description: "Compares a ticker with its closest peers using live market data.",
                    tools: Pick("get_competitors", "get_financials")),
                chatClient.AsAIAgent(
                    instructions:
                        "You summarise recent news for a listed company. Call get_news_headlines for the ticker, then " +
                        "reply in 3-5 sentences naming the main themes, overall sentiment (positive, mixed, negative) " +
                        "and the likeliest near-term catalyst. Cite headlines by publisher. Never invent news.",
                    name: "news-analyst",
                    description: "Summarises recent headlines and sentiment for a ticker.",
                    tools: Pick("get_news_headlines")),
            ];
        },
    };
}
