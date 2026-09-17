# NVDA 12-Month Investment Thesis — Plan (DEMO)

## Objective
Build a 12-month horizon investment thesis for NVDA using the demo tools (fabricated data). Output will be a concise memo with bulls, bears, catalysts, risks, and a 1-line takeaway.

## Checklist / Todo-driven workflow
1. Market snapshot (price, day move, 52-week range, market cap) — GetStockSnapshot.
2. Last 4 quarters financials (revenue, YoY growth, margins, FCF) — GetFinancials.
3. Analyst ratings + 12-month price target — GetAnalystRatings.
4. Competitors/peer set and positioning — GetCompetitors.
5. Recent news/catalysts — GetNewsHeadlines.
6. Valuation walk-through (DCF-like) — EstimateValuation (base: 8.5% discount, 3.0% terminal growth; discuss sensitivity).
7. Synthesis into final Investment Thesis Memo (bull case, bear case, catalysts, risks, takeaway; DEMO DATA disclaimer).

## Assumptions / Notes
- All tool outputs are fabricated for a Microsoft Agent Framework demo; not a real market feed.
- 12-month horizon framing: focus on product cycle, hyperscaler capex, AI accelerator competition, software ecosystem lock-in, supply constraints, and regulatory/geopolitical risks.
