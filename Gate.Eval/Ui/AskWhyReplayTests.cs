// Ask-why plus offline replay: cached answers serve with zero inner
// calls, and replay mode refuses a cache miss instead of calling live.
// No live calls.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class AskWhyReplayTests
{
    private sealed class StubCompliance : IComplianceEngine
    {
        public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
            VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
            ProposedAward award, IReadOnlyList<VolumeHistory> history, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Verdict>>([]);

        public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
            PrePayState state, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Verdict>>([]);
    }

    private static async Task<CaseCacheStore> StoreWithDecisionAsync()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1"),
        ]));
        return store;
    }

    private static AskWhyService Service(
        IModelClient models, CaseCacheStore store, ReplayState replay, string photoDir, string modelCacheDir,
        ModelSpendTracker? spend = null)
    {
        var agent = new P2pAgent(new InMemoryErpAdapter(), models, new StubCompliance());
        return new AskWhyService(
            agent, models, store,
            new DockPhotoService(photoDir, UiTestSupport.NewDir("gate-ui-vision")),
            replay, UiTestLogger<AskWhyService>.Instance, modelCacheDir, spend);
    }

    [Fact]
    public async Task CachedAnswerServesWithZeroInnerCalls()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = await StoreWithDecisionAsync();
        var service = Service(models, store, new ReplayState { IsReplay = true }, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        // Warm the cache outside replay, then replay the same question.
        var warmReplay = new ReplayState { IsReplay = false };
        var warm = Service(models, store, warmReplay, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);
        inner.QueueResponse("because the evidence says so");
        var first = await warm.AskAsync("CASE-1", "D-1", "Why is this blocked?");
        Assert.True(first.Ok, first.Text);
        Assert.Equal(1, inner.CallCount);

        var second = await service.AskAsync("CASE-1", "D-1", "Why is this blocked?");

        Assert.True(second.Ok, second.Text);
        Assert.StartsWith("because the evidence says so", second.Text);
        Assert.Contains("Spend so far", second.Text);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ReplayMissRefusesLiveCall()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = await StoreWithDecisionAsync();
        var service = Service(models, store, new ReplayState { IsReplay = true }, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        var answer = await service.AskAsync("CASE-1", "D-1", "A question nobody cached?");

        Assert.False(answer.Ok);
        Assert.Contains("zero live calls", answer.Text);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task MissingDecisionSurfacesError()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var service = Service(models, store, new ReplayState(), UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        var answer = await service.AskAsync("CASE-MISSING", "D-1", "Why?");

        Assert.False(answer.Ok);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task AnswersAndErrorsCarryMeteredSpend()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = await StoreWithDecisionAsync();
        var spend = new ModelSpendTracker(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModelSpend:CostPerCallUsd"] = "0.40",
                ["ModelSpend:CapUsd"] = "7",
            })
            .Build());
        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");
        var service = Service(
            models, store, new ReplayState(), UiTestSupport.NewDir("gate-ui-dock"), cacheDir, spend);

        inner.QueueResponse("metered answer");
        var answer = await service.AskAsync("CASE-1", "D-1", "Why is this blocked?");
        var replayMiss = await Service(
                models, store, new ReplayState { IsReplay = true },
                UiTestSupport.NewDir("gate-ui-dock"), cacheDir, spend)
            .AskAsync("CASE-1", "D-1", "A question nobody cached?");

        Assert.True(answer.Ok, answer.Text);
        Assert.Contains("$0.80 of $7.00", answer.Text);
        Assert.False(replayMiss.Ok);
        Assert.Contains("zero live calls", replayMiss.Text);
        Assert.Contains("$0.80 of $7.00", replayMiss.Text);
    }
}
