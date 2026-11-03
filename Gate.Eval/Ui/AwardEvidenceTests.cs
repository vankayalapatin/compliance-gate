// Award-evidence tests: the award freezes screening verdicts, sentences,
// and figures as they stood, and decided orders display the stored copy
// on the case page, the PO record, and the approval cards. Only orders
// not yet decided re-run screening live. All data is local test data.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class AwardEvidenceTests
{
    private sealed record Harness(InMemoryErpAdapter Erp, P2pAgent Core, CaseRunner Runner, string CaseDir);

    private static IReadOnlyDictionary<string, string> CountryMap() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
            ["US"] = "United States",
        };

    private static Harness Create(Action<InMemoryErpAdapter>? tune = null)
    {
        var erp = SeededErp.Create();
        tune?.Invoke(erp);
        var models = new FakeModelClient();
        var compliance = new ComplianceEngine();
        var core = new P2pAgent(erp, models, compliance);
        var dir = UiTestSupport.NewDir("gate-award-evidence");
        var runner = new CaseRunner(
            erp, core, models, compliance, new DockEvaluatorAdapter(), dir,
            countryNames: CountryMap());
        return new Harness(erp, core, runner, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    private static async Task<string> AwardBatteryAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = raised.Decisions.Single(d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(battery);
        return battery;
    }

    private static async Task ResolvePoApprovalAsync(Harness h, string caseId)
    {
        var file = Load(h, caseId);
        var poDecision = file.Decisions.Single(d =>
            d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(caseId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
    }

    [Fact]
    public async Task AwardStoresScreeningEvidenceAsDecided()
    {
        var h = Create();
        var battery = await AwardBatteryAsync(h);
        var file = Load(h, battery);

        var snapshot = file.ScreeningSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(file.Case.PrNumber, snapshot.PrNumber);
        Assert.Equal(SeededErp.Battery, snapshot.ItemId);
        Assert.Equal(90m, snapshot.AwardQty);
        Assert.Equal("V-B", snapshot.AwardedVendorId);
        var awarded = file.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Equal(awarded.Timestamp, snapshot.TakenAt);

        Assert.Equal(2, snapshot.Rows.Count);
        var loser = snapshot.Rows.Single(r => r.VendorId == "V-A");
        Assert.Equal("CinderVolt Energy Ltd", loser.VendorName);
        Assert.Equal("CN", loser.CountryCode);
        Assert.Equal("China", loser.Country);
        Assert.Equal(11m, loser.UnitPrice);
        Assert.False(loser.Passed);
        Assert.Equal("Blocked by CORP-02", loser.Result);
        Assert.Contains("60.4%", loser.Reason, StringComparison.Ordinal);
        Assert.False(loser.Awarded);
        var corp = loser.Verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        var figures = corp.OriginCap;
        Assert.NotNull(figures);
        Assert.Equal(SeededErp.Battery, figures.ItemId);
        Assert.Equal(6000m, figures.CountryCells);
        Assert.Equal(10000m, figures.TotalCells);
        Assert.Equal(90m, figures.AwardQty);
        Assert.Equal(0.6m, figures.Cap);
        Assert.Equal(0.6036m, Math.Round(figures.Share, 4));
        var math = loser.Math;
        Assert.NotNull(math);
        Assert.Contains("60.4%", math.PlainText, StringComparison.Ordinal);
        Assert.Contains("China", math.PlainText, StringComparison.Ordinal);

        var winner = snapshot.Rows.Single(r => r.VendorId == "V-B");
        Assert.True(winner.Passed);
        Assert.Equal("Passed", winner.Result);
        Assert.True(winner.Awarded);
        Assert.Contains("37.6%", winner.Reason, StringComparison.Ordinal);
        Assert.NotNull(winner.Math);
        Assert.Contains("37.6%", winner.Math.PlainText, StringComparison.Ordinal);

        // The case page renders the stored copy, not a live re-run.
        var rows = await VendorScreeningTable.RowsAsync(h.Erp, h.Core, file, null, CountryMap());
        var stored = Assert.Single(rows.Where(r => r.VendorId == "V-A"));
        Assert.Equal("China", stored.Country);
        Assert.Equal("$11.00", stored.Price);
        Assert.Equal("Blocked by CORP-02", stored.Result);
        Assert.Contains("60.4%", stored.Reason, StringComparison.Ordinal);
        var lines = await OriginCapExplainer.LinesAsync(h.Erp, file, null, CountryMap());
        Assert.Contains("60.4%", lines.Single(l => l.VendorId == "V-A").Line.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredSentenceMentionsOpenOrdersInTheMath()
    {
        var h = Create();
        // A 20-cell open order to CinderVolt sits unreceived: the battery
        // shortfall drops to 70, and the award math counts the open cells.
        var pre = await h.Erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 20m, "EA")],
            Guid.NewGuid().ToString("N"));
        if (pre.Status == PrStatus.Draft)
        {
            await h.Erp.SubmitForApprovalAsync("PR", pre.PrNumber, "delegate", "planner", Guid.NewGuid().ToString("N"));
            await h.Erp.RecordApprovalAsync("PR", pre.PrNumber, "delegate", true, "ok", Guid.NewGuid().ToString("N"));
        }

        await h.Erp.CreatePoAsync(pre.PrNumber, "V-A", RecordSource.Human, "planner", Guid.NewGuid().ToString("N"));

        var battery = await AwardBatteryAsync(h);
        var snapshot = Load(h, battery).ScreeningSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(70m, snapshot.AwardQty);

        var loser = snapshot.Rows.Single(r => r.VendorId == "V-A");
        Assert.False(loser.Passed);
        var corp = loser.Verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        var figures = corp.OriginCap;
        Assert.NotNull(figures);
        Assert.Equal(20m, figures.CountryOpenCells);
        Assert.Equal(20m, figures.TotalOpenCells);
        Assert.NotNull(loser.Math);
        Assert.Contains(
            "Another 20 battery cells are on open orders, 20 of them from China.",
            loser.Math.PlainText,
            StringComparison.Ordinal);

        var winner = snapshot.Rows.Single(r => r.VendorId == "V-B");
        Assert.True(winner.Awarded);
    }

    [Fact]
    public async Task ReceivedPoStillShowsAwardTimeShare()
    {
        // (601 + 90) / (1000 + 90) = 63.4%: CinderVolt blocks, Meridian wins.
        var h = Create(erp => erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 601),
            new VolumeHistory("V-B MX active", "MX", 399),
        ]));
        var battery = await AwardBatteryAsync(h);
        await ResolvePoApprovalAsync(h, battery);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(battery));

        var placed = Load(h, battery);
        Assert.NotNull(placed.Case.PoNumber);
        var po = await h.Erp.GetPurchaseOrderAsync(placed.Case.PoNumber!);
        Assert.NotNull(po);
        Assert.Equal("V-B", po.VendorId);
        var line = po.Lines.Single(l => l.ItemId == SeededErp.Battery);
        await h.Erp.PostReceiptAsync(po.PoNumber, "PACK-T", RecordSource.P2pAgent, "requester",
            [new ReceiptLine(line.PoLineRef, line.QtyOrdered, 0m, 0m, "LOT-T", null)],
            Guid.NewGuid().ToString("N"));

        // The receipt moved live history: (601 + 90) / (1090 + 90) = 58.6%.
        var live = await h.Core.ScreenCandidatesAsync(placed.Case.PrNumber!, ["V-A", "V-B"]);
        var liveCorp = live.Single(s => s.VendorId == "V-A").Verdicts
            .Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.DoesNotContain("63.4%", liveCorp.Reason, StringComparison.Ordinal);
        Assert.Contains("58.6%", liveCorp.Reason, StringComparison.Ordinal);

        // But the PO's screening section still shows the award-time 63.4%.
        var received = Load(h, battery);
        var rows = await VendorScreeningTable.RowsAsync(h.Erp, h.Core, received, null, CountryMap());
        Assert.Contains("63.4%",
            rows.Single(r => r.VendorId == "V-A").Reason, StringComparison.Ordinal);
        var lines = await OriginCapExplainer.LinesAsync(h.Erp, received, null, CountryMap());
        Assert.Contains("63.4%",
            lines.Single(l => l.VendorId == "V-A").Line.PlainText, StringComparison.Ordinal);

        var dir = UiTestSupport.NewDir("gate-award-po-section");
        var views = new CaseViewService(
            h.Erp, new CaseCacheStore(h.CaseDir),
            new DockPhotoService(dir, Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance,
            countries: new CountryNames(CountryMap()),
            screening: h.Core);
        var section = await views.ScreeningSectionAsync(received.Case.PrNumber, received.Case.PoNumber);
        Assert.NotNull(section);
        var text = string.Join("\n", section.Rows.SelectMany(cells => cells.Select(c => c.Text)));
        Assert.Contains("63.4%", text, StringComparison.Ordinal);
        Assert.DoesNotContain("58.6%", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReawardMovesSnapshotWinnerToPoVendor()
    {
        // Both vendors pass screening, but only Meridian names a price, so
        // the award goes to CinderVolt and the PO recovers to Meridian.
        // Screening ignores price, so the award still lands there first.
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("USD");
        erp.SeedAutoApproveThreshold(500m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedItem(new ItemDto(SeededErp.Battery, "Battery cell", true, 10, "EA"));
        erp.SeedItem(new ItemDto(SeededErp.Screw, "Screw", false, 10, "EA"));
        erp.SeedItem(new ItemDto(SeededErp.Glasses, "Smart glasses", false, 1, "EA"));
        erp.SeedSalesOrder(new SalesOrder("SO-1001", SalesOrderStatus.Confirmed, "USD",
            [new SalesOrderLine(SeededErp.Glasses, 100)]));
        erp.SeedBom(SeededErp.Glasses, [new BomLine(SeededErp.Battery, 1), new BomLine(SeededErp.Screw, 2)]);
        erp.SeedStock(SeededErp.Battery, 10);
        erp.SeedStock(SeededErp.Screw, 50);
        erp.SeedVendor(new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-B", "Meridian Cellworks Inc", "MX", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-C", "Formosa Fasteners Co", "US", false, null, "NET0"));
        erp.SeedVendorPrice("V-B", SeededErp.Battery, 12m);
        erp.SeedVendorPrice("V-B", SeededErp.Screw, 1m);
        erp.SeedVendorPrice("V-C", SeededErp.Screw, 1m);
        erp.SeedVendorDocuments("V-A", [
            new VendorDocumentDto("V-A", "UN38.3", "un383-a", true),
            new VendorDocumentDto("V-A", "SDS", "sds-a", true),
            new VendorDocumentDto("V-A", "RoHS", "rohs-a", true),
            new VendorDocumentDto("V-A", "REACH", "reach-a", true)]);
        erp.SeedVendorDocuments("V-B", [
            new VendorDocumentDto("V-B", "UN38.3", "un383-b", true),
            new VendorDocumentDto("V-B", "SDS", "sds-b", true),
            new VendorDocumentDto("V-B", "RoHS", "rohs-b", true),
            new VendorDocumentDto("V-B", "REACH", "reach-b", true)]);
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 100),
            new VolumeHistory("V-B MX active", "MX", 100),
            new VolumeHistory("Other non-CN", "OTHER", 9800),
        ]);
        erp.SeedUser(new UserDto("planner", "requester", 5000m, "USD"));
        erp.SeedUser(new UserDto("approver", "approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto("delegate", "delegate", 50_000m, "USD"));
        erp.SeedUser(new UserDto("finance", "payment-approver", 100_000m, "USD"));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        erp.SeedDelegation(new DelegationDto("approver", "delegate", today.AddDays(-30), today.AddDays(30)));
        var models = new FakeModelClient();
        var compliance = new ComplianceEngine();
        var core = new P2pAgent(erp, models, compliance);
        var dir = UiTestSupport.NewDir("gate-award-reaward");
        var repriced = new Harness(
            erp, core,
            new CaseRunner(erp, core, models, compliance, new DockEvaluatorAdapter(),
                dir, countryNames: CountryMap()),
            dir);

        var battery = await AwardBatteryAsync(repriced);
        var file = Load(repriced, battery);

        Assert.Equal("V-B", file.Case.AwardedVendorId);
        var po = await erp.GetPurchaseOrderAsync(file.Case.PoNumber!);
        Assert.NotNull(po);
        Assert.Equal("V-B", po.VendorId);
        var snapshot = file.ScreeningSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal("V-B", snapshot.AwardedVendorId);
        Assert.Equal(2, snapshot.Rows.Count);
        Assert.True(snapshot.Rows.Single(r => r.VendorId == "V-B").Awarded);
        var firstWinner = snapshot.Rows.Single(r => r.VendorId == "V-A");
        Assert.False(firstWinner.Awarded);
        Assert.Null(firstWinner.UnitPrice);
        Assert.True(firstWinner.Passed);
        Assert.NotNull(firstWinner.Math);
        Assert.Contains("1.9%", firstWinner.Math.PlainText, StringComparison.Ordinal);
    }
}
