# Plan: 12-month investment thesis for NVDA (DEMO scenario — fabricated tool data)

## Objective
Build a 12-month horizon investment thesis for NVIDIA (NVDA) using the demo tools. Output will be a research walkthrough, not investment advice.

## Checklist / Todos
1. Current market snapshot (price, day change, 52-week range, market cap) via GetStockSnapshot.
2. Last-4-quarter financials (revenue growth, margins, FCF) via GetFinancials.
3. Analyst rating distribution and 12-month target via GetAnalystRatings.
4. Competitor set (3 tickers) and positioning via GetCompetitors.
5. Recent catalysts (last 7 days) via GetNewsHeadlines.
6. Valuation walkthrough (DCF-like fair value) via EstimateValuation using 8.5% discount rate and 3% terminal growth.
7. Synthesize into memo: bull case, bear case, catalysts, risks, and 1-line takeaway; clearly label DEMO DATA / not investment advice.

## Clarification needed
None required to proceed; will assume USD, common shares, and a general long-only 12-month horizon.
