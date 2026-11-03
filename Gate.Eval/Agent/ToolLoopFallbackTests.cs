// Tool-loop fallback tests (offline, scripted model plus real ERP and
// compliance): three turns in a row with refused calls and no executed
// action hand the picked step to C#, which logs the fallback on the
// timeline. At the split step the fallback is a single-vendor award to
// the cheapest passing vendor; illegal calls report legality before
// coaching arguments. Test-double data only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval.VideoProbes;

namespace Gate.Eval.Agent;

public sealed class ToolLoopFallbackTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, P2pAgent Core, ProbeModelClient Models, CaseRunner Runner, string CaseDir);

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        // (5,600 + 1,200) / (10,000 + 1,200) = 60.7%: CinderVolt blocks
        // for the full 1,200 while Meridian passes, so screening routes
        // the case to the split step.
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 5600),
            new VolumeHistory("V-B MX active", "MX", 4000),
            new VolumeHistory("Other non-CN", "OTHER", 400),
        ]);
        erp.SeedSalesOrder(new SalesOrder("SO-FALLBACK-1", SalesOrderStatus.Confirmed, "USD",
            [new SalesOrderLine(SeededErp.Glasses, 1210)]));
        var models = new ProbeModelClient();
        var compliance = new ComplianceEngine();
        var core = new P2pAgent(erp, models, compliance);
        var dir = Path.Combine(Path.GetTempPath(), "gate-fallback-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(
            erp, core, models, compliance, new DockEvaluatorAdapter(), dir,
            countryNames: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CN"] = "China",
                ["MX"] = "Mexico",
                ["US"] = "United States",
            });
        return new Harness(erp, core, models, runner, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates(params string[] battery) =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = battery,
            [SeededErp.Screw] = ["V-C"],
        };

    private static string Args(string caseId) => $"{{\"case_id\":\"{caseId}\"}}";

    private static async Task<string> OpenBatteryAsync(Harness h, params string[] battery)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-FALLBACK-1", "planner", Candidates(battery)));
        var id = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        h.Models.QueueText("shortfall");
        h.Models.QueueText("raise");
        await h.Runner.AdvanceAsync(id);
        var raised = CaseStore.Load(h.CaseDir, id)!;
        var pr = raised.Decisions.Single(d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(id, pr.DecisionId, true, pr.RequestedTo, "ok");
        return id;
    }

    private static void QueueBadAward(ProbeModelClient models, int n)
    {
        // Malformed and illegal on a propose turn: no case id, and the
        // award tool is not offered there.
        models.QueueToolCalls($"bad award {n}", ($"b{n}", AgentTools.AwardSupplier, "{}"));
    }

    [Fact]
    public async Task SplitFallbackAwardsCheapestPassingAfterThreeRefusals()
    {
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        h.Models.QueueText("screen");
        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        QueueBadAward(h.Models, 3);
        h.Models.QueueToolCalls("po", ("c1", AgentTools.CreatePo, Args(battery)));
        h.Models.QueueToolCalls("submit", ("c2", AgentTools.SubmitPoApproval, Args(battery)));

        var parked = await h.Runner.AdvanceAsync(battery);

        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Equal("V-B", file.Case.AwardedVendorId);
        Assert.DoesNotContain(
            file.Decisions, d => d.Kind == DecisionKind.SplitApproval);
        var award = file.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("Cheapest passing vendor V-B", award.Summary, StringComparison.Ordinal);
        Assert.Contains(
            file.Events,
            e => e.Kind == CaseEventKind.Note
                && e.Summary == "After three refused tries, propose a split ran by rule instead.");
        var first = CaseTranscript.ReadTurns(h.CaseDir, battery)
            .SelectMany(t => t.Calls)
            .First(c => c.Name == AgentTools.AwardSupplier);
        Assert.Equal(ToolCallVerdict.Blocked, first.Verdict);
        Assert.Contains("Illegal move", first.Detail, StringComparison.Ordinal);
        Assert.Contains(AgentTools.ProposeSplit, first.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("case_id must be", first.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortBrokenRunUnderEveryLimitNeverFallsBack()
    {
        // Two refusals, a read, two more refusals: the refusal count
        // resets on the read, nothing repeats thrice, and five idle
        // turns stay under the eight-turn breaker, so the agent keeps
        // the step and its valid proposal stands.
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        h.Models.QueueText("screen");
        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        h.Models.QueueToolCalls("read first", ("r1", AgentTools.GetCase, Args(battery)));
        QueueBadAward(h.Models, 3);
        QueueBadAward(h.Models, 4);
        h.Models.QueueToolCalls("split 800/400",
            ("p1", AgentTools.ProposeSplit,
            $"{{\"case_id\":\"{battery}\",\"vendor_ids\":[\"V-A\",\"V-B\"],\"quantities\":[800,400]," +
            $"\"note\":\"keep dual supply for resilience\"}}"));

        var parked = await h.Runner.AdvanceAsync(battery);

        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Contains(
            file.Decisions,
            d => d.Kind == DecisionKind.SplitApproval && d.Status == DecisionStatus.Pending);
        Assert.DoesNotContain(
            file.Events,
            e => e.Kind == CaseEventKind.Note && e.Summary.Contains("ran by rule instead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScreenFallbackRunsStepByRuleAfterThreeRefusals()
    {
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        QueueBadAward(h.Models, 3);

        // The fallback screens by rule; the next turn then needs a model
        // answer that was never queued, so the advance ends there.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));
        Assert.Contains("failed after one retry", thrown.Message, StringComparison.Ordinal);

        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Contains(file.Events, e => e.Kind == CaseEventKind.VendorScreened);
        Assert.Contains(
            file.Events,
            e => e.Kind == CaseEventKind.Note
                && e.Summary == "After three refused tries, screen vendors ran by rule instead.");
    }

    [Fact]
    public async Task SplitFallbackRefusesLoudlyWithNoPassingVendor()
    {
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A");
        h.Models.QueueText("screen");
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);

        // Everyone blocked parks on the exception card first; the person
        // rejects it, and only then can the fallback find nobody to award.
        var screened = CaseStore.Load(h.CaseDir, battery)!;
        var exception = screened.Decisions.Single(d =>
            d.Kind == DecisionKind.ExceptionApproval && d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(battery, exception.DecisionId, false, exception.RequestedTo, "no");

        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        QueueBadAward(h.Models, 3);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));

        Assert.Contains("No compliant vendor", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitFallbackSkipsPricelessPassingVendor()
    {
        var h = Create();
        // V-C passes on US origin but names no battery price, so the
        // fallback skips it for priced Meridian.
        h.Erp.SeedVendorDocuments("V-C",
        [
            new VendorDocumentDto("V-C", "UN38.3", "un383-c", true),
            new VendorDocumentDto("V-C", "SDS", "sds-c", true),
            new VendorDocumentDto("V-C", "RoHS", "rohs-c", true),
            new VendorDocumentDto("V-C", "REACH", "reach-c", true),
        ]);
        var battery = await OpenBatteryAsync(h, "V-A", "V-B", "V-C");
        h.Models.QueueText("screen");
        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        QueueBadAward(h.Models, 3);
        h.Models.QueueToolCalls("po", ("c1", AgentTools.CreatePo, Args(battery)));
        h.Models.QueueToolCalls("submit", ("c2", AgentTools.SubmitPoApproval, Args(battery)));

        var parked = await h.Runner.AdvanceAsync(battery);

        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Equal("V-B", file.Case.AwardedVendorId);
        Assert.Contains(
            file.Events,
            e => e.Kind == CaseEventKind.Note
                && e.Summary == "After three refused tries, propose a split ran by rule instead.");
    }

    [Fact]
    public async Task RepeatReadsTripFallbackOnThirdIdenticalTurn()
    {
        // Three identical reads with no action between them can only
        // return identical facts: the third trips the fallback, which
        // screens by rule; the next turn then needs a model answer that
        // was never queued, so the advance ends there.
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        for (var n = 1; n <= 3; n++)
        {
            h.Models.QueueToolCalls($"read {n}", ($"r{n}", AgentTools.GetCase, Args(battery)));
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));
        Assert.Contains("failed after one retry", thrown.Message, StringComparison.Ordinal);

        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Contains(file.Events, e => e.Kind == CaseEventKind.VendorScreened);
        Assert.Contains(
            file.Events,
            e => e.Kind == CaseEventKind.Note
                && e.Summary == "After three identical tries with no action, screen vendors ran by rule instead.");
    }

    [Fact]
    public async Task MixedIdleRunTripsFallbackOnEighthTurn()
    {
        // Alternating refusals and reads defeat the refusal and repeat
        // counters alike; the eight-turn idle breaker still catches the
        // flail and screens by rule.
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        for (var n = 1; n <= 4; n++)
        {
            QueueBadAward(h.Models, n);
            h.Models.QueueToolCalls($"read {n}", ($"r{n}", AgentTools.GetCase, Args(battery)));
        }

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));
        Assert.Contains("failed after one retry", thrown.Message, StringComparison.Ordinal);

        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Contains(file.Events, e => e.Kind == CaseEventKind.VendorScreened);
        Assert.Contains(
            file.Events,
            e => e.Kind == CaseEventKind.Note
                && e.Summary == "After eight tries with no action, screen vendors ran by rule instead.");
    }

    [Fact]
    public async Task ProposeStepDeliberationNeverTripsIdleBreakers()
    {
        // The split step deliberates over read facts, so repeats and
        // long idles do not trip there: three identical headroom reads
        // and then a valid proposal still land the split card.
        var h = Create();
        var battery = await OpenBatteryAsync(h, "V-A", "V-B");
        h.Models.QueueText("screen");
        for (var n = 1; n <= 3; n++)
        {
            h.Models.QueueToolCalls($"read {n}", ($"r{n}", AgentTools.GetVendorHeadroom, Args(battery)));
        }

        h.Models.QueueToolCalls("split 800/400",
            ("p1", AgentTools.ProposeSplit,
            $"{{\"case_id\":\"{battery}\",\"vendor_ids\":[\"V-A\",\"V-B\"],\"quantities\":[800,400]," +
            $"\"note\":\"keep dual supply for resilience\"}}"));

        var parked = await h.Runner.AdvanceAsync(battery);

        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var file = CaseStore.Load(h.CaseDir, battery)!;
        Assert.Contains(
            file.Decisions,
            d => d.Kind == DecisionKind.SplitApproval && d.Status == DecisionStatus.Pending);
        Assert.DoesNotContain(
            file.Events,
            e => e.Kind == CaseEventKind.Note && e.Summary.Contains("ran by rule instead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitFallbackRefusesLoudlyWhenNobodyIsPriced()
    {
        var h = Create();
        h.Erp.SeedVendorDocuments("V-C",
        [
            new VendorDocumentDto("V-C", "UN38.3", "un383-c", true),
            new VendorDocumentDto("V-C", "SDS", "sds-c", true),
            new VendorDocumentDto("V-C", "RoHS", "rohs-c", true),
            new VendorDocumentDto("V-C", "REACH", "reach-c", true),
        ]);
        var battery = await OpenBatteryAsync(h, "V-A", "V-C");
        h.Models.QueueText("screen");
        QueueBadAward(h.Models, 1);
        QueueBadAward(h.Models, 2);
        QueueBadAward(h.Models, 3);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));

        Assert.Contains("No priced passing vendor", thrown.Message, StringComparison.Ordinal);
    }
}
