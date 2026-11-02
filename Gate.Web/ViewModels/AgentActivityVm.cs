// Agent activity view models: the right-side pane on every page. Three
// parts from existing case events and decisions only: Now (one plain line
// or Idle), Waiting On (each open case's next step plus owner plus link),
// Recent (newest-first feed). Every line stays under 25 plain words with
// no internal ids, stage codes, or step codes.
using System.Globalization;
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

public sealed record ActivityWaitingRow(string Text, string Href);

public sealed record ActivityRecentLine(string Text, string Ago);

public sealed record AgentActivityVm(
    string Now,
    IReadOnlyList<ActivityWaitingRow> Waiting,
    IReadOnlyList<ActivityRecentLine> Recent);

/// <summary>Pure render rules for the activity pane: step and kind words,
/// document phrases and links, event and decision lines, the waiting row
/// join, and the word cap. No reads: the service feeds these data.</summary>
public static class ActivityLines
{
    public const int MaxWords = 25;

    public static string Cap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= MaxWords ? string.Join(' ', words) : string.Join(' ', words.Take(MaxWords)) + " …";
    }

    public static string Ago(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed.TotalSeconds < 60)
        {
            return "just now";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{(int)elapsed.TotalMinutes} min ago";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{(int)elapsed.TotalHours} hr ago";
        }

        return at.ToLocalTime().ToString("MMM d");
    }

    public static string VendorName(IReadOnlyDictionary<string, string> vendors, string vendorId) =>
        vendors.TryGetValue(vendorId, out var name) ? name : vendorId;

    /// <summary>Pane money: $ with thousands separators, since the pane
    /// speaks to operators, not ledgers. Unknown currencies keep the
    /// amount-plus-code shape rather than guessing a symbol.</summary>
    public static string Money(decimal amount, string? currency) =>
        currency is null || string.Equals(currency, "USD", StringComparison.Ordinal)
            ? $"${amount.ToString("N2", CultureInfo.InvariantCulture)}"
            : $"{amount.ToString("N2", CultureInfo.InvariantCulture)} {currency}";

    public static string StepWords(CaseNextStep step) => step switch
    {
        CaseNextStep.ComputeShortfall => "Checking stock",
        CaseNextStep.RaisePr => "Raising the request",
        CaseNextStep.ScreenVendors => "Screening vendors",
        CaseNextStep.AwardSupplier => "Awarding the supplier",
        CaseNextStep.CreatePo => "Placing the order",
        CaseNextStep.SubmitPoApproval => "Submitting the approval",
        CaseNextStep.DraftPayment => "Drafting the payment",
        CaseNextStep.NotifyCustomer => "Notifying the customer",
        CaseNextStep.QueryVendor => "Emailing the supplier",
        CaseNextStep.FinishCase or CaseNextStep.Done => "Closing the case",
        CaseNextStep.AwaitDecision => "A decision",
        CaseNextStep.AwaitSupplierAck => "Supplier acknowledgement",
        CaseNextStep.AwaitAsn => "Shipment notice",
        CaseNextStep.AwaitDockReads => "Dock photos",
        CaseNextStep.AwaitInvoices => "Supplier invoice",
        CaseNextStep.AwaitDueDate => "Payment due date",
        CaseNextStep.AwaitVendorReply => "Supplier reply",
        _ => "Next step",
    };

    public static string KindWords(DecisionKind kind) => kind switch
    {
        DecisionKind.PrApproval => "Request approval",
        DecisionKind.PoApproval => "Purchase order approval",
        DecisionKind.InvoiceApproval => "Invoice approval",
        DecisionKind.PaymentApproval => "Payment approval",
        DecisionKind.ApReview => "Invoice review",
        DecisionKind.BankVerification => "Bank check",
        DecisionKind.DockReview => "Dock review",
        DecisionKind.DamageReview => "Damage review",
        DecisionKind.ExceptionApproval => "Exception review",
        DecisionKind.VendorIdentity => "Vendor check",
        DecisionKind.PolicyApproval => "Policy approval",
        DecisionKind.PolicyQuestion => "Policy question",
        _ => "Review",
    };

    public static string DocPhrase(
        IReadOnlyDictionary<string, string> vendors, string? documentType, string? documentNumber)
    {
        if (documentType is null || documentNumber is null)
        {
            return string.Empty;
        }

        if (string.Equals(documentType, "Invoice", StringComparison.Ordinal))
        {
            var parts = documentNumber.Split('/', 2);
            return parts.Length == 2
                ? $"invoice {parts[1]} from {VendorName(vendors, parts[0])}"
                : $"invoice {documentNumber}";
        }

        return documentType switch
        {
            "PR" => $"request {documentNumber}",
            "PO" => $"purchase order {documentNumber}",
            "Payment" => $"payment {documentNumber}",
            "Receipt" => $"receipt {documentNumber}",
            "ASN" => $"shipment {documentNumber}",
            "Vendor" or "VendorBank" => $"vendor {VendorName(vendors, documentNumber)}",
            "SO" or "SalesOrder" => $"order {documentNumber}",
            _ => $"{documentType} {documentNumber}",
        };
    }

    public static string DocHref(string? documentType, string? documentNumber)
    {
        if (documentType is null || documentNumber is null)
        {
            return "decisions";
        }

        if (string.Equals(documentType, "Invoice", StringComparison.Ordinal))
        {
            var parts = documentNumber.Split('/', 2);
            return parts.Length == 2
                ? ErpRoutes.RecordHref("invoice", parts[0], parts[1])
                : "decisions";
        }

        return documentType switch
        {
            "PR" => ErpRoutes.RecordHref("pr", documentNumber),
            "PO" => ErpRoutes.RecordHref("po", documentNumber),
            "Payment" => ErpRoutes.RecordHref("payment", documentNumber),
            "Receipt" => ErpRoutes.RecordHref("receipt", documentNumber),
            "ASN" => ErpRoutes.RecordHref("asn", documentNumber),
            "Vendor" or "VendorBank" => ErpRoutes.RecordHref("vendor", documentNumber),
            "SO" or "SalesOrder" => ErpRoutes.RecordHref("so", documentNumber),
            _ => "decisions",
        };
    }

    public static string NowVerb(CaseEventKind kind) => kind switch
    {
        CaseEventKind.OrderReceived => "Taking the customer order",
        CaseEventKind.ShortfallComputed => "Checking stock",
        CaseEventKind.PrCreated => "Raising the request",
        CaseEventKind.PrSubmitted => "Sending the request",
        CaseEventKind.PrApproved or CaseEventKind.PrRejected => "Recording the request decision",
        CaseEventKind.VendorScreened => "Screening vendors",
        CaseEventKind.VendorBlocked => "Blocking a vendor",
        CaseEventKind.SupplierAwarded => "Awarding the supplier",
        CaseEventKind.PoCreated => "Placing the order",
        CaseEventKind.PoApproved => "Recording the order approval",
        CaseEventKind.VendorAcknowledged => "Recording the acknowledgement",
        CaseEventKind.AsnRecorded => "Recording the shipment",
        CaseEventKind.ReceiptPosted => "Posting the receipt",
        CaseEventKind.DockEvaluated => "Reading dock photos",
        CaseEventKind.HoldSet => "Setting a hold",
        CaseEventKind.HoldReleased => "Releasing a hold",
        CaseEventKind.ReviewOpened => "Opening a review",
        CaseEventKind.ReviewClosed => "Closing a review",
        CaseEventKind.InvoiceCreated => "Recording the invoice",
        CaseEventKind.InvoiceMatched => "Matching the invoice",
        CaseEventKind.PaymentRequested => "Drafting the payment",
        CaseEventKind.PaymentPosted => "Posting the payment",
        CaseEventKind.CustomerNotified => "Notifying the customer",
        CaseEventKind.VendorEmailed => "Emailing the supplier",
        CaseEventKind.VendorReplied => "Reading the supplier reply",
        CaseEventKind.AgentRefused => "Retrying after a refusal",
        _ => "Working",
    };

    public static ActivityWaitingRow WaitingRow(
        CaseFile file, CaseNextStep step, IReadOnlyDictionary<string, string> vendors, bool nowIdle)
    {
        var caseHref = $"cases/{file.Case.CaseId}";
        if (step == CaseNextStep.AwaitDecision)
        {
            var pending = file.Decisions
                .Where(d => d.Status == DecisionStatus.Pending)
                .MinBy(d => d.RequestedAt);
            if (pending is not null)
            {
                var doc = DocPhrase(vendors, pending.DocumentType, pending.DocumentNumber);
                var what = doc.Length == 0
                    ? KindWords(pending.Kind)
                    : $"{KindWords(pending.Kind)} {doc}";
                return new ActivityWaitingRow(
                    Cap($"{what} waits on {ActorLabels.Display(pending.RequestedTo)}."),
                    DocHref(pending.DocumentType, pending.DocumentNumber));
            }

            return new ActivityWaitingRow(Cap("Held goods wait on the warehouse."), caseHref);
        }

        if (!step.IsWaiting())
        {
            // While idle the agent waits on nobody: a case parked on an
            // agent step reads as stopped, with a link back to the case.
            return nowIdle
                ? new ActivityWaitingRow(Cap($"Agent step stopped: {StepWords(step)}. Try again."), caseHref)
                : new ActivityWaitingRow(Cap($"{StepWords(step)} for order {file.Case.SoNumber}: agent."), caseHref);
        }

        var owner = step switch
        {
            CaseNextStep.AwaitDockReads => "the warehouse",
            CaseNextStep.AwaitDueDate => "the calendar",
            _ => "the supplier",
        };
        if (step is CaseNextStep.AwaitSupplierAck or CaseNextStep.AwaitAsn
            or CaseNextStep.AwaitInvoices or CaseNextStep.AwaitVendorReply)
        {
            var vendor = VendorFor(file, vendors);
            if (vendor is not null)
            {
                owner = vendor;
            }
        }

        return new ActivityWaitingRow(Cap($"{StepWords(step)} for order {file.Case.SoNumber} waits on {owner}."), caseHref);
    }

    public static string? VendorFor(CaseFile file, IReadOnlyDictionary<string, string> vendors)
    {
        // Prefer the vendor on the newest emailed invoice problem; fall back
        // to the awarded vendor. No reads: names resolve from the map.
        var emailed = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorEmailed
                && string.Equals(e.DocumentType, "Invoice", StringComparison.Ordinal)
                && e.DocumentNumber is not null)
            .MaxBy(e => e.Timestamp)?.DocumentNumber?.Split('/', 2).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(emailed))
        {
            return VendorName(vendors, emailed);
        }

        return file.Case.AwardedVendorId is null
            ? null
            : VendorName(vendors, file.Case.AwardedVendorId);
    }

    public static string? EventLine(CaseFile file, CaseEvent e, IReadOnlyDictionary<string, string> vendors)
    {
        var so = file.Case.SoNumber;
        var doc = e.DocumentNumber ?? string.Empty;
        return e.Kind switch
        {
            CaseEventKind.OrderReceived => $"Order {so} arrived for {file.Case.ItemId}.",
            CaseEventKind.ShortfallComputed => $"Stock check done for {file.Case.ItemId} on order {so}.",
            CaseEventKind.PrCreated => $"Request {doc} raised for order {so}.",
            CaseEventKind.PrSubmitted => $"Request {doc} sent for approval.",
            CaseEventKind.VendorScreened => e.RuleId is null
                ? $"A vendor passed screening for order {so}."
                : $"A vendor failed screening for order {so}: {e.RuleId}.",
            CaseEventKind.VendorBlocked => $"A vendor failed screening for order {so}: {e.RuleId ?? "blocked"}.",
            CaseEventKind.SupplierAwarded => file.Case.AwardedVendorId is null
                ? $"Order {so} buying awarded to a supplier."
                : $"Order {so} buying awarded to {VendorName(vendors, file.Case.AwardedVendorId)}.",
            CaseEventKind.PoCreated => $"Purchase order {doc} placed for order {so}.",
            CaseEventKind.VendorAcknowledged => $"Supplier confirmed purchase order {doc}.",
            CaseEventKind.AsnRecorded => $"Shipment notice {doc} recorded for order {so}.",
            CaseEventKind.ReceiptPosted => $"Receipt {doc} posted for order {so}.",
            CaseEventKind.DockEvaluated => $"Dock check done for order {so}.",
            CaseEventKind.HoldSet => $"Quarantine hold set on receipt {doc} for order {so}.",
            CaseEventKind.HoldReleased => $"Quarantine hold on receipt {doc} released.",
            CaseEventKind.ReviewOpened => $"Human review opened for receipt {doc}.",
            CaseEventKind.InvoiceCreated => $"Invoice {InvoiceShort(vendors, doc)} recorded.",
            CaseEventKind.InvoiceMatched => e.Amount is null
                ? $"Invoice {InvoiceShort(vendors, doc)} matched."
                : $"Invoice {InvoiceShort(vendors, doc)} matched: accepted {Money(e.Amount.Value, e.Currency)}.",
            CaseEventKind.PaymentRequested => e.Amount is null
                ? $"Payment {doc} drafted."
                : $"Payment {doc} drafted for {Money(e.Amount.Value, e.Currency)}.",
            CaseEventKind.PaymentPosted => $"Payment {doc} posted.",
            CaseEventKind.CustomerNotified => $"Customer told order {so} is secured.",
            CaseEventKind.VendorEmailed => $"Emailed about invoice {InvoiceShort(vendors, doc)}.",
            CaseEventKind.VendorReplied => $"Supplier replied about invoice {InvoiceShort(vendors, doc)}.",
            CaseEventKind.AgentRefused => e.Summary,
            CaseEventKind.Note => NoteLine(e),
            // Approval and review outcomes render from the decisions
            // themselves, which carry who decided and when.
            CaseEventKind.PrApproved or CaseEventKind.PrRejected or CaseEventKind.PoApproved
                or CaseEventKind.ReviewClosed => null,
            _ => null,
        };
    }

    public static string? NoteLine(CaseEvent e)
    {
        // Meta notes stay out of the feed: the candidate dump, aborted-turn
        // traces (failures surface through outcomes), and payment-run
        // markers. Clerk-meaningful notes render as written.
        if (string.Equals(e.DocumentType, "Candidates", StringComparison.Ordinal)
            || e.Summary.StartsWith("Turn aborted", StringComparison.Ordinal)
            || e.Summary.StartsWith(CasePicker.PaymentNotDueMarker, StringComparison.Ordinal))
        {
            return null;
        }

        return e.Summary;
    }

    public static string InvoiceShort(IReadOnlyDictionary<string, string> vendors, string doc)
    {
        var parts = doc.Split('/', 2);
        return parts.Length == 2
            ? $"{parts[1]} from {VendorName(vendors, parts[0])}"
            : doc;
    }

    public static string? DecidedLine(IReadOnlyDictionary<string, string> vendors, Decision d)
    {
        if (d.Status == DecisionStatus.Pending || d.DecidedBy is null)
        {
            return null;
        }

        var verdict = d.Status == DecisionStatus.Approved ? "approved" : "rejected";
        var doc = DocPhrase(vendors, d.DocumentType, d.DocumentNumber);
        var what = doc.Length == 0 ? KindWords(d.Kind).ToLowerInvariant() : doc;
        var amount = d.Amount is null ? string.Empty : $" for {Money(d.Amount.Value, d.Currency)}";
        return $"{ActorLabels.Display(d.DecidedBy)} {verdict} {what}{amount}.";
    }
}

