// Decision completion from the Decisions surface delegates to the runner:
// one transition implementation for UI and agent. Tests run the real runner
// against the in-memory fake ERP (unit tests only; the app uses EF Core)
// and pin invoice/payment posting, money rejection cancelling the case,
// review hold release, bank verification, and failure paths. No live calls.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class DecisionOutcomeTests
{
    private sealed record Harness(InMemoryErpAdapter Erp, CaseCacheStore Store, CaseRunner Runner);

    private sealed class FailCallsClient(IModelClient inner, params int[] failOn) : IModelClient
    {
        private int _calls;

        public string ModelName => inner.ModelName;

        public async Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            _calls++;
            if (failOn.Contains(_calls))
            {
                throw new System.Net.Http.HttpRequestException($"Boom {_calls}.");
            }

            return await inner.CompleteAsync(request, ct);
        }
    }

    private static Harness Create(IModelClient? models = null)
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-TEST", "Test Vendor", "US", false, null, "NET0"));
        erp.SeedVendorPrice("V-TEST", "ITEM-1", 10m);
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("stranger", "clerk", 1_000_000m, "TST"));
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        models ??= new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models, compliance,
            new DockEvaluatorAdapter(), dir);
        return new Harness(erp, store, runner);
    }

    private static DecisionOutcomeService Outcomes(Harness h) =>
        new(h.Store, UiTestLogger<DecisionOutcomeService>.Instance, h.Runner);

    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ApprovePrRecordsApprovalAndClosesDecision()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        var file = UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]);
        await h.Store.WriteAsync(file);

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "looks good", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var approvals = await h.Erp.GetApprovalsAsync("PR", pr.PrNumber);
        Assert.Single(approvals);
        Assert.True(approvals[0].Approved);
        Assert.Equal("looks good", approvals[0].Comment);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        var decided = reread.Decisions.Single(d => d.DecisionId == "D-1");
        Assert.Equal(DecisionStatus.Approved, decided.Status);
        Assert.Equal("approver", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal("looks good", decided.Comment);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PrApproved);
    }

    [Fact]
    public async Task RecordPageApproveSettlesLinkedCard()
    {
        // Approving on the ERP record page settles the linked case card
        // through the same resolver the Decisions page uses: the card shows
        // decided and no second approval row is recorded.
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));
        var actions = new ErpActionService(
            h.Erp, h.Runner, UiTestLogger<ErpActionService>.Instance);

        var outcome = await actions.AttemptAsync(
            "pr", pr.PrNumber, null, "approve",
            new Dictionary<string, string> { ["comment"] = "record page ok" }, "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("Settled 1 linked case card", outcome.Message, StringComparison.Ordinal);
        var approvals = await h.Erp.GetApprovalsAsync("PR", pr.PrNumber);
        Assert.Single(approvals);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        var decided = reread.Decisions.Single(d => d.DecisionId == "D-1");
        Assert.Equal(DecisionStatus.Approved, decided.Status);
        Assert.Equal("approver", decided.DecidedBy);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PrApproved);
    }

    [Fact]
    public async Task RejectPrCancelsTheCase()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", false, "over budget", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Rejected, reread.Decisions.Single().Status);
        Assert.Equal(CaseStage.Cancelled, reread.Case.Stage);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PrRejected);
    }

    [Fact]
    public async Task ApproveInvoicePostsIt()
    {
        var h = Create();
        var po = await ApprovedPoAsync(h);
        var line = po.Lines.Single();
        await h.Erp.PostReceiptAsync(po.PoNumber, "PACK-1", RecordSource.P2pAgent, "requester",
            [new ReceiptLine(line.PoLineRef, 5m, 0m, 0m, "LOT-1", null)], Idem());
        await h.Erp.CreateInvoiceAsync("INV-1", "V-TEST", po.PoNumber, RecordSource.P2pAgent, "requester",
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], Idem());
        await h.Erp.RecordMatchResultAsync("V-TEST", "INV-1",
            [new LineMatchResult(line.PoLineRef, true, "Matched.")], Idem());
        await h.Erp.SubmitForApprovalAsync("Invoice", "V-TEST/INV-1", "approver", "requester", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.InvoiceApproval, "Invoice", "V-TEST/INV-1"),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "ok", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var posted = await h.Erp.GetInvoiceAsync("V-TEST", "INV-1");
        Assert.NotNull(posted);
        Assert.Equal(InvoiceStatus.Posted, posted.Status);
        Assert.NotNull(posted.Voucher);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Approved, reread.Decisions.Single().Status);
        Assert.Contains(reread.Events, e => e.Summary.Contains("posted as voucher"));
    }

    [Fact]
    public async Task ApprovePaymentPostsItAndAppendsCaseNumbers()
    {
        var h = Create();
        var po = await ApprovedPoAsync(h);
        var line = po.Lines.Single();
        await h.Erp.PostReceiptAsync(po.PoNumber, "PACK-1", RecordSource.P2pAgent, "requester",
            [new ReceiptLine(line.PoLineRef, 5m, 0m, 0m, "LOT-1", null)], Idem());
        await h.Erp.CreateInvoiceAsync("INV-1", "V-TEST", po.PoNumber, RecordSource.P2pAgent, "requester",
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], Idem());
        await h.Erp.RecordMatchResultAsync("V-TEST", "INV-1",
            [new LineMatchResult(line.PoLineRef, true, "Matched.")], Idem());
        await h.Erp.SubmitForApprovalAsync("Invoice", "V-TEST/INV-1", "approver", "requester", Idem());
        await h.Erp.RecordApprovalAsync("Invoice", "V-TEST/INV-1", "approver", true, "ok", Idem());
        await h.Erp.PostInvoiceAsync("V-TEST", "INV-1", "requester", Idem());
        h.Erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        await h.Erp.VerifyVendorBankAsync("V-TEST", "approver", Idem());
        var draft = await h.Erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-TEST", "INV-1")], RecordSource.P2pAgent, "requester", Idem());
        await h.Erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "stranger", "requester", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PaymentApproval, "Payment", draft.PaymentNumber),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "pay it", "stranger");

        Assert.True(outcome.Ok, outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        var postedNumber = Assert.Single(reread.Case.PaymentNumbers);
        var posted = await h.Erp.GetPaymentAsync(postedNumber);
        Assert.NotNull(posted);
        Assert.Equal(5m * line.UnitPrice, posted.Lines.Sum(l => l.Amount));
        Assert.Equal(InvoiceStatus.Paid, (await h.Erp.GetInvoiceAsync("V-TEST", "INV-1"))!.Status);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PaymentPosted);
    }

    [Fact]
    public async Task WrongApproverFailsWithContext()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "stranger");

        Assert.False(outcome.Ok);
        Assert.Contains("stranger", outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Pending, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task AlreadyDecidedIsRefused()
    {
        var h = Create();
        var pending = UiTestSupport.PendingDecision("D-1", "CASE-1");
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            pending with { Status = DecisionStatus.Approved, DecidedBy = "approver" },
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.False(outcome.Ok);
        Assert.Contains("already", outcome.Message);
    }

    [Fact]
    public async Task MissingDocumentReferenceFails()
    {
        var h = Create();
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PoApproval, null, null),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.False(outcome.Ok);
        Assert.Contains("no document number", outcome.Message);
    }

    [Fact]
    public async Task BankVerificationApproveVerifiesVendor()
    {
        var h = Create();
        h.Erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.BankVerification, "Vendor", "V-TEST"),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "called the bank", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var bank = await h.Erp.GetVendorBankAsync("V-TEST");
        Assert.NotNull(bank);
        Assert.True(bank.Verified);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Approved, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task BankVerificationRejectLeavesMasterUnverified()
    {
        var h = Create();
        h.Erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.BankVerification, "Vendor", "V-TEST"),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", false, "could not reach", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var bank = await h.Erp.GetVendorBankAsync("V-TEST");
        Assert.NotNull(bank);
        Assert.False(bank.Verified);
    }

    [Fact]
    public async Task ReviewApprovalReleasesMatchingHold()
    {
        var h = Create();
        var hold = await h.Erp.SetHoldAsync(
            "DOCK", "test hold", "RULE-1", "CASE-1", "agent",
            "Receipt", "RCP-1", null, null, "CTN-1", Idem());
        var review = UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.DockReview, "Receipt", "RCP-1")
            with { RuleId = "RULE-1", EvidenceRefs = ["carton:CTN-1"] };
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [review]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "cleared", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(await h.Erp.GetOpenHoldsAsync("RCP-1"));
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Contains(reread.Events, e =>
            e.Kind == CaseEventKind.HoldReleased && e.Summary.Contains(hold.HoldId));
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.ReviewClosed);
    }

    [Fact]
    public async Task ReviewApprovalWithNoHoldWarnsButCloses()
    {
        var h = Create();
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.ApReview, "Invoice", "V-1/INV-1"),
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.NotEmpty(outcome.Warnings);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Approved, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task FailedAdvanceOffersExplicitRetry()
    {
        var h = Create(new FailCallsClient(new FakeModelClient(), 1, 2));
        h.Erp.SeedSalesOrder(new SalesOrder(
            "SO-1", SalesOrderStatus.Confirmed, "TST", [new SalesOrderLine("ITEM-1", 5m)]));
        h.Erp.SeedItem(new ItemDto("ITEM-1", "Test item", false, 6, "EA"));
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        var file = UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]);
        var staged = file with
        {
            Case = file.Case with { Stage = CaseStage.PrSubmitted, PrNumber = pr.PrNumber }
        };
        var events = staged.Events.ToList();
        events.Add(new CaseEvent(
            "CASE-1-e1", "CASE-1", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.Note, "V-TEST", null, "Candidates", "ITEM-1", null, null, null));
        await h.Store.WriteAsync(staged with { Events = events });

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.True(outcome.AdvanceFailed);
        Assert.Contains(outcome.Warnings, w => w.Contains("did not advance"));

        var retry = await Outcomes(h).RetryAdvanceAsync("CASE-1");

        Assert.True(retry.Ok, retry.Message);
        Assert.False(retry.AdvanceFailed, retry.Message);
        Assert.Contains("Case advanced to", retry.Message);
        var pos = await h.Erp.GetPurchaseOrdersAsync();
        var po = Assert.Single(pos);
        Assert.Equal("V-TEST", po.VendorId);
    }

    [Fact]
    public async Task RetryAdvanceOnMissingCaseFailsCleanly()
    {
        var h = Create();

        var retry = await Outcomes(h).RetryAdvanceAsync("NOPE");

        Assert.False(retry.Ok);
        Assert.Contains("was not found", retry.Message);
    }

    private static async Task<PurchaseOrder> ApprovedPoAsync(Harness h)
    {
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Idem());
        await h.Erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var po = await h.Erp.CreatePoAsync(pr.PrNumber, "V-TEST", RecordSource.P2pAgent, "requester", Idem());
        await h.Erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "requester", Idem());
        await h.Erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        await h.Erp.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.P2pAgent, "requester",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return po;
    }
}
