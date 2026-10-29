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
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
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

        verdicts.Add(EvaluateOriginCap(award, history));

        return Task.FromResult<IReadOnlyList<Verdict>>(verdicts);
    }

    private static Verdict EvaluateOriginCap(ProposedAward award, IReadOnlyList<VolumeHistory> history)
    {
        var total = history.Sum(h => h.Cells);
        if (total <= 0)
        {
            // No trailing history for this part: CORP-02 has no denominator.
            // Passes as not applicable so history-free parts (e.g. SCR-9 via
            // Vendor C) keep their straight-through path. The empty-history
            // human-review boundary from the scenario plan is still open.
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"No 12-month volume history for {award.ItemId}; country-of-origin cap not applicable.", CorpOriginCapSource);
        }

        var countryCells = history
            .Where(h => string.Equals(h.Country, award.Country, StringComparison.OrdinalIgnoreCase))
            .Sum(h => h.Cells);
        var share = (countryCells + award.Quantity) / (total + award.Quantity);
        return share > CorpOriginCap
            ? new Verdict(CorpOriginCapId, VerdictKind.Block,
                $"Awarding {award.Quantity} {award.ItemId} cells to {award.Country} puts its share at {share:P1} ({countryCells}+{award.Quantity} of {total}+{award.Quantity}), exceeding the {CorpOriginCap:P0} country-of-origin cap.", CorpOriginCapSource)
            : new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Awarding {award.Quantity} {award.ItemId} cells to {award.Country} puts its share at {share:P1}, within the {CorpOriginCap:P0} cap.", CorpOriginCapSource);
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
