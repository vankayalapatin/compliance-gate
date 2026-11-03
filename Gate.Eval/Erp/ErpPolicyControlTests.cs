// ERP control-point policy tests: the same refusal for the agent and a
// person at every control point. PO create and PO approve run sourcing
// (CORP-02 computed for the PO quantity); payment draft, approve, and post
// plus held-line release run ELEC-04 (nothing on a receipt with quarantine,
// open holds, or open reviews is payable or releasable). Test doubles only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpPolicyControlTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private sealed class StubPolicyContext(
        PolicyExceptionGrant? grant = null,
        IReadOnlyList<OpenReview>? reviews = null) : IErpPolicyContext
    {
        public IReadOnlyList<OpenReview> Reviews { get; set; } = reviews ?? [];

        public Task<PolicyExceptionGrant?> FindExceptionGrantAsync(
            string ruleId, string vendorId, string? prNumber, CancellationToken ct = default) =>
            Task.FromResult(grant);

        public Task<IReadOnlyList<OpenReview>> FindOpenReviewsAsync(
            IReadOnlyList<InvoiceKey> invoices, CancellationToken ct = default) =>
            Task.FromResult(Reviews);
    }

    private static async Task<string> ApprovedPrAsync(InMemoryErpAdapter erp, decimal qty = 90)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        return pr.PrNumber;
    }

    private static async Task<PurchaseOrder> ApprovedPoAsync(InMemoryErpAdapter erp, string prNumber, string vendor)
    {
        var po = await erp.CreatePoAsync(prNumber, vendor, RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = await erp.GetPurchaseOrderAsync(po.PoNumber)
                ?? throw new InvalidOperationException($"PO {po.PoNumber} vanished.");
        }

        return po;
    }

    [Fact]
    public async Task PersonCreatingCorpBreakingPoIsRefusedWithRuleVersionAndReason()
    {
        var erp = SeededErp.CreateWithPolicy();
        var pr = await ApprovedPrAsync(erp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem()));

        Assert.StartsWith("CORP-02 (CORP v", ex.Message, StringComparison.Ordinal);
        Assert.Contains("60", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentScreeningRefusesTheSameVendorWithTheSameRule()
    {
        var erp = SeededErp.CreateWithPolicy();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());
        var pr = await ApprovedPrAsync(erp);

        var screenings = await agent.ScreenCandidatesAsync(pr, ["V-A", "V-B"]);

        var rejected = screenings.Single(s => s.VendorId == "V-A");
        Assert.False(rejected.Passed);
        Assert.Contains(rejected.Verdicts,
            v => v.RuleId == ComplianceEngine.CorpOriginCapId && v.Kind == VerdictKind.Block);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem()));
        Assert.StartsWith(ComplianceEngine.CorpOriginCapId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingVendorStillOrdersAndApproves()
    {
        var erp = SeededErp.CreateWithPolicy();
        var pr = await ApprovedPrAsync(erp);

        var po = await ApprovedPoAsync(erp, pr, "V-B");

        Assert.Equal("V-B", po.VendorId);
    }

    [Fact]
    public async Task PartialQuantityPassesSourcingOnItsOwn()
    {
        var erp = SeededErp.CreateWithPolicy();
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 5600),
            new VolumeHistory("V-B MX active", "MX", 4000),
            new VolumeHistory("Other non-CN", "OTHER", 400),
        ]);
        var pr = await ApprovedPrAsync(erp, 1200);

        // Full quantity breaches: (5,600 + 1,200) / (10,000 + 1,200) = 60.7%.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem()));
        Assert.StartsWith(ComplianceEngine.CorpOriginCapId, refused.Message, StringComparison.Ordinal);

        // 800 on its own passes: (5,600 + 800) / (10,000 + 800) = 59.3%.
        var first = await erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem(),
            [new PoOrderLine(SeededErp.Battery, 800)]);
        Assert.Equal(800m, Assert.Single(first.Lines).QtyOrdered);

        // The second 400 to the same country sees the open 800 and refuses:
        // (5,600 + 800 + 400) / (10,000 + 800 + 400) = 60.7%.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem(),
                [new PoOrderLine(SeededErp.Battery, 400)]));
        Assert.StartsWith(ComplianceEngine.CorpOriginCapId, second.Message, StringComparison.Ordinal);

        // But 400 from Mexico passes on its own line.
        var split = await erp.CreatePoAsync(pr, "V-B", RecordSource.Human, "planner", Idem(),
            [new PoOrderLine(SeededErp.Battery, 400)]);
        Assert.Equal(400m, Assert.Single(split.Lines).QtyOrdered);
        Assert.Equal(PrStatus.Closed, (await erp.GetPrAsync(pr))?.Status);
    }

    [Fact]
    public async Task FiveSmallOrdersInARowRefuseOnceOpenQuantityCrossesCap()
    {
        var erp = SeededErp.CreateWithPolicy();
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 400),
            new VolumeHistory("V-B MX active", "MX", 600),
        ]);
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());
        // 200 cells to CinderVolt per round, nothing received: open orders
        // join the math, so round 1 passes at 50.0%, round 2 at 57.1%,
        // and rounds 3-5 refuse at 62.5% on both the agent and person path.
        for (var round = 1; round <= 5; round++)
        {
            var pr = await ApprovedPrAsync(erp, 200);
            var screenings = await agent.ScreenCandidatesAsync(pr, ["V-A"]);
            var corp = screenings.Single().Verdicts
                .Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
            if (round <= 2)
            {
                Assert.Equal(VerdictKind.Pass, corp.Kind);
                var po = await ApprovedPoAsync(erp, pr, "V-A");
                Assert.Equal("V-A", po.VendorId);
            }
            else
            {
                Assert.Equal(VerdictKind.Block, corp.Kind);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem()));
                Assert.StartsWith(ComplianceEngine.CorpOriginCapId, ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task LoggedExceptionGrantLetsThatAwardThrough()
    {
        var grant = new PolicyExceptionGrant(
            ComplianceEngine.CorpOriginCapId, "V-A", "Terry Owner",
            DateTimeOffset.UtcNow, "sole qualified source for this award");
        var erp = SeededErp.CreateWithPolicy(new StubPolicyContext(grant));
        var pr = await ApprovedPrAsync(erp);

        var po = await erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem());

        Assert.Equal("V-A", po.VendorId);
    }

    [Fact]
    public async Task NamelessExceptionGrantStillBlocks()
    {
        var grant = new PolicyExceptionGrant(
            ComplianceEngine.CorpOriginCapId, "V-A", " ", DateTimeOffset.UtcNow, " ");
        var erp = SeededErp.CreateWithPolicy(new StubPolicyContext(grant));
        var pr = await ApprovedPrAsync(erp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Idem()));
        Assert.StartsWith(ComplianceEngine.CorpOriginCapId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PoApproveRerunsSourcingAfterDocumentsChange()
    {
        var erp = SeededErp.CreateWithPolicy();
        var pr = await ApprovedPrAsync(erp);
        var po = await erp.CreatePoAsync(pr, "V-B", RecordSource.Human, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());

        // The pack document lapses between create and approve.
        erp.SeedVendorDocuments("V-B", []);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem()));

        Assert.StartsWith("BAT-LI-01 (BAT-LI v", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<(string PoNumber, string PoLineRef)> ReceivedPoAsync(InMemoryErpAdapter erp)
    {
        var pr = await ApprovedPrAsync(erp);
        var po = await ApprovedPoAsync(erp, pr, "V-B");
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var line = po.Lines.Single(l => l.ItemId == SeededErp.Battery);
        await erp.PostReceiptAsync(po.PoNumber, "PACK-T1", RecordSource.Human, "planner",
            [new ReceiptLine(line.PoLineRef, 80, 0, 0, "LOT-OPEN", null),
             new ReceiptLine(line.PoLineRef, 10, 0, 0, "LOT-QUAR", null)],
            Idem());
        return (po.PoNumber, line.PoLineRef);
    }

    private static async Task QuarantineAsync(InMemoryErpAdapter erp, string lot)
    {
        var hold = await erp.SetHoldAsync("RELABEL", "missing mark", DockEvaluator.MarkRuleId,
            "CASE-T", "planner", "Receipt", null, null, lot, null, Idem());
        await erp.QuarantineLotAsync(lot, hold.HoldId, "planner", Idem());
    }

    private static async Task<string> PostedInvoiceAsync(
        InMemoryErpAdapter erp, string poNumber, string poLineRef, string invoiceNumber = "INV-T1")
    {
        await erp.CreateInvoiceAsync(invoiceNumber, "V-B", poNumber, RecordSource.Human, "planner",
            [new InvoiceLineInput(poLineRef, 90, 12m)], Idem());
        await erp.RecordMatchResultAsync("V-B", invoiceNumber,
            [new LineMatchResult(poLineRef, true, "ok")], Idem());
        var key = $"V-B/{invoiceNumber}";
        await erp.SubmitForApprovalAsync("Invoice", key, "delegate", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", key, "delegate", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", invoiceNumber, "planner", Idem());
        return key;
    }

    [Fact]
    public async Task PaymentDraftWithQuarantinePaysAcceptedValueOnly()
    {
        var erp = SeededErp.CreateWithPolicy();
        var (poNumber, poLineRef) = await ReceivedPoAsync(erp);
        await QuarantineAsync(erp, "LOT-QUAR");
        await PostedInvoiceAsync(erp, poNumber, poLineRef);

        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-T1")], null, RecordSource.Human, "planner", Idem());

        Assert.Equal(80 * 12m, draft.Lines.Sum(l => l.Amount));
        var invoice = await erp.GetInvoiceAsync("V-B", "INV-T1");
        Assert.Contains(invoice!.Lines, l => l.Disposition == LineDisposition.Held);
    }

    [Fact]
    public async Task PaymentDraftApproveAndPostPassWithQuarantineOnly()
    {
        var erp = SeededErp.CreateWithPolicy();
        var (poNumber, poLineRef) = await ReceivedPoAsync(erp);
        await QuarantineAsync(erp, "LOT-QUAR");
        await PostedInvoiceAsync(erp, poNumber, poLineRef);

        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-T1")], null, RecordSource.Human, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());

        Assert.Equal(PaymentStatus.Posted, posted.Status);
        Assert.Equal(80 * 12m, posted.Lines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task PaymentDraftApproveAndPostRefuseOnOpenReviews()
    {
        var context = new StubPolicyContext();
        var erp = SeededErp.CreateWithPolicy(context);
        var (poNumber, poLineRef) = await ReceivedPoAsync(erp);
        await PostedInvoiceAsync(erp, poNumber, poLineRef);
        var review = new OpenReview(
            "D-1", "BAT-LI-05", "CASE-T", "CTN-T1", "LOT-OPEN",
            DateTimeOffset.UtcNow, "damage review open");

        context.Reviews = [review];
        var draftEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentDraftAsync(
                [new InvoiceKey("V-B", "INV-T1")], null, RecordSource.Human, "planner", Idem()));
        Assert.StartsWith("ELEC-04 (ELEC v", draftEx.Message, StringComparison.Ordinal);
        Assert.Contains("D-1", draftEx.Message, StringComparison.Ordinal);

        context.Reviews = [];
        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-T1")], null, RecordSource.Human, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());

        context.Reviews = [review];
        var approveEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem()));
        Assert.StartsWith("ELEC-04", approveEx.Message, StringComparison.Ordinal);

        context.Reviews = [];
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());

        context.Reviews = [review];
        var postEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem()));
        Assert.StartsWith("ELEC-04", postEx.Message, StringComparison.Ordinal);

        context.Reviews = [];
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());
        Assert.Equal(PaymentStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task HeldLineReleaseIsRefusedWhileQuarantinedThenPasses()
    {
        var erp = SeededErp.CreateWithPolicy();
        var (poNumber, poLineRef) = await ReceivedPoAsync(erp);
        await QuarantineAsync(erp, "LOT-QUAR");
        await PostedInvoiceAsync(erp, poNumber, poLineRef);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-T1", poLineRef, 10, "planner", Idem()));
        Assert.StartsWith("ELEC-04", ex.Message, StringComparison.Ordinal);

        var hold = (await erp.GetOpenHoldsAsync(null)).Single(h => h.LotRef == "LOT-QUAR");
        await erp.ReleaseHoldAsync(hold.HoldId, "planner", null, Idem());
        await erp.ReleaseLotAsync("LOT-QUAR", hold.HoldId, "planner", Idem());
        await erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-T1", poLineRef, 10, "planner", Idem());

        var invoice = await erp.GetInvoiceAsync("V-B", "INV-T1");
        Assert.All(invoice!.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }

    [Fact]
    public async Task CleanReceiptDraftsApprovesAndPosts()
    {
        var erp = SeededErp.CreateWithPolicy();
        var (poNumber, poLineRef) = await ReceivedPoAsync(erp);
        await PostedInvoiceAsync(erp, poNumber, poLineRef);

        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-T1")], null, RecordSource.Human, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());

        Assert.Equal(PaymentStatus.Posted, posted.Status);
    }
}
