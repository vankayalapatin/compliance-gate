// Simulation input records. Every handler takes one of these: external data
// in, ERP plus case-file writes out. No business values live in code —
// quantities, ids, banks, and dates arrive here from the scenario file, run
// manifest, supplier message, or vision cache. All handlers are idempotent:
// repeating the same input (same idempotency key) returns the stored outcome
// and makes no new live calls.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Shopify-style order webhook. SOs are pre-seeded by the scenario
/// loader: the runner matches the SO and starts one case per shortfall item,
/// never mints SOs. Candidate vendors per item come from sourcing data (the
/// scenario vendors in tests); the ERP exposes no vendors-for-item read.</summary>
public sealed record OrderWebhookInput(
    string ScenarioName,
    string SoNumber,
    string? Requester = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CandidateVendorsByItem = null,
    string? IdempotencyKey = null);

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
/// the ASN, so ASN handling posts the receipt in the same idempotent step.</summary>
public sealed record SupplierAsnInput(
    string CaseId,
    string AsnNumber,
    string SupplierPackingSlipNumber,
    IReadOnlyList<AsnCartonInput> Cartons,
    string? IdempotencyKey = null);

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
