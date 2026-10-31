// What-if gather tests: history quantities, open-PO pipeline rows, config
// mapping, and the approval-card flow. The five list-read paths (open PRs,
// draft POs, open invoices, open payments, unreceived ASNs) plus the lot
// map, quarantine flags, join caveats, and the rollout fallback live in
// WhatIfGatherListReadsTests.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoWhatIfGatherTests
{
    [Fact]
    public async Task HistoryRowsCarryQuantitiesOnly()
    {
        var h = MemoTestKit.Create();
        h.Erp.SeedVolumeHistory("BAT-9",
            [new VolumeHistory("Seeded lot A", "US", 120), new VolumeHistory("Seeded lot B", "MX", 80)]);

        var gather = new MemoWhatIfGather(h.Erp);
        var rows = await gather.GatherHistoryAsync(MemoFixtures.TestKb());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("BAT-9", r.ItemId);
            Assert.True(r.IsCritical);
            Assert.Null(r.VendorId);
            Assert.Equal(0m, r.UnitPrice);
            Assert.Null(r.Currency);
            Assert.Null(r.AwardDate);
            Assert.False(r.DocPresent);
        });
        Assert.Equal(120m, rows[0].Quantity);
        Assert.Equal("US", rows[0].Country);
        Assert.Contains("Seeded lot A", rows[0].DocumentNumber, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineRowsCoverOpenPoLinesWithRealDocsAndHolds()
    {
        var erp = new WhatIfListReadDouble(SeededErp.Create());
        var pr = await erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("SCR-9", 5, "EA")], "memo-whatif-pr");
        Assert.Equal(PrStatus.Approved, pr.Status);
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-C", "test", "planner", "memo-whatif-po");
        var proposal = MemoFixtures.Proposal(rule: MemoFixtures.CapRule());

        var gather = new MemoWhatIfGather(erp);
        var (rows, caveats) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), proposal);

        var row = Assert.Single(rows);
        Assert.Equal("PO", row.DocumentType);
        Assert.Equal(po.PoNumber, row.DocumentNumber);
        Assert.Equal("SCR-9", row.ItemId);
        Assert.False(row.IsCritical);
        Assert.Equal("V-C", row.VendorId);
        Assert.Equal("US", row.Country);
        Assert.Equal(5m, row.Quantity);
        Assert.Equal(1m, row.UnitPrice);
        Assert.Equal("USD", row.Currency);
        Assert.False(row.HasOpenHold);
        Assert.False(row.HasQuarantine);
        Assert.DoesNotContain(caveats, c => c.Contains("Open PRs", StringComparison.Ordinal));
        Assert.DoesNotContain(caveats, c => c.Contains("Draft POs", StringComparison.Ordinal));
        Assert.DoesNotContain(caveats, c => c.Contains("invoices", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(caveats, c => c.Contains("payments", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(caveats, c => c.Contains("ASN", StringComparison.Ordinal));
        Assert.Equal(2, caveats.Count);
        Assert.Contains(caveats, c => c.Contains("dock observables", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("eceipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentPresenceReadsRealVendorDocuments()
    {
        var h = MemoTestKit.Create();
        h.Erp.SeedVendorDocuments("V-C",
            [new VendorDocumentDto("V-C", "COO-Declaration", "coo-c", true)]);
        var pr = await h.Erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("SCR-9", 5, "EA")], "memo-doc-pr");
        await h.Erp.CreatePoAsync(pr.PrNumber, "V-C", "test", "planner", "memo-doc-po");
        var proposal = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "all" }));

        var gather = new MemoWhatIfGather(h.Erp);
        var (rows, _) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), proposal);

        Assert.True(Assert.Single(rows).DocPresent);
    }

    [Fact]
    public async Task HoldFlagReadsRealOpenHolds()
    {
        var h = MemoTestKit.Create();
        var pr = await h.Erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("SCR-9", 5, "EA")], "memo-hold-pr");
        var po = await h.Erp.CreatePoAsync(pr.PrNumber, "V-C", "test", "planner", "memo-hold-po");
        await h.Erp.SetHoldAsync("REVIEW", "look", null, "case-t", "planner",
            "PO", po.PoNumber, null, null, null, "memo-hold-1");
        var proposal = MemoFixtures.Proposal(rule: MemoFixtures.CapRule());

        var gather = new MemoWhatIfGather(h.Erp);
        var (rows, _) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), proposal);

        Assert.True(Assert.Single(rows).HasOpenHold);
    }

    [Fact]
    public void NewConfigComesFromTheProposedRule()
    {
        var h = MemoTestKit.Create();
        var proposal = MemoFixtures.Proposal(rule: MemoFixtures.CapRule(cap: "0.40"));

        var config = new MemoWhatIfGather(h.Erp).BuildNewConfig(proposal);

        Assert.Equal(MemoRuleRegistry.OriginShareCap, config.RuleType);
        Assert.Equal("0.40", config.Params["cap"]);
    }

    [Fact]
    public void NewConfigRefusesNotMappable()
    {
        var h = MemoTestKit.Create();
        var proposal = MemoFixtures.Proposal(
            notMappable: new NotMappableDetail("VendorShareCap", "no threshold", MissingCapability.Data));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MemoWhatIfGather(h.Erp).BuildNewConfig(proposal));

        Assert.Contains("not computed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OldConfigReadsTheKbSourcingPolicy()
    {
        var h = MemoTestKit.Create();
        var proposal = MemoFixtures.Proposal(rule: MemoFixtures.CapRule());
        var validation = new ProposalValidation(true, [],
            [new RuleConflict("CORP-02", RuleConflictKind.Tightens, "tighter")]);

        var (oldConfig, oldRuleId, caveat) = new MemoWhatIfGather(h.Erp)
            .BuildOldConfig(proposal, validation, MemoFixtures.TestKb());

        Assert.NotNull(oldConfig);
        Assert.Equal("0.60", oldConfig.Params["cap"]);
        Assert.Equal("CORP-02", oldRuleId);
        Assert.Null(caveat);
    }

    [Fact]
    public void OldConfigIsAbsentWithoutConflicts()
    {
        var h = MemoTestKit.Create();
        var proposal = MemoFixtures.Proposal(rule: MemoFixtures.CapRule());
        var validation = new ProposalValidation(true, [], []);

        var (oldConfig, oldRuleId, caveat) = new MemoWhatIfGather(h.Erp)
            .BuildOldConfig(proposal, validation, MemoFixtures.TestKb());

        Assert.Null(oldConfig);
        Assert.Null(oldRuleId);
        Assert.Null(caveat);
    }

    [Fact]
    public void NonCapConflictCaveatsTheMissingBaseline()
    {
        var h = MemoTestKit.Create();
        var proposal = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" }));
        var validation = new ProposalValidation(true, [],
            [new RuleConflict("BAT-LI-01", RuleConflictKind.Tightens, "wider")]);

        var (oldConfig, oldRuleId, caveat) = new MemoWhatIfGather(h.Erp)
            .BuildOldConfig(proposal, validation, MemoFixtures.TestKb());

        Assert.Null(oldConfig);
        Assert.Null(oldRuleId);
        Assert.Contains("BAT-LI-01", caveat, StringComparison.Ordinal);
        Assert.Contains("block-to-pass flips are not computed", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvanceComputesWhatIfAndRaisesTheCard()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson());

        var outcome = await h.Runner.AdvanceAsync("MEMO-T1");

        Assert.Equal(MemoNextStep.AwaitApproval, outcome.Step);
        Assert.False(outcome.Complete);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        var record = Assert.Single(file.Proposals);
        Assert.NotNull(record.WhatIf);
        Assert.Equal(record.Proposal.ProposalHash, record.WhatIf.ProposalHash);
        Assert.Equal("kbv9", record.WhatIf.KbVersion);
        Assert.Contains("trailing-12-month approximation", record.WhatIf.HistoryLabel, StringComparison.Ordinal);
        var card = Assert.Single(file.Decisions);
        Assert.Equal(DecisionKind.PolicyApproval, card.Kind);
        Assert.Equal("Terry Owner", card.RequestedTo);
        Assert.Equal(record.Proposal.ProposalHash, card.DocumentNumber);
        Assert.Contains("40%", card.Reason, StringComparison.Ordinal);
        Assert.Contains("CORP-02", card.Reason, StringComparison.Ordinal);
        Assert.Contains("What-if:", card.Reason, StringComparison.Ordinal);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("What-if", StringComparison.Ordinal)
            && e.Summary.Contains("Caveats:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotMappableSkipsWhatIfAndRaisesCloseOutCard()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h, "MEMO-T2");
        h.Models.QueueText(MemoTestKit.NotMappableJson());
        await h.Runner.ProposeAsync("MEMO-T2");
        var question = Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T2")!.Decisions);
        h.Models.QueueText(MemoTestKit.NotMappableResolvedJson());

        await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T2", question.DecisionId, true, "Terry Owner", "50%");
        var outcome = await h.Runner.AdvanceAsync("MEMO-T2");

        Assert.Equal(MemoNextStep.AwaitApproval, outcome.Step);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T2")!;
        Assert.Equal(2, file.Proposals.Count);
        var current = file.Proposals.Last();
        Assert.True(current.Validation.Ok);
        Assert.Null(current.Proposal.ProposedRule);
        Assert.Null(current.WhatIf);
        var card = file.Decisions.Single(d => d.Kind == DecisionKind.PolicyApproval);
        Assert.Contains("NotMappable", card.Reason, StringComparison.Ordinal);
        Assert.Contains("not computed", card.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ComputeWhatIf", h.Compliance.MemoCalls);
    }
}
