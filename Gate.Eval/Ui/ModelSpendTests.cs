// Spend ledger plus metering client: unconfigured trackers report
// counts and say spend is unmetered (never refusing), metered trackers
// price calls from config and refuse past the cap, and the metering
// client records only successful live calls. Rates and caps are
// test-local config values, never the demo numbers. No live calls.
using Gate.Contracts;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class ModelSpendTests
{
    private static ModelSpendTracker Tracker(string? rate = null, string? cap = null)
    {
        var values = new Dictionary<string, string?>();
        if (rate is not null)
        {
            values["ModelSpend:CostPerCallUsd"] = rate;
        }

        if (cap is not null)
        {
            values["ModelSpend:CapUsd"] = cap;
        }

        return new ModelSpendTracker(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private sealed class ThrowingInner : IModelClient
    {
        public string ModelName => "boom";

        public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
            Task.FromException<ModelResult>(new InvalidOperationException("live call exploded"));
    }

    [Fact]
    public void Unconfigured_ReportsCountsAsUnmetered()
    {
        var spend = Tracker();

        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");

        Assert.Equal(2, spend.LiveCallCount);
        Assert.Null(spend.CostPerCallUsd);
        Assert.Null(spend.CapUsd);
        Assert.Null(spend.SpendUsd);
        Assert.False(spend.AtCap);
        Assert.Contains("unmetered", spend.Summary);
        Assert.Equal(2, spend.CallsByModel["spark"]);
        spend.ThrowIfCapped();
    }

    [Fact]
    public void Metered_PricesCallsAndRefusesPastCap()
    {
        var spend = Tracker(rate: "0.25", cap: "1.00");

        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");

        Assert.Equal(0.50m, spend.SpendUsd);
        Assert.False(spend.AtCap);
        Assert.Equal("$0.50 of $1.00", spend.Summary);
        spend.ThrowIfCapped();

        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");

        Assert.Equal(1.00m, spend.SpendUsd);
        Assert.True(spend.AtCap);
        Assert.Contains("cap reached", spend.Summary);
        var ex = Assert.Throws<InvalidOperationException>(() => spend.ThrowIfCapped());
        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public void MeteredWithoutCap_NeverRefuses()
    {
        var spend = Tracker(rate: "0.25");

        for (var i = 0; i < 100; i++)
        {
            spend.RecordLiveCall("spark");
        }

        Assert.Equal(25.00m, spend.SpendUsd);
        Assert.False(spend.AtCap);
        Assert.Contains("no cap", spend.Summary);
        spend.ThrowIfCapped();
    }

    [Fact]
    public void InvalidConfigValues_MeanUnconfigured()
    {
        var spend = Tracker(rate: "bogus", cap: "-5");

        spend.RecordLiveCall("spark");

        Assert.Null(spend.CostPerCallUsd);
        Assert.Null(spend.CapUsd);
        Assert.Null(spend.SpendUsd);
        Assert.False(spend.AtCap);
        Assert.Contains("unmetered", spend.Summary);
        spend.ThrowIfCapped();
    }

    [Fact]
    public async Task MeteredClient_RecordsOnlySuccessfulLiveCalls()
    {
        var inner = new FakeModelClient();
        var spend = Tracker(rate: "1", cap: "100");
        var client = new MeteredModelClient(
            inner, spend, UiTestLogger<MeteredModelClient>.Instance);

        Assert.Equal("fake", client.ModelName);
        var result = await client.CompleteAsync(new ModelRequest("hi"));

        Assert.NotNull(result);
        Assert.Equal(1, inner.CallCount);
        Assert.Equal(1, spend.LiveCallCount);
        Assert.Equal(1m, spend.SpendUsd);
        Assert.Equal(1, spend.CallsByModel["fake"]);
    }

    [Fact]
    public async Task MeteredClient_AtCapRefusesWithoutCallingInner()
    {
        var inner = new FakeModelClient();
        var spend = Tracker(rate: "1", cap: "1");
        spend.RecordLiveCall("fake");
        var client = new MeteredModelClient(
            inner, spend, UiTestLogger<MeteredModelClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync(new ModelRequest("hi")));

        Assert.Contains("cap", ex.Message);
        Assert.Equal(0, inner.CallCount);
        Assert.Equal(1, spend.LiveCallCount);
    }

    [Fact]
    public async Task MeteredClient_FailedCallAccruesNothing()
    {
        var spend = Tracker(rate: "1", cap: "100");
        var client = new MeteredModelClient(
            new ThrowingInner(), spend, UiTestLogger<MeteredModelClient>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync(new ModelRequest("hi")));

        Assert.Equal(0, spend.LiveCallCount);
        Assert.Empty(spend.CallsByModel);
    }
}
