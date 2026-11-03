// Outside-world view models: the customer shop, supplier inbox, and dock
// camera. Display and form shapes only; every option value arrives from the
// case cache, the dock image directory, or the ERP adapter at runtime. No
// business values in code.
//
// Supplier-minted numbers (ASN number, packing-slip number, lot refs,
// invoice number) arrive blank: the supplier types them as printed when
// sending, and the human can change every prefilled row before sending.
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>One ASN carton row in the supplier inbox. Ship-plan defaults
/// derive from the runtime PO lines plus item pack sizes; the human edits
/// every row before sending. Problem is the simulated carton problem picked
/// for the row (a DockProblemKinds wire name, or null for none).</summary>
public sealed record AsnCartonForm(
    string CartonId,
    string ItemId,
    int UnitsPerCarton,
    string LotRef,
    string? Problem = null);

/// <summary>Supplier ASN submission: the supplier declares what shipped. A
/// null list means no cartons were picked (a refusal, never a guess).</summary>
public sealed record AsnSubmission(
    string CaseId,
    string AsnNumber,
    string PackingSlipNumber,
    IReadOnlyList<AsnCartonForm>? Cartons);

/// <summary>Purchase-order snapshot shown in the supplier inbox: our PO as
/// the supplier sees it, straight from the ERP at runtime.</summary>
public sealed record InboxPoVm(
    string PoNumber,
    string VendorId,
    string PaymentTermsId,
    string ApprovalStatus,
    string DocStatus,
    string Currency,
    IReadOnlyList<InboxPoLineVm> Lines,
    decimal Total);

/// <summary>One inbox PO line with its runtime quantities and price.</summary>
public sealed record InboxPoLineVm(
    string PoLineRef,
    string ItemId,
    decimal QtyOrdered,
    decimal UnitPrice,
    decimal LineAmount);

/// <summary>One thing the supplier already sent on this PO, with its date:
/// acknowledgement (supplier confirm date), shipment notice (received
/// date from the case file), or invoice (recorded date). The portal
/// renders these as the sent history above the next action.</summary>
public sealed record SupplierSentStep(
    string Step,
    string Detail);

/// <summary>The one supplier action that makes sense next on this PO.
/// The portal offers only this action; anything else waits.</summary>
public static class SupplierNextAction
{
    public const string WaitCase = "wait-case";
    public const string WaitApproval = "wait-approval";
    public const string Acknowledge = "acknowledge";
    public const string Ship = "ship";
    public const string Invoice = "invoice";
    public const string Done = "done";
    public const string Closed = "closed";
    public const string Unknown = "unknown";
}

/// <summary>Supplier-inbox prefill for one case: our PO, the agent mail
/// thread, ASN plus invoice defaults, and the reply suggestion. Nulls mean
/// the runtime source had nothing; the human types the value.</summary>
public sealed record SupplierInboxVm(
    string CaseId,
    InboxPoVm? Po,
    IReadOnlyList<VendorEmail> Mail,
    AsnSubmission? AsnPrefill,
    InvoicePrefill? InvoicePrefill,
    ReplySuggestion Reply,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SupplierSentStep> SentSteps,
    string NextAction);

/// <summary>Invoice prefill derived from the runtime PO lines. The number is
/// an editable supplier-minted default; lines mirror ordered qty/price.</summary>
public sealed record InvoicePrefill(
    string Number,
    string? VendorId,
    string? PoNumber,
    IReadOnlyList<InvoiceLineForm> Lines);

/// <summary>One supplier PO in the portal list: header plus lines plus
/// total, straight from the ERP at runtime.</summary>
public sealed record SupplierPoList(
    IReadOnlyList<InboxPoVm> Pos,
    IReadOnlyList<string> Notes);

/// <summary>New shop-order lines for the document-driven shop. No SO number:
/// the shop mints a shop order id internally and the ERP mints the SO.
/// EntityCode is the company entity from the ERP.</summary>
public sealed record NewShopOrder(
    string EntityCode,
    string? Requester,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CandidateVendorsByItem,
    IReadOnlyList<OrderLineForm> Lines,
    string? Currency);

/// <summary>Placed shop order: the POST outcome plus the shop order id sent
/// and the SO numbers the ERP minted (resolved from the new case files).</summary>
public sealed record ShopOrderPlaced(
    SimulationOutcome Outcome,
    string ShopOrderId,
    IReadOnlyList<string> MintedSoNumbers);

/// <summary>One carton on an expected truck, straight from the ASN.</summary>
public sealed record ExpectedTruckCarton(
    string CartonId,
    string ItemId,
    int UnitsPerCarton,
    string LotRef);

/// <summary>One expected truck at the dock: an ASN awaiting dock evidence
/// plus its PO, vendor, linked case, and cartons. CaseId is null when no
/// case links the ASN yet; the photo forms wait for the case.</summary>
public sealed record ExpectedTruckVm(
    string AsnNumber,
    string PoNumber,
    string? VendorId,
    string? CaseId,
    IReadOnlyList<ExpectedTruckCarton> Cartons);

