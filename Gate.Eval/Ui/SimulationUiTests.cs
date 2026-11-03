// Simulate-surface service tests: options come from runtime sources only
// (fake ERP, temp case dir, temp dock dir — never company-data numbers),
// every POST wires to the existing simulation endpoint path with the
// endpoint's input shape, successes parse, and every refusal and failure
// surfaces with context. No live calls.
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

public sealed class SimulationUiTests
{
    private static readonly JsonSerializerOptions WebOpts = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public int FailuresBeforeSuccess;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public readonly List<string?> Bodies = [];
        public HttpResponseMessage Response =
            new(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Bodies.Add(LastBody);
            if (CallCount <= FailuresBeforeSuccess)
            {
                throw Throw ?? new HttpRequestException("Boom.");
            }

            return Response;
        }
    }

    private sealed record Harness(
        SimulationUiService Service, StubHandler Http, string CacheDir, InMemoryErpAdapter Erp);

    private static Harness Create(bool seedErp = true)
    {
        var dir = UiTestSupport.NewDir("gate-ui-sim");
        Directory.CreateDirectory(dir);
        var erp = new InMemoryErpAdapter();
        if (seedErp)
        {
            erp.SeedBaseCurrency("TST");
            erp.SeedAutoApproveThreshold(10m);
            erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
            erp.SeedItem(new ItemDto("ITEM-1", "Test item 1", false, 10, "EA"));
            erp.SeedItem(new ItemDto("ITEM-2", "Test item 2", false, 5, "EA"));
            erp.SeedVendor(new VendorDto("V-2", "No Mail Vendor", "US", false, null, "NET0"));
            erp.SeedVendor(new VendorDto("V-1", "Mail Vendor", "US", false, null, "NET0", "vendor@test.example"));
            erp.SeedVendorPrice("V-1", "ITEM-1", 10m);
            erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
            erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        }

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

    private static void Respond(Harness h, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        h.Http.Response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static T SentAs<T>(Harness h) =>
        JsonSerializer.Deserialize<T>(h.Http.LastBody ?? string.Empty, WebOpts)
        ?? throw new InvalidOperationException($"POST body did not parse as {typeof(T).Name}.");

    [Fact]
    public async Task OptionsComeFromErpAndCaseDir()
    {
        var h = Create();
        await File.WriteAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "dock", "IMG-T.jpg"), [1, 2, 3]);
        // Base originals are working files, never counter photos.
        await File.WriteAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "dock", "IMG-T-base.jpg"), [4, 5, 6]);
        // Unrecorded files (a prior lifetime's views) stay on disk but are
        // never offered.
        await File.WriteAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "dock", "STALE-T.jpg"), [7, 8, 9]);
        await new CaseCacheStore(h.CacheDir).WriteAsync(
            UiTestSupport.CaseFileWith("CASE-1", "SO-7", "ITEM-1"));
        await File.WriteAllTextAsync(
            Path.Combine(h.CacheDir, "CASE-1.dockviews.json"),
            """{"caseId":"CASE-1","asnNumber":"ASN-T","images":["IMG-T.jpg"],"updatedAt":"2026-11-03T00:00:00Z"}""");

        var options = await h.Service.GetOptionsAsync();

        // The fake serves no company info: entity and currency stay empty
        // with a note, never guessed.
        Assert.Empty(options.EntityCode);
        Assert.Empty(options.Currency);
        Assert.Contains(options.Notes, n => n.Contains("Company info"));
        Assert.Equal(2, options.Users.Count);
        Assert.Equal(new[] {"V-1", "V-2"}, options.Vendors.Select(v => v.VendorId).ToList());
        var kase = Assert.Single(options.Cases);
        Assert.Equal("CASE-1", kase.CaseId);
        Assert.Equal(new[] {"IMG-T.jpg"}, options.DockImages);
        Assert.Contains(
            options.Notes, n => n.Contains("were not built for the current cases", StringComparison.Ordinal));
        Assert.Equal(new[] {"ITEM-1", "ITEM-2"}, options.Items.Select(i => i.ItemId).ToList());
        Assert.Equal("EA", options.Items[0].Unit);
        // Candidates are the vendors carrying a price for the item: V-1
        // prices ITEM-1 only, and nothing prices ITEM-2.
        Assert.Equal(new[] {"V-1"}, options.CandidateVendorsByItem["ITEM-1"]);
        Assert.Empty(options.CandidateVendorsByItem["ITEM-2"]);
    }

    [Fact]
    public void DockCounterSplitsPalletViewsFromReshotCloseUp()
    {
        // Dock-camera modes: a first check offers pallet views, a reshot
        // offers the close-up only, and base originals appear in neither.
        var files = new[]
        {
            "IMG-01.jpg", "IMG-02.jpg", "IMG-03.jpg", "IMG-04.jpg",
            "IMG-01-base.jpg", "IMG-02-base.jpg", "IMG-03-base.jpg",
        };

        Assert.Equal(
            new[] { "IMG-01.jpg", "IMG-02.jpg", "IMG-03.jpg" },
            DockPhotoService.OfferedCounterImages(files, reshot: false));
        Assert.Equal(
            new[] { "IMG-04.jpg" },
            DockPhotoService.OfferedCounterImages(files, reshot: true));
    }

    [Fact]
    public async Task OptionsCarryCompanyInfoWhenServed()
    {
        var h = Create();
        var company = new CompanyInfoDto(
            "LE-T", "Test Company Inc", "TST", "1 Bill St", "2 Ship St", "buy@test.example");
        var service = new SimulationUiService(
            new HttpClient(h.Http) { BaseAddress = new Uri("http://localhost/") },
            new CompanyInfoErpDouble(h.Erp, company),
            new CaseCacheStore(h.CacheDir),
            new DockPhotoService(
                Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "dock"),
                Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "vision")),
            UiTestLogger<SimulationUiService>.Instance);

        var options = await service.GetOptionsAsync();

        Assert.Equal("LE-T", options.EntityCode);
        Assert.Equal("TST", options.Currency);
        Assert.DoesNotContain(options.Notes, n => n.Contains("Company info"));
    }

    [Fact]
    public async Task EmptyErpYieldsEmptyOptionsWithNotes()
    {
        var h = Create(seedErp: false);
        await new CaseCacheStore(h.CacheDir).WriteAsync(
            UiTestSupport.CaseFileWith("CASE-1", "SO-7", "ITEM-1"));

        var options = await h.Service.GetOptionsAsync();

        Assert.Empty(options.EntityCode);
        Assert.Empty(options.Users);
        Assert.Empty(options.Vendors);
        Assert.Empty(options.Items);
        Assert.Empty(options.CandidateVendorsByItem);
        Assert.NotEmpty(options.Notes);
        Assert.Single(options.Cases);
    }

    [Fact]
    public async Task SendOrderPostsWebhookShapeAndParsesAcceptance()
    {
        var h = Create();
        Respond(h, """{"accepted":true,"caseIds":["CASE-1"]}""");

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-7", "requester",
            new Dictionary<string, IReadOnlyList<string>> { ["ITEM-1"] = ["V-1"] },
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(HttpMethod.Post, h.Http.LastRequest!.Method);
        Assert.Equal(SimulationUiService.OrderPath, h.Http.LastRequest.RequestUri!.AbsolutePath);
        var sent = SentAs<OrderWebhookInput>(h);
        Assert.Equal("LE-T", sent.ScenarioName);
        Assert.Equal("SHOP-7", sent.SoNumber);
        Assert.Equal("requester", sent.Requester);
        Assert.Equal(new[] {"V-1"}, sent.CandidateVendorsByItem!["ITEM-1"]);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(new[] {"CASE-1"}, outcome.CaseIds);
        Assert.Contains(outcome.Details, d => d.Contains("CASE-1") && d.Contains("opened"));
    }

    [Fact]
    public async Task SendOrderWithLinesCarriesCurrency()
    {
        var h = Create();
        Respond(h, """{"caseIds":[],"advances":[]}""");

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-9", null, null,
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.True(outcome.Ok, outcome.Summary);
        var sent = SentAs<OrderWebhookInput>(h);
        Assert.Equal("SHOP-9", sent.SoNumber);
        Assert.Equal("TST", sent.Currency);
        Assert.Equal("ITEM-1", Assert.Single(sent.Lines!).ItemId);
    }

    [Fact]
    public async Task SendOrderRefusesBlankShopId()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "  ", "requester", null,
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.False(outcome.Ok);
        Assert.Contains("Shop order id", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesWithoutLines()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-9", null, null, null, "TST"));

        Assert.False(outcome.Ok);
        Assert.Contains("order line", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesNullSubmission()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(null!);

        Assert.False(outcome.Ok);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesBlankEntity()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "  ", "SHOP-9", null, null,
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.False(outcome.Ok);
        Assert.Contains("Company code", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesLineWithoutItem()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-9", null, null, [new OrderLineForm("", 5m)], "TST"));

        Assert.False(outcome.Ok);
        Assert.Contains("item", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesLineWithoutQuantity()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-9", null, null, [new OrderLineForm("ITEM-1", 0m)], "TST"));

        Assert.False(outcome.Ok);
        Assert.Contains("quantity", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendOrderRefusesLinesWithoutCurrency()
    {
        var h = Create();

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-9", null, null, [new OrderLineForm("ITEM-1", 5m)], null));

        Assert.False(outcome.Ok);
        Assert.Contains("currency", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendDockPhotosPostsBytesAndParsesReceipt()
    {
        var h = Create();
        Respond(h, """{"accepted":true,"photoCount":1}""");

        var outcome = await h.Service.SendDockPhotosAsync(
            "CASE-1", [new SimulationPhoto("IMG-T.jpg", [7, 8, 9], "CTN-A")]);

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.DockPhotosPath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<DockPhotosRequest>(h);
        Assert.Equal("CASE-1", sent.CaseId);
        var photo = Assert.Single(sent.Photos);
        Assert.Equal("IMG-T.jpg", photo.ImageId);
        Assert.Equal(new byte[] {7, 8, 9}, photo.Jpeg);
        Assert.Equal("CTN-A", photo.CartonHint);
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("1 photo(s)", outcome.Summary);
        Assert.Contains("lands below", outcome.Summary);
    }

    [Fact]
    public async Task SendDockPhotosRefusesWithoutPhotos()
    {
        var h = Create();

        var outcome = await h.Service.SendDockPhotosAsync("CASE-1", []);

        Assert.False(outcome.Ok);
        Assert.Contains("photo", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendDockPhotosRefusesWithoutCase()
    {
        var h = Create();

        var outcome = await h.Service.SendDockPhotosAsync(
            null, [new SimulationPhoto("IMG-T.jpg", [1], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendDockPhotosRefusesEmptyBytes()
    {
        var h = Create();

        var outcome = await h.Service.SendDockPhotosAsync(
            "CASE-1", [new SimulationPhoto("IMG-T.jpg", [], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains("empty", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendReshotPostsCartonsAndParsesReceipt()
    {
        var h = Create();
        Respond(h, """{"accepted":true,"photoCount":1}""");

        var outcome = await h.Service.SendReshotAsync(
            "CASE-1", ["CTN-B"], [new SimulationPhoto("IMG-R.jpg", [4, 5], "CTN-B")]);

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.ReshotPath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<ReshotPhotosRequest>(h);
        Assert.Equal(new[] {"CTN-B"}, sent.CartonIds);
        Assert.Equal(new byte[] {4, 5}, Assert.Single(sent.Photos).Jpeg);
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("Reshot received: 1 photo(s)", outcome.Summary);
        Assert.Contains("lands below", outcome.Summary);
    }

    [Fact]
    public async Task SendReshotRefusesWithoutCase()
    {
        var h = Create();

        var outcome = await h.Service.SendReshotAsync(
            null, ["CTN-A"], [new SimulationPhoto("IMG-R.jpg", [4], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendReshotRefusesWithoutCartons()
    {
        var h = Create();

        var outcome = await h.Service.SendReshotAsync(
            "CASE-1", [], [new SimulationPhoto("IMG-R.jpg", [4], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains("carton", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoicePostsTypedLinesAndParsesValues()
    {
        var h = Create();
        Respond(h, """
            {"invoice":"V-1/INV-1","persisted":true,"acceptedValue":100,"heldValue":0,
             "step":"Match","complete":true}
            """);

        var outcome = await h.Service.SendInvoiceAsync(
            "CASE-1", "INV-1", "V-1", "PO-7",
            [new InvoiceLineForm("L1", 5m, 20m)], "BANK-1");

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.InvoicePath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<SupplierInvoiceInput>(h);
        Assert.Equal("INV-1", sent.SupplierInvoiceNumber);
        Assert.Equal("V-1", sent.VendorId);
        Assert.Equal("PO-7", sent.PoNumber);
        Assert.Equal("BANK-1", sent.BankAccountRef);
        var line = Assert.Single(sent.Lines);
        Assert.Equal("L1", line.PoLineRef);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("V-1/INV-1", outcome.Summary);
        Assert.Contains(outcome.Details, d => d.Contains("100") && d.Contains("held value 0"));
    }

    [Fact]
    public async Task SendInvoiceAllowsBlankVendorForMissingFieldsPath()
    {
        var h = Create();
        Respond(h, """
            {"invoice":"/INV-2","persisted":true,"acceptedValue":0,"heldValue":50,
             "step":"Match","complete":false}
            """);

        var outcome = await h.Service.SendInvoiceAsync(
            "CASE-1", "INV-2", null, null, [new InvoiceLineForm(null, 5m, 10m)], null);

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(1, h.Http.CallCount);
        var sent = SentAs<SupplierInvoiceInput>(h);
        Assert.Null(sent.VendorId);
        Assert.Null(sent.PoNumber);
    }

    [Fact]
    public async Task SendInvoiceRefusesBlankNumber()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoiceAsync(
            "CASE-1", "   ", "V-1", "PO-7", [new InvoiceLineForm("L1", 1m, 1m)], null);

        Assert.False(outcome.Ok);
        Assert.Contains("number", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoiceRefusesWithoutCase()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoiceAsync(
            null, "INV-1", "V-1", "PO-7", [new InvoiceLineForm("L1", 1m, 1m)], null);

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoiceRefusesWithoutLines()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoiceAsync("CASE-1", "INV-1", "V-1", "PO-7", [], null);

        Assert.False(outcome.Ok);
        Assert.Contains("line", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoiceRefusesZeroQuantity()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoiceAsync(
            "CASE-1", "INV-1", "V-1", "PO-7", [new InvoiceLineForm("L1", 0m, 20m)], null);

        Assert.False(outcome.Ok);
        Assert.Contains("quantity", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoiceRefusesNegativePrice()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoiceAsync(
            "CASE-1", "INV-1", "V-1", "PO-7", [new InvoiceLineForm("L1", 1m, -1m)], null);

        Assert.False(outcome.Ok);
        Assert.Contains("price", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoicePhotosPostsPhotoShape()
    {
        var h = Create();
        Respond(h, """{"accepted":true,"photoCount":1}""");

        var outcome = await h.Service.SendInvoicePhotosAsync(
            "CASE-1", [new SimulationPhoto("PAGE-1.jpg", [9, 9], null)]);

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.InvoicePhotoPath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<InvoicePhotosRequest>(h);
        Assert.Equal("CASE-1", sent.CaseId);
        Assert.Equal(new byte[] {9, 9}, Assert.Single(sent.Photos).Jpeg);
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("Invoice photos received: 1 photo(s)", outcome.Summary);
    }

    [Fact]
    public async Task SendInvoicePhotosRefusesWithoutCase()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoicePhotosAsync(
            null, [new SimulationPhoto("PAGE-1.jpg", [9], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendInvoicePhotosRefusesWithoutPhotos()
    {
        var h = Create();

        var outcome = await h.Service.SendInvoicePhotosAsync("CASE-1", []);

        Assert.False(outcome.Ok);
        Assert.Contains("photo", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendVendorReplyPostsReplyShapeAndParsesMail()
    {
        var h = Create();
        Respond(h, """{"emailId":"E-9","step":"ReadReply","complete":false}""");

        var outcome = await h.Service.SendVendorReplyAsync(
            "CASE-1", "vendor@test.example", "Corrected quantity is 5.", "Re: query", "E-1");

        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(SimulationUiService.EmailReplyPath, h.Http.LastRequest!.RequestUri!.AbsolutePath);
        var sent = SentAs<VendorReplyInput>(h);
        Assert.Equal("CASE-1", sent.CaseId);
        Assert.Equal("vendor@test.example", sent.From);
        Assert.Equal("E-1", sent.InReplyTo);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Contains("E-9", outcome.Summary);
    }

    [Fact]
    public async Task SendVendorReplyRefusesWithoutCase()
    {
        var h = Create();

        var outcome = await h.Service.SendVendorReplyAsync(null, "v@t", "Hi.", null, null);

        Assert.False(outcome.Ok);
        Assert.Contains("Case", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendVendorReplyRefusesBlankBody()
    {
        var h = Create();

        var outcome = await h.Service.SendVendorReplyAsync(
            "CASE-1", "vendor@test.example", "  ", null, null);

        Assert.False(outcome.Ok);
        Assert.Contains("body", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task SendVendorReplyRefusesBlankFrom()
    {
        var h = Create();

        var outcome = await h.Service.SendVendorReplyAsync("CASE-1", null, "Hello.", null, null);

        Assert.False(outcome.Ok);
        Assert.Contains("From", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task FailureSurfacesStatusAndBody()
    {
        var h = Create();
        Respond(h, "Unknown case CASE-X.", HttpStatusCode.BadRequest);

        var outcome = await h.Service.SendDockPhotosAsync(
            "CASE-X", [new SimulationPhoto("IMG-T.jpg", [1], null)]);

        Assert.False(outcome.Ok);
        Assert.Contains(SimulationUiService.DockPhotosPath, outcome.Summary);
        Assert.Contains("400", outcome.Summary);
        Assert.Contains("Unknown case CASE-X.", string.Join("\n", outcome.Details));
    }

    [Fact]
    public async Task TransportErrorRetriesOnceThenSurfacesWithContext()
    {
        var h = Create();
        h.Http.FailuresBeforeSuccess = 10;
        h.Http.Throw = new HttpRequestException("No connection could be made.");

        var outcome = await h.Service.SendVendorReplyAsync("CASE-1", "v@t", "Hi.", null, null);

        Assert.False(outcome.Ok);
        Assert.Equal(2, h.Http.CallCount);
        Assert.Contains(SimulationUiService.EmailReplyPath, outcome.Summary);
        Assert.Contains("2 attempt(s)", outcome.Summary);
        Assert.Contains("No connection could be made.", outcome.Summary);
    }

    [Fact]
    public async Task RetryReusesTheSameIdempotentPayload()
    {
        var h = Create();
        h.Http.FailuresBeforeSuccess = 1;
        Respond(h, """{"emailId":"E-9","step":"ReadReply","complete":false}""");

        var outcome = await h.Service.SendVendorReplyAsync("CASE-1", "v@t", "Hi.", null, null);

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(2, h.Http.CallCount);
        Assert.Equal(2, h.Http.Bodies.Count);
        Assert.Equal(h.Http.Bodies[0], h.Http.Bodies[1]);
        var sent = SentAs<VendorReplyInput>(h);
        Assert.False(string.IsNullOrWhiteSpace(sent.IdempotencyKey));
    }

    [Fact]
    public async Task CallerCancellationNeverRetries()
    {
        var h = Create();
        h.Http.FailuresBeforeSuccess = 10;
        h.Http.Throw = new TaskCanceledException("Caller went away.");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await h.Service.SendOrderAsync(
            new OrderSubmission("LE-T", "SHOP-7", null, null,
                [new OrderLineForm("ITEM-1", 5m)], "TST"), cts.Token);

        Assert.False(outcome.Ok);
        Assert.Equal(1, h.Http.CallCount);
        Assert.Contains("1 attempt(s)", outcome.Summary);
    }

    [Fact]
    public async Task ClientTimeoutRetriesOnce()
    {
        var h = Create();
        h.Http.FailuresBeforeSuccess = 1;
        h.Http.Throw = new TaskCanceledException("Client timed out.");
        Respond(h, """{"caseIds":[],"advances":[]}""");

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-7", null, null,
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(2, h.Http.CallCount);
    }

    [Fact]
    public async Task UnexpectedShapeStillReportsSuccessWithBody()
    {
        var h = Create();
        Respond(h, "not json");

        var outcome = await h.Service.SendOrderAsync(new OrderSubmission(
            "LE-T", "SHOP-7", null, null,
            [new OrderLineForm("ITEM-1", 5m)], "TST"));

        Assert.True(outcome.Ok);
        Assert.Contains("unexpected", outcome.Summary);
        Assert.Contains("not json", string.Join("\n", outcome.Details));
    }

    [Fact]
    public async Task ReadMailReturnsThreadInOrder()
    {
        var h = Create();
        await new CaseMailStore(h.CacheDir).WriteAsync(new CaseMail("CASE-1",
        [
            new VendorEmail("E-2", "CASE-1", "V-1", "vendor@test.example", "p2p-agent",
                "Re: query", "Fixed.",
                new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero), "E-1"),
            new VendorEmail("E-1", "CASE-1", "V-1", "p2p-agent", "vendor@test.example",
                "Invoice query", "Quantity?",
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
        ]));

        var mail = await h.Service.ReadMailAsync("CASE-1");

        Assert.Equal(new[] {"E-1", "E-2"}, mail.Select(m => m.EmailId).ToList());
    }

    [Fact]
    public async Task ReadMailMissingYieldsEmpty()
    {
        var h = Create();

        Assert.Empty(await h.Service.ReadMailAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task SuggestReplyPrefillsFromAgentQuery()
    {
        var h = Create();
        await new CaseMailStore(h.CacheDir).WriteAsync(new CaseMail("CASE-1",
        [
            new VendorEmail("E-1", "CASE-1", "V-1", RecordSource.P2pAgent, "vendor@test.example",
                "Invoice query", "Quantity?",
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
        ]));

        var suggestion = await h.Service.SuggestReplyAsync("CASE-1");

        Assert.Equal("vendor@test.example", suggestion.From);
        Assert.Equal("E-1", suggestion.InReplyTo);
        Assert.Equal("Re: Invoice query", suggestion.Subject);
    }

    [Fact]
    public async Task SuggestReplyFallsBackToLatestVendorMessage()
    {
        var h = Create();
        await new CaseMailStore(h.CacheDir).WriteAsync(new CaseMail("CASE-1",
        [
            new VendorEmail("E-1", "CASE-1", "V-1", "vendor@test.example", "p2p-agent",
                "Our invoice", "See attached.",
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
        ]));

        var suggestion = await h.Service.SuggestReplyAsync("CASE-1");

        Assert.Equal("vendor@test.example", suggestion.From);
        Assert.Equal("E-1", suggestion.InReplyTo);
    }

    [Fact]
    public async Task SuggestReplyWithNoThreadResolvesVendorEmail()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await h.Erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await h.Erp.CreatePoAsync(
            pr.PrNumber, "V-1", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        await new CaseCacheStore(h.CacheDir).WriteAsync(new CaseFile(
            new Case("CASE-1", "SO-7", "ITEM-1", "LE-T", CaseStage.Created, now, now,
                pr.PrNumber, po.PoNumber, null, null, [], [], null, null),
            [], []));

        var suggestion = await h.Service.SuggestReplyAsync("CASE-1");

        Assert.Equal("vendor@test.example", suggestion.From);
        Assert.Null(suggestion.InReplyTo);
    }

    [Fact]
    public async Task DockImageReadReturnsBytes()
    {
        var h = Create();
        var path = Path.Combine(Path.GetDirectoryName(h.CacheDir)!, "dock", "IMG-T.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var (bytes, error) = await h.Service.TryReadDockImageAsync("IMG-T.jpg");

        Assert.Null(error);
        Assert.Equal(new byte[] {1, 2, 3}, bytes);
    }

    [Fact]
    public async Task DockImageReadRefusesTraversal()
    {
        var h = Create();

        var (bytes, error) = await h.Service.TryReadDockImageAsync("../secret.json");

        Assert.Null(bytes);
        Assert.Contains("../secret.json", error);
    }

    [Fact]
    public async Task PoLinesComeFromErp()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await h.Erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await h.Erp.CreatePoAsync(
            pr.PrNumber, "V-1", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));

        var lines = await h.Service.GetPoLinesAsync(po.PoNumber);

        var line = Assert.Single(lines);
        Assert.Equal(po.Lines.Single().PoLineRef, line.PoLineRef);
        Assert.Empty(await h.Service.GetPoLinesAsync("PO-MISSING"));
    }
}
