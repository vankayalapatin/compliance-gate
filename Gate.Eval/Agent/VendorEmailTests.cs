// Vendor email tests (offline, scripted model): Spark-composed queries go to
// the vendor master email (missing addresses block with a gap error, never
// an invented one); sends and replies are idempotent; replies are read via
// Spark and acted on; AwaitVendorReply parks until the reply arrives.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class VendorEmailTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, FakeModelClient Models, string CaseDir);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-maill-test", Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
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

    private static void SeedEmail(Harness h, string vendorId, string email)
    {
        var current = h.Erp.GetVendorsAsync().GetAwaiter().GetResult()
            .Single(v => v.VendorId == vendorId);
        h.Erp.SeedVendor(current with { Email = email });
    }

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    [Fact]
    public async Task SendVendorEmailComposesStoresAndEvents()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        Assert.False(intake.Persisted);
        h.Models.QueueResponse("""{"subject":"Missing PO ref","body":"Please confirm the PO number."}""");

        var email = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");

        Assert.Equal(caseId, email.CaseId);
        Assert.Equal("V-C", email.VendorId);
        Assert.Equal(RecordSource.P2pAgent, email.From);
        Assert.Equal("sales@screws.example", email.To);
        Assert.Equal("Missing PO ref", email.Subject);
        Assert.Equal("V-C/INV-BADPO", email.InvoiceRef);
        var mail = EmailStore.Load(h.CaseDir, caseId);
        Assert.Equal(email, Assert.Single(mail.Messages));
        var emailed = Load(h, caseId).Events.Single(e => e.Kind == CaseEventKind.VendorEmailed);
        Assert.Equal("V-C/INV-BADPO", emailed.DocumentNumber);
        Assert.Equal($"mail:{email.EmailId}", emailed.EvidenceRef);
    }

    [Fact]
    public async Task SendIsIdempotentWithZeroNewModelCalls()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("""{"subject":"s","body":"b"}""");

        var first = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");
        var second = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");

        Assert.Equal(first.EmailId, second.EmailId);
        Assert.Equal(1, h.Models.CallCount);
        Assert.Single(EmailStore.Load(h.CaseDir, caseId).Messages);
        Assert.Single(Load(h, caseId).Events, e => e.Kind == CaseEventKind.VendorEmailed);
    }

    [Fact]
    public async Task MissingVendorEmailBlocksSendingWithAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO"));

        Assert.Contains("no email in the vendor master", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Models.CallCount);
        Assert.False(File.Exists(EmailStore.MailFilePath(h.CaseDir, caseId)));
    }

    [Fact]
    public async Task SendViaToolLoopNotesTheGapOnTheTranscriptAndRethrows()
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        models.QueueToolCalls("email the vendor",
            ("c1", AgentTools.SendVendorEmail,
                $$"""{"case_id":"{{caseId}}","invoice_ref":"V-C/INV-BADPO"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AdvanceAsync(caseId));

        Assert.Contains("no email in the vendor master", ex.Message, StringComparison.Ordinal);
        var file = CaseStore.Load(dir, caseId)!;
        Assert.Contains(file.Events, e =>
            e.Kind == CaseEventKind.Note && (e.Summary ?? string.Empty).Contains("send_vendor_email", StringComparison.Ordinal));
        var turn = Assert.Single(CaseTranscript.ReadTurns(dir, caseId));
        Assert.Equal(ToolCallVerdict.Failed, turn.Calls[0].Verdict);
        Assert.Contains("Turn aborted", turn.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownVendorAndNoPoVendorBlocksSending()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-X", "V-404", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.SendVendorEmailAsync(caseId, "V-404/INV-X"));

        Assert.Contains("No vendor to query", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryWithoutAnUnemailedProblemIsRefused()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-NOPE"));

        Assert.Contains("no unemailed AP problem", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonCompositionFallsBackToRawBody()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("Dear vendor, please help.");

        var email = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");

        Assert.Equal("Dear vendor, please help.", email.Body);
        Assert.Contains("V-C/INV-BADPO", email.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyIsReadAndActedOnViaCorrectedReintake()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-B", "sales@cells.example");
        await h.Runner.AdvanceAsync(caseId);
        var raised = Load(h, caseId);
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
        var ordered = Load(h, caseId);
        var poApproval = ordered.Decisions.Single(d => d.Kind == DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poApproval.DecisionId, true, poApproval.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        var poNumber = Load(h, caseId).Case.PoNumber!;
        var po = (await h.Erp.GetPurchaseOrderAsync(poNumber))!;
        var poLine = po.Lines.Single(l => l.ItemId == SeededErp.Battery);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(caseId, "ASN-R1", "PACK-R1",
            [new AsnCartonInput("CTN-R1", SeededErp.Battery, (int)poLine.QtyOrdered, "LOT-R1")]));

        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-MISS", null, null,
            [new SupplierInvoiceLineInput(null, 5, 10)]));
        h.Models.QueueResponse("""{"subject":"Need PO ref","body":"Which PO is INV-MISS for?"}""");
        var query = await h.Runner.SendVendorEmailAsync(caseId, "/INV-MISS");
        Assert.Equal("V-B", query.VendorId);

        var qty = poLine.QtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var price = poLine.UnitPrice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Models.QueueResponse(
            $$"""{"supplier_invoice_number":"INV-FIX","vendor_id_or_name":"V-B","po_number":"{{poNumber}}","lines":[{"po_line_ref":"{{poLine.PoLineRef}}","quantity":{{qty}},"unit_price":{{price}} }],"bank_account_ref":null,"summary":"Corrected details."}""");
        var reply = await h.Runner.OnVendorReplyAsync(new VendorReplyInput(
            caseId, "sales@cells.example", "INV-MISS was for your order; corrected attached.",
            InReplyTo: query.EmailId, EmailId: $"{caseId}-r-001"));

        Assert.Equal("V-B", reply.VendorId);
        Assert.Equal("/INV-MISS", reply.InvoiceRef);
        var stored = await h.Erp.GetInvoiceAsync("V-B", "INV-FIX");
        Assert.NotNull(stored);
        Assert.All(stored.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
        var cased = Load(h, caseId);
        Assert.Contains(cased.Case.Invoices, k => k.SupplierInvoiceNumber == "INV-FIX");
        Assert.Equal(CaseStage.InvoiceMatched, cased.Case.Stage);
        Assert.Contains(cased.Events, e =>
            e.Kind == CaseEventKind.VendorReplied && e.EvidenceRef == $"mail:{reply.EmailId}");
        Assert.Contains(cased.Events, e =>
            e.Kind == CaseEventKind.Note && (e.Summary ?? string.Empty).Contains("re-intaked as V-B/INV-FIX", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplyWithNothingUsableLandsOnTheTimeline()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("""{"subject":"s","body":"b"}""");
        var query = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");
        h.Models.QueueResponse(
            """{"supplier_invoice_number":null,"vendor_id_or_name":null,"po_number":null,"lines":[],"bank_account_ref":null,"summary":"Vendor is investigating."}""");

        await h.Runner.OnVendorReplyAsync(new VendorReplyInput(
            caseId, "sales@screws.example", "Looking into it.",
            InReplyTo: query.EmailId, EmailId: $"{caseId}-r-001"));

        Assert.Contains(Load(h, caseId).Events, e =>
            e.Kind == CaseEventKind.Note && (e.Summary ?? string.Empty).Contains("Vendor is investigating.", StringComparison.Ordinal));
        Assert.Null(await h.Erp.GetInvoiceAsync("V-C", "INV-BADPO"));
    }

    [Fact]
    public async Task ReplyIsIdempotentWithZeroNewModelCalls()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("""{"subject":"s","body":"b"}""");
        var query = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");
        h.Models.QueueResponse(
            """{"supplier_invoice_number":null,"vendor_id_or_name":null,"po_number":null,"lines":[],"bank_account_ref":null,"summary":"ok"}""");
        var input = new VendorReplyInput(
            caseId, "sales@screws.example", "ok.", InReplyTo: query.EmailId, EmailId: $"{caseId}-r-001");

        var first = await h.Runner.OnVendorReplyAsync(input);
        var second = await h.Runner.OnVendorReplyAsync(input);

        Assert.Equal(first.EmailId, second.EmailId);
        Assert.Equal(2, h.Models.CallCount);
        Assert.Equal(2, EmailStore.Load(h.CaseDir, caseId).Messages.Count);
    }

    [Fact]
    public async Task UnparseableReplyReadIsKeptForTheClerk()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("""{"subject":"s","body":"b"}""");
        var query = await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");
        h.Models.QueueResponse("definitely not json {");

        await h.Runner.OnVendorReplyAsync(new VendorReplyInput(
            caseId, "sales@screws.example", "see attached scan.",
            InReplyTo: query.EmailId, EmailId: $"{caseId}-r-001"));

        Assert.Contains(Load(h, caseId).Events, e =>
            e.Kind == CaseEventKind.Note && (e.Summary ?? string.Empty).Contains("needs clerk reading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplyFromUnknownAddressWithNoPoVendorIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.OnVendorReplyAsync(new VendorReplyInput(
                caseId, "stranger@example.net", "hello")));

        Assert.Contains("matches no vendor master email", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitVendorReplyParksUntilTheReplyArrives()
    {
        var now = DateTimeOffset.UtcNow;
        var emailed = new CaseEvent("e1", "case-1", now, RecordSource.P2pAgent,
            CaseEventKind.VendorEmailed, "Queried.", null, "Invoice", "V-B/INV-1", "mail:q", null, null);
        var file = new CaseFile(
            new Case("case-1", "SO-1", "BAT-1", "test", CaseStage.InvoiceMatched, now, now,
                "PR-1", "PO-1", "ASN-1", "RCT-1", [], [], null, null),
            [emailed], []);
        var view = new CaseErpView(true, PrStatus.Closed, PoApprovalStatus.Approved,
            true, true, true, 1, 100, 100, false, 0, true);

        Assert.True(CaseNextStep.AwaitVendorReply.IsWaiting());
        Assert.Equal(CaseNextStep.AwaitVendorReply, CasePicker.Pick(file, view).Step);
        Assert.DoesNotContain(AgentTools.SendVendorEmail, CasePicker.LegalTools(file, view));

        var replied = file with
        {
            Events = file.Events.Append(new CaseEvent("e2", "case-1", now, "V-B",
                CaseEventKind.VendorReplied, "Replied.", null, "Invoice", "V-B/INV-1", "mail:r", null, null)).ToList(),
        };
        Assert.Equal(CaseNextStep.FinishCase, CasePicker.Pick(replied, view).Step);
    }

    [Fact]
    public void CaseMailRoundTrips()
    {
        var dir = TempDir();
        Assert.Empty(EmailStore.Load(dir, "case-9").Messages);
        var mail = new CaseMail("case-9",
        [
            new VendorEmail("case-9-q-1", "case-9", "V-B", RecordSource.P2pAgent,
                "sales@cells.example", "Query", "Which PO?", DateTimeOffset.UtcNow,
                InvoiceRef: "V-B/INV-1"),
        ]);
        EmailStore.Save(dir, mail);

        var loaded = EmailStore.Load(dir, "case-9");

        Assert.Equal(mail.CaseId, loaded.CaseId);
        Assert.Equal(mail.Messages, loaded.Messages);
        var same = EmailStore.Append(dir, mail.Messages[0]);
        Assert.Equal(mail.Messages[0], same);
        Assert.Single(EmailStore.Load(dir, "case-9").Messages);
    }

    private sealed class GhostVendorErp : IErpAdapter
    {
        private readonly PurchaseOrder _po;
        private readonly IReadOnlyList<VendorDto> _vendors;

        public GhostVendorErp(PurchaseOrder po, IReadOnlyList<VendorDto> vendors)
        {
            _po = po;
            _vendors = vendors;
        }

        public Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default) =>
            Task.FromResult(_vendors);

        public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default) =>
            Task.FromResult<PurchaseOrder?>(string.Equals(poNumber, _po.PoNumber, StringComparison.Ordinal) ? _po : null);

        public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AsnDto> CreateAsnAsync(string asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task MissingPoVendorInMasterIsAModeledGapError()
    {
        var now = DateTimeOffset.UtcNow;
        var dir = TempDir();
        var caseId = "ghost-case-BAT-1";
        var po = new PurchaseOrder(
            "PO-GHOST", "PR-GHOST", "V-GHOST", "NET0",
            PoApprovalStatus.Approved, PoDocStatus.Open, "USD",
            RecordSource.P2pAgent, "planner", "ghost-po", []);
        var erp = new GhostVendorErp(po, [new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null, "NET0")]);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        CaseStore.Save(dir, new CaseFile(
            new Case(caseId, "SO-GHOST", SeededErp.Battery, "ghost", CaseStage.InvoiceMatched,
                now, now, "PR-GHOST", "PO-GHOST", null, null, [], [], null, null),
            [],
            [new Decision($"{caseId}-d001", caseId, DecisionKind.ApReview, "AP review for /INV-GHOST",
                DecisionStatus.Pending, "clerk", now, null, null, "Invoice", "/INV-GHOST",
                50m, "USD", null, "AP-C3: vendor or PO missing; held for the AP clerk.",
                ["invoice:/INV-GHOST"], null)]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.SendVendorEmailAsync(caseId, "/INV-GHOST"));

        Assert.Contains("V-GHOST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing from the vendor master", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, models.CallCount);
    }

    [Fact]
    public async Task WebhookEnumerationIgnoresMailFiles()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        SeedEmail(h, "V-C", "sales@screws.example");
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BADPO", "V-C", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        h.Models.QueueResponse("""{"subject":"s","body":"b"}""");
        await h.Runner.SendVendorEmailAsync(caseId, "V-C/INV-BADPO");

        Assert.DoesNotContain(CaseStore.ListCaseIds(h.CaseDir), id => id.EndsWith(".mail", StringComparison.Ordinal));
        var replayed = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        Assert.Equal(2, replayed.Count);
    }
}
