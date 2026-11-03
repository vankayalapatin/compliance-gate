// EfCoreErpAdapter writes: PR/PO lifecycle, confirmations, ASN, receipts.
// Each write replays its idempotency key, then commits rows plus the replay
// record in one transaction so a retried key returns the minted number.
using Gate.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp;

public sealed partial class EfCoreErpAdapter
{
    public async Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<PurchaseRequisition>(db, $"pr:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var currency = (await db.Config.FindAsync([BaseCurrencyKey], ct).ConfigureAwait(false))?.Value;
        if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("Base currency is not seeded.");
        if (lines.Count == 0) throw new InvalidOperationException("PR needs at least one line.");
        foreach (var l in lines) if (l.Quantity <= 0) throw new InvalidOperationException($"PR line {l.ItemId} quantity must be positive.");
        var estimated = 0m;
        foreach (var l in lines)
        {
            var price = await db.LastPrices.FindAsync([l.ItemId], ct).ConfigureAwait(false);
            if (price is null) throw new InvalidOperationException($"No last purchase price for item {l.ItemId}: PR cannot be estimated.");
            estimated += l.Quantity * price.UnitPrice;
        }
        var threshold = await GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        var status = estimated < threshold ? PrStatus.Approved : PrStatus.Draft;
        var prNumber = await NextAsync(db, "PR", ct).ConfigureAwait(false);
        var createdAt = DateTimeOffset.UtcNow;
        string? approvedBy = null;
        DateTimeOffset? approvedAt = null;
        if (status == PrStatus.Approved)
        {
            approvedBy = ErpApprovalActors.AutoApprovePolicy;
            approvedAt = createdAt;
            db.Approvals.Add(new ApprovalRow
            {
                RowId = Guid.NewGuid().ToString("N"), DocumentType = "PR", DocumentNumber = prNumber,
                RoutedTo = string.Empty, ActedBy = approvedBy, ActingAsDelegate = false,
                Timestamp = Text(createdAt), Approved = true,
                Comment = $"Auto-approved: {estimated} {currency} under the {threshold} threshold.",
                DocumentAmount = estimated, LimitChecked = threshold
            });
        }
        db.PurchaseRequisitions.Add(new PrRow
        {
            PrNumber = prNumber, Status = status.ToString(), Requester = requester, Currency = currency,
            EstimatedAmount = estimated, Source = source, CreatedBy = createdBy,
            CreatedAt = Text(createdAt), IdempotencyKey = idempotencyKey,
            ApprovedBy = approvedBy, ApprovedAt = approvedAt is null ? null : Text(approvedAt.Value)
        });
        var lineNo = 0;
        foreach (var l in lines)
        {
            lineNo++;
            db.PurchaseRequisitionLines.Add(new PrLineRow
            {
                PrNumber = prNumber, LineNo = lineNo, ItemId = l.ItemId, Quantity = l.Quantity, Unit = l.Unit
            });
        }
        db.Requesters.Add(new RequesterRow { DocumentType = "PR", DocumentNumber = prNumber, Requester = requester });
        var pr = new PurchaseRequisition(prNumber, status, requester, currency, estimated, source, createdBy, idempotencyKey, lines.ToList(), CreatedAt: createdAt, ApprovedBy: approvedBy, ApprovedAt: approvedAt);
        RecordReplay(db, $"pr:{idempotencyKey}", pr);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return pr;
    }

