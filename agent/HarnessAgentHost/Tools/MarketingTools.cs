// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace HarnessAgentHost.Tools;

/// <summary>
/// Demo marketing / product tools for the launch-campaign scenario. All data is fabricated but
/// deterministic per input, so the model can build on prior calls without an actual research feed.
/// </summary>
public static class MarketingTools
{
    public static IList<AITool> CreateAll() =>
    [
        AIFunctionFactory.Create(CompetitorScan),
        AIFunctionFactory.Create(KeywordTrends),
        AIFunctionFactory.Create(GeneratePersona),
        AIFunctionFactory.Create(EstimateReach),
        AIFunctionFactory.Create(DraftCopy),
        AIFunctionFactory.Create(SuggestChannels),
    ];

    [Description("Returns 3 fabricated competitor products with a one-line positioning summary and rough price band.")]
    public static string CompetitorScan(
        [Description("Product being launched, e.g. 'AI-powered notes app for students'.")] string product,
        [Description("Target market/region, e.g. 'India' or 'US SMB'.")] string market)
    {
        var rng = SeededRng($"{product}:{market}:comp");
        var pool = new[]
        {
            "brand-north-star", "quill-pilot", "notabl.ai", "studynote-x",
            "focusflow", "brainboard", "recallr", "loom-notes"
        };
        var competitors = pool.OrderBy(_ => rng.Next()).Take(3).Select(name => new
        {
            name,
            positioning = new[]
            {
                "Freemium AI summarization for lectures.",
                "Premium team notebooks with citation graph.",
                "Local-first offline notes with weekly AI review.",
                "Voice-to-notes with automatic study card generation."
            }[rng.Next(4)],
            monthlyPriceUsd = new[] { 0.0, 4.99, 6.99, 9.99, 12.99 }[rng.Next(5)]
        });
        return JsonSerializer.Serialize(new { product, market, competitors, note = "DEMO DATA." });
    }

    [Description("Returns fabricated top search keywords and rough monthly volumes for a topic in a market.")]
    public static string KeywordTrends(
        [Description("Topic to research, e.g. 'AI notes app'.")] string topic,
        [Description("Target market/region.")] string market)
    {
        var rng = SeededRng($"{topic}:{market}:kw");
        var seeds = new[]
        {
            "best {topic} for students", "{topic} free", "{topic} vs notion",
            "{topic} for exam prep", "how to use {topic}", "{topic} offline",
        };
        var kws = seeds.OrderBy(_ => rng.Next()).Take(5).Select(t => new
        {
            keyword = t.Replace("{topic}", topic.ToLowerInvariant()),
            monthlyVolume = rng.Next(2_000, 60_000),
            competitionScore = Math.Round(rng.NextDouble(), 2)
        });
        return JsonSerializer.Serialize(new { topic, market, keywords = kws, note = "DEMO DATA." });
    }

    [Description("Returns a fabricated audience persona (demographics, goals, pains, media diet) for a segment.")]
    public static string GeneratePersona(
        [Description("Segment description, e.g. 'undergraduate STEM students in Tier-2 Indian cities'.")] string segment)
    {
        var rng = SeededRng($"{segment}:persona");
        var name = new[] { "Aarav", "Priya", "Kabir", "Meera", "Rohit", "Ananya" }[rng.Next(6)];
        return JsonSerializer.Serialize(new
        {
            segment,
            persona = new
            {
                name,
                ageRange = new[] { "17-20", "19-22", "20-24" }[rng.Next(3)],
                context = "Balances heavy course load with side projects; commutes 45+ minutes.",
                goals = new[] { "clear exams with less study time", "capture ideas across devices", "review notes offline in transit" },
                pains = new[] { "typing on phone", "losing scattered notes", "no time to summarize" },
                mediaDiet = new[] { "Instagram Reels", "YouTube (2x speed)", "Reddit r/IndianStudents", "WhatsApp study groups" },
                priceSensitivityUsdMonthly = new[] { 0.0, 2.0, 4.99 }[rng.Next(3)]
            },
            note = "DEMO DATA."
        });
    }

    [Description("Returns a fabricated reach and cost estimate for a channel at a monthly budget.")]
    public static string EstimateReach(
        [Description("Channel name, e.g. 'YouTube pre-roll' | 'Instagram Reels' | 'Google Search'.")] string channel,
        [Description("Monthly budget in USD.")] double budgetUsd)
    {
        var rng = SeededRng($"{channel}:{budgetUsd}");
        var cpm = 2 + rng.NextDouble() * 12;
        var impressions = (long)(budgetUsd / cpm * 1000);
        var ctr = Math.Round(0.4 + rng.NextDouble() * 3.0, 2);
        var clicks = (long)(impressions * ctr / 100);
        var installs = (long)(clicks * (0.02 + rng.NextDouble() * 0.08));
        return JsonSerializer.Serialize(new
        {
            channel,
            budgetUsd,
            cpmUsd = Math.Round(cpm, 2),
            estimatedImpressions = impressions,
            estimatedClickThroughRatePct = ctr,
            estimatedClicks = clicks,
            estimatedInstalls = installs,
            note = "DEMO DATA."
        });
    }

    [Description("Returns 3 draft copy variants for a channel and message length. Use for Twitter thread hooks, ad headlines, email subject lines, etc.")]
    public static string DraftCopy(
        [Description("Channel: 'twitter' | 'email' | 'reels' | 'press-release'.")] string channel,
        [Description("Core message the copy should communicate.")] string message,
        [Description("Length: 'short' | 'medium' | 'long'.")] string length = "short")
    {
        var rng = SeededRng($"{channel}:{message}:{length}");
        var voices = new[] { "punchy", "curious", "confident", "friendly" };
        var variants = Enumerable.Range(0, 3).Select(_ => new
        {
            voice = voices[rng.Next(voices.Length)],
            copy = $"[{channel}/{length}] {message} — {new[] { "here's why it matters", "how it saves you 2 hours a week", "your notes, but smarter", "study smarter, not longer" }[rng.Next(4)]}"
        });
        return JsonSerializer.Serialize(new { channel, length, variants, note = "DEMO DATA — swap for a real skill in production." });
    }

    [Description("Returns a recommended channel mix for a segment and total monthly budget, split into percentages.")]
    public static string SuggestChannels(
        [Description("Segment description.")] string segment,
        [Description("Total monthly marketing budget in USD.")] double totalBudgetUsd)
    {
        var rng = SeededRng($"{segment}:{totalBudgetUsd}:mix");
        var pool = new[] { "YouTube Shorts", "Instagram Reels", "Google Search", "Reddit ads", "Campus ambassadors", "WhatsApp broadcast" };
        var picks = pool.OrderBy(_ => rng.Next()).Take(4).ToArray();
        var weights = Enumerable.Range(0, picks.Length).Select(_ => rng.NextDouble()).ToArray();
        var sum = weights.Sum();
        var mix = picks.Select((p, i) => new
        {
            channel = p,
            allocationPct = Math.Round(weights[i] / sum * 100, 1),
            monthlyBudgetUsd = Math.Round(totalBudgetUsd * weights[i] / sum, 2)
        });
        return JsonSerializer.Serialize(new { segment, totalBudgetUsd, mix, note = "DEMO DATA." });
    }

    private static Random SeededRng(string key)
    {
        int seed = 0;
        foreach (var c in key) seed = unchecked(seed * 31 + c);
        return new Random(seed);
    }
}
