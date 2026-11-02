// Recording compliance engine: the real engine with a tap on the pre-pay
// gate, plus the memo seam wired over the real deterministic Memo cores.
// Records every PrePayState call and every memo call. The pinned snapshot
// and prior pack JSONs are test-supplied (defaults: the shared memo
// fixtures); publications update the in-memory pack versions so update and
// rollback chains behave like the real engine. Agent-lane Spark-path tests
// run against this fake until the compliance-wireup lane ships the real
// engine seam; the wiring is real C# cores, so validation, conflicts,
// what-if, approval, and publish all judge honestly.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class RecordingComplianceEngine : IComplianceEngine
{
    private readonly ComplianceEngine _inner = new();

    public RecordingComplianceEngine()
    {
        Snapshot = MemoFixtures.TestKb();
        foreach (var (name, json) in MemoFixtures.FourPacks())
        {
            var packId = name switch
            {
                "Packs.bat-li.json" => "BAT-LI",
                "Packs.elec.json" => "ELEC",
                "Packs.scr.json" => "SCR",
                "Packs.corp.json" => "CORP",
                _ => throw new InvalidOperationException($"Fixture pack '{name}' names no pack id."),
            };
            PriorPacks[packId] = json;
            PackHistory[packId] = [(PackVersionOf(json), json)];
        }
    }

    public List<PrePayState> PrePayStates { get; } = new();

    public List<string> MemoCalls { get; } = new();

    public List<PackPublication> Published { get; } = new();

    public KbSnapshot Snapshot { get; set; }

    public Dictionary<string, string> PriorPacks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<(string Version, string Json)>> PackHistory { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award, IReadOnlyList<VolumeHistory> history, CancellationToken ct = default) =>
        _inner.EvaluateVendorAsync(vendor, item, documents, award, history, ct);

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state, CancellationToken ct = default)
    {
        PrePayStates.Add(state);
        return _inner.EvaluatePaymentAsync(state, ct);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePayableAsync(
        PrePayState state, CancellationToken ct = default)
    {
        PrePayStates.Add(state);
        return _inner.EvaluatePayableAsync(state, ct);
    }

    public bool IsDocumentRule(string ruleId) => _inner.IsDocumentRule(ruleId);

    public Task<KbSnapshot> LoadKbSnapshotAsync(CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("LoadKbSnapshot");
        return Task.FromResult(Snapshot);
    }

    public Task<string> ComputeProposalHashAsync(RuleProposal proposal, CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("ComputeProposalHash");
        return Task.FromResult(ProposalHasher.Hash(proposal));
    }

    public Task<ProposalValidation> ValidateProposalAsync(
        RuleProposal proposal, KbSnapshot snapshot, CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("ValidateProposal");
        return Task.FromResult(ProposalValidator.ValidateAgainstKb(proposal, snapshot));
    }

    public Task<IReadOnlyList<RuleConflict>> DetectConflictsAsync(
        ProposedRule rule, CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("DetectConflicts");
        return Task.FromResult(RuleConflictDetector.Compute(rule));
    }

    public Task<WhatIfReport> ComputeWhatIfAsync(
        IReadOnlyList<WhatIfHistoryRow> historyRows,
        IReadOnlyList<WhatIfPipelineRow> pipelineRows,
        WhatIfRuleConfig? oldConfig,
        WhatIfRuleConfig newConfig,
        string? oldRuleId,
        KbFiscalCalendar? fiscalCalendar,
        string proposalHash,
        string kbVersion,
        IReadOnlyDictionary<string, string> packVersions,
        DateTimeOffset computedAt,
        IReadOnlyList<WhatIfPipelineRow>? probes,
        CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("ComputeWhatIf");
        return Task.FromResult(WhatIfCalculator.Compute(
            historyRows, pipelineRows, oldConfig, newConfig, oldRuleId,
            fiscalCalendar, proposalHash, kbVersion, packVersions,
            computedAt, probes));
    }

    public Task<ApprovalValidation> ValidateApprovalAsync(
        RuleApproval approval, RuleProposal proposal, string currentKbVersion,
        CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add("ValidateApproval");
        return Task.FromResult(ApprovalValidator.Validate(approval, proposal, currentKbVersion));
    }

    public Task<PackPublication> PublishRuleUpdateAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add($"PublishRuleUpdate:{packId}:{ruleId}");
        var publication = PackPublisher.BuildUpdate(
            RequirePack(packId), proposal, validation, approval, testEvidence,
            ruleId, activationId, publishedAt);
        Remember(publication);
        return Task.FromResult(publication);
    }

    public Task<PackPublication> PublishNewRuleAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        NewRuleSpec spec,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add($"PublishNewRule:{packId}:{ruleId}");
        var publication = PackPublisher.BuildNewRule(
            RequirePack(packId), proposal, validation, approval, testEvidence,
            ruleId, spec, activationId, publishedAt);
        Remember(publication);
        return Task.FromResult(publication);
    }

    public Task<PackPublication> PublishRollbackAsync(
        string packId,
        string restoreVersion,
        string memoId,
        string approver,
        DateTimeOffset effectiveAt,
        RuleApplicability applicability,
        string proposalHash,
        string reason,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        _ = ct;
        MemoCalls.Add($"PublishRollback:{packId}:{restoreVersion}");
        var history = PackHistory.TryGetValue(packId, out var versions)
            ? versions
            : throw new InvalidOperationException(
                $"Fake engine carries no pack '{packId}' to roll back.");
        var restore = history.FirstOrDefault(v =>
            string.Equals(v.Version, restoreVersion, StringComparison.Ordinal));
        if (restore.Json is null)
        {
            throw new InvalidOperationException(
                $"Fake engine pack '{packId}' carries no version '{restoreVersion}' to restore " +
                $"(versions: {string.Join(", ", history.Select(v => v.Version))}).");
        }

        var publication = PackPublisher.BuildRollback(
            RequirePack(packId), restore.Json, memoId, approver, effectiveAt,
            applicability, proposalHash, reason, ruleId, activationId, publishedAt);
        Remember(publication);
        return Task.FromResult(publication);
    }

    private string RequirePack(string packId) =>
        PriorPacks.TryGetValue(packId, out var json)
            ? json
            : throw new InvalidOperationException(
                $"Fake engine carries no prior pack JSON for pack '{packId}'.");

    private void Remember(PackPublication publication)
    {
        Published.Add(publication);
        PriorPacks[publication.Activation.PackId] = publication.Json;
        if (!PackHistory.TryGetValue(publication.Activation.PackId, out var versions))
        {
            versions = [];
            PackHistory[publication.Activation.PackId] = versions;
        }

        versions.Add((publication.Activation.NewPackVersion, publication.Json));
    }

    private static string PackVersionOf(string packJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(packJson);
        if (doc.RootElement.TryGetProperty("version", out var version)
            && version.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return version.GetString() ?? "v0.0";
        }

        return "v0.0";
    }
}
