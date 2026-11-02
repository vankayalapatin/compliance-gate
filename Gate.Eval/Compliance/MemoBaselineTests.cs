// MEMO-0001 platform baseline: the hand-crafted
// Gate.Web/data/baseline/platform/memo-0001.memo.json loads through the real MemoStore
// seam as a MemoFile and carries the company-data story: Dana's September 15
// memo, the clean PROP-0001 round, Finn's September 24 approval, and the
// ACT-0012/ACT-0013 activations effective October 1.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoBaselineTests
{
    private static MemoFile LoadBaseline()
    {
        var file = CompanyKbLoader.ResolvePath("Gate.Web/data/baseline/platform/memo-0001.memo.json");
        Assert.True(File.Exists(file), $"Baseline memo file not found: {file}");
        var memo = MemoStore.Load(Path.GetDirectoryName(file)!, "memo-0001")!;
        Assert.NotNull(memo);
        return memo;
    }

    private static KbSnapshot LoadKbv2Snapshot()
    {
        var path = CompanyKbLoader.ResolvePath("company/company-kb.kbv2.json");
        return CompanyKbLoader.Load(File.ReadAllText(path)).ToSnapshot();
    }

    [Fact]
    public void MemoCarriesDanaSeptember15Excerpt()
    {
        var memo = LoadBaseline();
        Assert.Equal("MEMO-0001", memo.Memo.MemoId);
        Assert.Equal("Dana Director", memo.Memo.Author);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero), memo.Memo.ReceivedAt);
        Assert.Contains("no more than 60%", memo.Memo.Excerpt);
        Assert.Contains("no more than 50%", memo.Memo.Excerpt);
        Assert.Contains("Exceptions need Finance approval and stay logged.", memo.Memo.Excerpt);
        Assert.Contains(memo.Memo.Excerpt, memo.FullText);
    }

    [Fact]
    public void ProposalIsCleanValidatedRoundWithWhatIf()
    {
        var memo = LoadBaseline();
        var record = Assert.Single(memo.Proposals);
        Assert.Equal("Dana Director", record.PolicyOwner);

        var proposal = record.Proposal;
        Assert.Equal("PROP-0001", proposal.ProposalId);
        Assert.Equal("MEMO-0001", proposal.MemoId);
        Assert.Equal("kbv2", proposal.KbVersion);
        Assert.Equal(LoadKbv2Snapshot().KbHash, proposal.KbHash);
        Assert.Empty(proposal.Questions);
        var rule = proposal.ProposedRule!;
        Assert.NotNull(rule);
        Assert.Null(proposal.NotMappable);
        Assert.Equal(MemoRuleRegistry.OriginShareCap, rule.RuleType);
        Assert.Equal(MemoRuleGate.Vendor, rule.Gate);
        Assert.Equal("0.60", rule.Params["cap"]);
        Assert.Equal("12", rule.Params["windowMonths"]);
        Assert.Equal("critical", rule.Params["partScope"]);
        Assert.True(ProposalHasher.Verify(proposal));

        Assert.True(record.Validation.Ok);
        Assert.Empty(record.Validation.Errors);
        Assert.Empty(record.Validation.Conflicts);

        var whatIf = record.WhatIf!;
        Assert.NotNull(whatIf);
        Assert.Equal(proposal.ProposalHash, whatIf.ProposalHash);
        Assert.Equal("kbv2", whatIf.KbVersion);
        Assert.Equal(WhatIfCalculator.TrailingTwelveMonthLabel, whatIf.HistoryLabel);
        Assert.Equal(4, whatIf.HistoryRowCount);
        Assert.Empty(whatIf.HistoryFlips);
        Assert.Empty(whatIf.Flips);
        Assert.False(whatIf.Unviable);
    }

    [Fact]
    public void ProposalStillValidatesAgainstKbv2Snapshot()
    {
        var memo = LoadBaseline();
        var proposal = Assert.Single(memo.Proposals).Proposal;
        var snapshot = LoadKbv2Snapshot();

        var structural = ProposalValidator.ValidateStructure(proposal);
        Assert.True(structural.Ok, string.Join("; ", structural.Errors));
        Assert.Contains(structural.Conflicts, c =>
            c.RuleId == "CORP-02" && c.Kind == RuleConflictKind.Subsumes);

        var againstKb = ProposalValidator.ValidateAgainstKb(proposal, snapshot);
        Assert.True(againstKb.Ok, string.Join("; ", againstKb.Errors));
    }

    [Fact]
    public void FinnApprovedSeptember24()
    {
        var memo = LoadBaseline();
        var decision = Assert.Single(memo.Decisions);
        Assert.Equal("MEMO-0001-d001", decision.DecisionId);
        Assert.Equal(DecisionKind.PolicyApproval, decision.Kind);
        Assert.Equal(DecisionStatus.Approved, decision.Status);
        Assert.Equal("Finn Finance", decision.RequestedTo);
        Assert.Equal("Finn Finance", decision.DecidedBy);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero), decision.DecidedAt);
        Assert.Equal("PROP-0001", decision.DocumentNumber);
    }

    [Fact]
    public void ActivationsPublishCorp01And02EffectiveOctober1()
    {
        var memo = LoadBaseline();
        var proposalHash = Assert.Single(memo.Proposals).Proposal.ProposalHash;
        Assert.Equal(2, memo.Activations.Count);

        var vendor = memo.Activations.Single(a => a.ActivationId == "ACT-0012");
        Assert.Equal("CORP-01", vendor.RuleId);
        var origin = memo.Activations.Single(a => a.ActivationId == "ACT-0013");
        Assert.Equal("CORP-02", origin.RuleId);

        foreach (var activation in memo.Activations)
        {
            Assert.Equal("CORP", activation.PackId);
            Assert.Equal("v0.2", activation.NewPackVersion);
            Assert.Equal("MEMO-0001", activation.MemoId);
            Assert.Equal("Finn Finance", activation.Approver);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), activation.EffectiveAt);
            Assert.Equal(RuleApplicability.ProspectiveOnly, activation.Applicability);
            Assert.Equal(proposalHash, activation.ProposalHash);
            Assert.Equal("kbv2", activation.KbVersion);
        }
    }

    [Fact]
    public void EventsTellIntakeToAdoption()
    {
        var memo = LoadBaseline();
        Assert.Equal(5, memo.Events.Count);
        Assert.All(memo.Events, e => Assert.Equal("MEMO-0001", e.CaseId));
        Assert.Equal("Dana Director", memo.Events[0].Actor);
        Assert.Contains("received", memo.Events[0].Summary);
        Assert.Contains("PROP-0001", memo.Events[1].Summary);
        Assert.Contains("approved", memo.Events[2].Summary);
        Assert.Contains("ACT-0013", memo.Events[3].Summary);
        Assert.Contains("ACT-0012", memo.Events[3].Summary);
        Assert.Contains("sourcing-policy", memo.Events[4].Summary);
    }
}
