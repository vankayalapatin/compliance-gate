// Opening-seed tests: the projector maps ERP pending approvals 1:1 to
// opening shells plus Decision cards with zero agent-authored timeline
// entries, skips existing shells/cards on re-run (never rewriting decided
// cards), and no-ops on empty pending. The throwing model client proves
// zero model calls. Test-local ERP data only; nothing depends on demo
// numbers.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class OpeningSeedTests
{
    private const string Item = "TEST-1";
    private const string Vendor = "V-T";
    private const string Clerk = "clerk";
    private const string Boss = "boss";

    private sealed record Harness(
        PendingApprovalDouble Erp, CaseRunner Runner, ThrowingModelClient Models, string CaseDir);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-opening-seed-test", Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var fake = new InMemoryErpAdapter();
        fake.SeedBaseCurrency("USD");
        fake.SeedAutoApproveThreshold(500m);
        fake.SeedItem(new ItemDto(Item, "Test part", false, 10, "EA"));
        fake.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        fake.SeedVendor(new VendorDto(Vendor, "Test Vendor", "US", false, null, "NET0"));
        fake.SeedVendorBank(new VendorBankDto(Vendor, "BANK-T", true, DateTimeOffset.UtcNow));
        fake.SeedVendorPrice(Vendor, Item, 10m);
        fake.SeedUser(new UserDto(Clerk, "requester", 5000m, "USD"));
        fake.SeedUser(new UserDto(Boss, "approver", 100_000m, "USD"));
        var erp = new PendingApprovalDouble(fake);
        var models = new ThrowingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, models, dir);
    }

    /// <summary>A pending big PR plus a pending PO (via an approved mid PR,
    /// which itself drops out of pending).</summary>
    private static async Task SeedPrAndPoAsync(PendingApprovalDouble erp)
    {
        var big = await erp.CreatePrAsync(
            Clerk, RecordSource.Human, Clerk,
            [new PurchaseRequisitionLine(Item, 100, "EA")], "t:pr-big");
        await erp.SubmitForApprovalAsync("PR", big.PrNumber, Boss, Clerk, "t:pr-big:submit");

        var mid = await erp.CreatePrAsync(
            Clerk, RecordSource.Human, Clerk,
            [new PurchaseRequisitionLine(Item, 100, "EA")], "t:pr-mid");
        await erp.SubmitForApprovalAsync("PR", mid.PrNumber, Boss, Clerk, "t:pr-mid:submit");
        await erp.RecordApprovalAsync("PR", mid.PrNumber, Boss, true, null, "t:pr-mid:approve");
        var po = await erp.CreatePoAsync(mid.PrNumber, Vendor, RecordSource.Human, Clerk, "t:po");
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, Boss, Clerk, "t:po:submit");
    }

    /// <summary>A pending invoice plus a pending payment draft (via a second
    /// invoice that is approved and posted, which itself drops out).</summary>
    private static async Task SeedInvoiceAndPaymentAsync(PendingApprovalDouble erp)
    {
        var pr = await erp.CreatePrAsync(
            Clerk, RecordSource.Human, Clerk,
            [new PurchaseRequisitionLine(Item, 10, "EA")], "t:inv:pr");
        var po = await erp.CreatePoAsync(pr.PrNumber, Vendor, RecordSource.Human, Clerk, "t:inv:po");
        await erp.RecordVendorConfirmationAsync(po.PoNumber,
            DateOnly.FromDateTime(DateTime.UtcNow), RecordSource.Vendor, Vendor,
            [new VendorConfirmLine("PO-L1", 10, null)], "t:inv:ack");
        await erp.CreateAsnAsync("ASN-T1", po.PoNumber, RecordSource.Vendor, Vendor,
            [new AsnCarton("C-T1", Item, 10, "LOT-T1")], "t:inv:asn");
        await erp.PostReceiptAsync(po.PoNumber, "SLIP-T1", RecordSource.Human, Clerk,
            [new ReceiptLine("PO-L1", 20, 0, 0, "LOT-T1", null)], "t:inv:rct");

        await erp.CreateInvoiceAsync("INV-T1", Vendor, po.PoNumber, RecordSource.Vendor, Clerk,
            [new InvoiceLineInput("PO-L1", 10, 10)], "t:inv:1");
        await erp.RecordMatchResultAsync(Vendor, "INV-T1",
            [new LineMatchResult("PO-L1", true, "Match.")], "t:inv:1:match");
        await erp.SubmitForApprovalAsync("Invoice", $"{Vendor}/INV-T1", Boss, Clerk, "t:inv:1:submit");

        await erp.CreateInvoiceAsync("INV-T2", Vendor, po.PoNumber, RecordSource.Vendor, Clerk,
            [new InvoiceLineInput("PO-L1", 10, 10)], "t:inv:2");
        await erp.RecordMatchResultAsync(Vendor, "INV-T2",
            [new LineMatchResult("PO-L1", true, "Match.")], "t:inv:2:match");
        await erp.SubmitForApprovalAsync("Invoice", $"{Vendor}/INV-T2", Boss, Clerk, "t:inv:2:submit");
        await erp.RecordApprovalAsync("Invoice", $"{Vendor}/INV-T2", Boss, true, null, "t:inv:2:approve");
        await erp.PostInvoiceAsync(Vendor, "INV-T2", Clerk, "t:inv:2:post");
        var pay = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey(Vendor, "INV-T2")], RecordSource.Human, Clerk, "t:pay");
        await erp.SubmitForApprovalAsync("Payment", pay.PaymentNumber, Boss, Clerk, "t:pay:submit");
    }

    private static void AssertOpeningCard(
        Harness h, PendingApprovalDto row, DecisionKind kind, string scenario)
    {
        var caseId = CaseStore.MintCaseId(scenario, row.DocumentType, row.DocumentNumber);
        var file = CaseStore.Load(h.CaseDir, caseId);
        Assert.NotNull(file);
        Assert.True(file.Case.IsOpeningData);
        Assert.Equal(scenario, file.Case.ScenarioName);
        Assert.Empty(file.Events);
        var card = Assert.Single(file.Decisions);
        Assert.Equal(kind, card.Kind);
        Assert.Equal(DecisionStatus.Pending, card.Status);
        Assert.Equal(row.RoutedTo, card.RequestedTo);
        Assert.Equal(row.DocumentType, card.DocumentType);
        Assert.Equal(row.DocumentNumber, card.DocumentNumber);
        Assert.Equal(row.DocumentAmount, card.Amount);
        Assert.Contains(row.DocumentNumber, card.Reason, StringComparison.Ordinal);
        Assert.Contains(row.SubmittedBy, card.Reason, StringComparison.Ordinal);
        Assert.Null(card.RuleId);
        Assert.Empty(card.EvidenceRefs);
    }

    [Fact]
    public async Task PrAndPoProjectOneToOne()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);
        var pending = await h.Erp.GetPendingApprovalsAsync();
        Assert.Equal(2, pending.Count);

        var ids = await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, CaseStore.ListCaseIds(h.CaseDir).Count);
        foreach (var row in pending)
        {
            var kind = row.DocumentType == "PR" ? DecisionKind.PrApproval : DecisionKind.PoApproval;
            AssertOpeningCard(h, row, kind, "seedtest");
        }

        // Reverse direction: every seeded card matches a pending row.
        foreach (var caseId in CaseStore.ListCaseIds(h.CaseDir))
        {
            var card = Assert.Single(CaseStore.Load(h.CaseDir, caseId)!.Decisions);
            Assert.Contains(pending, r =>
                r.DocumentType == card.DocumentType && r.DocumentNumber == card.DocumentNumber
                && r.RoutedTo == card.RequestedTo && r.DocumentAmount == card.Amount);
        }

        var prShell = CaseStore.Load(
            h.CaseDir, CaseStore.MintCaseId("seedtest", "PR", pending.First(r => r.DocumentType == "PR").DocumentNumber))!;
        Assert.Equal(prShell.Decisions.Single().DocumentNumber, prShell.Case.PrNumber);
        var poShell = CaseStore.Load(
            h.CaseDir, CaseStore.MintCaseId("seedtest", "PO", pending.First(r => r.DocumentType == "PO").DocumentNumber))!;
        Assert.Equal(poShell.Decisions.Single().DocumentNumber, poShell.Case.PoNumber);
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task InvoiceAndPaymentProjectOneToOne()
    {
        var h = Create();
        await SeedInvoiceAndPaymentAsync(h.Erp);
        var pending = await h.Erp.GetPendingApprovalsAsync();
        Assert.Equal(2, pending.Count);

        var ids = await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        Assert.Equal(2, ids.Count);
        foreach (var row in pending)
        {
            var kind = row.DocumentType == "Invoice"
                ? DecisionKind.InvoiceApproval
                : DecisionKind.PaymentApproval;
            AssertOpeningCard(h, row, kind, "seedtest");
        }

        var invRow = pending.Single(r => r.DocumentType == "Invoice");
        var invShell = CaseStore.Load(
            h.CaseDir, CaseStore.MintCaseId("seedtest", "Invoice", invRow.DocumentNumber))!;
        Assert.Equal(new InvoiceKey(Vendor, "INV-T1"), Assert.Single(invShell.Case.Invoices));
        var payRow = pending.Single(r => r.DocumentType == "Payment");
        var payShell = CaseStore.Load(
            h.CaseDir, CaseStore.MintCaseId("seedtest", "Payment", payRow.DocumentNumber))!;
        Assert.Equal(payRow.DocumentNumber, Assert.Single(payShell.Case.PaymentNumbers));
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task OpeningShellsCarryZeroAgentEvents()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);
        await SeedInvoiceAndPaymentAsync(h.Erp);

        await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        var ids = CaseStore.ListCaseIds(h.CaseDir);
        Assert.Equal(4, ids.Count);
        foreach (var caseId in ids)
        {
            var file = CaseStore.Load(h.CaseDir, caseId);
            Assert.NotNull(file);
            Assert.True(file.Case.IsOpeningData);
            Assert.Empty(file.Events);
        }
    }

    [Fact]
    public async Task RerunSkipsWithoutRewriting()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);

        var first = await h.Runner.SeedOpeningDecisionsAsync("seedtest");
        var before = first.ToDictionary(
            id => id,
            id => File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, id)));

        var second = await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        Assert.Equal(first, second);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal),
            CaseStore.ListCaseIds(h.CaseDir).Order(StringComparer.Ordinal));
        foreach (var (id, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, id)));
            var file = CaseStore.Load(h.CaseDir, id)!;
            Assert.Single(file.Decisions);
            Assert.Empty(file.Events);
        }
    }

    [Fact]
    public async Task EmptyPendingIsNoOp()
    {
        var h = Create();

        var ids = await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        Assert.Empty(ids);
        Assert.Empty(CaseStore.ListCaseIds(h.CaseDir));
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task BlankScenarioNameThrowsWithoutSeeding()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Runner.SeedOpeningDecisionsAsync("  "));

        Assert.Empty(CaseStore.ListCaseIds(h.CaseDir));
    }

    [Fact]
    public async Task LiveCaseCollisionThrowsWithoutTouchingTheCase()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);
        var pending = await h.Erp.GetPendingApprovalsAsync();
        var row = pending.First();
        var caseId = CaseStore.MintCaseId("seedtest", row.DocumentType, row.DocumentNumber);
        var now = DateTimeOffset.UtcNow;
        CaseStore.Save(h.CaseDir, new CaseFile(
            new Case(caseId, "SO-9", Item, "seedtest", CaseStage.Created, now, now,
                null, null, null, null, [], [], null, null),
            [], []));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.SeedOpeningDecisionsAsync("seedtest"));

        Assert.Contains(caseId, ex.Message, StringComparison.Ordinal);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.False(file.Case.IsOpeningData);
        Assert.Empty(file.Decisions);
        Assert.Empty(file.Events);
    }

    [Fact]
    public async Task DecidedCardsAreNotReseeded()
    {
        var h = Create();
        await SeedPrAndPoAsync(h.Erp);
        var ids = await h.Runner.SeedOpeningDecisionsAsync("seedtest");
        var decidedId = ids.Order(StringComparer.Ordinal).First();
        var untouchedId = ids.Order(StringComparer.Ordinal).Last();

        // A human decision lands on the card; the file otherwise stays a shell.
        var file = CaseStore.Load(h.CaseDir, decidedId)!;
        var decided = file.Decisions.Single() with
        {
            Status = DecisionStatus.Approved,
            DecidedBy = Boss,
            DecidedAt = DateTimeOffset.UtcNow,
            Comment = "ok"
        };
        CaseStore.Save(h.CaseDir, file with { Decisions = [decided] });
        var untouchedBefore = File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, untouchedId));

        var rerun = await h.Runner.SeedOpeningDecisionsAsync("seedtest");

        Assert.Equal(ids, rerun);
        var after = CaseStore.Load(h.CaseDir, decidedId)!;
        var kept = Assert.Single(after.Decisions);
        Assert.Equal(DecisionStatus.Approved, kept.Status);
        Assert.Equal(Boss, kept.DecidedBy);
        Assert.Empty(after.Events);
        Assert.Equal(untouchedBefore, File.ReadAllBytes(CaseStore.CaseFilePath(h.CaseDir, untouchedId)));
    }
}
