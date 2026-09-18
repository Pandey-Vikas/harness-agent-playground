// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Live finance tools backed by Yahoo Finance. Every tool returns a JSON string the model can cite by name.
/// Supports US listings (e.g. NVDA) and international listings via Yahoo's suffix convention (e.g. INFY.NS for NSE).
/// Falls back to a structured error object when a live call fails, so the model can still reason about the gap.
/// </summary>
public sealed class FinanceTools
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly YahooFinanceClient _yahoo;
    private readonly ILogger<FinanceTools> _log;

    public FinanceTools(YahooFinanceClient yahoo, ILogger<FinanceTools> log)
    {
        this._yahoo = yahoo;
        this._log = log;
    }

    public IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(this.GetStockSnapshotAsync, new AIFunctionFactoryOptions { Name = "get_stock_snapshot" }),
        AIFunctionFactory.Create(this.GetFinancialsAsync,    new AIFunctionFactoryOptions { Name = "get_financials" }),
        AIFunctionFactory.Create(this.GetAnalystRatingsAsync, new AIFunctionFactoryOptions { Name = "get_analyst_ratings" }),
        AIFunctionFactory.Create(this.GetCompetitorsAsync,   new AIFunctionFactoryOptions { Name = "get_competitors" }),
        AIFunctionFactory.Create(this.GetNewsHeadlinesAsync, new AIFunctionFactoryOptions { Name = "get_news_headlines" }),
        AIFunctionFactory.Create(this.EstimateValuationAsync,new AIFunctionFactoryOptions { Name = "estimate_valuation" }),
    ];

    [Description("Returns a real-time-ish price snapshot from Yahoo Finance: price, day change, 52-week range, exchange, currency, long name. " +
                 "Use plain symbols for US listings (e.g. 'NVDA', 'MSFT') and Yahoo-style suffixes for international listings (e.g. 'INFY.NS' for NSE, 'INFY.BO' for BSE, 'TCS.NS').")]
    public async Task<string> GetStockSnapshotAsync(
        [Description("Yahoo Finance ticker. US: 'NVDA'. NSE: 'INFY.NS'. BSE: 'INFY.BO'. LSE: 'AZN.L'.")] string ticker,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var s = await this._yahoo.GetSnapshotAsync(ticker, cancellationToken);
            var change = s.PreviousClose > 0 ? Math.Round((s.Price - s.PreviousClose) / s.PreviousClose * 100.0, 2) : 0.0;
            return Json(new
            {
                ticker = s.Symbol,
                longName = s.LongName,
                exchange = s.Exchange,
                currency = s.Currency,
                price = Math.Round(s.Price, 2),
                previousClose = Math.Round(s.PreviousClose, 2),
                dayChangePct = change,
                fiftyTwoWeekLow = Math.Round(s.FiftyTwoWeekLow, 2),
                fiftyTwoWeekHigh = Math.Round(s.FiftyTwoWeekHigh, 2),
                source = "Yahoo Finance (v8 chart)"
            });
        }
        catch (Exception ex) { return LiveError(nameof(GetStockSnapshotAsync), ticker, ex); }
    }

    [Description("Returns live financials from Yahoo Finance: market cap, trailing PE, revenue growth, gross/operating/profit margins, free cash flow, total cash, total debt, last 4 quarters of revenue.")]
    public async Task<string> GetFinancialsAsync(
        [Description("Yahoo Finance ticker.")] string ticker,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await this._yahoo.GetQuoteSummaryAsync(ticker,
                "defaultKeyStatistics,financialData,summaryDetail,incomeStatementHistoryQuarterly",
                cancellationToken);
            if (doc is null) return LiveErrorText(nameof(GetFinancialsAsync), ticker, "QuoteSummary auth failed. See server logs.");
            var result = doc.RootElement.GetProperty("quoteSummary").GetProperty("result");
            if (result.GetArrayLength() == 0) return LiveErrorText(nameof(GetFinancialsAsync), ticker, "No data returned.");
            var root = result[0];

            var keyStats = root.TryGetProperty("defaultKeyStatistics", out var ks) ? ks : default;
            var fin = root.TryGetProperty("financialData", out var fd) ? fd : default;
            var summary = root.TryGetProperty("summaryDetail", out var sd) ? sd : default;

            var quarters = new List<object>();
            if (root.TryGetProperty("incomeStatementHistoryQuarterly", out var isq) &&
                isq.TryGetProperty("incomeStatementHistory", out var hist))
            {
                foreach (var q in hist.EnumerateArray())
                {
                    quarters.Add(new
                    {
                        endDate = Fmt(q, "endDate"),
                        totalRevenue = Fmt(q, "totalRevenue"),
                        grossProfit = Fmt(q, "grossProfit"),
                        operatingIncome = Fmt(q, "operatingIncome"),
                        netIncome = Fmt(q, "netIncome")
                    });
                }
            }

            return Json(new
            {
                ticker,
                marketCap = Fmt(keyStats, "marketCap") ?? Fmt(summary, "marketCap"),
                trailingPE = Fmt(summary, "trailingPE"),
                forwardPE = Fmt(summary, "forwardPE"),
                priceToBook = Fmt(keyStats, "priceToBook"),
                revenueGrowthPct = FmtPct(fin, "revenueGrowth"),
                grossMarginsPct = FmtPct(fin, "grossMargins"),
                operatingMarginsPct = FmtPct(fin, "operatingMargins"),
                profitMarginsPct = FmtPct(fin, "profitMargins"),
                returnOnEquityPct = FmtPct(fin, "returnOnEquity"),
                freeCashflow = Fmt(fin, "freeCashflow"),
                operatingCashflow = Fmt(fin, "operatingCashflow"),
                totalCash = Fmt(fin, "totalCash"),
                totalDebt = Fmt(fin, "totalDebt"),
                revenuePerShare = Fmt(fin, "revenuePerShare"),
                lastFourQuarters = quarters,
                source = "Yahoo Finance (v10 quoteSummary)"
            });
        }
        catch (Exception ex) { return LiveError(nameof(GetFinancialsAsync), ticker, ex); }
    }

    [Description("Returns live analyst rating distribution and mean 12-month price target from Yahoo Finance.")]
    public async Task<string> GetAnalystRatingsAsync(
        [Description("Yahoo Finance ticker.")] string ticker,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await this._yahoo.GetQuoteSummaryAsync(ticker,
                "financialData,recommendationTrend",
                cancellationToken);
            if (doc is null) return LiveErrorText(nameof(GetAnalystRatingsAsync), ticker, "QuoteSummary auth failed.");
            var result = doc.RootElement.GetProperty("quoteSummary").GetProperty("result");
            if (result.GetArrayLength() == 0) return LiveErrorText(nameof(GetAnalystRatingsAsync), ticker, "No data.");
            var root = result[0];

            var fin = root.TryGetProperty("financialData", out var fd) ? fd : default;
            object? latestTrend = null;
            if (root.TryGetProperty("recommendationTrend", out var rt) &&
                rt.TryGetProperty("trend", out var trendArr) && trendArr.GetArrayLength() > 0)
            {
                var latest = trendArr[0];
                latestTrend = new
                {
                    period = TryStr(latest, "period"),
                    strongBuy = TryInt(latest, "strongBuy"),
                    buy = TryInt(latest, "buy"),
                    hold = TryInt(latest, "hold"),
                    sell = TryInt(latest, "sell"),
                    strongSell = TryInt(latest, "strongSell")
                };
            }

            return Json(new
            {
                ticker,
                currentPrice = Fmt(fin, "currentPrice"),
                targetMeanPrice = Fmt(fin, "targetMeanPrice"),
                targetHighPrice = Fmt(fin, "targetHighPrice"),
                targetLowPrice = Fmt(fin, "targetLowPrice"),
                recommendationKey = TryStr(fin, "recommendationKey"),
                recommendationMean = Fmt(fin, "recommendationMean"),
                numberOfAnalystOpinions = TryInt(fin, "numberOfAnalystOpinions"),
                latestRecommendationTrend = latestTrend,
                source = "Yahoo Finance (v10 quoteSummary)"
            });
        }
        catch (Exception ex) { return LiveError(nameof(GetAnalystRatingsAsync), ticker, ex); }
    }

    [Description("Returns competitor tickers via Yahoo's recommendedSymbols endpoint, with a live snapshot for each. Also returns sector + industry.")]
    public async Task<string> GetCompetitorsAsync(
        [Description("Yahoo Finance ticker.")] string ticker,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var profileDoc = await this._yahoo.GetQuoteSummaryAsync(ticker, "assetProfile", cancellationToken);

            // recommendationsBySymbol is a separate lightweight endpoint that doesn't need crumb.
            var url = $"https://query1.finance.yahoo.com/v6/finance/recommendationsbysymbol/{Uri.EscapeDataString(ticker)}";
            var peerTickers = new List<string>();
            using (var peerReq = new HttpRequestMessage(HttpMethod.Get, url))
            using (var peerResp = await new HttpClient().SendAsync(peerReq, cancellationToken))
            {
                if (peerResp.IsSuccessStatusCode)
                {
                    var pbody = await peerResp.Content.ReadAsStringAsync(cancellationToken);
                    using var pdoc = JsonDocument.Parse(pbody);
                    if (pdoc.RootElement.TryGetProperty("finance", out var f) &&
                        f.TryGetProperty("result", out var r) && r.GetArrayLength() > 0 &&
                        r[0].TryGetProperty("recommendedSymbols", out var recs))
                    {
                        foreach (var rec in recs.EnumerateArray())
                        {
                            if (rec.TryGetProperty("symbol", out var sy)) peerTickers.Add(sy.GetString() ?? "");
                            if (peerTickers.Count >= 4) break;
                        }
                    }
                }
            }

            var peerData = new List<object>();
            foreach (var p in peerTickers.Where(t => !string.IsNullOrEmpty(t)))
            {
                try
                {
                    var s = await this._yahoo.GetSnapshotAsync(p, cancellationToken);
                    peerData.Add(new { ticker = s.Symbol, name = s.LongName, price = Math.Round(s.Price, 2), currency = s.Currency });
                }
                catch { /* skip failed peer */ }
            }

            string? sector = null, industry = null;
            if (profileDoc is not null)
            {
                var r = profileDoc.RootElement.GetProperty("quoteSummary").GetProperty("result");
                if (r.GetArrayLength() > 0 && r[0].TryGetProperty("assetProfile", out var ap))
                {
                    sector = TryStr(ap, "sector");
                    industry = TryStr(ap, "industry");
                }
            }

            return Json(new
            {
                ticker,
                sector,
                industry,
                competitors = peerData,
                note = peerData.Count == 0 ? "Yahoo did not return recommended symbols for this ticker." : null,
                source = "Yahoo Finance (v6 recommendationsBySymbol + v8 chart)"
            });
        }
        catch (Exception ex) { return LiveError(nameof(GetCompetitorsAsync), ticker, ex); }
    }

    [Description("Returns recent live news headlines for a ticker (title, publisher, hours-ago, link) from Yahoo Finance search.")]
    public async Task<string> GetNewsHeadlinesAsync(
        [Description("Yahoo Finance ticker.")] string ticker,
        [Description("Max headlines to return (default 6).")] int limit = 6,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Yahoo's news search often returns nothing for non-US tickers. Falling back to the company's
            // long name (from the snapshot) gets real coverage for Indian, UK, HK etc. listings.
            var news = await this._yahoo.GetNewsAsync(ticker, limit, cancellationToken);
            if (news.Count == 0)
            {
                try
                {
                    var snap = await this._yahoo.GetSnapshotAsync(ticker, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(snap.LongName))
                        news = await this._yahoo.GetNewsAsync(snap.LongName, limit, cancellationToken);
                }
                catch { /* keep empty */ }
            }
            return Json(new
            {
                ticker,
                headlines = news.Select(n => new
                {
                    title = n.Title,
                    publisher = n.Publisher,
                    hoursAgo = Math.Round((DateTime.UtcNow - n.PublishedUtc).TotalHours, 1),
                    publishedUtc = n.PublishedUtc.ToString("O"),
                    link = n.Link
                }),
                source = "Yahoo Finance (v1 search)"
            });
        }
        catch (Exception ex) { return LiveError(nameof(GetNewsHeadlinesAsync), ticker, ex); }
    }

    [Description("Runs a Gordon-growth DCF-style fair value estimate using the ticker's actual free cash flow and shares outstanding from Yahoo, at the supplied discount rate and terminal growth rate.")]
    public async Task<string> EstimateValuationAsync(
        [Description("Yahoo Finance ticker.")] string ticker,
        [Description("Discount rate in percent, e.g. 8.5.")] double discountRatePct,
        [Description("Terminal growth rate in percent, e.g. 3.0.")] double terminalGrowthPct,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await this._yahoo.GetQuoteSummaryAsync(ticker,
                "financialData,defaultKeyStatistics,summaryDetail",
                cancellationToken);
            var snap = await this._yahoo.GetSnapshotAsync(ticker, cancellationToken);

            double? fcf = null, marketCap = null, sharesOutstanding = null;
            if (doc is not null)
            {
                var result = doc.RootElement.GetProperty("quoteSummary").GetProperty("result");
                if (result.GetArrayLength() > 0)
                {
                    var root = result[0];
                    if (root.TryGetProperty("financialData", out var fd))
                        fcf = TryRawDouble(fd, "freeCashflow");
                    if (root.TryGetProperty("defaultKeyStatistics", out var ks))
                    {
                        marketCap = TryRawDouble(ks, "marketCap");
                        sharesOutstanding = TryRawDouble(ks, "sharesOutstanding");
                    }
                    if (marketCap is null && root.TryGetProperty("summaryDetail", out var sum))
                        marketCap = TryRawDouble(sum, "marketCap");
                }
            }

            if (fcf is null || fcf <= 0)
                return LiveErrorText(nameof(EstimateValuationAsync), ticker, "Free cash flow not available on Yahoo for this ticker — cannot run DCF.");

            var g = terminalGrowthPct / 100.0;
            var r = discountRatePct / 100.0;
            if (r <= g)
                return LiveErrorText(nameof(EstimateValuationAsync), ticker, "Discount rate must exceed terminal growth rate.");

            // Gordon-growth on trailing FCF gives an equity/enterprise value.
            var fairEnterpriseValue = fcf.Value * (1 + g) / (r - g);
            var fairPricePerShare = sharesOutstanding is > 0 ? fairEnterpriseValue / sharesOutstanding.Value : (double?)null;

            // Sanity check: if the DCF-implied per-share value is more than 20x or less than 1/20 of the
            // market price, the Yahoo FCF is almost certainly a period (quarterly) value that needs annualising,
            // OR the shares-outstanding field is in a different unit. Flag that instead of pretending the number is meaningful.
            string? sanityWarning = null;
            if (fairPricePerShare is double fps && snap.Price > 0)
            {
                var ratio = fps / snap.Price;
                if (ratio > 20 || ratio < 0.05)
                    sanityWarning = "DCF output is more than 20x off from market price — Yahoo's free cash flow field is likely period-scoped (e.g. quarterly) for this ticker, so the Gordon-growth output should not be read as a target. Treat as diagnostic only.";
            }

            return Json(new
            {
                ticker,
                assumptions = new { discountRatePct, terminalGrowthPct },
                inputs = new { freeCashflow = fcf, marketCap, sharesOutstanding, currentPrice = snap.Price, currency = snap.Currency },
                dcf = new
                {
                    fairEnterpriseValue = Math.Round(fairEnterpriseValue, 0),
                    fairPricePerShare = fairPricePerShare is null ? (double?)null : Math.Round(fairPricePerShare.Value, 2),
                    impliedUpsideVsPricePct = fairPricePerShare is null || snap.Price <= 0
                        ? (double?)null
                        : Math.Round((fairPricePerShare.Value / snap.Price - 1) * 100, 1)
                },
                sanityWarning,
                caveat = "Single-stage Gordon-growth DCF using trailing FCF. Real valuation would model multi-year cash flows, capex, working capital, WACC by scenario.",
                source = "Yahoo Finance financials + DCF math"
            });
        }
        catch (Exception ex) { return LiveError(nameof(EstimateValuationAsync), ticker, ex); }
    }

    private string LiveError(string method, string ticker, Exception ex)
    {
        this._log.LogWarning(ex, "{Method} failed for {Ticker}", method, ticker);
        return Json(new
        {
            ticker,
            error = ex.Message,
            hint = "For international stocks add the Yahoo suffix: '.NS' (NSE), '.BO' (BSE), '.L' (LSE), '.TO' (TSX), '.HK' (HKEX)."
        });
    }

    private static string LiveErrorText(string method, string ticker, string message) =>
        Json(new { ticker, method, error = message });

    private static string Json(object o) => JsonSerializer.Serialize(o, s_json);

    // Yahoo's quoteSummary returns { raw, fmt, longFmt } for numeric fields. Fmt = pretty string, Raw = number.
    private static string? Fmt(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return v.TryGetProperty("fmt", out var fmt) && fmt.ValueKind == JsonValueKind.String ? fmt.GetString() : null;
    }
    private static double? TryRawDouble(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var direct)) return direct;
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Number && raw.TryGetDouble(out var d)) return d;
        return null;
    }
    private static string? FmtPct(JsonElement el, string prop)
    {
        var raw = TryRawDouble(el, prop);
        return raw is null ? null : Math.Round(raw.Value * 100.0, 2).ToString("0.##") + "%";
    }
    private static string? TryStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? TryInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
