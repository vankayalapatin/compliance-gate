// Baseline restore: startup plus the Admin-page restore both copy the
// shipped snapshot pair back over the live stores. No loader, no model
// call: the snapshot holds the ERP database file plus the platform
// (case-cache) files, and restore wipes the live case cache first, then
// copies the snapshot files in. Layout under the baseline dir:
//   erp.db            -> live ERP sqlite path (lane 1's helper copies it)
//   platform/*        -> live case-cache dir (every file, recursively)
// Lane split: Gate.Erp.Baseline.BaselineRestore owns the ERP database
// copy; this Gate.Web coordinator owns the paths, the platform-dir copy,
// the step report, and the startup ensure.
using Gate.Web.Services;

namespace Gate.Web;

public static class BaselineRestore
{
    public const string BaselineErpFileName = "erp.db";

    public const string BaselinePlatformDirName = "platform";

    /// <summary>Live ERP sqlite path. The legacy Scenario:ErpSqlite key is
    /// honored during migration; Erp:Sqlite plus GATE_ERP_SQLITE win.</summary>
    public static string LiveErpPath(IConfiguration config) =>
        config["Erp:Sqlite"]
        ?? config["Scenario:ErpSqlite"]
        ?? Environment.GetEnvironmentVariable("GATE_ERP_SQLITE")
        ?? "data/erp.db";

    /// <summary>Shipped snapshot directory. Absolute when configured so;
    /// otherwise resolved under the content root.</summary>
    public static string BaselineDir(string contentRoot, IConfiguration config)
    {
        var configured = config["Baseline:Dir"]
            ?? Environment.GetEnvironmentVariable("GATE_BASELINE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(contentRoot, configured));
        }

        return Path.Combine(contentRoot, "data", "baseline");
    }

    /// <summary>Startup restore: when the live ERP database file is missing
    /// and the snapshot holds one, wipes the live case cache and copies the
    /// snapshot files into the live paths. Returns true when it restored.
    /// A missing snapshot is not an error: the app starts empty.</summary>
    public static async Task<bool> EnsureAsync(
        string contentRoot, IConfiguration config, ILogger log,
        CancellationToken ct = default)
    {
        var liveErp = Path.GetFullPath(LiveErpPath(config));
        if (File.Exists(liveErp))
        {
            return false;
        }

        var baseline = BaselineDir(contentRoot, config);
        var snapshotErp = Path.Combine(baseline, BaselineErpFileName);
        if (!File.Exists(snapshotErp))
        {
            log.LogInformation(
                "No live ERP database at {Live} and no snapshot at {Snapshot}; starting empty.",
                liveErp, snapshotErp);
            return false;
        }

        var steps = await RestoreAsync(
            baseline, liveErp, CaseCacheStore.ResolveDir(contentRoot), log, ct).ConfigureAwait(false);
        log.LogInformation("Baseline restored: {Steps}", string.Join(" | ", steps));
        return true;
    }

    /// <summary>Restores the snapshot pair over the live stores: validates
    /// the snapshot first, then copies the snapshot ERP file in, then wipes
    /// every file in the live case cache and copies every platform file in.
    /// The ERP copy runs before the wipe so a failed restore leaves the live
    /// stores untouched. Pure file IO; zero model calls. Returns the
    /// completed steps.</summary>
    public static async Task<IReadOnlyList<string>> RestoreAsync(
        string baselineDir, string liveErpPath, string liveCaseCacheDir,
        ILogger? log = null, CancellationToken ct = default)
    {
        var snapshotErp = Path.Combine(baselineDir, BaselineErpFileName);
        if (!File.Exists(snapshotErp))
        {
            throw new InvalidOperationException(
                $"Saved opening data not found: {snapshotErp} is missing.");
        }

        var snapshotPlatform = Path.Combine(baselineDir, BaselinePlatformDirName);
        var platformFiles = Directory.Exists(snapshotPlatform)
            ? Directory.GetFiles(snapshotPlatform, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

        Gate.Erp.Baseline.BaselineRestore.Restore(baselineDir, liveErpPath, liveCaseCacheDir);

        Directory.CreateDirectory(liveCaseCacheDir);
        var wiped = 0;
        foreach (var file in Directory.GetFiles(liveCaseCacheDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(file);
            wiped++;
        }

        var copied = 0;
        foreach (var file in platformFiles)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(snapshotPlatform, file);
            var target = Path.Combine(liveCaseCacheDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            copied++;
        }

        log?.LogInformation(
            "Baseline restore: wiped {Wiped} live file(s), copied the saved ERP database plus {Copied} platform file(s).",
            wiped, copied);
        await Task.CompletedTask.ConfigureAwait(false);
        return
        [
            $"Saved ERP database restored ({new FileInfo(liveErpPath).Length} bytes).",
            $"Live workspace wiped ({CountWords(wiped, "file")} removed), {CountWords(copied, "saved file")} copied in.",
        ];
    }

    private static string CountWords(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
