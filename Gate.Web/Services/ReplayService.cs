// Restore-to-opening plus explicit offline answers behind the Admin-page
// controls. Restore wipes the live workspace and copies the shipped
// snapshot pair (ERP database plus platform files) back in; it runs no
// loader and no model call, and never touches the answers mode, so a
// restore while live stays live. Offline answers is a separate explicit
// switch: entering it pins Ask-why to cached answers so the rest of the
// session makes zero live model calls. Every step reports current spend.
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class ReplayService
{
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ReplayState _replay;
    private readonly IConfiguration _config;
    private readonly ILogger<ReplayService> _log;
    private readonly string _contentRoot;
    private readonly ModelSpendTracker _spend;

    public ReplayService(
        CaseCacheStore cases,
        DockPhotoService photos,
        ReplayState replay,
        IConfiguration config,
        ILogger<ReplayService> log,
        string contentRoot,
        ModelSpendTracker? spend = null)
    {
        _cases = cases;
        _photos = photos;
        _replay = replay;
        _config = config;
        _log = log;
        _contentRoot = contentRoot;
        _spend = spend ?? new ModelSpendTracker(config);
    }

    /// <summary>Enters offline-answers mode explicitly: Ask-why answers from
    /// the model cache only and refuses to place a live call.</summary>
    public void EnterReplay()
    {
        _replay.IsReplay = true;
        _log.LogInformation(
            "Offline-answers mode on: Ask-why answers from the model cache only (zero live calls).");
    }

    /// <summary>Leaves offline-answers mode explicitly: live model calls allowed.</summary>
    public void ExitReplay()
    {
        _replay.IsReplay = false;
        _log.LogInformation("Offline-answers mode off: live model calls allowed.");
    }

    /// <summary>Restores the saved opening data and reports each completed
    /// step as it lands so the Admin page can show movement; the returned
    /// report carries the full result. Restore never switches the answers
    /// mode: the report's IsReplay echoes the unchanged mode. Zero live
    /// calls happen in here: the snapshot copy, cache counts, and the
    /// spend readout are all local.</summary>
    public async Task<ReplayReport> RestoreAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        static string CountWords(int count, string one, string? many = null) =>
            count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

        void Step(string text)
        {
            steps.Add(text);
            progress?.Report(text);
        }

        try
        {
            var restored = await BaselineRestore.RestoreAsync(
                BaselineRestore.BaselineDir(_contentRoot, _config),
                Path.GetFullPath(BaselineRestore.LiveErpPath(_config)),
                _cases.CacheDir,
                _log,
                ct,
                BaselineRestore.ResolveImageDirOrNull(_log)).ConfigureAwait(false);
            foreach (var step in restored)
            {
                Step(step);
            }

            var modelCache = ModelCachePaths.ResolveDir();
            var modelEntries = Directory.Exists(modelCache) ? Directory.GetFiles(modelCache, "*.json").Length : 0;
            Step($"Model cache holds {CountWords(modelEntries, "entry", "entries")}; vision cache holds {CountWords(_photos.CachedReadCount(), "read")}.");
            if (modelEntries == 0)
            {
                warnings.Add("Model cache is empty; Ask-why will refuse every question until answers are cached outside offline-answers mode.");
            }

            Step($"Spend so far: {_spend.Summary}.");
            return new ReplayReport(DateTimeOffset.UtcNow, _replay.IsReplay, steps, warnings, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed.");
            return new ReplayReport(DateTimeOffset.UtcNow, _replay.IsReplay, steps, warnings,
                $"Restore failed: {ex.Message}");
        }
    }
}
