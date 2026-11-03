// View models for the ERP area: sidebar modules, list rows, record shapes,
// money formatting, and action/input specs. Display shapes only; every value
// arrives from the ERP adapter at runtime. No business values in code.
using System.Globalization;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>ERP sidebar: five modules, each expanding to its list pages.
// Slugs double as record kinds where a list has records.</summary>
public static class ErpModules
{
    public sealed record ListLink(string Slug, string Title);

    public sealed record Module(string Name, IReadOnlyList<ListLink> Lists);

    public static readonly IReadOnlyList<Module> All =
    [
        new("Sales", [new("so", "Sales orders")]),
        new("Procurement",
        [
            new("pr", "Purchase requisitions"),
            new("po", "Purchase orders"),
            new("asn", "ASNs"),
            new("receipt", "Receipts"),
        ]),
        new("Inventory", [new("stock", "Stock and lots")]),
        new("Accounts Payable",
        [
            new("invoice", "Invoices"),
            new("payment", "Payments"),
            new("hold", "Holds"),
        ]),
        new("Masters",
        [
            new("vendor", "Vendors"),
            new("item", "Items"),
        ]),
    ];

    public static string? ModuleFor(string slug) =>
        All.FirstOrDefault(m => m.Lists.Any(l =>
            string.Equals(l.Slug, slug, StringComparison.Ordinal)))?.Name;

    public static string TitleFor(string slug) =>
        All.SelectMany(m => m.Lists).FirstOrDefault(l =>
            string.Equals(l.Slug, slug, StringComparison.Ordinal))?.Title
        ?? $"Unknown list '{slug}'";

    public static bool IsKnownList(string slug) =>
        All.Any(m => m.Lists.Any(l =>
            string.Equals(l.Slug, slug, StringComparison.Ordinal)));

    public static string ListHref(string slug) => $"erp/list/{slug}";
}

/// <summary>ERP area routes. Records live under erp/record so list, new,
/// and record URLs never collide on one router.</summary>
public static class ErpRoutes
{
    /// <summary>Lists with a real ERP create behind them. ASNs arrive from
    /// vendors, stock derives from receipts, holds derive from decisions —
    /// those lists honestly have no New button.</summary>
    public static readonly IReadOnlySet<string> CreatableKinds =
        new HashSet<string>(StringComparer.Ordinal)
        { "so", "pr", "po", "invoice", "payment", "receipt", "vendor", "item" };

    public static bool CanCreate(string slug) => CreatableKinds.Contains(slug);

    /// <summary>Per-list New label naming what the button creates.</summary>
    public static string NewLabelFor(string slug) =>
        slug switch
        {
            "so" => "New sales order",
            "pr" => "New purchase requisition",
            "po" => "New purchase order",
            "invoice" => "New invoice",
            "payment" => "New payment",
            "receipt" => "New receipt",
            "vendor" => "New vendor",
            "item" => "New item",
            _ => "New",
        };

    public static string RecordHref(string kind, string key, string? key2 = null) =>
        key2 is null
            ? $"erp/record/{kind}/{Uri.EscapeDataString(key)}"
            : $"erp/record/{kind}/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(key2)}";

    public static string NewHref(string kind, string? preset = null) =>
        preset is null
            ? $"erp/new/{kind}"
            : $"erp/new/{kind}/{Uri.EscapeDataString(preset)}";
}

/// <summary>Clean numbers for ledger columns: money with two decimals plus
/// currency ($ for USD), quantities with thousands separators and no
/// trailing zeros, stamps in local time. Invariant culture so every
/// operator sees the same shape.</summary>
public static class ErpFormat
{
    /// <summary>Number shapes shared with the lower layers: agent prose
    /// and ledger cells format money identically.</summary>
    public static string Money(decimal amount, string? currency) =>
        DisplayWords.Money(amount, currency);

    public static string Amount(decimal amount) =>
        DisplayWords.Amount(amount);

    public static string Qty(decimal qty) =>
        DisplayWords.Qty(qty);

