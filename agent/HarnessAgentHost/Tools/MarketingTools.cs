// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Live market-research tools. Competitors come from the Apple App Store search API, search demand from Google's
/// live autocomplete, and audience sizing from the World Bank indicators API. No key and no fabricated numbers.
/// </summary>
public sealed class MarketingTools
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private static readonly (string Code, string Indicator, string Label)[] s_indicators =
    [
        ("population", "SP.POP.TOTL", "Total population"),
        ("internetUsersPct", "IT.NET.USER.ZS", "Individuals using the internet (% of population)"),
        ("mobileSubsPer100", "IT.CEL.SETS.P2", "Mobile cellular subscriptions (per 100 people)"),
        ("gdpPerCapitaUsd", "NY.GDP.PCAP.CD", "GDP per capita (current US$)"),
        ("tertiaryEnrollmentPct", "SE.TER.ENRR", "School enrollment, tertiary (% gross)"),
    ];

    private readonly HttpClient _http;
    private readonly ILogger<MarketingTools> _log;
    private Dictionary<string, string>? _countries;

    public MarketingTools(HttpClient http, ILogger<MarketingTools> log)
    {
        this._http = http;
        this._log = log;
        this._http.Timeout = TimeSpan.FromSeconds(15);
        if (!this._http.DefaultRequestHeaders.UserAgent.Any())
            this._http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 harness-playground");
    }

    public IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(this.SearchAppStoreAsync, new AIFunctionFactoryOptions { Name = "search_app_store" }),
        AIFunctionFactory.Create(this.SearchSuggestionsAsync, new AIFunctionFactoryOptions { Name = "search_suggestions" }),
        AIFunctionFactory.Create(this.GetMarketProfileAsync, new AIFunctionFactoryOptions { Name = "get_market_profile" }),
    ];

    [Description("Live competitor scan: searches the Apple App Store for products matching a query in a country and returns name, seller, price, rating, rating count, genre and release date for the top results.")]
    public async Task<string> SearchAppStoreAsync(
        [Description("What to search for, e.g. 'AI notes app' or 'study flashcards'.")] string query,
        [Description("Target market, e.g. 'India', 'United States', 'Germany'.")] string market,
        CancellationToken ct = default)
    {
        try
        {
            var iso = await this.ResolveCountryAsync(market, ct);
            var cc = (iso ?? "US").ToLowerInvariant();
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&country={cc}&entity=software&limit=8";
            using var doc = JsonDocument.Parse(await this._http.GetStringAsync(url, ct));
            var apps = doc.RootElement.GetProperty("results").EnumerateArray().Take(6).Select(a => new
            {
                name = Str(a, "trackName"),
                seller = Str(a, "sellerName"),
                priceFormatted = Str(a, "formattedPrice"),
                avgRating = Num(a, "averageUserRating"),
                ratingCount = Num(a, "userRatingCount"),
                genre = Str(a, "primaryGenreName"),
                released = Str(a, "releaseDate"),
                description = Trim(Str(a, "description"), 220),
            }).ToList();
            return Json(new { query, market, storeCountry = cc.ToUpperInvariant(), apps, source = "Apple App Store Search API (live)" });
        }
        catch (Exception ex)
        {
            return Error("search_app_store", ex);
        }
    }

    [Description("Live search-demand signals: Google autocomplete suggestions for a topic in a market. These are real queries people type; suggestions are ordered by popularity, but no volumes are available.")]
    public async Task<string> SearchSuggestionsAsync(
        [Description("Topic or seed query, e.g. 'ai notes app'.")] string topic,
        [Description("Target market, e.g. 'India'.")] string market,
        CancellationToken ct = default)
    {
        try
        {
            var iso = (await this.ResolveCountryAsync(market, ct) ?? "US").ToLowerInvariant();
            var seeds = new[] { topic, $"best {topic}", $"{topic} for", $"{topic} vs", $"{topic} free" };
            var groups = new List<object>();
            foreach (var seed in seeds)
            {
                var url = $"https://suggestqueries.google.com/complete/search?client=firefox&q={Uri.EscapeDataString(seed)}&hl=en&gl={iso}";
                using var doc = JsonDocument.Parse(await this._http.GetStringAsync(url, ct));
                var items = doc.RootElement[1].EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(6).ToList();
                groups.Add(new { seed, suggestions = items });
            }
            return Json(new { topic, market, groups, source = "Google autocomplete (live)" });
        }
        catch (Exception ex)
        {
            return Error("search_suggestions", ex);
        }
    }

    [Description("Live audience sizing for a country from the World Bank: population, internet users %, mobile subscriptions, GDP per capita and tertiary enrollment, each with the year of the latest value.")]
    public async Task<string> GetMarketProfileAsync(
        [Description("Country name, e.g. 'India' or 'Germany'.")] string market,
        CancellationToken ct = default)
    {
        try
        {
            var iso = await this.ResolveCountryAsync(market, ct);
            if (iso is null) return Json(new { market, error = "Could not match that market to a country in the World Bank list." });

            var rows = new List<object>();
            foreach (var (code, indicator, label) in s_indicators)
            {
                var url = $"https://api.worldbank.org/v2/country/{iso}/indicator/{indicator}?format=json&mrnev=1";
                using var doc = JsonDocument.Parse(await this._http.GetStringAsync(url, ct));
                object? row = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 1 && doc.RootElement[1].ValueKind == JsonValueKind.Array)
                {
                    var first = doc.RootElement[1].EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                        row = new { metric = code, label, value = Math.Round(v.GetDouble(), 2), year = Str(first, "date") };
                }
                rows.Add(row ?? new { metric = code, label, value = (double?)null, year = (string?)null });
            }
            return Json(new { market, iso, indicators = rows, source = "World Bank Open Data API (latest available year)" });
        }
        catch (Exception ex)
        {
            return Error("get_market_profile", ex);
        }
    }

    private async Task<string?> ResolveCountryAsync(string market, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(market)) return null;
        this._countries ??= await this.LoadCountriesAsync(ct);
        var key = market.Trim().ToLowerInvariant();
        if (this._countries.TryGetValue(key, out var iso)) return iso;
        var hit = this._countries.FirstOrDefault(kv => key.Contains(kv.Key) || kv.Key.Contains(key));
        if (hit.Value is not null) return hit.Value;
        if (key is "us" or "usa" or "u.s." or "america") return "US";
        if (key is "uk" or "britain") return "GB";
        return null;
    }

    private async Task<Dictionary<string, string>> LoadCountriesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(await this._http.GetStringAsync("https://api.worldbank.org/v2/country?format=json&per_page=400", ct));
        foreach (var c in doc.RootElement[1].EnumerateArray())
        {
            var region = c.GetProperty("region").GetProperty("value").GetString();
            if (region == "Aggregates") continue;
            var name = c.GetProperty("name").GetString();
            var iso = c.GetProperty("iso2Code").GetString();
            if (name is not null && iso is not null) map[name.ToLowerInvariant()] = iso;
        }
        return map;
    }

    private string Error(string tool, Exception ex)
    {
        this._log.LogWarning(ex, "{Tool} failed", tool);
        return Json(new { error = $"{tool} failed: {ex.Message}" });
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? Math.Round(p.GetDouble(), 2) : null;
    private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
    private static string Json(object o) => JsonSerializer.Serialize(o, s_json);
}
