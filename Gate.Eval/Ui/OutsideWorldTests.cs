// Outside-world service tests: the supplier ASN post wires to its
// endpoint path with the runner input shape, the inbox prefill derives
// from the runtime PO plus scenario cartons, options carry carton details
// plus the scenario order line, outcome text sanitizes endpoint paths to
// friendly flow names, and no .razor shows an endpoint name. All sources
// are temp (scenario, manifest, fake ERP, temp cache, temp dock dir —
// never the demo numbers). Paths arrive via in-memory configuration, so
// these tests never touch GATE_MANIFEST / GATE_SCENARIO. No live calls.
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

    private const string ScenarioJson = """
        {"name":"ui-outside","currency":"TST",
         "salesOrder":{"item":"ITEM-1","quantity":12},
         "items":[{"id":"ITEM-1","unit":"EA"}],
         "vendors":[{"id":"V-1","item":"ITEM-1"}],
         "cartons":[{"id":"CTN-A","part":"ITEM-1","units":6},{"id":"CTN-B"}]}
        """;

    private const string ManifestJson = """
        {"scenario":"ui-outside","documents":[
         {"event":"salesOrder","docType":"SO","number":"SO-7"},
         {"event":"openPo.po","docType":"PO","number":"PO-7"}]}
        """;

    private static Harness Create(
        string? scenarioJson = ScenarioJson, string? manifestJson = ManifestJson)
    {
        var dir = UiTestSupport.NewDir("gate-ui-outside");
        var scenarioPath = Path.Combine(dir, "scenario.json");
        var manifestPath = Path.Combine(dir, "run-manifest.json");
        Directory.CreateDirectory(dir);
        if (scenarioJson is not null)
        {
            File.WriteAllText(scenarioPath, scenarioJson);
        }

        if (manifestJson is not null)
        {
            File.WriteAllText(manifestPath, manifestJson);
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = scenarioPath,
                ["Scenario:Manifest"] = manifestPath,
            })
            .Build();
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-1", "Mail Vendor", "US", false, null, "NET0", "vendor@test.example"));
        erp.SeedVendorPrice("V-1", "ITEM-1", 10m);
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
            config, UiTestLogger<SimulationUiService>.Instance);
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

    private static async Task WriteCaseAsync(Harness h, string? poNumber)
    {
        var now = DateTimeOffset.UtcNow;
        await new CaseCacheStore(h.CacheDir).WriteAsync(new CaseFile(
            new Case("CASE-1", "SO-7", "ITEM-1", "ui-outside", CaseStage.PoPlaced, now, now,
                "PR-1", poNumber, null, null, [], [], null, null),
            [], []));
    }

    [Fact]
    public async Task OptionsCarryCartonDetailsAndOrderLine()
    {
        var h = Create();

        var options = await h.Service.GetOptionsAsync();

        Assert.Equal(["CTN-A", "CTN-B"], options.CartonIds);
        Assert.Equal(2, options.CartonDetails.Count);
        Assert.Equal(new CartonOption("CTN-A", "ITEM-1", 6), options.CartonDetails[0]);
        Assert.Equal(new CartonOption("CTN-B", string.Empty, 0), options.CartonDetails[1]);
        Assert.Equal("ITEM-1", options.SalesOrderItem);
        Assert.Equal(12m, options.SalesOrderQuantity);
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

    [Theory]
    [InlineData(null, "ASN-1", "PACK-1", "Case")]
    [InlineData("CASE-1", null, "PACK-1", "ASN number")]
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
    public async Task InboxPrefillDerivesFromRuntimePoAndCartons()
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
        Assert.Equal($"ASN-{poNumber}", inbox.AsnPrefill.AsnNumber);
        Assert.Equal($"PACK-{poNumber}", inbox.AsnPrefill.PackingSlipNumber);
        Assert.Equal(2, inbox.AsnPrefill.Cartons!.Count);
        Assert.Equal($"{poNumber}-CTN-A", inbox.AsnPrefill.Cartons[0].LotRef);
        Assert.Equal(6, inbox.AsnPrefill.Cartons[0].UnitsPerCarton);
        Assert.Equal("ITEM-1", inbox.AsnPrefill.Cartons[1].ItemId);

        Assert.NotNull(inbox.InvoicePrefill);
        Assert.Equal($"INV-{poNumber}", inbox.InvoicePrefill.Number);
        Assert.Equal("V-1", inbox.InvoicePrefill.VendorId);
        Assert.Equal(poNumber, inbox.InvoicePrefill.PoNumber);
        var invLine = Assert.Single(inbox.InvoicePrefill.Lines);
        Assert.Equal(poLineRef, invLine.PoLineRef);
        Assert.Equal(5m, invLine.Quantity);
        Assert.Equal(10m, invLine.UnitPrice);
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
        Assert.NotNull(inbox.AsnPrefill);
        Assert.Equal("LOT-CTN-A", inbox.AsnPrefill.Cartons![0].LotRef);
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
