// ERP-work pickup tests: the agent picks up work the ERP hands it (an
// approved PR without a PO, an approved/confirmed PO on the goods leg, a
// received or matched invoice), opening a live case when no case covers the
// document. Test-local ERP data only; nothing depends on demo numbers.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpPickupTests
{
    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, FakeModelClient Models, string CaseDir);

    private static Harness Create(IModelClient? models = null)
    {
        var erp = SeededErp.Create();
        var inner = models ?? new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var core = new P2pAgent(erp, inner, compliance);
        var dir = TempDir("gate-pickup-test");
        var runner = new CaseRunner(erp, core, inner, compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, inner as FakeModelClient ?? new FakeModelClient(), dir);
    }

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    private static Decision PendingDecision(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    private static async Task<PurchaseRequisition> ApprovedPrAsync(
        Harness h, string item, decimal qty, string key)
    {
        var pr = await h.Erp.CreatePrAsync(
            "planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(item, qty, "EA")], key);
        Assert.Equal(PrStatus.Approved, pr.Status);
        return pr;
    }

    private static async Task<PurchaseOrder> ConfirmedPoAsync(
        Harness h, string prNumber, string vendor, string key)
    {
        var po = await h.Erp.CreatePoAsync(prNumber, vendor, RecordSource.Human, "planner", $"{key}:po");
        await h.Erp.RecordVendorConfirmationAsync(po.PoNumber,
            DateOnly.FromDateTime(DateTime.UtcNow), RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(),
            $"{key}:ack");
        return (await h.Erp.GetPurchaseOrderAsync(po.PoNumber))!;
    }

    private static CaseFile PickupFile(
        string soNumber, string? pr, string? po, CaseStage stage, bool opening = false,
        IReadOnlyList<InvoiceKey>? invoices = null, IReadOnlyList<Decision>? decisions = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new CaseFile(
            new Case("case-1", soNumber, "PART-1", "test", stage, now, now,
                pr, po, null, null, invoices ?? [], [], null, null, null, opening),
            [], decisions ?? []);
    }

    [Fact]
    public async Task ApprovedPrWithoutPoOpensALiveCaseThatScreensAwardsAndOrders()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Battery, 10, "t:pickup:pr");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        var caseId = CaseStore.MintCaseId("pick", "PR", pr.PrNumber);
        Assert.Equal([caseId], touched);
        var file = Load(h, caseId);
        Assert.False(file.Case.IsOpeningData);
        Assert.Equal(string.Empty, file.Case.SoNumber);
        Assert.Equal(SeededErp.Battery, file.Case.ItemId);
        Assert.Equal(CaseStage.PrApproved, file.Case.Stage);
        Assert.Equal(pr.PrNumber, file.Case.PrNumber);
        Assert.Empty(file.Decisions);
        Assert.Contains(file.Events,
            e => e.Kind == CaseEventKind.Note && e.Summary.Contains(pr.PrNumber, StringComparison.Ordinal));
        Assert.Equal(0, h.Models.CallCount);

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
        var advanced = Load(h, caseId);
        Assert.NotNull(advanced.Case.PoNumber);
        Assert.Contains(advanced.Events, e =>
            e.Kind == CaseEventKind.VendorBlocked && e.RuleId == ComplianceEngine.CorpOriginCapId);
        var award = advanced.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-A", award.Summary, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, award.Summary, StringComparison.Ordinal);
        var po = await h.Erp.GetPurchaseOrderAsync(advanced.Case.PoNumber);
        Assert.NotNull(po);
        Assert.Equal(pr.PrNumber, po.PrNumber);
    }

    [Fact]
    public async Task PrWithPoIsSkippedWhileItsPoIsAdopted()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:prpo");
        var po = await h.Erp.CreatePoAsync(pr.PrNumber, "V-C", RecordSource.Human, "planner", "t:pickup:prpo:po");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        var poCaseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([poCaseId], touched);
        Assert.Null(CaseStore.Load(h.CaseDir, CaseStore.MintCaseId("pick", "PR", pr.PrNumber)));
        var file = Load(h, poCaseId);
        Assert.Equal(pr.PrNumber, file.Case.PrNumber);
        Assert.Equal(po.PoNumber, file.Case.PoNumber);
        Assert.Equal("V-C", file.Case.AwardedVendorId);

        var outcome = await h.Runner.AdvanceAsync(poCaseId);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
    }

    [Fact]
    public async Task ConfirmedPoAwaitingGoodsParksOnAsnThenTakesSupplierSends()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:poleg");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:poleg");
        Assert.Equal(PoApprovalStatus.Confirmed, po.ApprovalStatus);

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        var caseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([caseId], touched);
        Assert.Equal(CaseStage.PoAcknowledged, Load(h, caseId).Case.Stage);

        var parked = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitAsn, parked.Step);

        var (_, receipt) = await h.Runner.OnAsnAsync(new SupplierAsnInput(
            caseId, "ASN-PICK-1", "SLIP-PICK-1",
            [new AsnCartonInput("C-PICK-1", SeededErp.Screw, 10, "LOT-PICK-1")]));
        Assert.NotNull(receipt);
        Assert.Equal(CaseStage.ReceiptPosted, Load(h, caseId).Case.Stage);

        var afterGoods = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDockReads, afterGoods.Step);
    }

    [Fact]
    public async Task ReceivedInvoiceIsMatchedLinkedAndSubmittedForApproval()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:inv");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:inv");
        await h.Erp.CreateAsnAsync("ASN-PICK-2", po.PoNumber, RecordSource.Vendor, "V-C",
            [new AsnCarton("C-PICK-2", SeededErp.Screw, 10, "LOT-PICK-2")], "t:pickup:inv:asn");
        await h.Erp.PostReceiptAsync(po.PoNumber, "SLIP-PICK-2", RecordSource.Human, "planner",
            [new ReceiptLine("PO-L1", 10, 0, 0, "LOT-PICK-2", null)], "t:pickup:inv:rct");
        await h.Erp.CreateInvoiceAsync("INV-PICK-1", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:inv:1");
        Assert.Empty((await h.Erp.GetInvoiceAsync("V-C", "INV-PICK-1"))!.MatchResults);

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        var caseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([caseId], touched);
        var stored = await h.Erp.GetInvoiceAsync("V-C", "INV-PICK-1");
        Assert.NotNull(stored);
        Assert.NotEmpty(stored.MatchResults);
        Assert.All(stored.MatchResults, r => Assert.True(r.Matched));
        var file = Load(h, caseId);
        Assert.Contains(new InvoiceKey("V-C", "INV-PICK-1"), file.Case.Invoices);
        Assert.Equal(CaseStage.InvoiceMatched, file.Case.Stage);
        Assert.Contains(file.Events, e => e.Kind == CaseEventKind.InvoiceMatched);
        var card = PendingDecision(file, DecisionKind.InvoiceApproval);
        Assert.Equal("V-C/INV-PICK-1", card.DocumentNumber);

        var parked = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);

        await h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, card.RequestedTo, "ok");
        var afterApproval = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, afterApproval.Step);
        Assert.Equal(CaseStage.PaymentRequested, Load(h, caseId).Case.Stage);
    }

    [Fact]
    public async Task AlreadyMatchedInvoiceLinksWithoutRematching()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:inv2");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:inv2");
        await h.Erp.CreateAsnAsync("ASN-PICK-3", po.PoNumber, RecordSource.Vendor, "V-C",
            [new AsnCarton("C-PICK-3", SeededErp.Screw, 10, "LOT-PICK-3")], "t:pickup:inv2:asn");
        await h.Erp.PostReceiptAsync(po.PoNumber, "SLIP-PICK-3", RecordSource.Human, "planner",
            [new ReceiptLine("PO-L1", 10, 0, 0, "LOT-PICK-3", null)], "t:pickup:inv2:rct");
        await h.Erp.CreateInvoiceAsync("INV-PICK-2", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:inv2:1");
        await h.Erp.RecordMatchResultAsync("V-C", "INV-PICK-2",
            [new LineMatchResult("PO-L1", true, "Matched.")], "t:pickup:inv2:match");
        var before = await h.Erp.GetInvoiceAsync("V-C", "INV-PICK-2");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        var caseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([caseId], touched);
        var after = await h.Erp.GetInvoiceAsync("V-C", "INV-PICK-2");
        Assert.Equal(before!.MatchResults, after!.MatchResults);
        Assert.Contains(new InvoiceKey("V-C", "INV-PICK-2"), Load(h, caseId).Case.Invoices);
    }

    [Fact]
    public async Task RerunTouchesNothingAndRewritesNothing()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:idem");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:idem");
        await h.Erp.CreateAsnAsync("ASN-PICK-4", po.PoNumber, RecordSource.Vendor, "V-C",
            [new AsnCarton("C-PICK-4", SeededErp.Screw, 10, "LOT-PICK-4")], "t:pickup:idem:asn");
        await h.Erp.PostReceiptAsync(po.PoNumber, "SLIP-PICK-4", RecordSource.Human, "planner",
            [new ReceiptLine("PO-L1", 10, 0, 0, "LOT-PICK-4", null)], "t:pickup:idem:rct");
        await h.Erp.CreateInvoiceAsync("INV-PICK-3", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:idem:1");

        var first = await h.Runner.PickupErpWorkAsync("pick");
        Assert.NotEmpty(first);
        var before = CaseStore.ListCaseIds(h.CaseDir).ToDictionary(
            id => id, id => File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, id)));

        var second = await h.Runner.PickupErpWorkAsync("pick");

        Assert.Empty(second);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
            CaseStore.ListCaseIds(h.CaseDir).Order(StringComparer.Ordinal));
        foreach (var (id, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, id)));
        }
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task SubmittedClosedAndTerminalAnchorsAreSkipped()
    {
        var h = Create();
        var big = await h.Erp.CreatePrAsync(
            "planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 100, "EA")], "t:pickup:skip:pr");
        Assert.Equal(PrStatus.Draft, big.Status);
        await h.Erp.SubmitForApprovalAsync("PR", big.PrNumber, "approver", "planner", "t:pickup:skip:submit");

        var small = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:skip:small");
        var closed = await h.Erp.CreatePoAsync(small.PrNumber, "V-C", RecordSource.Human, "planner", "t:pickup:skip:po");
        await h.Erp.CreateInvoiceAsync("INV-PICK-9", "V-C", closed.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:skip:inv");
        await h.Erp.ClosePoAsync(closed.PoNumber, "planner", "t:pickup:skip:close");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        Assert.Empty(touched);
        Assert.Empty(CaseStore.ListCaseIds(h.CaseDir));
    }

    [Fact]
    public async Task InvoiceOnTerminalAnchorIsSkippedWithoutResurrection()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:term");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:term");
        var caseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([caseId], await h.Runner.PickupErpWorkAsync("pick"));
        var file = Load(h, caseId);
        CaseStore.Save(h.CaseDir, file with { Case = file.Case with { Stage = CaseStage.Resolved } });
        await h.Erp.CreateAsnAsync("ASN-PICK-5", po.PoNumber, RecordSource.Vendor, "V-C",
            [new AsnCarton("C-PICK-5", SeededErp.Screw, 10, "LOT-PICK-5")], "t:pickup:term:asn");
        await h.Erp.PostReceiptAsync(po.PoNumber, "SLIP-PICK-5", RecordSource.Human, "planner",
            [new ReceiptLine("PO-L1", 10, 0, 0, "LOT-PICK-5", null)], "t:pickup:term:rct");
        await h.Erp.CreateInvoiceAsync("INV-PICK-4", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:term:1");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        Assert.Empty(touched);
        var skipped = Load(h, caseId);
        Assert.Equal(CaseStage.Resolved, skipped.Case.Stage);
        Assert.Empty(skipped.Case.Invoices);
    }

    [Fact]
    public async Task OpeningShellConvertsToLiveCaseOnApproval()
    {
        var fake = new InMemoryErpAdapter();
        fake.SeedBaseCurrency("USD");
        fake.SeedAutoApproveThreshold(500m);
        fake.SeedItem(new ItemDto("PART-1", "Test part", false, 10, "EA"));
        fake.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        fake.SeedVendor(new VendorDto("V-T", "Test Vendor", "US", false, null, "NET0"));
        fake.SeedVendorBank(new VendorBankDto("V-T", "BANK-T", true, DateTimeOffset.UtcNow));
        fake.SeedVendorPrice("V-T", "PART-1", 10m);
        fake.SeedUser(new UserDto("clerk", "requester", 5000m, "USD"));
        fake.SeedUser(new UserDto("boss", "approver", 100_000m, "USD"));
        var erp = fake;
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-pickup-shell-test");
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);

        var big = await erp.CreatePrAsync(
            "clerk", RecordSource.Human, "clerk",
            [new PurchaseRequisitionLine("PART-1", 100, "EA")], "t:shell:pr");
        await erp.SubmitForApprovalAsync("PR", big.PrNumber, "boss", "clerk", "t:shell:submit");
        var shells = await runner.SeedOpeningDecisionsAsync("shelltest");
        var caseId = Assert.Single(shells);
        var shell = CaseStore.Load(dir, caseId)!;
        await runner.ResolveDecisionAsync(
            caseId, PendingDecision(shell, DecisionKind.PrApproval).DecisionId, true, "boss", "ok");

        var touched = await runner.PickupErpWorkAsync("shelltest");

        Assert.Equal([caseId], touched);
        var file = CaseStore.Load(dir, caseId)!;
        Assert.False(file.Case.IsOpeningData);
        Assert.Equal(CaseStage.PrApproved, file.Case.Stage);
        Assert.Equal(big.PrNumber, file.Case.PrNumber);
        var card = Assert.Single(file.Decisions);
        Assert.Equal(DecisionKind.PrApproval, card.Kind);
        Assert.Equal(DecisionStatus.Approved, card.Status);
        Assert.NotEmpty(file.Events);

        // The converted case screens its single vendor, awards, and creates a
        // PO above the auto-approve threshold, so it parks on PO approval.
        var outcome = await runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var advanced = CaseStore.Load(dir, caseId)!;
        Assert.Contains(advanced.Events, e => e.Kind == CaseEventKind.VendorScreened);
        Assert.NotNull(advanced.Case.PoNumber);
        PendingDecision(advanced, DecisionKind.PoApproval);
    }

    [Fact]
    public async Task BlankScenarioNameThrowsWithoutTouchingTheCache()
    {
        var h = Create();
        await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:blank");

        await Assert.ThrowsAsync<ArgumentException>(() => h.Runner.PickupErpWorkAsync("  "));

        Assert.Empty(CaseStore.ListCaseIds(h.CaseDir));
    }

    [Fact]
    public async Task LiveCaseCollisionThrowsWithoutTouchingTheCase()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:collide");
        var caseId = CaseStore.MintCaseId("pick", "PR", pr.PrNumber);
        var now = DateTimeOffset.UtcNow;
        CaseStore.Save(h.CaseDir, new CaseFile(
            new Case(caseId, "SO-9", SeededErp.Screw, "pick", CaseStage.Created, now, now,
                "PR-OTHER", null, null, null, [], [], null, null),
            [], []));
        var before = File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, caseId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.PickupErpWorkAsync("pick"));

        Assert.Contains(caseId, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, caseId)));
    }

    [Fact]
    public void ErpHandedWorkAdvancesWithoutASalesOrder()
    {
        var view = new CaseErpView(
            false, PrStatus.Approved, null, false, false, false, 0, 0, 0, false, 0, false);

        var (step, _) = CasePicker.Pick(
            PickupFile(string.Empty, "PR-1", null, CaseStage.PrApproved), view);

        Assert.Equal(CaseNextStep.ScreenVendors, step);
        Assert.True(CasePicker.IsErpHandedWork(PickupFile(string.Empty, "PR-1", null, CaseStage.PrApproved)));
    }

    [Fact]
    public void OpeningShellAndVanishedSalesOrderStayDone()
    {
        var shellView = new CaseErpView(
            false, PrStatus.Approved, null, false, false, false, 0, 0, 0, false, 0, false);
        var (shellStep, _) = CasePicker.Pick(
            PickupFile(string.Empty, "PR-1", null, CaseStage.Created, opening: true), shellView);
        Assert.Equal(CaseNextStep.Done, shellStep);
        Assert.False(CasePicker.IsErpHandedWork(
            PickupFile(string.Empty, "PR-1", null, CaseStage.Created, opening: true)));

        var goneView = new CaseErpView(
            false, PrStatus.Approved, null, false, false, false, 0, 0, 0, false, 0, false);
        var (goneStep, goneReason) = CasePicker.Pick(
            PickupFile("SO-1", "PR-1", null, CaseStage.PrApproved), goneView);
        Assert.Equal(CaseNextStep.Done, goneStep);
        Assert.Contains("gone", goneReason, StringComparison.OrdinalIgnoreCase);

        var bareView = new CaseErpView(
            false, null, null, false, false, false, 0, 0, 0, false, 0, false);
        var (bareStep, _) = CasePicker.Pick(
            PickupFile(string.Empty, null, null, CaseStage.Created), bareView);
        Assert.Equal(CaseNextStep.Done, bareStep);
    }

    [Fact]
    public void ErpHandedPoCaseWaitsOnTheSupplierLeg()
    {
        var ackView = new CaseErpView(
            false, PrStatus.Closed, PoApprovalStatus.Approved, false, false, false,
            0, 0, 0, false, 0, false);
        Assert.Equal(CaseNextStep.AwaitSupplierAck,
            CasePicker.Pick(PickupFile(string.Empty, "PR-1", "PO-1", CaseStage.PoPlaced), ackView).Step);

        var asnView = ackView with { ConfirmationExists = true };
        Assert.Equal(CaseNextStep.AwaitAsn,
            CasePicker.Pick(PickupFile(string.Empty, "PR-1", "PO-1", CaseStage.PoAcknowledged), asnView).Step);
    }

    [Fact]
    public async Task SubmittedInvoiceStaysOnTheHumanLane()
    {
        var h = Create();
        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:pend");
        var po = await ConfirmedPoAsync(h, pr.PrNumber, "V-C", "t:pickup:pend");
        await h.Erp.CreateInvoiceAsync("INV-PICK-6", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:pend:1");
        await h.Erp.SubmitForApprovalAsync("Invoice", "V-C/INV-PICK-6", "approver", "planner", "t:pickup:pend:submit");
        Assert.Contains(await h.Erp.GetPendingApprovalsAsync(),
            r => r.DocumentType == "Invoice" && r.DocumentNumber == "V-C/INV-PICK-6");

        var touched = await h.Runner.PickupErpWorkAsync("pick");

        // The PO still adopts (supplier leg work); the submitted invoice
        // stays unlinked so pickup never duplicates the human approval card.
        var caseId = CaseStore.MintCaseId("pick", "PO", po.PoNumber);
        Assert.Equal([caseId], touched);
        var file = Load(h, caseId);
        Assert.Empty(file.Case.Invoices);
        Assert.DoesNotContain(file.Decisions, d => d.Kind == DecisionKind.InvoiceApproval);
    }

    [Fact]
    public async Task MatchExistingInvoiceMatchesThenReturnsUntouched()
    {
        var h = Create();
        var core = new P2pAgent(h.Erp, h.Models, new RecordingComplianceEngine());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            core.MatchExistingInvoiceAsync("V-C", "INV-NOPE"));

        var pr = await ApprovedPrAsync(h, SeededErp.Screw, 10, "t:pickup:match");
        var po = await h.Erp.CreatePoAsync(pr.PrNumber, "V-C", RecordSource.Human, "planner", "t:pickup:match:po");
        await h.Erp.CreateInvoiceAsync("INV-PICK-5", "V-C", po.PoNumber, RecordSource.Vendor, "planner",
            [new InvoiceLineInput("PO-L1", 10, 1)], "t:pickup:match:1");
        var matched = await core.MatchExistingInvoiceAsync("V-C", "INV-PICK-5");
        Assert.NotEmpty(matched.MatchResults);
        Assert.False(matched.MatchResults.All(r => r.Matched));

        var again = await core.MatchExistingInvoiceAsync("V-C", "INV-PICK-5");
        Assert.Equal(matched.MatchResults, again.MatchResults);
    }
}