    public async Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"submit:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document PR {documentNumber}.");
                var status = Enum<PrStatus>(pr.Status);
                if (status is PrStatus.Submitted) break;
                if (status is not PrStatus.Draft and not PrStatus.Rejected) throw new InvalidOperationException($"PR {documentNumber} cannot be submitted from {pr.Status}.");
                pr.Status = PrStatus.Submitted.ToString();
                break;
            }
            case "PO":
            {
                var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document PO {documentNumber}.");
                var status = Enum<PoApprovalStatus>(po.ApprovalStatus);
                if (status is PoApprovalStatus.InReview) break;
                if (status is not PoApprovalStatus.Draft) throw new InvalidOperationException($"PO {documentNumber} cannot be submitted from {po.ApprovalStatus}.");
                po.ApprovalStatus = PoApprovalStatus.InReview.ToString();
                break;
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) throw new InvalidOperationException($"Unknown invoice {documentNumber}.");
                var inv = await db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown invoice {documentNumber}.");
                var status = Enum<InvoiceStatus>(inv.Status);
                if (status is not InvoiceStatus.Received and not InvoiceStatus.Matched) throw new InvalidOperationException($"Invoice {documentNumber} cannot be submitted from {inv.Status}.");
                inv.Status = InvoiceStatus.Matched.ToString();
                break;
            }
            case "Payment":
            {
                var pay = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document Payment {documentNumber}.");
                var status = Enum<PaymentStatus>(pay.Status);
                if (status is PaymentStatus.Submitted) break;
                if (status is not PaymentStatus.Draft) throw new InvalidOperationException($"Payment {documentNumber} cannot be submitted from {pay.Status}.");
                pay.Status = PaymentStatus.Submitted.ToString();
                break;
            }
            default: throw new InvalidOperationException($"Unknown document {documentType} {documentNumber}.");
        }
        var routed = await db.RoutedTo.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false);
        if (routed is null) db.RoutedTo.Add(new RoutedToRow { DocumentType = documentType, DocumentNumber = documentNumber, RoutedTo = routedTo, SubmittedBy = submittedBy });
        else { routed.RoutedTo = routedTo; routed.SubmittedBy = submittedBy; }
        RecordReplay(db, $"submit:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<ApprovalDto>(db, $"approval:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        // Same-actor idempotency beyond the idempotency key: a repeated click
        // mints a fresh key, so the replay row cannot catch it. The same
        // actor repeating the same decision gets the stored row back; a
        // flipped decision is refused because the document already moved.
        var mine = await db.Approvals
            .Where(a => a.DocumentType == documentType && a.DocumentNumber == documentNumber)
            .OrderBy(a => a.Timestamp).ToListAsync(ct).ConfigureAwait(false);
        var prior = mine.FirstOrDefault(a =>
            string.Equals(a.ActedBy ?? a.RoutedTo, actedBy, StringComparison.OrdinalIgnoreCase));
        if (prior is not null)
        {
            if (prior.Approved != approved)
                throw new InvalidOperationException(
                    $"{actedBy} already {(prior.Approved ? "approved" : "rejected")} {documentType} {documentNumber}; " +
                    "a changed decision needs a new submission.");
            return new ApprovalDto(prior.DocumentType, prior.DocumentNumber, prior.RoutedTo, prior.ActedBy ?? prior.RoutedTo,
                prior.ActingAsDelegate, DateTime(prior.Timestamp), prior.Approved, prior.Comment,
                prior.DocumentAmount, prior.LimitChecked);
        }
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var routed = await db.RoutedTo.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false);
        if (routed is null) throw new InvalidOperationException($"Document {documentType} {documentNumber} was never submitted for approval.");
        var actor = await db.Users.FindAsync([actedBy], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown user {actedBy}.");
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var delegations = await db.Delegations.ToListAsync(ct).ConfigureAwait(false);
        var isDelegate = delegations.Any(d =>
            string.Equals(d.FromUser, routed.RoutedTo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.ToUser, actedBy, StringComparison.OrdinalIgnoreCase) &&
            today >= Date(d.Start) && today <= Date(d.End));
        if (!string.Equals(actedBy, routed.RoutedTo, StringComparison.OrdinalIgnoreCase) && !isDelegate)
            throw new InvalidOperationException($"{actedBy} is not routed to approve {documentType} {documentNumber} and holds no delegation.");
        var requester = (await db.Requesters.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false))?.Requester;
        if (requester is not null && string.Equals(actedBy, requester, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Requester cannot approve their own request.");
        var amount = await ResolveDocumentAmountAsync(db, documentType, documentNumber, ct).ConfigureAwait(false);
        if (approved && amount > actor.SigningLimit)
            throw new InvalidOperationException($"{actedBy} cannot approve {amount} above signing limit {actor.SigningLimit}.");
        if (documentType == "Payment" && approved)
        {
            _ = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown payment {documentNumber}.");
            var payLines = await db.PaymentLines.Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in payLines)
            {
                var invKey = InvoiceDoc(line.VendorId, line.SupplierInvoiceNumber);
                var same = await db.Approvals.AnyAsync(a => a.DocumentType == "Invoice" && a.DocumentNumber == invKey && a.Approved &&
                    (a.ActedBy ?? a.RoutedTo) == actedBy, ct).ConfigureAwait(false);
                if (same) throw new InvalidOperationException("Invoice approver and payment approver must be different people.");
            }
        }
        if (approved && documentType == "PO")
        {
            var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown PO {documentNumber}.");
            var poLines = await db.PurchaseOrderLines
                .Where(l => l.PoNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in poLines)
            {
                await RequireSourcingAsync(
                    db, po.VendorId, line.ItemId, line.QtyOrdered, po.PrNumber, ct, documentNumber).ConfigureAwait(false);
            }
        }
        if (approved && documentType == "Payment")
        {
            var keys = (await db.PaymentLines
                .Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false))
                .Select(l => new InvoiceKey(l.VendorId, l.SupplierInvoiceNumber))
                .ToList();
            await RequirePayableAsync(db, keys, ct).ConfigureAwait(false);
        }
        var stored = new ApprovalDto(documentType, documentNumber, routed.RoutedTo, actedBy, isDelegate,
            DateTimeOffset.UtcNow, approved, comment, amount, actor.SigningLimit);
        db.Approvals.Add(new ApprovalRow
        {
            RowId = Guid.NewGuid().ToString("N"), DocumentType = documentType, DocumentNumber = documentNumber,
            RoutedTo = routed.RoutedTo, ActedBy = actedBy, ActingAsDelegate = isDelegate,
            Timestamp = Text(stored.Timestamp), Approved = approved, Comment = comment,
            DocumentAmount = amount, LimitChecked = actor.SigningLimit
        });
        await ApplyApprovalOutcomeAsync(db, documentType, documentNumber, approved, ct).ConfigureAwait(false);
        if (approved) await StampApprovedAsync(db, documentType, documentNumber, actedBy, stored.Timestamp, ct).ConfigureAwait(false);
        RecordReplay(db, $"approval:{idempotencyKey}", stored);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return stored;
    }

    private async Task StampApprovedAsync(ErpDbContext db, string documentType, string documentNumber, string actedBy, DateTimeOffset at, CancellationToken ct)
    {
        var stamp = Text(at);
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pr is not null) { pr.ApprovedBy = actedBy; pr.ApprovedAt = stamp; }
                break;
            }
            case "PO":
            {
                var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (po is not null) { po.ApprovedBy = actedBy; po.ApprovedAt = stamp; }
                break;
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) break;
                var inv = await db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false);
                if (inv is not null) { inv.ApprovedBy = actedBy; inv.ApprovedAt = stamp; }
                break;
            }
            case "Payment":
            {
                var pay = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pay is not null) { pay.ApprovedBy = actedBy; pay.ApprovedAt = stamp; }
                break;
            }
        }
    }

    private async Task<decimal> ResolveDocumentAmountAsync(ErpDbContext db, string documentType, string documentNumber, CancellationToken ct)
    {
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                return pr?.EstimatedAmount ?? 0m;
            }
            case "PO":
            {
                var lines = await db.PurchaseOrderLines.Where(l => l.PoNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.LineAmount);
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) return 0m;
                var lines = await db.VendorInvoiceLines
                    .Where(l => l.VendorId == parts[0] && l.SupplierInvoiceNumber == parts[1]).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.Quantity * l.UnitPrice);
            }
            case "Payment":
            {
                var lines = await db.PaymentLines.Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.Amount);
            }
            default: return 0m;
        }
    }

    private async Task ApplyApprovalOutcomeAsync(ErpDbContext db, string documentType, string documentNumber, bool approved, CancellationToken ct)
    {
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pr is not null) pr.Status = (approved ? PrStatus.Approved : PrStatus.Rejected).ToString();
                break;
            }
            case "PO":
            {
                var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (po is not null) po.ApprovalStatus = (approved ? PoApprovalStatus.Approved : PoApprovalStatus.Draft).ToString();
                break;
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) break;
                var inv = await db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false);
                if (inv is not null) inv.Status = (approved ? InvoiceStatus.Approved : InvoiceStatus.Received).ToString();
                break;
            }
            case "Payment":
            {
                var pay = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pay is not null) pay.Status = (approved ? PaymentStatus.Approved : PaymentStatus.Draft).ToString();
                break;
            }
        }
    }

    public async Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, IReadOnlyList<PoOrderLine>? lines = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<PurchaseOrder>(db, $"po:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var pr = await db.PurchaseRequisitions.FindAsync([prNumber], ct).ConfigureAwait(false);
        if (pr is null || Enum<PrStatus>(pr.Status) is not (PrStatus.Approved or PrStatus.PartiallyOrdered))
            throw new InvalidOperationException($"No valid approved PR {prNumber}: PO refused.");
        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        if (vendor.IsBlocked) throw new InvalidOperationException($"Vendor {vendorId} is blocked: {vendor.BlockReason}.");
        var prLines = await db.PurchaseRequisitionLines
            .Where(l => l.PrNumber == prNumber).OrderBy(l => l.LineNo).ToListAsync(ct).ConfigureAwait(false);
        var order = await ResolveOrderLinesAsync(db, prNumber, prLines, lines, ct).ConfigureAwait(false);
        foreach (var l in order)
        {
            await RequireSourcingAsync(db, vendorId, l.ItemId, l.Quantity, prNumber, ct).ConfigureAwait(false);
        }
        var poNumber = await NextAsync(db, "PO", ct).ConfigureAwait(false);
        var newLines = new List<PurchaseOrderLine>();
        var lineNo = 0;
        foreach (var l in order)
        {
            lineNo++;
            var price = await db.VendorPrices.FindAsync([vendorId, l.ItemId], ct).ConfigureAwait(false);
            if (price is null) throw new InvalidOperationException($"No price for vendor {vendorId} item {l.ItemId}: PO refused.");
            var lineRef = $"PO-L{lineNo}";
            db.PurchaseOrderLines.Add(new PoLineRow
            {
                PoNumber = poNumber, PoLineRef = lineRef, LineNumber = lineNo, ItemId = l.ItemId,
                QtyOrdered = l.Quantity, QtyReceived = 0, QtyInvoiced = 0, QtyQuarantined = 0, QtyRejected = 0,
                UnitPrice = price.UnitPrice, LineAmount = l.Quantity * price.UnitPrice
            });
            newLines.Add(new PurchaseOrderLine(lineRef, lineNo, l.ItemId, l.Quantity, 0, 0, 0, 0, price.UnitPrice, l.Quantity * price.UnitPrice));
            var last = await db.LastPrices.FindAsync([l.ItemId], ct).ConfigureAwait(false);
            if (last is null) db.LastPrices.Add(new LastPriceRow { ItemId = l.ItemId, UnitPrice = price.UnitPrice });
            else last.UnitPrice = price.UnitPrice;
        }
        var total = newLines.Sum(l => l.LineAmount);
        var threshold = await GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        var approval = total < threshold ? PoApprovalStatus.Approved : PoApprovalStatus.Draft;
        var poCreatedAt = DateTimeOffset.UtcNow;
        string? poApprovedBy = null;
        DateTimeOffset? poApprovedAt = null;
        if (approval == PoApprovalStatus.Approved)
        {
            poApprovedBy = ErpApprovalActors.AutoApprovePolicy;
            poApprovedAt = poCreatedAt;
            db.Approvals.Add(new ApprovalRow
            {
                RowId = Guid.NewGuid().ToString("N"), DocumentType = "PO", DocumentNumber = poNumber,
                RoutedTo = string.Empty, ActedBy = poApprovedBy, ActingAsDelegate = false,
                Timestamp = Text(poCreatedAt), Approved = true,
                Comment = $"Auto-approved: {total} {pr.Currency} under the {threshold} threshold.",
                DocumentAmount = total, LimitChecked = threshold
            });
        }
        db.PurchaseOrders.Add(new PoRow
        {
            PoNumber = poNumber, PrNumber = prNumber, VendorId = vendorId, PaymentTermsId = vendor.PaymentTermsId,
            ApprovalStatus = approval.ToString(), DocStatus = PoDocStatus.Open.ToString(), Currency = pr.Currency,
            Source = source, CreatedBy = createdBy, CreatedAt = Text(poCreatedAt), IdempotencyKey = idempotencyKey,
            ApprovedBy = poApprovedBy, ApprovedAt = poApprovedAt is null ? null : Text(poApprovedAt.Value)
        });
        db.Requesters.Add(new RequesterRow { DocumentType = "PO", DocumentNumber = poNumber, Requester = pr.Requester });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        pr.Status = await PrFullyOrderedAsync(db, prNumber, prLines, ct).ConfigureAwait(false)
            ? PrStatus.Closed.ToString()
            : PrStatus.PartiallyOrdered.ToString();
        var po = new PurchaseOrder(poNumber, prNumber, vendorId, vendor.PaymentTermsId, approval,
            PoDocStatus.Open, pr.Currency, source, createdBy, idempotencyKey, newLines, CreatedAt: poCreatedAt, ApprovedBy: poApprovedBy, ApprovedAt: poApprovedAt);
        RecordReplay(db, $"po:{idempotencyKey}", po);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return po;
    }

    /// <summary>Resolves what a PO actually orders: the whole request when no
    /// lines are named, else the named quantities. Every line must name a
    /// requested item with a positive quantity, and no item may exceed what
    /// the request still has left across all its non-cancelled POs.</summary>
    private static async Task<IReadOnlyList<PoOrderLine>> ResolveOrderLinesAsync(
        ErpDbContext db, string prNumber, IReadOnlyList<PrLineRow> prLines,
        IReadOnlyList<PoOrderLine>? lines, CancellationToken ct)
    {
        var wanted = lines ?? prLines.Select(l => new PoOrderLine(l.ItemId, l.Quantity)).ToList();
        if (wanted.Count == 0)
        {
            throw new InvalidOperationException($"PR {prNumber} names nothing to order: PO refused.");
        }

        var requested = prLines
            .GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var now = wanted
            .GroupBy(l => l.ItemId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        foreach (var (itemId, qty) in now)
        {
            if (qty <= 0)
            {
                throw new InvalidOperationException(
                    $"PO for PR {prNumber} must order a positive quantity of {itemId}: PO refused.");
            }

            if (!requested.TryGetValue(itemId, out var ask))
            {
                throw new InvalidOperationException(
                    $"PR {prNumber} does not request {itemId}: PO refused.");
            }

            var ordered = await OrderedForPrAsync(db, prNumber, itemId, ct).ConfigureAwait(false);
            if (ordered + qty > ask)
            {
                throw new InvalidOperationException(
                    $"PR {prNumber} requests {DisplayWords.Qty(ask)} of {itemId} with " +
                    $"{DisplayWords.Qty(ask - ordered)} left: ordering {DisplayWords.Qty(qty)} is refused.");
            }
        }

        return wanted;
    }

    /// <summary>Quantity already ordered for one request line across the
    /// request's non-cancelled POs. Cancelled POs consume nothing; received
    /// ones still count, since reorders go on a new request.</summary>
    private static async Task<decimal> OrderedForPrAsync(
        ErpDbContext db, string prNumber, string itemId, CancellationToken ct)
    {
        var cancelled = nameof(PoDocStatus.Cancelled);
        var poNumbers = await db.PurchaseOrders
            .Where(p => p.PrNumber == prNumber && p.DocStatus != cancelled)
            .Select(p => p.PoNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        // Client-side sum: SQLite cannot aggregate decimals server-side.
        var ordered = await db.PurchaseOrderLines
            .Where(l => poNumbers.Contains(l.PoNumber) && l.ItemId == itemId)
            .Select(l => l.QtyOrdered)
            .ToListAsync(ct).ConfigureAwait(false);
        return ordered.Sum();
    }

    private static async Task<bool> PrFullyOrderedAsync(
        ErpDbContext db, string prNumber, IReadOnlyList<PrLineRow> prLines, CancellationToken ct)
    {
        foreach (var group in prLines.GroupBy(l => l.ItemId))
        {
            var ordered = await OrderedForPrAsync(db, prNumber, group.Key, ct).ConfigureAwait(false);
            if (ordered < group.Sum(l => l.Quantity))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorConfirmation>(db, $"confirm:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (Enum<PoApprovalStatus>(po.ApprovalStatus) != PoApprovalStatus.Approved) throw new InvalidOperationException($"PO {poNumber} must be approved before confirmation.");
        var poLines = await db.PurchaseOrderLines.Where(l => l.PoNumber == poNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in lines)
        {
            var poLine = poLines.FirstOrDefault(x => x.PoLineRef == l.PoLineRef)
                ?? throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
            if (l.ConfirmedQty > poLine.QtyOrdered) throw new InvalidOperationException($"Confirmed {l.ConfirmedQty} exceeds ordered {poLine.QtyOrdered}.");
        }
        db.VendorConfirmations.Add(new ConfirmationRow
        {
            PoNumber = poNumber, ConfirmDate = Text(confirmDate), Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            db.VendorConfirmationLines.Add(new ConfirmationLineRow
            {
                PoNumber = poNumber, PoLineRef = l.PoLineRef, ConfirmedQty = l.ConfirmedQty, BackorderNote = l.BackorderNote
            });
        }
        po.ApprovalStatus = PoApprovalStatus.Confirmed.ToString();
        var conf = new VendorConfirmation(poNumber, confirmDate, source, createdBy, idempotencyKey, lines.ToList());
        RecordReplay(db, $"confirm:{idempotencyKey}", conf);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return conf;
    }

    public async Task<AsnDto> CreateAsnAsync(string? asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<AsnDto>(db, $"asn:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (cartons.Count == 0) throw new InvalidOperationException("ASN needs at least one carton.");
        string number;
        if (string.IsNullOrWhiteSpace(asnNumber))
        {
            number = await NextAsync(db, "ASN", ct).ConfigureAwait(false);
        }
        else
        {
            number = asnNumber;
            if (await db.Asns.FindAsync([number], ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException($"Duplicate ASN {number}.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cartons)
        {
            if (!seen.Add(c.CartonId))
                throw new InvalidOperationException($"Duplicate carton {c.CartonId} on ASN {number}.");
            var dup = await db.AsnCartons.FindAsync([number, c.CartonId], ct).ConfigureAwait(false);
            if (dup is not null) throw new InvalidOperationException($"Duplicate carton {c.CartonId} on ASN {number}.");
        }
        db.Asns.Add(new AsnRow
        {
            AsnNumber = number, PoNumber = poNumber, Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var c in cartons)
        {
            db.AsnCartons.Add(new AsnCartonRow
            {
                CartonId = c.CartonId, AsnNumber = number, ItemId = c.ItemId, UnitsPerCarton = c.UnitsPerCarton, LotRef = c.LotRef
            });
        }
        var asn = new AsnDto(number, poNumber, source, createdBy, idempotencyKey, cartons.ToList());
        RecordReplay(db, $"asn:{idempotencyKey}", asn);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return asn;
    }

    public async Task<SalesOrder> CreateSalesOrderAsync(string currency, IReadOnlyList<SalesOrderLine> lines, string source, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<SalesOrder>(db, $"so:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("Sales order currency is required.");
        if (lines.Count == 0) throw new InvalidOperationException("Sales order needs at least one line.");
        foreach (var l in lines) if (l.Quantity <= 0) throw new InvalidOperationException($"Sales order line {l.ItemId} quantity must be positive.");
        string soNumber;
        do { soNumber = await NextAsync(db, "SO", ct).ConfigureAwait(false); }
        while (await db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false) is not null);
        db.SalesOrders.Add(new SalesOrderRow
        {
            SoNumber = soNumber, Status = SalesOrderStatus.Created.ToString(), Currency = currency
        });
        var lineNo = 0;
        foreach (var l in lines)
        {
            lineNo++;
            db.SalesOrderLines.Add(new SalesOrderLineRow
            {
                SoNumber = soNumber, LineNo = lineNo, ItemId = l.ItemId, Quantity = l.Quantity
            });
        }
        var so = new SalesOrder(soNumber, SalesOrderStatus.Created, currency, lines.ToList());
        RecordReplay(db, $"so:{idempotencyKey}", so);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return so;
    }

    public async Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"sostatus:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var so = await db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown sales order {soNumber}.");
        ThrowIfTerminal(soNumber, Enum<SalesOrderStatus>(so.Status) is SalesOrderStatus.Closed or SalesOrderStatus.Cancelled);
        so.Status = status.ToString();
        RecordReplay(db, $"sostatus:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"block:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        vendor.IsBlocked = true;
        vendor.BlockReason = reason;
        vendor.BlockedBy = blockedBy;
        vendor.BlockedAt = Text(DateTimeOffset.UtcNow);
        RecordReplay(db, $"block:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"unblock:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        vendor.IsBlocked = false;
        vendor.BlockReason = null;
        vendor.BlockedBy = null;
        vendor.BlockedAt = null;
        RecordReplay(db, $"unblock:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"note:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.PurchaseOrderNotes.Add(new PoNoteRow
        {
            RowId = Guid.NewGuid().ToString("N"), PoNumber = poNumber,
            Note = $"{createdBy}: {note}", CreatedBy = createdBy, CreatedAt = Text(DateTimeOffset.UtcNow)
        });
        RecordReplay(db, $"note:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"closepo:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        ThrowIfTerminal(poNumber, Enum<PoDocStatus>(po.DocStatus) is PoDocStatus.Closed or PoDocStatus.Cancelled);
        po.DocStatus = PoDocStatus.Closed.ToString();
        RecordReplay(db, $"closepo:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<ShipmentReceipt>(db, $"receipt:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (Enum<PoApprovalStatus>(po.ApprovalStatus) != PoApprovalStatus.Confirmed) throw new InvalidOperationException($"PO {poNumber} is not confirmed for receipt.");
        ThrowIfTerminal(poNumber, Enum<PoDocStatus>(po.DocStatus) is PoDocStatus.Closed or PoDocStatus.Cancelled);
        if (string.IsNullOrWhiteSpace(supplierPackingSlipNumber)) throw new InvalidOperationException("Supplier packing-slip number is required.");
        var poLines = await db.PurchaseOrderLines.Where(l => l.PoNumber == poNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in lines)
        {
            if (!poLines.Any(x => x.PoLineRef == l.PoLineRef)) throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
            if (l.QtyReceived < 0 || l.QtyQuarantined < 0 || l.QtyRejected < 0) throw new InvalidOperationException("Receipt quantities cannot be negative.");
            if (l.QtyQuarantined + l.QtyRejected > l.QtyReceived) throw new InvalidOperationException($"Quarantined plus rejected exceeds received on {l.PoLineRef}.");
            if (string.IsNullOrWhiteSpace(l.LotRef)) throw new InvalidOperationException("Lot reference is required.");
        }
        var receiptNumber = await NextAsync(db, "RCT", ct).ConfigureAwait(false);
        var businessDate = postingDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var receiptCreatedAt = DateTimeOffset.UtcNow;
        var receiptPostedAt = BusinessStamp(postingDate);
        db.ShipmentReceipts.Add(new ReceiptRow
        {
            ReceiptNumber = receiptNumber, PoNumber = poNumber, SupplierPackingSlipNumber = supplierPackingSlipNumber,
            Status = ReceiptStatus.Posted.ToString(), Source = source, CreatedBy = createdBy,
            CreatedAt = Text(receiptCreatedAt), PostedBy = createdBy, PostedAt = Text(receiptPostedAt),
            PostingDate = Text(businessDate), IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            db.ShipmentReceiptLines.Add(new ReceiptLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), ReceiptNumber = receiptNumber, PoLineRef = l.PoLineRef,
                QtyReceived = l.QtyReceived, QtyQuarantined = l.QtyQuarantined, QtyRejected = l.QtyRejected,
                LotRef = l.LotRef, ConditionNote = l.ConditionNote
            });
            var poLine = poLines.First(x => x.PoLineRef == l.PoLineRef);
            var lot = await db.LotMap.FindAsync([l.LotRef], ct).ConfigureAwait(false);
            if (lot is null) db.LotMap.Add(new LotMapRow { LotRef = l.LotRef, PoNumber = poNumber, PoLineRef = l.PoLineRef, ItemId = poLine.ItemId });
            AddInventory(db, poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Available), l.QtyReceived - l.QtyQuarantined - l.QtyRejected);
            if (l.QtyQuarantined > 0) AddInventory(db, poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Quarantined), l.QtyQuarantined);
            if (l.QtyRejected > 0) AddInventory(db, poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Rejected), l.QtyRejected);
        }
        foreach (var poLine in poLines)
        {
            var got = lines.Where(r => r.PoLineRef == poLine.PoLineRef).Sum(r => r.QtyReceived);
            var quar = lines.Where(r => r.PoLineRef == poLine.PoLineRef).Sum(r => r.QtyQuarantined);
            var rej = lines.Where(r => r.PoLineRef == poLine.PoLineRef).Sum(r => r.QtyRejected);
            poLine.QtyReceived += got;
            poLine.QtyQuarantined += quar;
            poLine.QtyRejected += rej;
        }
        po.DocStatus = DerivePoDoc(Enum<PoDocStatus>(po.DocStatus), poLines);
        var receipt = new ShipmentReceipt(receiptNumber, poNumber, supplierPackingSlipNumber, ReceiptStatus.Posted, source, createdBy, idempotencyKey, lines.ToList(),
            CreatedAt: receiptCreatedAt, PostedBy: createdBy, PostedAt: receiptPostedAt, PostingDate: businessDate);
        RecordReplay(db, $"receipt:{idempotencyKey}", receipt);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }

    private void AddInventory(ErpDbContext db, string itemId, string receiptRef, string lotRef, string status, decimal qty)
    {
        if (qty <= 0) return;
        db.InventoryTransactions.Add(new InventoryRow
        {
            RowId = Guid.NewGuid().ToString("N"), ItemId = itemId, ReceiptRef = receiptRef,
            LotRef = lotRef, Status = status, Quantity = qty
        });
    }

    public async Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"quar:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var hold = await db.Holds.FindAsync([holdId], ct).ConfigureAwait(false);
        if (hold is null) throw new InvalidOperationException($"Unknown hold {holdId}.");
        var map = await db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var available = await db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Available)).ToListAsync(ct).ConfigureAwait(false);
        var qty = available.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (qty <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing available to quarantine.");
        db.InventoryTransactions.RemoveRange(available.Where(t => t.ItemId == map.ItemId));
        AddInventory(db, map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Quarantined), qty);
        var poLine = await db.PurchaseOrderLines.FindAsync([map.PoNumber, map.PoLineRef], ct).ConfigureAwait(false);
        if (poLine is not null) poLine.QtyQuarantined += qty;
        RecordReplay(db, $"quar:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"releaselot:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var map = await db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var quar = await db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Quarantined)).ToListAsync(ct).ConfigureAwait(false);
        var qty = quar.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (qty <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing quarantined to release.");
        db.InventoryTransactions.RemoveRange(quar.Where(t => t.ItemId == map.ItemId));
        AddInventory(db, map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Available), qty);
        var poLine = await db.PurchaseOrderLines.FindAsync([map.PoNumber, map.PoLineRef], ct).ConfigureAwait(false);
        if (poLine is not null) poLine.QtyQuarantined = Math.Max(0, poLine.QtyQuarantined - qty);
        RecordReplay(db, $"releaselot:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"consume:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var map = await db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var so = await db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false);
        if (so is null) throw new InvalidOperationException($"Unknown sales order {soNumber}.");
        var available = await db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Available)).ToListAsync(ct).ConfigureAwait(false);
        var qty = available.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (quantity <= 0 || quantity > qty) throw new InvalidOperationException($"Cannot consume {quantity} from lot {lotRef} (available {qty}).");
        db.InventoryTransactions.RemoveRange(available.Where(t => t.ItemId == map.ItemId));
        if (qty - quantity > 0) AddInventory(db, map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Available), qty - quantity);
        AddInventory(db, map.ItemId, soNumber, lotRef, nameof(InventoryStatus.Consumed), quantity);
        RecordReplay(db, $"consume:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default, string? asnNumber = null)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<HoldDto>(db, $"hold:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var holdId = await NextAsync(db, "HLD", ct).ConfigureAwait(false);
        var setAt = DateTimeOffset.UtcNow;
        db.Holds.Add(new HoldRow
        {
            HoldId = holdId, ReasonCode = reasonCode, Note = note, RuleId = ruleId, CaseId = caseId,
            SetBy = setBy, SetAt = Text(setAt), Status = HoldStatus.Set.ToString(),
            DocumentType = documentType, DocumentNumber = documentNumber,
            PoLineRef = poLineRef, LotRef = lotRef, CartonId = cartonId, AsnNumber = asnNumber
        });
        var hold = new HoldDto(holdId, reasonCode, note, ruleId, caseId, setBy, setAt,
            HoldStatus.Set, documentType, documentNumber, poLineRef, lotRef, cartonId, asnNumber);
        RecordReplay(db, $"hold:{idempotencyKey}", hold);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return hold;
    }

    public async Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"releasehold:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var hold = await db.Holds.FindAsync([holdId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown hold {holdId}.");
        if (Enum<HoldStatus>(hold.Status) == HoldStatus.Released) throw new InvalidOperationException($"Hold {holdId} is already released.");
        hold.Status = HoldStatus.Released.ToString();
        db.HoldReleases.Add(new HoldReleaseRow
        {
            RowId = Guid.NewGuid().ToString("N"), HoldId = holdId, ReleasedBy = releasedBy,
            ReleasedAt = Text(DateTimeOffset.UtcNow), ApprovalRef = approvalRef
        });
        RecordReplay(db, $"releasehold:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, DateOnly? invoiceDate = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorInvoice>(db, $"invoice:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber))
            throw new InvalidOperationException("Supplier invoice number is required.");
        if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(poNumber))
            throw new InvalidOperationException("Vendor or PO number missing; goods receipt required before pay.");
        var vendorRow = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        var po = await db.PurchaseOrders.FindAsync([poNumber!], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {poNumber}.");
        var dup = await db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false);
        if (dup is not null) throw new InvalidOperationException($"Duplicate invoice {supplierInvoiceNumber} for vendor {vendorId}.");
        if (lines.Count == 0) throw new InvalidOperationException("Invoice needs at least one line.");
        var terms = await db.PaymentTerms.FindAsync([vendorRow.PaymentTermsId], ct).ConfigureAwait(false);
        if (terms is null) throw new InvalidOperationException($"No payment terms for vendor {vendorId}.");
        var invoicedOn = invoiceDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var due = invoicedOn.AddDays(terms.NetDays);
        var invoiceCreatedAt = DateTimeOffset.UtcNow;
        db.VendorInvoices.Add(new InvoiceRow
        {
            VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber, PoNumber = poNumber,
            Status = InvoiceStatus.Received.ToString(), Voucher = null, Currency = po.Currency,
            Source = source, CreatedBy = createdBy, CreatedAt = Text(invoiceCreatedAt),
            IdempotencyKey = idempotencyKey, PostedAt = null, DueDate = Text(due)
        });
        var stored = new List<VendorInvoiceLine>();
        foreach (var l in lines)
        {
            db.VendorInvoiceLines.Add(new InvoiceLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = l.PoLineRef, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                Disposition = LineDisposition.Held.ToString(), HoldReason = "Awaiting match."
            });
            stored.Add(new VendorInvoiceLine(l.PoLineRef, l.Quantity, l.UnitPrice, LineDisposition.Held, "Awaiting match."));
        }
        db.Requesters.Add(new RequesterRow
        {
            DocumentType = "Invoice", DocumentNumber = InvoiceDoc(vendorId, supplierInvoiceNumber), Requester = createdBy
        });
        var inv = new VendorInvoice(supplierInvoiceNumber, vendorId, poNumber, InvoiceStatus.Received, null,
            po.Currency, source, createdBy, idempotencyKey, stored, [],
            CreatedAt: invoiceCreatedAt, DueDate: due);
        RecordReplay(db, $"invoice:{idempotencyKey}", inv);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return inv;
    }

    public async Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"match:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        var po = inv.PoNumber is null ? null : await db.PurchaseOrders.FindAsync([inv.PoNumber], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {inv.PoNumber}.");
        var poLines = await db.PurchaseOrderLines.Where(l => l.PoNumber == po.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        var dockPendingByLine = await DockPendingQtyByLineAsync(
            db, po.PoNumber, [new InvoiceKey(vendorId, supplierInvoiceNumber)], ct).ConfigureAwait(false);
        var current = await db.VendorInvoiceLines
            .Where(l => l.VendorId == vendorId && l.SupplierInvoiceNumber == supplierInvoiceNumber)
            .OrderBy(l => l.RowId).ToListAsync(ct).ConfigureAwait(false);
        var split = new List<InvoiceLineRow>();
        foreach (var line in current)
        {
            var result = results.FirstOrDefault(r => r.PoLineRef == line.PoLineRef);
            var poLine = poLines.FirstOrDefault(l => l.PoLineRef == line.PoLineRef);
            if (poLine is null || result is null || !result.Matched)
            {
                split.Add(new InvoiceLineRow
                {
                    RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                    PoLineRef = line.PoLineRef, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                    Disposition = LineDisposition.Held.ToString(), HoldReason = result?.Message ?? "AP-C3: no PO line match."
                });
                continue;
            }
            if (line.UnitPrice < 0) throw new InvalidOperationException("Invoice price cannot be negative.");
            var poPrice = poLine.UnitPrice;
            var priceOk = poPrice == 0 || Math.Abs(line.UnitPrice - poPrice) / poPrice <= PriceTolerance;
            if (!priceOk)
            {
                split.Add(new InvoiceLineRow
                {
                    RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                    PoLineRef = line.PoLineRef, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                    Disposition = LineDisposition.Held.ToString(), HoldReason = $"AP-C2: price {line.UnitPrice} vs PO {poPrice}."
                });
                continue;
            }
            var acceptedSoFar = await PoAcceptedInvoicedAsync(db, po.PoNumber, poLine.PoLineRef, ct).ConfigureAwait(false);
            var available = Math.Max(0, poLine.QtyReceived - poLine.QtyQuarantined - poLine.QtyRejected - acceptedSoFar);
            dockPendingByLine.TryGetValue(poLine.PoLineRef, out var dockPending);
            available = Math.Max(0, available - dockPending);
            var acceptedQty = Math.Min(line.Quantity, available);
            var heldQty = line.Quantity - acceptedQty;
            if (acceptedQty > 0) split.Add(new InvoiceLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = line.PoLineRef, Quantity = acceptedQty, UnitPrice = line.UnitPrice,
                Disposition = LineDisposition.Accepted.ToString(), HoldReason = null
            });
            if (heldQty > 0)
            {
                // The dock check is the first gate in time: while its
                // quantity is still pending, the hold names it. Quarantine
                // and reviews still guard the later release with their own
                // cited refusals.
                var why = dockPending > 0
                    ? "Awaiting dock check"
                    : acceptedQty <= 0 && available <= 0
                        ? $"AP-C3: qty {line.Quantity} exceeds available received {available}."
                        : $"Quarantine hold: {heldQty} of {line.Quantity} held.";
                split.Add(new InvoiceLineRow
                {
                    RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                    PoLineRef = line.PoLineRef, Quantity = heldQty, UnitPrice = line.UnitPrice,
                    Disposition = LineDisposition.Held.ToString(), HoldReason = why
                });
            }
        }
        db.VendorInvoiceLines.RemoveRange(current);
        db.VendorInvoiceLines.AddRange(split);
        foreach (var r in results)
        {
            db.MatchResults.Add(new MatchResultRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = r.PoLineRef, Matched = r.Matched, Message = r.Message
            });
        }
        inv.Status = InvoiceStatus.Matched.ToString();
        RecordReplay(db, $"match:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Dock-pending received quantity per PO line: receipt lots on
    /// ASN-declared cartons the platform has not passed yet, net of their
    /// quarantine and rejections. Empty without a platform context or
    /// without declared cartons.</summary>
    private async Task<IReadOnlyDictionary<string, decimal>> DockPendingQtyByLineAsync(
        ErpDbContext db, string poNumber, IReadOnlyList<InvoiceKey> invoices, CancellationToken ct)
    {
        var empty = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (_policyContext is null)
        {
            return empty;
        }

        var asnNumbers = await db.Asns
            .Where(a => a.PoNumber == poNumber).Select(a => a.AsnNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        var cartonLots = new Dictionary<(string AsnNumber, string CartonId), string>();
        foreach (var asn in asnNumbers)
        {
            var cartons = await db.AsnCartons
                .Where(c => c.AsnNumber == asn).ToListAsync(ct).ConfigureAwait(false);
            foreach (var carton in cartons)
            {
                cartonLots.TryAdd((asn, carton.CartonId), carton.LotRef);
            }
        }

        if (cartonLots.Count == 0)
        {
            return empty;
        }

        var passed = await _policyContext
            .FindDockPassedCartonsAsync(invoices, ct).ConfigureAwait(false);
        var passedSet = passed.Select(p => (p.AsnNumber, p.CartonId)).ToHashSet();
        var pendingLots = cartonLots
            .Where(kv => !passedSet.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (pendingLots.Count == 0)
        {
            return empty;
        }

        var receiptNumbers = await db.ShipmentReceipts
            .Where(r => r.PoNumber == poNumber).Select(r => r.ReceiptNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        var receiptLines = await db.ShipmentReceiptLines
            .Where(l => receiptNumbers.Contains(l.ReceiptNumber) && pendingLots.Contains(l.LotRef))
            .ToListAsync(ct).ConfigureAwait(false);
        // Client-side sum: SQLite cannot aggregate decimals server-side.
        return receiptLines
            .GroupBy(l => l.PoLineRef, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(l => Math.Max(0, l.QtyReceived - l.QtyQuarantined - l.QtyRejected)),
                StringComparer.Ordinal);
    }

    private async Task<decimal> PoAcceptedInvoicedAsync(ErpDbContext db, string poNumber, string poLineRef, CancellationToken ct)
    {
        var numbers = await db.VendorInvoices.Where(i => i.PoNumber == poNumber)
            .Select(i => new { i.VendorId, i.SupplierInvoiceNumber }).ToListAsync(ct).ConfigureAwait(false);
        var total = 0m;
        foreach (var n in numbers)
        {
            // Client-side sum: SQLite cannot aggregate decimals server-side.
            var billed = await db.VendorInvoiceLines
                .Where(l => l.VendorId == n.VendorId && l.SupplierInvoiceNumber == n.SupplierInvoiceNumber &&
                    l.PoLineRef == poLineRef && l.Disposition == nameof(LineDisposition.Accepted))
                .ToListAsync(ct).ConfigureAwait(false);
            total += billed.Sum(l => l.Quantity);
        }
        return total;
    }

    public async Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorInvoice>(db, $"postinv:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        if (Enum<InvoiceStatus>(inv.Status) is InvoiceStatus.Posted or InvoiceStatus.Paid or InvoiceStatus.Closed)
            throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is already posted and immutable.");
        var docKey = InvoiceDoc(vendorId, supplierInvoiceNumber);
        var approved = await db.Approvals.AnyAsync(a => a.DocumentType == "Invoice" && a.DocumentNumber == docKey && a.Approved, ct).ConfigureAwait(false);
        if (!approved) throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} must be approved before posting.");
        inv.Status = InvoiceStatus.Posted.ToString();
        inv.Voucher = await NextAsync(db, "V", ct).ConfigureAwait(false);
        inv.PostedBy = postedBy;
        inv.PostedAt = Text(BusinessStamp(postingDate));
        if (inv.PoNumber is not null)
        {
            var po = await db.PurchaseOrders.FindAsync([inv.PoNumber], ct).ConfigureAwait(false);
            var billed = await db.VendorInvoiceLines
                .Where(l => l.VendorId == vendorId && l.SupplierInvoiceNumber == supplierInvoiceNumber).ToListAsync(ct).ConfigureAwait(false);
            if (po is not null)
            {
                var poLines = await db.PurchaseOrderLines.Where(l => l.PoNumber == po.PoNumber).ToListAsync(ct).ConfigureAwait(false);
                foreach (var poLine in poLines)
                {
                    poLine.QtyInvoiced += billed.Where(x => x.PoLineRef == poLine.PoLineRef).Sum(x => x.Quantity);
                }
                po.DocStatus = DerivePoDoc(Enum<PoDocStatus>(po.DocStatus), poLines);
            }
        }
        var posted = await ReadInvoiceAsync(db, vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false);
        RecordReplay(db, $"postinv:{idempotencyKey}", posted);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return posted!;
    }

    public async Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<string>(db, $"releaseline:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        if (Enum<InvoiceStatus>(inv.Status) is InvoiceStatus.Paid or InvoiceStatus.Closed) throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is terminal.");
        await RequireReleasableAsync(db, [new InvoiceKey(vendorId, supplierInvoiceNumber)], ct).ConfigureAwait(false);
        var held = await db.VendorInvoiceLines
            .Where(l => l.VendorId == vendorId && l.SupplierInvoiceNumber == supplierInvoiceNumber &&
                l.PoLineRef == poLineRef && l.Disposition == nameof(LineDisposition.Held))
            .OrderBy(l => l.RowId).ToListAsync(ct).ConfigureAwait(false);
        if (quantity <= 0 || quantity > held.Sum(l => l.Quantity)) throw new InvalidOperationException($"Cannot release {quantity} (held {held.Sum(l => l.Quantity)}).");
        var remaining = quantity;
        foreach (var l in held)
        {
            if (remaining <= 0) break;
            var take = Math.Min(l.Quantity, remaining);
            remaining -= take;
            if (l.Quantity - take > 0) l.Quantity -= take;
            else db.VendorInvoiceLines.Remove(l);
            db.VendorInvoiceLines.Add(new InvoiceLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = l.PoLineRef, Quantity = take, UnitPrice = l.UnitPrice,
                Disposition = LineDisposition.Accepted.ToString(), HoldReason = null
            });
        }
        db.HeldReleases.Add(new HeldReleaseRow
        {
            RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
            PoLineRef = poLineRef, Quantity = quantity, ReleasedBy = releasedBy, ReleasedAt = Text(DateTimeOffset.UtcNow)
        });
        RecordReplay(db, $"releaseline:{idempotencyKey}", OkPayload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        CreatePaymentDraftAsync(invoices, null, source, createdBy, idempotencyKey, postingDate, ct);

    public async Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<Payment>(db, $"paydraft:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (invoices.Count == 0) throw new InvalidOperationException("Payment needs at least one invoice.");
        // One draft may pay many vendors (a payment run); every line vendor
        // needs verified bank details, and the header snapshots the bank of
        // the alphabetically-first paying line for the posting check.
        var currency = string.Empty;
        var lines = new List<PaymentLine>();
        var due = DateOnly.MinValue;
        var banksByVendor = new Dictionary<string, VendorBankRow>(StringComparer.Ordinal);
        foreach (var key in invoices)
        {
            var inv = await db.VendorInvoices.FindAsync([key.VendorId, key.SupplierInvoiceNumber], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown invoice {key.SupplierInvoiceNumber}.");
            // Fully settled invoices contribute no line: skip them before
            // any per-line validation so a remainder draft over older
            // paid invoices is not refused for their terminal status.
            // Client-side sum: SQLite cannot aggregate decimals server-side.
            var acceptedRows = await db.VendorInvoiceLines
                .Where(l => l.VendorId == inv.VendorId && l.SupplierInvoiceNumber == inv.SupplierInvoiceNumber &&
                    l.Disposition == nameof(LineDisposition.Accepted))
                .ToListAsync(ct).ConfigureAwait(false);
            var accepted = acceptedRows.Sum(l => l.Quantity * l.UnitPrice);
            var paid = (await db.PaidAmounts.FindAsync([inv.VendorId, inv.SupplierInvoiceNumber], ct).ConfigureAwait(false))?.Amount ?? 0m;
            if (accepted - paid <= 0) continue;
            if (Enum<InvoiceStatus>(inv.Status) is not InvoiceStatus.Posted and not InvoiceStatus.PartiallyPaid)
                throw new InvalidOperationException($"Invoice {key.SupplierInvoiceNumber} must be posted before pay.");
            if (string.IsNullOrEmpty(currency)) currency = inv.Currency;
            else if (currency != inv.Currency) throw new InvalidOperationException("Payment lines must share one currency.");
            var lineBank = await db.VendorBanks.FindAsync([inv.VendorId], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No bank details for vendor {inv.VendorId}.");
            if (!lineBank.Verified) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
            var dueDate = Day(inv.DueDate) ?? await FallbackDueDateAsync(db, inv, ct).ConfigureAwait(false);
            if (payUpToDate.HasValue && dueDate > payUpToDate.Value) continue;
            if (due > dueDate || due == DateOnly.MinValue) due = dueDate;
            // Terms set the due date shown on the draft; like D365, the ERP
            // does not refuse early payment (PROJECT.md Done needs same-day pay runs).
            var remaining = accepted - paid;
            if (remaining > 0)
            {
                banksByVendor.TryAdd(inv.VendorId, lineBank);
                lines.Add(new PaymentLine(inv.VendorId, inv.SupplierInvoiceNumber, remaining));
            }
        }
        if (lines.Count == 0) throw new InvalidOperationException("Nothing due: held value excluded, remainder stays open.");
        await RequirePayableAsync(db,
            lines.Select(l => new InvoiceKey(l.VendorId, l.SupplierInvoiceNumber)).ToList(), ct)
            .ConfigureAwait(false);
        var draftId = await NextAsync(db, "PAYDRAFT", ct).ConfigureAwait(false);
        var draftCreatedAt = BusinessStamp(postingDate);
        var firstPaying = lines.OrderBy(l => l.VendorId, StringComparer.Ordinal)
            .ThenBy(l => l.SupplierInvoiceNumber, StringComparer.Ordinal).First();
        var bank = banksByVendor[firstPaying.VendorId];
        db.Payments.Add(new PaymentRow
        {
            PaymentNumber = draftId, Status = PaymentStatus.Draft.ToString(), Currency = currency,
            PayeeAccountRef = bank.AccountRef, PayeeVerified = bank.Verified, DueDate = Text(due),
            Source = source, CreatedBy = createdBy, CreatedAt = Text(draftCreatedAt), IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            db.PaymentLines.Add(new PaymentLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), PaymentNumber = draftId,
                VendorId = l.VendorId, SupplierInvoiceNumber = l.SupplierInvoiceNumber, Amount = l.Amount
            });
        }
        db.Requesters.Add(new RequesterRow { DocumentType = "Payment", DocumentNumber = draftId, Requester = createdBy });
        var draft = new Payment(draftId, PaymentStatus.Draft, currency,
            new VendorBankSnapshot(bank.AccountRef, bank.Verified), due, source, createdBy, idempotencyKey, lines,
            CreatedAt: draftCreatedAt);
        RecordReplay(db, $"paydraft:{idempotencyKey}", draft);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return draft;
    }

    private async Task<DateOnly> FallbackDueDateAsync(ErpDbContext db, InvoiceRow inv, CancellationToken ct)
    {
        var vendorRow = await db.Vendors.FindAsync([inv.VendorId], ct).ConfigureAwait(false);
        var terms = vendorRow is null ? null : await db.PaymentTerms.FindAsync([vendorRow.PaymentTermsId], ct).ConfigureAwait(false);
        if (terms is null) throw new InvalidOperationException($"No payment terms for vendor {inv.VendorId}.");
        var postedAt = inv.PostedAt is null ? DateTimeOffset.UtcNow : DateTime(inv.PostedAt);
        return DateOnly.FromDateTime(postedAt.DateTime).AddDays(terms.NetDays);
    }

    public async Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<Payment>(db, $"paypost:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var draft = await db.Payments.FindAsync([draftPaymentNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown payment draft {draftPaymentNumber}.");
        if (Enum<PaymentStatus>(draft.Status) != PaymentStatus.Approved) throw new InvalidOperationException($"Payment {draftPaymentNumber} must be approved before posting.");
        var draftLines = await db.PaymentLines.Where(l => l.PaymentNumber == draftPaymentNumber).ToListAsync(ct).ConfigureAwait(false);
        await RequirePayableAsync(db,
            draftLines.Select(l => new InvoiceKey(l.VendorId, l.SupplierInvoiceNumber)).ToList(), ct)
            .ConfigureAwait(false);
        var orderedLines = draftLines.OrderBy(l => l.VendorId, StringComparer.Ordinal)
            .ThenBy(l => l.SupplierInvoiceNumber, StringComparer.Ordinal).ToList();
        VendorBankRow? firstBank = null;
        foreach (var vendorId in orderedLines.Select(l => l.VendorId).Distinct(StringComparer.Ordinal))
        {
            var lineBank = await db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false);
            if (lineBank is null || !lineBank.Verified) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
            firstBank ??= lineBank;
        }
        var bank = firstBank!;
        if (bank.AccountRef != draft.PayeeAccountRef) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
        var payNumber = await NextAsync(db, "PAY", ct).ConfigureAwait(false);
        var payPostedAt = BusinessStamp(postingDate);
        db.Payments.Add(new PaymentRow
        {
            PaymentNumber = payNumber, Status = PaymentStatus.Posted.ToString(), Currency = draft.Currency,
            PayeeAccountRef = bank.AccountRef, PayeeVerified = bank.Verified, DueDate = draft.DueDate,
            Source = draft.Source, CreatedBy = draft.CreatedBy, CreatedAt = draft.CreatedAt,
            ApprovedBy = draft.ApprovedBy, ApprovedAt = draft.ApprovedAt,
            PostedBy = postedBy, PostedAt = Text(payPostedAt), IdempotencyKey = idempotencyKey
        });
        var postedLines = new List<PaymentLine>();
        foreach (var line in draftLines)
        {
            db.PaymentLines.Add(new PaymentLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), PaymentNumber = payNumber,
                VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber, Amount = line.Amount
            });
            postedLines.Add(new PaymentLine(line.VendorId, line.SupplierInvoiceNumber, line.Amount));
            var paidRow = await db.PaidAmounts.FindAsync([line.VendorId, line.SupplierInvoiceNumber], ct).ConfigureAwait(false);
            if (paidRow is null)
            {
                paidRow = new PaidAmountRow { VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber, Amount = 0 };
                db.PaidAmounts.Add(paidRow);
            }
            paidRow.Amount += line.Amount;
            var inv = await db.VendorInvoices.FindAsync([line.VendorId, line.SupplierInvoiceNumber], ct).ConfigureAwait(false);
            if (inv is null) throw new InvalidOperationException($"Unknown invoice {line.SupplierInvoiceNumber}.");
            var invLines = await db.VendorInvoiceLines
                .Where(l => l.VendorId == line.VendorId && l.SupplierInvoiceNumber == line.SupplierInvoiceNumber).ToListAsync(ct).ConfigureAwait(false);
            var totalInvoiced = invLines.Sum(l => l.Quantity * l.UnitPrice);
            var remaining = totalInvoiced - paidRow.Amount;
            db.Settlements.Add(new SettlementRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber,
                PaymentNumber = payNumber, SettledAmount = line.Amount, RemainingBalance = Math.Max(0, remaining)
            });
            var accepted = invLines.Where(l => l.Disposition == nameof(LineDisposition.Accepted)).Sum(l => l.Quantity * l.UnitPrice);
            var held = invLines.Where(l => l.Disposition == nameof(LineDisposition.Held)).Sum(l => l.Quantity * l.UnitPrice);
            inv.Status = (paidRow.Amount >= accepted && held <= 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid).ToString();
        }
        db.PaymentLines.RemoveRange(draftLines);
        db.Payments.Remove(draft);
        var routed = await db.RoutedTo.FindAsync(["Payment", draftPaymentNumber], ct).ConfigureAwait(false);
        if (routed is not null) db.RoutedTo.Remove(routed);
        var requester = await db.Requesters.FindAsync(["Payment", draftPaymentNumber], ct).ConfigureAwait(false);
        if (requester is not null) db.Requesters.Remove(requester);
        var posted = new Payment(payNumber, PaymentStatus.Posted, draft.Currency,
            new VendorBankSnapshot(bank.AccountRef, bank.Verified), Date(draft.DueDate),
            draft.Source, draft.CreatedBy, idempotencyKey, postedLines,
            CreatedAt: Stamp(draft.CreatedAt), ApprovedBy: draft.ApprovedBy, ApprovedAt: Stamp(draft.ApprovedAt),
            PostedBy: postedBy, PostedAt: payPostedAt);
        RecordReplay(db, $"paypost:{idempotencyKey}", posted);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return posted;
    }

    public async Task<VendorDto> CreateVendorAsync(string? vendorId, string name, string countryOfOrigin, string paymentTermsId, string? email, string source, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorDto>(db, $"vendor:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Vendor name is required.");
        if (string.IsNullOrWhiteSpace(countryOfOrigin)) throw new InvalidOperationException("Vendor country of origin is required.");
        if (string.IsNullOrWhiteSpace(paymentTermsId)) throw new InvalidOperationException("Payment terms are required.");
        _ = await db.PaymentTerms.FindAsync([paymentTermsId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown payment terms {paymentTermsId}.");
        string id;
        if (string.IsNullOrWhiteSpace(vendorId))
        {
            do { id = await NextAsync(db, "VND", ct).ConfigureAwait(false); }
            while (await db.Vendors.FindAsync([id], ct).ConfigureAwait(false) is not null);
        }
        else
        {
            id = vendorId;
            if (await db.Vendors.FindAsync([id], ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException($"Vendor {id} already exists.");
        }
        db.Vendors.Add(new VendorRow
        {
            VendorId = id, Name = name, CountryOfOrigin = countryOfOrigin,
            IsBlocked = false, BlockReason = null, PaymentTermsId = paymentTermsId, Email = email
        });
        var vendor = new VendorDto(id, name, countryOfOrigin, false, null, paymentTermsId, email);
        RecordReplay(db, $"vendor:{idempotencyKey}", vendor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return vendor;
    }

    public async Task<ItemDto> CreateItemAsync(string itemId, string description, string unit, bool isCritical, int unitsPerCarton, string source, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<ItemDto>(db, $"item:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(itemId)) throw new InvalidOperationException("Item id is required.");
        if (await db.Items.FindAsync([itemId], ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException($"Item {itemId} already exists.");
        if (unitsPerCarton <= 0) throw new InvalidOperationException("Units per carton must be above zero.");
        db.Items.Add(new ItemRow
        {
            ItemId = itemId, Description = description, IsCritical = isCritical,
            UnitsPerCarton = unitsPerCarton, Unit = unit
        });
        var item = new ItemDto(itemId, description, isCritical, unitsPerCarton, unit);
        RecordReplay(db, $"item:{idempotencyKey}", item);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return item;
    }

    public async Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorBankDto>(db, $"bankchange:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false);
        if (vendor is null) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        var bank = await db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false);
        if (bank is null) db.VendorBanks.Add(bank = new VendorBankRow { VendorId = vendorId, AccountRef = newAccountRef, Verified = false, VerifiedAt = null });
        else { bank.AccountRef = newAccountRef; bank.Verified = false; bank.VerifiedAt = null; }
        var dto = new VendorBankDto(vendorId, newAccountRef, false, null);
        RecordReplay(db, $"bankchange:{idempotencyKey}", dto);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return dto;
    }

    public async Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var replay = await ReplayAsync<VendorBankDto>(db, $"bankverify:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var bank = await db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No bank details for vendor {vendorId}.");
        var at = DateTimeOffset.UtcNow;
        bank.Verified = true;
        bank.VerifiedAt = Text(at);
        var dto = new VendorBankDto(vendorId, bank.AccountRef, true, at);
        RecordReplay(db, $"bankverify:{idempotencyKey}", dto);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return dto;
    }
}
