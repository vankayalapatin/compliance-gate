// Outside-world view models: the customer shop, supplier inbox, and dock
// camera. Display and form shapes only; every option value arrives from the
// scenario file, the run manifest, the case cache, the dock image
// directory, or the ERP adapter at runtime. No business values in code.
//
// Supplier-minted numbers (ASN number, packing-slip number, lot refs,
// invoice number) cannot come from the scenario: the supplier mints them
// when sending. Prefills derive editable defaults from the runtime PO
// number plus carton id (the same derivation the agent-lane tests use),
// and the human can change every one before sending.
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>One ASN carton row in the supplier inbox. Defaults derive from
/// the runtime case plus scenario carton; the human edits before sending.</summary>
public sealed record AsnCartonForm(
    string CartonId,
    string ItemId,
    int UnitsPerCarton,
    string LotRef);

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
    IReadOnlyList<string> Notes);

/// <summary>Invoice prefill derived from the runtime PO lines. The number is
/// an editable supplier-minted default; lines mirror ordered qty/price.</summary>
public sealed record InvoicePrefill(
    string Number,
    string? VendorId,
    string? PoNumber,
    IReadOnlyList<InvoiceLineForm> Lines);

/// <summary>Display-only outside-world labels. Friendly flow names replace
/// endpoint paths before anything reaches the screen: no endpoint name ever
/// appears in UI text (pinned by OutsideWorldTests).</summary>
public static class OutsideWorldLabels
{
    public const string OrderFlow = "customer order";
    public const string AsnFlow = "supplier shipment notice";
    public const string DockFlow = "dock photos";
    public const string ReshotFlow = "dock reshot";
    public const string InvoiceFlow = "supplier invoice";
    public const string InvoicePhotoFlow = "invoice photos";
    public const string ReplyFlow = "vendor reply";

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
            ("/simulation/supplier/ack", AsnFlow),
            ("/simulation/dock/reshot", ReshotFlow),
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
