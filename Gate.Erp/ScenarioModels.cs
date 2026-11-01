// Scenario file shapes. Intents only: quantities, prices, names, and dates
// live here, never document numbers. The loader mints every SO/PR/PO/ASN/
// RCP/INV/PAY number from sqlite sequences at posting and records the
// assigned numbers in scenarios/run-manifest.json. Code holds logic only.
using System.Text.Json.Serialization;

namespace Gate.Erp;

public sealed record ScenarioSoIntent(string Item, decimal Quantity);

public sealed record ScenarioBomIntent(string Item, string Component, decimal QtyPer);

public sealed record ScenarioStockIntent(string Item, decimal Quantity);

public sealed record ScenarioOpenPoIntent(string Item, decimal Quantity, string Vendor);

public sealed record ScenarioItem(string Id, string Description, bool Critical, int PackSize, string Unit);

public sealed record ScenarioPaymentTerm(string Id, int NetDays);

public sealed record ScenarioVendorDoc(string Kind, string Reference, bool Present);

public sealed record ScenarioVendor(
    string Id,
    string Name,
    string Country,
    string Role,
    string Item,
    decimal UnitPrice,
    string Terms,
    string BankAccount,
    IReadOnlyList<ScenarioVendorDoc> Documents,
    string? Email = null);

public sealed record ScenarioHistoryCells(string Source, string Country, decimal Cells);

public sealed record ScenarioUser(string Name, string Role, decimal SigningLimit);

public sealed record ScenarioDelegation(string From, string To, string Start, string End);

public sealed record ScenarioCartonPlan(int UnitsPerCarton, int Cartons);

public sealed record ScenarioSmallPo(string Key, string Item, decimal Quantity, string Vendor, decimal UnitPrice);

/// <summary>One number-sequence rule: how the loader formats and starts the
/// counter for a document prefix. Format is a template with {prefix} and
/// {number} slots; the number is zero-padded to Padding digits (0 means no
/// padding). Start is the first number unless seeded rows already run higher,
/// in which case the sequence continues after the max seeded number.</summary>
public sealed record ScenarioNumberSequence(
    string Prefix,
    string? Format = null,
    int Padding = 0,
    long Start = 1);

public sealed record ScenarioLifecycleInvoice(string Key, string Number, decimal Quantity, string Stage);

/// <summary>Lifecycle seed intents: one non-short item plus its vendor carry
/// a document at every P2P stage so list pages, pending queues, and history
/// have rows on a fresh load. All quantities, references, supplier numbers,
/// and routing names come from the scenario file, never from code. Invoice
/// stages are received, matched, submitted, approved, or posted; the posted
/// invoices back the draft, submitted, approved, and posted seed payments.
/// Total invoice quantity must not exceed the backbone receipt.</summary>
public sealed record ScenarioLifecycle(
    string Item,
    string Vendor,
    decimal PrDraftQty,
    decimal PrSubmittedQty,
    string PrSubmittedTo,
    decimal PrApprovedQty,
    decimal PoDraftQty,
    decimal PoSubmittedQty,
    string PoSubmittedTo,
    decimal PoApprovedQty,
    decimal BackboneQty,
    string PackingSlip,
    string Asn,
    string Carton,
    string Lot,
    string InvoiceSubmittedTo,
    string PaySubmittedTo,
    IReadOnlyList<ScenarioLifecycleInvoice> Invoices);

public sealed record ScenarioFile(
    [property: JsonPropertyName("$comment")] string? Comment,
    string Name,
    string Currency,
    ScenarioSoIntent SalesOrder,
    IReadOnlyList<ScenarioBomIntent> Bom,
    IReadOnlyList<ScenarioStockIntent> Stock,
    ScenarioOpenPoIntent OpenPoInbound,
    IReadOnlyList<ScenarioItem> Items,
    IReadOnlyList<ScenarioPaymentTerm> PaymentTerms,
    IReadOnlyList<ScenarioVendor> Vendors,
    string HistoryPart,
    IReadOnlyList<ScenarioHistoryCells> HistoryCells,
    string? HistoryNote,
    IReadOnlyList<ScenarioUser> Users,
    IReadOnlyList<ScenarioDelegation> Delegations,
    decimal AutoApproveUnder,
    ScenarioCartonPlan CartonPlan,
    IReadOnlyList<ScenarioSmallPo> SmallPos,
    IReadOnlyList<ScenarioNumberSequence>? NumberSequences = null,
    ScenarioLifecycle? Lifecycle = null);

/// <summary>One generated id recorded for a scenario event.</summary>
public sealed record ManifestDocument(string Event, string DocType, string Number);

/// <summary>Run manifest: which scenario produced which document numbers.</summary>
public sealed record ScenarioManifest(
    string Scenario,
    string ScenarioFile,
    string Sqlite,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<ManifestDocument> Documents);
