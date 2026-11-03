// Dock carton-figure tests: every carton on a dock decision card renders one
// line (its failing verdicts plus rules, or the pass line) with its own
// full photo (vision reads carry no positions, so never a crop); figured
// photos are not repeated as loose images; missing photos still render
// captions. Test data is local, on an unfamiliar item.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class DecisionCardFiguresTests
{
    [Fact]
    public async Task HeldCartonGetsFigureWithPhotoAndVerdict()
    {
        var world = await WorldAsync();
        var card = world.Cards.Single(c => c.Decision.DecisionId == "D-FIG");

        Assert.Equal("SO-77", card.SoNumber);
        Assert.Equal("COIL-3", card.ItemId);
        Assert.Equal(2, card.CartonFigures.Count);
        var figure = Assert.Single(card.CartonFigures, f => f.CartonId == "CTN-1");
        Assert.Equal("IMG-1.jpg", figure.ImageFileName);
        Assert.Contains("Carton CTN-1", figure.Caption);
        Assert.Contains("BAT-LI-01", figure.Caption);
        Assert.Contains("handling mark missing", figure.Caption);
        Assert.NotNull(figure.Read);
        Assert.Contains("mark absent", figure.Read);
        var passed = Assert.Single(card.CartonFigures, f => f.CartonId == "CTN-2");
        Assert.Contains("passed the dock check", passed.Caption);
        Assert.DoesNotContain("IMG-1.jpg", card.ImageFileNames);
        Assert.DoesNotContain(card.EvidenceCaptions, c => c.Contains("IMG-1.jpg"));
    }

    [Fact]
    public async Task MissingPhotoStillRendersCaption()
    {
        var world = await WorldAsync(extraReads: true);
        var card = world.Cards.Single(c => c.Decision.DecisionId == "D-FIG");

        var figure = Assert.Single(card.CartonFigures, f => f.CartonId == "CTN-9");
        Assert.Null(figure.ImageFileName);
        Assert.Null(figure.Read);
        Assert.Contains("CTN-9", figure.Caption);
        Assert.Contains("DOCK-9", figure.Caption);
    }

    [Fact]
    public async Task NoEvidenceKeepsLooseImages()
    {
        var dir = UiTestSupport.NewDir("gate-ui-figures-none");
        var cacheDir = Path.Combine(dir, "case-cache");
        var imgDir = Path.Combine(dir, "img");
        Directory.CreateDirectory(imgDir);
        await File.WriteAllBytesAsync(Path.Combine(imgDir, "IMG-7.jpg"), [7, 7, 7]);
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-F7", "SO-77", "COIL-3", "ui-test", CaseStage.DockChecked, now, now,
                null, null, null, "RCT-7", [], [], null, null),
            [],
            [
                new Decision("D-7", "CASE-F7", DecisionKind.DockReview, "Review dock", DecisionStatus.Pending,
                    "clerk", now, null, null, "Receipt", "RCT-7", null, null, null, null, ["IMG-7"], null),
            ]));
        var views = new CaseViewService(
            SeededErp.Create(), new CaseCacheStore(cacheDir),
            new DockPhotoService(imgDir, Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance);

        var card = (await views.ListDecisionCardsAsync()).Single();

        Assert.Empty(card.CartonFigures);
        Assert.Equal(["IMG-7.jpg"], card.ImageFileNames);
    }

    private sealed record World(IReadOnlyList<Gate.Web.ViewModels.DecisionCardVm> Cards);

    private static async Task<World> WorldAsync(bool extraReads = false)
    {
        var dir = UiTestSupport.NewDir("gate-ui-figures");
        var cacheDir = Path.Combine(dir, "case-cache");
        var imgDir = Path.Combine(dir, "img");
        var readDir = Path.Combine(dir, "reads");
        Directory.CreateDirectory(imgDir);
        var bytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(imgDir, "IMG-1.jpg"), bytes);
        new VisionReadCache(readDir).Write(
            VisionReadCache.Sha256Hex(bytes), VisionReadCache.CartonReadV1, "m", "CTN-1: mark absent.");
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-F1", "SO-77", "COIL-3", "ui-test", CaseStage.DockChecked, now, now,
                null, null, null, "RCT-1", [], [], null, null),
            [],
            [
                new Decision("D-FIG", "CASE-F1", DecisionKind.DockReview, "Review dock", DecisionStatus.Pending,
                    "clerk", now, null, null, "Receipt", "RCT-1", null, null, null, null, ["IMG-1"], null),
            ]));
        var reads = new List<DockCartonRead>
        {
            new("CTN-1", "COIL-3", "PO-1", "LOT-1", 5, false, false, null,
                false, false, false, null, "IMG-1", 0.9m),
            new("CTN-2", "COIL-3", "PO-1", "LOT-1", 5, true, true, null,
                false, false, false, null, "IMG-1", 0.9m),
        };
        var verdicts = new Dictionary<string, IReadOnlyList<Verdict>>(StringComparer.Ordinal)
        {
            ["CTN-1"] = [new Verdict("BAT-LI-01", VerdictKind.Hold, "handling mark missing", "pack")],
            ["CTN-2"] = [new Verdict("BAT-LI-01", VerdictKind.Pass, "ok", "pack")],
        };
        if (extraReads)
        {
            reads.Add(new DockCartonRead(
                "CTN-9", "COIL-3", "PO-1", "LOT-9", 5, true, true, null,
                false, false, true, "dented", "IMG-MISSING", 0.9m));
            verdicts["CTN-9"] = [new Verdict("DOCK-9", VerdictKind.HumanReview, "needs a human look", "pack")];
        }

        CaseStore.SaveDock(cacheDir, new DockEvidence("CASE-F1", reads, verdicts, now));
        var views = new CaseViewService(
            SeededErp.Create(), new CaseCacheStore(cacheDir),
            new DockPhotoService(imgDir, readDir),
            UiTestLogger<CaseViewService>.Instance);
        return new World(await views.ListDecisionCardsAsync());
    }
}
