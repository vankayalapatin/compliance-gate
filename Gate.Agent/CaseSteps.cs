// Next-step picker. The runner picks each next tool from case state plus ERP
// reads plus pending decisions; no fixed step sequence lives in code. Pick is
// a pure function of the case file and a small ERP view the runner builds
// fresh (re-reading the file) before every step.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Every tool the picker can choose, plus the waiting states it
/// parks in. Waiting steps name what external input the case needs next.</summary>
public enum CaseNextStep
{
    ComputeShortfall,
    RaisePr,
    ScreenVendors,
    AwardSupplier,
    CreatePo,
    SubmitPoApproval,
    DraftPayment,
    NotifyCustomer,
    QueryVendor,
    FinishCase,
    AwaitDecision,
    AwaitSupplierAck,
    AwaitAsn,
    AwaitDockReads,
    AwaitInvoices,
    AwaitDueDate,
    AwaitVendorReply,
    Done
}

public static class CaseNextStepExtensions
{
    public static bool IsWaiting(this CaseNextStep step) => step switch
    {
        CaseNextStep.AwaitDecision => true,
        CaseNextStep.AwaitSupplierAck => true,
        CaseNextStep.AwaitAsn => true,
        CaseNextStep.AwaitDockReads => true,
        CaseNextStep.AwaitInvoices => true,
        CaseNextStep.AwaitDueDate => true,
        CaseNextStep.AwaitVendorReply => true,
        CaseNextStep.Done => true,
        _ => false
    };
}

/// <summary>Small ERP view behind one pick: document states plus payable and
/// held money plus open blocks. Built from adapter reads only, plus the
/// pre-pay gate state and the order-level readiness the notify gate needs.</summary>
public sealed record CaseErpView(
    bool SoExists,
    PrStatus? PrStatus,
    PoApprovalStatus? PoApproval,
    bool ConfirmationExists,
    bool AsnExists,
    bool ReceiptExists,
    int MatchedInvoices,
    decimal AcceptedTotal,
    decimal PaidTotal,
    bool HasHeldLines,
    int OpenHoldCount,
    bool CustomerNotified,
    bool PrePayBlocked = false,
    string? PrePayBlockReason = null,
    bool OrderReady = false);

public static class CasePicker
{
    /// <summary>Note-event marker the pre-pay step records when the ERP
    /// refuses a draft for due-date/terms reasons although accepted value
    /// exists. The marker carries the refusing run date: runs on that date
    /// or earlier keep parking in AwaitDueDate, while a later-dated or
    /// undated run (a wider window) re-attempts the draft. An undated
    /// marker (a refusal outside any run date) stays sticky.</summary>
    public const string PaymentNotDueMarker = "payment-not-due";

    public static string PaymentNotDueMarkerFor(DateOnly? runDate) =>
        runDate.HasValue
            ? $"{PaymentNotDueMarker}:{runDate.Value:yyyy-MM-dd}"
            : PaymentNotDueMarker;

    public static bool HasPaymentNotDueMarker(CaseFile file, DateOnly? runDate = null) =>
        file.Events.Any(e => e.Kind == CaseEventKind.Note
            && e.DocumentType == "Payment"
            && MarkerBlocksRun(e.Summary, runDate));

