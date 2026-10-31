// Vendor-identity tests (offline, scripted model): exact C# id/name matches
// need no card and no model call; blank text needs nothing; non-blank
// unmatched text gets a Spark suggestion plus a clerk VendorIdentity card
// (photo or mail refs as evidence) with no auto-matching; unknown, blank,
// or malformed suggestions are loud gap errors; approve completes the stored
// intake with the confirmed vendor, reject keeps the missing-fields path;
// direct intake while a card pends is refused.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class VendorIdentityTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, FakeModelClient Models, string CaseDir);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-vendorid-test", Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        erp.SeedUser(new UserDto("clerk", "ap-clerk", 0, "USD"));
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, models, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static async Task<string> OpenBatteryCaseAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        return cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
    }

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    private static string Idem() => Guid.NewGuid().ToString("N");

    private static async Task<PurchaseOrder> CreateReceivedSmallPoAsync(
        InMemoryErpAdapter erp, string vendor, string item, decimal qty, string lot)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine(item, qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-SMALL", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines.Single().PoLineRef, qty, 0, 0, lot, null)], Idem());
        return (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
    }

    [Fact]
    public async Task ExactIdMatchNeedsNoCardAndNoModelCall()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);

        var card = await h.Runner.ProposeVendorIdentityAsync(caseId, "V-B", ["image:INV-1"]);

        Assert.Null(card);
        Assert.Equal(0, h.Models.CallCount);
        Assert.DoesNotContain(Load(h, caseId).Decisions, d => d.Kind == DecisionKind.VendorIdentity);
    }

    [Fact]
    public async Task ExactNameMatchNeedsNoCardAndNoModelCall()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);

        var card = await h.Runner.ProposeVendorIdentityAsync(
            caseId, "Compliant Cells Inc", ["image:INV-1"]);

        Assert.Null(card);
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task BlankTextNeedsNoCardAndNoModelCall()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);

        Assert.Null(await h.Runner.ProposeVendorIdentityAsync(caseId, null, ["image:INV-1"]));
        Assert.Null(await h.Runner.ProposeVendorIdentityAsync(caseId, "   ", ["image:INV-1"]));
        Assert.Equal(0, h.Models.CallCount);
    }

    [Fact]
    public async Task UnmatchedTextRaisesACardWithNoAutoMatching()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        var card = await h.Runner.ProposeVendorIdentityAsync(
            caseId, "Compliant Cells (V-B)", ["image:INV-IMG-1"]);

        Assert.NotNull(card);
        Assert.Equal(DecisionKind.VendorIdentity, card.Kind);
        Assert.Equal(DecisionStatus.Pending, card.Status);
        Assert.Equal("clerk", card.RequestedTo);
        Assert.Equal("Vendor", card.DocumentType);
        Assert.Equal("V-B", card.DocumentNumber);
        Assert.Equal("Compliant Cells (V-B)", card.Reason);
        Assert.Equal(["image:INV-IMG-1"], card.EvidenceRefs);
        Assert.Equal(1, h.Models.CallCount);
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", "INV-1"));
        Assert.DoesNotContain(
            Load(h, caseId).Decisions, d => d.Kind == DecisionKind.ApReview);
        Assert.Contains(Load(h, caseId).Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains("proposes V-B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownSuggestionIsALoudGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"V-404"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ProposeVendorIdentityAsync(caseId, "Mystery Co", ["image:INV-1"]));

        Assert.Contains("V-404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not in the vendor master", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to invent", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Load(h, caseId).Decisions, d => d.Kind == DecisionKind.VendorIdentity);
    }

    [Fact]
    public async Task BlankSuggestionIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"  "}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ProposeVendorIdentityAsync(caseId, "Mystery Co", ["image:INV-1"]));

        Assert.Contains("blank", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to invent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSuggestionIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("definitely not json {");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ProposeVendorIdentityAsync(caseId, "Mystery Co", ["image:INV-1"]));

        Assert.Contains("did not parse", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to invent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSuggestionKeyIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"other":"V-B"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ProposeVendorIdentityAsync(caseId, "Mystery Co", ["image:INV-1"]));

        Assert.Contains("blank", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProposeIsIdempotentForTheSameRawTextAndEvidence()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        var first = await h.Runner.ProposeVendorIdentityAsync(
            caseId, "Compliant Cells (V-B)", ["image:INV-1"]);
        var second = await h.Runner.ProposeVendorIdentityAsync(
            caseId, "Compliant Cells (V-B)", ["image:INV-1"]);

        Assert.Equal(first!.DecisionId, second!.DecisionId);
        Assert.Equal(1, h.Models.CallCount);
        Assert.Single(Load(h, caseId).Decisions, d => d.Kind == DecisionKind.VendorIdentity);
    }

    [Fact]
    public async Task DifferentEvidenceGetsSeparateCards()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        await h.Runner.ProposeVendorIdentityAsync(caseId, "Compliant Cells (V-B)", ["image:INV-1"]);
        await h.Runner.ProposeVendorIdentityAsync(caseId, "Compliant Cells (V-B)", ["image:INV-2"]);

        Assert.Equal(2, Load(h, caseId).Decisions.Count(d => d.Kind == DecisionKind.VendorIdentity));
        Assert.Equal(2, h.Models.CallCount);
    }

    [Fact]
    public async Task UnknownCaseIsAGapError()
    {
        var h = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ProposeVendorIdentityAsync("nope", "Some Co", ["image:INV-1"]));
    }

    [Fact]
    public async Task PhotoFlowApproveConfirmsTheMatchIntoIntake()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-B", SeededErp.Battery, 50, "LOT-VID");
        var line = small.Lines.Single();
        var qty = line.QtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var price = line.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Models.QueueResponse(
            $$"""{"supplier_invoice_number":"INV-VID","vendor_id_or_name":"Compliant Cells (V-B)","po_number":"{{small.PoNumber}}","lines":[{"po_line_ref":"{{line.PoLineRef}}","quantity":{{qty}},"unit_price":{{price}}}],"bank_account_ref":null}""");
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        var transcribed = await h.Runner.TranscribeInvoicePhotosAsync(
            caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);

        Assert.Null(transcribed.VendorIdOrName);
        var card = Load(h, caseId).Decisions.Single(d => d.Kind == DecisionKind.VendorIdentity);
        Assert.Equal(["image:INV-IMG-1"], card.EvidenceRefs);
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", "INV-VID"));

        await h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, "clerk", "confirmed");

        var stored = await h.Erp.GetInvoiceAsync("V-B", "INV-VID");
        Assert.NotNull(stored);
        Assert.All(stored.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
        var file = Load(h, caseId);
        Assert.Equal(DecisionStatus.Approved,
            file.Decisions.Single(d => d.DecisionId == card.DecisionId).Status);
        Assert.Contains(file.Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains("Vendor identity confirmed", StringComparison.Ordinal));
        Assert.Null(VendorIdentityStore.Find(h.CaseDir, caseId, card.DecisionId));
    }

    [Fact]
    public async Task PhotoFlowRejectKeepsTheMissingFieldsPath()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-VIDR","vendor_id_or_name":"Mystery Supplier Co","po_number":null,"lines":[{"po_line_ref":null,"quantity":5,"unit_price":10}],"bank_account_ref":null}""");
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        await h.Runner.TranscribeInvoicePhotosAsync(caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);
        var card = Load(h, caseId).Decisions.Single(d => d.Kind == DecisionKind.VendorIdentity);

        await h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, false, "clerk", "not ours");

        var file = Load(h, caseId);
        Assert.Equal(DecisionStatus.Rejected,
            file.Decisions.Single(d => d.DecisionId == card.DecisionId).Status);
        var review = Assert.Single(file.Decisions, d => d.Kind == DecisionKind.ApReview);
        Assert.Equal("clerk", review.RequestedTo);
        Assert.Contains(file.Events, e =>
            (e.Summary ?? string.Empty).Contains("AP-C3", StringComparison.Ordinal));
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", "INV-VIDR"));
        Assert.Null(VendorIdentityStore.Find(h.CaseDir, caseId, card.DecisionId));
    }

    [Fact]
    public async Task DirectIntakeWhileACardPendsIsRefused()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-WAIT","vendor_id_or_name":"Mystery Co","po_number":null,"lines":[{"po_line_ref":null,"quantity":5,"unit_price":10}],"bank_account_ref":null}""");
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        await h.Runner.TranscribeInvoicePhotosAsync(caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
                caseId, "INV-WAIT", null, null,
                [new SupplierInvoiceLineInput(null, 5, 10)])));

        Assert.Contains("pending vendor identity confirm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MailFlowUnmatchedVendorRaisesACardWithTheMailRef()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        var vendor = (await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-B");
        h.Erp.SeedVendor(vendor with { Email = "sales@cells.example" });
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-B", SeededErp.Battery, 50, "LOT-VIDM");
        var line = small.Lines.Single();
        var qty = line.QtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var price = line.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Models.QueueResponse(
            $$"""{"supplier_invoice_number":"INV-VIDM","vendor_id_or_name":"Compliant Cells (V-B)","po_number":"{{small.PoNumber}}","lines":[{"po_line_ref":"{{line.PoLineRef}}","quantity":{{qty}},"unit_price":{{price}}}],"bank_account_ref":null,"summary":"Corrected details."}""");
        h.Models.QueueResponse("""{"vendor_id":"V-B"}""");

        var reply = await h.Runner.OnVendorReplyAsync(new VendorReplyInput(
            caseId, "sales@cells.example", "Corrected invoice attached.",
            EmailId: $"{caseId}-r-001"));

        var card = Load(h, caseId).Decisions.Single(d => d.Kind == DecisionKind.VendorIdentity);
        Assert.Equal("V-B", card.DocumentNumber);
        Assert.Equal("Compliant Cells (V-B)", card.Reason);
        Assert.Equal([$"mail:{reply.EmailId}"], card.EvidenceRefs);
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", "INV-VIDM"));
        Assert.Contains(Load(h, caseId).Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains("clerk confirm needed", StringComparison.Ordinal));

        await h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, "clerk", "confirmed");

        var stored = await h.Erp.GetInvoiceAsync("V-B", "INV-VIDM");
        Assert.NotNull(stored);
        Assert.All(stored.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }

    [Fact]
    public async Task ResolveWithoutPendingIntakeJustMarksTheCard()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("""{"vendor_id":"V-C"}""");

        var card = await h.Runner.ProposeVendorIdentityAsync(
            caseId, "Screws Co (V-C)", ["image:INV-1"]);

        var approved = await h.Runner.ResolveDecisionAsync(
            caseId, card!.DecisionId, true, "clerk", "ok");

        Assert.Equal(DecisionStatus.Approved, approved.Status);
        Assert.Contains(Load(h, caseId).Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains("Vendor identity confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PendingStoreRoundTripsAndListsCleanly()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        Assert.Empty(VendorIdentityStore.Load(h.CaseDir, caseId).Pendings);
        var pending = new VendorIdentityPending(
            "d1", caseId, "INV-1", "Raw Co", "V-B", null,
            [new SupplierInvoiceLineInput(null, 5, 10)], null);
        VendorIdentityStore.Add(h.CaseDir, pending);

        var found = VendorIdentityStore.Find(h.CaseDir, caseId, "d1");
        Assert.NotNull(found);
        Assert.Equal(pending.DecisionId, found.DecisionId);
        Assert.Equal(pending.CaseId, found.CaseId);
        Assert.Equal(pending.SupplierInvoiceNumber, found.SupplierInvoiceNumber);
        Assert.Equal(pending.RawVendorText, found.RawVendorText);
        Assert.Equal(pending.ProposedVendorId, found.ProposedVendorId);
        Assert.Equal(pending.PoNumber, found.PoNumber);
        Assert.Equal(pending.BankAccountRef, found.BankAccountRef);
        var line = Assert.Single(found.Lines);
        Assert.Null(line.PoLineRef);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(10m, line.UnitPrice);
        Assert.NotNull(VendorIdentityStore.FindByInvoiceNumber(h.CaseDir, caseId, "INV-1"));
        Assert.Contains(caseId, CaseStore.ListCaseIds(h.CaseDir));
        Assert.DoesNotContain(CaseStore.ListCaseIds(h.CaseDir),
            id => id.Contains("vendor-identity", StringComparison.Ordinal));

        VendorIdentityStore.Remove(h.CaseDir, caseId, "d1");
        Assert.Empty(VendorIdentityStore.Load(h.CaseDir, caseId).Pendings);
    }
}
