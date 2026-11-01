// CORP-02 logged-exception flow (agent side). The compliance engine owns
// the origin-cap verdict; the agent owns the human grant flow: a CORP-02
// award block raises an ExceptionApproval card to the sourcing-policy matrix
// owner, a named-approver grant with reason re-screens the vendor with the
// exception and continues the award, a deny keeps the rejection. Every step
// timelines with rule, vendor, and approver.
//
// The grant record mirrors Gate.Compliance.CorpExceptionApproval until the
// contract unifies them (lead request): same fields, same validity rule
// (CORP-02 plus this vendor plus named approver plus reason). Agent-side
// excusing converts CORP-02 Blocks to Passes with the exception noted;
// the VENDOR-BLOCK marker is ignored for the pass check because the vendor
// was blocked as the consequence of the CORP-02 block, matching the award
// path's ScreeningBlockRuleIds.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>One CORP-02 logged exception grant: rule, vendor, named approver,
/// timestamp, and reason. All three of rule match, approver, and reason are
/// required; anything less still blocks.</summary>
public sealed record CorpExceptionGrant(
    string RuleId,
    string VendorId,
    string Approver,
    DateTimeOffset At,
    string Reason)
{
    public bool IsValidFor(string ruleId, string vendorId) =>
        RuleId == ruleId
        && VendorId == vendorId
        && !string.IsNullOrWhiteSpace(Approver)
        && !string.IsNullOrWhiteSpace(Reason);
}

public static class CorpExceptionFlow
{
    /// <summary>CORP origin-cap rule id. Mirrors the compliance engine's
    /// CorpOriginCapId; logic, not business data.</summary>
    public const string CorpOriginCapRuleId = "CORP-02";

    /// <summary>KB approval-matrix action routing CORP-02 exceptions.
    /// Data-driven: the owner comes from the matrix row, never code.</summary>
    public const string SourcingPolicyAction = "sourcing-policy";

    /// <summary>Decision document type for exception cards: the blocked
    /// vendor id travels in DocumentNumber.</summary>
    public const string VendorDocumentType = "Vendor";

    /// <summary>Excuses CORP-02 Blocks with a valid grant, noting the
    /// approver and reason on each converted verdict. An invalid grant
    /// returns the screening unchanged. Passed ignores the VENDOR-BLOCK
    /// marker (the block consequence, never a screening rule).</summary>
    public static VendorScreening ExcuseCorpBlock(
        VendorScreening screening, CorpExceptionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(screening);
        ArgumentNullException.ThrowIfNull(grant);

        if (!grant.IsValidFor(CorpOriginCapRuleId, screening.VendorId))
        {
            return screening;
        }

        var verdicts = screening.Verdicts.Select(v =>
            v.Kind == VerdictKind.Block && v.RuleId == CorpOriginCapRuleId
                ? v with
                {
                    Kind = VerdictKind.Pass,
                    Reason = $"{v.Reason} Overridden by logged exception from " +
                        $"{grant.Approver} at {grant.At:u}: {grant.Reason}.",
                }
                : v).ToList();
        var excused = new VendorScreening(screening.VendorId, false, verdicts);
        return excused with { Passed = IsPassIgnoringVendorBlock(excused) };
    }

    /// <summary>True when every verdict except the VENDOR-BLOCK marker is a
    /// Pass. Blocked vendors re-screen with the marker present; the marker
    /// is the consequence of the earlier block, not a rule to re-judge.</summary>
    public static bool IsPassIgnoringVendorBlock(VendorScreening screening)
    {
        ArgumentNullException.ThrowIfNull(screening);
        return screening.Verdicts
            .Where(v => !string.Equals(v.RuleId, ComplianceVerdictIds.VendorBlock, StringComparison.Ordinal))
            .All(v => v.Kind == VerdictKind.Pass);
    }

    /// <summary>Applies approved CORP-02 grants to fresh screenings: each
    /// failed vendor with an approved ExceptionApproval card for CORP-02 is
    /// re-evaluated with its grant. Returns the effective screenings (excused
    /// vendors replaced), the effective passing ids, and the grants used.
    /// Cards with blank approver or reason are ignored as invalid.</summary>
    public static (
        IReadOnlyList<VendorScreening> EffectiveScreenings,
        IReadOnlyList<string> EffectivePassing,
        IReadOnlyDictionary<string, CorpExceptionGrant> Grants)
        ApplyApprovedExceptions(
            CaseFile file, IReadOnlyList<VendorScreening> raw)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(raw);

        var effective = new List<VendorScreening>();
        var passing = new List<string>();
        var grants = new Dictionary<string, CorpExceptionGrant>(StringComparer.Ordinal);
        foreach (var screening in raw)
        {
            if (screening.Passed)
            {
                effective.Add(screening);
                passing.Add(screening.VendorId);
                continue;
            }

            var card = file.Decisions.FirstOrDefault(d =>
                d.Kind == DecisionKind.ExceptionApproval
                && d.Status == DecisionStatus.Approved
                && string.Equals(d.RuleId, CorpOriginCapRuleId, StringComparison.Ordinal)
                && string.Equals(d.DocumentNumber, screening.VendorId, StringComparison.Ordinal));
            if (card is null
                || string.IsNullOrWhiteSpace(card.DecidedBy)
                || string.IsNullOrWhiteSpace(card.Comment)
                || card.DecidedAt is null)
            {
                effective.Add(screening);
                continue;
            }

            var grant = new CorpExceptionGrant(
                card.RuleId!, card.DocumentNumber!, card.DecidedBy!, card.DecidedAt.Value, card.Comment!);
            var excused = ExcuseCorpBlock(screening, grant);
            effective.Add(excused);
            if (excused.Passed)
            {
                passing.Add(screening.VendorId);
                grants[screening.VendorId] = grant;
            }
        }

        return (effective, passing, grants);
    }
}
