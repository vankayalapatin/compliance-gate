// Closed memo-to-rule type registry (docs/company-kb/RULE-TYPES.md).
// Eight types, their gates, and their parameter shapes and ranges. A memo
// maps to one of these or it does not become a rule. New types require
// code plus tests, never model invention.
//
// Parameter values arrive as strings (Gate.Contracts ProposedRule.Params);
// this registry parses and range-checks them. Ranges below are logic, not
// business values: caps live strictly inside (0,1) (0 blocks every award
// and 1 passes every award, both degenerate), windows are 1..120 months,
// scopes and boolean sets are closed vocabularies. Thresholds themselves
// come from memos and approvals, never from here.
using Gate.Contracts;

namespace Gate.Compliance;

/// <summary>One closed-registry rule type: its gate plus its parameter spec.</summary>
public sealed record MemoRuleTypeSpec(
    string RuleType,
    MemoRuleGate Gate,
    IReadOnlyList<string> RequiredParams,
    IReadOnlyList<string> OptionalParams,
    string Description);

public static class MemoRuleRegistry
{
    public const string DocumentPresence = "DocumentPresence";
    public const string OriginShareCap = "OriginShareCap";
    public const string VendorShareCap = "VendorShareCap";
    public const string ScreeningMatch = "ScreeningMatch";
    public const string DockMarkPresence = "DockMarkPresence";
    public const string DockHandlingPresence = "DockHandlingPresence";
    public const string DamageReview = "DamageReview";
    public const string PrePayBlockOnOpen = "PrePayBlockOnOpen";

    /// <summary>Share-cap bounds (exclusive): 0 blocks every award, 1 passes
    /// every award. Both ends are degenerate, so both are rejected.</summary>
    public const decimal MinCapExclusive = 0m;
    public const decimal MaxCapExclusive = 1m;

    /// <summary>Trailing-window bounds in months, inclusive.</summary>
    public const int MinWindowMonths = 1;
    public const int MaxWindowMonths = 120;

    public const string ScopeCritical = "critical";
    public const string ScopeAll = "all";

    private static readonly IReadOnlyDictionary<string, MemoRuleTypeSpec> Specs =
        new Dictionary<string, MemoRuleTypeSpec>(StringComparer.Ordinal)
        {
            [DocumentPresence] = new(DocumentPresence, MemoRuleGate.Vendor,
                ["docKind", "partScope"], [],
                "Vendor gate: a document kind must be on file for the lot."),
            [OriginShareCap] = new(OriginShareCap, MemoRuleGate.Vendor,
                ["cap", "windowMonths", "partScope"], [],
                "Vendor gate: awarded-country share cap over the trailing window."),
            [VendorShareCap] = new(VendorShareCap, MemoRuleGate.Vendor,
                ["cap", "windowMonths", "partScope"], [],
                "Vendor gate: awarded-vendor share cap over the trailing window."),
            [ScreeningMatch] = new(ScreeningMatch, MemoRuleGate.Vendor,
                [], [],
                "Vendor gate: restricted-party screening hit blocks. List scope is data-driven; memos cannot tune it, so proposals carry no params."),
            [DockMarkPresence] = new(DockMarkPresence, MemoRuleGate.Dock,
                ["markKind"], [],
                "Dock gate, photo-only: a mark kind must be visible and legible."),
            [DockHandlingPresence] = new(DockHandlingPresence, MemoRuleGate.Dock,
                ["markKind"], [],
                "Dock gate, photo-only: a handling mark must be visible when required."),
            [DamageReview] = new(DamageReview, MemoRuleGate.Dock,
                [], [],
                "Dock gate, photo-only: suspected damage routes to a human, never auto-decides. No params by design."),
            [PrePayBlockOnOpen] = new(PrePayBlockOnOpen, MemoRuleGate.PrePay,
                [], ["holds", "quarantine", "reviews"],
                "Pre-pay gate: blocks while any selected open state (holds, quarantine, reviews) is still open. At least one must be true."),
        };

    public static IReadOnlyList<MemoRuleTypeSpec> All => Specs.Values.ToList();

    public static bool IsKnownType(string ruleType) => Specs.ContainsKey(ruleType);

    public static MemoRuleTypeSpec Find(string ruleType)
    {
        if (!Specs.TryGetValue(ruleType, out var spec))
        {
            throw new InvalidOperationException(
                $"Unknown memo rule type '{ruleType}'. The closed registry holds exactly: {string.Join(", ", Specs.Keys)}. " +
                "New types require code plus tests, never model invention.");
        }

        return spec;
    }

