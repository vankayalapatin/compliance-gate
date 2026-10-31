// Conflict computation against live packs (docs/company-kb/MEMO-PIPELINE.md
// stage 3). Every conflict quotes both sides. Kinds:
// - Tightens: the proposal blocks a superset of what the live rule blocks
//   (lower cap, wider scope, newly-enforced requirement).
// - Loosens: the proposal blocks a subset (higher cap, dropped pre-pay state).
// - Contradicts: the proposal undermines a live enforced rule's path. The
//   registry's concrete case is a pre-pay proposal that drops an open state
//   which a live enforced Tier-1 rule still produces: payment could proceed
//   despite an open Tier-1 hold, quarantine, or review.
// - Subsumes: the live rule already enforces exactly this (identical cap,
//   same document, same mark, screening already live, damage already
//   human-routed, identical pre-pay set).
//
// A window-only cap change (equal cap, different window) yields no record:
// a shorter window is not strictly tighter, so the kinds cannot express
// it; the what-if report shows its impact instead. A proposal dimension
// that widens while another narrows yields one record per dimension.
//
// Live-pack assumptions, each pinned by test: the live origin/vendor caps
// come from the CORP pack config; the live window is 12 months (the
// VolumeHistory contract is trailing-12-month); the live CORP scope is
// critical (rule titles plus PROJECT.md section 6); the live pre-pay set
// is holds+quarantine+reviews (mirrors ComplianceEngine.EvaluatePaymentAsync,
// pinned by a consistency test); pre-pay state producers are the enforced
// dock rules below (mirrors DockEvaluator: BAT-LI-03/04 hold, BAT-LI-05
// reviews with a human quarantine path).
using System.Globalization;
using System.Text;
using Gate.Contracts;

namespace Gate.Compliance;

public static class RuleConflictDetector
{
    public const string CorpOriginRuleId = "CORP-02";
    public const string CorpVendorRuleId = "CORP-01";
    public const string ScreeningRuleId = "SCR-01";
    public const string PrePayRuleId = "ELEC-04";
    public const string DockMarkRuleId = "BAT-LI-03";
    public const string DockHandlingRuleId = "BAT-LI-04";
    public const string DamageRuleId = "BAT-LI-05";

    /// <summary>Live CORP window in months. The VolumeHistory contract is
    /// trailing-12-month and the engine evaluates those rows as given.</summary>
    public const int LiveCorpWindowMonths = 12;

    /// <summary>Live CORP part scope. CORP titles plus PROJECT.md section 6
    /// scope both caps to critical battery volume.</summary>
    public const string LiveCorpScope = MemoRuleRegistry.ScopeCritical;