    private static bool MarkerBlocksRun(string? summary, DateOnly? runDate)
    {
        var text = summary ?? string.Empty;
        if (!text.StartsWith(PaymentNotDueMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = text[PaymentNotDueMarker.Length..];
        if (!rest.StartsWith(":", StringComparison.Ordinal)
            || rest.Length < 11
            || !DateOnly.TryParseExact(rest.Substring(1, 10), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var markedDate))
        {
            return true;
        }

        return runDate.HasValue && runDate.Value <= markedDate;
    }

    /// <summary>Picks the next tool for the case. Terminal stages return Done;
    /// anything needing a human, supplier, or camera input returns a waiting
    /// step naming it; otherwise the next runnable agent tool. runDate is the
    /// payment run date behind this pick: a not-due marker blocks only runs
    /// on its date or earlier.</summary>
    public static (CaseNextStep Step, string Reason) Pick(CaseFile file, CaseErpView view, DateOnly? runDate = null)
    {
        var current = file.Case;
        if (current.Stage is CaseStage.Resolved or CaseStage.Closed or CaseStage.Cancelled)
        {
            return (CaseNextStep.Done, $"Case is {current.Stage}.");
        }

        if (!view.SoExists && !IsErpHandedWork(file))
        {
            return (CaseNextStep.Done, "Sales order is gone; case cannot advance.");
        }

        var pending = file.Decisions.Where(d => d.Status == DecisionStatus.Pending).ToList();
        var pendingKinds = pending.Select(d => d.Kind).ToHashSet();
        if (pendingKinds.Contains(DecisionKind.PrApproval))
        {
            return (CaseNextStep.AwaitDecision, "PR approval decision is pending.");
        }

        if (current.Stage == CaseStage.Created)
        {
            return (CaseNextStep.ComputeShortfall, "Case created; shortfall not yet computed.");
        }

        if (current.Stage == CaseStage.ShortfallComputed)
        {
            return view.PrStatus is null
                ? (CaseNextStep.RaisePr, "Shortfall computed; PR not yet raised.")
                : (CaseNextStep.AwaitDecision, "PR raised; approval decision is pending.");
        }

        if (current.Stage == CaseStage.PrSubmitted && view.PrStatus != Contracts.PrStatus.Approved)
        {
            return (CaseNextStep.AwaitDecision, "PR submitted; approval decision is pending.");
        }

        if (current.Stage is CaseStage.PrSubmitted or CaseStage.PrApproved)
        {
            if (!file.Events.Any(e => e.Kind == CaseEventKind.VendorScreened))
            {
                return (CaseNextStep.ScreenVendors, "PR approved; vendors not yet screened.");
            }

            if (current.AwardedVendorId is null)
            {
                if (pendingKinds.Contains(DecisionKind.ExceptionApproval))
                {
                    return (CaseNextStep.AwaitDecision, "CORP-02 exception decision is pending.");
                }

                return (CaseNextStep.AwardSupplier, "Vendors screened; no supplier awarded yet.");
            }

            if (current.PoNumber is null)
            {
                return (CaseNextStep.CreatePo, $"Supplier {current.AwardedVendorId} awarded; PO not yet created.");
            }
        }

        if (pendingKinds.Contains(DecisionKind.PoApproval))
        {
            return (CaseNextStep.AwaitDecision, "PO approval decision is pending.");
        }

        if (current.PoNumber is not null && view.PoApproval == PoApprovalStatus.Draft)
        {
            return (CaseNextStep.SubmitPoApproval, "PO is draft with no pending approval; resubmit.");
        }

        if (current.Stage == CaseStage.PoPlaced && !view.ConfirmationExists)
        {
            return (CaseNextStep.AwaitSupplierAck, "PO placed; waiting on the supplier ACK.");
        }

        if (current.Stage == CaseStage.PoAcknowledged && !view.AsnExists)
        {
            return (CaseNextStep.AwaitAsn, "PO acknowledged; waiting on the supplier ASN.");
        }

        if (current.Stage == CaseStage.AsnRecorded && !view.ReceiptExists)
        {
            return (CaseNextStep.AwaitAsn, "ASN recorded without a receipt; waiting on ASN re-delivery.");
        }

        if (current.Stage == CaseStage.ReceiptPosted)
        {
            return (CaseNextStep.AwaitDockReads, "Receipt posted; waiting on dock reads.");
        }

        if (!view.CustomerNotified
            && view.ReceiptExists
            && current.Stage >= CaseStage.DockChecked
            && view.OpenHoldCount == 0
            && !view.PrePayBlocked
            && !pendingKinds.Contains(DecisionKind.DockReview)
            && !pendingKinds.Contains(DecisionKind.DamageReview)
            && view.OrderReady)
        {
            return (CaseNextStep.NotifyCustomer, "Order secured: every case for the order is resolved.");
        }

        if (pendingKinds.Contains(DecisionKind.InvoiceApproval)
            || pendingKinds.Contains(DecisionKind.PaymentApproval))
        {
            return (CaseNextStep.AwaitDecision, "Invoice or payment approval decision is pending.");
        }

        if (HasUnansweredVendorEmail(file))
        {
            return (CaseNextStep.AwaitVendorReply, "Vendor query sent; waiting on the vendor reply.");
        }

        if (view.MatchedInvoices == 0)
        {
            return (CaseNextStep.AwaitInvoices, "Dock checked; waiting on the first matched invoice.");
        }

        if (pendingKinds.Contains(DecisionKind.BankVerification))
        {
            return (CaseNextStep.AwaitDecision, "Bank verification is pending; the ERP refuses drafts on unverified banks.");
        }

        if (view.PrePayBlocked)
        {
            return (CaseNextStep.AwaitDecision,
                $"Pre-pay gate blocks payment ({view.PrePayBlockReason ?? "open reviews"}); " +
                "open reviews must close before anything pays, and the ERP refuses the draft until they do.");
        }

        if (view.PaidTotal < view.AcceptedTotal)
        {
            return HasPaymentNotDueMarker(file, runDate)
                ? (CaseNextStep.AwaitDueDate, "ERP refused the draft on terms although value is payable; parked until due.")
                : (CaseNextStep.DraftPayment, "Payable value exceeds paid value; draft the next payment.");
        }

        if (view.HasHeldLines || view.OpenHoldCount > 0)
        {
            return (CaseNextStep.AwaitDecision, "Nothing payable right now; held value waits on a review or reshot.");
        }

        if (UnemailedInvoiceProblems(file).Count > 0)
        {
            return (CaseNextStep.QueryVendor, "A held invoice problem has no vendor query yet; email the vendor.");
        }

        var blocking = pending.Any(d => d.Kind is not DecisionKind.BankVerification);
        if (blocking)
        {
            return (CaseNextStep.AwaitDecision, "Nothing payable right now; an open problem waits on its human owner.");
        }

        return (CaseNextStep.FinishCase, "Paid in full with no open holds, held lines, or pending case reviews.");
    }

    /// <summary>True when a live case carries ERP-handed work without a sales
    /// order: an adopted PR, PO, or invoice the ERP handed the agent outside
    /// any shop order (see CaseRunner.Pickup.cs). Opening shells are never
    /// handed work: they stay parked for their human card even when they
    /// link a document. A live case whose non-blank SO vanished is gone,
    /// not handed, and still parks at Done.</summary>
    public static bool IsErpHandedWork(CaseFile file)
    {
        var current = file.Case;
        return !current.IsOpeningData
            && string.IsNullOrWhiteSpace(current.SoNumber)
            && (current.PrNumber is not null
                || current.PoNumber is not null
                || current.Invoices.Count > 0);
    }

    /// <summary>True when the latest mail event is an outbound query with no
    /// vendor reply after it. Event order is time order (append-only).</summary>
    /// <summary>True when a still-pending AP-review invoice problem was
    /// emailed and its reply has not arrived. Answered, decided, or never
    /// emailed problems never park the case: a stale mail about a problem
    /// nobody owns anymore must not strand the case in AwaitVendorReply.</summary>
    public static bool HasUnansweredVendorEmail(CaseFile file)
    {
        var emailed = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorEmailed
                && string.Equals(e.DocumentType, "Invoice", StringComparison.Ordinal)
                && e.DocumentNumber is not null)
            .Select(e => e.DocumentNumber!)
            .ToHashSet(StringComparer.Ordinal);
        var replied = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorReplied
                && string.Equals(e.DocumentType, "Invoice", StringComparison.Ordinal)
                && e.DocumentNumber is not null)
            .Select(e => e.DocumentNumber!)
            .ToHashSet(StringComparer.Ordinal);
        return file.Decisions.Any(d => d.Kind == DecisionKind.ApReview
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.DocumentType, "Invoice", StringComparison.Ordinal)
            && d.DocumentNumber is not null
            && emailed.Contains(d.DocumentNumber)
            && !replied.Contains(d.DocumentNumber));
    }

    /// <summary>Held invoice problems the agent cannot resolve itself: pending
    /// AP reviews on invoice docs the vendor has not been queried about yet.
    /// Each is a legal send_vendor_email target. Refs without a number are
    /// clerk-only: no query can name them, so they never qualify. A blank
    /// vendor half is fine: the query resolves the vendor from the PO.</summary>
    public static IReadOnlyList<Decision> UnemailedInvoiceProblems(CaseFile file)
    {
        var emailed = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorEmailed
                && string.Equals(e.DocumentType, "Invoice", StringComparison.Ordinal)
                && e.DocumentNumber is not null)
            .Select(e => e.DocumentNumber!)
            .ToHashSet(StringComparer.Ordinal);
        return file.Decisions
            .Where(d => d.Kind == DecisionKind.ApReview
                && d.Status == DecisionStatus.Pending
                && string.Equals(d.DocumentType, "Invoice", StringComparison.Ordinal)
                && d.DocumentNumber is not null
                && AgentTools.IsEmailableInvoiceRef(d.DocumentNumber)
                && !emailed.Contains(d.DocumentNumber))
            .ToList();
    }

    /// <summary>Every tool Spark may legally call in this state: the read
    /// tools always, plus exactly the picked step's action tool when
    /// runnable. Each step offers only its own tool: email belongs to the
    /// vendor-query step alone, never to payment or anything else. Waiting
    /// steps end the advance, so they contribute no action tool.</summary>
    public static IReadOnlySet<string> LegalTools(CaseFile file, CaseErpView view, DateOnly? runDate = null)
    {
        var legal = new HashSet<string>(StringComparer.Ordinal)
        {
            AgentTools.GetCase,
            AgentTools.GetErpView,
            AgentTools.GetMail,
            AgentTools.CheckPrePay,
        };
        var action = AgentTools.ToToolName(Pick(file, view, runDate).Step);
        if (action is not null)
        {
            legal.Add(action);
        }

        return legal;
    }
}
