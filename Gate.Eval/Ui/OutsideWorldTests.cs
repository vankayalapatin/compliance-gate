// Outside-world service tests: document-driven portal lists (POs by
// vendor, expected trucks), the ACK plus ASN posts wire to their endpoint
// paths with the runner input shapes, the inbox prefill derives from the
// runtime PO lines plus item pack sizes with supplier numbers suggested
// from the vendor's own series (blank ASN mints from our sequence),
// the new shop order mints a shop id and resolves the ERP-minted
// SO, outcome text sanitizes endpoint paths to friendly flow names, and
// no .razor shows an endpoint name. All sources are temp (fake ERP, temp
// cache, temp dock dir — never company-data numbers). No live calls.
using System.Net;
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class OutsideWorldTests
{
    private static readonly JsonSerializerOptions WebOpts = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public HttpResponseMessage Response =
            new(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Response;
        }
    }

    private sealed record Harness(
        SimulationUiService Service, StubHandler Http, string CacheDir, InMemoryErpAdapter Erp);

    private static Harness Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-outside");
        Directory.CreateDirectory(dir);
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-1", "Mail Vendor", "US", false, null, "NET0", "vendor@test.example"));
        erp.SeedVendorPrice("V-1", "ITEM-1", 10m);
        erp.SeedItem(new ItemDto("ITEM-1", "Test item", false, 6, "EA"));
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));

        var cacheDir = Path.Combine(dir, "case-cache");
        var dockDir = Path.Combine(dir, "dock");
        Directory.CreateDirectory(dockDir);
        var stub = new StubHandler();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        var service = new SimulationUiService(
            http, erp, new CaseCacheStore(cacheDir),
            new DockPhotoService(dockDir, Path.Combine(dir, "vision")),
            UiTestLogger<SimulationUiService>.Instance);
        return new Harness(service, stub, cacheDir, erp);
    }

    private static void Respond(Harness h, string json) =>
        h.Http.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static T SentAs<T>(Harness h) =>
        JsonSerializer.Deserialize<T>(h.Http.LastBody ?? string.Empty, WebOpts)
        ?? throw new InvalidOperationException($"POST body did not parse as {typeof(T).Name}.");

    private static async Task<(string PoNumber, string PoLineRef)> SeedPoAsync(Harness h)
    {
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await h.Erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await h.Erp.CreatePoAsync(
            pr.PrNumber, "V-1", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));
        return (po.PoNumber, po.Lines.Single().PoLineRef);
    }

    private static async Task ApprovePoAsync(Harness h, string poNumber)
    {
        await h.Erp.SubmitForApprovalAsync("PO", poNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await h.Erp.RecordApprovalAsync("PO", poNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
    }

    private static async Task WriteCaseAsync(Harness h, string? poNumber)
    {
        var now = DateTimeOffset.UtcNow;
        await new CaseCacheStore(h.CacheDir).WriteAsync(new CaseFile(
            new Case("CASE-1", "SO-7", "ITEM-1", "LE-T", CaseStage.PoPlaced, now, now,
                "PR-1", poNumber, null, null, [], [], null, null),
            [], []));
    }

    [Fact]
    public async Task OptionsCarryErpItemsAndPricedVendorCandidates()
    {
        var h = Create();

        var options = await h.Service.GetOptionsAsync();

        var item = Assert.Single(options.Items);
        Assert.Equal("ITEM-1", item.ItemId);
        Assert.Equal("EA", item.Unit);
        Assert.Equal(["V-1"], options.CandidateVendorsByItem["ITEM-1"]);
    }

    [Fact]
    public async Task SendAsnPostsAsnShapeAndParsesReceipt()
    {
        var h = Create();
        Respond(h, """{"asnNumber":"ASN-1","receiptNumber":"RCP-1","step":"Docked","complete":false}""");

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            "CASE-1", "ASN-1", "PACK-1",
            [new AsnCartonForm("CTN-A", "ITEM-1", 6, "LOT-A")]));

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(HttpMethod.Post, h.Http.LastRequest!.Method);
        Assert.Equal(SimulationUiService.AsnPath, h.Http.LastRequest.RequestUri!.AbsolutePath);
        var sent = SentAs<SupplierAsnInput>(h);
        Assert.Equal("CASE-1", sent.CaseId);
        Assert.Equal("ASN-1", sent.AsnNumber);
        Assert.Equal("PACK-1", sent.SupplierPackingSlipNumber);
        var carton = Assert.Single(sent.Cartons);
        Assert.Equal("CTN-A", carton.CartonId);
        Assert.Equal("ITEM-1", carton.ItemId);
        Assert.Equal(6, carton.UnitsPerCarton);
        Assert.Equal("LOT-A", carton.LotRef);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("ASN-1", outcome.Summary);
        Assert.Contains(outcome.Details, d => d.Contains("RCP-1"));
    }

    [Fact]
    public async Task SendAsnAcceptsBlankNumberForSequenceMint()
    {
        var h = Create();
        Respond(h, """{"asnNumber":"ASN-9","receiptNumber":"RCP-9","step":"Docked","complete":false}""");

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            "CASE-1", string.Empty, "PACK-1",
            [new AsnCartonForm("CTN-A", "ITEM-1", 6, "LOT-A")]));

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(string.Empty, SentAs<SupplierAsnInput>(h).AsnNumber);
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("ASN-9", outcome.Summary);
    }

    [Theory]
    [InlineData(null, "ASN-1", "PACK-1", "Case")]
    [InlineData("CASE-1", "ASN-1", null, "Packing-slip")]
    public async Task SendAsnRefusesBlanks(string? caseId, string? asn, string? slip, string fragment)
    {
        var h = Create();

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            caseId!, asn!, slip!, [new AsnCartonForm("CTN-A", "ITEM-1", 6, "LOT-A")]));

        Assert.False(outcome.Ok);
        Assert.Contains(fragment, outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendAsnRefusesNullAndEmptyCartons()
    {
        var h = Create();

        Assert.False((await h.Service.SendAsnAsync(null)).Ok);
        var outcome = await h.Service.SendAsnAsync(new AsnSubmission("CASE-1", "ASN-1", "PACK-1", []));
        Assert.False(outcome.Ok);
        Assert.Contains("carton", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Theory]
    [InlineData("", "ITEM-1", 6, "LOT-A", "carton id")]
    [InlineData("CTN-A", "", 6, "LOT-A", "item")]
    [InlineData("CTN-A", "ITEM-1", 0, "LOT-A", "units per carton")]
    [InlineData("CTN-A", "ITEM-1", 6, "", "lot ref")]
    public async Task SendAsnRefusesBadCartons(
        string cartonId, string item, int units, string lot, string fragment)
    {
        var h = Create();

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            "CASE-1", "ASN-1", "PACK-1", [new AsnCartonForm(cartonId, item, units, lot)]));

        Assert.False(outcome.Ok);
        Assert.Contains(fragment, outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task InboxPrefillDerivesFromRuntimePoAndPackSizes()
    {
        var h = Create();
        var (poNumber, poLineRef) = await SeedPoAsync(h);
        await WriteCaseAsync(h, poNumber);

        var inbox = await h.Service.GetSupplierInboxAsync("CASE-1");

        Assert.NotNull(inbox);
        Assert.NotNull(inbox.Po);
        Assert.Equal(poNumber, inbox.Po.PoNumber);
        Assert.Equal("V-1", inbox.Po.VendorId);
        var line = Assert.Single(inbox.Po.Lines);
        Assert.Equal(poLineRef, line.PoLineRef);
        Assert.Equal(5m * 10m, inbox.Po.Total);

        Assert.NotNull(inbox.AsnPrefill);
        Assert.Equal(string.Empty, inbox.AsnPrefill.AsnNumber);
        Assert.Equal(string.Empty, inbox.AsnPrefill.PackingSlipNumber);
        var carton = Assert.Single(inbox.AsnPrefill.Cartons!);
        Assert.Equal("CTN-01", carton.CartonId);
        Assert.Equal("ITEM-1", carton.ItemId);
        Assert.Equal(5, carton.UnitsPerCarton);
        Assert.Equal("LOT-001", carton.LotRef);

        Assert.NotNull(inbox.InvoicePrefill);
        Assert.Equal(string.Empty, inbox.InvoicePrefill.Number);
        Assert.Equal("V-1", inbox.InvoicePrefill.VendorId);
        Assert.Equal(poNumber, inbox.InvoicePrefill.PoNumber);
        var invLine = Assert.Single(inbox.InvoicePrefill.Lines);
        Assert.Equal(poLineRef, invLine.PoLineRef);
        Assert.Equal(5m, invLine.Quantity);
        Assert.Equal(10m, invLine.UnitPrice);
    }

    [Fact]
    public async Task InboxForPoResolvesCaseAndShipPlan()
    {
        var h = Create();
        var (poNumber, _) = await SeedPoAsync(h);
        await WriteCaseAsync(h, poNumber);

        var inbox = await h.Service.GetSupplierInboxForPoAsync(poNumber);

        Assert.NotNull(inbox);
        Assert.Equal("CASE-1", inbox.CaseId);
        Assert.NotNull(inbox.Po);
        Assert.Equal(poNumber, inbox.Po.PoNumber);
        Assert.NotNull(inbox.AsnPrefill);
        Assert.Equal("CTN-01", inbox.AsnPrefill.Cartons!.Single().CartonId);
        Assert.NotNull(inbox.InvoicePrefill);
    }

    [Fact]
    public async Task InboxForMissingPoReadsNull()
    {
        var h = Create();

        Assert.Null(await h.Service.GetSupplierInboxForPoAsync("PO-MISSING"));
    }

    [Fact]
    public void ShipPlanSplitsLinesByPackSize()
    {
        var notes = new List<string>();
        var cartons = OutsideShipPlan.Build(
            [new InboxPoLineVm("PO-L1", "ITEM-1", 12m, 10m, 120m)],
            _ => 5,
            notes);

        Assert.Equal(3, cartons.Count);
        Assert.Equal("CTN-01", cartons[0].CartonId);
        Assert.Equal(5, cartons[0].UnitsPerCarton);
        Assert.Equal("LOT-001", cartons[0].LotRef);
        Assert.Equal("CTN-03", cartons[2].CartonId);
        Assert.Equal(2, cartons[2].UnitsPerCarton);
        Assert.Equal("LOT-003", cartons[2].LotRef);
        Assert.Equal(12, cartons.Sum(c => c.UnitsPerCarton));
        Assert.Empty(notes);
    }

    [Fact]
    public void ShipPlanMissingPackSizeYieldsEditableRowPlusNote()
    {
        var notes = new List<string>();
        var cartons = OutsideShipPlan.Build(
            [
                new InboxPoLineVm("PO-L1", "ITEM-X", 4m, 10m, 40m),
                new InboxPoLineVm("PO-L2", "ITEM-1", 0m, 10m, 0m),
            ],
            item => item == "ITEM-1" ? 5 : null,
            notes);

        var fallback = Assert.Single(cartons);
        Assert.Equal("CTN-01", fallback.CartonId);
        Assert.Equal("LOT-001", fallback.LotRef);
        Assert.Equal(0, fallback.UnitsPerCarton);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public async Task SupplierPosListByVendor()
    {
        var h = Create();
        var (poNumber, _) = await SeedPoAsync(h);

        var listed = await h.Service.ListSupplierPosAsync("V-1");
        var empty = await h.Service.ListSupplierPosAsync("V-NOPE");

        Assert.Contains(listed.Pos, p => p.PoNumber == poNumber);
        Assert.Empty(listed.Notes);
        Assert.Empty(empty.Pos);
    }

    [Fact]
    public async Task SupplierPosListNewestActionableFirst()
    {
        var h = Create();
        var (poA, _) = await SeedPoAsync(h);
        var (poB, _) = await SeedPoAsync(h);
        var (poC, _) = await SeedPoAsync(h);
        await ApprovePoAsync(h, poA);
        await ApprovePoAsync(h, poB);
        await ApprovePoAsync(h, poC);
        await h.Erp.ClosePoAsync(poC, "requester", Guid.NewGuid().ToString("N"));

        var listed = await h.Service.ListSupplierPosAsync("V-1");

        Assert.Equal([poB, poA, poC], listed.Pos.Select(p => p.PoNumber));
    }

    [Fact]
    public async Task SupplierPosListAwaitingApprovalAfterActionable()
    {
        var h = Create();
        var (poA, _) = await SeedPoAsync(h);
        var (poB, _) = await SeedPoAsync(h);
        await ApprovePoAsync(h, poA);

        var listed = await h.Service.ListSupplierPosAsync("V-1");

        Assert.Equal([poA, poB], listed.Pos.Select(p => p.PoNumber));
    }

    [Fact]
    public async Task InboxPrefillSuggestsVendorSeriesNumbers()
    {
        var h = Create();
        var (poA, lineA) = await SeedPoAsync(h);
        await ApprovePoAsync(h, poA);
        await h.Erp.RecordVendorConfirmationAsync(poA, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.Vendor, "vendor", [new VendorConfirmLine(lineA, 5m, null)],
            Guid.NewGuid().ToString("N"));
        await h.Erp.PostReceiptAsync(poA, "TS-PS-20240506", RecordSource.Vendor, "vendor",
            [new ReceiptLine(lineA, 5m, 0m, 0m, "LOT-T", null)], Guid.NewGuid().ToString("N"));
        await h.Erp.CreateInvoiceAsync("TS-2024-05-06", "V-1", poA, RecordSource.Vendor, "vendor",
            [new InvoiceLineInput(lineA, 5m, 10m)], Guid.NewGuid().ToString("N"));
        var (poB, _) = await SeedPoAsync(h);
        await WriteCaseAsync(h, poB);

        var inbox = await h.Service.GetSupplierInboxForPoAsync(poB);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        Assert.NotNull(inbox);
        Assert.NotNull(inbox.AsnPrefill);
        Assert.Equal(string.Empty, inbox.AsnPrefill.AsnNumber);
        Assert.Equal($"TS-PS-{today:yyyyMMdd}", inbox.AsnPrefill.PackingSlipNumber);
        Assert.NotNull(inbox.InvoicePrefill);
        Assert.Equal($"TS-{today:yyyy-MM-dd}", inbox.InvoicePrefill.Number);
    }

    [Fact]
    public async Task InboxPrefillLeavesUnknownSeriesBlank()
    {
        var h = Create();
        var (poA, lineA) = await SeedPoAsync(h);
        await h.Erp.CreateInvoiceAsync("NO-DATE-SERIES", "V-1", poA, RecordSource.Vendor, "vendor",
            [new InvoiceLineInput(lineA, 5m, 10m)], Guid.NewGuid().ToString("N"));
        var (poB, _) = await SeedPoAsync(h);
        await WriteCaseAsync(h, poB);

        var inbox = await h.Service.GetSupplierInboxForPoAsync(poB);

        Assert.NotNull(inbox);
        Assert.NotNull(inbox.InvoicePrefill);
        Assert.Equal(string.Empty, inbox.InvoicePrefill.Number);
        Assert.NotNull(inbox.AsnPrefill);
        Assert.Equal(string.Empty, inbox.AsnPrefill.PackingSlipNumber);
    }

    [Fact]
    public async Task ExpectedTrucksLinkAsnToCase()
    {
        var h = Create();
        var (poNumber, _) = await SeedPoAsync(h);
        await WriteCaseAsync(h, poNumber);
        await h.Erp.CreateAsnAsync(
            "ASN-7", poNumber, RecordSource.Vendor, "V-1",
            [new AsnCarton("CTN-7", "ITEM-1", 5, "LOT-7")], Guid.NewGuid().ToString("N"));

        var trucks = await h.Service.ListExpectedTrucksAsync();

        var truck = Assert.Single(trucks.Trucks);
        Assert.Equal("ASN-7", truck.AsnNumber);
        Assert.Equal(poNumber, truck.PoNumber);
        Assert.Equal("V-1", truck.VendorId);
        Assert.Equal("CASE-1", truck.CaseId);
        Assert.Equal("CTN-7", Assert.Single(truck.Cartons).CartonId);
    }

    [Fact]
    public async Task SendAckPostsAckShapeAndParsesPo()
    {
        var h = Create();
        Respond(h, """{"poNumber":"PO-7","step":"Acknowledged","complete":false}""");

        var outcome = await h.Service.SendAckAsync("CASE-1");

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.AckPath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<SupplierAckInput>(h);
        Assert.Equal("CASE-1", sent.CaseId);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("PO-7", outcome.Summary);
    }

    [Fact]
    public async Task SendAckRefusesBlankCase()
    {
        var h = Create();

        var outcome = await h.Service.SendAckAsync("  ");

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendNewShopOrderMintsShopIdAndResolvesSo()
    {
        var h = Create();
        Respond(h, """{"caseIds":["CASE-1"],"advances":[]}""");
        await WriteCaseAsync(h, "PO-7");

        var placed = await h.Service.SendNewShopOrderAsync(new NewShopOrder(
            "LE-T", null, null, [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.True(placed.Outcome.Ok, placed.Outcome.Summary);
        Assert.StartsWith("SHOP-", placed.ShopOrderId);
        var sent = SentAs<OrderWebhookInput>(h);
        Assert.Equal(placed.ShopOrderId, sent.SoNumber);
        Assert.Equal(["SO-7"], placed.MintedSoNumbers);
    }

    [Fact]
    public async Task SendNewShopOrderRefusesNullAndEmptyLines()
    {
        var h = Create();

        var nullPlaced = await h.Service.SendNewShopOrderAsync(null);
        var emptyPlaced = await h.Service.SendNewShopOrderAsync(
            new NewShopOrder("LE-T", null, null, [], "TST"));

        Assert.False(nullPlaced.Outcome.Ok);
        Assert.False(emptyPlaced.Outcome.Ok);
        Assert.Contains("line", emptyPlaced.Outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task InboxWithoutPoStillReplies()
    {
        var h = Create();
        await WriteCaseAsync(h, null);

        var inbox = await h.Service.GetSupplierInboxAsync("CASE-1");

        Assert.NotNull(inbox);
        Assert.Null(inbox.Po);
        Assert.Null(inbox.InvoicePrefill);
        Assert.Null(inbox.AsnPrefill);
        Assert.NotEmpty(inbox.Notes);
    }

    [Fact]
    public async Task InboxMissingCaseReadsNull()
    {
        var h = Create();

        Assert.Null(await h.Service.GetSupplierInboxAsync("CASE-MISSING"));
    }

    [Fact]
    public void SanitizeReplacesEveryEndpointPath()
    {
        Assert.Equal(
            "customer order failed.",
            OutsideWorldLabels.Sanitize("POST /simulation/shopify/order failed."));
        Assert.Contains(
            "supplier shipment notice",
            OutsideWorldLabels.Sanitize($"POST {SimulationUiService.AsnPath} returned 400."));
        Assert.Contains(
            OutsideWorldLabels.AckFlow,
            OutsideWorldLabels.Sanitize($"POST {SimulationUiService.AckPath} returned 400."));
        Assert.Contains(
            "dock photos",
            OutsideWorldLabels.Sanitize($"POST {SimulationUiService.DockPhotosPath} failed."));
        Assert.Contains(
            "supplier invoice",
            OutsideWorldLabels.Sanitize($"POST {SimulationUiService.InvoicePath} failed."));
        Assert.Contains(
            "vendor reply",
            OutsideWorldLabels.Sanitize($"POST {SimulationUiService.EmailReplyPath} failed."));
        Assert.True(OutsideWorldLabels.IsClean(OutsideWorldLabels.Sanitize(
            $"POST {SimulationUiService.InvoicePhotoPath} and {SimulationUiService.ReshotPath} failed.")));

        var outcome = new SimulationOutcome(
            false, $"POST {SimulationUiService.DockPhotosPath} failed.",
            [$"body from {SimulationUiService.DockPhotosPath}"], []);
        var shown = OutsideWorldLabels.Sanitized(outcome);
        Assert.True(OutsideWorldLabels.IsClean(shown.Summary));
        Assert.All(shown.Details, d => Assert.True(OutsideWorldLabels.IsClean(d)));
    }

    [Fact]
    public void RazorComponents_MustNotShowEndpointNames()
    {
        var root = FindRepoRoot();
        var offenders = Directory
            .GetFiles(Path.Combine(root, "Gate.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("/simulation/", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "UI text never names endpoints — the friendly flow name shows instead. "
            + $"Offenders: {(offenders.Count == 0 ? "none" : string.Join(", ", offenders))}.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Gate.Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (no directory containing Gate.Web found above "
            + AppContext.BaseDirectory + ").");
    }
}
