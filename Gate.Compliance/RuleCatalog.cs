// Embedded compliance rule catalog (PROJECT.md section 6; each rule
// carries its own pack version, e.g. BAT-LI v2, ELEC v1).
// Rule records plus pack thresholds load from per-pack JSON (see
// RulePacks.cs); C# holds the shape only. Versions live in the pack files.
// This static is the SHIPPED base only. Live evaluation never reads it:
// the engine resolves the current pack set per evaluation through
// RulePackLoader.LoadCurrent, which overlays published pack files from
// the pack directory over these embedded bases. C# decides
// pass/block/hold/human-review; the model only explains.
// Tier 3 rules are documented here and never enforced.
// RuleTier lives in Gate.Contracts; RuleDefinition.Tier is that enum.
using Gate.Contracts;

namespace Gate.Compliance;

public enum ComplianceGate
{
    Vendor,
    Dock,
    PrePay
}

public sealed record RuleDefinition(
    string RuleId,
    string Pack,
    string PackVersion,
    RuleTier Tier,
    ComplianceGate Gate,
    string Title,
    string Source,
    bool Enforced,
    string Owner,
    DateOnly AdoptedDate,
    string? DocumentKind = null,
    string? DocumentLabel = null,
    // Memo-to-rule registry type for published rules (null for the shipped
    // base rules, which predate the registry).
    string? RuleType = null,
    // Published rule params (null when the rule carries none).
    IReadOnlyDictionary<string, string>? Params = null);

public static class RuleCatalog
{
    public const string CatalogVersion = "v1";

    private static readonly Lazy<RulePackSet> Loaded = new(RulePackLoader.LoadEmbedded);

    public static IReadOnlyList<RuleDefinition> Rules => Loaded.Value.Rules;

    public static CorpPackConfig Corp => Loaded.Value.Corp;

    public static RuleDefinition Find(string ruleId)
    {
        var rule = Rules.FirstOrDefault(r => r.RuleId == ruleId);
        if (rule is null)
        {
            throw new InvalidOperationException($"Unknown compliance rule id '{ruleId}' in catalog {CatalogVersion}.");
        }

        return rule;
    }

    /// <summary>Null when the id is not a catalog rule (tool names, match
    /// codes). Screens use this so unknown ids render honestly instead of
    /// throwing.</summary>
    public static RuleDefinition? TryFind(string? ruleId) =>
        string.IsNullOrWhiteSpace(ruleId)
            ? null
            : Rules.FirstOrDefault(r => r.RuleId == ruleId);
}
