// Agent scorecard tests: the builder counts tool calls, refusals, rule
// steps, and model usage from the case's own transcript and timeline with
// costs from configured rates; the case service wires ERP approvals in.
// Test data is local; rates are test-local config values.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class ScorecardTests
{
    private static ModelSpendTracker Tracker(string? inputRate, string? outputRate)
    {
        var values = new Dictionary<string, string?>();
        if (inputRate is not null)
        {
            values["ModelSpend:InputCostPerMillionUsd"] = inputRate;
        }

        if (outputRate is not null)
        {
            values["ModelSpend:OutputCostPerMillionUsd"] = outputRate;
        }

        return new ModelSpendTracker(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void TokenCostIsBallparkFromConfiguredRates()
    {
        var spend = Tracker("1.25", "4.25");

        Assert.Equal(0.0185m, spend.CostUsd(8000, 2000));
        Assert.True(spend.HasTokenRates);
        Assert.Equal("about $0.02", Scorecard.FormatCost(0.0185m));
        Assert.Equal("about $0.0003", Scorecard.FormatCost(0.0003375m));
        Assert.Equal("1,234,567", Scorecard.FormatTokens(1234567));
    }

    [Fact]
    public void TokenCostIsNullWithoutRates()
    {
        Assert.Null(Tracker(null, null).CostUsd(8000, 2000));
        Assert.False(Tracker(null, null).HasTokenRates);
        Assert.Null(Tracker("1.25", null).CostUsd(8000, 2000));
    }

    [Fact]
    public void BuildCountsCallsRefusalsRuleStepsAndUsage()
    {
        var now = DateTimeOffset.UtcNow;
        var turns = new List<TranscriptTurn>
        {
            new(1, now.AddMinutes(-9), "Created", nameof(CaseNextStep.ComputeShortfall), "shortfall",
                [new TranscriptToolCall("c1", AgentTools.ComputeShortfall, "{}", ToolCallVerdict.Executed, "ok")],
                ["done"], null, "spark", 4000, 1000, false),
            new(2, now.AddMinutes(-8), "ShortfallComputed", nameof(CaseNextStep.RaisePr), "raise",
                [
                    new TranscriptToolCall("c2", AgentTools.RaisePr, "{}", ToolCallVerdict.Blocked, "Illegal move: no."),
                    new TranscriptToolCall("c3", AgentTools.RaisePr, "{}", ToolCallVerdict.Executed, "ok"),
                ],
                ["done"], null, "spark", 4000, 1000, false),
            new(3, now.AddMinutes(-7), "PrApproved", nameof(CaseNextStep.DraftPayment), "deferred",
                [new TranscriptToolCall("-", AgentTools.DraftPayment, "{}", ToolCallVerdict.ModelDeferred, "ran it")],
                ["done"], "Model-deferred turn.", "spark", null, null, true),
        };
        var file = new CaseFile(
            new Case("CASE-SC", "SO-1", "COIL-3", "ui-test", CaseStage.PaymentRequested, now, now,
                "PR-9", "PO-9", null, null, [], [], null, null),
            [
                new CaseEvent("e1", "CASE-SC", now.AddMinutes(-6), RecordSource.P2pAgent,
                    CaseEventKind.Note, "Payment drafted by rule after two refused agent calls.",
                    null, null, null, null, null, null),
            ],
            []);
        var pr = new PurchaseRequisition("PR-9", PrStatus.Closed, "planner", "USD", 100m,
            RecordSource.P2pAgent, "planner", "k", [], ApprovedBy: ErpApprovalActors.AutoApprovePolicy);

        var card = Scorecard.Build(turns, file, pr, null, 500m, 1.25m, 4.25m);

        Assert.Equal(3, card.ToolCallsMade);
        Assert.Equal(2, card.ToolCallsExecuted);
        var refusal = Assert.Single(card.Refused);
        Assert.Equal(AgentTools.RaisePr, refusal.Tool);
        Assert.Equal("Illegal move: no.", refusal.Reason);
        Assert.Empty(card.Failed);
        Assert.Equal(2, card.RuleSteps.Count);
        Assert.Contains(card.RuleSteps, s => s.Title == "Drafting the payment");
        Assert.Contains(card.RuleSteps, s => s.Title == "Rule fallback");
        var policy = Assert.Single(card.PolicyApprovals);
        Assert.Equal("Request PR-9", policy.Document);
        Assert.Contains("under $500.00", policy.Detail);
        Assert.Equal(3, card.ModelCalls);
        Assert.Equal(2, card.LiveModelCalls);
        Assert.Equal(1, card.CachedModelCalls);
        Assert.Equal(8000, card.InputTokens);
        Assert.Equal(2000, card.OutputTokens);
        Assert.Equal(0.0185m, card.EstimatedCostUsd);
        Assert.True(card.RatesConfigured);
    }

    [Fact]
    public void BuildAddsTheVisionLineWithItsOwnCost()
    {
        var now = DateTimeOffset.UtcNow;
        var file = UiTestSupport.CaseFileWith("CASE-SC", "SO-1", "COIL-3");
        var vision = new List<VisionPhotoUsage>
        {
            new("IMG-1", VisionReadCache.CartonReadV1, "spark", 8000, 2000, false, now),
            new("IMG-2", VisionReadCache.CartonReadV1, "spark", null, null, true, now),
        };

        var card = Scorecard.Build([], file, null, null, null, 1.25m, 4.25m, vision);

        Assert.NotNull(card.VisionReads);
        Assert.Equal(2, card.VisionReads.Photos);
        Assert.Equal(8000, card.VisionReads.InputTokens);
        Assert.Equal(2000, card.VisionReads.OutputTokens);
        Assert.Equal(0.0185m, card.VisionReads.EstimatedCostUsd);
        Assert.Equal(0, card.ModelCalls);
    }

    [Fact]
    public void BuildOmitsTheVisionLineWithoutReads()
    {
        var file = UiTestSupport.CaseFileWith("CASE-SC", "SO-1", "COIL-3");

        Assert.Null(Scorecard.Build([], file, null, null, null, 1.25m, 4.25m).VisionReads);
        Assert.Null(Scorecard.Build([], file, null, null, null, 1.25m, 4.25m, []).VisionReads);
    }

    [Fact]
    public void BuildWithoutRatesShowsCallsAndTokensOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var file = UiTestSupport.CaseFileWith("CASE-SC", "SO-1", "COIL-3");
        var turns = new List<TranscriptTurn>
        {
            new(1, now, "Created", nameof(CaseNextStep.ComputeShortfall), "r",
                [new TranscriptToolCall("c1", AgentTools.ComputeShortfall, "{}", ToolCallVerdict.Executed, "ok")],
                [], null, "spark", 100, 50, false),
        };

        var card = Scorecard.Build(turns, file, null, null, null, null, null);

        Assert.Null(card.EstimatedCostUsd);
        Assert.False(card.RatesConfigured);
        Assert.Equal(100, card.InputTokens);
        Assert.Equal(50, card.OutputTokens);
        Assert.Empty(card.PolicyApprovals);
    }

    [Fact]
    public async Task CaseDetailCarriesTheScorecardForAnUnfamiliarItem()
    {
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("COIL-3", "Copper coil", false, 5, "EA"));
        erp.SeedVendor(new VendorDto("V-Z", "Zed Parts AB", "SE", false, null, "NET0"));
        erp.SeedVendorPrice("V-Z", "COIL-3", 2m);
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("COIL-3", 10m, "EA")],
            Guid.NewGuid().ToString("N"));
        var po = await erp.CreatePoAsync(
            pr.PrNumber, "V-Z", RecordSource.P2pAgent, "planner", Guid.NewGuid().ToString("N"));
        var dir = UiTestSupport.NewDir("gate-ui-scorecard-case");
        var cacheDir = Path.Combine(dir, "case-cache");
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-SC9", "SO-77", "COIL-3", "ui-test", CaseStage.PoPlaced, now, now,
                pr.PrNumber, po.PoNumber, null, null, [], [], null, null, AwardedVendorId: "V-Z"),
            [
                new CaseEvent("e1", "CASE-SC9", now.AddMinutes(-5), RecordSource.P2pAgent,
                    CaseEventKind.PoCreated, "PO placed.", null, "PO", po.PoNumber, null, null, null),
            ],
            []));
        CaseTranscript.AppendTurn(cacheDir, "CASE-SC9", new TranscriptTurn(
            1, now.AddMinutes(-6), "PoPlaced", nameof(CaseNextStep.CreatePo), "place",
            [
                new TranscriptToolCall("c1", AgentTools.CreatePo, "{}", ToolCallVerdict.Blocked, "Illegal move: no."),
                new TranscriptToolCall("c2", AgentTools.CreatePo, "{}", ToolCallVerdict.Executed, "ok"),
            ],
            ["done"], null, "spark", 8000, 2000, false));
        CaseVisionUsage.AppendPhoto(cacheDir, "CASE-SC9", new VisionPhotoUsage(
            "IMG-1", VisionReadCache.CartonReadV1, "spark", 1200, 300, false, now));
        var views = new CaseViewService(
            erp, new CaseCacheStore(cacheDir),
            new DockPhotoService(Path.Combine(dir, "img"), Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance, Tracker("1.25", "4.25"));

        var detail = await views.GetCaseDetailAsync("CASE-SC9");

        Assert.NotNull(detail);
        Assert.Equal(2, detail.Scorecard.ToolCallsMade);
        Assert.Equal(1, detail.Scorecard.ToolCallsExecuted);
        Assert.Equal("Illegal move: no.", Assert.Single(detail.Scorecard.Refused).Reason);
        Assert.Equal(2, detail.Scorecard.PolicyApprovals.Count);
        Assert.Equal(1, detail.Scorecard.ModelCalls);
        Assert.Equal(0.0185m, detail.Scorecard.EstimatedCostUsd);
        Assert.NotNull(detail.Scorecard.VisionReads);
        Assert.Equal(1, detail.Scorecard.VisionReads.Photos);
        Assert.Equal(1200, detail.Scorecard.VisionReads.InputTokens);
        Assert.Equal((1200m * 1.25m + 300m * 4.25m) / 1_000_000m, detail.Scorecard.VisionReads.EstimatedCostUsd);
    }
}
