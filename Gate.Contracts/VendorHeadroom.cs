// Vendor headroom: the most a supplier can provide under every cap with
// the rest of the request already open (last position), open orders
// counted, in whole cartons. Computed by the compliance engine; the agent
// reads it as split facts, never invents it.
namespace Gate.Contracts;

public sealed record VendorHeadroom(
    string VendorId,
    decimal HeadroomQty,
    int HeadroomCartons,
    bool FullRequestPossible,
    string? BindingRuleId,
    string? BindingReason);
