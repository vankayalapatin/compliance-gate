// Number sequences: padded default mints, scenario-configured format and
// start, continuation after the max seeded number, and loader application
// of the scenario numberSequences section. Scenario data below is owned by
// these tests, never the demo numbers.
using System.Text.Json;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class NumberSequenceTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task DefaultMintIsPadded_Fake()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], Idem());
        Assert.Equal("PR-0001", pr.PrNumber);
        var hold = await erp.SetHoldAsync("TEST", "note", null, "CASE-1", "planner",
            null, null, null, null, null, Idem());
        Assert.Equal("HLD-0001", hold.HoldId);
    }

    [Fact]
    public async Task DefaultMintIsPadded_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var pr = await fx.Adapter.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], Idem());
        Assert.Equal("PR-0001", pr.PrNumber);
        var hold = await fx.Adapter.SetHoldAsync("TEST", "note", null, "CASE-1", "planner",
            null, null, null, null, null, Idem());
        Assert.Equal("HLD-0001", hold.HoldId);
    }

    [Fact]
    public async Task ConfiguredFormatAndStartApply_Fake()
    {
        var erp = SeededErp.Create();
        erp.ConfigureSequence("PR", "PR#{number}", 0, 7);
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], Idem());
        Assert.Equal("PR#7", pr.PrNumber);
        var second = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], Idem());
        Assert.Equal("PR#8", second.PrNumber);
    }

    [Fact]
    public async Task ConfiguredFormatAndStartApply_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        ErpSeeder.ApplyNumberSequences(fx.Db, [new ScenarioNumberSequence("PR", "PR#{number}", 0, 7)]);
        var pr = await fx.Adapter.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], Idem());
        Assert.Equal("PR#7", pr.PrNumber);
        var row = await fx.Db.Sequences.FindAsync("PR");
        Assert.NotNull(row);
        Assert.Equal("PR#{number}", row.Format);
        Assert.Equal(0, row.Padding);
        Assert.Equal(8, row.Next);
    }

    [Fact]
    public async Task SequenceStartsAfterMaxSeededNumber_Fake()
    {
        var erp = SeededErp.Create();
        // SeededErp already carries SO-1001; the explicit row above it wins.
        erp.SeedSalesOrder(new SalesOrder("SO-1900", SalesOrderStatus.Created, "USD",
            [new SalesOrderLine("GLASS-1", 1)]));
        erp.ConfigureSequence("SO", null, 4, 500);
        var so = await erp.CreateSalesOrderAsync("USD",
            [new SalesOrderLine("GLASS-1", 1)], "test", "test", Idem());
        Assert.Equal("SO-1901", so.SoNumber);
    }

    [Fact]
    public async Task SequenceStartsAfterMaxSeededNumber_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        // EfCoreTestDb seeds SO-1001 explicitly, above the configured start.
        ErpSeeder.ApplyNumberSequences(fx.Db, [new ScenarioNumberSequence("SO", null, 4, 500)]);
        var so = await fx.Adapter.CreateSalesOrderAsync("USD",
            [new SalesOrderLine("GLASS-1", 1)], "test", "test", Idem());
        Assert.Equal("SO-1002", so.SoNumber);
        var row = await fx.Db.Sequences.FindAsync("SO");
        Assert.NotNull(row);
        Assert.Equal(4, row.Padding);
        Assert.Equal(1003, row.Next);
    }

    [Fact]
    public async Task LoaderAppliesScenarioSequences_Fake()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "gate-seq-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var scenario = Path.Combine(workDir, "seq-test.json");
            await File.WriteAllTextAsync(scenario, TestScenarioJson());
            var erp = new InMemoryErpAdapter();
            var manifest = await ScenarioLoader.ResetAsync(erp, scenario,
                Path.Combine(workDir, "gate.db"), Path.Combine(workDir, "run-manifest.json"));
            string Doc(string eventKey) => manifest.Documents.Single(d => d.Event == eventKey).Number;
            Assert.Equal("SO-500", Doc("salesOrder"));
            Assert.Equal("PR-500", Doc("openPoInbound.pr"));
            Assert.Equal("PO-500", Doc("openPoInbound.po"));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoaderAppliesScenarioSequences_EfCore()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "gate-seq-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var scenario = Path.Combine(workDir, "seq-test.json");
            await File.WriteAllTextAsync(scenario, TestScenarioJson());
            var options = new DbContextOptionsBuilder<ErpDbContext>()
                .UseSqlite($"Data Source={Path.Combine(workDir, "erp.db")}")
                .Options;
            await using var db = new ErpDbContext(options);
            var manifest = await ScenarioLoader.ResetAsync(new EfCoreErpAdapter(db), scenario,
                Path.Combine(workDir, "gate.db"), Path.Combine(workDir, "run-manifest.json"));
            string Doc(string eventKey) => manifest.Documents.Single(d => d.Event == eventKey).Number;
            Assert.Equal("SO-500", Doc("salesOrder"));
            Assert.Equal("PR-500", Doc("openPoInbound.pr"));
            Assert.Equal("PO-500", Doc("openPoInbound.po"));
            var seqRow = await db.Sequences.FindAsync("PR");
            Assert.NotNull(seqRow);
            Assert.Equal(3, seqRow.Padding);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static string TestScenarioJson()
    {
        return JsonSerializer.Serialize(new
        {
            name = "seq-test",
            currency = "USD",
            numberSequences = new[]
            {
                new { prefix = "SO", format = "{prefix}-{number}", padding = 3, start = 500L },
                new { prefix = "PR", format = "{prefix}-{number}", padding = 3, start = 500L },
                new { prefix = "PO", format = "{prefix}-{number}", padding = 3, start = 500L },
            },
            salesOrder = new { item = "GLASS-1", quantity = 10m },
            bom = new[] { new { item = "GLASS-1", component = "BAT-1", qtyPer = 1m } },
            stock = new[] { new { item = "BAT-1", quantity = 1m } },
            openPoInbound = new { item = "BAT-1", quantity = 2m, vendor = "V-B" },
            items = new[]
            {
                new { id = "BAT-1", description = "Battery cell", critical = true, packSize = 10, unit = "EA" },
                new { id = "GLASS-1", description = "Smart glasses", critical = false, packSize = 1, unit = "EA" },
            },
            paymentTerms = new[] { new { id = "NET0", netDays = 0 } },
            vendors = new[]
            {
                new
                {
                    id = "V-B", name = "Cells Inc", country = "MX", role = "test",
                    item = "BAT-1", unitPrice = 12m, terms = "NET0", bankAccount = "BANK-T",
                    documents = Array.Empty<object>(),
                },
            },
            historyPart = "BAT-1",
            historyCells = new[] { new { source = "test", country = "MX", cells = 10m } },
            users = new[]
            {
                new { name = "req", role = "requester", signingLimit = 5000m },
                new { name = "app", role = "approver", signingLimit = 50000m },
                new { name = "fin", role = "payment-approver", signingLimit = 100000m },
            },
            delegations = Array.Empty<object>(),
            autoApproveUnder = 500m,
            cartonPlan = new { unitsPerCarton = 10, cartons = 1 },
            smallPos = Array.Empty<object>(),
        });
    }
}
