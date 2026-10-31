// Memo-to-rule shared shapes. Mirrors docs/company-kb/: Spark proposes from
// a board memo against a pinned KB snapshot, C# validates every citation,
// a human approves, C# publishes a versioned pack. A proposal without
// valid citations is not a proposal. No business values here: memo text,
// KB entries, thresholds, and counts arrive at runtime.
namespace Gate.Contracts;

/// <summary>Checkpoint a registry rule type enforces at (RULE-TYPES.md).
/// New gates require code plus tests, never model invention.</summary>
public enum MemoRuleGate
{
    Vendor,
    Dock,
    PrePay
}

/// <summary>What a NotMappable proposal is missing (RULE-TYPES.md): a new
/// KB list or signal, a new observation, a checkpoint the flow does not
/// have, or a new registry type with tests.</summary>
public enum MissingCapability
{
    Data,
    Sensor,
    Gate,
    Code
}

/// <summary>How a new rule treats in-flight documents at the effective
/// datetime (GO-LIVE.md). Posted rows are never re-evaluated.</summary>
public enum RuleApplicability
{
    /// <summary>Default. Open/draft docs evaluate at the next gate;
    /// approved docs are grandfathered plus flagged for human review.</summary>
    ProspectiveOnly,
    /// <summary>As above, plus every grandfathered flag needs an explicit
    /// human disposition before its case closes.</summary>
    ProspectiveWithReview,
    /// <summary>Safety or regulatory Tier-1 only: hold in-flight docs
    /// pending human review. Creates holds, never cancels.</summary>
    ImmediateHold
}

/// <summary>How a proposal relates to a live pack rule, computed by C#
/// validation against live packs and shown on the approval card.</summary>
public enum RuleConflictKind
{
    Tightens,
    Loosens,
    Contradicts,
    Subsumes
}

/// <summary>One versioned KB reference. Wire form is
/// kb:&lt;section&gt;#&lt;id&gt;@&lt;version&gt;. C# resolves each citation
/// against the pinned KB snapshot; an unresolvable citation rejects the
/// proposal loudly.</summary>
public sealed record KbCitation(
    string Section,
    string EntryId,
    string KbVersion);

/// <summary>One memo statement: the verbatim quote plus Spark's
/// interpretation of it. Confidence is 0 to 1.</summary>
public sealed record ProposalStatement(
    string Quote,
    string Interpretation,
    decimal Confidence);

/// <summary>One resolved entity: a memo phrase mapped to a KB entry with
/// its citation. Unresolvable phrases become blocking questions instead.</summary>
public sealed record EntityResolution(
    string Phrase,
    string EntityId,
    KbCitation Citation);

/// <summary>Spark's proposed enforceable rule. RuleType names the closed
/// registry type (DocumentPresence, OriginShareCap, VendorShareCap,
/// ScreeningMatch, DockMarkPresence, DockHandlingPresence, DamageReview,
/// PrePayBlockOnOpen); membership and param ranges are validated in C#.
/// Params carry string values only; memos never tune list scope.</summary>
public sealed record ProposedRule(
    string RuleType,
    MemoRuleGate Gate,
    IReadOnlyDictionary<string, string> Params);

/// <summary>When no registry type fits: the closest type considered, the
/// exact mismatch, what would be needed, and the blocking questions whose
/// answers could make it mappable (carried on the proposal).</summary>
public sealed record NotMappableDetail(
    string NearestType,
    string WhyNot,
    MissingCapability MissingCapability);

/// <summary>One open question. Blocking questions stop the pipeline until
/// the policy owner answers; the memo waits and nothing publishes.</summary>
public sealed record OpenQuestion(
    string QuestionId,
    string Question,
    IReadOnlyList<string> Options,
    bool Blocking);

/// <summary>One board memo at ingest: external input only, never encoded
/// policy meaning. The excerpt is shown verbatim on the approval card;
/// the full text stays at the storage ref.</summary>
public sealed record PolicyMemo(
    string MemoId,
    DateTimeOffset ReceivedAt,
    string Author,
    string Excerpt,
    string? StorageRef = null);

/// <summary>Spark's structured proposal for one memo. Exactly one of
/// ProposedRule and NotMappable is set; enforcement of that sits with the
/// C# validator, not this shape. ProposalHash covers the whole proposal;
/// a KB version change after proposal requires re-grounding before
/// approval can proceed.</summary>
public sealed record RuleProposal(
    string ProposalId,
    string MemoId,
    string KbVersion,
    string KbHash,
    IReadOnlyList<ProposalStatement> Statements,
    IReadOnlyList<EntityResolution> Resolutions,
    ProposedRule? ProposedRule,
    NotMappableDetail? NotMappable,
    IReadOnlyList<OpenQuestion> Questions,
    IReadOnlyList<KbCitation> Citations,
    string ProposalHash,
    DateTimeOffset ProposedAt);

/// <summary>One verdict flip under the proposed rule, either direction.
/// Amounts are qty times price with the currency from the document
/// header; multi-currency sets report per flip, never blended.</summary>
public sealed record WhatIfFlip(
    string DocumentType,
    string DocumentNumber,
    VerdictKind OldKind,
    VerdictKind NewKind,
    string? BlockingRuleId,
    decimal Quantity,
    decimal Amount,
    string? Currency);

/// <summary>One open-pipeline doc re-evaluated as if the rule were live:
/// its old verdict, its new verdict, and the blocking rule id.</summary>
public sealed record PipelineRowImpact(
    string DocumentType,
    string DocumentNumber,
    VerdictKind OldKind,
    VerdictKind NewKind,
    string? BlockingRuleId);

