// Agent-side case store shapes. Cases live outside ERP (agent-side), one per
// sales-order shortfall item, linked to the SO plus its PR/PO/ASN/receipt/
// invoice/payment chain. The agent picks its next tool from case state; the
// UI renders Decisions, Cases, and Audit from these same DTOs.
// No business values here: ids, quantities, limits, and names arrive at runtime.
namespace Gate.Contracts;

public enum CaseStage
{
    Created,
    ShortfallComputed,
    PrSubmitted,
    PrApproved,
    VendorScreened,
    PoPlaced,
    PoAcknowledged,
    AsnRecorded,
    ReceiptPosted,
    DockChecked,
    InvoiceMatched,
    PaymentRequested,
    PaymentPartial,
    Resolved,
    Closed,
    Cancelled,
    /// <summary>Split parent: the approved plan's children are ordering.
    /// Appended last: stages persist as numbers in case files, so existing
    /// values never shift.</summary>
    SplitOrdered
}

/// <summary>One shortfall-item chain: SO + item plus its document refs and
/// current block. Quantities stay on the ERP documents; this is identity,
/// stage, links, and block summary only.</summary>
public sealed record Case(
    string CaseId,
    string SoNumber,
    string ItemId,
    string ScenarioName,
    CaseStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PrNumber,
    string? PoNumber,
    string? AsnNumber,
    string? ReceiptNumber,
    IReadOnlyList<InvoiceKey> Invoices,
    IReadOnlyList<string> PaymentNumbers,
    string? BlockedByRuleId,
    string? BlockedReason,
    string? AwardedVendorId = null,
    /// <summary>Seeded opening shell: no agent-authored timeline entries,
    /// decisions project 1:1 from ERP pending approvals. Only real runs
    /// show what the agent did.</summary>
    bool IsOpeningData = false,
    /// <summary>Split parent case id. Set only on split children: each
    /// child runs one approved split line through today's single-PO flow,
    /// and the customer message waits for the parent.</summary>
    string? ParentCaseId = null);

public enum CaseEventKind
{
    OrderReceived,
    ShortfallComputed,
    PrCreated,
    PrSubmitted,
    PrApproved,
    PrRejected,
    VendorScreened,
    VendorBlocked,
    PoCreated,
    PoApproved,
    VendorAcknowledged,
    AsnRecorded,
    ReceiptPosted,
    DockEvaluated,
    HoldSet,
    HoldReleased,
    ReviewOpened,
    ReviewClosed,
    InvoiceCreated,
    InvoiceMatched,
    PaymentRequested,
    PaymentPosted,
    CustomerNotified,
    Note,
    VendorEmailed,
    VendorReplied,
    SupplierAwarded,
    /// <summary>The agent tried a tool call the step refused. The summary is
    /// plain words ("tried to ..., refused"); RuleId carries the tool name
    /// and the transcript keeps the full reason.</summary>
    AgentRefused
}

/// <summary>One append-only timeline entry. Actor is a user id, vendor id, or
/// RecordSource value. EvidenceRef points at a photo, model-cache entry, or
/// explanation; the payload itself stays in its own store.</summary>
public sealed record CaseEvent(
    string EventId,
    string CaseId,
    DateTimeOffset Timestamp,
    string Actor,
    CaseEventKind Kind,
    string Summary,
    string? RuleId,
    string? DocumentType,
    string? DocumentNumber,
    string? EvidenceRef,
    decimal? Amount,
    string? Currency);

