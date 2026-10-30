// CORP-02 override: works only by logged exception from a named approver.
// Approver, timestamp, and reason are all required and recorded on the verdict.
namespace Gate.Compliance;

public sealed record CorpExceptionApproval(
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
