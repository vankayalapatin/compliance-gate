// Vendor portal identity: the portal shows the supplier company name,
// never the bare vendor code. The title and the PO line resolve the name
// from the runtime vendor master; the code shows only when the master
// has no name for the id. bUnit renders the real component over the fake
// ERP with test-local vendors; no live calls.
using Bunit;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Outside;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class SupplierPortalTests
{
    private static (SimulationUiService Service, InMemoryErpAdapter Erp, string Dir) Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-portal");
        Directory.CreateDirectory(dir);
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto(
            "V-P", "Portal Company Ltd", "US", false, null, "NET0", "ap@portal.test"));
        erp.SeedVendorPrice("V-P", "ITEM-P", 10m);
        erp.SeedItem(new ItemDto("ITEM-P", "Portal item", false, 1, "EA"));
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        var dockDir = Path.Combine(dir, "dock");
        Directory.CreateDirectory(dockDir);
        var service = new SimulationUiService(
            new HttpClient { BaseAddress = new Uri("http://localhost/") },
            erp, new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(dockDir, Path.Combine(dir, "vision")),
            UiTestLogger<SimulationUiService>.Instance);
        return (service, erp, dir);
    }

    private static SimulateOptions OptionsWith(params VendorDto[] vendors) =>
        new("LE-T", "TST", [], vendors, [], [],
            [new ItemOption("ITEM-P", "EA")],
            new Dictionary<string, IReadOnlyList<string>>(), []);

    private static async Task SeedPoAsync(InMemoryErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-P", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync(
            "PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync(
            "PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        await erp.CreatePoAsync(
            pr.PrNumber, "V-P", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task PortalTitle_ShowsCompanyNameNotCode()
    {
        var (service, erp, _) = Create();
        await SeedPoAsync(erp);
        var options = OptionsWith(new VendorDto(
            "V-P", "Portal Company Ltd", "US", false, null, "NET0", "ap@portal.test"));
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);

        using var page = ctx.RenderComponent<SupplierPortal>(parameters => parameters
            .Add(p => p.VendorId, "V-P")
            .Add(p => p.Options, options));

        page.WaitForAssertion(
            () => Assert.Contains("Purchase orders for Portal Company Ltd", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Purchase orders for V-P", page.Markup);
        Assert.Contains("vendor Portal Company Ltd", page.Markup);
    }

    [Fact]
    public void PortalTitle_FallsBackToCodeWhenMasterHasNoName()
    {
        var (service, _, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);

        using var page = ctx.RenderComponent<SupplierPortal>(parameters => parameters
            .Add(p => p.VendorId, "V-P")
            .Add(p => p.Options, OptionsWith()));

        page.WaitForAssertion(
            () => Assert.Contains("Purchase orders for V-P", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Contains("No purchase orders for this supplier yet.", page.Markup);
    }

    private static async Task<PurchaseOrder> SeedApprovedPoAsync(InMemoryErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-P", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync(
            "PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync(
            "PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await erp.CreatePoAsync(
            pr.PrNumber, "V-P", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync(
            "PO", po.PoNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync(
            "PO", po.PoNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        return (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
    }

    private static async Task LinkCaseAsync(
        string dir, string poNumber, IReadOnlyList<CaseEvent>? events = null)
    {
        var now = DateTimeOffset.UtcNow;
        await new CaseCacheStore(Path.Combine(dir, "case-cache")).WriteAsync(new CaseFile(
            new Case("CASE-P", "SO-P", "ITEM-P", "LE-T", CaseStage.PoPlaced, now, now,
                null, poNumber, null, null, [], [], null, null),
            events ?? [], []));
    }

    private static string RenderPortal(SimulationUiService service, string vendorId)
    {
        var options = OptionsWith(new VendorDto(
            "V-P", "Portal Company Ltd", "US", false, null, "NET0", "ap@portal.test"));
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        using var page = ctx.RenderComponent<SupplierPortal>(parameters => parameters
            .Add(p => p.VendorId, vendorId)
            .Add(p => p.Options, options));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    [Fact]
    public async Task ApprovedPo_OffersAcknowledgeOnly()
    {
        var (service, erp, dir) = Create();
        var po = await SeedApprovedPoAsync(erp);
        await LinkCaseAsync(dir, po.PoNumber);

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.Acknowledge, inbox.NextAction);
        Assert.Empty(inbox.SentSteps);
        var markup = RenderPortal(service, "V-P");
        Assert.Contains(">Acknowledge<", markup);
        Assert.Contains("Nothing sent on this order yet.", markup);
        Assert.DoesNotContain(">Send a shipment notice<", markup);
        Assert.DoesNotContain(">Send an invoice<", markup);
    }

    [Fact]
    public async Task AckedPo_ShowsSentAckAndOffersShip()
    {
        var (service, erp, dir) = Create();
        var po = await SeedApprovedPoAsync(erp);
        await LinkCaseAsync(dir, po.PoNumber);
        var line = po.Lines.Single().PoLineRef;
        await erp.RecordVendorConfirmationAsync(
            po.PoNumber, new DateOnly(2026, 10, 5), RecordSource.Vendor, "V-P",
            [new VendorConfirmLine(line, 5m, null)], Guid.NewGuid().ToString("N"));

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.Ship, inbox.NextAction);
        var ack = Assert.Single(inbox.SentSteps);
        Assert.Equal("Acknowledged", ack.Step);
        Assert.Contains("2026-10-05", ack.Detail);
        var markup = RenderPortal(service, "V-P");
        Assert.Contains(">Already sent<", markup);
        Assert.Contains("2026-10-05", markup);
        Assert.Contains(">Send a shipment notice<", markup);
        Assert.DoesNotContain(">Acknowledge<", markup);
        Assert.DoesNotContain(">Send an invoice<", markup);
    }

    [Fact]
    public async Task ShippedPo_ShowsSentShipmentAndOffersInvoice()
    {
        var (service, erp, dir) = Create();
        var po = await SeedApprovedPoAsync(erp);
        var line = po.Lines.Single().PoLineRef;
        await erp.RecordVendorConfirmationAsync(
            po.PoNumber, new DateOnly(2026, 10, 5), RecordSource.Vendor, "V-P",
            [new VendorConfirmLine(line, 5m, null)], Guid.NewGuid().ToString("N"));
        await erp.CreateAsnAsync(
            "ASN-P1", po.PoNumber, RecordSource.Vendor, "V-P",
            [new AsnCarton("CTN-P1", "ITEM-P", 5, "LOT-P1")], Guid.NewGuid().ToString("N"));
        var shippedAt = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
        await LinkCaseAsync(dir, po.PoNumber,
        [
            new CaseEvent("e1", "CASE-P", shippedAt, RecordSource.P2pAgent,
                CaseEventKind.AsnRecorded, "ASN-P1 recorded.", null, "ASN", "ASN-P1", null, null, null),
        ]);

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.Invoice, inbox.NextAction);
        Assert.Equal(2, inbox.SentSteps.Count);
        Assert.Contains(inbox.SentSteps, s => s.Step == "Shipment notice" && s.Detail.Contains("2026-10-06"));
        var markup = RenderPortal(service, "V-P");
        Assert.Contains(">Send an invoice<", markup);
        Assert.DoesNotContain(">Acknowledge<", markup);
        Assert.DoesNotContain(">Send a shipment notice<", markup);
    }

    [Fact]
    public async Task InvoicedPo_ShowsDoneWithExtraSends()
    {
        var (service, erp, dir) = Create();
        var po = await SeedApprovedPoAsync(erp);
        var line = po.Lines.Single().PoLineRef;
        await erp.RecordVendorConfirmationAsync(
            po.PoNumber, new DateOnly(2026, 10, 5), RecordSource.Vendor, "V-P",
            [new VendorConfirmLine(line, 5m, null)], Guid.NewGuid().ToString("N"));
        await erp.CreateAsnAsync(
            "ASN-P1", po.PoNumber, RecordSource.Vendor, "V-P",
            [new AsnCarton("CTN-P1", "ITEM-P", 5, "LOT-P1")], Guid.NewGuid().ToString("N"));
        await erp.CreateInvoiceAsync(
            "INV-P1", "V-P", po.PoNumber, RecordSource.Vendor, "V-P",
            [new InvoiceLineInput(line, 5m, 10m)], Guid.NewGuid().ToString("N"));
        await LinkCaseAsync(dir, po.PoNumber);

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.Done, inbox.NextAction);
        Assert.Equal(3, inbox.SentSteps.Count);
        var options = OptionsWith(new VendorDto(
            "V-P", "Portal Company Ltd", "US", false, null, "NET0", "ap@portal.test"));
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        using var page = ctx.RenderComponent<SupplierPortal>(parameters => parameters
            .Add(p => p.VendorId, "V-P")
            .Add(p => p.Options, options));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Contains("This order is fully sent.", page.Markup);
        Assert.DoesNotContain(">Send a shipment notice<", page.Markup);
        Assert.DoesNotContain(">Send an invoice<", page.Markup);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Send another", StringComparison.Ordinal))
            .Click();
        Assert.Contains(">Send a shipment notice<", page.Markup);
        Assert.Contains(">Send an invoice<", page.Markup);
        Assert.DoesNotContain(">Acknowledge<", page.Markup);
    }

    [Fact]
    public async Task DraftPo_WaitsForApproval()
    {
        var (service, erp, dir) = Create();
        await SeedPoAsync(erp);
        var po = (await erp.GetPurchaseOrdersByVendorAsync("V-P")).Single();
        await LinkCaseAsync(dir, po.PoNumber);

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.WaitApproval, inbox.NextAction);
        var markup = RenderPortal(service, "V-P");
        Assert.Contains("Waiting on buyer approval", markup);
        Assert.DoesNotContain(">Acknowledge<", markup);
        Assert.DoesNotContain(">Send a shipment notice<", markup);
        Assert.DoesNotContain(">Send an invoice<", markup);
    }

    [Fact]
    public async Task PoWithoutCase_WaitsForCase()
    {
        var (service, erp, _) = Create();
        var po = await SeedApprovedPoAsync(erp);

        var inbox = await service.GetSupplierInboxForPoAsync(po.PoNumber);

        Assert.NotNull(inbox);
        Assert.Equal(SupplierNextAction.WaitCase, inbox.NextAction);
        var markup = RenderPortal(service, "V-P");
        Assert.Contains("No case links", markup);
        Assert.DoesNotContain(">Acknowledge<", markup);
    }
}
