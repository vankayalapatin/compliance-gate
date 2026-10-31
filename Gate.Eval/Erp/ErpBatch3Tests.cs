// Batch-3 ERP additions on the fake: sales-order creation with idempotent
// redelivery, pay-up-to-date payment drafts, vendor email seeding.
using System.Text.Json;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpBatch3Tests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task CreateSalesOrderMintsNumberAndStartsCreated()
    {
        var erp = SeededErp.Create();
        var so = await erp.CreateSalesOrderAsync("USD",
            [new SalesOrderLine("GLASS-1", 5)], "order-webhook", "order-webhook", Idem());
        Assert.StartsWith("SO-", so.SoNumber);
        Assert.Equal(SalesOrderStatus.Created, so.Status);
        Assert.Equal("USD", so.Currency);
        var line = Assert.Single(so.Lines);
        Assert.Equal("GLASS-1", line.ItemId);
        Assert.Equal(5m, line.Quantity);
        var stored = await erp.GetSalesOrderAsync(so.SoNumber);
        Assert.NotNull(stored);
        Assert.Equal(SalesOrderStatus.Created, stored.Status);
        Assert.Equal("USD", stored.Currency);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal("GLASS-1", storedLine.ItemId);
        Assert.Equal(5m, storedLine.Quantity);
    }

    [Fact]
    public async Task CreateSalesOrderRedeliveryReturnsStoredOrder()
    {
        var erp = SeededErp.Create();
        var key = Idem();
        var first = await erp.CreateSalesOrderAsync("USD",
            [new SalesOrderLine("GLASS-1", 5)], "order-webhook", "order-webhook", key);
        var second = await erp.CreateSalesOrderAsync("USD",
            [new SalesOrderLine("BAT-1", 99)], "order-webhook", "order-webhook", key);
        Assert.Equal(first.SoNumber, second.SoNumber);
        Assert.Equal(SalesOrderStatus.Created, second.Status);
        var stored = await erp.GetSalesOrderAsync(first.SoNumber);
        Assert.NotNull(stored);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal("GLASS-1", storedLine.ItemId);
        Assert.Equal(5m, storedLine.Quantity);
    }

    [Fact]
    public async Task CreateSalesOrderRefusesBadLines()
    {
        var erp = SeededErp.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateSalesOrderAsync("USD", [], "order-webhook", "order-webhook", Idem()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateSalesOrderAsync("USD", [new SalesOrderLine("GLASS-1", 0)],
                "order-webhook", "order-webhook", Idem()));
    }

    [Fact]
    public async Task PayUpToDateSkipsInvoiceDueAfter()
    {
        var erp = SeededErp.Create();
        await PostedInvoiceAsync(erp, "INV-D1");
        var key = new InvoiceKey("V-B", "INV-D1");
        var baseline = await erp.CreatePaymentDraftAsync([key], RecordSource.P2pAgent, "planner", Idem());
        var due = baseline.DueDate;
        var onDate = await erp.CreatePaymentDraftAsync([key], due, RecordSource.P2pAgent, "planner", Idem());
        Assert.Single(onDate.Lines);
        Assert.Equal(baseline.Lines[0].Amount, onDate.Lines[0].Amount);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentDraftAsync([key], due.AddDays(-1), RecordSource.P2pAgent, "planner", Idem()));
        Assert.Contains("Nothing due", ex.Message);
    }

    [Fact]
    public async Task PayUpToDateNullMeansNoLimit()
    {
        var erp = SeededErp.Create();
        await PostedInvoiceAsync(erp, "INV-D2");
        var keys = new[] { new InvoiceKey("V-B", "INV-D2") };
        var dated = await erp.CreatePaymentDraftAsync(keys, null, RecordSource.P2pAgent, "planner", Idem());
        var plain = await erp.CreatePaymentDraftAsync(keys, RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(plain.Lines.Count, dated.Lines.Count);
        Assert.Equal(plain.Lines[0].Amount, dated.Lines[0].Amount);
        Assert.Equal(plain.DueDate, dated.DueDate);
    }

    [Fact]
    public async Task PayUpToDateAllSkippedThrowsNothingDue()
    {
        var erp = SeededErp.Create();
        await PostedInvoiceAsync(erp, "INV-D3");
        await PostedInvoiceAsync(erp, "INV-D4");
        var keys = new[] { new InvoiceKey("V-B", "INV-D3"), new InvoiceKey("V-B", "INV-D4") };
        var baseline = await erp.CreatePaymentDraftAsync(keys, RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(2, baseline.Lines.Count);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentDraftAsync(keys, baseline.DueDate.AddDays(-1), RecordSource.P2pAgent, "planner", Idem()));
        Assert.Contains("Nothing due", ex.Message);
    }

    [Fact]
    public async Task SeedVendorCarriesEmail()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedVendor(new VendorDto("V-T", "Test Vendor", "US", false, null, "NET0", "ap@test.example"));
        var vendors = await erp.GetVendorsAsync();
        Assert.Equal("ap@test.example", Assert.Single(vendors).Email);
    }

    [Fact]
    public async Task VendorEmailSeedsFromDemoScenario()
    {
        await AssertScenarioEmailsAsync("scenarios/demo-v1.json");
    }

    [Fact]
    public async Task VendorEmailSeedsFromAltScenario()
    {
        await AssertScenarioEmailsAsync("scenarios/alt-v1.json");
    }

    private static async Task PostedInvoiceAsync(InMemoryErpAdapter erp, string supplierInvoiceNumber, decimal qty = 2)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, $"PACK-{supplierInvoiceNumber}", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, qty, 0, 0, $"LOT-{supplierInvoiceNumber}", "ok")], Idem());
        await erp.CreateInvoiceAsync(supplierInvoiceNumber, "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, qty, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", supplierInvoiceNumber,
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var invKey = $"V-B/{supplierInvoiceNumber}";
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", supplierInvoiceNumber, "planner", Idem());
    }

    private static async Task AssertScenarioEmailsAsync(string scenario)
    {
        var erp = new InMemoryErpAdapter();
        var path = ScenarioLoader.ResolvePath(scenario);
        Assert.True(File.Exists(path), $"Scenario file not found: {path}");
        var workDir = Path.Combine(Path.GetTempPath(), "gate-email-test", Guid.NewGuid().ToString("N"));
        try
        {
            await ScenarioLoader.ResetAsync(erp, path,
                Path.Combine(workDir, "gate.db"), Path.Combine(workDir, "run-manifest.json"));
            var expected = ReadScenarioEmails(path);
            Assert.NotEmpty(expected);
            foreach (var email in expected.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(email));
            }
            var vendors = await erp.GetVendorsAsync();
            Assert.Equal(expected.Count, vendors.Count);
            foreach (var v in vendors)
            {
                Assert.True(expected.TryGetValue(v.VendorId, out var email), $"Vendor {v.VendorId} missing from scenario.");
                Assert.Equal(email, v.Email);
            }
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    private static Dictionary<string, string?> ReadScenarioEmails(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("vendors").EnumerateArray()
            .ToDictionary(
                v => v.GetProperty("id").GetString()!,
                v => v.TryGetProperty("email", out var e) ? e.GetString() : null);
    }
}