    /// <summary>Live ELEC-04 state set. Mirrors
    /// ComplianceEngine.EvaluatePaymentAsync, which blocks unless holds,
    /// quarantine, and reviews are all empty.</summary>
    public static readonly IReadOnlyDictionary<string, bool> LivePrePayStates =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["holds"] = true,
            ["quarantine"] = true,
            ["reviews"] = true,
        };

    /// <summary>Which enforced dock rules produce each pre-pay open state
    /// (mirrors DockEvaluator). Dropping a state one of these still
    /// produces contradicts that rule's enforcement path.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PrePayStateProducers =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["holds"] = [DockMarkRuleId, DockHandlingRuleId],
            ["quarantine"] = [DamageRuleId],
            ["reviews"] = [DamageRuleId],
        };

    /// <summary>Computes conflicts against the live catalog (default) or an
    /// explicit live set (tests). The proposal is assumed structurally
    /// valid; malformed params throw loudly instead of guessing.</summary>
    public static IReadOnlyList<RuleConflict> Compute(
        ProposedRule rule,
        IReadOnlyList<RuleDefinition>? liveRules = null,
        CorpPackConfig? corp = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        liveRules ??= RuleCatalog.Rules;
        corp ??= RuleCatalog.Corp;

        return rule.RuleType switch
        {
            MemoRuleRegistry.OriginShareCap => ShareCapConflicts(
                rule, CorpOriginRuleId, corp.OriginCap, liveRules),
            MemoRuleRegistry.VendorShareCap => ShareCapConflicts(
                rule, CorpVendorRuleId, corp.VendorCap, liveRules),
            MemoRuleRegistry.DocumentPresence => DocumentConflicts(rule, liveRules),
            MemoRuleRegistry.ScreeningMatch => ScreeningConflicts(liveRules),
            MemoRuleRegistry.DockMarkPresence => DockMarkConflicts(
                rule, DockMarkRuleId, liveRules, IsLithiumBatteryMark),
            MemoRuleRegistry.DockHandlingPresence => DockMarkConflicts(
                rule, DockHandlingRuleId, liveRules, IsClass9HandlingMark),
            MemoRuleRegistry.DamageReview => DamageConflicts(liveRules),
            MemoRuleRegistry.PrePayBlockOnOpen => PrePayConflicts(rule, liveRules),
            _ => throw new InvalidOperationException(
                $"Cannot compute conflicts for unknown rule type '{rule.RuleType}'; validate the proposal first."),
        };
    }

    private static IReadOnlyList<RuleConflict> ShareCapConflicts(
        ProposedRule rule, string liveRuleId, decimal liveCap, IReadOnlyList<RuleDefinition> liveRules)
    {
        var live = liveRules.FirstOrDefault(r => r.RuleId == liveRuleId);
        if (live is null)
        {
            return [];
        }

        var cap = RequireCap(rule);
        var window = RequireWindow(rule);
        var scope = RequireParam(rule, "partScope");
        var conflicts = new List<RuleConflict>();

        if (!live.Enforced)
        {
            conflicts.Add(new RuleConflict(liveRuleId, RuleConflictKind.Tightens,
                $"Proposed {rule.RuleType} cap {cap:P0} (window {window}, scope {scope}) newly enforces " +
                $"a cap where live {liveRuleId} is documented-only at {liveCap:P0} (window {LiveCorpWindowMonths}, scope {LiveCorpScope})."));
            return conflicts;
        }

        if (cap < liveCap)
        {
            conflicts.Add(new RuleConflict(liveRuleId, RuleConflictKind.Tightens,
                $"Proposed {rule.RuleType} cap {cap:P0} tightens live {liveRuleId} cap {liveCap:P0} " +
                $"(window {LiveCorpWindowMonths} to {window}, scope {LiveCorpScope} to {scope})."));
        }
        else if (cap > liveCap)
        {
            conflicts.Add(new RuleConflict(liveRuleId, RuleConflictKind.Loosens,
                $"Proposed {rule.RuleType} cap {cap:P0} loosens live {liveRuleId} cap {liveCap:P0} " +
                $"(window {LiveCorpWindowMonths} to {window}, scope {LiveCorpScope} to {scope})."));
        }

        if (cap == liveCap && window == LiveCorpWindowMonths && scope == LiveCorpScope)
        {
            conflicts.Add(new RuleConflict(liveRuleId, RuleConflictKind.Subsumes,
                $"Live {liveRuleId} already enforces {rule.RuleType} cap {liveCap:P0} " +
                $"(window {LiveCorpWindowMonths}, scope {LiveCorpScope}); the proposal adds nothing."));
            return conflicts;
        }

        if (!string.Equals(scope, LiveCorpScope, StringComparison.Ordinal))
        {
            var widens = string.Equals(scope, MemoRuleRegistry.ScopeAll, StringComparison.Ordinal);
            conflicts.Add(new RuleConflict(liveRuleId,
                widens ? RuleConflictKind.Tightens : RuleConflictKind.Loosens,
                $"Proposed {rule.RuleType} scope '{scope}' {(widens ? "widens" : "narrows")} live {liveRuleId} scope '{LiveCorpScope}'."));
        }

        return conflicts;
    }

    private static IReadOnlyList<RuleConflict> DocumentConflicts(
        ProposedRule rule, IReadOnlyList<RuleDefinition> liveRules)
    {
        var docKind = RequireParam(rule, "docKind");
        var scope = RequireParam(rule, "partScope");
        var live = liveRules.FirstOrDefault(r =>
            r.DocumentKind is not null && string.Equals(r.DocumentKind, docKind, StringComparison.Ordinal));
        if (live is null)
        {
            return [];
        }

        if (!live.Enforced)
        {
            return [new RuleConflict(live.RuleId, RuleConflictKind.Tightens,
                $"Proposed DocumentPresence '{docKind}' (scope {scope}) newly enforces a document " +
                $"live {live.RuleId} only documents.")];
        }

        if (string.Equals(scope, MemoRuleRegistry.ScopeAll, StringComparison.Ordinal))
        {
            return [new RuleConflict(live.RuleId, RuleConflictKind.Tightens,
                $"Proposed DocumentPresence '{docKind}' scope 'all' widens live {live.RuleId}, " +
                "which requires the document for critical parts only.")];
        }

        return [new RuleConflict(live.RuleId, RuleConflictKind.Subsumes,
            $"Live {live.RuleId} already requires '{docKind}' for critical parts; the proposal adds nothing.")];
    }

    private static IReadOnlyList<RuleConflict> ScreeningConflicts(IReadOnlyList<RuleDefinition> liveRules)
    {
        var live = liveRules.FirstOrDefault(r => r.RuleId == ScreeningRuleId);
        if (live is null)
        {
            return [];
        }

        return live.Enforced
            ? [new RuleConflict(ScreeningRuleId, RuleConflictKind.Subsumes,
                $"Live {ScreeningRuleId} already screens vendors against the compliance-side snapshot; " +
                "a ScreeningMatch proposal adds nothing (list scope is data-driven).")]
            : [new RuleConflict(ScreeningRuleId, RuleConflictKind.Tightens,
                $"Proposed ScreeningMatch newly enforces screening where live {ScreeningRuleId} is documented-only.")];
    }

    private static IReadOnlyList<RuleConflict> DockMarkConflicts(
        ProposedRule rule, string liveRuleId, IReadOnlyList<RuleDefinition> liveRules, Func<string, bool> isLiveMark)
    {
        var live = liveRules.FirstOrDefault(r => r.RuleId == liveRuleId);
        if (live is null || !isLiveMark(RequireParam(rule, "markKind")))
        {
            return [];
        }

        return live.Enforced
            ? [new RuleConflict(liveRuleId, RuleConflictKind.Subsumes,
                $"Live {liveRuleId} already gates mark '{rule.Params["markKind"]}'; the proposal adds nothing.")]
            : [new RuleConflict(liveRuleId, RuleConflictKind.Tightens,
                $"Proposed {rule.RuleType} '{rule.Params["markKind"]}' newly enforces a mark live {liveRuleId} only documents.")];
    }

    private static IReadOnlyList<RuleConflict> DamageConflicts(IReadOnlyList<RuleDefinition> liveRules)
    {
        var live = liveRules.FirstOrDefault(r => r.RuleId == DamageRuleId);
        if (live is null)
        {
            return [];
        }

        return live.Enforced
            ? [new RuleConflict(DamageRuleId, RuleConflictKind.Subsumes,
                $"Live {DamageRuleId} already routes suspected damage to a human and never auto-decides; " +
                "a DamageReview proposal adds nothing.")]
            : [new RuleConflict(DamageRuleId, RuleConflictKind.Tightens,
                $"Proposed DamageReview newly enforces human damage review where live {DamageRuleId} is documented-only.")];
    }

    private static IReadOnlyList<RuleConflict> PrePayConflicts(
        ProposedRule rule, IReadOnlyList<RuleDefinition> liveRules)
    {
        var live = liveRules.FirstOrDefault(r => r.RuleId == PrePayRuleId);
        if (live is null)
        {
            return [];
        }

        var proposed = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["holds"] = IsTrue(rule.Params, "holds"),
            ["quarantine"] = IsTrue(rule.Params, "quarantine"),
            ["reviews"] = IsTrue(rule.Params, "reviews"),
        };

        if (!live.Enforced)
        {
            return [new RuleConflict(PrePayRuleId, RuleConflictKind.Tightens,
                $"Proposed PrePayBlockOnOpen [{DescribeStates(proposed)}] newly enforces pre-pay gating " +
                $"where live {PrePayRuleId} is documented-only.")];
        }

        var dropped = LivePrePayStates.Keys.Where(k => LivePrePayStates[k] && !proposed[k]).ToList();
        var added = LivePrePayStates.Keys.Where(k => !LivePrePayStates[k] && proposed[k]).ToList();
        if (dropped.Count == 0 && added.Count == 0)
        {
            return [new RuleConflict(PrePayRuleId, RuleConflictKind.Subsumes,
                $"Live {PrePayRuleId} already blocks on [{DescribeStates(proposed)}]; the proposal adds nothing.")];
        }

        var conflicts = new List<RuleConflict>();
        if (dropped.Count > 0)
        {
            conflicts.Add(new RuleConflict(PrePayRuleId, RuleConflictKind.Loosens,
                $"Proposed PrePayBlockOnOpen drops {string.Join(", ", dropped)} from live {PrePayRuleId} " +
                $"[{DescribeStates(LivePrePayStates)}]; payment could proceed with those states open."));
            foreach (var state in dropped)
            {
                foreach (var producerId in PrePayStateProducers[state])
                {
                    var producer = liveRules.FirstOrDefault(r => r.RuleId == producerId);
                    if (producer is not null && producer.Enforced && producer.Tier == RuleTier.Tier1)
                    {
                        conflicts.Add(new RuleConflict(producerId, RuleConflictKind.Contradicts,
                            $"Proposed PrePayBlockOnOpen drops '{state}' while live enforced Tier-1 {producerId} " +
                            $"still produces {state}; payment could proceed despite an open Tier-1 {state} state, " +
                            $"contradicting {producerId}'s enforcement path."));
                    }
                }
            }
        }

        if (added.Count > 0)
        {
            conflicts.Add(new RuleConflict(PrePayRuleId, RuleConflictKind.Tightens,
                $"Proposed PrePayBlockOnOpen adds {string.Join(", ", added)} to live {PrePayRuleId} " +
                $"[{DescribeStates(LivePrePayStates)}]."));
        }

        return conflicts;
    }

    private static decimal RequireCap(ProposedRule rule)
    {
        var raw = RequireParam(rule, "cap");
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var cap))
        {
            throw new InvalidOperationException(
                $"Cannot compute conflicts: {rule.RuleType} 'cap' value '{raw}' is not a decimal. " +
                "Validate the proposal first.");
        }

        return cap;
    }

    private static int RequireWindow(ProposedRule rule)
    {
        var raw = RequireParam(rule, "windowMonths");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window))
        {
            throw new InvalidOperationException(
                $"Cannot compute conflicts: {rule.RuleType} 'windowMonths' value '{raw}' is not an integer. " +
                "Validate the proposal first.");
        }

        return window;
    }

    private static string RequireParam(ProposedRule rule, string name)
    {
        if (!rule.Params.TryGetValue(name, out var value))
        {
            throw new InvalidOperationException(
                $"Cannot compute conflicts: {rule.RuleType} requires parameter '{name}' but the proposal omits it. " +
                "Validate the proposal first.");
        }

        return value;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> paramsMap, string key) =>
        paramsMap.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) && value;

    private static string DescribeStates(IReadOnlyDictionary<string, bool> states) =>
        string.Join(", ", states.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>True when the memo's mark phrasing names the live BAT-LI-03
    /// lithium-battery mark. Normalization is lowercase alphanumeric-only so
    /// "Lithium-Battery Mark" and "lithium battery mark" compare equal.</summary>
    internal static bool IsLithiumBatteryMark(string markKind) =>
        NormalizeMark(markKind).Contains("lithium", StringComparison.Ordinal)
        && NormalizeMark(markKind).Contains("battery", StringComparison.Ordinal);

    /// <summary>True when the memo's mark phrasing names live BAT-LI-04
    /// handling (Class 9 diamond or Cargo Aircraft Only).</summary>
    internal static bool IsClass9HandlingMark(string markKind)
    {
        var normalized = NormalizeMark(markKind);
        return normalized.Contains("class9", StringComparison.Ordinal)
            || normalized.Contains("cargoaircraftonly", StringComparison.Ordinal);
    }

    internal static string NormalizeMark(string markKind)
    {
        var sb = new StringBuilder(markKind.Length);
        foreach (var ch in markKind.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
