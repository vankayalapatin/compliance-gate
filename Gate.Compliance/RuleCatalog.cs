// Versioned compliance rule catalog (PROJECT.md section 6, catalog v0.2).
// Rule records plus pack thresholds load from per-pack JSON (see
// RulePacks.cs); C# holds the shape and the expected version only.
// C# decides pass/block/hold/human-review; the model only explains.
// Tier 3 rules are documented here and never enforced in the demo.
namespace Gate.Compliance;

public enum RuleTier
{
    Tier1,
    Tier2,
    Tier3
}

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
    string? DocumentKind = null,
    string? DocumentLabel = null);

public static class RuleCatalog
{
    public const string CatalogVersion = "v0.2";

    /// <summary>Expected per-pack version. The loader refuses any pack file
    /// carrying a different version instead of mixing catalog versions.</summary>
    public const string PackVersion = "v0.2";

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
}
