// Adapter-backed view models for Decisions, Cases, and ERP. Every value
// comes from the case files, the dock-evidence sidecars, or the ERP
// adapter at runtime. Missing documents surface as notes; nothing is
// silently omitted.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class CaseViewService
{
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ILogger<CaseViewService> _log;
    private readonly ModelSpendTracker? _spend;
    private readonly CountryNames? _countries;

    public CaseViewService(
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ILogger<CaseViewService> log,
        ModelSpendTracker? spend = null,
        CountryNames? countries = null)
    {
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _log = log;
        _spend = spend;
        _countries = countries;
    }

    public async Task<IReadOnlyList<DecisionCardVm>> ListDecisionCardsAsync(CancellationToken ct = default)
    {
        var files = await _cases.ListAsync(ct).ConfigureAwait(false);
        var cards = new List<DecisionCardVm>();
        foreach (var file in files)
        {
            foreach (var decision in file.Decisions)
            {
                cards.Add(await BuildCardAsync(file.Case, decision, ct).ConfigureAwait(false));
            }
        }

        return cards
            .OrderBy(c => c.Decision.Status != DecisionStatus.Pending)
            .ThenBy(c => c.Decision.RequestedAt)
            .ToList();
    }

    private async Task<DecisionCardVm> BuildCardAsync(Case kase, Decision decision, CancellationToken ct)
    {
        var captions = new List<string>();
        var images = new List<string>();
        var figures = new List<CartonFigureVm>();
        // Dock reviews render one figure per held or reviewed carton (full
        // photo plus verdict caption); the text lines below stay for cards
        // with no figures.
        if (decision.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
        {
            figures.AddRange(BuildCartonFigures(kase.CaseId, decision));
            if (figures.Count == 0)
            {
                captions.AddRange(ReadDockEvidenceLines(kase.CaseId));
            }
        }

        var figured = figures
            .Select(f => f.ImageFileName)
            .Where(i => i is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var evidence in decision.EvidenceRefs)
        {
            var image = _photos.ResolveImageFileName(evidence);
            if (image is null)
            {
                captions.Add(evidence);
                continue;
            }

            // A photo already shown in a carton figure is not repeated as a
            // loose image; the figure carries its caption and read.
            if (figured.Contains(image))
            {
                continue;
            }

            images.Add(image);
            try
            {
                var read = _photos.TryCachedRead(image);
                captions.Add(read is null
                    ? $"Photo {image}: no cached read."
                    : $"Photo {image} cached read: {read}");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cached read refused for {Image}.", image);
                captions.Add($"Photo {image}: cached read refused ({ex.Message}).");
            }
        }

        string? summary = null;
        try
        {
            summary = await DescribeDocumentAsync(decision, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ERP lookup failed for decision {DecisionId}.", decision.DecisionId);
            summary = $"ERP lookup failed: {ex.Message}";
        }

        return new DecisionCardVm(
            decision, kase.SoNumber, kase.ItemId, kase.Stage, captions, images, summary, figures);
    }

    /// <summary>One figure for this card's own carton only: the photo the
    /// carton was judged from plus the carton id and verdict caption. A
    /// damage card shows the photo whose read saw the damage; other cards
    /// show the carton's first read photo. Passed cartons stay out; a
    /// carton without a readable photo still renders its caption.</summary>
    private IReadOnlyList<CartonFigureVm> BuildCartonFigures(string caseId, Decision decision)
    {
        var evidence = ReadDockEvidence(caseId);
        if (evidence is null)
        {
            return [];
        }

        var ownCarton = decision.EvidenceRefs
            .Where(r => r.StartsWith("carton:", StringComparison.Ordinal))
            .Select(r => r["carton:".Length..])
            .FirstOrDefault();
        var figures = new List<CartonFigureVm>();
        foreach (var cartonId in evidence.Verdicts.Keys.Order(StringComparer.Ordinal))
        {
            if (ownCarton is not null
                && !string.Equals(cartonId, ownCarton, StringComparison.Ordinal))
            {
                continue;
            }

            var verdicts = evidence.Verdicts[cartonId]
                .Where(v => v.Kind != VerdictKind.Pass)
                .ToList();
            if (verdicts.Count == 0)
            {
                continue;
            }

            var cartonReads = evidence.Reads
                .Where(r => string.Equals(r.CartonId, cartonId, StringComparison.Ordinal))
                .ToList();
            // The judging photo: damage cards show the read that saw the
            // damage, every other card the carton's first read.
            var judging = decision.Kind == DecisionKind.DamageReview
                ? cartonReads.FirstOrDefault(r => r.DamageVisible) ?? cartonReads.FirstOrDefault()
                : cartonReads.FirstOrDefault();
            var imageId = judging?.ImageId;
            var image = _photos.ResolveImageFileName(imageId);
            string? read = null;
            if (image is not null)
            {
                try
                {
                    var cached = _photos.TryCachedRead(image);
                    read = cached is null
                        ? $"Photo {image}: no cached read."
                        : $"Photo {image} cached read: {cached}";
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cached read refused for {Image}.", image);
                    read = $"Photo {image}: cached read refused ({ex.Message}).";
                }
            }

            figures.Add(new CartonFigureVm(
                cartonId,
                image,
                $"Carton {cartonId}" + (imageId is null ? string.Empty : $" judged from {imageId}") + ": "
                + string.Join("; ", verdicts.Select(v => $"{v.Kind} by {v.RuleId} — {v.Reason}")),
                read));
        }

        return figures;
    }

    private async Task<string?> DescribeDocumentAsync(Decision decision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(decision.DocumentType)
            || string.IsNullOrWhiteSpace(decision.DocumentNumber))
        {
            return null;
        }

        return decision.DocumentType switch
        {
            "PR" => await _erp.GetPrAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } pr
                ? $"PR {pr.PrNumber}: {pr.Status}, {pr.EstimatedAmount} {pr.Currency}, {pr.Lines.Count} line(s)."
                : $"PR {decision.DocumentNumber} not found in ERP.",
            "PO" => await _erp.GetPurchaseOrderAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } po
                ? $"PO {po.PoNumber}: approval {po.ApprovalStatus}, doc {po.DocStatus}, {po.Lines.Sum(l => l.LineAmount)} {po.Currency}."
                : $"PO {decision.DocumentNumber} not found in ERP.",
            "Invoice" => await DescribeInvoiceAsync(decision.DocumentNumber, ct).ConfigureAwait(false),
            "Payment" => await _erp.GetPaymentAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } pay
                ? $"Payment {pay.PaymentNumber}: {pay.Status}, {pay.Lines.Sum(l => l.Amount)} {pay.Currency}, payee bank {(pay.PayeeBank.Verified ? "verified" : "not verified")}."
                : $"Payment {decision.DocumentNumber} not found in ERP.",
            "Vendor" or "VendorBank" => await _erp.GetVendorBankAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } bank
                ? $"Vendor {bank.VendorId} bank {bank.AccountRef}: {(bank.Verified ? "verified" : "not verified")}."
                : $"Vendor bank for {decision.DocumentNumber} not found in ERP.",
            _ => $"Document {decision.DocumentType} {decision.DocumentNumber} (no ERP detail reader).",
        };
    }

    private async Task<string> DescribeInvoiceAsync(string documentNumber, CancellationToken ct)
    {
        var parts = documentNumber.Split('/', 2);
        if (parts.Length != 2)
        {
            return $"Invoice ref {documentNumber} is not vendor/number; cannot look up.";
        }

        var inv = await _erp.GetInvoiceAsync(parts[0], parts[1], ct).ConfigureAwait(false);
        if (inv is null)
        {
            return $"Invoice {documentNumber} not found in ERP.";
        }

        var total = inv.Lines.Sum(l => l.Quantity * l.UnitPrice);
        return $"Invoice {inv.SupplierInvoiceNumber} ({inv.VendorId}): {inv.Status}, {total} {inv.Currency}, voucher {inv.Voucher ?? "none"}.";
    }

    public async Task<CaseDetailVm?> GetCaseDetailAsync(string caseId, CancellationToken ct = default)
    {
        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        var kase = file.Case;
        var chain = CaseChain.MarkCurrent(new List<ChainStepVm>
        {
            await SoStepAsync(kase, ct).ConfigureAwait(false),
            PrStep(kase),
            await PoStepAsync(kase, ct).ConfigureAwait(false),
            AsnStep(kase),
            ReceiptStep(kase),
            InvoicesStep(kase),
            PaymentsStep(kase),
        }, kase.Stage);

        IReadOnlyList<HoldDto> holds = [];
        try
        {
            holds = (await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false))
                .Where(h => string.Equals(h.CaseId, kase.CaseId, StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Open-hold lookup failed for case {CaseId}.", kase.CaseId);
        }

        IReadOnlyList<VendorEmail> mail = [];
        try
        {
            var thread = await new CaseMailStore(_cases.CacheDir)
                .ReadAsync(kase.CaseId, ct).ConfigureAwait(false);
            mail = thread?.Messages ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor-mail lookup failed for case {CaseId}.", kase.CaseId);
        }

        IReadOnlyList<OriginCapLine> math = [];
        try
        {
            math = await OriginCapExplainer.LinesAsync(_erp, file, _log, _countries?.Map, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Screening-math lookup failed for case {CaseId}.", kase.CaseId);
        }

        return new CaseDetailVm(
            kase, chain, file.Events, file.Decisions, holds, mail,
            AgentSummary.Build(kase, file.Events, file.Decisions, holds),
            math.Select(m => m.Line).ToList(),
            await ScorecardAsync(file, ct).ConfigureAwait(false));
    }

    private async Task<ChainStepVm> SoStepAsync(Case kase, CancellationToken ct)
    {
        try
        {
            var so = await _erp.GetSalesOrderAsync(kase.SoNumber, ct).ConfigureAwait(false);
            return so is null
                ? new ChainStepVm(CaseChain.SoLabel, kase.SoNumber, "missing", "SO not found in ERP.")
                : new ChainStepVm(CaseChain.SoLabel, so.SoNumber, so.Status.ToString(),
                    $"{so.Lines.Count} line(s), {so.Currency}.");
        }
        catch (Exception ex)
        {
            return new ChainStepVm(CaseChain.SoLabel, kase.SoNumber, "error", ex.Message);
        }
    }

    private ChainStepVm PrStep(Case kase) =>
        kase.PrNumber is null
            ? new ChainStepVm(CaseChain.PrLabel, null, "not created", null)
            : new ChainStepVm(CaseChain.PrLabel, kase.PrNumber, "linked", "Detail on ERP.");

    private async Task<ChainStepVm> PoStepAsync(Case kase, CancellationToken ct)
    {
        if (kase.PoNumber is null)
        {
            return new ChainStepVm(CaseChain.PoLabel, null, "not placed", null);
        }

        try
        {
            var po = await _erp.GetPurchaseOrderAsync(kase.PoNumber, ct).ConfigureAwait(false);
            return po is null
                ? new ChainStepVm(CaseChain.PoLabel, kase.PoNumber, "missing", "PO not found in ERP.")
                : new ChainStepVm(CaseChain.PoLabel, po.PoNumber,
                    $"{po.ApprovalStatus} / {po.DocStatus}", $"Vendor {po.VendorId}, {po.Currency}.");
        }
        catch (Exception ex)
        {
            return new ChainStepVm(CaseChain.PoLabel, kase.PoNumber, "error", ex.Message);
        }
    }

    private static ChainStepVm AsnStep(Case kase) =>
        kase.AsnNumber is null
            ? new ChainStepVm(CaseChain.AsnLabel, null, "not recorded", null)
            : new ChainStepVm(CaseChain.AsnLabel, kase.AsnNumber, "recorded", null);

    private static ChainStepVm ReceiptStep(Case kase) =>
        kase.ReceiptNumber is null
            ? new ChainStepVm(CaseChain.ReceiptLabel, null, "not posted", null)
            : new ChainStepVm(CaseChain.ReceiptLabel, kase.ReceiptNumber, "posted", "Detail on ERP.");

    private static ChainStepVm InvoicesStep(Case kase) =>
        kase.Invoices.Count == 0
            ? new ChainStepVm(CaseChain.InvoicesLabel, null, "none", null)
            : new ChainStepVm(CaseChain.InvoicesLabel, string.Join("; ", kase.Invoices.Select(i => $"{i.VendorId}/{i.SupplierInvoiceNumber}")),
                $"{kase.Invoices.Count} invoice(s)", "Detail on ERP.");

    private static ChainStepVm PaymentsStep(Case kase) =>
        kase.PaymentNumbers.Count == 0
            ? new ChainStepVm(CaseChain.PaymentsLabel, null, "none", null)
            : new ChainStepVm(CaseChain.PaymentsLabel, string.Join("; ", kase.PaymentNumbers),
                $"{kase.PaymentNumbers.Count} payment(s)", "Detail on ERP.");

    /// <summary>Agent scorecard for one case: transcript counts plus policy
    /// approvals from the ERP documents. Every read degrades to empty on
    /// failure with a log entry; the card always renders.</summary>
    private async Task<ScorecardVm> ScorecardAsync(CaseFile file, CancellationToken ct)
    {
        var kase = file.Case;
        IReadOnlyList<TranscriptTurn> turns = [];
        try
        {
            turns = CaseTranscript.ReadTurns(_cases.CacheDir, kase.CaseId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scorecard transcript lookup failed for case {CaseId}.", kase.CaseId);
        }

        IReadOnlyList<VisionPhotoUsage> vision = [];
        try
        {
            vision = CaseVisionUsage.ReadPhotos(_cases.CacheDir, kase.CaseId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scorecard vision lookup failed for case {CaseId}.", kase.CaseId);
        }

        PurchaseRequisition? pr = null;
        PurchaseOrder? po = null;
        decimal? threshold = null;
        try
        {
            if (kase.PrNumber is not null)
            {
                pr = await _erp.GetPrAsync(kase.PrNumber, ct).ConfigureAwait(false);
            }

            if (kase.PoNumber is not null)
            {
                po = await _erp.GetPurchaseOrderAsync(kase.PoNumber, ct).ConfigureAwait(false);
            }

            threshold = await _erp.GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Scorecard ERP lookup failed for case {CaseId}.", kase.CaseId);
        }

        return Scorecard.Build(
            turns, file, pr, po, threshold,
            _spend?.InputCostPerMillionUsd, _spend?.OutputCostPerMillionUsd, vision);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default) =>
        await _erp.GetUsersAsync(ct).ConfigureAwait(false);

    /// <summary>Dock images plus reads for one receipt, rendered from the
    /// linked case's persisted per-carton dock evidence. Images resolve
    /// from the evidence photo ids; vision-cache captions stay as the
    /// fallback. Shared with the ERP receipt record page.</summary>
    public async Task<ReceiptVm> GetReceiptMediaAsync(
        ShipmentReceipt receipt, AsnDto? asn, CancellationToken ct = default)
    {
        var reads = new List<string>();
        var images = new List<string>();
        var caseId = await FindCaseIdForReceiptAsync(receipt, asn, ct).ConfigureAwait(false);
        if (caseId is null)
        {
            return new ReceiptVm(
                receipt, images, ["No case links this receipt yet; no dock evidence recorded."]);
        }

        var evidence = ReadDockEvidence(caseId);
        if (evidence is null)
        {
            return new ReceiptVm(
                receipt, images, [$"Case {caseId} links this receipt; no dock evidence recorded yet."]);
        }

        reads.AddRange(DockEvidenceLines(evidence));
        foreach (var imageId in evidence.Reads
            .Select(r => r.ImageId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            var image = _photos.ResolveImageFileName(imageId);
            if (image is null)
            {
                reads.Add(imageId);
                continue;
            }

            if (!images.Contains(image, StringComparer.Ordinal))
            {
                images.Add(image);
            }

            try
            {
                var cached = _photos.TryCachedRead(image);
                reads.Add(cached is null ? $"{image}: no cached read." : $"{image}: {cached}");
            }
            catch (Exception ex)
            {
                reads.Add($"{image}: cached read refused ({ex.Message}).");
            }
        }

        return new ReceiptVm(receipt, images, reads);
    }

    private async Task<string?> FindCaseIdForReceiptAsync(
        ShipmentReceipt receipt, AsnDto? asn, CancellationToken ct)
    {
        try
        {
            var files = await _cases.ListAsync(ct).ConfigureAwait(false);
            return files
                .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
                .FirstOrDefault(f =>
                    string.Equals(f.Case.ReceiptNumber, receipt.ReceiptNumber, StringComparison.Ordinal)
                    || (asn is not null && string.Equals(f.Case.AsnNumber, asn.AsnNumber, StringComparison.Ordinal)))
                ?.Case.CaseId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Case lookup failed for receipt {Receipt}.", receipt.ReceiptNumber);
            return null;
        }
    }

    /// <summary>Per-carton evidence lines for one case, or empty when the
    /// case recorded no dock evidence yet. Failures log and yield nothing:
    /// the vision-cache captions stay as the fallback.</summary>
    private IReadOnlyList<string> ReadDockEvidenceLines(string caseId)
    {
        var evidence = ReadDockEvidence(caseId);
        return evidence is null ? [] : DockEvidenceLines(evidence);
    }

    private DockEvidence? ReadDockEvidence(string caseId)
    {
        try
        {
            return CaseStore.LoadDock(_cases.CacheDir, caseId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dock evidence lookup failed for case {CaseId}.", caseId);
            return null;
        }
    }

    private static IReadOnlyList<string> DockEvidenceLines(DockEvidence evidence)
    {
        var lines = new List<string>();
        foreach (var cartonId in evidence.Verdicts.Keys.Order(StringComparer.Ordinal))
        {
            var verdicts = evidence.Verdicts[cartonId];
            lines.Add(verdicts.All(v => v.Kind == VerdictKind.Pass)
                ? $"Carton {cartonId}: passed the dock check."
                : $"Carton {cartonId}: " + string.Join("; ", verdicts
                    .Where(v => v.Kind != VerdictKind.Pass)
                    .Select(v => $"{v.Kind} by {v.RuleId} — {v.Reason}")));
        }

        return lines;
    }

    public async Task<IReadOnlyList<DelegationDto>> ListDelegationsAsync(CancellationToken ct = default) =>
        await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);
}
