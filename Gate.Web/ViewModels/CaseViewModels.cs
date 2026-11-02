// View models for the Decisions and Cases surfaces. Display shapes only;
// every value arrives from the case files, the dock-evidence sidecars, or
// the ERP adapter at runtime. No business values in code.
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>One decision card: the decision plus its case identity and any
/// resolved evidence captions (cached vision reads, ERP document summary).
/// ImageFileNames name files served by /dock-images/{name}.</summary>
public sealed record DecisionCardVm(
    Decision Decision,
    string SoNumber,
    string ItemId,
    CaseStage Stage,
    IReadOnlyList<string> EvidenceCaptions,
    IReadOnlyList<string> ImageFileNames,
    string? ErpSummary);

/// <summary>One SO-to-payment chain step for the Cases surface. IsCurrent
/// marks the step the case stage points at; at most one step per chain.</summary>
public sealed record ChainStepVm(
    string Label,
    string? DocumentNumber,
    string Status,
    string? Detail,
    bool IsCurrent = false);

/// <summary>Pure order-to-payment chain helpers: stage-to-step mapping plus
/// a no-ERP strip built straight from the case refs for the Cases list.
/// Labels must stay identical to the CaseViewService detail chain.</summary>
public static class CaseChain
{
    public const string SoLabel = "Sales order";
    public const string PrLabel = "Purchase requisition";
    public const string PoLabel = "Purchase order";
    public const string AsnLabel = "ASN";
    public const string ReceiptLabel = "Receipt";
    public const string InvoicesLabel = "Invoices";
    public const string PaymentsLabel = "Payments";

    /// <summary>Maps a case stage to its chain-step label. Cancelled maps to
    /// null: no step is current once the case is cancelled.</summary>
    public static string? StepFor(CaseStage stage) =>
        stage switch
        {
            CaseStage.Created or CaseStage.ShortfallComputed => SoLabel,
            CaseStage.PrSubmitted or CaseStage.PrApproved => PrLabel,
            CaseStage.VendorScreened or CaseStage.PoPlaced or CaseStage.PoAcknowledged => PoLabel,
            CaseStage.AsnRecorded => AsnLabel,
            CaseStage.ReceiptPosted or CaseStage.DockChecked => ReceiptLabel,
            CaseStage.InvoiceMatched => InvoicesLabel,
            CaseStage.PaymentRequested or CaseStage.PaymentPartial
                or CaseStage.Resolved or CaseStage.Closed => PaymentsLabel,
            CaseStage.Cancelled => null,
            _ => null,
        };

    /// <summary>Marks the step the stage points at. Steps keep their order;
    /// exactly one is current unless the stage maps to none.</summary>
    public static IReadOnlyList<ChainStepVm> MarkCurrent(
        IEnumerable<ChainStepVm> steps, CaseStage stage)
    {
        var current = StepFor(stage);
        return steps
            .Select(s => s with { IsCurrent = current is not null && s.Label == current })
            .ToList();
    }

    /// <summary>List-page strip from the case refs only (no ERP reads).
    /// Statuses are presence-based; the detail page enriches from the ERP.</summary>
    public static IReadOnlyList<ChainStepVm> Strip(Case kase)
    {
        var steps = new List<ChainStepVm>
        {
            new(SoLabel, kase.SoNumber, "recorded", null),
            kase.PrNumber is null
                ? new(PrLabel, null, "not created", null)
                : new(PrLabel, kase.PrNumber, "linked", null),
            kase.PoNumber is null
                ? new(PoLabel, null, "not placed", null)
                : new(PoLabel, kase.PoNumber, "linked", null),
            kase.AsnNumber is null
                ? new(AsnLabel, null, "not recorded", null)
                : new(AsnLabel, kase.AsnNumber, "recorded", null),
            kase.ReceiptNumber is null
                ? new(ReceiptLabel, null, "not posted", null)
                : new(ReceiptLabel, kase.ReceiptNumber, "posted", null),
            kase.Invoices.Count == 0
                ? new(InvoicesLabel, null, "none", null)
                : new(InvoicesLabel,
                    string.Join("; ", kase.Invoices.Select(i => $"{i.VendorId}/{i.SupplierInvoiceNumber}")),
                    $"{kase.Invoices.Count} invoice(s)", null),
            kase.PaymentNumbers.Count == 0
                ? new(PaymentsLabel, null, "none", null)
                : new(PaymentsLabel, string.Join("; ", kase.PaymentNumbers),
                    $"{kase.PaymentNumbers.Count} payment(s)", null),
        };
        return MarkCurrent(steps, kase.Stage);
    }
}

