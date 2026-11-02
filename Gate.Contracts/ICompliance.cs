// Compliance verdicts. C# decides, the model explains, every block cites
// a rule id plus a stored public source.
namespace Gate.Contracts;

public enum VerdictKind
{
    Pass,
    Block,
    Hold,
    HumanReview
}

public sealed record Verdict(
    string RuleId,
    VerdictKind Kind,
    string Reason,
    string Source);

/// <summary>Shared verdict rule ids both sides compare by constant, never
/// by mirrored literals. Compliance emits them; the agent filters on them.</summary>
public static class ComplianceVerdictIds
{
    /// <summary>ERP vendor-master block (blocked flag plus reason).</summary>
    public const string VendorBlock = "VENDOR-BLOCK";
}

public interface IComplianceEngine
{
    /// <summary>Evaluates a vendor for a proposed award. The engine computes
    /// every share itself as (country history + award) / (total history +
    /// award) from the trailing-12-month rows; callers never pass shares.
    /// Document packs (BAT-LI, ELEC) apply to critical parts only; for
    /// non-critical parts those rules return not-applicable passes so the
    /// straight-through path needs no pack documents.</summary>
    Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CancellationToken ct = default);

    /// <summary>Pre-pay gate over holds plus quarantine plus open reviews
    /// (ELEC-04). Blocks unless all three are empty.</summary>
    Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state,
        CancellationToken ct = default);

    /// <summary>True when the rule id names a document-presence rule (its
    /// pack entry carries a document kind). Screening failures on document
    /// rules alone lose the award without a vendor-master block: a vendor
    /// missing this lot's papers cannot supply it but stays buyable for
    /// its own trade. Unknown ids are not document rules.</summary>
    bool IsDocumentRule(string ruleId) =>
        throw new NotSupportedException("IsDocumentRule is implemented by the compliance engine.");

    // Memo-to-rule seam. The agent orchestrates (Spark proposes, humans
    // decide); every method below is deterministic C# over the Memo core.
    // Defaults throw so existing fakes compile; the engine overrides all.

    /// <summary>Loads the highest versioned KB file and returns the pinned
    /// snapshot proposals resolve against. Version files are never
    /// overwritten; priors stay on disk for pinned proposals.</summary>
    Task<KbSnapshot> LoadKbSnapshotAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("LoadKbSnapshotAsync is implemented by the compliance engine.");

    /// <summary>Loads one exact KB version for pinned display (citations
    /// carry their version). An unknown version refuses loudly.</summary>
    Task<KbSnapshot> LoadKbSnapshotAtAsync(string kbVersion, CancellationToken ct = default) =>
        throw new NotSupportedException("LoadKbSnapshotAtAsync is implemented by the compliance engine.");

    /// <summary>Publishes one KB version from a base version plus changes:
    /// single-space edits only, approved by the space owner, editors never
    /// approving their own change. Mints kbv(n+1), writes the new file,
    /// and returns the publication. The acting approver flows in
    /// approvedBy; a delegate approving for the space owner additionally
    /// names them in approvedFor (the caller verifies the delegation
    /// window first).</summary>
    Task<KbPublication> PublishKbUpdateAsync(
        string baseVersion,
        IReadOnlyList<KbEntryChange> changes,
        string approvedBy,
        string reason,
        string activationId,
        DateTimeOffset publishedAt,
        string? approvedFor = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("PublishKbUpdateAsync is implemented by the compliance engine.");

    /// <summary>Knowledge spaces with owners from the approval matrix,
    /// sections per space, and member names for display.</summary>
    Task<IReadOnlyList<KbSpace>> ListKbSpacesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("ListKbSpacesAsync is implemented by the compliance engine.");

    /// <summary>Live pack rules with pack versions for the Rules views.
    /// The UI reads packs through here, never from the pack directory.</summary>
    Task<IReadOnlyList<LivePackRule>> ListLivePackRulesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("ListLivePackRulesAsync is implemented by the compliance engine.");

    /// <summary>Canonical proposal hash. The agent stamps it at propose
    /// time; approval and publish re-verify it.</summary>
    Task<string> ComputeProposalHashAsync(RuleProposal proposal, CancellationToken ct = default) =>
        throw new NotSupportedException("ComputeProposalHashAsync is implemented by the compliance engine.");

    /// <summary>Structural plus KB-pinned validation: exactly one of rule
    /// or not-mappable, registry membership, param ranges, well-formed
    /// questions, every citation resolving in the snapshot.</summary>
    Task<ProposalValidation> ValidateProposalAsync(
        RuleProposal proposal, KbSnapshot snapshot, CancellationToken ct = default) =>
        throw new NotSupportedException("ValidateProposalAsync is implemented by the compliance engine.");

    /// <summary>Conflicts between a proposed rule and the live packs
    /// (tighten, loosen, contradiction, subsumption).</summary>
    Task<IReadOnlyList<RuleConflict>> DetectConflictsAsync(
        ProposedRule rule, CancellationToken ct = default) =>
        throw new NotSupportedException("DetectConflictsAsync is implemented by the compliance engine.");

    /// <summary>Read-only old-vs-new impact report over supplied rows.
    /// History and pipeline windows stay separate; computedAt is supplied
    /// (no clock) so replays are byte-identical.</summary>
    Task<WhatIfReport> ComputeWhatIfAsync(
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
        CancellationToken ct = default) =>
        throw new NotSupportedException("ComputeWhatIfAsync is implemented by the compliance engine.");

    /// <summary>Validates the human approval pin against its proposal plus
    /// the current KB version. A KB change after proposal refuses until
    /// re-grounded.</summary>
    Task<ApprovalValidation> ValidateApprovalAsync(
        RuleApproval approval, RuleProposal proposal, string currentKbVersion,
        CancellationToken ct = default) =>
        throw new NotSupportedException("ValidateApprovalAsync is implemented by the compliance engine.");

    /// <summary>Publishes a parameter change to a live rule (the rule must
    /// already exist in the pack). Builds the version-bumped pack, writes
    /// it, and returns the publication with its activation.</summary>
    Task<PackPublication> PublishRuleUpdateAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default) =>
        throw new NotSupportedException("PublishRuleUpdateAsync is implemented by the compliance engine.");

    /// <summary>Publishes a brand-new rule into a pack. Title, tier, and
    /// document label are the approver's call.</summary>
    Task<PackPublication> PublishNewRuleAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        NewRuleSpec spec,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default) =>
        throw new NotSupportedException("PublishNewRuleAsync is implemented by the compliance engine.");

    /// <summary>Publishes a rollback: a new pack version restoring the
    /// named prior version's parameters. History is untouched.</summary>
    Task<PackPublication> PublishRollbackAsync(
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
        CancellationToken ct = default) =>
        throw new NotSupportedException("PublishRollbackAsync is implemented by the compliance engine.");
}
