// Baseline-restore tests: startup plus the Admin restore copy the
// snapshot pair (ERP database plus platform files) over the live stores,
// wiping the live workspace first. A missing snapshot refuses loudly on
// restore and starts empty on startup. Temp dirs only, zero live calls.
using Gate.Erp;
using Gate.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

[CollectionDefinition(RestoreEnvCollection.Name, DisableParallelization = true)]
public sealed class RestoreEnvCollection
{
    public const string Name = "RestoreEnv";
}

[Collection(RestoreEnvCollection.Name)]
public sealed class BaselineRestoreTests
{
    private static string NewDir() => UiTestSupport.NewDir("gate-ui-baseline");

    private static string WriteSnapshot(string root)
    {
        var baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(Path.Combine(baseline, "platform"));
        File.WriteAllBytes(Path.Combine(baseline, "erp.db"), [9, 9]);
        File.WriteAllText(Path.Combine(baseline, "platform", "CASE-T.json"), """{"saved":true}""");
        return baseline;
    }

    private static IConfiguration Config(string baseline, string liveErp) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Baseline:Dir"] = baseline,
                ["Erp:Sqlite"] = liveErp,
            })
            .Build();

    [Fact]
    public async Task RestoreWipesLiveThenCopiesSnapshotIn()
    {
        var root = NewDir();
        var baseline = WriteSnapshot(root);
        var liveErp = Path.Combine(root, "live", "erp.db");
        var liveCache = Path.Combine(root, "case-cache");
        Directory.CreateDirectory(liveCache);
        await File.WriteAllTextAsync(Path.Combine(liveCache, "STALE.json"), "{}");

        var steps = await BaselineRestore.RestoreAsync(
            baseline, liveErp, liveCache, UiTestLogger<BaselineRestoreTests>.Instance);

        Assert.Equal(2, steps.Count);
        Assert.Equal(new byte[] {9, 9}, await File.ReadAllBytesAsync(liveErp));
        Assert.True(File.Exists(Path.Combine(liveCache, "CASE-T.json")));
        Assert.False(File.Exists(Path.Combine(liveCache, "STALE.json")));
    }

    [Fact]
    public async Task RestoreDeletesRecordedDockViewsAndClearsImageCache()
    {
        // The prior lifetime's counter: one recorded view, one unrecorded
        // same-shipment view, one shipped photo, a two-file image cache,
        // and a traversal-shaped recorded name that must never retarget.
        var root = NewDir();
        var baseline = WriteSnapshot(root);
        var liveErp = Path.Combine(root, "live", "erp.db");
        var liveCache = Path.Combine(root, "case-cache");
        Directory.CreateDirectory(liveCache);
        Gate.Agent.CaseStore.RecordDockViews(
            liveCache, "CASE-T", "ASN-T",
            ["ASN-T-SCENE-04.jpg", "../IMG-04.jpg"]);
        var dockDir = Path.Combine(root, "dock");
        Directory.CreateDirectory(dockDir);
        await File.WriteAllBytesAsync(Path.Combine(dockDir, "ASN-T-SCENE-04.jpg"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(dockDir, "ASN-T-SCENE-01.jpg"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(dockDir, "IMG-04.jpg"), [7, 8, 9]);
        var imageCache = Path.Combine(dockDir, ".image-cache");
        Directory.CreateDirectory(Path.Combine(imageCache, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(imageCache, "a.jpg"), [10]);
        await File.WriteAllBytesAsync(Path.Combine(imageCache, "nested", "b.jpg"), [11]);

        var steps = await BaselineRestore.RestoreAsync(
            baseline, liveErp, liveCache, UiTestLogger<BaselineRestoreTests>.Instance,
            ct: default, dockImageDir: dockDir);

        Assert.Equal(3, steps.Count);
        Assert.Contains(steps, s => s.Contains("Dock counter cleared", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("1 generated view", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("2 image-cache files", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(dockDir, "ASN-T-SCENE-04.jpg")));
        Assert.True(File.Exists(Path.Combine(dockDir, "ASN-T-SCENE-01.jpg")));
        Assert.True(File.Exists(Path.Combine(dockDir, "IMG-04.jpg")));
        Assert.Empty(Directory.GetFiles(imageCache, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(liveCache, "CASE-T.json")));
    }

    [Fact]
    public async Task RestoreClearsPublishedPacksMemoFilesAndInbox()
    {
        // A prior lifetime published CORP v3 and left a memo plus a pending
        // submission behind. Restore must clear the published pack files
        // (the company base packs win on an empty dir), wipe the memo
        // files back to the snapshot, and clear the inbox, so the company
        // returns exactly to CORP v2 at 60%.
        var root = NewDir();
        var baseline = WriteSnapshot(root);
        var liveErp = Path.Combine(root, "live", "erp.db");
        var liveCache = Path.Combine(root, "case-cache");
        Directory.CreateDirectory(liveCache);
        await File.WriteAllTextAsync(Path.Combine(liveCache, "MEMO-9.memo.json"), "{}");
        var packsDir = Path.Combine(root, "packs");
        Directory.CreateDirectory(packsDir);
        await File.WriteAllTextAsync(
            Path.Combine(packsDir, "corp.v3.json"), Gate.Eval.MemoFixtures.CorpPackJson("v3", "0.40"));
        await File.WriteAllTextAsync(Path.Combine(packsDir, "notes.txt"), "not a pack");
        var inboxDir = Path.Combine(root, "memo-inbox");
        Directory.CreateDirectory(inboxDir);
        await File.WriteAllTextAsync(Path.Combine(inboxDir, "MEMO-9.json"), "{}");

        var before = Gate.Compliance.RulePackLoader.LoadCurrent(packsDir);
        Assert.Equal("v3", before.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        Assert.Equal(0.40m, before.Corp.OriginCap);

        var steps = await BaselineRestore.RestoreAsync(
            baseline, liveErp, liveCache, UiTestLogger<BaselineRestoreTests>.Instance,
            ct: default, packsDir: packsDir, memoInboxDir: inboxDir);

        Assert.Contains(steps, s => s.Contains("Published rule packs cleared", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("1 published version", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Contains("Memo inbox cleared", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(packsDir, "corp.v3.json")));
        Assert.True(File.Exists(Path.Combine(packsDir, "notes.txt")));
        Assert.Empty(Directory.GetFiles(inboxDir));
        Assert.False(File.Exists(Path.Combine(liveCache, "MEMO-9.memo.json")));
        Assert.True(File.Exists(Path.Combine(liveCache, "CASE-T.json")));

        var after = Gate.Compliance.RulePackLoader.LoadCurrent(packsDir);
        Assert.Equal("v2", after.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        Assert.Equal(0.60m, after.Corp.OriginCap);
    }

    [Fact]
    public async Task RestoreWorksWhileAppHoldsLiveDb()
    {
        // Mimics the running app: a real sqlite live db with pooled
        // connections left behind undisposed-cleared, exactly the state that
        // failed File.Copy with "file is being used by another process".
        // Restore must still bring back both the ERP db and the platform files.
        var root = NewDir();
        var baseline = Path.Combine(root, "baseline");
        Directory.CreateDirectory(Path.Combine(baseline, "platform"));
        var liveErp = Path.Combine(root, "live", "erp.db");
        Directory.CreateDirectory(Path.Combine(root, "live"));
        var liveCache = Path.Combine(root, "case-cache");
        Directory.CreateDirectory(liveCache);
        await File.WriteAllTextAsync(Path.Combine(liveCache, "STALE.json"), "{}");
        File.WriteAllText(Path.Combine(baseline, "platform", "CASE-T.json"), """{"saved":true}""");

        await using (var snapshot = MakeDb(Path.Combine(baseline, "erp.db")))
        {
            snapshot.Config.Add(new ConfigRow { Key = "origin", Value = "snapshot" });
            await snapshot.SaveChangesAsync();
        }
        var live = MakeDb(liveErp);
        live.Config.Add(new ConfigRow { Key = "origin", Value = "live" });
        await live.SaveChangesAsync();
        _ = await live.Config.CountAsync();
        await live.DisposeAsync();
        try
        {
            var steps = await BaselineRestore.RestoreAsync(
                baseline, liveErp, liveCache, UiTestLogger<BaselineRestoreTests>.Instance);

            Assert.Equal(2, steps.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        await using var reread = MakeDb(liveErp);
        Assert.Equal("snapshot", (await reread.Config.SingleAsync(c => c.Key == "origin")).Value);
        Assert.True(File.Exists(Path.Combine(liveCache, "CASE-T.json")));
        Assert.False(File.Exists(Path.Combine(liveCache, "STALE.json")));
        SqliteConnection.ClearAllPools();
    }

    private static ErpDbContext MakeDb(string file)
    {
        var db = new ErpDbContext(
            new DbContextOptionsBuilder<ErpDbContext>().UseSqlite($"Data Source={file}").Options);
        db.Database.Migrate();
        return db;
    }

    [Fact]
    public async Task RestoreRefusesMissingSnapshot()
    {
        var root = NewDir();
        Directory.CreateDirectory(root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BaselineRestore.RestoreAsync(
                Path.Combine(root, "baseline"),
                Path.Combine(root, "live", "erp.db"),
                Path.Combine(root, "case-cache")));

        Assert.Contains("Saved opening data not found", ex.Message);
    }

    [Fact]
    public async Task EnsureRestoresOnlyWhenLiveIsMissing()
    {
        var previousCache = Environment.GetEnvironmentVariable("GATE_CASE_CACHE");
        var previousDock = Environment.GetEnvironmentVariable("GATE_DOCK_IMAGES");
        try
        {
            Environment.SetEnvironmentVariable("GATE_CASE_CACHE", null);
            var root = NewDir();
            // The ensure wipes the image cache through env resolution; pin
            // it at temp so the suite never clears the real dock counter.
            Environment.SetEnvironmentVariable("GATE_DOCK_IMAGES", Path.Combine(root, "dock"));
            var baseline = WriteSnapshot(root);
            var liveErp = Path.Combine(root, "live", "erp.db");
            var config = Config(baseline, liveErp);
            ILogger log = UiTestLogger<BaselineRestoreTests>.Instance;

            Assert.True(await BaselineRestore.EnsureAsync(root, config, log));
            Assert.True(File.Exists(liveErp));

            await File.WriteAllBytesAsync(liveErp, [1]);
            Assert.False(await BaselineRestore.EnsureAsync(root, config, log));
            Assert.Equal(new byte[] {1}, await File.ReadAllBytesAsync(liveErp));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GATE_CASE_CACHE", previousCache);
            Environment.SetEnvironmentVariable("GATE_DOCK_IMAGES", previousDock);
        }
    }

    [Fact]
    public async Task EnsureStartsEmptyWithoutSnapshot()
    {
        var root = NewDir();
        Directory.CreateDirectory(root);
        var config = Config(Path.Combine(root, "baseline"), Path.Combine(root, "live", "erp.db"));

        Assert.False(await BaselineRestore.EnsureAsync(
            root, config, UiTestLogger<BaselineRestoreTests>.Instance));
        Assert.False(File.Exists(Path.Combine(root, "live", "erp.db")));
    }

    [Fact]
    public void PathsResolveFromConfig()
    {
        var previousBaseline = Environment.GetEnvironmentVariable("GATE_BASELINE");
        var previousSqlite = Environment.GetEnvironmentVariable("GATE_ERP_SQLITE");
        try
        {
            Environment.SetEnvironmentVariable("GATE_BASELINE", null);
            Environment.SetEnvironmentVariable("GATE_ERP_SQLITE", null);
            var config = Config("/tmp/base", "/tmp/live/erp.db");

            Assert.Equal("/tmp/base", BaselineRestore.BaselineDir("/content", config));
            Assert.Equal("/tmp/live/erp.db", BaselineRestore.LiveErpPath(config));
            Assert.Equal("data/erp.db", BaselineRestore.LiveErpPath(new ConfigurationBuilder().Build()));
            Assert.Equal(
                Path.Combine("/content", "data", "baseline"),
                BaselineRestore.BaselineDir("/content", new ConfigurationBuilder().Build()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GATE_BASELINE", previousBaseline);
            Environment.SetEnvironmentVariable("GATE_ERP_SQLITE", previousSqlite);
        }
    }
}
