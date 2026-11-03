// Baseline restore: startup plus the Admin-page restore both copy the
// shipped snapshot pair back over the live stores. No loader, no model
// call: the snapshot holds the ERP database file plus the platform
// (case-cache) files, and restore wipes the live case cache first, then
// copies the snapshot files in. Published rule packs and pending memo
// submissions are lifetime-scoped too: the packs dir starts empty (the
// company base packs win until the first publication) and the memo inbox
// starts empty, so restore deletes both dirs' live files. Layout under
// the baseline dir:
//   erp.db            -> live ERP sqlite path (lane 1's helper copies it)
//   platform/*        -> live case-cache dir (every file, recursively)
// Lane split: Gate.Erp.Baseline.BaselineRestore owns the ERP database
// copy; this Gate.Web coordinator owns the paths, the platform-dir copy,
// the step report, and the startup ensure.
using Gate.Compliance;
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

    /// <summary>Live published-packs dir, resolved exactly like startup:
    /// the Compliance:PackDirectory setting, else the default, rooted at
    /// the content root.</summary>
    public static string LivePacksDir(string contentRoot, IConfiguration config) =>
        Path.GetFullPath(
            config[ComplianceEngine.PackDirectoryConfigKey]
                ?? ComplianceEngine.DefaultPackDirectory,
            contentRoot);

    /// <summary>Live memo-inbox dir, resolved exactly like memo submit:
    /// data/memo-inbox under the content root.</summary>
    public static string LiveMemoInboxDir(string contentRoot) =>
        Path.Combine(contentRoot, "data", "memo-inbox");

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
            baseline, liveErp, CaseCacheStore.ResolveDir(contentRoot), log, ct,
            ResolveImageDirOrNull(log),
            LivePacksDir(contentRoot, config),
            LiveMemoInboxDir(contentRoot)).ConfigureAwait(false);
        log.LogInformation("Baseline restored: {Steps}", string.Join(" | ", steps));
        return true;
    }

    /// <summary>Resolves the dock image dir for the restore wipe. A missing
    /// dir means there is nothing to wipe, so resolution failure logs and
    /// yields null (dock step skipped) instead of failing the restore.</summary>
    internal static string? ResolveImageDirOrNull(ILogger log)
    {
        try
        {
            return DockPhotoService.ResolveImageDir();
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning("Dock image directory not found; skipping the dock wipe: {Message}.", ex.Message);
            return null;
        }
    }

    /// <summary>Restores the snapshot pair over the live stores: validates
    /// the snapshot first, then copies the snapshot ERP file in, then wipes
    /// every file in the live case cache and copies every platform file in.
    /// The ERP copy runs before the wipe so a failed restore leaves the live
    /// stores untouched. Pure file IO; zero model calls. Returns the
    /// completed steps. A non-null dock image dir also deletes the recorded
    /// generated dock views plus the image cache (null skips that step); a
    /// non-null packs dir deletes the published pack files so the company
    /// base packs win again, and a non-null memo inbox dir deletes pending
    /// submissions (nulls skip those steps).</summary>
    public static async Task<IReadOnlyList<string>> RestoreAsync(
        string baselineDir, string liveErpPath, string liveCaseCacheDir,
        ILogger? log = null, CancellationToken ct = default, string? dockImageDir = null,
        string? packsDir = null, string? memoInboxDir = null)
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

        // Generated dock views are lifetime-scoped: sequence numbers
        // restart, so a prior lifetime's views would name this lifetime's
        // cartons while showing another shipment. Delete the recorded views
        // plus the image cache before their sidecars go with the case
        // cache. Shipped photos and anything unrecorded stay on disk; the
        // counter offers recorded files only.
        string? dockStep = null;
        if (dockImageDir is not null)
        {
            var views = await new CaseCacheStore(liveCaseCacheDir)
                .ListDockViewFilesAsync(ct).ConfigureAwait(false);
            var removed = 0;
            var imageRoot = Path.GetFullPath(dockImageDir);
            foreach (var name in views)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(name);
                if (string.IsNullOrEmpty(fileName)
                    || !string.Equals(fileName, name, StringComparison.Ordinal))
                {
                    // Minted names are bare file names; a recorded name
                    // with directories would retarget the wipe under
                    // GetFileName, so it is skipped, never reinterpreted.
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(imageRoot, fileName));
                if (!full.StartsWith(imageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                if (File.Exists(full))
                {
                    File.Delete(full);
                    removed++;
                }
            }

            var cacheCleared = 0;
            var imageCache = Path.Combine(imageRoot, ".image-cache");
            if (Directory.Exists(imageCache))
            {
                foreach (var file in Directory.GetFiles(imageCache, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Delete(file);
                    cacheCleared++;
                }
            }

            log?.LogInformation(
                "Baseline restore: removed {Removed} generated dock view(s), cleared {Cache} image-cache file(s).",
                removed, cacheCleared);
            dockStep = $"Dock counter cleared ({CountWords(removed, "generated view")} removed, " +
                $"{CountWords(cacheCleared, "image-cache file")} cleared).";
        }

        Directory.CreateDirectory(liveCaseCacheDir);
        var wiped = 0;
        foreach (var file in Directory.GetFiles(liveCaseCacheDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(file);
            wiped++;
        }

        // Published packs are lifetime-scoped: without this wipe a prior
        // lifetime's published versions would keep enforcing after the
        // restore. The company base packs win on an empty dir, so the
        // company returns exactly to opening.
        string? packsStep = null;
        if (packsDir is not null)
        {
            var removed = 0;
            if (Directory.Exists(packsDir))
            {
                foreach (var file in Directory.GetFiles(packsDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Delete(file);
                    removed++;
                }
            }

            log?.LogInformation(
                "Baseline restore: removed {Removed} published pack file(s).", removed);
            packsStep = $"Published rule packs cleared ({CountWords(removed, "published version")} removed); " +
                "the company packs are back to opening.";
        }

        string? inboxStep = null;
        if (memoInboxDir is not null)
        {
            var cleared = 0;
            if (Directory.Exists(memoInboxDir))
            {
                foreach (var file in Directory.GetFiles(memoInboxDir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Delete(file);
                    cleared++;
                }
            }

            log?.LogInformation(
                "Baseline restore: cleared {Cleared} pending memo submission(s).", cleared);
            inboxStep = $"Memo inbox cleared ({CountWords(cleared, "pending submission")} removed).";
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
        var steps = new List<string>(capacity: 5)
        {
            $"Saved ERP database restored ({new FileInfo(liveErpPath).Length} bytes).",
            $"Live workspace wiped ({CountWords(wiped, "file")} removed), {CountWords(copied, "saved file")} copied in.",
        };
        if (dockStep is not null)
        {
            steps.Add(dockStep);
        }

        if (packsStep is not null)
        {
            steps.Add(packsStep);
        }

        if (inboxStep is not null)
        {
            steps.Add(inboxStep);
        }

        return steps;
    }

    private static string CountWords(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
