// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Demo finance tools. All data is fabricated but deterministic per input (seeded RNG),
/// so the model gets consistent answers across turns without a real market feed.
/// </summary>
public static class FinanceTools
{
    public static IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(GetStockSnapshot),
        AIFunctionFactory.Create(GetFinancials),
        AIFunctionFactory.Create(GetAnalystRatings),
        AIFunctionFactory.Create(GetNewsHeadlines),
        AIFunctionFactory.Create(GetCompetitors),
        AIFunctionFactory.Create(EstimateValuation),
    ];

    [Description("Returns a fabricated real-time-like price snapshot for a public ticker (price, day change, 52-week range, market cap).")]
    public static string GetStockSnapshot(
        [Description("Stock ticker symbol, e.g. 'MSFT' or 'NVDA'.")] string ticker)
    {
        var rng = SeededRng(ticker);
        var price = Math.Round(20 + rng.NextDouble() * 900, 2);
        var change = Math.Round((rng.NextDouble() - 0.5) * 6, 2);
        var low52 = Math.Round(price * (0.55 + rng.NextDouble() * 0.2), 2);
        var high52 = Math.Round(price * (1.1 + rng.NextDouble() * 0.4), 2);
        var capBillions = Math.Round(price * (rng.Next(150, 8000) / 100.0), 1);
        return JsonSerializer.Serialize(new
        {
            ticker = ticker.ToUpperInvariant(),
            price,
            dayChangePct = change,
            fiftyTwoWeekLow = low52,
            fiftyTwoWeekHigh = high52,
            marketCapBillionsUsd = capBillions,
            note = "DEMO DATA — not a real quote."
        });
    }

    [Description("Returns fabricated last-4-quarter financials (revenue, YoY growth, gross margin, operating margin, FCF).")]
    public static string GetFinancials(
        [Description("Stock ticker symbol.")] string ticker)
    {
        var rng = SeededRng(ticker + ":fin");
        var quarters = new List<object>();
        var revStart = 5 + rng.NextDouble() * 40;
        for (int q = 0; q < 4; q++)
        {
            var rev = Math.Round(revStart * (1 + q * (rng.NextDouble() * 0.1 - 0.02)), 2);
            quarters.Add(new
            {
                period = $"Q{4 - q} 2025",
                revenueBillionsUsd = rev,
                yoyGrowthPct = Math.Round((rng.NextDouble() - 0.2) * 60, 1),
                grossMarginPct = Math.Round(45 + rng.NextDouble() * 40, 1),
                operatingMarginPct = Math.Round(10 + rng.NextDouble() * 40, 1),
                freeCashFlowBillionsUsd = Math.Round(rev * (0.1 + rng.NextDouble() * 0.3), 2)
            });
        }
        return JsonSerializer.Serialize(new
        {
            ticker = ticker.ToUpperInvariant(),
            quarters,
            note = "DEMO DATA — synthetic numbers."
        });
    }

    [Description("Returns fabricated analyst rating distribution (buy / hold / sell counts + 12-month price target).")]
    public static string GetAnalystRatings(
        [Description("Stock ticker symbol.")] string ticker)
    {
        var rng = SeededRng(ticker + ":rat");
        var total = rng.Next(18, 45);
        var buy = rng.Next(total / 3, total - 2);
        var sell = rng.Next(0, Math.Max(1, total - buy - 3));
        var hold = total - buy - sell;
        var target = Math.Round(50 + rng.NextDouble() * 800, 2);
        return JsonSerializer.Serialize(new
        {
            ticker = ticker.ToUpperInvariant(),
            analystCount = total,
            buy, hold, sell,
            twelveMonthTargetUsd = target,
            note = "DEMO DATA."
        });
    }

    [Description("Returns 3-5 fabricated recent news headlines for a ticker within the given window.")]
    public static string GetNewsHeadlines(
        [Description("Stock ticker symbol.")] string ticker,
        [Description("Lookback window in days (default 7).")] int daysBack = 7)
    {
        var rng = SeededRng(ticker + ":news:" + daysBack);
        var templates = new[]
        {
            "{T} unveils new product line, shares react",
            "Analysts upgrade {T} on stronger guidance",
            "{T} faces regulatory review in EU",
            "Insider selling reported at {T}",
            "{T} announces partnership with major cloud vendor",
            "{T} misses top-line estimates; margins beat",
            "Supply chain woes flagged in {T} earnings call",
            "{T} accelerates AI roadmap, hires senior VP",
        };
        var t = ticker.ToUpperInvariant();
        var count = rng.Next(3, 6);
        var headlines = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            var days = rng.Next(0, daysBack + 1);
            headlines.Add(new
            {
                headline = templates[rng.Next(templates.Length)].Replace("{T}", t),
                daysAgo = days,
                source = new[] { "The Wire", "Market Watchdog", "Daily Ticker", "Signal Feed" }[rng.Next(4)]
            });
        }
        return JsonSerializer.Serialize(new { ticker = t, headlines, note = "DEMO DATA." });
    }

    [Description("Returns 3 fabricated competitor tickers and one-line positioning summaries.")]
    public static string GetCompetitors(
        [Description("Stock ticker symbol.")] string ticker)
    {
        var rng = SeededRng(ticker + ":comp");
        var pool = new (string T, string Blurb)[]
        {
            ("MSFT", "Diversified productivity + cloud + AI infra"),
            ("GOOGL", "Search advertising + cloud + Gemini AI stack"),
            ("META", "Social ad network + Reality Labs bets"),
            ("AAPL", "Consumer hardware + services + on-device silicon"),
            ("AMZN", "E-commerce + AWS + advertising"),
            ("NVDA", "AI accelerators + CUDA moat + data center"),
            ("AMD", "GPU + CPU alternative to NVDA/INTC"),
            ("INTC", "Legacy x86 + IFS foundry pivot"),
            ("ORCL", "Enterprise DB + OCI cloud + apps"),
            ("CRM", "SaaS CRM + Data Cloud + Agentforce"),
        };
        var sample = pool.Where(p => !string.Equals(p.T, ticker, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(_ => rng.Next()).Take(3)
                        .Select(p => new { ticker = p.T, positioning = p.Blurb });
        return JsonSerializer.Serialize(new { of = ticker.ToUpperInvariant(), competitors = sample, note = "DEMO DATA." });
    }

    [Description("Returns a fabricated DCF-like fair-value estimate given a discount rate and growth assumption.")]
    public static string EstimateValuation(
        [Description("Stock ticker symbol.")] string ticker,
        [Description("Discount rate in percent, e.g. 8.5.")] double discountRatePct,
        [Description("Terminal growth rate in percent, e.g. 3.0.")] double terminalGrowthPct)
    {
        var rng = SeededRng(ticker + ":val");
        var baseFcf = 5 + rng.NextDouble() * 40;
        var fair = baseFcf * (1 + terminalGrowthPct / 100.0) / Math.Max(0.01, (discountRatePct - terminalGrowthPct) / 100.0);
        var perShare = Math.Round(fair / (rng.Next(150, 3000) / 100.0), 2);
        return JsonSerializer.Serialize(new
        {
            ticker = ticker.ToUpperInvariant(),
            assumedDiscountRatePct = discountRatePct,
            assumedTerminalGrowthPct = terminalGrowthPct,
            fairValuePerShareUsd = perShare,
            note = "DEMO DATA — synthetic DCF."
        });
    }

    private static Random SeededRng(string key)
    {
        int seed = 0;
        foreach (var c in key) seed = unchecked(seed * 31 + c);
        return new Random(seed);
    }
}
