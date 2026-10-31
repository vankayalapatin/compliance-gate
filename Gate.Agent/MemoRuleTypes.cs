// Agent-owned closed-registry type names. The agent cannot reference the
// compliance assembly (it depends on the engine interface only), so the
// names live here for prompt building, pack routing, and what-if config
// mapping. Every constant is pinned by test against MemoRuleRegistry:
// drift fails loudly instead of misleading Spark or misrouting publish.
namespace Gate.Agent;

public static class MemoRuleTypes
{
    public const string DocumentPresence = "DocumentPresence";
    public const string OriginShareCap = "OriginShareCap";
    public const string VendorShareCap = "VendorShareCap";
    public const string ScreeningMatch = "ScreeningMatch";
    public const string DockMarkPresence = "DockMarkPresence";
    public const string DockHandlingPresence = "DockHandlingPresence";
    public const string DamageReview = "DamageReview";
    public const string PrePayBlockOnOpen = "PrePayBlockOnOpen";

    public static IReadOnlyList<string> All { get; } =
    [
        DocumentPresence,
        OriginShareCap,
        VendorShareCap,
        ScreeningMatch,
        DockMarkPresence,
        DockHandlingPresence,
        DamageReview,
        PrePayBlockOnOpen,
    ];
}
