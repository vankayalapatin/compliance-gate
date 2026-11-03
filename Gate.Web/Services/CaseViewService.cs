// Adapter-backed view models for Decisions, Cases, and ERP. Every value
// comes from the case files, the dock-evidence sidecars, or the ERP
// adapter at runtime. Missing documents surface as notes; nothing is
// silently omitted.
using Gate.Agent;
using Gate.Compliance;
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
    private readonly P2pAgent? _screening;

    public CaseViewService(
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ILogger<CaseViewService> log,
        ModelSpendTracker? spend = null,
        CountryNames? countries = null,
        P2pAgent? screening = null)
    {
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _log = log;
        _spend = spend;
        _countries = countries;
        _screening = screening;
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
                var pretty = DockReadFormat.Pretty(_photos.TryCachedRead(image));
                captions.Add(pretty is null
                    ? $"Photo {image}: no cached read."
                    : $"Photo {image} cached read:\n{pretty}");
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

        string? ruleLine;
        try
        {
            ruleLine = await RuleLineAsync(decision, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rule line failed for decision {DecisionId}.", decision.DecisionId);
            ruleLine = null;
        }

        // PR/PO approvers see the screening table on the card: who was
        // considered, at what price, and why this vendor won.
        IReadOnlyList<ScreeningRowVm>? screening = null;
        if (decision.Kind is DecisionKind.PrApproval or DecisionKind.PoApproval)
        {
            try
            {
                screening = await ScreeningTableAsync(kase.CaseId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Card screening table failed for case {CaseId}.", kase.CaseId);
            }
        }

        return new DecisionCardVm(
            decision, kase.SoNumber, kase.ItemId, kase.Stage, captions, images, summary, figures, ruleLine, screening);
    }

    /// <summary>One row per screened candidate for a case, or empty when
    /// screening has not run or the screening agent is unavailable.</summary>
    public async Task<IReadOnlyList<ScreeningRowVm>> ScreeningTableAsync(
        string caseId, CancellationToken ct = default)
    {
        if (_screening is null)
        {
            return [];
        }

        CaseFile? file;
        try
        {
            file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Screening table lookup failed for case {CaseId}.", caseId);
            return [];
        }

        return file is null
            ? []
            : await VendorScreeningTable.RowsAsync(
                _erp, _screening, file, _log, _countries?.Map, ct).ConfigureAwait(false);
    }

    /// <summary>Rule line for one decision card: rule id, pack version, and
    /// name plus the reason — or, for a plain approval with no rule, the
    /// routing in words ("Over Maya's $5,000 limit, so it goes to Dana
    /// (Del covering)."). Never a bare double dash.</summary>
    public async Task<string?> RuleLineAsync(Decision decision, CancellationToken ct = default)
    {
        if (decision.RuleId is not null)
        {
            var reason = decision.Reason ?? "no reason recorded";
            var rule = RuleCatalog.TryFind(decision.RuleId);
            return rule is null
                ? $"{decision.RuleId} — {reason}"
                : $"{rule.RuleId} ({rule.PackVersion}): {rule.Title} — {reason}";
        }

        if (decision.Kind is not (DecisionKind.PrApproval or DecisionKind.PoApproval
            or DecisionKind.InvoiceApproval or DecisionKind.PaymentApproval))
        {
            return decision.Reason;
        }

        string? requester = null;
        try
        {
            requester = decision.DocumentType switch
            {
                "PR" => (await _erp.GetPrAsync(decision.DocumentNumber!, ct).ConfigureAwait(false))?.Requester,
                "PO" => await PoRequesterAsync(decision.DocumentNumber, ct).ConfigureAwait(false),
                "Invoice" => await InvoiceRequesterAsync(decision.DocumentNumber, ct).ConfigureAwait(false),
                "Payment" => (await _erp.GetPaymentAsync(decision.DocumentNumber!, ct).ConfigureAwait(false))?.CreatedBy,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rule line requester lookup failed for decision {DecisionId}.", decision.DecisionId);
        }

        if (requester is null || decision.Amount is null)
        {
            return decision.Reason ?? "Needs a human approval.";
        }

        UserDto? user;
        try
        {
            user = await _erp.GetUserAsync(requester, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rule line user lookup failed for {User}.", requester);
            user = null;
        }

        if (user is null)
        {
            return decision.Reason ?? "Needs a human approval.";
        }

        string? covering = null;
        try
        {
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            covering = (await _erp.GetDelegationsAsync(ct).ConfigureAwait(false))
                .Where(d => string.Equals(d.FromUser, decision.RequestedTo, StringComparison.OrdinalIgnoreCase)
                    && today >= d.Start && today <= d.End)
                .Select(d => d.ToUser)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rule line delegation lookup failed for decision {DecisionId}.", decision.DecisionId);
        }

        var tail = covering is null ? "." : $" ({covering} covering).";
        return decision.Amount.Value > user.SigningLimit
            ? $"Over {requester}'s {ErpFormat.Money(user.SigningLimit, user.LimitCurrency)} limit, " +
              $"so it goes to {decision.RequestedTo}{tail}"
            : $"Routed to {decision.RequestedTo}{tail} {decision.Reason ?? "Needs a human approval."}";
    }

    private async Task<string?> PoRequesterAsync(string? poNumber, CancellationToken ct)
    {
        if (poNumber is null)
        {
            return null;
        }

        var po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
        return po is null
            ? null
            : (await _erp.GetPrAsync(po.PrNumber, ct).ConfigureAwait(false))?.Requester;
    }

    private async Task<string?> InvoiceRequesterAsync(string? documentNumber, CancellationToken ct)
    {
        if (documentNumber is null)
        {
            return null;
        }

        var parts = documentNumber.Split('/', 2);
        return parts.Length != 2
            ? null
            : (await _erp.GetInvoiceAsync(parts[0], parts[1], ct).ConfigureAwait(false))?.CreatedBy;
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
            // One line per carton: each failing verdict as its reason
            // sentence plus rule, or the pass line when all passed.
            var caption = verdicts.Count == 0
                ? $"Carton {cartonId}: passed the dock check."
                : $"Carton {cartonId}: " + string.Join(" ", verdicts.Select(v => $"{v.Reason} ({v.RuleId})"));

            var cartonReads = evidence.Reads
                .Where(r => string.Equals(r.CartonId, cartonId, StringComparison.Ordinal))
                .ToList();
            // The judging photo: damage cards show the read that saw the
            // damage, every other card the carton's first read.
            var judging = decision.Kind == DecisionKind.DamageReview
                ? cartonReads.FirstOrDefault(r => r.DamageVisible) ?? cartonReads.FirstOrDefault()
                : cartonReads.FirstOrDefault();
            var image = _photos.ResolveImageFileName(judging?.ImageId);
            string? read = null;
            if (image is not null)
            {
                try
                {
                    var cached = _photos.TryCachedRead(image);
                    var pretty = DockReadFormat.Pretty(cached);
                    read = pretty is null
                        ? $"Photo {image}: no cached read."
                        : $"Photo {image} cached read:\n{pretty}";
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cached read refused for {Image}.", image);
                    read = $"Photo {image}: cached read refused ({ex.Message}).";
                }
            }

            figures.Add(new CartonFigureVm(cartonId, image, caption, read));
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
                ? $"PR {pr.PrNumber}: {ErpStatusWords.Of(pr.Status)}, {ErpFormat.Money(pr.EstimatedAmount, pr.Currency)}, {LineWords(pr.Lines.Count)}."
                : $"PR {decision.DocumentNumber} not found in ERP.",
            "PO" => await _erp.GetPurchaseOrderAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } po
                ? $"PO {po.PoNumber}: {ErpStatusWords.Of(po.ApprovalStatus)}, document {ErpStatusWords.Of(po.DocStatus).ToLowerInvariant()}, {ErpFormat.Money(po.Lines.Sum(l => l.LineAmount), po.Currency)}."
                : $"PO {decision.DocumentNumber} not found in ERP.",
            "Invoice" => await DescribeInvoiceAsync(decision.DocumentNumber, ct).ConfigureAwait(false),
            "Payment" => await _erp.GetPaymentAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } pay
                ? $"Payment {pay.PaymentNumber}: {ErpStatusWords.Of(pay.Status).ToLowerInvariant()}, {ErpFormat.Money(pay.Lines.Sum(l => l.Amount), pay.Currency)}, payee bank {(pay.PayeeBank.Verified ? "verified" : "not verified")}."
                : $"Payment {decision.DocumentNumber} not found in ERP.",
            "Vendor" or "VendorBank" => await _erp.GetVendorBankAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } bank
                ? $"Vendor {bank.VendorId} bank {bank.AccountRef}: {(bank.Verified ? "verified" : "not verified")}."
                : $"Vendor bank for {decision.DocumentNumber} not found in ERP.",
            _ => $"Document {decision.DocumentType} {decision.DocumentNumber} (no ERP detail reader).",
        };
    }

    private static string LineWords(int count) =>
        count == 1 ? "1 line" : $"{count} lines";

    private async Task<string> VendorNameAsync(string vendorId, CancellationToken ct)
    {
        try
        {
            var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
            return vendors.FirstOrDefault(
                v => string.Equals(v.VendorId, vendorId, StringComparison.Ordinal))?.Name
                ?? vendorId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor name lookup failed for {Vendor}.", vendorId);
            return vendorId;
        }
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
        return $"Invoice {inv.SupplierInvoiceNumber} from {await VendorNameAsync(inv.VendorId, ct).ConfigureAwait(false)}: {ErpStatusWords.Of(inv.Status).ToLowerInvariant()}, {ErpFormat.Money(total, inv.Currency)}, voucher {inv.Voucher ?? "none"}.";
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

        IReadOnlyList<ScreeningRowVm> screening = [];
        try
        {
            screening = await ScreeningTableAsync(kase.CaseId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Screening-table lookup failed for case {CaseId}.", kase.CaseId);
        }

        var ruleLines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var decision in file.Decisions)
        {
            try
            {
                var line = await RuleLineAsync(decision, ct).ConfigureAwait(false);
                if (line is not null)
                {
                    ruleLines[decision.DecisionId] = line;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Rule line failed for decision {DecisionId}.", decision.DecisionId);
            }
        }

        IReadOnlyList<SplitChildVm> splitChildren = [];
        try
        {
            splitChildren = await SplitChildrenAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Split-children lookup failed for case {CaseId}.", kase.CaseId);
        }

        return new CaseDetailVm(
            kase, chain, file.Events, file.Decisions, holds, mail,
            AgentSummary.Build(kase, file.Events, file.Decisions, holds),
            math.Select(m => m.Line).ToList(),
            await ScorecardAsync(file, ct).ConfigureAwait(false),
            screening, ruleLines, splitChildren);
    }

    /// <summary>Split children for a parent's page: each child with its
    /// approved line plus its own order and stage. Empty for non-parents.
    /// The plan on the split decision names the lines; children match by
    /// awarded vendor, in plan order.</summary>
    private async Task<IReadOnlyList<SplitChildVm>> SplitChildrenAsync(CaseFile file, CancellationToken ct)
    {
        var plan = file.Decisions
            .Where(d => d.Kind == DecisionKind.SplitApproval)
            .Select(d => d.Split)
            .FirstOrDefault(p => p is not null);
        if (plan is null)
        {
            return [];
        }

        var children = (await _cases.ListAsync(ct).ConfigureAwait(false))
            .Where(f => string.Equals(f.Case.ParentCaseId, file.Case.CaseId, StringComparison.Ordinal))
            .ToList();
        if (children.Count == 0)
        {
            return [];
        }

        var rows = new List<SplitChildVm>();
        foreach (var line in plan.Lines)
        {
            var child = children.FirstOrDefault(c =>
                string.Equals(c.Case.AwardedVendorId, line.VendorId, StringComparison.Ordinal));
            if (child is null)
            {
                continue;
            }

            rows.Add(new SplitChildVm(
                child.Case.CaseId, line.VendorName, line.Quantity, child.Case.PoNumber,
                CaseDisplay.StageSummary(child.Case.Stage)));
        }

        return rows;
    }

    private async Task<ChainStepVm> SoStepAsync(Case kase, CancellationToken ct)
    {
        try
        {
            var so = await _erp.GetSalesOrderAsync(kase.SoNumber, ct).ConfigureAwait(false);
            return so is null
                ? new ChainStepVm(CaseChain.SoLabel, kase.SoNumber, "missing", "SO not found in ERP.")
                : new ChainStepVm(CaseChain.SoLabel, so.SoNumber, ErpStatusWords.Of(so.Status),
                    $"{LineWords(so.Lines.Count)}, {so.Currency}.");
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
                    $"{ErpStatusWords.Of(po.ApprovalStatus)} / {ErpStatusWords.Of(po.DocStatus)}",
                    $"Vendor {await VendorNameAsync(po.VendorId, ct).ConfigureAwait(false)}, {po.Currency}.");
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
                CaseChain.CountWords(kase.Invoices.Count, "invoice"), "Detail on ERP.");

    private static ChainStepVm PaymentsStep(Case kase) =>
        kase.PaymentNumbers.Count == 0
            ? new ChainStepVm(CaseChain.PaymentsLabel, null, "none", null)
            : new ChainStepVm(CaseChain.PaymentsLabel, string.Join("; ", kase.PaymentNumbers),
                CaseChain.CountWords(kase.PaymentNumbers.Count, "payment"), "Detail on ERP.");

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
                var pretty = DockReadFormat.Pretty(_photos.TryCachedRead(image));
                reads.Add(pretty is null ? $"{image}: no cached read." : $"{image}:\n{pretty}");
            }
            catch (Exception ex)
            {
                reads.Add($"{image}: cached read refused ({ex.Message}).");
            }
        }

        return new ReceiptVm(receipt, images, reads);
    }

    /// <summary>Screening table for a PR/PO record page, found through the
    /// case carrying that document number. Null when no case links it.</summary>
    public async Task<ErpLineTable?> ScreeningSectionAsync(
        string? prNumber, string? poNumber, CancellationToken ct = default)
    {
        string? caseId = null;
        try
        {
            var files = await _cases.ListAsync(ct).ConfigureAwait(false);
            caseId = files
                .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
                .FirstOrDefault(f =>
                    (prNumber is not null
                        && string.Equals(f.Case.PrNumber, prNumber, StringComparison.Ordinal))
                    || (poNumber is not null
                        && string.Equals(f.Case.PoNumber, poNumber, StringComparison.Ordinal)))
                ?.Case.CaseId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Case lookup failed for screening section {Pr}/{Po}.", prNumber, poNumber);
            return null;
        }

        if (caseId is null)
        {
            return null;
        }

        var rows = await ScreeningTableAsync(caseId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        return new ErpLineTable("Vendor screening",
            [new("Vendor"), new("Country"), new("Unit price", true), new("Result"), new("Reason")],
            rows.Select(r => (IReadOnlyList<ErpCell>)
            [
                new(r.Awarded ? $"{r.VendorName} (awarded)" : r.VendorName),
                new(r.Country),
                new(r.Price, true),
                new(r.Result),
                new(r.Reason),
            ]).ToList());
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
                : $"Carton {cartonId}: " + string.Join(" ", verdicts
                    .Where(v => v.Kind != VerdictKind.Pass)
                    .Select(v => $"{v.Reason} ({v.RuleId})")));
        }

        return lines;
    }

    public async Task<IReadOnlyList<DelegationDto>> ListDelegationsAsync(CancellationToken ct = default) =>
        await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);
}
