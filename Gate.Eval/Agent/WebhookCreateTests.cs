// Order-creation webhook tests: unknown shop orders carrying lines plus
// currency are created through the ERP (which mints the number) with the
// idempotency key from the shop order id; replays return the stored order
// and cases; unknown orders without lines stay a gap error.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class WebhookCreateTests
{
    private sealed record Harness(InMemoryErpAdapter Erp, CaseRunner Runner, string CaseDir);

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-webhook-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, dir);
    }

    private static OrderWebhookInput CreateInput(string shopOrder) =>
        new("shop", shopOrder, "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"]
            },
            Lines: [new SalesOrderLine(SeededErp.Glasses, 100)],
            Currency: "USD");

    [Fact]
    public async Task UnknownShopOrderWithLinesCreatesTheSalesOrder()
    {
        var h = Create();

        var caseIds = await h.Runner.OnOrderWebhookAsync(CreateInput("SHOP-7000"));

        Assert.Equal(2, caseIds.Count);
        var minted = CaseStore.Load(h.CaseDir, caseIds[0])!.Case.SoNumber;
        Assert.NotEqual("SHOP-7000", minted);
        var so = await h.Erp.GetSalesOrderAsync(minted);
        Assert.NotNull(so);
        Assert.Equal(SalesOrderStatus.Confirmed, so.Status);
        Assert.Equal("USD", so.Currency);
        Assert.Equal<List<(string ItemId, decimal Quantity)>>(
            [(SeededErp.Glasses, 100m)],
            so.Lines.Select(l => (l.ItemId, l.Quantity)).ToList());
        Assert.All(caseIds, id =>
            Assert.Equal(minted, CaseStore.Load(h.CaseDir, id)!.Case.SoNumber));
    }

    [Fact]
    public async Task CreatedOrderReplayReturnsTheSameCases()
    {
        var h = Create();
        var first = await h.Runner.OnOrderWebhookAsync(CreateInput("SHOP-7001"));

        var second = await h.Runner.OnOrderWebhookAsync(CreateInput("SHOP-7001"));

        Assert.Equal(first.Order().ToList(), second.Order().ToList());
        Assert.Equal(2, CaseStore.ListCaseIds(h.CaseDir).Count);
    }

    [Fact]
    public async Task UnknownOrderWithoutLinesThrows()
    {
        var h = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.OnOrderWebhookAsync(new OrderWebhookInput("shop", "SHOP-404", "planner")));

        Assert.Contains("carried no lines", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOrderWithoutCurrencyThrows()
    {
        var h = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.OnOrderWebhookAsync(new OrderWebhookInput("shop", "SHOP-405", "planner",
                Lines: [new SalesOrderLine(SeededErp.Glasses, 100)])));

        Assert.Contains("carried no currency", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownOrderStillMatchesThePreseededSo()
    {
        var h = Create();

        var caseIds = await h.Runner.OnOrderWebhookAsync(CreateInput("SO-1001"));

        Assert.Equal(2, caseIds.Count);
        Assert.All(caseIds, id =>
            Assert.Equal("SO-1001", CaseStore.Load(h.CaseDir, id)!.Case.SoNumber));
    }
}
