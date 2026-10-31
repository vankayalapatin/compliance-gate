// Adapter-backed view models for Decisions, Cases, and Records. Every
// value comes from the case cache, the run manifest, the scenario file
// (carton-to-image display mapping only), or the ERP adapter at runtime.
// Missing documents surface as notes; nothing is silently omitted.
using System.Text.Json;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class CaseViewService
{
    private static readonly JsonSerializerOptions ManifestOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly IConfiguration _config;
    private readonly ILogger<CaseViewService> _log;

    public CaseViewService(
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        IConfiguration config,
        ILogger<CaseViewService> log)
    {
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _config = config;
        _log = log;
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
        foreach (var evidence in decision.EvidenceRefs)
        {
            var image = _photos.ResolveImageFileName(evidence);
            if (image is null)
            {
                captions.Add(evidence);
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

        return new DecisionCardVm(decision, kase.SoNumber, kase.ItemId, kase.Stage, captions, images, summary);
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
                ? $"Payment {pay.PaymentNumber}: {pay.Status}, {pay.Lines.Sum(l => l.Amount)} {pay.Currency}, bank verified={pay.PayeeBank.Verified}."
                : $"Payment {decision.DocumentNumber} not found in ERP.",
            "Vendor" or "VendorBank" => await _erp.GetVendorBankAsync(decision.DocumentNumber, ct).ConfigureAwait(false) is { } bank
                ? $"Vendor {bank.VendorId} bank {bank.AccountRef}: verified={bank.Verified}."
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

        return new CaseDetailVm(
            kase, chain, file.Events, file.Decisions, holds, mail,
            AgentSummary.Build(kase, file.Events, file.Decisions, holds));
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
            : new ChainStepVm(CaseChain.PrLabel, kase.PrNumber, "linked", "Detail on Records.");

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
            : new ChainStepVm(CaseChain.ReceiptLabel, kase.ReceiptNumber, "posted", "Detail on Records.");

    private static ChainStepVm InvoicesStep(Case kase) =>
        kase.Invoices.Count == 0
            ? new ChainStepVm(CaseChain.InvoicesLabel, null, "none", null)
            : new ChainStepVm(CaseChain.InvoicesLabel, string.Join("; ", kase.Invoices.Select(i => $"{i.VendorId}/{i.SupplierInvoiceNumber}")),
                $"{kase.Invoices.Count} invoice(s)", "Detail on Records.");

    private static ChainStepVm PaymentsStep(Case kase) =>
        kase.PaymentNumbers.Count == 0
            ? new ChainStepVm(CaseChain.PaymentsLabel, null, "none", null)
            : new ChainStepVm(CaseChain.PaymentsLabel, string.Join("; ", kase.PaymentNumbers),
                $"{kase.PaymentNumbers.Count} payment(s)", "Detail on Records.");

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default) =>
        await _erp.GetUsersAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DelegationDto>> ListDelegationsAsync(CancellationToken ct = default) =>
        await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);

    public async Task<RecordsVm> GetRecordsAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        var files = await _cases.ListAsync(ct).ConfigureAwait(false);

        var soNumbers = new SortedSet<string>(StringComparer.Ordinal);
        var prNumbers = new SortedSet<string>(StringComparer.Ordinal);
        var poNumbers = new SortedSet<string>(StringComparer.Ordinal);
        var asnNumbers = new SortedSet<string>(StringComparer.Ordinal);
        var receiptNumbers = new SortedSet<string>(StringComparer.Ordinal);
        var invoiceKeys = new SortedSet<string>(StringComparer.Ordinal);
        var paymentNumbers = new SortedSet<string>(StringComparer.Ordinal);

        var opening = new SortedSet<string>(StringComparer.Ordinal);
        var smallPoVendors = ReadSmallPoVendors(notes);
        foreach (var doc in ReadManifestDocuments(notes))
        {
            opening.Add(doc.Number);
            switch (doc.DocType)
            {
                case "SO": soNumbers.Add(doc.Number); break;
                case "PR": prNumbers.Add(doc.Number); break;
                case "PO": poNumbers.Add(doc.Number); break;
                case "INV":
                    var vendor = VendorForSeedInvoice(doc, smallPoVendors);
                    if (vendor is null)
                    {
                        notes.Add(
                            $"Seeded invoice {doc.Number} (event '{doc.Event}') names no resolvable vendor; " +
                            "it stays tracked as opening data but cannot be fetched.");
                    }
                    else
                    {
                        invoiceKeys.Add($"{vendor}/{doc.Number}");
                    }
                    break;
                default: break;
            }
        }

        foreach (var file in files)
        {
            soNumbers.Add(file.Case.SoNumber);
            if (file.Case.PrNumber is not null) prNumbers.Add(file.Case.PrNumber);
            if (file.Case.PoNumber is not null) poNumbers.Add(file.Case.PoNumber);
            if (file.Case.AsnNumber is not null) asnNumbers.Add(file.Case.AsnNumber);
            if (file.Case.ReceiptNumber is not null) receiptNumbers.Add(file.Case.ReceiptNumber);
            foreach (var key in file.Case.Invoices)
            {
                invoiceKeys.Add($"{key.VendorId}/{key.SupplierInvoiceNumber}");
            }
            foreach (var pay in file.Case.PaymentNumbers)
            {
                paymentNumbers.Add(pay);
            }
        }

        var salesOrders = new List<SalesOrder>();
        foreach (var soNumber in soNumbers)
        {
            var so = await TryReadAsync(() => _erp.GetSalesOrderAsync(soNumber, ct), $"SO {soNumber}", notes).ConfigureAwait(false);
            if (so is not null) salesOrders.Add(so);
        }

        var prs = new List<PurchaseRequisition>();
        foreach (var prNumber in prNumbers)
        {
            var pr = await TryReadAsync(() => _erp.GetPrAsync(prNumber, ct), $"PR {prNumber}", notes).ConfigureAwait(false);
            if (pr is not null) prs.Add(pr);
        }

        var pos = new List<PurchaseOrder>();
        foreach (var poNumber in poNumbers)
        {
            var po = await TryReadAsync(() => _erp.GetPurchaseOrderAsync(poNumber, ct), $"PO {poNumber}", notes).ConfigureAwait(false);
            if (po is not null) pos.Add(po);
        }

        var asns = new List<AsnDto>();
        foreach (var asnNumber in asnNumbers)
        {
            var asn = await TryReadAsync(() => _erp.GetAsnAsync(asnNumber, ct), $"ASN {asnNumber}", notes).ConfigureAwait(false);
            if (asn is not null) asns.Add(asn);
        }

        var cartonImages = ReadCartonImages(notes);
        var receipts = new List<ReceiptVm>();
        foreach (var receiptNumber in receiptNumbers)
        {
            var receipt = await TryReadAsync(() => _erp.GetReceiptAsync(receiptNumber, ct), $"Receipt {receiptNumber}", notes).ConfigureAwait(false);
            if (receipt is null) continue;
            var images = ImagesForReceipt(receipt, asns, cartonImages);
            var reads = new List<string>();
            foreach (var image in images)
            {
                try
                {
                    var read = _photos.TryCachedRead(image);
                    reads.Add(read is null ? $"{image}: no cached read." : $"{image}: {read}");
                }
                catch (Exception ex)
                {
                    reads.Add($"{image}: cached read refused ({ex.Message}).");
                }
            }
            receipts.Add(new ReceiptVm(receipt, images, reads));
        }

        var invoices = new List<VendorInvoice>();
        foreach (var key in invoiceKeys)
        {
            var parts = key.Split('/', 2);
            var inv = await TryReadAsync(() => _erp.GetInvoiceAsync(parts[0], parts[1], ct), $"Invoice {key}", notes).ConfigureAwait(false);
            if (inv is not null) invoices.Add(inv);
        }

        var payments = new List<Payment>();
        foreach (var payNumber in paymentNumbers)
        {
            var pay = await TryReadAsync(() => _erp.GetPaymentAsync(payNumber, ct), $"Payment {payNumber}", notes).ConfigureAwait(false);
            if (pay is not null) payments.Add(pay);
        }

        var itemIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            itemIds.Add(file.Case.ItemId);
        }
        foreach (var so in salesOrders) foreach (var line in so.Lines) itemIds.Add(line.ItemId);
        foreach (var pr in prs) foreach (var line in pr.Lines) itemIds.Add(line.ItemId);
        foreach (var po in pos) foreach (var line in po.Lines) itemIds.Add(line.ItemId);

        var inventory = new List<InventoryVm>();
        foreach (var itemId in itemIds)
        {
            decimal stock = 0;
            IReadOnlyList<InventoryTransaction> txns = [];
            try
            {
                stock = await _erp.GetStockAsync(itemId, ct).ConfigureAwait(false);
                txns = await _erp.GetInventoryTransactionsAsync(itemId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                notes.Add($"Inventory for {itemId} failed: {ex.Message}.");
            }
            inventory.Add(new InventoryVm(itemId, stock, txns));
        }

        var approvals = new List<ApprovalVm>();
        foreach (var pr in prs) approvals.AddRange(await ApprovalsForAsync("PR", pr.PrNumber, ct).ConfigureAwait(false));
        foreach (var po in pos) approvals.AddRange(await ApprovalsForAsync("PO", po.PoNumber, ct).ConfigureAwait(false));
        foreach (var inv in invoices) approvals.AddRange(await ApprovalsForAsync("Invoice", $"{inv.VendorId}/{inv.SupplierInvoiceNumber}", ct).ConfigureAwait(false));
        foreach (var pay in payments) approvals.AddRange(await ApprovalsForAsync("Payment", pay.PaymentNumber, ct).ConfigureAwait(false));

        IReadOnlyList<HoldDto> holds = [];
        try
        {
            holds = await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notes.Add($"Open-hold list failed: {ex.Message}.");
        }

        IReadOnlyList<VendorDto> vendors = [];
        try
        {
            vendors = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .OrderBy(v => v.VendorId, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"Vendor list failed: {ex.Message}.");
        }

        return new RecordsVm(salesOrders, prs, pos, asns, receipts, invoices, payments, inventory, holds, approvals, vendors, notes, opening);
    }

    /// <summary>Maps scenario small-PO keys to their vendors so seeded INV
    /// manifest entries (event 'smallPo.&lt;key&gt;.invoice') resolve to a
    /// vendor for fetching. Empty when the scenario file is absent.</summary>
    private Dictionary<string, string> ReadSmallPoVendors(List<string> notes)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = Gate.Erp.ScenarioLoader.ResolvePath(ScenarioEndpoints.ScenarioPath(_config));
            if (!File.Exists(path))
            {
                return map;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("smallPos", out var smallPos)
                || smallPos.ValueKind != JsonValueKind.Array)
            {
                return map;
            }

            foreach (var row in smallPos.EnumerateArray())
            {
                var key = row.TryGetProperty("key", out var k) ? k.GetString() : null;
                var vendor = row.TryGetProperty("vendor", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(vendor))
                {
                    map[key] = vendor;
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Scenario small-PO map read failed: {ex.Message}.");
        }

        return map;
    }

    private static string? VendorForSeedInvoice(
        ManifestDoc doc, IReadOnlyDictionary<string, string> smallPoVendors)
    {
        const string prefix = "smallPo.";
        const string suffix = ".invoice";
        if (!doc.Event.StartsWith(prefix, StringComparison.Ordinal)
            || !doc.Event.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var key = doc.Event[prefix.Length..^suffix.Length];
        return smallPoVendors.TryGetValue(key, out var vendor) ? vendor : null;
    }

    private async Task<IReadOnlyList<ApprovalVm>> ApprovalsForAsync(string docType, string docNumber, CancellationToken ct)
    {
        try
        {
            var rows = await _erp.GetApprovalsAsync(docType, docNumber, ct).ConfigureAwait(false);
            return rows.Select(a => new ApprovalVm(
                a.DocumentType, a.DocumentNumber, a.RoutedTo, a.ActedBy,
                a.Approved, a.Comment, a.DocumentAmount, a.LimitChecked)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Approval list failed for {DocType} {DocNumber}.", docType, docNumber);
            return [];
        }
    }

    private static async Task<T?> TryReadAsync<T>(Func<Task<T?>> read, string label, List<string> notes) where T : class
    {
        try
        {
            var value = await read().ConfigureAwait(false);
            if (value is null)
            {
                notes.Add($"{label} referenced but not found in ERP.");
            }
            return value;
        }
        catch (Exception ex)
        {
            notes.Add($"{label} read failed: {ex.Message}.");
            return null;
        }
    }

    private IReadOnlyList<ManifestDoc> ReadManifestDocuments(List<string> notes)
    {
        try
        {
            var path = Gate.Erp.ScenarioLoader.ResolvePath(ScenarioEndpoints.ManifestPath(_config));
            if (!File.Exists(path))
            {
                notes.Add("No run manifest yet; seed documents are unknown until a scenario load.");
                return [];
            }

            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<ScenarioManifestDto>(json, ManifestOpts);
            if (manifest?.Documents is null)
            {
                notes.Add($"Run manifest {path} did not parse.");
                return [];
            }

            return manifest.Documents;
        }
        catch (Exception ex)
        {
            notes.Add($"Run manifest read failed: {ex.Message}.");
            return [];
        }
    }

    private sealed record ScenarioManifestDto(IReadOnlyList<ManifestDoc>? Documents);

    private sealed record ManifestDoc(string Event, string DocType, string Number);

    private Dictionary<string, IReadOnlyList<string>> ReadCartonImages(List<string> notes)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            var path = Gate.Erp.ScenarioLoader.ResolvePath(ScenarioEndpoints.ScenarioPath(_config));
            if (!File.Exists(path))
            {
                return map;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("cartons", out var cartons))
            {
                return map;
            }

            foreach (var carton in cartons.EnumerateArray())
            {
                if (!carton.TryGetProperty("id", out var idProp)) continue;
                var images = new List<string>();
                if (carton.TryGetProperty("visibleIn", out var visible))
                {
                    foreach (var img in visible.EnumerateArray())
                    {
                        var name = img.GetString();
                        if (name is null) continue;
                        var resolved = _photos.ResolveImageFileName(name);
                        if (resolved is not null) images.Add(resolved);
                    }
                }
                map[idProp.GetString() ?? string.Empty] = images;
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Scenario carton map read failed: {ex.Message}.");
        }

        return map;
    }

    private static IReadOnlyList<string> ImagesForReceipt(
        ShipmentReceipt receipt, IReadOnlyList<AsnDto> asns, Dictionary<string, IReadOnlyList<string>> cartonImages)
    {
        var images = new List<string>();
        var asn = asns.FirstOrDefault(a => string.Equals(a.PoNumber, receipt.PoNumber, StringComparison.Ordinal));
        if (asn is null)
        {
            return images;
        }

        foreach (var carton in asn.Cartons)
        {
            if (cartonImages.TryGetValue(carton.CartonId, out var names))
            {
                foreach (var name in names)
                {
                    if (!images.Contains(name, StringComparer.Ordinal))
                    {
                        images.Add(name);
                    }
                }
            }
        }

        return images;
    }
}
