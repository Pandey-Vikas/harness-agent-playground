// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Thin client over Yahoo Finance's public endpoints. Chart + Search endpoints work without auth.
/// QuoteSummary needs the cookie/crumb handshake — done lazily and cached for process lifetime.
/// </summary>
public sealed class YahooFinanceClient
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceClient> _log;
    private readonly SemaphoreSlim _crumbLock = new(1, 1);
    private string? _crumb;

    public YahooFinanceClient(HttpClient http, ILogger<YahooFinanceClient> log)
    {
        this._http = http;
        this._log = log;
        if (!this._http.DefaultRequestHeaders.UserAgent.Any())
            this._http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<Snapshot> GetSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?interval=1d&range=1y&includePrePost=false";
        using var doc = await GetJsonAsync(url, ct);
        var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
        return new Snapshot(
            Symbol: TryString(meta, "symbol") ?? symbol.ToUpperInvariant(),
            LongName: TryString(meta, "longName") ?? TryString(meta, "shortName") ?? symbol,
            Currency: TryString(meta, "currency") ?? "USD",
            Exchange: TryString(meta, "exchangeName") ?? TryString(meta, "fullExchangeName") ?? "",
            Price: TryDouble(meta, "regularMarketPrice") ?? 0,
            PreviousClose: TryDouble(meta, "chartPreviousClose") ?? TryDouble(meta, "previousClose") ?? 0,
            FiftyTwoWeekLow: TryDouble(meta, "fiftyTwoWeekLow") ?? 0,
            FiftyTwoWeekHigh: TryDouble(meta, "fiftyTwoWeekHigh") ?? 0);
    }

    public async Task<List<NewsItem>> GetNewsAsync(string query, int limit = 6, CancellationToken ct = default)
    {
        var url = $"https://query1.finance.yahoo.com/v1/finance/search?q={Uri.EscapeDataString(query)}&newsCount={limit}&quotesCount=0";
        using var doc = await GetJsonAsync(url, ct);
        var results = new List<NewsItem>();
        if (doc.RootElement.TryGetProperty("news", out var newsArr) && newsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in newsArr.EnumerateArray())
            {
                var when = item.TryGetProperty("providerPublishTime", out var pt) && pt.TryGetInt64(out var ptl)
                    ? DateTimeOffset.FromUnixTimeSeconds(ptl).UtcDateTime
                    : DateTime.UtcNow;
                results.Add(new NewsItem(
                    Title: TryString(item, "title") ?? "",
                    Publisher: TryString(item, "publisher") ?? "",
                    Link: TryString(item, "link") ?? "",
                    PublishedUtc: when));
                if (results.Count >= limit) break;
            }
        }
        return results;
    }

    /// <summary>
    /// Calls v10 quoteSummary with the modules requested. Returns raw JsonDocument for the caller to project.
    /// Returns null if the crumb dance fails.
    /// </summary>
    public async Task<JsonDocument?> GetQuoteSummaryAsync(string symbol, string modules, CancellationToken ct = default)
    {
        var crumb = await EnsureCrumbAsync(ct);
        if (string.IsNullOrEmpty(crumb))
        {
            this._log.LogWarning("QuoteSummary skipped for {Symbol} — no crumb.", symbol);
            return null;
        }
        var url = $"https://query1.finance.yahoo.com/v10/finance/quoteSummary/{Uri.EscapeDataString(symbol)}?modules={Uri.EscapeDataString(modules)}&crumb={Uri.EscapeDataString(crumb)}";
        try
        {
            return await GetJsonAsync(url, ct);
        }
        catch (Exception ex)
        {
            this._log.LogWarning(ex, "QuoteSummary failed for {Symbol}/{Modules}", symbol, modules);
            return null;
        }
    }

    private async Task<string?> EnsureCrumbAsync(CancellationToken ct)
    {
        if (this._crumb != null) return this._crumb;
        await this._crumbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this._crumb != null) return this._crumb;
            // Warm cookies. fc.yahoo.com is the standard crumb-cookie source; ignore any error.
            try { using (await this._http.GetAsync("https://fc.yahoo.com/", ct)) { } } catch { /* ignore */ }
            using var resp = await this._http.GetAsync("https://query1.finance.yahoo.com/v1/test/getcrumb", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var crumb = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (string.IsNullOrEmpty(crumb) || crumb.Length > 64) return null;
            this._crumb = crumb;
            return crumb;
        }
        finally { this._crumbLock.Release(); }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await this._http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? TryString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? TryDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
}

public sealed record Snapshot(
    string Symbol,
    string LongName,
    string Currency,
    string Exchange,
    double Price,
    double PreviousClose,
    double FiftyTwoWeekLow,
    double FiftyTwoWeekHigh);

public sealed record NewsItem(
    string Title,
    string Publisher,
    string Link,
    DateTime PublishedUtc);
