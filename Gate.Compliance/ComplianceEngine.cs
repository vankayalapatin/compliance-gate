// Starter compliance engine. Implements three demo-driving rules with cited
// sources; the compliance agent grows this into JSON-loaded versioned packs.
using Gate.Contracts;

namespace Gate.Compliance;

public sealed class ComplianceEngine : IComplianceEngine
{
    public const string CorpOriginCapId = "CORP-02";
    public const string CorpOriginCapSource = "Fictional COO memo and board minutes (company policy)";
    public const decimal CorpOriginCap = 0.60m;

    public const string MissingDocSource = "UN38.3 / SDS transport documentation requirements";
    public const string PrePayHoldId = "ELEC-04";

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        IReadOnlyList<VendorDocumentDto> documents,
        decimal volumeShare,
        CancellationToken ct = default)
    {
        var verdicts = new List<Verdict>();
        if (vendor.IsBlocked)
        {
            verdicts.Add(new Verdict("VENDOR-BLOCK", VerdictKind.Block, $"Vendor {vendor.VendorId} is blocked: {vendor.BlockReason}.", "ERP vendor master"));
        }

        foreach (var required in new[] { "UN38.3", "SDS" })
        {
            var present = documents.Any(d => d.Kind == required && d.Present);
            verdicts.Add(present
                ? new Verdict($"BAT-LI-{(required == "UN38.3" ? "01" : "02")}", VerdictKind.Pass, $"{required} declaration present.", MissingDocSource)
                : new Verdict($"BAT-LI-{(required == "UN38.3" ? "01" : "02")}", VerdictKind.Block, $"{required} declaration missing for lot.", MissingDocSource));
        }

        verdicts.Add(volumeShare > CorpOriginCap
            ? new Verdict(CorpOriginCapId, VerdictKind.Block,
                $"Vendor country share {volumeShare:P0} exceeds {CorpOriginCap:P0} country-of-origin cap.", CorpOriginCapSource)
            : new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Vendor country share {volumeShare:P0} within {CorpOriginCap:P0} cap.", CorpOriginCapSource));

        return Task.FromResult<IReadOnlyList<Verdict>>(verdicts);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        IReadOnlyList<HoldDto> openHolds,
        CancellationToken ct = default)
    {
        IReadOnlyList<Verdict> verdicts = openHolds.Count == 0
            ? [new Verdict(PrePayHoldId, VerdictKind.Pass, "No open holds on lot.", "Internal gate dependency")]
            : [new Verdict(PrePayHoldId, VerdictKind.Block,
                $"{openHolds.Count} open hold(s): {string.Join(", ", openHolds.Select(h => h.HoldId))}. Payment blocked.", "Internal gate dependency")];
        return Task.FromResult(verdicts);
    }
}
