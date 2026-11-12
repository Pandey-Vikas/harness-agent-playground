// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Agents.AI;

namespace HarnessAgentHost.Scenarios;

/// <summary>
/// Code-defined Agent Skills. The harness advertises each skill's name and description in the system
/// prompt; the model pulls the full body with <c>load_skill</c> only when it needs it (progressive disclosure).
/// </summary>
public static class ScenarioSkills
{
    public const string InvestmentMemo = "investment-memo";
    public const string IncidentPostmortem = "incident-postmortem";
    public const string CampaignBrief = "campaign-brief";

    public static IReadOnlyList<AgentSkill> Financial() =>
    [
        new AgentInlineSkill(
            name: InvestmentMemo,
            description: "House format and writing rules for an equity investment thesis memo. Use before writing any investment memo.",
            instructions:
                """
                # Investment memo format

                Write the memo as one Copilot-style answer. Weave numbers into sentences and cite the tool that
                produced them in backticks (for example `get_financials`). Never invent numbers.

                ```
                # Investment Thesis Memo — {TICKER} (12-month horizon)

                *Live data from Yahoo Finance. Not investment advice.*

                ## Overview
                (3-4 sentence paragraph: price and scale with the correct currency symbol, what the tools showed,
                 and the thesis direction.)

                ## The bull case
                (1-2 short paragraphs. No bullets.)

                ## The bear case
                (1-2 short paragraphs. No bullets.)

                ## Peer and news context
                (Short paragraph that folds in the peer comparison and the news themes.)

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

                The DCF from `estimate_valuation` is a single-stage Gordon-growth sanity check on trailing free cash
                flow. If it disagrees materially with the market price, say so and call it a data limitation, not a
                directional signal. Read the `valuation-caveats` resource if you need the wording.
                """)
            .AddResource(
                "valuation-caveats",
                """
                A single-stage DCF on trailing free cash flow ignores growth durability, reinvestment needs and
                share-based compensation. Treat a large gap to the market price as a model limitation. Prefer
                language such as "the simple DCF is not informative here" over calling the stock mis-priced.
                """),
    ];

    public static IReadOnlyList<AgentSkill> Incident() =>
    [
        new AgentInlineSkill(
            name: IncidentPostmortem,
            description: "Blameless incident post-mortem format and writing rules. Use before writing an incident summary.",
            instructions:
                """
                # Post-mortem format

                One Copilot-style answer. Weave metric values into prose and cite the tool in backticks.

                ```
                # Incident Post-Mortem — {SERVICE}

                *Telemetry measured live from this host.*

                ## Overview
                (3-4 sentences: what was seen and the top hypothesis.)

                ## Symptoms
                (Short paragraph citing metric values.)

                ## Timeline
                One-sentence lead-in.
                - short bullet
                - short bullet

                ## Hypothesis
                (Short paragraph.)

                ## Evidence
                One-sentence lead-in.
                - short bullet
                - short bullet

                ## Proposed remediation
                (Short paragraph. Name the exact action and say it is waiting on human approval.)

                ## Blast radius
                (1-2 sentences.)

                ## Follow-ups
                One-sentence lead-in.
                - short bullet
                - short bullet

                ## Bottom line
                (1-2 sentences.)
                ```
                """)
            .AddResource(
                "severity-guide",
                """
                SEV1: customer-facing outage or data loss. SEV2: major degradation for a large share of traffic.
                SEV3: minor degradation with a workaround. Pick the lowest severity the evidence supports.
                """),
    ];

    public static IReadOnlyList<AgentSkill> Campaign() =>
    [
        new AgentInlineSkill(
            name: CampaignBrief,
            description: "Launch brief structure and brand-voice rules for go-to-market plans. Use before writing a launch brief.",
            instructions:
                """
                # Launch brief format

                One Copilot-style answer. Personas are short prose paragraphs, not bullet lists.

                ```
                # Launch Brief — {PRODUCT} in {MARKET}

                *Live research: Apple App Store, Google autocomplete, World Bank and web search. Reach and budget figures are assumptions.*

                ## Overview
                (3-4 sentences.)

                ## Audience personas
                ### Primary: {Name}
                (2-3 sentence paragraph.)

                ### Secondary: {Name}
                (2-3 sentence paragraph.)

                ## Positioning
                (1-2 sentences.)

                ## Channel mix (4-week budget split)
                | Channel | % | Assumed reach and why |
                |---|---:|---|

                ## 4-week content calendar
                Week 1 — Awareness: (short prose + CTA)
                Week 2 — Proof: (short prose + CTA)
                Week 3 — Activation: (short prose + CTA)
                Week 4 — Conversion: (short prose + CTA)

                ## Sample copy
                ### Instagram Reels
                (2-3 voice variants you write, grounded in the search suggestions.)

                ### Email
                (2-3 voice variants.)

                ## Success metrics
                One-sentence lead-in.
                - short bullet
                - short bullet
                - short bullet

                ## Bottom line
                (1-2 sentences.)
                ```
                """)
            .AddResource(
                "voice-guide",
                """
                Punchy: short sentences, one idea, a verb first. Friendly: second person, warm, light humour.
                Never promise outcomes you cannot back with a number from the research tools.
                """),
    ];
}
