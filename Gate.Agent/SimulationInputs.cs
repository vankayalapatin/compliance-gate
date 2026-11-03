// Simulation input records. Every handler takes one of these: external data
// in, ERP plus case-file writes out. No business values live in code —
// quantities, ids, banks, and dates arrive here from the scenario file, run
// manifest, supplier message, or vision cache. All handlers are idempotent:
// repeating the same input (same idempotency key) returns the stored outcome
// and makes no new live calls.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Shopify-style order webhook. Known SOs match the pre-seeded order
/// and start one case per shortfall item. Unknown shop orders carry Lines
/// plus Currency so the runner creates the SO through the ERP (which mints
/// the number); without lines an unknown order is a gap error. Candidate
/// vendors per item come from sourcing data (the scenario vendors in tests);
/// the ERP exposes no vendors-for-item read.</summary>
public sealed record OrderWebhookInput(
    string ScenarioName,
    string SoNumber,
    string? Requester = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CandidateVendorsByItem = null,
    string? IdempotencyKey = null,
    IReadOnlyList<SalesOrderLine>? Lines = null,
    string? Currency = null);

/// <summary>One ASN carton as the supplier declares it. Lot refs are the
/// supplier's lot ids (tests derive them from the manifest PO plus carton).</summary>
public sealed record AsnCartonInput(
    string CartonId,
    string ItemId,
    int UnitsPerCarton,
    string LotRef);

/// <summary>Supplier ACK for a case PO. Lines default to the full ordered qty
/// read back from the PO when omitted.</summary>
public sealed record SupplierAckInput(
    string CaseId,
    DateOnly? ConfirmDate = null,
    IReadOnlyList<VendorConfirmLine>? Lines = null,
    string? IdempotencyKey = null);

/// <summary>Supplier ASN for a case PO. The packing-slip number travels with
/// the ASN, so ASN handling posts the receipt in the same idempotent step.
/// Simulated problems ride along for the dock views only; uploads never
/// take problems.</summary>
public sealed record SupplierAsnInput(
    string CaseId,
    string AsnNumber,
    string SupplierPackingSlipNumber,
    IReadOnlyList<AsnCartonInput> Cartons,
    string? IdempotencyKey = null,
    IReadOnlyList<SimulatedProblem>? SimulatedProblems = null);

/// <summary>Dock-view rebuild for a case: merge these problems into the
/// stored set (winning per carton) and rebuild the views. The problem
/// shape itself is vision-canonical in DockImagePipeline.cs.</summary>
public sealed record RebuildDockViewsInput(string CaseId, List<SimulatedProblem> Problems);

/// <summary>One supplier invoice line as transcribed (vision or supplier EDI).
/// PoLineRef may be blank on the tricky missing-fields invoice.</summary>
public sealed record SupplierInvoiceLineInput(
    string? PoLineRef,
    decimal Quantity,
    decimal UnitPrice);

/// <summary>Supplier invoice intake, main or tricky-inbox. Blank VendorId or
/// PoNumber means the transcription carried blanks (missing-fields catch).
/// BankAccountRef carries the remittance bank on the invoice when shown.</summary>
public sealed record SupplierInvoiceInput(
    string CaseId,
    string SupplierInvoiceNumber,
    string? VendorId,
    string? PoNumber,
    IReadOnlyList<SupplierInvoiceLineInput> Lines,
    string? BankAccountRef = null,
    string? IdempotencyKey = null);

/// <summary>Relabel reshot (or any follow-up dock read set): fresh reads for
/// the listed cartons only. A passing reshot releases the carton hold, the
/// lot quarantine, and the held invoice value for the next pay run.</summary>
public sealed record ReshotInput(
    string CaseId,
    IReadOnlyList<string> CartonIds,
    IReadOnlyList<DockCartonRead> Reads,
    string? IdempotencyKey = null);

/// <summary>One dock photo for transcription. ImageId travels onto every read
/// from this photo; CartonHint is the carton the camera targets and fills in
/// only when the transcription leaves a label id blank.</summary>
public sealed record DockPhoto(
    string ImageId,
    byte[] Jpeg,
    string? CartonHint);

/// <summary>One supplier invoice photo for transcription (multi-page invoices
/// arrive as one photo per page).</summary>
public sealed record InvoicePhoto(
    string ImageId,
    byte[] Jpeg);

/// <summary>One transcribed supplier invoice, validated against the ERP:
/// VendorIdOrName is the resolved vendor id (or null when blank or unmatched)
/// and PoNumber the resolved PO (or null when blank or unmatched), so
/// unmatched values route to the missing-fields path, never invented.</summary>
public sealed record TranscribedInvoice(
    string? SupplierInvoiceNumber,
    string? VendorIdOrName,
    string? PoNumber,
    IReadOnlyList<SupplierInvoiceLineInput> Lines,
    string? BankAccountRef);

/// <summary>Vendor reply to a case query, appended by simulation. The agent
/// reads it via Spark and acts on any corrected invoice fields it carries.</summary>
public sealed record VendorReplyInput(
    string CaseId,
    string From,
    string Body,
    string? Subject = null,
    string? InReplyTo = null,
    string? EmailId = null,
    string? IdempotencyKey = null);