/// <summary>Expected-truck list plus notes for sources that were missing or
/// failed. Empty list (never null) renders an honest empty state.</summary>
public sealed record ExpectedTrucks(
    IReadOnlyList<ExpectedTruckVm> Trucks,
    IReadOnlyList<string> Notes);

/// <summary>Pure ship-plan builder: cartons per PO line from the item pack
/// size. Count is ceil(ordered qty / units per carton); ids derive from
/// PO plus line ref plus per-line sequence; the last carton carries the
/// remainder so the plan totals the ordered qty. Missing pack sizes yield
/// one editable row with zero units plus a note; nothing is guessed.</summary>
public static class OutsideShipPlan
{
    public static IReadOnlyList<AsnCartonForm> Build(
        IReadOnlyList<InboxPoLineVm> lines,
        Func<string, int?> unitsPerCarton,
        List<string> notes)
    {
        // Supplier-style labels: cartons and lots number in one global
        // sequence across the shipment (CTN-01..., LOT-001...), the way a
        // supplier stencil reads. No PO number ever lands on a carton id.
        var cartons = new List<AsnCartonForm>();
        var seq = 0;
        foreach (var line in lines)
        {
            var units = unitsPerCarton(line.ItemId);
            if (units is null || units <= 0)
            {
                seq++;
                var fallbackId = $"CTN-{seq:00}";
                notes.Add(
                    $"Item {line.ItemId} has no pack size; carton {fallbackId} starts empty.");
                cartons.Add(new AsnCartonForm(fallbackId, line.ItemId, 0, $"LOT-{seq:000}"));
                continue;
            }

            if (line.QtyOrdered <= 0)
            {
                notes.Add(
                    $"PO line {line.PoLineRef} orders {line.QtyOrdered}; no cartons planned for it.");
                continue;
            }

            var count = (int)Math.Ceiling(line.QtyOrdered / units.Value);
            if (count <= 0)
            {
                continue;
            }

            for (var lineSeq = 1; lineSeq <= count; lineSeq++)
            {
                seq++;
                var id = $"CTN-{seq:00}";
                var inCarton = lineSeq < count
                    ? units.Value
                    : Math.Max(1, (int)Math.Ceiling(line.QtyOrdered - ((decimal)(count - 1) * units.Value)));
                cartons.Add(new AsnCartonForm(id, line.ItemId, inCarton, $"LOT-{seq:000}"));
            }
        }

        return cartons;
    }
}

/// <summary>Display-only outside-world labels. Friendly flow names replace
/// endpoint paths before anything reaches the screen: no endpoint name ever
/// appears in UI text (pinned by OutsideWorldTests).</summary>
public static class OutsideWorldLabels
{
    public const string OrderFlow = "customer order";
    public const string AckFlow = "supplier acknowledgement";
    public const string AsnFlow = "supplier shipment notice";
    public const string DockFlow = "dock photos";
    public const string ReshotFlow = "dock reshot";
    public const string InvoiceFlow = "supplier invoice";
    public const string InvoicePhotoFlow = "invoice photos";
    public const string ReplyFlow = "vendor reply";
    public const string RebuildFlow = "dock view rebuild";

    /// <summary>Replaces known endpoint calls ("POST /simulation/..." or the
    /// bare path) with their friendly flow names. Unknown text passes
    /// through untouched; failures keep their status and body excerpt, only
    /// the call reads human.</summary>
    public static string Sanitize(string text)
    {
        // "POST <path>" first so the verb goes with the path; longer paths
        // before their prefixes (invoice-photo before invoice).
        var pairs = new (string Path, string Flow)[]
        {
            ("/simulation/shopify/order", OrderFlow),
            ("/simulation/supplier/asn", AsnFlow),
            ("/simulation/supplier/ack", AckFlow),
            ("/simulation/dock/reshot", ReshotFlow),
            ("/simulation/dock/rebuild-views", RebuildFlow),
            ("/simulation/dock/photos", DockFlow),
            ("/simulation/supplier/invoice-photo", InvoicePhotoFlow),
            ("/simulation/supplier/invoice", InvoiceFlow),
            ("/simulation/supplier/email-reply", ReplyFlow),
        };
        foreach (var (path, flow) in pairs)
        {
            text = text.Replace($"POST {path}", flow, StringComparison.Ordinal);
        }

        foreach (var (path, flow) in pairs)
        {
            text = text.Replace(path, flow, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>True when the text names no simulation endpoint path.</summary>
    public static bool IsClean(string? text) =>
        text is null || !text.Contains("/simulation/", StringComparison.Ordinal);

    public static SimulationOutcome Sanitized(SimulationOutcome outcome) =>
        outcome with
        {
            Summary = Sanitize(outcome.Summary),
            Details = outcome.Details.Select(Sanitize).ToList(),
        };
}
