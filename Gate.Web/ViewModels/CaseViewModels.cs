// View models for the Decisions, Cases, and Records surfaces. Display
// shapes only; every value arrives from the case cache, the run manifest,
// or the ERP adapter at runtime. No business values in code.
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

/// <summary>One SO-to-payment chain step for the Cases surface.</summary>
public sealed record ChainStepVm(
    string Label,
    string? DocumentNumber,
    string Status,
    string? Detail);

public sealed record CaseDetailVm(
    Case Case,
    IReadOnlyList<ChainStepVm> Chain,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<Decision> Decisions,
    IReadOnlyList<HoldDto> OpenHolds);

public sealed record ApprovalVm(
    string DocumentType,
    string DocumentNumber,
    string RoutedTo,
    string? ActedBy,
    bool Approved,
    string? Comment,
    decimal DocumentAmount,
    decimal LimitChecked);

/// <summary>Plain ERP ledger snapshot. Every list is empty (never null) when
/// its documents are absent, so pages render honest empty states.</summary>
public sealed record RecordsVm(
    IReadOnlyList<SalesOrder> SalesOrders,
    IReadOnlyList<PurchaseRequisition> PurchaseRequisitions,
    IReadOnlyList<PurchaseOrder> PurchaseOrders,
    IReadOnlyList<AsnDto> Asns,
    IReadOnlyList<ReceiptVm> Receipts,
    IReadOnlyList<VendorInvoice> Invoices,
    IReadOnlyList<Payment> Payments,
    IReadOnlyList<InventoryVm> Inventory,
    IReadOnlyList<HoldDto> OpenHolds,
    IReadOnlyList<ApprovalVm> Approvals,
    IReadOnlyList<string> Notes);

public sealed record ReceiptVm(
    ShipmentReceipt Receipt,
    IReadOnlyList<string> ImageFileNames,
    IReadOnlyList<string> CachedReads);

public sealed record InventoryVm(
    string ItemId,
    decimal Stock,
    IReadOnlyList<InventoryTransaction> Transactions);

/// <summary>Step-by-step reset/replay report rendered on Home.</summary>
public sealed record ReplayReport(
    DateTimeOffset RanAtUtc,
    bool IsReplay,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    string? Error);
