// Batch-3 UI: vendor mail thread store plus case-detail surfacing, new
// timeline event labels, vendor contact-email display, and proof that
// mail files do not break case listing. No live calls.
using System.Text.Json;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class CaseMailUiTests
{
    private static CaseViewService Service(InMemoryErpAdapter erp, CaseCacheStore store) =>
        new(erp, store,
            new DockPhotoService(UiTestSupport.NewDir("gate-ui-dock"), UiTestSupport.NewDir("gate-ui-vision")),
            UiTestLogger<CaseViewService>.Instance);

    private static CaseMail ThreadWith(string caseId) =>
        new(caseId,
        [
            new VendorEmail("E-1", caseId, "V-1", "ap@test-buyer.test", "sales@test-vendor.test",
                "Invoice query", "Line 2 quantity looks wrong.",
                new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero)),
            new VendorEmail("E-2", caseId, "V-1", "sales@test-vendor.test", "ap@test-buyer.test",
                "Re: Invoice query", "Corrected invoice attached.",
                new DateTimeOffset(2026, 10, 21, 10, 30, 0, TimeSpan.Zero), "E-1", "INV-7"),
        ]);

    [Fact]
    public async Task MailWriteThenReadRoundTrips()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseMailStore(dir);
        var mail = ThreadWith("CASE-1");

        await store.WriteAsync(mail);
        var reread = await store.ReadAsync("CASE-1");

        Assert.NotNull(reread);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(
            JsonSerializer.Serialize(mail, web),
            JsonSerializer.Serialize(reread, web));
    }

    [Fact]
    public async Task MailWriteIsByteExactWebDefaults()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseMailStore(dir);
        var mail = ThreadWith("CASE-9");

        await store.WriteAsync(mail);

        var path = Path.Combine(dir, "CASE-9.mail.json");
        Assert.True(File.Exists(path));
        var expected = JsonSerializer.Serialize(mail, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingMailFileReadsNull()
    {
        var store = new CaseMailStore(UiTestSupport.NewDir("gate-ui-cache"));

        Assert.Null(await store.ReadAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task MissingMailFileYieldsEmptyThreadNeverError()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        var service = Service(new InMemoryErpAdapter(), store);

        var detail = await service.GetCaseDetailAsync("CASE-1");

        Assert.NotNull(detail);
        Assert.Empty(detail.Mail);
    }

    [Fact]
    public async Task PresentMailFileSurfacesThread()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        await new CaseMailStore(dir).WriteAsync(ThreadWith("CASE-1"));
        var service = Service(new InMemoryErpAdapter(), store);

        var detail = await service.GetCaseDetailAsync("CASE-1");

        Assert.NotNull(detail);
        Assert.Equal(2, detail.Mail.Count);
        Assert.Contains(detail.Mail, m =>
            m.From == "ap@test-buyer.test" && m.To == "sales@test-vendor.test"
            && m.Subject == "Invoice query" && m.Body.Contains("quantity"));
        Assert.Contains(detail.Mail, m =>
            m.From == "sales@test-vendor.test" && m.InReplyTo == "E-1");
    }

    [Fact]
    public async Task ListSkipsMailFiles()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        await new CaseMailStore(dir).WriteAsync(ThreadWith("CASE-1"));

        var listed = await store.ListAsync();

        var single = Assert.Single(listed);
        Assert.Equal("CASE-1", single.Case.CaseId);
    }

    [Fact]
    public async Task ListSkipsDockSidecars()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        Gate.Agent.CaseStore.SaveDock(dir, new Gate.Agent.DockEvidence(
            "CASE-1", [], new Dictionary<string, IReadOnlyList<Gate.Contracts.Verdict>>(),
            DateTimeOffset.UtcNow));

        var listed = await store.ListAsync();

        var single = Assert.Single(listed);
        Assert.Equal("CASE-1", single.Case.CaseId);
        Assert.NotNull(await store.ReadDockAsync("CASE-1"));
        Assert.Null(await store.ReadDockAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task ListSkipsProblemSidecars()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        Gate.Agent.CaseStore.SaveProblems(dir, new Gate.Agent.DockProblems(
            "CASE-1", [new Gate.Agent.SimulatedProblem("C-1", Gate.Agent.SimulatedProblemKind.Crushed)],
            DateTimeOffset.UtcNow));

        var listed = await store.ListAsync();

        var single = Assert.Single(listed);
        Assert.Equal("CASE-1", single.Case.CaseId);
    }

    [Fact]
    public void NewEventLabelsAreClear()
    {
        Assert.Equal("Vendor emailed", CaseEventLabels.Label(CaseEventKind.VendorEmailed));
        Assert.Equal("Vendor replied", CaseEventLabels.Label(CaseEventKind.VendorReplied));
    }

    [Fact]
    public void EveryEventLabelReadsAsPlainWords()
    {
        // Multi-word kinds never render their raw member name; single-word
        // kinds (Note) are already plain.
        foreach (var kind in Enum.GetValues<CaseEventKind>())
        {
            var label = CaseEventLabels.Label(kind);
            Assert.False(string.IsNullOrWhiteSpace(label));
            if (kind.ToString().Skip(1).Any(char.IsUpper))
            {
                Assert.NotEqual(kind.ToString(), label.Replace(" ", string.Empty));
            }
        }

        Assert.Equal("Note", CaseEventLabels.Label(CaseEventKind.Note));
        Assert.Equal("Request approved", CaseEventLabels.Label(CaseEventKind.PrApproved));
        Assert.Equal("Dock checked", CaseEventLabels.Label(CaseEventKind.DockEvaluated));
        Assert.Equal("Order placed", CaseEventLabels.Label(CaseEventKind.PoCreated));
        Assert.Equal("Payment posted", CaseEventLabels.Label(CaseEventKind.PaymentPosted));
    }

    [Fact]
    public void NullEmailRendersPlaceholder()
    {
        Assert.Equal("not-on-file", VendorLabels.EmailOrPlaceholder(null));
        Assert.Equal("not-on-file", VendorLabels.EmailNotOnFile);
        Assert.Equal("ap@test-vendor.test", VendorLabels.EmailOrPlaceholder("ap@test-vendor.test"));
    }

    [Fact]
    public async Task VendorListCarriesVendorsWithEmail()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedVendor(new VendorDto("V-2", "No Mail Vendor", "US", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-1", "Mail Vendor", "US", false, null, "NET0", "ap@test-vendor.test"));
        var views = new ErpViewService(
            erp, Service(erp, new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"))),
            UiTestLogger<ErpViewService>.Instance);

        var vendors = await views.ListVendorsAsync();

        Assert.Equal(2, vendors.Rows.Count);
        Assert.Equal("V-1", vendors.Rows[0].VendorId);
        Assert.Equal("ap@test-vendor.test", vendors.Rows[0].Email);
        Assert.Null(vendors.Rows[1].Email);
    }
}
