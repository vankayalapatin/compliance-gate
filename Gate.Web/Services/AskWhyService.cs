// Ask-the-agent-why: answers a human question over one decision's
// evidence through the injected model client (via P2pAgent.ExplainAsync).
// In replay mode the cached answer must already exist on disk; a miss
// surfaces as an error instead of placing a live call, so replay makes
// zero live calls by construction. Every answer and error carries the
// current spend readout.
using System.Text;
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed record WhyAnswer(bool Ok, string Text);

public sealed class AskWhyService
{
    private readonly P2pAgent _agent;
    private readonly IModelClient _models;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ReplayState _replay;
    private readonly ILogger<AskWhyService> _log;
    private readonly string _modelCacheDir;
    private readonly ModelSpendTracker _spend;

    public AskWhyService(
        P2pAgent agent,
        IModelClient models,
        CaseCacheStore cases,
        DockPhotoService photos,
        ReplayState replay,
        ILogger<AskWhyService> log,
        string modelCacheDir,
        ModelSpendTracker? spend = null)
    {
        _agent = agent;
        _models = models;
        _cases = cases;
        _photos = photos;
        _replay = replay;
        _log = log;
        _modelCacheDir = modelCacheDir;
        _spend = spend ?? new ModelSpendTracker();
    }

    public async Task<WhyAnswer> AskAsync(
        string caseId, string decisionId, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new WhyAnswer(false, "Type a question first.");
        }

        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new WhyAnswer(false, $"Case {caseId} has no case file.");
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new WhyAnswer(false, $"Decision {decisionId} not found in case {caseId}.");
        }

        var evidence = BuildEvidence(file.Case, decision);
        var trimmed = question.Trim();
        if (_replay.IsReplay && !HasCachedAnswer(trimmed, evidence))
        {
            return new WhyAnswer(false, WithSpend(
                "Replay makes zero live calls and no cached answer exists for this exact question. " +
                "Rephrase to a previously answered question or leave replay mode."));
        }

        try
        {
            var text = await _agent.ExplainAsync(trimmed, evidence, ct).ConfigureAwait(false);
            return new WhyAnswer(true, WithSpend(text));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ask-why failed for decision {DecisionId}.", decisionId);
            return new WhyAnswer(false, WithSpend($"The agent call failed: {ex.Message}"));
        }
    }

    private string WithSpend(string text) => $"{text}\n\nSpend so far: {_spend.Summary}.";

    private string BuildEvidence(Case kase, Decision decision)
    {
        var sb = new StringBuilder();
        sb.Append("Case ").Append(kase.CaseId)
            .Append(" (SO ").Append(kase.SoNumber)
            .Append(", item ").Append(kase.ItemId)
            .Append(", stage ").Append(kase.Stage).Append("). ");
        sb.Append("Decision ").Append(decision.DecisionId)
            .Append(": ").Append(decision.Kind)
            .Append(" '").Append(decision.Title).Append("', status ").Append(decision.Status)
            .Append(", requested to ").Append(decision.RequestedTo).Append(". ");
        if (decision.RuleId is not null) sb.Append("Rule ").Append(decision.RuleId).Append(". ");
        if (decision.Reason is not null) sb.Append("Reason: ").Append(decision.Reason).Append(' ');
        if (decision.Amount is not null)
        {
            sb.Append("Amount ").Append(decision.Amount).Append(' ').Append(decision.Currency ?? "?").Append(". ");
        }
        if (decision.DocumentType is not null)
        {
            sb.Append("Document ").Append(decision.DocumentType).Append(' ').Append(decision.DocumentNumber).Append(". ");
        }
        foreach (var evidence in decision.EvidenceRefs)
        {
            var image = _photos.ResolveImageFileName(evidence);
            if (image is null)
            {
                sb.Append("Evidence: ").Append(evidence).Append(". ");
                continue;
            }

            sb.Append("Photo ").Append(image).Append(". ");
            try
            {
                var read = _photos.TryCachedRead(image);
                if (read is not null) sb.Append("Cached read: ").Append(read).Append(". ");
            }
            catch (Exception ex)
            {
                sb.Append("Cached read refused: ").Append(ex.Message).Append(". ");
            }
        }

        return sb.ToString();
    }

    /// <summary>True when the model cache already holds this exact call.
    /// Mirrors the request P2pAgent.ExplainAsync builds; if that shape
    /// changes, this guard must change with it.</summary>
    private bool HasCachedAnswer(string question, string evidence)
    {
        var request = new ModelRequest(question, $"Decide using only this evidence: {evidence}");
        var path = Path.Combine(_modelCacheDir, CachedModelClient.CacheKey(_models.ModelName, request) + ".json");
        return File.Exists(path);
    }
}
