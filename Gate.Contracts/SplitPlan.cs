// Split award plan: the verified whole-carton split a person approves
// before any PO is created. Lines run in creation order, which is the
// order verification checked: each line passed the policy check on its
// own with the earlier lines counted as open. Every figure is computed
// by code at verify time, never by the model. No business values here:
// vendors, quantities, shares, and money all arrive with the proposal.
namespace Gate.Contracts;

/// <summary>One split line: vendor plus quantity in whole cartons, with the
/// full screening verdicts at that quantity as verified.</summary>
public sealed record SplitPlanLine(
    string VendorId,
    string VendorName,
    decimal Quantity,
    IReadOnlyList<Verdict> Verdicts);

/// <summary>One country's final share once every split line lands: history
/// plus open orders plus all split quantities, in words on the card.</summary>
public sealed record SplitCountryShare(
    string CountryCode,
    string Country,
    decimal Share);

/// <summary>A verified split award. SingleVendorTotal and Saving are null
/// when no single vendor passes for the full quantity: the split is then
/// the only way to order, not a saving.</summary>
public sealed record SplitPlan(
    IReadOnlyList<SplitPlanLine> Lines,
    IReadOnlyList<SplitCountryShare> FinalShares,
    decimal SplitTotal,
    decimal? SingleVendorTotal,
    decimal? Saving,
    string? Currency);