public enum DecisionKind
{
    PrApproval,
    PoApproval,
    InvoiceApproval,
    PaymentApproval,
    DockReview,
    DamageReview,
    ExceptionApproval,
    /// <summary>AP clerk or finance verifies a supplier-requested bank change
    /// out of band (VerifyVendorBankAsync). The master never auto-updates.</summary>
    BankVerification,
    /// <summary>AP clerk owns a held invoice exception: missing vendor or PO,
    /// price hold, or other AP catch. Resolve or reject.</summary>
    ApReview,
    /// <summary>Human confirms a proposed vendor-master match for a
    /// transcribed vendor id or name (invoice photo, vendor reply). The
    /// agent proposes exactly one candidate; the master never auto-matches.
    /// DocumentType "Vendor" plus DocumentNumber carry the proposed vendor
    /// id, Reason carries the raw transcribed text, EvidenceRefs the photo
    /// or mail refs. Approve confirms, reject routes to the missing-fields
    /// path.</summary>
    VendorIdentity,
    /// <summary>Policy owner approves a memo-to-rule proposal: memo excerpt,
    /// cited interpretation, conflicts, what-if flips, applicability mode,
    /// effective datetime (see Gate.Contracts/MemoRules.cs). Approval pins
    /// proposal hash plus KB version plus approver plus timestamp; publish
    /// is a separate activation step.</summary>
    PolicyApproval,
    /// <summary>Policy owner answers one blocking open question on a
    /// memo-to-rule proposal. Reason carries the question, Comment records
    /// the chosen option on approve. The memo waits; nothing publishes
    /// past a block.</summary>
    PolicyQuestion,
    /// <summary>A person approves a verified split award plan before any PO
    /// is created. The plan rides on Decision.Split: each line, the final
    /// country shares, and the saving. Appended last: kinds persist as
    /// numbers in case files, so existing values never shift.</summary>
    SplitApproval
}

public enum DecisionStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>One human decision card. ERP approvals link via DocumentType plus
/// DocumentNumber; dock and damage reviews cite RuleId plus evidence refs.
/// All money carries its currency.</summary>
public sealed record Decision(
    string DecisionId,
    string CaseId,
    DecisionKind Kind,
    string Title,
    DecisionStatus Status,
    string RequestedTo,
    DateTimeOffset RequestedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DocumentType,
    string? DocumentNumber,
    decimal? Amount,
    string? Currency,
    string? RuleId,
    string? Reason,
    IReadOnlyList<string> EvidenceRefs,
    string? Comment,
    /// <summary>Verified split award plan. Set only on SplitApproval cards:
    /// the lines in creation order, final country shares, totals, and the
    /// saving versus the single-vendor award.</summary>
    SplitPlan? Split = null);

/// <summary>One case file: the JSON root the agent writes and the UI reads.
/// Both sides serialize with web defaults; one file per case.</summary>
public sealed record CaseFile(
    Case Case,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<Decision> Decisions,
    AwardScreeningSnapshot? ScreeningSnapshot = null)
{
    /// <summary>Null-tolerant read: JSON that predates (or omits) a
    /// collection deserializes it as null, and a foreign file sharing the
    /// cache dir parses with every collection null. Readers see empty
    /// lists instead, so a case file never carries null collections in
    /// memory. A missing Case stays missing: the file is not a case and
    /// every scan skips it. Both lanes call this on every read.</summary>
    public CaseFile Normalized()
    {
        var file = Case is null
            ? this
            : this with
            {
                Case = Case with
                {
                    Invoices = Case.Invoices ?? [],
                    PaymentNumbers = Case.PaymentNumbers ?? [],
                },
            };
        return file with
        {
            Events = file.Events ?? [],
            Decisions = (file.Decisions ?? (IReadOnlyList<Decision>)[]).Select(NormalizeDecision).ToList(),
            ScreeningSnapshot = NormalizeSnapshot(file.ScreeningSnapshot),
        };
    }

    private static Decision NormalizeDecision(Decision decision)
    {
        var split = decision.Split;
        if (split is not null)
        {
            split = split with
            {
                Lines = (split.Lines ?? (IReadOnlyList<SplitPlanLine>)[])
                    .Select(l => l.Verdicts is null ? l with { Verdicts = [] } : l)
                    .ToList(),
                FinalShares = split.FinalShares ?? [],
            };
        }

        return decision with
        {
            EvidenceRefs = decision.EvidenceRefs ?? [],
            Split = split,
        };
    }

    private static AwardScreeningSnapshot? NormalizeSnapshot(AwardScreeningSnapshot? snapshot) =>
        snapshot is null
            ? null
            : snapshot with
            {
                Rows = (snapshot.Rows ?? (IReadOnlyList<AwardScreeningRow>)[])
                    .Select(NormalizeRow)
                    .ToList(),
            };

    private static AwardScreeningRow NormalizeRow(AwardScreeningRow row) =>
        row with
        {
            Verdicts = row.Verdicts ?? [],
            Math = row.Math is null
                ? null
                : row.Math with { Segments = row.Math.Segments ?? [] },
        };
}
