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
            2. **Execute mode — tool-first, always.**
               When `mode_set('execute')` fires, IMMEDIATELY call a tool. Do NOT write prose first.
               Recommended tool order matching the todos:
               `get_stock_snapshot(ticker)` → `get_financials(ticker)` → `get_analyst_ratings(ticker)` →
               `get_competitors(ticker)` → `get_news_headlines(ticker)` → `estimate_valuation(ticker, 8.5, 3)`.
               After each tool result, call `todos_complete` for the id it satisfies, then invoke the next tool.

               **HARD RULES for execute mode:**
               * Every assistant turn MUST include at least one tool call while todos are open.
               * A text-only response in execute mode is a failure — do not do it.
               * Do NOT paraphrase or restate the todo list. Just call tools.
               * Only after every todo is complete, write the final memo.
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
