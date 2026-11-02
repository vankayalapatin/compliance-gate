// Seed consistency: the loader creates every document through adapter
// writes, covers every lifecycle stage per type, routes pending approvals
// per person, and records approved history against real users — all while
// preserving the demo math (shortfall inputs, CORP-02 history). Expected
// values are read from the scenario file at runtime; these tests never
// hard-code demo numbers.
using System.Text.Json;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class SeedConsistencyTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task DemoSeedConsistent_Fake()
    {
        await ConsistentAsync("scenarios/demo-v1.json", useEfCore: false);
    }

    [Fact]
    public async Task DemoSeedConsistent_EfCore()
    {
        await ConsistentAsync("scenarios/demo-v1.json", useEfCore: true);
    }

    [Fact]
    public async Task AltSeedConsistent_Fake()
    {
        await ConsistentAsync("scenarios/alt-v1.json", useEfCore: false);
    }

    [Fact]
    public async Task AltSeedConsistent_EfCore()
    {
        await ConsistentAsync("scenarios/alt-v1.json", useEfCore: true);
    }

    private static async Task ConsistentAsync(string scenarioPath, bool useEfCore)
    {
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        Assert.True(File.Exists(resolved), $"Scenario file not found: {resolved}");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resolved));
        var root = json.RootElement;
        var expected = ScenarioExpectations.Read(root);

        var workDir = Path.Combine(Path.GetTempPath(), "gate-seed-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            IErpAdapter erp;
            ErpDbContext? db = null;
            if (useEfCore)
            {
                var options = new DbContextOptionsBuilder<ErpDbContext>()
                    .UseSqlite($"Data Source={Path.Combine(workDir, "erp.db")}")
                    .Options;
                db = new ErpDbContext(options);
                erp = new EfCoreErpAdapter(options);
            }
            else
            {
                erp = new InMemoryErpAdapter();
            }

            await using (db ?? (IAsyncDisposable)new NoopAsync())
            {
                var manifest = await ScenarioLoader.ResetAsync(erp, resolved,
                    Path.Combine(workDir, "gate.db"), Path.Combine(workDir, "run-manifest.json"));
                string Doc(string eventKey) => manifest.Documents.Single(d => d.Event == eventKey).Number;

                await HeaderPlusLinesAsync(erp);
                await StageCoverageAsync(erp, expected, Doc);
                await PendingRoutedPerPersonAsync(erp, expected);
                await ApprovalHistoryAsync(erp, expected);
                await SequencesContinueAsync(erp, expected);
                await DemoMathPreservedAsync(erp, expected, useEfCore);
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static async Task HeaderPlusLinesAsync(IErpAdapter erp)
    {
        Assert.All(await erp.GetSalesOrdersAsync(), s => Assert.NotEmpty(s.Lines));
        Assert.All(await erp.GetPurchaseRequisitionsAsync(), p => Assert.NotEmpty(p.Lines));
        Assert.All(await erp.GetPurchaseOrdersAsync(), p => Assert.NotEmpty(p.Lines));
        Assert.All(await erp.GetShipmentReceiptsAsync(), r => Assert.NotEmpty(r.Lines));
        Assert.All(await erp.GetVendorInvoicesAsync(), i => Assert.NotEmpty(i.Lines));
        Assert.All(await erp.GetPaymentsAsync(), p => Assert.NotEmpty(p.Lines));
        Assert.All(await erp.GetAsnsAsync(), a => Assert.NotEmpty(a.Cartons));
        // Adapter-write provenance: every generated document carries the
        // idempotency key it was written with.
        Assert.All(await erp.GetPurchaseRequisitionsAsync(), p => Assert.False(string.IsNullOrWhiteSpace(p.IdempotencyKey)));
        Assert.All(await erp.GetPurchaseOrdersAsync(), p => Assert.False(string.IsNullOrWhiteSpace(p.IdempotencyKey)));
        Assert.All(await erp.GetShipmentReceiptsAsync(), r => Assert.False(string.IsNullOrWhiteSpace(r.IdempotencyKey)));
        Assert.All(await erp.GetVendorInvoicesAsync(), i => Assert.False(string.IsNullOrWhiteSpace(i.IdempotencyKey)));
        Assert.All(await erp.GetPaymentsAsync(), p => Assert.False(string.IsNullOrWhiteSpace(p.IdempotencyKey)));
        Assert.All(await erp.GetAsnsAsync(), a => Assert.False(string.IsNullOrWhiteSpace(a.IdempotencyKey)));
    }

    private static async Task StageCoverageAsync(IErpAdapter erp, ScenarioExpectations expected, Func<string, string> doc)
    {
        var prs = await erp.GetPurchaseRequisitionsAsync();
        Assert.Contains(prs, p => p.PrNumber == doc("lc.prDraft.pr") && p.Status == PrStatus.Draft);
        Assert.Contains(prs, p => p.PrNumber == doc("lc.prSubmitted.pr") && p.Status == PrStatus.Submitted);
        Assert.Contains(prs, p => p.PrNumber == doc("lc.prApproved.pr") && p.Status == PrStatus.Approved);
        Assert.Contains(prs, p => p.PrNumber == doc("lc.backbone.pr") && p.Status == PrStatus.Closed);

        var pos = await erp.GetPurchaseOrdersAsync();
        Assert.Contains(pos, p => p.PoNumber == doc("lc.poDraft.po") && p.ApprovalStatus == PoApprovalStatus.Draft);
        Assert.Contains(pos, p => p.PoNumber == doc("lc.poSubmitted.po") && p.ApprovalStatus == PoApprovalStatus.InReview);
        Assert.Contains(pos, p => p.PoNumber == doc("lc.poApproved.po") && p.ApprovalStatus == PoApprovalStatus.Approved);
        Assert.Contains(pos, p => p.PoNumber == doc("lc.backbone.po") && p.ApprovalStatus == PoApprovalStatus.Confirmed);
        Assert.Contains(pos, p => p.DocStatus == PoDocStatus.Closed);

        var receipts = await erp.GetShipmentReceiptsAsync();
        Assert.Contains(receipts, r => r.ReceiptNumber == doc("lc.backbone.receipt") && r.Status == ReceiptStatus.Posted);

        var asns = await erp.GetAsnsAsync();
        var asn = Assert.Single(asns);
        Assert.Equal(expected.LifecycleAsn, asn.AsnNumber);
        Assert.Equal(doc("lc.backbone.po"), asn.PoNumber);

        var invoices = await erp.GetVendorInvoicesAsync();
        Assert.Contains(invoices, i => i.SupplierInvoiceNumber == expected.InvoiceNumber("received") && i.Status == InvoiceStatus.Received);
        Assert.Contains(invoices, i => i.SupplierInvoiceNumber == expected.InvoiceNumber("matched") && i.Status == InvoiceStatus.Matched);
        Assert.Contains(invoices, i => i.SupplierInvoiceNumber == expected.InvoiceNumber("approved") && i.Status == InvoiceStatus.Approved);
        Assert.Contains(invoices, i => i.SupplierInvoiceNumber == expected.InvoiceNumber("payDraft") && i.Status == InvoiceStatus.Posted);
        var paid = invoices.Single(i => i.SupplierInvoiceNumber == expected.InvoiceNumber("paid"));
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.All(paid.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));

        var payments = await erp.GetPaymentsAsync();
        Assert.Contains(payments, p => p.PaymentNumber == doc("lc.payDraft.pay") && p.Status == PaymentStatus.Draft);
        Assert.Contains(payments, p => p.PaymentNumber == doc("lc.paySubmitted.pay") && p.Status == PaymentStatus.Submitted);
        Assert.Contains(payments, p => p.PaymentNumber == doc("lc.payApproved.pay") && p.Status == PaymentStatus.Approved);
        Assert.Contains(payments, p => p.PaymentNumber == doc("lc.payPosted.pay") && p.Status == PaymentStatus.Posted);
    }

    private static async Task PendingRoutedPerPersonAsync(IErpAdapter erp, ScenarioExpectations expected)
    {
        var pending = await erp.GetPendingApprovalsAsync();
        Assert.Equal(4, pending.Count);
        Assert.Contains(pending, r => r.DocumentType == "PR" && r.RoutedTo == expected.PrSubmittedTo);
        Assert.Contains(pending, r => r.DocumentType == "PO" && r.RoutedTo == expected.PoSubmittedTo);
        Assert.Contains(pending, r => r.DocumentType == "Invoice" && r.RoutedTo == expected.InvoiceSubmittedTo);
        Assert.Contains(pending, r => r.DocumentType == "Payment" && r.RoutedTo == expected.PaySubmittedTo);
        Assert.All(pending, r =>
        {
            Assert.Equal(expected.Requester, r.SubmittedBy);
            Assert.True(r.DocumentAmount > 0);
            Assert.Contains(r.RoutedTo, expected.Users);
        });
        Assert.Equal(4, pending.Select(r => r.RoutedTo).Distinct(StringComparer.Ordinal).Count());
    }

    private static async Task ApprovalHistoryAsync(IErpAdapter erp, ScenarioExpectations expected)
    {
        var count = 0;
        async Task CheckAsync(string type, string number)
        {
            foreach (var approval in await erp.GetApprovalsAsync(type, number))
            {
                count++;
                Assert.NotNull(approval.ActedBy);
                if (string.Equals(approval.ActedBy, ErpApprovalActors.AutoApprovePolicy, StringComparison.Ordinal))
                {
                    Assert.True(approval.Approved);
                    Assert.True(approval.DocumentAmount <= approval.LimitChecked);
                    continue;
                }

                Assert.Contains(approval.ActedBy, expected.Users);
                Assert.NotEqual(expected.Requester, approval.ActedBy);
                if (approval.Approved) Assert.True(approval.DocumentAmount <= approval.LimitChecked);
                var user = await erp.GetUserAsync(approval.ActedBy);
                Assert.NotNull(user);
                Assert.Equal(user.SigningLimit, approval.LimitChecked);
            }
        }
        foreach (var pr in await erp.GetPurchaseRequisitionsAsync()) await CheckAsync("PR", pr.PrNumber);
        foreach (var po in await erp.GetPurchaseOrdersAsync()) await CheckAsync("PO", po.PoNumber);
        foreach (var inv in await erp.GetVendorInvoicesAsync())
            await CheckAsync("Invoice", $"{inv.VendorId}/{inv.SupplierInvoiceNumber}");
        foreach (var pay in await erp.GetPaymentsAsync()) await CheckAsync("Payment", pay.PaymentNumber);
        Assert.True(count >= 10, $"Expected at least 10 seed approvals, found {count}.");
    }

    private static async Task SequencesContinueAsync(IErpAdapter erp, ScenarioExpectations expected)
    {
        static long Suffix(string prefix, string number)
        {
            var tail = number.Substring(prefix.Length + 1);
            return long.Parse(tail, System.Globalization.CultureInfo.InvariantCulture);
        }
        var prMax = (await erp.GetPurchaseRequisitionsAsync()).Max(p => Suffix("PR", p.PrNumber));
        var next = await erp.CreatePrAsync("seed-check", RecordSource.Human, "seed-check",
            [new PurchaseRequisitionLine(expected.OpenPoItem, 1, "EA")], Idem());
        Assert.True(Suffix("PR", next.PrNumber) > prMax);
        var poMax = (await erp.GetPurchaseOrdersAsync()).Max(p => Suffix("PO", p.PoNumber));
        Assert.Matches(@"^PO-\d{4,}$", (await erp.GetPurchaseOrdersAsync()).First().PoNumber);
        Assert.True(poMax > 0);
    }

    private static async Task DemoMathPreservedAsync(IErpAdapter erp, ScenarioExpectations expected, bool useEfCore)
    {
        // Shortfall inputs: stock untouched, only the inbound PO open.
        foreach (var (item, qty) in expected.Stock)
        {
            Assert.Equal(qty, await erp.GetStockAsync(item));
        }
        var openInbound = await erp.GetOpenPurchaseOrdersAsync(expected.OpenPoItem);
        var openQty = openInbound.SelectMany(p => p.Lines)
            .Where(l => l.ItemId == expected.OpenPoItem)
            .Sum(l => l.QtyOrdered - l.QtyReceived);
        Assert.Equal(expected.OpenPoQty, openQty);
        foreach (var component in expected.BomComponents.Where(c => c != expected.OpenPoItem))
        {
            Assert.Empty(await erp.GetOpenPurchaseOrdersAsync(component));
        }
        if (!useEfCore)
        {
            // CORP-02 history on the fake: seeded cells only, no lifecycle
            // receipt pollution.
            var history = await erp.GetVolumeHistoryAsync(expected.HistoryPart);
            var byCountry = history.GroupBy(h => h.Country).ToDictionary(g => g.Key, g => g.Sum(h => h.Cells));
            Assert.Equal(expected.HistoryCells.Count, byCountry.Count);
            foreach (var (country, cells) in expected.HistoryCells)
            {
                Assert.True(byCountry.TryGetValue(country, out var got) && got == cells,
                    $"History {country}: expected {cells}, ERP has {byCountry.GetValueOrDefault(country)}.");
            }
            return;
        }
        // CORP-02 history on EF Core: posted receipt lines only. The loader
        // posts no receipts for the history part, so its history is empty;
        // the backbone receipt carries the lifecycle item instead.
        Assert.Empty(await erp.GetVolumeHistoryAsync(expected.HistoryPart));
        var lcHistory = await erp.GetVolumeHistoryAsync(expected.LifecycleItem);
        Assert.Equal(expected.BackboneQty, lcHistory.Sum(h => h.Cells));
        Assert.All(lcHistory, h => Assert.Equal(expected.LifecycleVendorCountry, h.Country));
    }

    private sealed class NoopAsync : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record ScenarioExpectations(
        string Requester,
        IReadOnlyList<string> Users,
        IReadOnlyList<(string Item, decimal Qty)> Stock,
        string HistoryPart,
        string OpenPoItem,
        decimal OpenPoQty,
        IReadOnlyList<string> BomComponents,
        IReadOnlyList<(string Country, decimal Cells)> HistoryCells,
        string PrSubmittedTo,
        string PoSubmittedTo,
        string InvoiceSubmittedTo,
        string PaySubmittedTo,
        string LifecycleAsn,
        string LifecycleItem,
        string LifecycleVendorCountry,
        decimal BackboneQty,
        IReadOnlyDictionary<string, string> InvoiceNumbers)
    {
        public string InvoiceNumber(string key) => InvoiceNumbers[key];

        public static ScenarioExpectations Read(JsonElement root)
        {
            var users = root.GetProperty("users").EnumerateArray()
                .Select(u => u.GetProperty("name").GetString()!).ToList();
            var requester = root.GetProperty("users").EnumerateArray()
                .First(u => u.GetProperty("role").GetString() == "requester")
                .GetProperty("name").GetString()!;
            var stock = root.GetProperty("stock").EnumerateArray()
                .Select(s => (s.GetProperty("item").GetString()!, s.GetProperty("quantity").GetDecimal())).ToList();
            var history = root.GetProperty("historyCells").EnumerateArray()
                .GroupBy(h => h.GetProperty("country").GetString()!)
                .Select(g => (g.Key, g.Sum(h => h.GetProperty("cells").GetDecimal()))).ToList();
            var lc = root.GetProperty("lifecycle");
            var invoices = lc.GetProperty("invoices").EnumerateArray()
                .ToDictionary(i => i.GetProperty("key").GetString()!, i => i.GetProperty("number").GetString()!);
            var lcVendor = lc.GetProperty("vendor").GetString()!;
            var lcCountry = root.GetProperty("vendors").EnumerateArray()
                .First(v => v.GetProperty("id").GetString() == lcVendor)
                .GetProperty("country").GetString()!;
            return new ScenarioExpectations(
                requester,
                users,
                stock,
                root.GetProperty("historyPart").GetString()!,
                root.GetProperty("openPoInbound").GetProperty("item").GetString()!,
                root.GetProperty("openPoInbound").GetProperty("quantity").GetDecimal(),
                root.GetProperty("bom").EnumerateArray()
                    .Select(b => b.GetProperty("component").GetString()!).ToList(),
                history,
                lc.GetProperty("prSubmittedTo").GetString()!,
                lc.GetProperty("poSubmittedTo").GetString()!,
                lc.GetProperty("invoiceSubmittedTo").GetString()!,
                lc.GetProperty("paySubmittedTo").GetString()!,
                lc.GetProperty("asn").GetString()!,
                lc.GetProperty("item").GetString()!,
                lcCountry,
                lc.GetProperty("backboneQty").GetDecimal(),
                invoices);
        }
    }
}
