// Minted dock-view sidecar: builds record their image files per case,
// merges accumulate across rebuilds and reshots, and the sidecar is
// never listed as a case. No live calls.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class DockViewsTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-dockviews", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void RecordMergesAcrossMints()
    {
        var dir = NewDir();
        CaseStore.RecordDockViews(dir, "CASE-1", "ASN-1", ["ASN-1-SCENE-01.jpg"]);
        CaseStore.RecordDockViews(dir, "CASE-1", "ASN-1", ["ASN-1-SCENE-02.jpg", "ASN-1-SCENE-01.jpg"]);

        var reread = CaseStore.LoadDockViews(dir, "CASE-1");
        Assert.NotNull(reread);
        Assert.Equal("CASE-1", reread.CaseId);
        Assert.Equal("ASN-1", reread.AsnNumber);
        Assert.Equal(
            ["ASN-1-SCENE-01.jpg", "ASN-1-SCENE-02.jpg"],
            reread.Images.ToList());
        Assert.Null(CaseStore.LoadDockViews(dir, "CASE-MISSING"));
    }

    [Fact]
    public void RecordSkipsNamesWithDirectories()
    {
        var dir = NewDir();
        CaseStore.RecordDockViews(
            dir, "CASE-1", "ASN-1",
            ["ASN-1-SCENE-04.jpg", "../IMG-04.jpg", "sub/evil.jpg", "  ", "back\\slash.jpg"]);

        var reread = CaseStore.LoadDockViews(dir, "CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(["ASN-1-SCENE-04.jpg"], reread.Images.ToList());
    }

    [Fact]
    public void ViewsSidecarExcludedFromCaseList()
    {
        var dir = NewDir();
        var now = DateTimeOffset.UtcNow;
        CaseStore.Save(dir, new CaseFile(
            new Case("CASE-1", "SO-1", "ITEM-1", "dockviews-test", CaseStage.Created, now, now,
                null, null, null, null, [], [], null, null),
            [], []));
        CaseStore.RecordDockViews(dir, "CASE-1", "ASN-1", ["ASN-1-SCENE-01.jpg"]);

        Assert.Equal("CASE-1", Assert.Single(CaseStore.ListCaseIds(dir)));
    }
}