    public static string Count(int count) =>
        DisplayWords.Count(count);

    /// <summary>Local-time stamp for record fields and timelines. Never a
    /// trailing Z: the reader sees their own clock.</summary>
    public static string When(DateTimeOffset at) =>
        DisplayWords.When(at);

    public static string When(DateTimeOffset? at) =>
        at is null ? "—" : When(at.Value);

    /// <summary>Calendar-day stamp for due and posting dates.</summary>
    public static string Day(DateOnly day) =>
        day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Plain words for every ERP status enum. Screens never render a
/// raw enum name.</summary>
public static class ErpStatusWords
{
    public static string Of(SalesOrderStatus status) => status switch
    {
        SalesOrderStatus.Created => "Created",
        SalesOrderStatus.Confirmed => "Confirmed",
        SalesOrderStatus.PartiallyDelivered => "Partly delivered",
        SalesOrderStatus.Delivered => "Delivered",
        SalesOrderStatus.Invoiced => "Invoiced",
        SalesOrderStatus.Closed => "Closed",
        SalesOrderStatus.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string Of(PrStatus status) => status switch
    {
        PrStatus.Draft => "Draft",
        PrStatus.Submitted => "Submitted",
        PrStatus.Approved => "Approved",
        PrStatus.Rejected => "Rejected",
        PrStatus.PartiallyOrdered => "Partially ordered",
        PrStatus.Closed => "Closed",
        PrStatus.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string Of(PoApprovalStatus status) => status switch
    {
        PoApprovalStatus.Draft => "Draft",
        PoApprovalStatus.InReview => "In review",
        PoApprovalStatus.Approved => "Approved",
        PoApprovalStatus.Confirmed => "Confirmed",
        PoApprovalStatus.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string Of(PoDocStatus status) => status switch
    {
        PoDocStatus.Open => "Open",
        PoDocStatus.PartiallyReceived => "Partly received",
        PoDocStatus.Received => "Received",
        PoDocStatus.PartiallyInvoiced => "Partly invoiced",
        PoDocStatus.Invoiced => "Invoiced",
        PoDocStatus.Closed => "Closed",
        PoDocStatus.Cancelled => "Cancelled",
        _ => "Unknown",
    };

    public static string Of(ReceiptStatus status) => status switch
    {
        ReceiptStatus.Posted => "Posted",
        _ => "Unknown",
    };

    public static string Of(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Received => "Received",
        InvoiceStatus.Matched => "Matched",
        InvoiceStatus.Approved => "Approved",
        InvoiceStatus.Posted => "Posted",
        InvoiceStatus.PartiallyPaid => "Partly paid",
        InvoiceStatus.Paid => "Paid",
        InvoiceStatus.Closed => "Closed",
        _ => "Unknown",
    };

    public static string Of(PaymentStatus status) => status switch
    {
        PaymentStatus.Draft => "Draft",
        PaymentStatus.Submitted => "Submitted",
        PaymentStatus.Approved => "Approved",
        PaymentStatus.Posted => "Posted",
        _ => "Unknown",
    };

    public static string Of(HoldStatus status) => status switch
    {
        HoldStatus.Set => "On hold",
        HoldStatus.Released => "Released",
        _ => "Unknown",
    };

    public static string Of(LineDisposition disposition) => disposition switch
    {
        LineDisposition.Accepted => "Accepted",
        LineDisposition.Held => "Held",
        _ => "Unknown",
    };

    public static string Of(InventoryStatus status) => status switch
    {
        InventoryStatus.Available => "Available",
        InventoryStatus.Quarantined => "Quarantined",
        InventoryStatus.Rejected => "Rejected",
        InventoryStatus.Consumed => "Consumed",
        _ => "Unknown",
    };

    public static string Of(DecisionStatus status) => status switch
    {
        DecisionStatus.Pending => "Pending",
        DecisionStatus.Approved => "Approved",
        DecisionStatus.Rejected => "Rejected",
        _ => "Unknown",
    };
}

/// <summary>One ledger cell. Numeric cells right-align with tabular figures.
/// Href links the cell to another page when it names one.</summary>
public sealed record ErpCell(string Text, bool Numeric = false, string? Href = null);

/// <summary>ERP workflow document types per record kind. Submit and approve
/// travel with these types; kinds without a workflow map to null.</summary>
public static class ErpDocTypes
{
    public static string? ForKind(string kind) =>
        kind switch
        {
            "pr" => "PR",
            "po" => "PO",
            "invoice" => "Invoice",
            "payment" => "Payment",
            _ => null,
        };

    public static string InvoiceDocNumber(string vendorId, string supplierInvoiceNumber) =>
        $"{vendorId}/{supplierInvoiceNumber}";

    /// <summary>Maps an approval/hold document type back to a record kind
    /// plus key for the related-document link. Invoice numbers split into
    /// vendor plus number.</summary>
    public static (string Kind, string Key, string? Key2)? RecordFor(string documentType, string? documentNumber)
    {
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            return null;
        }

        return documentType switch
        {
            "SO" => ("so", documentNumber, null),
            "PR" => ("pr", documentNumber, null),
            "PO" => ("po", documentNumber, null),
            "ASN" => ("asn", documentNumber, null),
            "Receipt" => ("receipt", documentNumber, null),
            "Payment" => ("payment", documentNumber, null),
            "Vendor" => ("vendor", documentNumber, null),
            "Invoice" => SplitInvoice(documentNumber),
            _ => null,
        };
    }

    private static (string Kind, string Key, string? Key2)? SplitInvoice(string documentNumber)
    {
        var parts = documentNumber.Split('/', 2);
        return parts.Length == 2
            ? ("invoice", parts[0], parts[1])
            : null;
    }
}

public sealed record ErpColumn(string Header, bool Numeric = false);

public sealed record ErpLineTable(
    string Title,
    IReadOnlyList<ErpColumn> Columns,
    IReadOnlyList<IReadOnlyList<ErpCell>> Rows);

/// <summary>One header fact on a record page. Href links the value to
/// another record when the fact names one.</summary>
public sealed record ErpFact(string Label, string Value, string? Href = null);

/// <summary>One related document. HrefOverride links at creation flows
/// (for example an approved PR links the PO form); otherwise the href
/// points at the related record.</summary>
public sealed record ErpRelatedDoc(
    string Label,
    string Kind,
    string Key,
    string? Key2,
    string Status,
    string? HrefOverride = null)
{
    public string Href => HrefOverride ?? ErpRoutes.RecordHref(Kind, Key, Key2);
}

/// <summary>One dropdown option: the submitted value plus the plain-words
/// label the operator sees.</summary>
public sealed record ErpActionOption(string Value, string Label);

/// <summary>One action input. Options (non-null) renders a dropdown; a null
/// Options renders a text box. Keys are stable ids the action service reads.</summary>
public sealed record ErpActionInput(
    string Key,
    string Label,
    bool Required,
    IReadOnlyList<ErpActionOption>? Options = null);

/// <summary>One candidate action on a record. Candidates come from the
/// document's current state; the ERP still enforces everything and the UI
/// surfaces every refusal with its reason. Nothing is pre-enforced.</summary>
public sealed record ErpActionDef(
    string Id,
    string Label,
    string Hint,
    IReadOnlyList<ErpActionInput> Inputs);

/// <summary>Stable action ids shared by the record page and the action service.</summary>
public static class ErpActions
{
    public const string Submit = "submit";
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string PostInvoice = "post-invoice";
    public const string ReleaseInvoiceLine = "release-line";
    public const string PostPayment = "post-payment";
    public const string ReleaseHold = "release-hold";
    public const string BlockVendor = "block-vendor";
    public const string UnblockVendor = "unblock-vendor";
    public const string VerifyBank = "verify-bank";
    public const string ClosePo = "close-po";
    public const string AddPoNote = "add-note";
    public const string UpdateSoStatus = "update-so";
    public const string QuarantineLot = "quarantine-lot";
    public const string ReleaseLot = "release-lot";
    public const string ConsumeLot = "consume-lot";

    public const string InRoutedTo = "routedTo";
    public const string InComment = "comment";
    public const string InApprovalRef = "approvalRef";
    public const string InReason = "reason";
    public const string InNote = "note";
    public const string InStatus = "status";
    public const string InPoLineRef = "poLineRef";
    public const string InQuantity = "quantity";
    public const string InHoldId = "holdId";
    public const string InLotRef = "lotRef";
    public const string InSoNumber = "soNumber";
}

/// <summary>Role-gated views on record pages. The AP clerk hides the
/// quarantine-lot views (the stock lots table plus the quarantine and
/// release actions) and the vendor-block actions. This is display only:
/// the ERP still enforces every control behind the attempt.</summary>
public static class RoleGating
{
    public const string ApClerk = "ap-clerk";

    public static bool IsApClerk(string? role) =>
        string.Equals(role, ApClerk, StringComparison.OrdinalIgnoreCase);

    public static bool ActionVisible(string? role, string actionId) =>
        !IsApClerk(role)
        || (actionId is not ErpActions.BlockVendor
            and not ErpActions.UnblockVendor
            and not ErpActions.QuarantineLot
            and not ErpActions.ReleaseLot);

    public static bool TableVisible(string? role, string kind, string title) =>
        !IsApClerk(role)
        || !(string.Equals(kind, "stock", StringComparison.Ordinal)
            && string.Equals(title, "Lots", StringComparison.Ordinal));
}

/// <summary>One ERP record page: header facts, line tables, related
/// documents, candidate actions, and notes. SourceName feeds the
/// agent-made banner; every other row stays plain.</summary>
public sealed record ErpRecordVm(
    string Kind,
    string Key,
    string Title,
    IReadOnlyList<string> StatusBadges,
    string? SourceName,
    string? OriginNumber,
    IReadOnlyList<ErpFact> Header,
    IReadOnlyList<ErpLineTable> LineTables,
    IReadOnlyList<ErpRelatedDoc> Related,
    IReadOnlyList<ErpActionDef> Actions,
    IReadOnlyList<string> ImageFileNames,
    IReadOnlyList<string> CachedReads,
    IReadOnlyList<string> Notes);

/// <summary>One ERP list page: typed rows plus notes. Notes name every
/// partial failure; nothing is silently omitted.</summary>
public sealed record ErpListVm<T>(
    IReadOnlyList<T> Rows,
    IReadOnlyList<string> Notes);

/// <summary>One stock-list row: item identity plus on-hand plus lot lines.</summary>
public sealed record ErpStockRow(
    string ItemId,
    string? Description,
    string? Unit,
    decimal Stock,
    IReadOnlyList<InventoryTransaction> Lots);

/// <summary>One picker option on a New form: the stored value plus the
/// human-readable label the page shows.</summary>
public sealed record ErpNewOption(string Value, string Label);

/// <summary>One purchase-history row: a volume-history source plus its
/// country and cells. Receipt rows link to the receipt record; opening
/// rows carry no receipt.</summary>
public sealed record PurchaseHistoryRow(
    string Source,
    string CountryCode,
    string CountryName,
    decimal Cells,
    string? ReceiptHref);

/// <summary>Country subtotal over the purchase-history rows.</summary>
public sealed record PurchaseHistoryCountry(
    string CountryCode,
    string CountryName,
    decimal Cells);

/// <summary>Read-only purchase history for one item: the same rows the
/// country-share math reads, grouped by vendor country with totals.</summary>
public sealed record PurchaseHistoryVm(
    string ItemId,
    string? ItemDescription,
    string WindowNote,
    IReadOnlyList<PurchaseHistoryRow> Rows,
    IReadOnlyList<PurchaseHistoryCountry> CountryTotals,
    decimal GrandTotal,
    string ItemHref,
    IReadOnlyList<string> Notes);
