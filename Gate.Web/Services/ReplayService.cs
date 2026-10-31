// Reset-to-start plus offline replay behind the Decisions reset section. Reset wipes
// and reloads the ERP scenario (every number regenerated) and clears the
// case cache; replay mode then pins Ask-why to cached answers so the rest
// of the session makes zero live model calls. Agent re-execution stays in
// the agent lane; this service restores start state and reports each step.
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class ReplayService
{
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ReplayState _replay;
    private readonly IConfiguration _config;
    private readonly ILogger<ReplayService> _log;

    public ReplayService(
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ReplayState replay,
        IConfiguration config,
        ILogger<ReplayService> log)
    {
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _replay = replay;
        _config = config;
        _log = log;
    }

    public async Task<ReplayReport> ResetAndReplayAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        try
        {
            var manifest = await ScenarioLoader.ResetAsync(
                _erp,
                ScenarioEndpoints.ScenarioPath(_config),
                ScenarioEndpoints.SqlitePath(_config),
                ScenarioEndpoints.ManifestPath(_config),
                ct).ConfigureAwait(false);
            steps.Add($"Scenario {manifest.Scenario} reloaded; {manifest.Documents.Count} document(s) regenerated.");

            var cleared = await _cases.ClearAsync(ct).ConfigureAwait(false);
            steps.Add($"Case cache cleared ({cleared} file(s) removed).");

            _replay.IsReplay = true;
            steps.Add("Replay mode on: Ask-why answers from the model cache only (zero live calls).");

            var modelCache = ModelCachePaths.ResolveDir();
            var modelEntries = Directory.Exists(modelCache) ? Directory.GetFiles(modelCache, "*.json").Length : 0;
            steps.Add($"Model cache holds {modelEntries} entr(y/ies); vision cache holds {_photos.CachedReadCount()} read(s).");
            if (modelEntries == 0)
            {
                warnings.Add("Model cache is empty; Ask-why will refuse every question until answers are cached outside replay.");
            }

            warnings.Add("Agent re-execution is owned by the agent lane; replay restores ERP start state and an empty case cache.");
            return new ReplayReport(DateTimeOffset.UtcNow, true, steps, warnings, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reset-plus-replay failed.");
            return new ReplayReport(DateTimeOffset.UtcNow, _replay.IsReplay, steps, warnings,
                $"Reset-plus-replay failed: {ex.Message}");
        }
    }
}
