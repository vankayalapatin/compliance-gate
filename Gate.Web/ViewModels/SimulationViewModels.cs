// Simulation form shapes for the Simulate surface. Display and form shapes
// only; every option value arrives from the scenario file, the run
// manifest, the case cache, the dock image directory, or the ERP adapter at
// runtime. No business values in code.
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>One cached case offered by the Simulate dropdowns.</summary>
public sealed record CaseOption(
    string CaseId,
    string SoNumber,
    string ItemId,
    string Stage,
    string? PoNumber);

/// <summary>One scenario item offered for new shop-order lines.</summary>
public sealed record ItemOption(
    string ItemId,
    string Unit);

/// <summary>Every option list on the Simulate surface, plus notes for sources
/// that were missing or failed. Empty lists (never null) render honest empty
/// states; notes surface the cause.</summary>
public sealed record SimulateOptions(
    string ScenarioName,
    string Currency,
    IReadOnlyList<string> SoNumbers,
    IReadOnlyList<string> PoNumbers,
    IReadOnlyList<UserDto> Users,
    IReadOnlyList<VendorDto> Vendors,
    IReadOnlyList<CaseOption> Cases,
    IReadOnlyList<string> CartonIds,
    IReadOnlyList<string> DockImages,
    IReadOnlyList<ItemOption> Items,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CandidateVendorsByItem,
    IReadOnlyList<string> Notes);

/// <summary>One photo the human injects: an uploaded file or a server dock
/// image, read to bytes before posting. CartonHint fills a blank label id
/// only.</summary>
public sealed record SimulationPhoto(
    string ImageId,
    byte[] Jpeg,
    string? CartonHint);

/// <summary>New shop-order lines for an unknown order id. Known SOs post
/// without lines.</summary>
public sealed record OrderLineForm(
    string ItemId,
    decimal Quantity);

/// <summary>Order webhook submission. Null Requester means the ERP default
/// requester; null Lines means a known (pre-seeded) SO.</summary>
public sealed record OrderSubmission(
    string ScenarioName,
    string SoNumber,
    string? Requester,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CandidateVendorsByItem,
    IReadOnlyList<OrderLineForm>? Lines,
    string? Currency);

/// <summary>One typed supplier-invoice line. Blank PoLineRef is a valid
/// missing-fields input, never a refusal.</summary>
public sealed record InvoiceLineForm(
    string? PoLineRef,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>Outcome of one simulation POST. Ok mirrors HTTP success; failures
/// and refusals carry the path, the status, and the body excerpt so the
/// human sees the full context. CaseIds link on to Cases and Decisions.</summary>
public sealed record SimulationOutcome(
    bool Ok,
    string Summary,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> CaseIds);

/// <summary>Reply-as-vendor prefill from the case mail thread (or the case PO
/// vendor when no thread exists yet). Nulls mean no suggestion; the human
/// types the value.</summary>
public sealed record ReplySuggestion(
    string? From,
    string? InReplyTo,
    string? Subject);

/// <summary>One PO line offered for invoice po-line-ref picks.</summary>
public sealed record PoLineOption(
    string PoLineRef,
    string ItemId,
    decimal QtyOrdered);