/// <summary>What-if totals: counts plus quantities plus amounts. Currency
/// is null when the flipped set spans more than one currency.</summary>
public sealed record WhatIfTotals(
    int DocumentsEvaluated,
    int FlipsToBlock,
    int FlipsToPass,
    decimal QuantityFlipped,
    decimal AmountFlipped,
    string? Currency);

/// <summary>Read-only old-vs-new impact report. History is never
/// rewritten: the report shows what would have changed. The history and
/// pipeline windows are always reported separately, never blended. Until
/// the KB fiscal calendar exists the history label must say so (trailing
/// twelve-month approximation, fiscal-year window pending). Unviable is
/// explicit when no award path passes. Same inputs replay byte-identical.</summary>
public sealed record WhatIfReport(
    string ProposalHash,
    string KbVersion,
    IReadOnlyDictionary<string, string> PackVersions,
    string HistoryLabel,
    int HistoryRowCount,
    IReadOnlyList<WhatIfFlip> HistoryFlips,
    IReadOnlyList<PipelineRowImpact> PipelineRows,
    IReadOnlyList<WhatIfFlip> Flips,
    WhatIfTotals Totals,
    bool Unviable,
    DateTimeOffset ComputedAt);

/// <summary>One conflict between the proposal and a live pack rule,
/// computed by C# validation. Detail quotes both sides.</summary>
public sealed record RuleConflict(
    string RuleId,
    RuleConflictKind Kind,
    string Detail);

public enum RuleApprovalOutcome
{
    Approved,
    Rejected
}

/// <summary>The human approval pin for one proposal: proposal hash plus KB
/// version plus approver plus timestamp, with the mandatory effective
/// datetime and the applicability mode. EditedParams carries approver
/// edits made within the registry; null means approved as proposed.
/// EffectiveAt is never backdated without a recorded reason.</summary>
public sealed record RuleApproval(
    string ProposalHash,
    string KbVersion,
    string Approver,
    DateTimeOffset DecidedAt,
    RuleApprovalOutcome Outcome,
    DateTimeOffset EffectiveAt,
    RuleApplicability Applicability,
    IReadOnlyList<RuleConflict> Conflicts,
    IReadOnlyDictionary<string, string>? EditedParams,
    string? Comment);

/// <summary>One KB legal entity: id, name, country of incorporation,
/// base currency.</summary>
public sealed record KbLegalEntity(
    string Id,
    string Name,
    string Country,
    string Currency);

/// <summary>One KB site or warehouse: id, legal entity ref, region ref
/// (resolves via regionRules), capabilities.</summary>
public sealed record KbSite(
    string Id,
    string EntityId,
    string Region,
    IReadOnlyList<string> Capabilities);

/// <summary>KB fiscal calendar. Null until seeded; while null, history
/// reports use the labeled trailing-twelve-month approximation.</summary>
public sealed record KbFiscalCalendar(
    string YearEnd,
    string CurrentFy,
    IReadOnlyList<string> Periods);

/// <summary>One KB approval-matrix row: policy or action, owner role,
/// named person. The matrix names who; the ERP enforces how much.</summary>
public sealed record KbApprovalEntry(
    string Action,
    string OwnerRole,
    string Owner);

/// <summary>One KB item row: item id plus critical flag. Mirrors the
/// scenario items; equality is proven by test, never asserted.</summary>
public sealed record KbItem(
    string Id,
    bool Critical);

/// <summary>One KB sourcing policy: id plus string params (caps,
/// thresholds, windows, part scope). Mirrors pack config values;
/// equality is proven by test.</summary>
public sealed record KbSourcingPolicy(
    string Id,
    IReadOnlyDictionary<string, string> Params);

/// <summary>One KB region rule: named region plus explicit versioned
/// country list. Region boundaries are data.</summary>
public sealed record KbRegionRule(
    string Id,
    IReadOnlyList<string> Countries,
    string Version);

/// <summary>One KB document row. P2pRelevant false (HR policies and
/// similar) means searchable agent context, excluded from rule mapping
/// by a C# allowlist.</summary>
public sealed record KbDocument(
    string Id,
    string Kind,
    bool P2pRelevant,
    string? ContentHash = null,
    string? StorageRef = null);

/// <summary>Pinned KB snapshot every proposal resolves against. The
/// proposal pins KbVersion plus KbHash; any KB change after a proposal
/// requires re-grounding before approval can proceed.</summary>
public sealed record KbSnapshot(
    string KbVersion,
    string KbHash,
    IReadOnlyList<KbLegalEntity> LegalEntities,
    IReadOnlyList<KbSite> SitesWarehouses,
    KbFiscalCalendar? FiscalCalendar,
    IReadOnlyList<KbApprovalEntry> ApprovalMatrix,
    IReadOnlyList<KbItem> Items,
    IReadOnlyList<KbSourcingPolicy> SourcingPolicies,
    IReadOnlyList<KbRegionRule> RegionRules,
    IReadOnlyList<KbDocument> Documents);

/// <summary>Publish record: a validated plus approved plus tested proposal
/// shipped as a version-bumped pack. Source is memo id plus approver plus
/// effective date. PriorPackVersion is the rollback point: rollback
/// publishes a new version restoring prior parameters, history untouched.</summary>
public sealed record RuleActivation(
    string ActivationId,
    string RuleId,
    string PackId,
    string NewPackVersion,
    string PriorPackVersion,
    string MemoId,
    string Approver,
    DateTimeOffset EffectiveAt,
    RuleApplicability Applicability,
    string ProposalHash,
    DateTimeOffset PublishedAt);
