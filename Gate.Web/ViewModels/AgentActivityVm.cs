// Agent activity view models: the right-side pane on every page. Three
// parts from existing case events and decisions only: Now (one plain line
// or Idle), Waiting On (each open case's next step plus owner plus link),
// Recent (newest-first feed grouped under one header per case). Every line
// names its item and order so sibling cases on one order never mix; people
// actions (approvals, rejections) render on the right, agent entries on
// the left, refused attempts with an amber edge. Lines stay under 25 plain
// words with no internal ids, stage codes, or step codes.
using System.Globalization;
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

public sealed record ActivityWaitingRow(string Text, string Href);

public enum ActivitySide
{
    Agent,
    Person,
}

public sealed record ActivityRecentLine(
    string Text,
    string Ago,
    DateTimeOffset At,
    ActivitySide Side,
    bool Refused,
    string Href);

public sealed record ActivityRecentGroup(
    string HeaderText,
    string CaseHref,
    IReadOnlyList<ActivityRecentLine> Lines);

public sealed record AgentActivityVm(
    string Now,
    IReadOnlyList<ActivityWaitingRow> Waiting,
    IReadOnlyList<ActivityRecentGroup> Recent);

/// <summary>Display lookups the service feeds the pure render rules: vendor
/// names, item descriptions, and the auto-approve threshold (null when the
/// ERP read failed, so policy lines stay honest about what is unknown).
/// No reads here: the service feeds these data.</summary>
public sealed record ActivityLookups(
    IReadOnlyDictionary<string, string> Vendors,
    IReadOnlyDictionary<string, string> Items,
    decimal? AutoApproveThreshold);

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

    /// <summary>Short absolute stamp under each bubble. Invariant culture so
    /// every operator sees the same shape.</summary>
    public static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    public static string VendorName(ActivityLookups lookups, string vendorId) =>
        lookups.Vendors.TryGetValue(vendorId, out var name) ? name : vendorId;

    public static string ItemLabel(ActivityLookups lookups, string itemId) =>
        lookups.Items.TryGetValue(itemId, out var description) ? description : itemId;

    /// <summary>Case header for waiting rows, the Now line, and recent
    /// groups: item plus order, never the internal case id.</summary>
    public static string CaseHeader(CaseFile file, ActivityLookups lookups) =>
        $"{ItemLabel(lookups, file.Case.ItemId)} for {file.Case.SoNumber}";

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
        CaseNextStep.ProposeSplit => "Proposing a split",
        CaseNextStep.CreateSplitChildren => "Placing the split orders",
        CaseNextStep.AwaitSplitChildren => "Split orders",
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
        DecisionKind.PrApproval => "Purchase request approval",
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
        DecisionKind.SplitApproval => "Split approval",
        _ => "Review",
    };

    public static string DocPhrase(
        ActivityLookups lookups, string? documentType, string? documentNumber)
    {
        if (documentType is null || documentNumber is null)
        {
            return string.Empty;
        }

        if (string.Equals(documentType, "Invoice", StringComparison.Ordinal))
        {
            var parts = documentNumber.Split('/', 2);
            return parts.Length == 2
                ? $"invoice {parts[1]} from {VendorName(lookups, parts[0])}"
                : $"invoice {documentNumber}";
        }

        return documentType switch
        {
            "PR" => $"request {documentNumber}",
            "PO" => $"purchase order {documentNumber}",
            "Payment" => $"payment {documentNumber}",
            "Receipt" => $"receipt {documentNumber}",
            "ASN" => $"shipment {documentNumber}",
            "Vendor" or "VendorBank" => $"vendor {VendorName(lookups, documentNumber)}",
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

    /// <summary>One waiting row: the case header plus its next step, owner,
    /// and link. poHint carries the latest purchase-order approval outcome
    /// for supplier-acknowledgement waits (auto-approved under the threshold
    /// or approved by a named person); every other wait passes null.</summary>
    public static ActivityWaitingRow WaitingRow(
        CaseFile file, CaseNextStep step, ActivityLookups lookups, bool nowIdle, string? poHint = null)
    {
        var header = CaseHeader(file, lookups);
        var caseHref = $"cases/{file.Case.CaseId}";
        if (step == CaseNextStep.AwaitDecision)
        {
            var pending = file.Decisions
                .Where(d => d.Status == DecisionStatus.Pending)
                .MinBy(d => d.RequestedAt);
            if (pending is not null)
            {
                var doc = DocPhrase(lookups, pending.DocumentType, pending.DocumentNumber);
                var what = doc.Length == 0
                    ? KindWords(pending.Kind)
                    : $"{KindWords(pending.Kind)} {doc}";
                return new ActivityWaitingRow(
                    Cap($"{header}: {what} waits on {ActorLabels.Display(pending.RequestedTo)}."),
                    DocHref(pending.DocumentType, pending.DocumentNumber));
            }

            return new ActivityWaitingRow(Cap($"{header}: held goods wait on the warehouse."), caseHref);
        }

        if (!step.IsWaiting())
        {
            // While idle the agent waits on nobody: a case parked on an
            // agent step reads as stopped, with a link back to the case.
            return nowIdle
                ? new ActivityWaitingRow(Cap($"{header}: agent step stopped: {StepWords(step)}. Try again."), caseHref)
                : new ActivityWaitingRow(Cap($"{header}: {StepWords(step)}: agent."), caseHref);
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
            var vendor = VendorFor(file, lookups);
            if (vendor is not null)
            {
                owner = vendor;
            }
        }

        var prefix = poHint is null ? string.Empty : poHint + " ";
        return new ActivityWaitingRow(Cap($"{header}: {prefix}{StepWords(step)} waits on {owner}."), caseHref);
    }

    public static string? VendorFor(CaseFile file, ActivityLookups lookups)
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
            return VendorName(lookups, emailed);
        }

        return file.Case.AwardedVendorId is null
            ? null
            : VendorName(lookups, file.Case.AwardedVendorId);
    }

    public static string? EventLine(CaseFile file, CaseEvent e, ActivityLookups lookups)
    {
        var so = file.Case.SoNumber;
        var doc = e.DocumentNumber ?? string.Empty;
        return e.Kind switch
        {
            CaseEventKind.OrderReceived => $"Order {so} arrived for {file.Case.ItemId}.",
            CaseEventKind.ShortfallComputed => $"Stock check done for {file.Case.ItemId} on order {so}.",
            CaseEventKind.PrCreated => $"Request {doc} raised for order {so}.",
            CaseEventKind.PrSubmitted => $"Request {doc} sent for approval.",
            CaseEventKind.VendorScreened => ScreenedLine(file, e, lookups),
            CaseEventKind.VendorBlocked => BlockedLine(file, e, lookups),
            CaseEventKind.SupplierAwarded => file.Case.AwardedVendorId is null
                ? $"Order {so} buying awarded to a supplier."
                : $"Order {so} buying awarded to {VendorName(lookups, file.Case.AwardedVendorId)}.",
            CaseEventKind.PoCreated => $"Purchase order {doc} placed for order {so}.",
            CaseEventKind.PrApproved => PolicyApprovalLine(file, e, "Request", lookups),
            CaseEventKind.PoApproved => PolicyApprovalLine(file, e, "Purchase order", lookups),
            CaseEventKind.VendorAcknowledged => $"Supplier confirmed purchase order {doc}.",
            CaseEventKind.AsnRecorded => $"Shipment notice {doc} recorded for order {so}.",
            CaseEventKind.ReceiptPosted => $"Receipt {doc} posted for order {so}.",
            CaseEventKind.DockEvaluated => $"Dock check done for order {so}.",
            CaseEventKind.HoldSet => $"Quarantine hold set on receipt {doc} for order {so}.",
            CaseEventKind.HoldReleased => $"Quarantine hold on receipt {doc} released.",
            CaseEventKind.ReviewOpened => $"Human review opened for receipt {doc}.",
            CaseEventKind.InvoiceCreated => $"Invoice {InvoiceShort(lookups, doc)} recorded.",
            CaseEventKind.InvoiceMatched => e.Amount is null
                ? $"Invoice {InvoiceShort(lookups, doc)} matched."
                : $"Invoice {InvoiceShort(lookups, doc)} matched: accepted {Money(e.Amount.Value, e.Currency)}.",
            CaseEventKind.PaymentRequested => e.Amount is null
                ? $"Payment {doc} drafted."
                : $"Payment {doc} drafted for {Money(e.Amount.Value, e.Currency)}.",
            CaseEventKind.PaymentPosted => $"Payment {doc} posted.",
            CaseEventKind.CustomerNotified => $"Customer told order {so} is secured.",
            CaseEventKind.VendorEmailed => $"Emailed about invoice {InvoiceShort(lookups, doc)}.",
            CaseEventKind.VendorReplied => $"Supplier replied about invoice {InvoiceShort(lookups, doc)}.",
            CaseEventKind.AgentRefused => e.Summary,
            CaseEventKind.Note => NoteLine(file, e, lookups),
            // Human approval and review outcomes render from the decisions
            // themselves, which carry who decided and when.
            CaseEventKind.PrRejected or CaseEventKind.ReviewClosed => null,
            _ => null,
        };
    }

    /// <summary>Screening line naming the vendor and the blocking rule:
    /// "CinderVolt Energy blocked by CORP-02 for order SO-1001." Passes
    /// name the vendor with no rule.</summary>
    public static string ScreenedLine(CaseFile file, CaseEvent e, ActivityLookups lookups)
    {
        var vendorId = OriginCapMath.TryParseScreenedVendor(e.Summary);
        var vendor = vendorId is null ? "A vendor" : VendorName(lookups, vendorId);
        return e.RuleId is null
            ? $"{vendor} passed screening for order {file.Case.SoNumber}."
            : $"{vendor} blocked by {e.RuleId} for order {file.Case.SoNumber}.";
    }

    /// <summary>Legacy vendor-block line, same naming as screening.</summary>
    public static string BlockedLine(CaseFile file, CaseEvent e, ActivityLookups lookups)
    {
        var vendorId = OriginCapMath.TryParseBlockedVendor(e.Summary);
        var vendor = vendorId is null ? "A vendor" : VendorName(lookups, vendorId);
        return e.RuleId is null
            ? $"{vendor} blocked for order {file.Case.SoNumber}."
            : $"{vendor} blocked by {e.RuleId} for order {file.Case.SoNumber}.";
    }

    /// <summary>Approval-event line: human approvals defer to their decision
    /// row; an approval with no decision behind it is a policy
    /// auto-approval and reads as one, with the threshold when known.</summary>
    public static string? PolicyApprovalLine(
        CaseFile file, CaseEvent e, string docWords, ActivityLookups lookups)
    {
        if (e.DocumentNumber is null)
        {
            return null;
        }

        var covered = file.Decisions.Any(d =>
            string.Equals(d.DocumentType, e.DocumentType, StringComparison.Ordinal)
            && string.Equals(d.DocumentNumber, e.DocumentNumber, StringComparison.Ordinal));
        if (covered)
        {
            return null;
        }

        return lookups.AutoApproveThreshold is { } threshold
            ? $"{docWords} {e.DocumentNumber} auto-approved (under {Money(threshold, null)})."
            : $"{docWords} {e.DocumentNumber} auto-approved by policy.";
    }

    public static string? NoteLine(CaseFile file, CaseEvent e, ActivityLookups lookups)
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

        // Award rejections name the vendor, not the id.
        var rejectedId = OriginCapMath.TryParseBlockedVendor(e.Summary);
        if (rejectedId is not null && e.RuleId is not null)
        {
            var marker = $"{e.RuleId}: ";
            var reason = e.Summary.Contains(marker, StringComparison.Ordinal)
                ? e.Summary[(e.Summary.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
                : e.Summary;
            return $"{VendorName(lookups, rejectedId)} blocked by {e.RuleId} for order {file.Case.SoNumber}: {reason}";
        }

        return e.Summary;
    }

    public static string InvoiceShort(ActivityLookups lookups, string doc)
    {
        var parts = doc.Split('/', 2);
        return parts.Length == 2
            ? $"{parts[1]} from {VendorName(lookups, parts[0])}"
            : doc;
    }

    public static string? DecidedLine(ActivityLookups lookups, Decision d)
    {
        if (d.Status == DecisionStatus.Pending || d.DecidedBy is null)
        {
            return null;
        }

        var verdict = d.Status == DecisionStatus.Approved ? "approved" : "rejected";
        var doc = DocPhrase(lookups, d.DocumentType, d.DocumentNumber);
        // Reviews decide the review, not the document: "Del approved dock
        // review for receipt RCT-1035", never "approved receipt RCT-1035".
        // Review lines carry no amount: the review is not about money.
        if (d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview or DecisionKind.ApReview
            or DecisionKind.BankVerification or DecisionKind.VendorIdentity
            or DecisionKind.ExceptionApproval or DecisionKind.PolicyQuestion
            or DecisionKind.SplitApproval)
        {
            var review = doc.Length == 0
                ? KindWords(d.Kind).ToLowerInvariant()
                : $"{KindWords(d.Kind).ToLowerInvariant()} for {doc}";
            return $"{ActorLabels.Display(d.DecidedBy)} {verdict} {review}.";
        }

        var what = doc.Length == 0 ? KindWords(d.Kind).ToLowerInvariant() : doc;
        var amount = d.Amount is null ? string.Empty : $" for {Money(d.Amount.Value, d.Currency)}";
        return $"{ActorLabels.Display(d.DecidedBy)} {verdict} {what}{amount}.";
    }
}
