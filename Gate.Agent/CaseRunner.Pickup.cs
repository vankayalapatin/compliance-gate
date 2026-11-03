// ERP-work pickup: connects seeded (or human-created) ERP documents to
// agent cases. The agent picks up work the ERP hands it: any approved PR
// without a PO, any approved or confirmed PO the goods leg still owns, and
// any received or matched invoice, opening a live case when no case covers
// the document yet. Pure C# plus adapter reads/writes: no model calls.
// Deterministic document order, idempotent re-runs (covered documents are
// skipped, never re-adopted), and every adoption is a timeline event.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public partial class CaseRunner
{
    /// <summary>Ensures every actionable ERP document has a live agent case:
    /// approved PRs without a PO (screened, awarded, and ordered by the
    /// normal advance), approved/confirmed non-closed POs (parked on the
    /// supplier leg their state names), and received/matched invoices on
    /// those POs that carry no pending approval (matched when unmatched,
    /// then intaken like any invoice). Opens a live case when no live case
    /// covers the document; converts the opening shell when the human lane
    /// already projected one (its decided card stays as history). Skips
    /// submitted/draft documents (the human lane owns them through opening
    /// cards), closed/cancelled documents (history), and documents a
    /// terminal case already closed (no resurrection). Returns the touched
    /// case ids in adoption order.</summary>
    public async Task<IReadOnlyList<string>> PickupErpWorkAsync(
        string scenarioName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scenarioName))
        {
            throw new ArgumentException("Scenario name is required for ERP-work pickup.", nameof(scenarioName));
        }

        try
        {
            var files = LoadCaseIndex();
            var touched = new List<string>();

            var purchaseOrders = (await _erp.GetPurchaseOrdersAsync(ct).ConfigureAwait(false))
                .OrderBy(p => p.PoNumber, StringComparer.Ordinal)
                .ToList();
            var prsWithPo = purchaseOrders
                .Select(p => p.PrNumber)
                .ToHashSet(StringComparer.Ordinal);

            var prs = (await _erp.GetPurchaseRequisitionsAsync(ct).ConfigureAwait(false))
                .Where(p => p.Status == PrStatus.Approved)
                .OrderBy(p => p.PrNumber, StringComparer.Ordinal)
                .ToList();
            foreach (var pr in prs)
            {
                if (prsWithPo.Contains(pr.PrNumber))
                {
                    continue;
                }

                // Opening shells do not cover the PR: adopting converts the
                // shell into the live case instead of skipping it.
                if (files.Values.Any(f => !f.Case.IsOpeningData && LinksPr(f, pr.PrNumber)))
                {
                    continue;
                }

                touched.Add(await AdoptPrCaseAsync(scenarioName, pr, files, ct).ConfigureAwait(false));
            }

            var asns = (await _erp.GetAsnsAsync(ct).ConfigureAwait(false))
                .OrderBy(a => a.AsnNumber, StringComparer.Ordinal)
                .ToList();
            var receipts = (await _erp.GetShipmentReceiptsAsync(ct).ConfigureAwait(false))
                .OrderBy(r => r.ReceiptNumber, StringComparer.Ordinal)
                .ToList();
            foreach (var po in purchaseOrders.Where(IsAdoptablePo))
            {
                // Opening shells do not cover the PO: adopting converts the
                // shell into the live case instead of skipping it.
                if (files.Values.Any(f => !f.Case.IsOpeningData && LinksPo(f, po.PoNumber)))
                {
                    continue;
                }

                touched.Add(await AdoptPoCaseAsync(scenarioName, po, asns, receipts, files, ct).ConfigureAwait(false));
            }

            // Submitted invoices still read Matched: the pending set keeps
            // pickup off the human lane's cards (no duplicate approvals).
            var pendingInvoices = (await _erp.GetPendingApprovalsAsync(ct).ConfigureAwait(false))
                .Where(r => r.DocumentType == "Invoice")
                .Select(r => r.DocumentNumber)
                .ToHashSet(StringComparer.Ordinal);
            var invoices = (await _erp.GetVendorInvoicesAsync(ct).ConfigureAwait(false))
                .Where(i => i.Status is InvoiceStatus.Received or InvoiceStatus.Matched)
                .OrderBy(i => i.VendorId, StringComparer.Ordinal)
                .ThenBy(i => i.SupplierInvoiceNumber, StringComparer.Ordinal)
                .ToList();
            foreach (var invoice in invoices)
            {
                var adopted = await AdoptInvoiceAsync(
                    scenarioName, invoice, purchaseOrders, asns, receipts, pendingInvoices, files, ct)
                    .ConfigureAwait(false);
                if (adopted is not null && !touched.Contains(adopted, StringComparer.Ordinal))
                {
                    touched.Add(adopted);
                }
            }

            _logger.LogInformation(
                "ERP-work pickup for {Scenario}: {ApprovedPrs} approved PR(s), {Pos} adoptable PO(s), " +
                "{Invoices} receivable invoice(s), {Touched} case(s) touched.",
                scenarioName, prs.Count, purchaseOrders.Count(IsAdoptablePo),
                invoices.Count, touched.Count);
            return touched;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ERP-work pickup failed for scenario {Scenario}.", scenarioName);
            throw;
        }
    }

    private static bool IsAdoptablePo(PurchaseOrder po) =>
        po.ApprovalStatus is PoApprovalStatus.Approved or PoApprovalStatus.Confirmed
        && po.DocStatus is not PoDocStatus.Closed and not PoDocStatus.Cancelled;

    private static bool IsTerminal(Case current) =>
        current.Stage is CaseStage.Resolved or CaseStage.Closed or CaseStage.Cancelled;

    private static bool LinksPr(CaseFile file, string prNumber) =>
        string.Equals(file.Case.PrNumber, prNumber, StringComparison.Ordinal);

    private static bool LinksPo(CaseFile file, string poNumber) =>
        string.Equals(file.Case.PoNumber, poNumber, StringComparison.Ordinal);

    private Dictionary<string, CaseFile> LoadCaseIndex()
    {
        var files = new Dictionary<string, CaseFile>(StringComparer.Ordinal);
        foreach (var caseId in CaseStore.ListCaseIds(_cacheDir))
        {
            var file = CaseStore.Load(_cacheDir, caseId);
            if (file is not null)
            {
                files[caseId] = file;
            }
        }

        return files;
    }

    private void RefreshIndex(Dictionary<string, CaseFile> files, string caseId) =>
        files[caseId] = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished during ERP-work pickup.");

    /// <summary>Adopts one approved PR without a PO into a live case at
    /// PrApproved. The handoff carries no sourcing candidates (the ERP
    /// exposes no vendors-for-item read), so the case screens every ERP
    /// vendor, named in the pickup event. Converts the opening shell when
    /// the human lane projected one for this PR.</summary>
    private async Task<string> AdoptPrCaseAsync(
        string scenarioName, PurchaseRequisition pr,
        Dictionary<string, CaseFile> files, CancellationToken ct)
    {
        var caseId = CaseStore.MintCaseId(scenarioName, "PR", pr.PrNumber);
        if (files.TryGetValue(caseId, out var existing))
        {
            if (!existing.Case.IsOpeningData)
            {
                if (LinksPr(existing, pr.PrNumber))
                {
                    return caseId;
                }

                throw new InvalidOperationException(
                    $"Case {caseId} is a live case for another document; " +
                    $"refusing to adopt PR {pr.PrNumber} over it.");
            }

            return ConvertPrShell(files, existing, pr, await VendorIdsAsync(ct).ConfigureAwait(false));
        }

        return CreatePrCase(scenarioName, caseId, pr, await VendorIdsAsync(ct).ConfigureAwait(false), files);
    }

    private async Task<IReadOnlyList<string>> VendorIdsAsync(CancellationToken ct) =>
        (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .Select(v => v.VendorId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private string CreatePrCase(
        string scenarioName, string caseId, PurchaseRequisition pr,
        IReadOnlyList<string> vendorIds, Dictionary<string, CaseFile> files)
    {
        var now = DateTimeOffset.UtcNow;
        var itemId = pr.Lines.FirstOrDefault()?.ItemId ?? string.Empty;
        var fresh = new CaseFile(
            new Case(caseId, string.Empty, itemId, scenarioName, CaseStage.PrApproved, now, now,
                pr.PrNumber, null, null, null, [], [], null, null),
            [], []);
        var work = new CaseWork(fresh);
        work.AddEvent(CaseEventKind.Note, PrPickupSummary(pr, vendorIds),
            documentType: "PR", documentNumber: pr.PrNumber);
        WriteCandidates(work, itemId, vendorIds);
        work.Save(_cacheDir);
        files[caseId] = CaseStore.Load(_cacheDir, caseId)!;
        return caseId;
    }

    private string ConvertPrShell(
        Dictionary<string, CaseFile> files, CaseFile shell,
        PurchaseRequisition pr, IReadOnlyList<string> vendorIds)
    {
        var itemId = pr.Lines.FirstOrDefault()?.ItemId ?? string.Empty;
        var work = new CaseWork(shell with
        {
            Case = shell.Case with
            {
                ItemId = itemId,
                Stage = CaseStage.PrApproved,
                PrNumber = pr.PrNumber,
                IsOpeningData = false
            }
        });
        work.AddEvent(CaseEventKind.Note, PrPickupSummary(pr, vendorIds),
            documentType: "PR", documentNumber: pr.PrNumber);
        WriteCandidates(work, itemId, vendorIds);
        work.Save(_cacheDir);
        RefreshIndex(files, shell.Case.CaseId);
        return shell.Case.CaseId;
    }

    private static string PrPickupSummary(PurchaseRequisition pr, IReadOnlyList<string> vendorIds)
    {
        var lines = pr.Lines.Count == 0
            ? "no lines"
            : string.Join("; ", pr.Lines.Select(l => $"{DisplayWords.Qty(l.Quantity)} {l.Unit} of {l.ItemId}"));
        return $"Picked up approved PR {pr.PrNumber} ({lines}) from ERP; no PO yet. " +
            $"No sourcing candidates on the handoff; screening all {DisplayWords.Plural(vendorIds.Count, "ERP vendor")}: " +
            string.Join(", ", vendorIds) + ".";
    }

    /// <summary>Adopts one adoptable PO into a live case, staged from the ERP
    /// leg state (receipt posted, ASN recorded, acknowledged, or placed) and
    /// linked to its PR, vendor, ASN, and receipt. Heals the live PR case
    /// when one already works this PO's PR without a PO (a PO created
    /// outside the case must not fork the chain); converts the opening
    /// shell when the human lane projected one.</summary>
    private async Task<string> AdoptPoCaseAsync(
        string scenarioName, PurchaseOrder po,
        IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts,
        Dictionary<string, CaseFile> files, CancellationToken ct)
    {
        var orphanPrCase = files.Values
            .Where(f => !f.Case.IsOpeningData
                && !IsTerminal(f.Case)
                && LinksPr(f, po.PrNumber)
                && f.Case.PoNumber is null)
            .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (orphanPrCase is not null)
        {
            return await HealPrCaseWithPoAsync(orphanPrCase, po, asns, receipts, files, ct).ConfigureAwait(false);
        }

        var caseId = CaseStore.MintCaseId(scenarioName, "PO", po.PoNumber);
        if (files.TryGetValue(caseId, out var existing))
        {
            if (!existing.Case.IsOpeningData)
            {
                if (LinksPo(existing, po.PoNumber))
                {
                    return caseId;
                }

                throw new InvalidOperationException(
                    $"Case {caseId} is a live case for another document; " +
                    $"refusing to adopt PO {po.PoNumber} over it.");
            }

            return await ConvertPoShellAsync(files, existing, po, asns, receipts, ct).ConfigureAwait(false);
        }

        return await CreatePoCaseAsync(scenarioName, caseId, po, asns, receipts, files, ct).ConfigureAwait(false);
    }

    private async Task<string> HealPrCaseWithPoAsync(
        CaseFile prCase, PurchaseOrder po,
        IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts,
        Dictionary<string, CaseFile> files, CancellationToken ct)
    {
        var (stage, asnNumber, receiptNumber, leg) = await PoLegAsync(po, asns, receipts, ct).ConfigureAwait(false);
        var current = prCase.Case;
        var work = new CaseWork(prCase);
        work.Patch(current with
        {
            PoNumber = po.PoNumber,
            AsnNumber = asnNumber,
            ReceiptNumber = receiptNumber,
            AwardedVendorId = po.VendorId
        });
        work.Stage(stage);
        work.AddEvent(CaseEventKind.Note,
            $"Picked up {po.ApprovalStatus} PO {po.PoNumber} to {po.VendorId} (PR {po.PrNumber}) from ERP; {leg}. " +
            $"Linked onto this PR case instead of opening a new one.",
            documentType: "PO", documentNumber: po.PoNumber);
        work.Save(_cacheDir);
        RefreshIndex(files, current.CaseId);
        return current.CaseId;
    }

    private async Task<string> CreatePoCaseAsync(
        string scenarioName, string caseId, PurchaseOrder po,
        IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts,
        Dictionary<string, CaseFile> files, CancellationToken ct)
    {
        var (stage, asnNumber, receiptNumber, leg) = await PoLegAsync(po, asns, receipts, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var fresh = new CaseFile(
            new Case(caseId, string.Empty, po.Lines.FirstOrDefault()?.ItemId ?? string.Empty,
                scenarioName, stage, now, now, po.PrNumber, po.PoNumber,
                asnNumber, receiptNumber, [], [], null, null, po.VendorId),
            [], []);
        var work = new CaseWork(fresh);
        work.AddEvent(CaseEventKind.Note,
            $"Picked up {po.ApprovalStatus} PO {po.PoNumber} to {po.VendorId} (PR {po.PrNumber}) from ERP; {leg}.",
            documentType: "PO", documentNumber: po.PoNumber);
        work.Save(_cacheDir);
        files[caseId] = CaseStore.Load(_cacheDir, caseId)!;
        return caseId;
    }

    private async Task<string> ConvertPoShellAsync(
        Dictionary<string, CaseFile> files, CaseFile shell, PurchaseOrder po,
        IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts, CancellationToken ct)
    {
        var (stage, asnNumber, receiptNumber, leg) = await PoLegAsync(po, asns, receipts, ct).ConfigureAwait(false);
        var work = new CaseWork(shell with
        {
            Case = shell.Case with
            {
                ItemId = po.Lines.FirstOrDefault()?.ItemId ?? string.Empty,
                Stage = stage,
                PrNumber = po.PrNumber,
                PoNumber = po.PoNumber,
                AsnNumber = asnNumber,
                ReceiptNumber = receiptNumber,
                AwardedVendorId = po.VendorId,
                IsOpeningData = false
            }
        });
        work.AddEvent(CaseEventKind.Note,
            $"Picked up {po.ApprovalStatus} PO {po.PoNumber} to {po.VendorId} (PR {po.PrNumber}) from ERP; {leg}.",
            documentType: "PO", documentNumber: po.PoNumber);
        work.Save(_cacheDir);
        RefreshIndex(files, shell.Case.CaseId);
        return shell.Case.CaseId;
    }

    /// <summary>Derives the supplier-leg stage plus links from ERP state: the
    /// first ASN and receipt on the PO (ordinal), the vendor confirmation,
    /// and the factual leg summary for the pickup event.</summary>
    private async Task<(CaseStage Stage, string? AsnNumber, string? ReceiptNumber, string Leg)> PoLegAsync(
        PurchaseOrder po, IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts,
        CancellationToken ct)
    {
        var asn = asns.FirstOrDefault(a =>
            string.Equals(a.PoNumber, po.PoNumber, StringComparison.Ordinal));
        var receipt = receipts.FirstOrDefault(r =>
            string.Equals(r.PoNumber, po.PoNumber, StringComparison.Ordinal));
        if (receipt is not null)
        {
            return (CaseStage.ReceiptPosted, asn?.AsnNumber, receipt.ReceiptNumber,
                $"receipt {receipt.ReceiptNumber} posted, awaiting dock reads");
        }

        if (asn is not null)
        {
            return (CaseStage.AsnRecorded, asn.AsnNumber, null,
                $"ASN {asn.AsnNumber} recorded, receipt pending");
        }

        var confirmation = await _erp.GetVendorConfirmationAsync(po.PoNumber, ct).ConfigureAwait(false);
        return confirmation is not null
            ? (CaseStage.PoAcknowledged, null, null, "acknowledged, awaiting ASN")
            : (CaseStage.PoPlaced, null, null, "awaiting supplier ACK");
    }

    /// <summary>Adopts one received or matched invoice onto its PO's case,
    /// adopting the PO case first when no live case covers it. Matches the
    /// invoice through the agent core when it carries no match results yet,
    /// then runs the same matched intake as supplier-sent invoices (link,
    /// events, approval card, stage). Skips already-linked invoices,
    /// pending-approval invoices (the human lane owns the card), invoices
    /// with no PO, an unresolvable PO, a pre-approval PO (the human lane
    /// owns it), a closed PO (history), and invoices whose only anchor
    /// case is terminal (no resurrection). Returns the anchor case id, or
    /// null when skipped.</summary>
    private async Task<string?> AdoptInvoiceAsync(
        string scenarioName, VendorInvoice invoice,
        IReadOnlyList<PurchaseOrder> purchaseOrders,
        IReadOnlyList<AsnDto> asns, IReadOnlyList<ShipmentReceipt> receipts,
        IReadOnlySet<string> pendingInvoices,
        Dictionary<string, CaseFile> files, CancellationToken ct)
    {
        var key = new InvoiceKey(invoice.VendorId, invoice.SupplierInvoiceNumber);
        if (files.Values.Any(f => !f.Case.IsOpeningData && f.Case.Invoices.Contains(key)))
        {
            return null;
        }

        if (pendingInvoices.Contains($"{invoice.VendorId}/{invoice.SupplierInvoiceNumber}"))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(invoice.PoNumber))
        {
            _logger.LogInformation(
                "ERP-work pickup skips invoice {Vendor}/{Number}: no PO to anchor a case.",
                invoice.VendorId, invoice.SupplierInvoiceNumber);
            return null;
        }

        var po = purchaseOrders.FirstOrDefault(p =>
            string.Equals(p.PoNumber, invoice.PoNumber, StringComparison.Ordinal))
            ?? await _erp.GetPurchaseOrderAsync(invoice.PoNumber, ct).ConfigureAwait(false);
        if (po is null)
        {
            _logger.LogInformation(
                "ERP-work pickup skips invoice {Vendor}/{Number}: PO {Po} is unresolvable.",
                invoice.VendorId, invoice.SupplierInvoiceNumber, invoice.PoNumber);
            return null;
        }

        if (!IsAdoptablePo(po))
        {
            return null;
        }

        // Live anchors win over opening shells; a shell-only anchor converts
        // through the same PO adoption (decided card stays as history).
        var anchorId = files.Values
            .Where(f => !f.Case.IsOpeningData && LinksPo(f, po.PoNumber))
            .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
            .FirstOrDefault()?.Case.CaseId
            ?? await AdoptPoCaseAsync(scenarioName, po, asns, receipts, files, ct).ConfigureAwait(false);
        var anchor = files[anchorId].Case;
        if (IsTerminal(anchor))
        {
            _logger.LogInformation(
                "ERP-work pickup skips invoice {Vendor}/{Number}: anchor case {Case} is {Stage}.",
                invoice.VendorId, invoice.SupplierInvoiceNumber, anchorId, anchor.Stage);
            return null;
        }

        var matched = invoice.MatchResults.Count > 0
            ? invoice
            : await _core.MatchExistingInvoiceAsync(
                invoice.VendorId, invoice.SupplierInvoiceNumber, ct,
                idempotencyScope: $"{anchorId}:pickup:match:{invoice.VendorId}/{invoice.SupplierInvoiceNumber}")
                .ConfigureAwait(false);
        var file = CaseStore.Load(_cacheDir, anchorId)
            ?? throw new InvalidOperationException($"Case {anchorId} vanished during ERP-work pickup.");
        await FinishMatchedIntakeAsync(file, matched, bankHold: false, ct).ConfigureAwait(false);
        RefreshIndex(files, anchorId);
        return anchorId;
    }
}