/// <summary>One thing the agent tried and was stopped from doing: a block,
/// hold, review, or wait. RuleId is null only for plain waits (no rule
/// cited); Reason always carries the human-readable cause.</summary>
public sealed record BlockedAttemptVm(
    DateTimeOffset? At,
    string What,
    string? RuleId,
    string Reason);

/// <summary>Plain-language agent summary for one case: what it did, in
/// timeline order, plus what it tried and was stopped from doing.</summary>
public sealed record AgentSummaryVm(
    IReadOnlyList<string> Did,
    IReadOnlyList<BlockedAttemptVm> Stopped);

/// <summary>Pure agent-summary builder. Every line comes from the case
/// timeline, its decisions, and its holds at runtime; code holds the
/// shaping only. Refusals, blocks, holds, reviews, and waits all land in
/// Stopped with their rule id plus reason.</summary>
public static class AgentSummary
{
    /// <summary>Event kinds that always mean the agent was stopped, even
    /// when the timeline entry carries no rule id of its own.</summary>
    public static bool IsStopKind(CaseEventKind kind) =>
        kind is CaseEventKind.VendorBlocked
            or CaseEventKind.HoldSet
            or CaseEventKind.ReviewOpened;

    public static AgentSummaryVm Build(
        Case kase,
        IEnumerable<CaseEvent> events,
        IEnumerable<Decision> decisions,
        IEnumerable<HoldDto> holds)
    {
        var ordered = events.OrderBy(e => e.Timestamp).ToList();
        var did = ordered.Select(DidLine).ToList();
        var stopped = new List<BlockedAttemptVm>();

        foreach (var e in ordered)
        {
            if (e.RuleId is null && !IsStopKind(e.Kind))
            {
                continue;
            }

            stopped.Add(new BlockedAttemptVm(
                e.Timestamp,
                CaseEventLabels.Label(e.Kind),
                e.RuleId,
                StopReason(e)));
        }

        foreach (var hold in holds.OrderBy(h => h.SetAt))
        {
            stopped.Add(new BlockedAttemptVm(
                hold.SetAt,
                $"Held {hold.DocumentType ?? "document"} {hold.DocumentNumber ?? hold.HoldId}",
                hold.RuleId,
                $"{hold.ReasonCode}: {hold.Note}"));
        }

        foreach (var d in decisions
            .Where(d => d.Status == DecisionStatus.Pending)
            .OrderBy(d => d.RequestedAt))
        {
            stopped.Add(new BlockedAttemptVm(
                d.RequestedAt,
                $"Waiting on {d.RequestedTo}: {d.Title}",
                d.RuleId,
                d.Reason ?? $"Needs a {d.Kind} decision from {d.RequestedTo}."));
        }

        if (kase.BlockedByRuleId is not null)
        {
            stopped.Add(new BlockedAttemptVm(
                kase.UpdatedAt,
                "Now blocked",
                kase.BlockedByRuleId,
                kase.BlockedReason ?? $"Blocked by rule {kase.BlockedByRuleId}."));
        }

        return new AgentSummaryVm(did, stopped);
    }

    private static string DidLine(CaseEvent e)
    {
        var line = $"{CaseEventLabels.Label(e.Kind)} — {e.Summary}";
        if (e.Amount is not null)
        {
            line += $" ({e.Amount} {e.Currency ?? "currency missing"})";
        }

        return line;
    }

    private static string StopReason(CaseEvent e)
    {
        var doc = e.DocumentType is null || e.DocumentNumber is null
            ? null
            : $"{e.DocumentType} {e.DocumentNumber}";
        return doc is null ? e.Summary : $"{doc}: {e.Summary}";
    }
}

/// <summary>Readable case display names for the Cases list and detail.
/// Keys and routes stay the case id; the name is SO plus item plus a
/// stage summary built from runtime values only.</summary>
public static class CaseDisplay
{
    public const string NoAgentRun = "no agent run";

