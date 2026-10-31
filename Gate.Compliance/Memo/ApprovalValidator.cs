// Approval-gate validation, stage 6 of docs/company-kb/MEMO-PIPELINE.md.
// A RuleApproval must pin the current proposal hash plus KB version (plus
// approver, timestamp, mandatory effective datetime, applicability mode).
// Mismatches refuse loudly. Backdating without a recorded reason refuses;
// ImmediateHold is safety/regulatory Tier-1 only, which in this registry
// means dock-gate proposals, and requires the recorded reason. Approver
// edits stay within the registry. Approving a NotMappable proposal is
// refused (nothing to publish); a rejection records the memo's close.
using Gate.Contracts;

namespace Gate.Compliance;

// ApprovalValidation lives in Gate.Contracts; this file holds the
// validator only.

public static class ApprovalValidator
{
    /// <summary>Validates the human approval pin against the proposal it
    /// claims plus the current KB version. A KB version change after
    /// proposal requires re-grounding before approval can proceed.</summary>
    public static ApprovalValidation Validate(
        RuleApproval approval, RuleProposal proposal, string currentKbVersion)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKbVersion);

        var errors = new List<string>();

        if (!ProposalHasher.Verify(proposal))
        {
            errors.Add($"Proposal hash mismatch: the proposal claims '{proposal.ProposalHash}' but the canonical " +
                "recomputation differs. The proposal was altered after hashing; re-ground it.");
            return new ApprovalValidation(false, errors);
        }

        if (!string.Equals(approval.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal))
        {
            errors.Add($"Approval pins proposal hash '{approval.ProposalHash}' but the current proposal hashes " +
                $"'{proposal.ProposalHash}'. The approval is for a different proposal; re-ground it.");
        }

        if (!string.Equals(approval.KbVersion, proposal.KbVersion, StringComparison.Ordinal))
        {
            errors.Add($"Approval pins KB version '{approval.KbVersion}' but the proposal pins '{proposal.KbVersion}'. " +
                "Re-ground the proposal against the current KB before approval can proceed.");
        }

        if (!string.Equals(proposal.KbVersion, currentKbVersion, StringComparison.Ordinal))
        {
            errors.Add($"KB version change after proposal: the proposal pins KB '{proposal.KbVersion}' " +
                $"but the current KB is '{currentKbVersion}'. Re-ground the proposal before approval can proceed.");
        }

        if (string.IsNullOrWhiteSpace(approval.Approver))
        {
            errors.Add("Approval names no approver; the KB approval matrix names who must decide.");
        }

        if (approval.DecidedAt == default)
        {
            errors.Add("Approval carries no decision timestamp.");
        }

        if (approval.EffectiveAt == default)
        {
            errors.Add("Approval carries no effective datetime; the effective datetime is mandatory on every approval.");
        }
        else if (approval.EffectiveAt < approval.DecidedAt && string.IsNullOrWhiteSpace(approval.Comment))
        {
            errors.Add($"Approval effective datetime {approval.EffectiveAt:u} predates the decision {approval.DecidedAt:u} " +
                "with no recorded reason; backdating requires a recorded reason.");
        }

        if (approval.Applicability == RuleApplicability.ImmediateHold)
        {
            if (proposal.ProposedRule is null || proposal.ProposedRule.Gate != MemoRuleGate.Dock)
            {
                errors.Add("Applicability ImmediateHold is safety/regulatory Tier-1 only, which means dock-gate " +
                    "proposals in this registry; this proposal is not a dock-gate rule. Use a prospective mode.");
            }

            if (string.IsNullOrWhiteSpace(approval.Comment))
            {
                errors.Add("Applicability ImmediateHold requires the named approver plus a recorded reason; no reason is recorded.");
            }
        }

        if (proposal.NotMappable is not null && approval.Outcome == RuleApprovalOutcome.Approved)
        {
            errors.Add("Cannot approve a NotMappable proposal: there is no rule to publish. " +
                "Record Rejected to close the memo, or re-propose once the blocking questions are answered.");
        }

        if (approval.EditedParams is not null)
        {
            if (proposal.ProposedRule is null)
            {
                errors.Add("Approval carries edited params but the proposal has no proposed rule to edit.");
            }
            else
            {
                foreach (var error in MemoRuleRegistry.ValidateParams(proposal.ProposedRule.RuleType, approval.EditedParams))
                {
                    errors.Add("Approver edit rejected: " + error);
                }
            }
        }

        if (approval.Conflicts is null)
        {
            errors.Add("Approval carries a null conflicts list; an empty list is required when nothing conflicts.");
        }

        return new ApprovalValidation(errors.Count == 0, errors);
    }
}
