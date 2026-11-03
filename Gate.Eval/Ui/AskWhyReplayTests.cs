// Ask-why plus offline answers: cached answers serve with zero inner
// calls, offline mode refuses a cache miss instead of calling live, every
// answer renders the model text verbatim (no spend line), and the
// evidence carries the ERP document chain plus the consequence preview.
// No live calls.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class AskWhyReplayTests
{
    private sealed class StubCompliance : IComplianceEngine
    {
        public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
            VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
            ProposedAward award, IReadOnlyList<VolumeHistory> history,
            IReadOnlyList<OpenOrderQty>? openOrders = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Verdict>>([]);

        public Task<VendorHeadroom> HeadroomAsync(
            VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
            decimal requestedQty, IReadOnlyList<VolumeHistory> history,
            IReadOnlyList<VolumeHistory> openOrders, int cartonSize,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Ask-why replay never reads headroom.");

        public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
            PrePayState state, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Verdict>>([]);
    }

    private sealed class RecordingModelClient : IModelClient
    {
        public string ModelName => "recording";
        public ModelRequest? LastRequest { get; private set; }

        public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ModelResult("Recorded answer."));
        }
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
        IModelClient models, IErpAdapter erp, CaseCacheStore store, ReplayState replay,
        string photoDir, string modelCacheDir)
    {
        var agent = new P2pAgent(
            erp is CompanyInfoErpDouble d ? d.Inner : erp, models, new StubCompliance());
        return new AskWhyService(
            agent, models, erp, store,
            new DockPhotoService(photoDir, UiTestSupport.NewDir("gate-ui-vision")),
            replay, UiTestLogger<AskWhyService>.Instance, modelCacheDir);
    }

    [Fact]
    public async Task CachedAnswerServesWithZeroInnerCalls()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var erp = new CompanyInfoErpDouble(new InMemoryErpAdapter());
        var store = await StoreWithDecisionAsync();
        var service = Service(models, erp, store, new ReplayState { IsReplay = true }, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        // Warm the cache outside offline mode, then replay the same question.
        var warm = Service(models, erp, store, new ReplayState { IsReplay = false }, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);
        inner.QueueResponse("because the evidence says so");
        var first = await warm.AskAsync("CASE-1", "D-1", "Why is this blocked?");
        Assert.True(first.Ok, first.Text);
        Assert.Equal(1, inner.CallCount);

        var second = await service.AskAsync("CASE-1", "D-1", "Why is this blocked?");

        Assert.True(second.Ok, second.Text);
        Assert.Equal("because the evidence says so", second.Text);
        Assert.DoesNotContain("Spend", second.Text);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task ReplayMissRefusesLiveCall()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = await StoreWithDecisionAsync();
        var service = Service(
            models, new CompanyInfoErpDouble(new InMemoryErpAdapter()), store,
            new ReplayState { IsReplay = true }, UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        var answer = await service.AskAsync("CASE-1", "D-1", "A question nobody cached?");

        Assert.False(answer.Ok);
        Assert.Contains("Offline answers are on", answer.Text);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task MissingCaseSurfacesNotFound()
    {
        var inner = new FakeModelClient();
        var cacheDir = UiTestSupport.NewDir("gate-ui-model");
        IModelClient models = new CachedModelClient(inner, cacheDir);
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var service = Service(
            models, new CompanyInfoErpDouble(new InMemoryErpAdapter()), store,
            new ReplayState(), UiTestSupport.NewDir("gate-ui-dock"), cacheDir);

        var answer = await service.AskAsync("CASE-MISSING", "D-1", "Why?");

        Assert.False(answer.Ok);
        Assert.Contains("was not found", answer.Text);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task EvidenceCarriesChainAndPreviewInPlainStyle()
    {
        var recording = new RecordingModelClient();
        var fake = new InMemoryErpAdapter();
        fake.SeedBaseCurrency("TST");
        fake.SeedAutoApproveThreshold(10m);
        fake.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        fake.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        fake.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        fake.SeedVendorPrice("V-1", "ITEM-1", 10m);
        var pr = await fake.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-1", "SO-MISSING", "ITEM-1", "LE-T", CaseStage.Created, now, now,
                pr.PrNumber, null, null, null, [], [], null, null),
            [], [UiTestSupport.PendingDecision("D-1", "CASE-1")]));
        var erp = new CompanyInfoErpDouble(fake)
        {
            Preview = new ConsequencePreview(
                "PR", pr.PrNumber,
                ["Dr 14000 / Cr 21100 500 TST"], new DateOnly(2026, 11, 30), "RUN-9",
                "approver", 0m, null, null),
        };
        var service = Service(
            recording, erp, store, new ReplayState(),
            UiTestSupport.NewDir("gate-ui-dock"), UiTestSupport.NewDir("gate-ui-model"));

        var answer = await service.AskAsync("CASE-1", "D-1", "Why is this blocked?");

        Assert.True(answer.Ok, answer.Text);
        var evidence = recording.LastRequest?.SystemPrompt ?? string.Empty;
        Assert.Contains("2 to 4 plain sentences", evidence);
        Assert.Contains("no markdown", evidence);
        Assert.Contains("Document chain:", evidence);
        Assert.Contains($"purchase requisition {pr.PrNumber}", evidence);
        Assert.Contains("sales order SO-MISSING (not found in ERP)", evidence);
        Assert.Contains("Consequence preview:", evidence);
        Assert.Contains("RUN-9", evidence);
        Assert.Contains("Next approver approver", evidence);
    }

    [Fact]
    public async Task EvidenceWithoutPreviewStillAnswersWithGap()
    {
        var recording = new RecordingModelClient();
        var store = await StoreWithDecisionAsync();
        // Bare fake: no company reads, so the preview lands as a gap.
        var service = Service(
            recording, new InMemoryErpAdapter(), store, new ReplayState(),
            UiTestSupport.NewDir("gate-ui-dock"), UiTestSupport.NewDir("gate-ui-model"));

        var answer = await service.AskAsync("CASE-1", "D-1", "Why is this blocked?");

        Assert.True(answer.Ok, answer.Text);
        Assert.Contains("not implemented by the ERP layer yet", recording.LastRequest?.SystemPrompt ?? string.Empty);
    }
}
