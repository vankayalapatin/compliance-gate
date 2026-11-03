// Case-view service tests: decision cards, case detail, and receipt
// media. Cards carry the bank-verified wording plus dock evidence from
// the persisted per-carton sidecar, case detail resolves mail plus vendor
// contacts, and receipt media renders from the linked case's dock
// evidence with vision-cache captions as the fallback. Temp ERP plus
// temp cache only — never company-data numbers. No live calls.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class CaseViewTests
{
    private static CaseViewService Service(InMemoryErpAdapter erp, CaseCacheStore store) =>
        new(erp, store,
            new DockPhotoService(UiTestSupport.NewDir("gate-ui-dock"), UiTestSupport.NewDir("gate-ui-vision")),
            UiTestLogger<CaseViewService>.Instance);

    private static DockCartonRead Read(string carton, string image) =>
        new(carton, "ITEM-T", "PO-1", null, 1, true, true, null, false, false, false, null, image, 0.9m);

    private static Verdict HoldVerdict() =>
        new("CORP-T", VerdictKind.Hold, "test hold", RecordSource.P2pAgent);

    private static void SaveDockEvidence(string cacheDir, string caseId)
    {
        var read = Read("CTN-1", "IMG-1.jpg");
        CaseStore.SaveDock(cacheDir, new DockEvidence(
            caseId, [read],
            new Dictionary<string, IReadOnlyList<Verdict>>
            {
                ["CTN-1"] = [new Verdict("DOCK-T", VerdictKind.Pass, "ok", RecordSource.P2pAgent)],
            },
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task CardsAndDetailResolveWithoutScenario()
    {
        var dir = UiTestSupport.NewDir("gate-ui-case");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1"),
        ]));
        var service = Service(new InMemoryErpAdapter(), store);

        var cards = await service.ListDecisionCardsAsync();
        var detail = await service.GetCaseDetailAsync("CASE-1");

        var card = Assert.Single(cards);
        Assert.Equal("D-1", card.Decision.DecisionId);
        Assert.NotNull(card.ErpSummary);
        Assert.Contains("PR-TEST", card.ErpSummary);
        Assert.NotNull(detail);
        Assert.Equal("CASE-1", detail.Case.CaseId);
    }

    [Fact]
    public async Task CardsBankWordingNeverPrintsBooleans()
    {
        var dir = UiTestSupport.NewDir("gate-ui-case");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision(
                "D-BANK", "CASE-1", DecisionKind.BankVerification,
                "VendorBank", "V-9"),
        ]));
        var service = Service(new InMemoryErpAdapter(), store);

        var cards = await service.ListDecisionCardsAsync();

        var card = Assert.Single(cards);
        Assert.NotNull(card.ErpSummary);
        Assert.DoesNotContain("True", card.ErpSummary);
        Assert.DoesNotContain("False", card.ErpSummary);
        Assert.DoesNotContain("verified=", card.ErpSummary);
    }

    [Fact]
    public async Task DockDecisionCardsCarryPersistedEvidence()
    {
        var dir = UiTestSupport.NewDir("gate-ui-case");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision(
                "D-DOCK", "CASE-1", DecisionKind.DockReview, "ASN", "ASN-1"),
        ]));
        SaveDockEvidence(dir, "CASE-1");
        var service = Service(new InMemoryErpAdapter(), store);

        var cards = await service.ListDecisionCardsAsync();

        var card = Assert.Single(cards);
        var figure = Assert.Single(card.CartonFigures);
        Assert.Contains("CTN-1", figure.Caption);
        Assert.Contains("passed the dock check", figure.Caption);
    }

    [Fact]
    public async Task NonDockCardsSkipDockEvidence()
    {
        var dir = UiTestSupport.NewDir("gate-ui-case");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1"),
        ]));
        SaveDockEvidence(dir, "CASE-1");
        var service = Service(new InMemoryErpAdapter(), store);

        var cards = await service.ListDecisionCardsAsync();

        var card = Assert.Single(cards);
        Assert.DoesNotContain(card.EvidenceCaptions, c => c.Contains("CTN-1"));
    }

    [Fact]
    public async Task MissingCaseYieldsNullDetail()
    {
        var service = Service(
            new InMemoryErpAdapter(),
            new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache")));

        Assert.Null(await service.GetCaseDetailAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task ReceiptMediaRendersFromDockEvidence()
    {
        var dir = UiTestSupport.NewDir("gate-ui-case");
        var store = new CaseCacheStore(dir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-1", "SO-1", "ITEM-1", "LE-T", CaseStage.DockChecked, now, now,
                null, "PO-1", null, "RCP-1", [], [], null, null),
            [], []));
        CaseStore.SaveDock(dir, new DockEvidence(
            "CASE-1", [Read("CTN-1", "IMG-1.jpg")],
            new Dictionary<string, IReadOnlyList<Verdict>> { ["CTN-1"] = [HoldVerdict()] },
            now));
        var service = Service(new InMemoryErpAdapter(), store);
        var receipt = new ShipmentReceipt(
            "RCP-1", "PO-1", "PACK-1", ReceiptStatus.Posted, RecordSource.P2pAgent,
            "requester", "key-1", [], now);

        var media = await service.GetReceiptMediaAsync(receipt, null);

        Assert.Contains(media.CachedReads, r => r.Contains("CTN-1") && r.Contains("CORP-T"));
        // Vision-cache captions stay as the fallback line for the photo.
        Assert.Contains(media.CachedReads, r => r.Contains("IMG-1.jpg"));
    }

    [Fact]
    public async Task ReceiptMediaWithoutCaseSaysSo()
    {
        var service = Service(
            new InMemoryErpAdapter(),
            new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache")));
        var receipt = new ShipmentReceipt(
            "RCP-X", "PO-X", "PACK-X", ReceiptStatus.Posted, RecordSource.P2pAgent,
            "requester", "key-2", [], DateTimeOffset.UtcNow);

        var media = await service.GetReceiptMediaAsync(receipt, null);

        Assert.Empty(media.ImageFileNames);
        Assert.Contains(media.CachedReads, r => r.Contains("No case links this receipt"));
    }

    [Fact]
    public async Task DetailChainMarksCurrentAndSummarizes()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        var service = Service(new InMemoryErpAdapter(), store);

        var detail = await service.GetCaseDetailAsync("CASE-1");

        Assert.NotNull(detail);
        Assert.Equal(7, detail.Chain.Count);
        var current = Assert.Single(detail.Chain, s => s.IsCurrent);
        Assert.Equal(CaseChain.SoLabel, current.Label);
        Assert.Contains("SO-1", current.DocumentNumber);
        Assert.Equal("missing", current.Status);
    }

    [Fact]
    public void ActorAndCommentLabelsHideLoaderEraValues()
    {
        Assert.Equal("—", ActorLabels.Display(null));
        Assert.Equal("—", ActorLabels.Display("system"));
        Assert.Equal("—", ActorLabels.Display(" System "));
        Assert.Equal("approver", ActorLabels.Display("approver"));
        Assert.Equal("—", CommentLabels.Display(null));
        Assert.Equal("—", CommentLabels.Display("scenario seed"));
        Assert.Equal("looks good", CommentLabels.Display("looks good"));
    }
}
