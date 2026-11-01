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
    private static (SimulationUiService Service, InMemoryErpAdapter Erp) Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-portal");
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = Path.Combine(dir, "scenario.json"),
                ["Scenario:Manifest"] = Path.Combine(dir, "run-manifest.json"),
            })
            .Build();
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
            config, UiTestLogger<SimulationUiService>.Instance);
        return (service, erp);
    }

    private static SimulateOptions OptionsWith(params VendorDto[] vendors) =>
        new("ui-portal", "TST", [], [], [], vendors, [], [], [], [],
            new Dictionary<string, IReadOnlyList<string>>(), [], [], "ITEM-P", 1m);

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
        var (service, erp) = Create();
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
        var (service, _) = Create();
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
}
