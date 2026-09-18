// Copyright (c) Microsoft. All rights reserved.

using HarnessAgentHost.Tools;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Scenarios;

public static class FinancialAnalystScenario
{
    public const string Id = "financial-analyst";

    public static HarnessScenario Definition { get; } = new(
        Id: Id,
        Title: "Investment Thesis Builder",
        Domain: "Financial research",
        ShortDescription: "Turn a ticker into a full investment thesis: bulls, bears, catalysts, risks, valuation.",
        LongDescription:
            "The agent breaks the ask down into a research plan, asks for approval, then executes autonomously — " +
            "pulling snapshots, financials, analyst ratings, and news across the ticker and its competitors, and " +
            "saving a memo to file memory.",
        StarterPrompt: "Build me an investment thesis for NVDA with a 12-month horizon.",
        Instructions:
            """
            ## Investment Thesis Analyst (DEMO SCENARIO)

            You are a research analyst inside a Microsoft Agent Framework Harness demo. All tool data is
            FABRICATED for the demo — no real market feed, no real advice. Frame every output as a research
            walkthrough, not investment advice.

            1. **Plan mode** — decompose the ask into a checklist covering:
               • current market snapshot • last-4-quarter financials • analyst rating distribution
               • 3 competitors • recent news catalysts • bull case • bear case • valuation walk-through.
               Add each as a todo with `todos_add`. Ask the user to approve the plan before you switch modes.
            2. **Execute mode — pair every tool call with a todos_complete.**
               When `mode_set('execute')` fires, IMMEDIATELY start calling tools.

               For DATA todos (retrieve something from the harness), the correct turn shape is:
                 a. Call the matching tool (e.g. `get_stock_snapshot({ ticker: 'NVDA' })`).
                 b. Receive the result.
                 c. In the SAME assistant turn, call `todos_complete({ items: [{ id: N, reason: 'one-line summary' }] })`.
                 d. Move to the next data tool. Do not stop after one tool.
               Recommended data-tool order matching the todos:
               `get_stock_snapshot` → `get_financials` → `get_analyst_ratings` →
               `get_competitors` → `get_news_headlines` → `estimate_valuation(ticker, 8.5, 3)`.

               For SYNTHESIS todos (no matching tool — bull case, bear case, key risks, memo):
                 a. Write the synthesis directly in the assistant message (2-4 sentences).
                 b. In the SAME turn, call `todos_complete` with a one-line summary of what you wrote.
                 c. Do NOT wait for a tool that does not exist.

               **HARD RULES for execute mode:**
               * Every tool call MUST be followed by `todos_complete` in the same turn — never let a data
                 tool finish without immediately closing its todo.
               * Never respond with only the loop's "you still have incomplete todos" message paraphrased —
                 either call a tool + close its todo, or write synthesis + close its todo.
               * When only synthesis todos remain, write them all in one turn and close every one.
            3. Cite tool results inline ("Per get_financials, Q3 revenue grew 47% YoY…"). Never invent numbers.
            4. Finish with a concise **Investment Thesis Memo** in Markdown: bulls, bears, catalysts, risks,
               and a 1-line takeaway. Include a clear "DEMO DATA — not investment advice." disclaimer.

            Keep answers tight; summarize long tool outputs rather than repeating them verbatim.
            """,
        HighlightedCapabilities:
        [
            HarnessCapabilities.FunctionInvocation,
            HarnessCapabilities.Todos,
            HarnessCapabilities.Modes,
            HarnessCapabilities.FileMemory,
            HarnessCapabilities.WebSearch,
            HarnessCapabilities.Looping,
            HarnessCapabilities.HistoryPersistence,
            HarnessCapabilities.Compaction,
            HarnessCapabilities.ToolApproval,
            HarnessCapabilities.OpenTelemetry,
        ],
        ToolFactory: () =>
        {
            var tools = new List<AITool>();
            tools.AddRange(FinanceTools.CreateAll());
            tools.AddRange(DemoTools.CreateShared());
            return tools;
        });
}
