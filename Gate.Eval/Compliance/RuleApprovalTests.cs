// Approval-gate validation: the approval pins the current proposal hash
// plus KB version (plus approver, timestamp, mandatory effective
// datetime, applicability mode). Mismatches refuse loudly. Test-double
// proposals and approvals only.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class RuleApprovalTests
{
    private static RuleProposal CapProposal() => MemoFixtures.Proposal(rule: MemoFixtures.CapRule());

    [Fact]
    public void MatchingPinsApprove()
    {
        var proposal = CapProposal();
        var result = ApprovalValidator.Validate(MemoFixtures.Approval(proposal), proposal, "kbv9");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ProposalHashMismatchRefusesQuotingBoth()
    {
        var proposal = CapProposal();
        var other = CapProposal() with { ProposalId = "PROP-2" };
        other = other with { ProposalHash = ProposalHasher.Hash(other) };
        var approval = MemoFixtures.Approval(other);
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("proposal hash") && e.Contains(other.ProposalHash));
    }

    [Fact]
    public void KbVersionMismatchRefusesWithRegrounding()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(proposal) with { KbVersion = "kbv8" };
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kbv8") && e.Contains("Re-ground"));
    }

    [Fact]
    public void CurrentKbMoveAfterProposalRefuses()
    {
        var proposal = CapProposal();
        var result = ApprovalValidator.Validate(MemoFixtures.Approval(proposal), proposal, "kbv10");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kbv10") && e.Contains("Re-ground"));
    }

    [Fact]
    public void MissingApproverTimestampAndEffectiveRefuse()
    {
        var proposal = CapProposal();
        var bare = new RuleApproval(
            proposal.ProposalHash, proposal.KbVersion, "", default,
            RuleApprovalOutcome.Approved, default, RuleApplicability.ProspectiveOnly, [], null, null);
        var result = ApprovalValidator.Validate(bare, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("approver"));
        Assert.Contains(result.Errors, e => e.Contains("timestamp"));
        Assert.Contains(result.Errors, e => e.Contains("effective datetime"));
    }

    [Fact]
    public void BackdatingWithoutReasonRefuses()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(proposal, effectiveAt: MemoFixtures.FixedAt.AddDays(-1));
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("predates") && e.Contains("recorded reason"));
    }

    [Fact]
    public void BackdatingWithReasonPasses()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(
            proposal, effectiveAt: MemoFixtures.FixedAt.AddDays(-1), comment: "Board ratified late; backdate recorded.");
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ImmediateHoldOnVendorRuleRefuses()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(
            proposal, applicability: RuleApplicability.ImmediateHold, comment: "Urgent.");
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("ImmediateHold") && e.Contains("dock-gate"));
    }

    [Fact]
    public void ImmediateHoldOnDockRuleWithoutReasonRefuses()
    {
        var proposal = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.DamageReview, MemoRuleGate.Dock, new Dictionary<string, string>()));
        var approval = MemoFixtures.Approval(proposal, applicability: RuleApplicability.ImmediateHold);
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("recorded reason"));
    }

    [Fact]
    public void ImmediateHoldOnDockRuleWithReasonPasses()
    {
        var proposal = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.DamageReview, MemoRuleGate.Dock, new Dictionary<string, string>()));
        var approval = MemoFixtures.Approval(
            proposal, applicability: RuleApplicability.ImmediateHold, comment: "Safety recall; freeze in-flight lots.");
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void OutOfRegistryEditRefuses()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(proposal, editedParams: new Dictionary<string, string>
        {
            ["cap"] = "1.50",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        });
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("Approver edit rejected") && e.Contains("cap"));
    }

    [Fact]
    public void InRegistryEditPasses()
    {
        var proposal = CapProposal();
        var approval = MemoFixtures.Approval(proposal, editedParams: new Dictionary<string, string>
        {
            ["cap"] = "0.50",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        });
        var result = ApprovalValidator.Validate(approval, proposal, "kbv9");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ApprovingNotMappableRefuses()
    {
        var proposal = MemoFixtures.Proposal(
            notMappable: new NotMappableDetail("VendorShareCap", "no share named", MissingCapability.Data));
        var result = ApprovalValidator.Validate(MemoFixtures.Approval(proposal), proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("NotMappable") && e.Contains("Rejected"));
    }

    [Fact]
    public void RejectingNotMappableClosesTheMemo()
    {
        var proposal = MemoFixtures.Proposal(
            notMappable: new NotMappableDetail("VendorShareCap", "no share named", MissingCapability.Data));
        var result = ApprovalValidator.Validate(
            MemoFixtures.Approval(proposal, outcome: RuleApprovalOutcome.Rejected, comment: "Unenforceable as worded."),
            proposal, "kbv9");
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void TamperedProposalRefusesAtTheGate()
    {
        var proposal = CapProposal() with { MemoId = "MEMO-2" };
        var approval = MemoFixtures.Approval(CapProposal());
        var fixedApproval = approval with { ProposalHash = proposal.ProposalHash };
        var result = ApprovalValidator.Validate(fixedApproval, proposal, "kbv9");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("hash mismatch"));
    }

    [Fact]
    public void EnsureValidRefusesLoudly()
    {
        var proposal = CapProposal();
        var bad = MemoFixtures.Approval(proposal) with { Approver = "" };
        var result = ApprovalValidator.Validate(bad, proposal, "kbv9");
        var ex = Assert.Throws<InvalidOperationException>(() => result.EnsureValid());
        Assert.Contains("approver", ex.Message);
    }
}
