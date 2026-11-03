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
    public const string LabelRuleId = "BAT-LI-06";

    /// <summary>Evaluates every ASN carton in ASN order, then any read carton
    /// missing from the ASN in sorted order (never silently dropped). The
    /// label check compares each read against the PO being received and the
    /// ASN lots: a stale photo set held against a new PO holds every carton.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Verdict>> EvaluateDock(
        IReadOnlyList<string> asnCartonIds,
        IReadOnlyList<CartonRead> reads,
        string expectedPoNumber,
        IReadOnlyDictionary<string, string> cartonLots)
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
            result[cartonId] = EvaluateCarton(cartonId, cartonReads ?? [], expectedPoNumber, cartonLots);
        }

        return result;
    }

    /// <summary>One carton, one verdict per dock rule, in rule-id order.</summary>
    public IReadOnlyList<Verdict> EvaluateCarton(
        string cartonId,
        IReadOnlyList<CartonRead> reads,
        string expectedPoNumber,
        IReadOnlyDictionary<string, string> cartonLots) =>
    [
        EvaluateMark(cartonId, reads),
        EvaluateClass9(cartonId, reads),
        EvaluateDamage(cartonId, reads),
        EvaluateLabel(cartonId, reads, expectedPoNumber, cartonLots)
    ];

    /// <summary>Label identity: the read PO must be the PO being received
    /// and the read lot must be the carton's ASN lot. Mismatches hold for
    /// reconciliation; an unreadable label goes to human review. Cartons
    /// with no reads have nothing to check and pass this rule.</summary>
    private static Verdict EvaluateLabel(
        string cartonId,
        IReadOnlyList<CartonRead> reads,
        string expectedPoNumber,
        IReadOnlyDictionary<string, string> cartonLots)
    {
        var source = RuleCatalog.Find(LabelRuleId).Source;
        if (reads.Count == 0)
        {
            return new Verdict(LabelRuleId, VerdictKind.Pass,
                $"{cartonId} has no label read to check.", source);
        }

        cartonLots.TryGetValue(cartonId, out var expectedLot);
        var holds = new List<string>();
        var reviews = new List<string>();
        foreach (var read in reads)
        {
            if (string.IsNullOrWhiteSpace(read.PoNumber))
            {
                reviews.Add($"{read.ImageId} shows no readable PO");
            }
            else if (!string.Equals(read.PoNumber, expectedPoNumber, StringComparison.Ordinal))
            {
                holds.Add($"{read.ImageId} labels PO {read.PoNumber}, not {expectedPoNumber}");
            }

            if (string.IsNullOrWhiteSpace(read.LotCode))
            {
                reviews.Add($"{read.ImageId} shows no readable lot");
            }
            else if (expectedLot is not null
                && !string.Equals(read.LotCode, expectedLot, StringComparison.Ordinal))
            {
                holds.Add($"{read.ImageId} labels lot {read.LotCode}, not ASN lot {expectedLot}");
            }
            else if (expectedLot is null
                && !cartonLots.Values.Contains(read.LotCode, StringComparer.Ordinal))
            {
                holds.Add($"{read.ImageId} labels lot {read.LotCode}, which is not on the ASN");
            }
        }

        if (holds.Count > 0)
        {
            return new Verdict(LabelRuleId, VerdictKind.Hold,
                $"{cartonId} label identity mismatch: {string.Join("; ", holds)}; hold for reconciliation.",
                source);
        }

        if (reviews.Count > 0)
        {
            return new Verdict(LabelRuleId, VerdictKind.HumanReview,
                $"{cartonId} label identity unreadable: {string.Join("; ", reviews)}; route to human review.",
                source);
        }

        return new Verdict(LabelRuleId, VerdictKind.Pass,
            $"{cartonId} labels PO {expectedPoNumber} lot {expectedLot ?? "on the ASN"}.", source);
    }

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
                $"{cartonId} shows the lithium-battery mark legible in {DisplayWords.Plural(reads.Count, "read")}.",
                source);
        }

        if (reads.All(r => !r.MarkPresent))
        {
            var images = string.Join(", ", reads.Select(r => r.ImageId).Distinct(StringComparer.Ordinal));
            return new Verdict(MarkRuleId, VerdictKind.Hold,
                $"{cartonId} shows no lithium-battery mark in {DisplayWords.Plural(reads.Count, "read")} ({images}); hold for relabel.",
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
