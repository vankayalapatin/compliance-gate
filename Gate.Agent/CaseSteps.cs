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
    SubmitPoApproval,
    EvaluatePrePay,
    NotifyCustomer,
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
/// held money plus open blocks. Built from adapter reads only.</summary>
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
    bool CustomerNotified);

public static class CasePicker
{
    /// <summary>Note-event marker the pre-pay step records when the ERP
    /// refuses a draft for due-date/terms reasons although accepted value
    /// exists. While the marker stands, the picker parks in AwaitDueDate
    /// instead of re-attempting the draft every advance.</summary>
    public const string PaymentNotDueMarker = "payment-not-due";

    public static bool HasPaymentNotDueMarker(CaseFile file) =>
        file.Events.Any(e => e.Kind == CaseEventKind.Note
            && e.DocumentType == "Payment"
            && (e.Summary ?? string.Empty).StartsWith(PaymentNotDueMarker, StringComparison.Ordinal));

    /// <summary>Picks the next tool for the case. Terminal stages return Done;
    /// anything needing a human, supplier, or camera input returns a waiting
    /// step naming it; otherwise the next runnable agent tool.</summary>
    public static (CaseNextStep Step, string Reason) Pick(CaseFile file, CaseErpView view)
    {
        var current = file.Case;
        if (current.Stage is CaseStage.Resolved or CaseStage.Closed or CaseStage.Cancelled)
        {
            return (CaseNextStep.Done, $"Case is {current.Stage}.");
        }

        if (!view.SoExists)
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

        if (current.Stage == CaseStage.PrSubmitted)
        {
            return view.PrStatus == Contracts.PrStatus.Approved
                ? (CaseNextStep.ScreenVendors, "PR approved; vendors not yet screened.")
                : (CaseNextStep.AwaitDecision, "PR submitted; approval decision is pending.");
        }

        if (current.Stage == CaseStage.PrApproved)
        {
            return (CaseNextStep.ScreenVendors, "PR approved; vendors not yet screened.");
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
            && !pendingKinds.Contains(DecisionKind.DockReview)
            && !pendingKinds.Contains(DecisionKind.DamageReview))
        {
            return (CaseNextStep.NotifyCustomer, "Materials secured with no open holds or dock reviews.");
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

        if (view.PaidTotal < view.AcceptedTotal)
        {
            return HasPaymentNotDueMarker(file)
                ? (CaseNextStep.AwaitDueDate, "ERP refused the draft on terms although value is payable; parked until due.")
                : (CaseNextStep.EvaluatePrePay, "Payable value exceeds paid value; run the pre-pay gate.");
        }

        var blocking = pending.Any(d => d.Kind is not (DecisionKind.ApReview or DecisionKind.BankVerification));
        if (view.HasHeldLines || view.OpenHoldCount > 0 || blocking)
        {
            return (CaseNextStep.AwaitDecision, "Nothing payable right now; held value waits on a review or reshot.");
        }

        return (CaseNextStep.FinishCase, "Paid in full with no open holds, held lines, or pending case reviews.");
    }

    /// <summary>True when the latest mail event is an outbound query with no
    /// vendor reply after it. Event order is time order (append-only).</summary>
    public static bool HasUnansweredVendorEmail(CaseFile file)
    {
        CaseEvent? latest = null;
        foreach (var e in file.Events)
        {
            if (e.Kind is CaseEventKind.VendorEmailed or CaseEventKind.VendorReplied)
            {
                latest = e;
            }
        }

        return latest is not null && latest.Kind == CaseEventKind.VendorEmailed;
    }

    /// <summary>Held invoice problems the agent cannot resolve itself: pending
    /// AP reviews on invoice docs the vendor has not been queried about yet.
    /// Each is a legal send_vendor_email target.</summary>
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
                && !emailed.Contains(d.DocumentNumber))
            .ToList();
    }

    /// <summary>Every tool Spark may legally call in this state: the read
    /// tools always, the picked step's action tool when runnable, and
    /// send_vendor_email while an unemailed invoice problem stands. Waiting
    /// steps end the advance, so they contribute no action tool.</summary>
    public static IReadOnlySet<string> LegalTools(CaseFile file, CaseErpView view)
    {
        var legal = new HashSet<string>(StringComparer.Ordinal)
        {
            AgentTools.GetCase,
            AgentTools.GetErpView,
            AgentTools.GetMail,
        };
        var action = AgentTools.ToToolName(Pick(file, view).Step);
        if (action is not null)
        {
            legal.Add(action);
        }

        if (UnemailedInvoiceProblems(file).Count > 0)
        {
            legal.Add(AgentTools.SendVendorEmail);
        }

        return legal;
    }
}
