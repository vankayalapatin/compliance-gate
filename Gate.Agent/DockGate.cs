// Dock-gate seam. The runner evaluates photo transcriptions through this
// interface so Gate.Agent keeps its contracts-only references: the C# dock
// rules live compliance-side, and the adapter mapping DockCartonRead to the
// compliance CartonRead lives wherever both assemblies meet (tests, app).
// Reads are transcription only; every judgment runs in C# behind this call.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Agent-side copy of one model transcription of one carton in one
/// image. Field-for-field with the compliance read; photos never set
/// quantities, quantities stay on the ERP receipt.</summary>
public sealed record DockCartonRead(
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

public interface IDockEvaluator
{
    /// <summary>Rule id of the lithium-mark gate (holds relabel work).</summary>
    string MarkRuleId { get; }

    /// <summary>Rule id of the Class 9 handling gate.</summary>
    string Class9RuleId { get; }

    /// <summary>Rule id of the damage gate (human review, never auto-hold).</summary>
    string DamageRuleId { get; }

    /// <summary>Rule id of the label-identity gate (read PO and lot must
    /// match the received PO and ASN lots).</summary>
    string LabelRuleId { get; }

    /// <summary>Evaluates every listed carton in order, then any read carton
    /// missing from the list in sorted order (never silently dropped). The
    /// label check compares each read against the PO being received and the
    /// carton-to-lot map from the ASN.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<Verdict>> EvaluateDock(
        IReadOnlyList<string> asnCartonIds,
        IReadOnlyList<DockCartonRead> reads,
        string expectedPoNumber,
        IReadOnlyDictionary<string, string> cartonLots);
}