    /// <summary>Validates params for a registry type. Returns the error list;
    /// empty means valid. Unknown keys, missing keys, and out-of-range
    /// values are all rejected loudly by the caller.</summary>
    public static IReadOnlyList<string> ValidateParams(string ruleType, IReadOnlyDictionary<string, string>? paramsByName)
    {
        var spec = Find(ruleType);
        var paramsMap = paramsByName ?? new Dictionary<string, string>();

        if (spec.RequiredParams.Count == 0 && spec.OptionalParams.Count == 0 && paramsMap.Count > 0)
        {
            return ruleType switch
            {
                ScreeningMatch => ValidateEmptyParams(ruleType, paramsMap,
                    "List scope is data-driven; memos cannot tune it."),
                DamageReview => ValidateEmptyParams(ruleType, paramsMap,
                    "DamageReview always routes to a human and takes no parameters."),
                _ => [$"Rule type '{ruleType}' takes no parameters but the proposal carries: " +
                    $"{string.Join(", ", paramsMap.Keys.OrderBy(k => k, StringComparer.Ordinal))}."],
            };
        }

        var errors = new List<string>();
        foreach (var key in paramsMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!spec.RequiredParams.Contains(key) && !spec.OptionalParams.Contains(key))
            {
                errors.Add(
                    $"Rule type '{ruleType}' takes no parameter '{key}'. " +
                    $"Known parameters: [{string.Join(", ", spec.RequiredParams.Concat(spec.OptionalParams))}].");
            }
        }

        foreach (var required in spec.RequiredParams)
        {
            if (!paramsMap.ContainsKey(required))
            {
                errors.Add($"Rule type '{ruleType}' requires parameter '{required}' but the proposal omits it.");
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        return ruleType switch
        {
            DocumentPresence => ValidateDocumentPresence(paramsMap),
            OriginShareCap or VendorShareCap => ValidateShareCap(ruleType, paramsMap),
            ScreeningMatch => ValidateEmptyParams(ruleType, paramsMap,
                "List scope is data-driven; memos cannot tune it."),
            DockMarkPresence or DockHandlingPresence => ValidateMarkKind(ruleType, paramsMap),
            DamageReview => ValidateEmptyParams(ruleType, paramsMap,
                "DamageReview always routes to a human and takes no parameters."),
            PrePayBlockOnOpen => ValidatePrePayStates(paramsMap),
            _ => errors,
        };
    }

    private static IReadOnlyList<string> ValidateDocumentPresence(IReadOnlyDictionary<string, string> paramsMap)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(paramsMap["docKind"]))
        {
            errors.Add("DocumentPresence 'docKind' must name a non-empty document kind.");
        }

        ValidatePartScope(paramsMap["partScope"], errors);
        return errors;
    }

    private static IReadOnlyList<string> ValidateShareCap(string ruleType, IReadOnlyDictionary<string, string> paramsMap)
    {
        var errors = new List<string>();
        if (!decimal.TryParse(paramsMap["cap"], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var cap))
        {
            errors.Add($"{ruleType} 'cap' value '{paramsMap["cap"]}' is not a decimal.");
        }
        else if (cap <= MinCapExclusive || cap >= MaxCapExclusive)
        {
            errors.Add($"{ruleType} 'cap' value '{paramsMap["cap"]}' is out of range: cap must be strictly between 0 and 1.");
        }

        if (!int.TryParse(paramsMap["windowMonths"], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var window))
        {
            errors.Add($"{ruleType} 'windowMonths' value '{paramsMap["windowMonths"]}' is not an integer.");
        }
        else if (window < MinWindowMonths || window > MaxWindowMonths)
        {
            errors.Add($"{ruleType} 'windowMonths' value '{paramsMap["windowMonths"]}' is out of range: " +
                $"must be {MinWindowMonths}..{MaxWindowMonths}.");
        }

        ValidatePartScope(paramsMap["partScope"], errors);
        return errors;
    }

    private static void ValidatePartScope(string scope, List<string> errors)
    {
        if (!string.Equals(scope, ScopeCritical, StringComparison.Ordinal)
            && !string.Equals(scope, ScopeAll, StringComparison.Ordinal))
        {
            errors.Add($"'partScope' value '{scope}' is out of range: must be '{ScopeCritical}' or '{ScopeAll}'.");
        }
    }

    private static IReadOnlyList<string> ValidateEmptyParams(
        string ruleType, IReadOnlyDictionary<string, string> paramsMap, string reason)
    {
        if (paramsMap.Count == 0)
        {
            return [];
        }

        return [$"{ruleType} takes no parameters ({reason}) but the proposal carries: " +
            $"{string.Join(", ", paramsMap.Keys.OrderBy(k => k, StringComparer.Ordinal))}."];
    }

    private static IReadOnlyList<string> ValidateMarkKind(string ruleType, IReadOnlyDictionary<string, string> paramsMap)
    {
        if (!string.IsNullOrWhiteSpace(paramsMap["markKind"]))
        {
            return [];
        }

        return [$"{ruleType} 'markKind' must name a non-empty mark kind."];
    }

    private static IReadOnlyList<string> ValidatePrePayStates(IReadOnlyDictionary<string, string> paramsMap)
    {
        var errors = new List<string>();
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in new[] { "holds", "quarantine", "reviews" })
        {
            if (!paramsMap.TryGetValue(key, out var raw))
            {
                states[key] = false;
                continue;
            }

            if (!bool.TryParse(raw, out var value))
            {
                errors.Add($"PrePayBlockOnOpen '{key}' value '{raw}' is not a boolean (expected 'true' or 'false').");
            }
            else
            {
                states[key] = value;
            }
        }

        if (errors.Count == 0 && states.Values.All(v => !v))
        {
            errors.Add("PrePayBlockOnOpen must block on at least one open state: 'holds', 'quarantine', or 'reviews' must be 'true'.");
        }

        return errors;
    }
}
