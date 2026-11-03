// File-backed memo proposal store. One file per board memo named
// <MemoId>.memo.json holding the memo plus its proposal records,
// decisions, timeline, and activations, serialized with web defaults.
// Lives next to the case files in the same cache dir; CaseStore.ListCaseIds
// skips *.memo.json. The UI lane reads these files for the PolicyApproval
// and PolicyQuestion cards plus the rollback action.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>One stored proposal round: Spark's proposal, the engine's
/// validation, the what-if report (null for NotMappable, which carries
/// "not computed" instead), and the Spark-named policy owner the engine
/// cannot see (it lives outside RuleProposal, so the agent checks it
/// against the KB approval matrix at parse time).</summary>
public sealed record MemoProposalRecord(
    RuleProposal Proposal,
    ProposalValidation Validation,
    WhatIfReport? WhatIf,
    string PolicyOwner);

/// <summary>One memo file: the JSON root the memo runner writes and the UI
/// reads. Proposals are append-only (last is current); answering a blocking
/// question re-drives a new round. No status field: MemoPicker derives the
/// next step from this state.</summary>
public sealed record MemoFile(
    PolicyMemo Memo,
    string FullText,
    IReadOnlyList<MemoProposalRecord> Proposals,
    IReadOnlyList<Decision> Decisions,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<RuleActivation> Activations);

public static class MemoStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string MemoFilePath(string cacheDir, string memoId) =>
        Path.Combine(cacheDir, memoId + ".memo.json");

    public static MemoFile? Load(string cacheDir, string memoId)
    {
        var path = MemoFilePath(cacheDir, memoId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<MemoFile>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Memo file {path} did not parse as a MemoFile.");
    }

    public static void Save(string cacheDir, MemoFile file)
    {
        Directory.CreateDirectory(cacheDir);
        WriteFile(MemoFilePath(cacheDir, file.Memo.MemoId), file);
    }

    public static IReadOnlyList<string> ListMemoIds(string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
        {
            return [];
        }

        return Directory.GetFiles(cacheDir, "*.memo.json")
            .Select(f => Path.GetFileName(f))
            .Where(n => n is not null && n.EndsWith(".memo.json", StringComparison.Ordinal))
            .Select(n => n![..^".memo.json".Length])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList()!;
    }

    private static void WriteFile(string path, MemoFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
}

/// <summary>Mutable accumulator over one memo-file save: append proposals,
/// events, decisions, and activations, then save once.</summary>
internal sealed class MemoWork
{
    private readonly string _memoId;
    private readonly PolicyMemo _memo;
    private readonly string _fullText;
    private readonly List<MemoProposalRecord> _proposals;
    private readonly List<CaseEvent> _events;
    private readonly List<Decision> _decisions;
    private readonly List<RuleActivation> _activations;

    public MemoWork(MemoFile file)
    {
        _memoId = file.Memo.MemoId;
        _memo = file.Memo;
        _fullText = file.FullText;
        _proposals = file.Proposals.ToList();
        _events = file.Events.ToList();
        _decisions = file.Decisions.ToList();
        _activations = file.Activations.ToList();
    }

    public MemoFile CurrentFile => new(
        _memo, _fullText, _proposals, _decisions, _events, _activations);

    public void AddProposal(MemoProposalRecord record) => _proposals.Add(record);

    public void SetWhatIf(string proposalHash, WhatIfReport report)
    {
        var index = _proposals.FindIndex(p =>
            string.Equals(p.Proposal.ProposalHash, proposalHash, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Memo {_memoId} carries no proposal with hash '{proposalHash}' to attach a what-if report to.");
        }

        _proposals[index] = _proposals[index] with { WhatIf = report };
    }

    /// <summary>Rewrites the policy owner on the proposal round with the
    /// given hash (approval re-routing when Spark names someone who
    /// cannot approve).</summary>
    public void SetPolicyOwner(string proposalHash, string owner)
    {
        var index = _proposals.FindIndex(p =>
            string.Equals(p.Proposal.ProposalHash, proposalHash, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Memo {_memoId} carries no proposal with hash '{proposalHash}' to re-route.");
        }

        _proposals[index] = _proposals[index] with { PolicyOwner = owner };
    }

    public void AddActivation(RuleActivation activation) => _activations.Add(activation);

    public CaseEvent AddEvent(
        CaseEventKind kind, string summary, string? ruleId = null,
        string? documentType = null, string? documentNumber = null,
        string? evidenceRef = null, decimal? amount = null, string? currency = null,
        string? actor = null)
    {
        var entry = new CaseEvent(
            $"{_memoId}-e{_events.Count + 1:000}", _memoId, DateTimeOffset.UtcNow,
            actor ?? RecordSource.P2pAgent, kind, summary, ruleId,
            documentType, documentNumber, evidenceRef, amount, currency);
        _events.Add(entry);
        return entry;
    }

    public Decision FindOrCreateDecision(
        DecisionKind kind, string title, string requestedTo,
        string? documentType, string? documentNumber,
        decimal? amount = null, string? currency = null, string? reason = null,
        string? ruleId = null, IReadOnlyList<string>? evidenceRefs = null)
    {
        var existing = _decisions.FirstOrDefault(d =>
            d.Kind == kind
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.DocumentType, documentType, StringComparison.Ordinal)
            && string.Equals(d.DocumentNumber, documentNumber, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var decision = new Decision(
            $"{_memoId}-d{_decisions.Count + 1:000}", _memoId, kind, title,
            DecisionStatus.Pending, requestedTo, DateTimeOffset.UtcNow,
            null, null, documentType, documentNumber, amount, currency,
            ruleId, reason, evidenceRefs ?? [], null);
        _decisions.Add(decision);
        return decision;
    }

    public Decision Decide(Decision decision, bool approved, string decidedBy, string? comment)
    {
        var index = _decisions.FindIndex(d =>
            string.Equals(d.DecisionId, decision.DecisionId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} is not on memo {_memoId}.");
        }

        var updated = decision with
        {
            Status = approved ? DecisionStatus.Approved : DecisionStatus.Rejected,
            DecidedBy = decidedBy,
            DecidedAt = DateTimeOffset.UtcNow,
            Comment = comment
        };
        _decisions[index] = updated;
        return updated;
    }

    public void Save(string cacheDir) => MemoStore.Save(cacheDir, CurrentFile);
}
