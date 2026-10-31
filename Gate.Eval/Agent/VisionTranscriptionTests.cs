// Vision transcription tests (offline, scripted model): dock photos become
// carton reads through the shared prompt plus parser with Class 9
// requirement from the item master; invoice photos become validated invoices
// with unmatched vendor/PO resolving to null; every photo read caches by
// image hash; unknown parts and missing money are gap errors, never guesses.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class VisionTranscriptionTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, FakeModelClient Models, string CaseDir);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-vision-test", Guid.NewGuid().ToString("N"));

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

    private static async Task<string> OpenBatteryCaseAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"]
            }));
        return cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
    }

    private const string DockLabel = """
        {"carton_id":"CTN-1","part":"BAT-1","po_number":"PO-X","lot_code":"LOT-1","units_per_carton":10,"mark_present":true,"mark_legible":true,"un_number":"UN3480","class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.95}
        """;

    private const string ScrewLabel = """
        {"carton_id":"CTN-S","part":"SCR-9","po_number":"PO-X","lot_code":"LOT-S","units_per_carton":150,"mark_present":true,"mark_legible":true,"un_number":null,"class9_visible":false,"damage_visible":false,"damage_note":null,"read_confidence":0.9}
        """;

    [Fact]
    public async Task DockPhotosTranscribeToReads()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse($"[{DockLabel},{ScrewLabel}]");

        var reads = await h.Runner.TranscribeDockPhotosAsync(
            caseId, [new DockPhoto("IMG-1", [1, 2, 3], null)]);

        Assert.Equal(2, reads.Count);
        var battery = reads.Single(r => r.CartonId == "CTN-1");
        Assert.Equal("BAT-1", battery.Part);
        Assert.Equal("IMG-1", battery.ImageId);
        Assert.True(battery.Class9Required);
        Assert.True(battery.MarkPresent);
        Assert.False(battery.DamageVisible);
        Assert.Equal(0.95m, battery.ReadConfidence);
        Assert.False(reads.Single(r => r.CartonId == "CTN-S").Class9Required);
        Assert.Equal(1, h.Models.CallCount);
    }

    [Fact]
    public async Task DockTranscriptionHitsTheImageCache()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse($"[{DockLabel}]");
        var photo = new DockPhoto("IMG-1", [7, 7, 7], null);

        var first = await h.Runner.TranscribeDockPhotosAsync(caseId, [photo]);
        var second = await h.Runner.TranscribeDockPhotosAsync(caseId, [photo]);

        Assert.Equal(first, second);
        Assert.Equal(1, h.Models.CallCount);
        Assert.Single(Directory.GetFiles(Path.Combine(h.CaseDir, "vision-reads")));
    }

    [Fact]
    public async Task CartonHintFillsABlankLabelId()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """[{"carton_id":null,"part":"BAT-1","po_number":null,"lot_code":null,"units_per_carton":10,"mark_present":false,"mark_legible":false,"un_number":null,"class9_visible":false,"damage_visible":false,"damage_note":null,"read_confidence":0.5}]""");

        var reads = await h.Runner.TranscribeDockPhotosAsync(
            caseId, [new DockPhoto("IMG-1", [9], "CTN-H")]);

        Assert.Equal("CTN-H", Assert.Single(reads).CartonId);
    }

    [Fact]
    public async Task BlankLabelIdWithoutAHintIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """[{"carton_id":null,"part":"BAT-1","po_number":null,"lot_code":null,"units_per_carton":10,"mark_present":false,"mark_legible":false,"un_number":null,"class9_visible":false,"damage_visible":false,"damage_note":null,"read_confidence":0.5}]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync(caseId, [new DockPhoto("IMG-1", [9], null)]));

        Assert.Contains("no carton id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPartIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """[{"carton_id":"CTN-X","part":"PART-404","po_number":null,"lot_code":null,"units_per_carton":10,"mark_present":false,"mark_legible":false,"un_number":null,"class9_visible":false,"damage_visible":false,"damage_note":null,"read_confidence":0.5}]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync(caseId, [new DockPhoto("IMG-1", [9], null)]));

        Assert.Contains("PART-404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not in the ERP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnparseableDockReadRetriesOnceThenThrows()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse("not json");
        h.Models.QueueResponse("still not json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync(caseId, [new DockPhoto("IMG-1", [9], null)]));

        Assert.Contains("failed after one retry", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, h.Models.CallCount);
        Assert.False(Directory.Exists(Path.Combine(h.CaseDir, "vision-reads")));
    }

    [Fact]
    public async Task InvoicePhotosTranscribeAndResolve()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        await h.Runner.AdvanceAsync(caseId);
        var raised = CaseStore.Load(h.CaseDir, caseId)!;
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
        var poNumber = CaseStore.Load(h.CaseDir, caseId)!.Case.PoNumber!;
        var poLineRef = (await h.Erp.GetPurchaseOrderAsync(poNumber))!.Lines
            .Single(l => l.ItemId == SeededErp.Battery).PoLineRef;
        h.Models.QueueResponse(
            $$"""{"supplier_invoice_number":"INV-PHOTO","vendor_id_or_name":"V-B","po_number":"{{poNumber}}","lines":[{"po_line_ref":"{{poLineRef}}","quantity":90,"unit_price":12}],"bank_account_ref":"BANK-B-TEST"}""");

        var invoice = await h.Runner.TranscribeInvoicePhotosAsync(
            caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);

        Assert.Equal("INV-PHOTO", invoice.SupplierInvoiceNumber);
        Assert.Equal("V-B", invoice.VendorIdOrName);
        Assert.Equal(poNumber, invoice.PoNumber);
        Assert.Equal("BANK-B-TEST", invoice.BankAccountRef);
        var line = Assert.Single(invoice.Lines);
        Assert.Equal(poLineRef, line.PoLineRef);
        Assert.Equal(90m, line.Quantity);
        Assert.Equal(12m, line.UnitPrice);
    }

    [Fact]
    public async Task UnmatchedVendorAndPoRouteToTheMissingFieldsPath()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-STRANGE","vendor_id_or_name":"V-404","po_number":"PO-404","lines":[{"po_line_ref":null,"quantity":5,"unit_price":10}],"bank_account_ref":null}""");

        var invoice = await h.Runner.TranscribeInvoicePhotosAsync(
            caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);

        Assert.Equal("INV-STRANGE", invoice.SupplierInvoiceNumber);
        Assert.Null(invoice.VendorIdOrName);
        Assert.Null(invoice.PoNumber);
        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, invoice.SupplierInvoiceNumber!, invoice.VendorIdOrName, invoice.PoNumber,
            invoice.Lines, invoice.BankAccountRef));
        Assert.False(outcome.Persisted);
        Assert.Equal(50m, outcome.HeldValue);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.Contains(file.Events, e => (e.Summary ?? string.Empty).Contains("AP-C3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvoiceVendorNameResolvesToTheMasterId()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-NAME","vendor_id_or_name":"Compliant Cells Inc","po_number":null,"lines":[{"po_line_ref":null,"quantity":5,"unit_price":10}],"bank_account_ref":null}""");

        var invoice = await h.Runner.TranscribeInvoicePhotosAsync(
            caseId, [new InvoicePhoto("INV-IMG-1", [3, 3])]);

        Assert.Equal("V-B", invoice.VendorIdOrName);
        Assert.Null(invoice.PoNumber);
    }

    [Fact]
    public async Task MultiPageInvoicesMergeAcrossPhotos()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-MULTI","vendor_id_or_name":null,"po_number":null,"lines":[{"po_line_ref":"L1","quantity":5,"unit_price":10}],"bank_account_ref":null}""");
        h.Models.QueueResponse(
            """{"supplier_invoice_number":null,"vendor_id_or_name":"V-C","po_number":null,"lines":[{"po_line_ref":"L2","quantity":7,"unit_price":1}],"bank_account_ref":"BANK-C"}""");

        var invoice = await h.Runner.TranscribeInvoicePhotosAsync(caseId,
        [
            new InvoicePhoto("INV-IMG-1", [3, 3]),
            new InvoicePhoto("INV-IMG-2", [4, 4]),
        ]);

        Assert.Equal("INV-MULTI", invoice.SupplierInvoiceNumber);
        Assert.Equal("V-C", invoice.VendorIdOrName);
        Assert.Equal("BANK-C", invoice.BankAccountRef);
        Assert.Equal(2, invoice.Lines.Count);
        Assert.Equal(5 * 10m + 7 * 1m, invoice.Lines.Sum(l => l.Quantity * l.UnitPrice));
        Assert.Equal(2, h.Models.CallCount);
    }

    [Fact]
    public async Task InvoiceTranscriptionHitsTheImageCache()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-C","vendor_id_or_name":null,"po_number":null,"lines":[],"bank_account_ref":null}""");
        var photo = new InvoicePhoto("INV-IMG-1", [5, 5]);

        var first = await h.Runner.TranscribeInvoicePhotosAsync(caseId, [photo]);
        var second = await h.Runner.TranscribeInvoicePhotosAsync(caseId, [photo]);

        Assert.Equal(first.SupplierInvoiceNumber, second.SupplierInvoiceNumber);
        Assert.Equal(first.VendorIdOrName, second.VendorIdOrName);
        Assert.Equal(first.PoNumber, second.PoNumber);
        Assert.Equal(first.BankAccountRef, second.BankAccountRef);
        Assert.Equal(first.Lines, second.Lines);
        Assert.Equal(1, h.Models.CallCount);
    }

    [Fact]
    public async Task MissingMoneyIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-NOMONEY","vendor_id_or_name":null,"po_number":null,"lines":[{"po_line_ref":null,"quantity":5}],"bank_account_ref":null}""");
        h.Models.QueueResponse(
            """{"supplier_invoice_number":"INV-NOMONEY","vendor_id_or_name":null,"po_number":null,"lines":[{"po_line_ref":null,"quantity":5}],"bank_account_ref":null}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeInvoicePhotosAsync(caseId, [new InvoicePhoto("INV-IMG-1", [5, 5])]));

        Assert.Contains("failed after one retry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TranscriptionOnUnknownCaseThrows()
    {
        var h = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync("nope", [new DockPhoto("IMG-1", [1], null)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeInvoicePhotosAsync("nope", [new InvoicePhoto("INV-IMG-1", [1])]));
    }

    [Fact]
    public async Task MissingDamageVisibleIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """[{"carton_id":"CTN-1","part":"BAT-1","po_number":null,"lot_code":null,"units_per_carton":10,"mark_present":true,"mark_legible":true,"un_number":null,"class9_visible":true,"damage_note":null,"read_confidence":0.9}]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync(caseId, [new DockPhoto("IMG-1", [11], null)]));

        Assert.Contains("damage_visible", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CTN-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IMG-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to guess", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMarkPresentIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueResponse(
            """[{"carton_id":"CTN-1","part":"BAT-1","po_number":null,"lot_code":null,"units_per_carton":10,"mark_legible":true,"un_number":null,"class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.9}]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeDockPhotosAsync(caseId, [new DockPhoto("IMG-1", [12], null)]));

        Assert.Contains("mark_present", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CTN-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IMG-1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to guess", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvoiceMissingKeyIsAGapError()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        var bad = """{"supplier_invoice_number":"INV-NOKEY","vendor_id_or_name":null,"po_number":null,"lines":[],"bank_account_ref":null}""";
        var missing = bad.Replace("\"bank_account_ref\":null", "\"bank_account_ref_missing\":null");
        h.Models.QueueResponse(missing);
        h.Models.QueueResponse(missing);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.TranscribeInvoicePhotosAsync(caseId, [new InvoicePhoto("INV-IMG-1", [13, 13])]));

        Assert.Contains("failed after one retry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bank_account_ref", ex.InnerException?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, h.Models.CallCount);
    }
}
