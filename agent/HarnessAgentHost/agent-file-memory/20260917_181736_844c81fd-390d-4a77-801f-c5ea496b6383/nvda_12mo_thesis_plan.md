# NVDA 12-month Investment Thesis — Plan (DEMO)

## Objective
Build a 12-month investment thesis for NVDA using the demo tool data (fabricated), covering market snapshot, financial trend, Street view, competitors, catalysts, bull/bear cases, and a simple valuation walkthrough.

## Checklist / Todos
1. Current market snapshot (price, 1d move, 52-week range, market cap)
2. Last 4 quarters financials (revenue, YoY growth, gross & operating margin, FCF)
3. Analyst rating distribution + 12-month target
4. Competitors (3–5) + positioning
5. Recent news catalysts (last ~7 days)
6. Bull case (12-month)
7. Bear case (12-month)
8. Valuation walkthrough (DCF-like): discount rate 8.5%, terminal growth 3.0%
9. Final memo (Markdown): bulls/bears/catalysts/risks + 1-line takeaway + DEMO disclaimer

## Tool order in Execute mode
GetStockSnapshot → GetFinancials → GetAnalystRatings → GetCompetitors → GetNewsHeadlines → EstimateValuation.

## Output format
Concise Markdown memo with explicit reference to tool outputs and a prominent disclaimer: “DEMO DATA — not investment advice.”
