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
    IReadOnlyList<ScenarioSmallPo> SmallPos);

/// <summary>One generated id recorded for a scenario event.</summary>
public sealed record ManifestDocument(string Event, string DocType, string Number);

/// <summary>Run manifest: which scenario produced which document numbers.</summary>
public sealed record ScenarioManifest(
    string Scenario,
    string ScenarioFile,
    string Sqlite,
    DateTimeOffset LoadedAtUtc,
    IReadOnlyList<ManifestDocument> Documents);
