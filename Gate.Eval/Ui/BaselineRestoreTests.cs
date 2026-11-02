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
        try
        {
            Environment.SetEnvironmentVariable("GATE_CASE_CACHE", null);
            var root = NewDir();
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
