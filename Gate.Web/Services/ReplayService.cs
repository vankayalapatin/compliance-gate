// Reset-to-start plus explicit offline replay behind the top-bar
// controls. Reset wipes and reloads the ERP scenario (every number
// regenerated), clears the case cache, seeds the opening decisions
// from ERP pending approvals, then picks up actionable ERP work into
// live cases; it never touches replay mode, so a reset
// in Live stays Live. Replay is a separate explicit switch: entering it
// pins Ask-why to cached answers so the rest of the session makes zero
// live model calls. Every step reports current spend. Agent re-execution
// stays in the agent lane; this service restores start state and reports
// each step.
using Gate.Agent;
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
    private readonly CaseRunner? _runner;
    private readonly ModelSpendTracker _spend;

    public ReplayService(
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ReplayState replay,
        IConfiguration config,
        ILogger<ReplayService> log,
        CaseRunner? runner = null,
        ModelSpendTracker? spend = null)
    {
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _replay = replay;
        _config = config;
        _log = log;
        _runner = runner;
        _spend = spend ?? new ModelSpendTracker(config);
    }

    /// <summary>Enters replay mode explicitly: Ask-why answers from the
    /// model cache only and refuses to place a live call.</summary>
    public void EnterReplay()
    {
        _replay.IsReplay = true;
        _log.LogInformation(
            "Replay mode on: Ask-why answers from the model cache only (zero live calls).");
    }

    /// <summary>Leaves replay mode explicitly: live model calls allowed.</summary>
    public void ExitReplay()
    {
        _replay.IsReplay = false;
        _log.LogInformation("Replay mode off: live model calls allowed.");
    }

    /// <summary>Resets to scenario start and reports each completed step as
    /// it lands so the top bar can show movement; the returned report
    /// carries the full result. Reset never switches replay mode: the
    /// report's IsReplay echoes the unchanged mode. Zero live calls happen
    /// in here: scenario reload, cache clear, the pure-C# opening-decision
    /// seed, the ERP-work pickup, cache counts, and the spend readout are
    /// all local.</summary>
    public async Task<ReplayReport> ResetAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        void Step(string text)
        {
            steps.Add(text);
            progress?.Report(text);
        }

        try
        {
            var manifest = await ScenarioLoader.ResetAsync(
                _erp,
                ScenarioEndpoints.ScenarioPath(_config),
                ScenarioEndpoints.SqlitePath(_config),
                ScenarioEndpoints.ManifestPath(_config),
                ct).ConfigureAwait(false);
            Step($"Scenario {manifest.Scenario} reloaded; {manifest.Documents.Count} document(s) regenerated.");

            var cleared = await _cases.ClearAsync(ct).ConfigureAwait(false);

            // Opening-decision seed (agent lane owns the projector; this is
            // the one-line reset wiring). A seed failure warns but keeps the
            // reset: ERP start state still holds without the cards.
            IReadOnlyList<string> seeded = [];
            if (_runner is not null)
            {
                try
                {
                    seeded = await _runner.SeedOpeningDecisionsAsync(manifest.Scenario, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Opening-decision seed failed for scenario {Scenario}.",
                        manifest.Scenario);
                    warnings.Add($"Opening-decision seed failed: {ex.Message}.");
                }
            }

            // ERP-work pickup (agent lane owns the adopter; pure C# plus
            // adapter reads/writes, zero live calls). Actionable seeded
            // documents get live cases now instead of sitting caseless;
            // submitted/draft documents stay on their opening cards. A
            // pickup failure warns but keeps the reset: ERP start state
            // still holds without the cases.
            IReadOnlyList<string> picked = [];
            if (_runner is not null)
            {
                try
                {
                    picked = await _runner.PickupErpWorkAsync(manifest.Scenario, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ERP-work pickup failed for scenario {Scenario}.",
                        manifest.Scenario);
                    warnings.Add($"ERP-work pickup failed: {ex.Message}.");
                }
            }

            Step($"Case cache cleared ({cleared} file(s) removed; {seeded.Count} opening decision(s) seeded; {picked.Count} ERP-work case(s) picked up).");

            var modelCache = ModelCachePaths.ResolveDir();
            var modelEntries = Directory.Exists(modelCache) ? Directory.GetFiles(modelCache, "*.json").Length : 0;
            Step($"Model cache holds {modelEntries} entr(y/ies); vision cache holds {_photos.CachedReadCount()} read(s).");
            if (modelEntries == 0)
            {
                warnings.Add("Model cache is empty; Ask-why will refuse every question until answers are cached outside replay.");
            }

            Step($"Spend so far: {_spend.Summary}.");

            warnings.Add("Agent re-execution is owned by the agent lane; reset restores ERP start state and re-adopts its actionable documents.");
            return new ReplayReport(DateTimeOffset.UtcNow, _replay.IsReplay, steps, warnings, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reset failed.");
            return new ReplayReport(DateTimeOffset.UtcNow, _replay.IsReplay, steps, warnings,
                $"Reset failed: {ex.Message}");
        }
    }
}
