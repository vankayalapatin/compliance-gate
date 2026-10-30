// Dock gate: photo-only carton evaluation. Transcription in, cited verdicts out.
// Photos never set quantities; quantities stay on the ERP receipt.
// BAT-LI-05 damage never auto-quarantines: human decides from model crops,
// and putaway plus pay stay blocked until the review closes.
using Gate.Contracts;

namespace Gate.Compliance;

/// <summary>One model transcription of one carton in one image. Flags only;
/// every judgment below runs in C#.</summary>
public sealed record CartonRead(
    string CartonId,
    string Part,
    string? PoNumber,
    string? LotCode,
    int UnitsPerCarton,
    bool MarkPresent,
    bool MarkLegible,
    string? UnNumber,
    bool Class9Required,
    bool Class9Visible,
    bool DamageVisible,
    string? DamageNote,
    string ImageId,
    decimal ReadConfidence);

public sealed class DockEvaluator
{
    public const string MarkRuleId = "BAT-LI-03";
    public const string Class9RuleId = "BAT-LI-04";
    public const string DamageRuleId = "BAT-LI-05";

    /// <summary>Evaluates every ASN carton in ASN order, then any read carton
    /// missing from the ASN in sorted order (never silently dropped).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Verdict>> EvaluateDock(
        IReadOnlyList<string> asnCartonIds,
        IReadOnlyList<CartonRead> reads)
    {
        var byCarton = reads
            .GroupBy(r => r.CartonId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CartonRead>)g.ToList(), StringComparer.Ordinal);
        var ordered = asnCartonIds
            .Concat(byCarton.Keys.Where(k => !asnCartonIds.Contains(k, StringComparer.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, IReadOnlyList<Verdict>>(StringComparer.Ordinal);
        foreach (var cartonId in ordered)
        {
            byCarton.TryGetValue(cartonId, out var cartonReads);
            result[cartonId] = EvaluateCarton(cartonId, cartonReads ?? []);
        }

        return result;
    }

    /// <summary>One carton, one verdict per dock rule, in rule-id order.</summary>
    public IReadOnlyList<Verdict> EvaluateCarton(string cartonId, IReadOnlyList<CartonRead> reads) =>
    [
        EvaluateMark(cartonId, reads),
        EvaluateClass9(cartonId, reads),
        EvaluateDamage(cartonId, reads)
    ];

    private static Verdict EvaluateMark(string cartonId, IReadOnlyList<CartonRead> reads)
    {
        var source = RuleCatalog.Find(MarkRuleId).Source;
        if (reads.Count == 0)
        {
            return new Verdict(MarkRuleId, VerdictKind.HumanReview,
                $"{cartonId} is on the ASN but was found in no image; route to human review (ASN reconciliation).",
                source);
        }

        if (reads.All(r => r.MarkPresent && r.MarkLegible))
        {
            return new Verdict(MarkRuleId, VerdictKind.Pass,
                $"{cartonId} shows the lithium-battery mark legible in {reads.Count} read(s).",
                source);
        }

        if (reads.All(r => !r.MarkPresent))
        {
            var images = string.Join(", ", reads.Select(r => r.ImageId).Distinct(StringComparer.Ordinal));
            return new Verdict(MarkRuleId, VerdictKind.Hold,
                $"{cartonId} shows no lithium-battery mark in {reads.Count} read(s) ({images}); hold for relabel.",
                source);
        }

        return new Verdict(MarkRuleId, VerdictKind.HumanReview,
            $"{cartonId} has illegible or conflicting mark reads; route to human review.",
            source);
    }

    private static Verdict EvaluateClass9(string cartonId, IReadOnlyList<CartonRead> reads)
    {
        var source = RuleCatalog.Find(Class9RuleId).Source;
        var required = reads.Any(r => r.Class9Required);
        if (!required)
        {
            return new Verdict(Class9RuleId, VerdictKind.Pass,
                $"{cartonId} does not require Class 9 handling marks.", source);
        }

        return reads.Any(r => r.Class9Visible)
            ? new Verdict(Class9RuleId, VerdictKind.Pass,
                $"{cartonId} shows the required Class 9 handling marks.", source)
            : new Verdict(Class9RuleId, VerdictKind.Hold,
                $"{cartonId} requires Class 9 handling marks but none is visible; hold.", source);
    }

    private static Verdict EvaluateDamage(string cartonId, IReadOnlyList<CartonRead> reads)
    {
        var source = RuleCatalog.Find(DamageRuleId).Source;
        var damaged = reads.Where(r => r.DamageVisible).ToList();
        if (damaged.Count == 0)
        {
            return new Verdict(DamageRuleId, VerdictKind.Pass,
                $"{cartonId} shows no visible outer damage.", source);
        }

        var note = damaged.Select(r => r.DamageNote).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var images = string.Join(", ", damaged.Select(r => r.ImageId).Distinct(StringComparer.Ordinal));
        return new Verdict(DamageRuleId, VerdictKind.HumanReview,
            $"{cartonId} shows suspected outer damage in {images}" +
            (note is null ? string.Empty : $" ({note})") +
            "; human decides quarantine or release from model crops. Putaway and pay stay blocked until review closes.",
            source);
    }
}