    /// <summary>Live cases read SO plus item plus stage. Opening shells carry
    /// no SO or item, so they read their seeded document ref plus stage;
    /// a shell with no ref at all falls back to its case id.</summary>
    public static string DisplayName(Case kase)
    {
        if (!string.IsNullOrWhiteSpace(kase.SoNumber) && !string.IsNullOrWhiteSpace(kase.ItemId))
        {
            return $"SO {kase.SoNumber} · {kase.ItemId} · {StageSummary(kase.Stage)}";
        }

        var doc = kase.PrNumber is not null ? $"PR {kase.PrNumber}"
            : kase.PoNumber is not null ? $"PO {kase.PoNumber}"
            : kase.Invoices.Count > 0
                ? $"Invoice {kase.Invoices[0].VendorId}/{kase.Invoices[0].SupplierInvoiceNumber}"
            : kase.PaymentNumbers.Count > 0 ? $"Payment {kase.PaymentNumbers[0]}"
            : kase.AsnNumber is not null ? $"ASN {kase.AsnNumber}"
            : kase.ReceiptNumber is not null ? $"Receipt {kase.ReceiptNumber}"
            : kase.CaseId;
        return $"{doc} · {StageSummary(kase.Stage)}";
    }

    public static string StageSummary(CaseStage stage) =>
        stage switch
        {
            CaseStage.Created => "order created",
            CaseStage.ShortfallComputed => "shortfall computed",
            CaseStage.PrSubmitted => "PR submitted",
            CaseStage.PrApproved => "PR approved",
            CaseStage.VendorScreened => "vendor screened",
            CaseStage.PoPlaced => "PO placed",
            CaseStage.PoAcknowledged => "PO acknowledged",
            CaseStage.AsnRecorded => "ASN recorded",
            CaseStage.ReceiptPosted => "receipt posted",
            CaseStage.DockChecked => "dock checked",
            CaseStage.InvoiceMatched => "invoice matched",
            CaseStage.PaymentRequested => "payment requested",
            CaseStage.PaymentPartial => "partially paid",
            CaseStage.Resolved => "resolved",
            CaseStage.Closed => "closed",
            CaseStage.Cancelled => "cancelled",
            _ => stage.ToString(),
        };
}

public sealed record CaseDetailVm(
    Case Case,
    IReadOnlyList<ChainStepVm> Chain,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<Decision> Decisions,
    IReadOnlyList<HoldDto> OpenHolds,
    IReadOnlyList<VendorEmail> Mail,
    AgentSummaryVm Summary);

/// <summary>Human-readable timeline labels. New batch-3 event kinds get
/// clear phrases; every other kind keeps its enum name.</summary>
public static class CaseEventLabels
{
    public static string Label(CaseEventKind kind) =>
        kind switch
        {
            CaseEventKind.VendorEmailed => "Vendor emailed",
            CaseEventKind.VendorReplied => "Vendor replied",
            CaseEventKind.AgentRefused => "Agent refused",
            _ => kind.ToString(),
        };
}

/// <summary>Vendor contact-email display. A missing address renders as
/// the not-on-file placeholder, never blank and never guessed.</summary>
public static class VendorLabels
{
    public const string EmailNotOnFile = "not-on-file";

    public static string EmailOrPlaceholder(string? email) => email ?? EmailNotOnFile;
}

/// <summary>Ledger row-origin labels: agent-made rows carry the banner
/// driven by native Source; every other row stays plain.</summary>
public static class RecordLabels
{
    public static bool IsAgentMade(string? source) =>
        string.Equals(source, RecordSource.P2pAgent, StringComparison.Ordinal);
}

/// <summary>Display names for stored actors. The loader-era "system" actor
/// never renders as a person: it shows as the unset placeholder.</summary>
public static class ActorLabels
{
    public const string Unset = "—";

    public static string Display(string? actor) =>
        string.IsNullOrWhiteSpace(actor)
        || string.Equals(actor.Trim(), "system", StringComparison.OrdinalIgnoreCase)
            ? Unset
            : actor.Trim();
}

/// <summary>Stored approval comments for display. Loader-era "scenario
/// seed" comments carry no human meaning and render as the unset
/// placeholder.</summary>
public static class CommentLabels
{
    public static string Display(string? comment) =>
        string.IsNullOrWhiteSpace(comment)
        || string.Equals(comment.Trim(), "scenario seed", StringComparison.OrdinalIgnoreCase)
            ? ActorLabels.Unset
            : comment.Trim();
}

public sealed record ReceiptVm(
    ShipmentReceipt Receipt,
    IReadOnlyList<string> ImageFileNames,
    IReadOnlyList<string> CachedReads);

/// <summary>Step-by-step reset/replay report rendered on Decisions.</summary>
public sealed record ReplayReport(
    DateTimeOffset RanAtUtc,
    bool IsReplay,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    string? Error);
