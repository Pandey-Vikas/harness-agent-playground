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
            2. **Execute mode — Copilot-style single response.**
               After approval, `mode_set('execute')` is already handled by the app — do NOT call it yourself.

               In the FIRST execute turn, do this exact sequence, all in one assistant message:

                 a. Call the 6 data tools in this order (no prose between them):
                    `get_stock_snapshot(ticker)` → `get_financials(ticker)` → `get_analyst_ratings(ticker)` →
                    `get_competitors(ticker)` → `get_news_headlines(ticker)` → `estimate_valuation(ticker, 8.5, 3)`.

                 b. Then WRITE THE FULL MEMO IN THE SAME MESSAGE (the format is below).
                    This is the ONLY prose the user will see.

                 c. THEN call one single batched `todos_complete` closing ALL 9 todos in one call:
                    `todos_complete({items:[{id:1,reason:'snapshot'},{id:2,reason:'financials'},{id:3,reason:'ratings'},{id:4,reason:'competitors'},{id:5,reason:'headlines'},{id:6,reason:'valuation'},{id:7,reason:'bull case in memo'},{id:8,reason:'bear case in memo'},{id:9,reason:'memo written'}]})`.

               **Do NOT call todos_complete after each tool** — batch them all at the end. This is critical
               to prevent turn-boundary issues.

               **Writing style — read like a Copilot response, not a bulleted checklist.**
               * Open with a short **Overview** paragraph (3-4 sentences) setting price/scale, what the tools
                 showed, and the thesis direction.
               * Bull case and Bear case are **short paragraphs** (1-2 each), NOT bullet lists. Weave numbers
                 into sentences naturally: *"Revenue held above $16B across the last four quarters (GetFinancials),
                 with growth decelerating from 43.8% to 7% YoY."*
               * Catalysts and Key risks get a one-sentence lead-in then 3-4 simple bullets — no bold labels,
                 no colon-prefixed keywords.
               * End with **Bottom line** — 1-2 sentences.

               **Never paraphrase the loop's "incomplete todos" message.** If the loop re-injects, it means
               you left todos open — go back and write the memo.

               **Final memo format:**

               ```
               # Investment Thesis Memo — {TICKER} (12-month horizon)

               *DEMO DATA — not investment advice.*

               ## Overview
               (3-4 sentence paragraph.)

               ## The bull case
               (1-2 short paragraphs. No bullets. Cite tool names inline.)

               ## The bear case
               (1-2 short paragraphs. No bullets.)

               ## Catalysts to watch
               One-sentence lead-in.
               - short bullet
               - short bullet
               - short bullet

               ## Key risks
               One-sentence lead-in.
               - short bullet
               - short bullet
               - short bullet

               ## Bottom line
               (1-2 sentences.)
               ```
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
