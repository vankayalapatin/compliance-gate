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
        var replay = await ReplayAsync<PurchaseRequisition>($"pr:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var currency = (await _db.Config.FindAsync([BaseCurrencyKey], ct).ConfigureAwait(false))?.Value;
        if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("Base currency is not seeded.");
        if (lines.Count == 0) throw new InvalidOperationException("PR needs at least one line.");
        foreach (var l in lines) if (l.Quantity <= 0) throw new InvalidOperationException($"PR line {l.ItemId} quantity must be positive.");
        var estimated = 0m;
        foreach (var l in lines)
        {
            var price = await _db.LastPrices.FindAsync([l.ItemId], ct).ConfigureAwait(false);
            if (price is null) throw new InvalidOperationException($"No last purchase price for item {l.ItemId}: PR cannot be estimated.");
            estimated += l.Quantity * price.UnitPrice;
        }
        var threshold = await GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        var status = estimated < threshold ? PrStatus.Approved : PrStatus.Draft;
        var prNumber = await NextAsync("PR", ct).ConfigureAwait(false);
        _db.PurchaseRequisitions.Add(new PrRow
        {
            PrNumber = prNumber, Status = status.ToString(), Requester = requester, Currency = currency,
            EstimatedAmount = estimated, Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        var lineNo = 0;
        foreach (var l in lines)
        {
            lineNo++;
            _db.PurchaseRequisitionLines.Add(new PrLineRow
            {
                PrNumber = prNumber, LineNo = lineNo, ItemId = l.ItemId, Quantity = l.Quantity, Unit = l.Unit
            });
        }
        _db.Requesters.Add(new RequesterRow { DocumentType = "PR", DocumentNumber = prNumber, Requester = requester });
        var pr = new PurchaseRequisition(prNumber, status, requester, currency, estimated, source, createdBy, idempotencyKey, lines.ToList());
        RecordReplay($"pr:{idempotencyKey}", pr);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return pr;
    }

    public async Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"submit:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        switch (documentType)
        {
            case "PR":
            {
                var pr = await _db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document PR {documentNumber}.");
                var status = Enum<PrStatus>(pr.Status);
                if (status is PrStatus.Submitted) break;
                if (status is not PrStatus.Draft and not PrStatus.Rejected) throw new InvalidOperationException($"PR {documentNumber} cannot be submitted from {pr.Status}.");
                pr.Status = PrStatus.Submitted.ToString();
                break;
            }
            case "PO":
            {
                var po = await _db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false)
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
                var inv = await _db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown invoice {documentNumber}.");
                var status = Enum<InvoiceStatus>(inv.Status);
                if (status is not InvoiceStatus.Received and not InvoiceStatus.Matched) throw new InvalidOperationException($"Invoice {documentNumber} cannot be submitted from {inv.Status}.");
                inv.Status = InvoiceStatus.Matched.ToString();
                break;
            }
            case "Payment":
            {
                var pay = await _db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown document Payment {documentNumber}.");
                var status = Enum<PaymentStatus>(pay.Status);
                if (status is PaymentStatus.Submitted) break;
                if (status is not PaymentStatus.Draft) throw new InvalidOperationException($"Payment {documentNumber} cannot be submitted from {pay.Status}.");
                pay.Status = PaymentStatus.Submitted.ToString();
                break;
            }
            default: throw new InvalidOperationException($"Unknown document {documentType} {documentNumber}.");
        }
        var routed = await _db.RoutedTo.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false);
        if (routed is null) _db.RoutedTo.Add(new RoutedToRow { DocumentType = documentType, DocumentNumber = documentNumber, RoutedTo = routedTo });
        else routed.RoutedTo = routedTo;
        RecordReplay($"submit:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<ApprovalDto>($"approval:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var routed = await _db.RoutedTo.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false);
        if (routed is null) throw new InvalidOperationException($"Document {documentType} {documentNumber} was never submitted for approval.");
        var actor = await _db.Users.FindAsync([actedBy], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown user {actedBy}.");
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var delegations = await _db.Delegations.ToListAsync(ct).ConfigureAwait(false);
        var isDelegate = delegations.Any(d =>
            string.Equals(d.FromUser, routed.RoutedTo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.ToUser, actedBy, StringComparison.OrdinalIgnoreCase) &&
            today >= Date(d.Start) && today <= Date(d.End));
        if (!string.Equals(actedBy, routed.RoutedTo, StringComparison.OrdinalIgnoreCase) && !isDelegate)
            throw new InvalidOperationException($"{actedBy} is not routed to approve {documentType} {documentNumber} and holds no delegation.");
        var requester = (await _db.Requesters.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false))?.Requester;
        if (requester is not null && string.Equals(actedBy, requester, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Requester cannot approve their own request.");
        var amount = await ResolveDocumentAmountAsync(documentType, documentNumber, ct).ConfigureAwait(false);
        if (approved && amount > actor.SigningLimit)
            throw new InvalidOperationException($"{actedBy} cannot approve {amount} above signing limit {actor.SigningLimit}.");
        if (documentType == "Payment" && approved)
        {
            _ = await _db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown payment {documentNumber}.");
            var payLines = await _db.PaymentLines.Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in payLines)
            {
                var invKey = InvoiceDoc(line.VendorId, line.SupplierInvoiceNumber);
                var same = await _db.Approvals.AnyAsync(a => a.DocumentType == "Invoice" && a.DocumentNumber == invKey && a.Approved &&
                    (a.ActedBy ?? a.RoutedTo) == actedBy, ct).ConfigureAwait(false);
                if (same) throw new InvalidOperationException("Invoice approver and payment approver must be different people.");
            }
        }
        var stored = new ApprovalDto(documentType, documentNumber, routed.RoutedTo, actedBy, isDelegate,
            DateTimeOffset.UtcNow, approved, comment, amount, actor.SigningLimit);
        _db.Approvals.Add(new ApprovalRow
        {
            RowId = Guid.NewGuid().ToString("N"), DocumentType = documentType, DocumentNumber = documentNumber,
            RoutedTo = routed.RoutedTo, ActedBy = actedBy, ActingAsDelegate = isDelegate,
            Timestamp = Text(stored.Timestamp), Approved = approved, Comment = comment,
            DocumentAmount = amount, LimitChecked = actor.SigningLimit
        });
        await ApplyApprovalOutcomeAsync(documentType, documentNumber, approved, ct).ConfigureAwait(false);
        RecordReplay($"approval:{idempotencyKey}", stored);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return stored;
    }

    private async Task<decimal> ResolveDocumentAmountAsync(string documentType, string documentNumber, CancellationToken ct)
    {
        switch (documentType)
        {
            case "PR":
            {
                var pr = await _db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                return pr?.EstimatedAmount ?? 0m;
            }
            case "PO":
            {
                var lines = await _db.PurchaseOrderLines.Where(l => l.PoNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.LineAmount);
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) return 0m;
                var lines = await _db.VendorInvoiceLines
                    .Where(l => l.VendorId == parts[0] && l.SupplierInvoiceNumber == parts[1]).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.Quantity * l.UnitPrice);
            }
            case "Payment":
            {
                var lines = await _db.PaymentLines.Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                return lines.Sum(l => l.Amount);
            }
            default: return 0m;
        }
    }

    private async Task ApplyApprovalOutcomeAsync(string documentType, string documentNumber, bool approved, CancellationToken ct)
    {
        switch (documentType)
        {
            case "PR":
            {
                var pr = await _db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pr is not null) pr.Status = (approved ? PrStatus.Approved : PrStatus.Rejected).ToString();
                break;
            }
            case "PO":
            {
                var po = await _db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (po is not null) po.ApprovalStatus = (approved ? PoApprovalStatus.Approved : PoApprovalStatus.Draft).ToString();
                break;
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) break;
                var inv = await _db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false);
                if (inv is not null) inv.Status = (approved ? InvoiceStatus.Approved : InvoiceStatus.Received).ToString();
                break;
            }
            case "Payment":
            {
                var pay = await _db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pay is not null) pay.Status = (approved ? PaymentStatus.Approved : PaymentStatus.Draft).ToString();
                break;
            }
        }
    }

    public async Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<PurchaseOrder>($"po:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var pr = await _db.PurchaseRequisitions.FindAsync([prNumber], ct).ConfigureAwait(false);
        if (pr is null || Enum<PrStatus>(pr.Status) != PrStatus.Approved)
            throw new InvalidOperationException($"No valid approved PR {prNumber}: PO refused.");
        var vendor = await _db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        if (vendor.IsBlocked) throw new InvalidOperationException($"Vendor {vendorId} is blocked: {vendor.BlockReason}.");
        var prLines = await _db.PurchaseRequisitionLines
            .Where(l => l.PrNumber == prNumber).OrderBy(l => l.LineNo).ToListAsync(ct).ConfigureAwait(false);
        var poNumber = await NextAsync("PO", ct).ConfigureAwait(false);
        var newLines = new List<PurchaseOrderLine>();
        var lineNo = 0;
        foreach (var l in prLines)
        {
            lineNo++;
            var price = await _db.VendorPrices.FindAsync([vendorId, l.ItemId], ct).ConfigureAwait(false);
            if (price is null) throw new InvalidOperationException($"No price for vendor {vendorId} item {l.ItemId}: PO refused.");
            var lineRef = $"PO-L{lineNo}";
            _db.PurchaseOrderLines.Add(new PoLineRow
            {
                PoNumber = poNumber, PoLineRef = lineRef, LineNumber = lineNo, ItemId = l.ItemId,
                QtyOrdered = l.Quantity, QtyReceived = 0, QtyInvoiced = 0, QtyQuarantined = 0, QtyRejected = 0,
                UnitPrice = price.UnitPrice, LineAmount = l.Quantity * price.UnitPrice
            });
            newLines.Add(new PurchaseOrderLine(lineRef, lineNo, l.ItemId, l.Quantity, 0, 0, 0, 0, price.UnitPrice, l.Quantity * price.UnitPrice));
            var last = await _db.LastPrices.FindAsync([l.ItemId], ct).ConfigureAwait(false);
            if (last is null) _db.LastPrices.Add(new LastPriceRow { ItemId = l.ItemId, UnitPrice = price.UnitPrice });
            else last.UnitPrice = price.UnitPrice;
        }
        var total = newLines.Sum(l => l.LineAmount);
        var threshold = await GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        var approval = total < threshold ? PoApprovalStatus.Approved : PoApprovalStatus.Draft;
        _db.PurchaseOrders.Add(new PoRow
        {
            PoNumber = poNumber, PrNumber = prNumber, VendorId = vendorId, PaymentTermsId = vendor.PaymentTermsId,
            ApprovalStatus = approval.ToString(), DocStatus = PoDocStatus.Open.ToString(), Currency = pr.Currency,
            Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        _db.Requesters.Add(new RequesterRow { DocumentType = "PO", DocumentNumber = poNumber, Requester = pr.Requester });
        pr.Status = PrStatus.Closed.ToString();
        var po = new PurchaseOrder(poNumber, prNumber, vendorId, vendor.PaymentTermsId, approval,
            PoDocStatus.Open, pr.Currency, source, createdBy, idempotencyKey, newLines);
        RecordReplay($"po:{idempotencyKey}", po);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return po;
    }

    public async Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<VendorConfirmation>($"confirm:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await _db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (Enum<PoApprovalStatus>(po.ApprovalStatus) != PoApprovalStatus.Approved) throw new InvalidOperationException($"PO {poNumber} must be approved before confirmation.");
        var poLines = await _db.PurchaseOrderLines.Where(l => l.PoNumber == poNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in lines)
        {
            var poLine = poLines.FirstOrDefault(x => x.PoLineRef == l.PoLineRef)
                ?? throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
            if (l.ConfirmedQty > poLine.QtyOrdered) throw new InvalidOperationException($"Confirmed {l.ConfirmedQty} exceeds ordered {poLine.QtyOrdered}.");
        }
        _db.VendorConfirmations.Add(new ConfirmationRow
        {
            PoNumber = poNumber, ConfirmDate = Text(confirmDate), Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            _db.VendorConfirmationLines.Add(new ConfirmationLineRow
            {
                PoNumber = poNumber, PoLineRef = l.PoLineRef, ConfirmedQty = l.ConfirmedQty, BackorderNote = l.BackorderNote
            });
        }
        po.ApprovalStatus = PoApprovalStatus.Confirmed.ToString();
        var conf = new VendorConfirmation(poNumber, confirmDate, source, createdBy, idempotencyKey, lines.ToList());
        RecordReplay($"confirm:{idempotencyKey}", conf);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return conf;
    }

    public async Task<AsnDto> CreateAsnAsync(string asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<AsnDto>($"asn:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await _db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (cartons.Count == 0) throw new InvalidOperationException("ASN needs at least one carton.");
        foreach (var c in cartons)
        {
            var dup = await _db.AsnCartons.FindAsync([c.CartonId], ct).ConfigureAwait(false);
            if (dup is not null) throw new InvalidOperationException($"Duplicate carton {c.CartonId}.");
        }
        _db.Asns.Add(new AsnRow
        {
            AsnNumber = asnNumber, PoNumber = poNumber, Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var c in cartons)
        {
            _db.AsnCartons.Add(new AsnCartonRow
            {
                CartonId = c.CartonId, AsnNumber = asnNumber, ItemId = c.ItemId, UnitsPerCarton = c.UnitsPerCarton, LotRef = c.LotRef
            });
        }
        var asn = new AsnDto(asnNumber, poNumber, source, createdBy, idempotencyKey, cartons.ToList());
        RecordReplay($"asn:{idempotencyKey}", asn);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return asn;
    }

    public async Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"sostatus:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var so = await _db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown sales order {soNumber}.");
        ThrowIfTerminal(soNumber, Enum<SalesOrderStatus>(so.Status) is SalesOrderStatus.Closed or SalesOrderStatus.Cancelled);
        so.Status = status.ToString();
        RecordReplay($"sostatus:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"block:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await _db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        vendor.IsBlocked = true;
        vendor.BlockReason = reason;
        RecordReplay($"block:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"unblock:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await _db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        vendor.IsBlocked = false;
        vendor.BlockReason = null;
        RecordReplay($"unblock:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"note:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        _db.PurchaseOrderNotes.Add(new PoNoteRow
        {
            RowId = Guid.NewGuid().ToString("N"), PoNumber = poNumber,
            Note = $"{createdBy}: {note}", CreatedBy = createdBy, CreatedAt = Text(DateTimeOffset.UtcNow)
        });
        RecordReplay($"note:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"closepo:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await _db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        ThrowIfTerminal(poNumber, Enum<PoDocStatus>(po.DocStatus) is PoDocStatus.Closed or PoDocStatus.Cancelled);
        po.DocStatus = PoDocStatus.Closed.ToString();
        RecordReplay($"closepo:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<ShipmentReceipt>($"receipt:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var po = await _db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PO {poNumber}.");
        if (Enum<PoApprovalStatus>(po.ApprovalStatus) != PoApprovalStatus.Confirmed) throw new InvalidOperationException($"PO {poNumber} is not confirmed for receipt.");
        ThrowIfTerminal(poNumber, Enum<PoDocStatus>(po.DocStatus) is PoDocStatus.Closed or PoDocStatus.Cancelled);
        if (string.IsNullOrWhiteSpace(supplierPackingSlipNumber)) throw new InvalidOperationException("Supplier packing-slip number is required.");
        var poLines = await _db.PurchaseOrderLines.Where(l => l.PoNumber == poNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in lines)
        {
            if (!poLines.Any(x => x.PoLineRef == l.PoLineRef)) throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
            if (l.QtyReceived < 0 || l.QtyQuarantined < 0 || l.QtyRejected < 0) throw new InvalidOperationException("Receipt quantities cannot be negative.");
            if (l.QtyQuarantined + l.QtyRejected > l.QtyReceived) throw new InvalidOperationException($"Quarantined plus rejected exceeds received on {l.PoLineRef}.");
            if (string.IsNullOrWhiteSpace(l.LotRef)) throw new InvalidOperationException("Lot reference is required.");
        }
        var receiptNumber = await NextAsync("RCT", ct).ConfigureAwait(false);
        _db.ShipmentReceipts.Add(new ReceiptRow
        {
            ReceiptNumber = receiptNumber, PoNumber = poNumber, SupplierPackingSlipNumber = supplierPackingSlipNumber,
            Status = ReceiptStatus.Posted.ToString(), Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            _db.ShipmentReceiptLines.Add(new ReceiptLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), ReceiptNumber = receiptNumber, PoLineRef = l.PoLineRef,
                QtyReceived = l.QtyReceived, QtyQuarantined = l.QtyQuarantined, QtyRejected = l.QtyRejected,
                LotRef = l.LotRef, ConditionNote = l.ConditionNote
            });
            var poLine = poLines.First(x => x.PoLineRef == l.PoLineRef);
            var lot = await _db.LotMap.FindAsync([l.LotRef], ct).ConfigureAwait(false);
            if (lot is null) _db.LotMap.Add(new LotMapRow { LotRef = l.LotRef, PoNumber = poNumber, PoLineRef = l.PoLineRef, ItemId = poLine.ItemId });
            AddInventory(poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Available), l.QtyReceived - l.QtyQuarantined - l.QtyRejected);
            if (l.QtyQuarantined > 0) AddInventory(poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Quarantined), l.QtyQuarantined);
            if (l.QtyRejected > 0) AddInventory(poLine.ItemId, receiptNumber, l.LotRef, nameof(InventoryStatus.Rejected), l.QtyRejected);
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
        var receipt = new ShipmentReceipt(receiptNumber, poNumber, supplierPackingSlipNumber, ReceiptStatus.Posted, source, createdBy, idempotencyKey, lines.ToList());
        RecordReplay($"receipt:{idempotencyKey}", receipt);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }

    private void AddInventory(string itemId, string receiptRef, string lotRef, string status, decimal qty)
    {
        if (qty <= 0) return;
        _db.InventoryTransactions.Add(new InventoryRow
        {
            RowId = Guid.NewGuid().ToString("N"), ItemId = itemId, ReceiptRef = receiptRef,
            LotRef = lotRef, Status = status, Quantity = qty
        });
    }

    public async Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"quar:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var hold = await _db.Holds.FindAsync([holdId], ct).ConfigureAwait(false);
        if (hold is null) throw new InvalidOperationException($"Unknown hold {holdId}.");
        var map = await _db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var available = await _db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Available)).ToListAsync(ct).ConfigureAwait(false);
        var qty = available.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (qty <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing available to quarantine.");
        _db.InventoryTransactions.RemoveRange(available.Where(t => t.ItemId == map.ItemId));
        AddInventory(map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Quarantined), qty);
        var poLine = await _db.PurchaseOrderLines.FindAsync([map.PoNumber, map.PoLineRef], ct).ConfigureAwait(false);
        if (poLine is not null) poLine.QtyQuarantined += qty;
        RecordReplay($"quar:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"releaselot:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var map = await _db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var quar = await _db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Quarantined)).ToListAsync(ct).ConfigureAwait(false);
        var qty = quar.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (qty <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing quarantined to release.");
        _db.InventoryTransactions.RemoveRange(quar.Where(t => t.ItemId == map.ItemId));
        AddInventory(map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Available), qty);
        var poLine = await _db.PurchaseOrderLines.FindAsync([map.PoNumber, map.PoLineRef], ct).ConfigureAwait(false);
        if (poLine is not null) poLine.QtyQuarantined = Math.Max(0, poLine.QtyQuarantined - qty);
        RecordReplay($"releaselot:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"consume:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var map = await _db.LotMap.FindAsync([lotRef], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown lot {lotRef}.");
        var so = await _db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false);
        if (so is null) throw new InvalidOperationException($"Unknown sales order {soNumber}.");
        var available = await _db.InventoryTransactions
            .Where(t => t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Available)).ToListAsync(ct).ConfigureAwait(false);
        var qty = available.Where(t => t.ItemId == map.ItemId).Sum(t => t.Quantity);
        if (quantity <= 0 || quantity > qty) throw new InvalidOperationException($"Cannot consume {quantity} from lot {lotRef} (available {qty}).");
        _db.InventoryTransactions.RemoveRange(available.Where(t => t.ItemId == map.ItemId));
        if (qty - quantity > 0) AddInventory(map.ItemId, "QUAR", lotRef, nameof(InventoryStatus.Available), qty - quantity);
        AddInventory(map.ItemId, soNumber, lotRef, nameof(InventoryStatus.Consumed), quantity);
        RecordReplay($"consume:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<HoldDto>($"hold:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var holdId = await NextAsync("HLD", ct).ConfigureAwait(false);
        var setAt = DateTimeOffset.UtcNow;
        _db.Holds.Add(new HoldRow
        {
            HoldId = holdId, ReasonCode = reasonCode, Note = note, RuleId = ruleId, CaseId = caseId,
            SetBy = setBy, SetAt = Text(setAt), Status = HoldStatus.Set.ToString(),
            DocumentType = documentType, DocumentNumber = documentNumber,
            PoLineRef = poLineRef, LotRef = lotRef, CartonId = cartonId
        });
        var hold = new HoldDto(holdId, reasonCode, note, ruleId, caseId, setBy, setAt,
            HoldStatus.Set, documentType, documentNumber, poLineRef, lotRef, cartonId);
        RecordReplay($"hold:{idempotencyKey}", hold);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return hold;
    }

    public async Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"releasehold:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var hold = await _db.Holds.FindAsync([holdId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown hold {holdId}.");
        if (Enum<HoldStatus>(hold.Status) == HoldStatus.Released) throw new InvalidOperationException($"Hold {holdId} is already released.");
        hold.Status = HoldStatus.Released.ToString();
        _db.HoldReleases.Add(new HoldReleaseRow
        {
            RowId = Guid.NewGuid().ToString("N"), HoldId = holdId, ReleasedBy = releasedBy,
            ReleasedAt = Text(DateTimeOffset.UtcNow), ApprovalRef = approvalRef
        });
        RecordReplay($"releasehold:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<VendorInvoice>($"invoice:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(poNumber))
            throw new InvalidOperationException("Vendor or PO number missing; goods receipt required before pay.");
        _ = await _db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        var po = await _db.PurchaseOrders.FindAsync([poNumber!], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {poNumber}.");
        var dup = await _db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false);
        if (dup is not null) throw new InvalidOperationException($"Duplicate invoice {supplierInvoiceNumber} for vendor {vendorId}.");
        if (lines.Count == 0) throw new InvalidOperationException("Invoice needs at least one line.");
        _db.VendorInvoices.Add(new InvoiceRow
        {
            VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber, PoNumber = poNumber,
            Status = InvoiceStatus.Received.ToString(), Voucher = null, Currency = po.Currency,
            Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey, PostedAt = null
        });
        var stored = new List<VendorInvoiceLine>();
        foreach (var l in lines)
        {
            _db.VendorInvoiceLines.Add(new InvoiceLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = l.PoLineRef, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                Disposition = LineDisposition.Held.ToString(), HoldReason = "Awaiting match."
            });
            stored.Add(new VendorInvoiceLine(l.PoLineRef, l.Quantity, l.UnitPrice, LineDisposition.Held, "Awaiting match."));
        }
        _db.Requesters.Add(new RequesterRow
        {
            DocumentType = "Invoice", DocumentNumber = InvoiceDoc(vendorId, supplierInvoiceNumber), Requester = createdBy
        });
        var inv = new VendorInvoice(supplierInvoiceNumber, vendorId, poNumber, InvoiceStatus.Received, null,
            po.Currency, source, createdBy, idempotencyKey, stored, []);
        RecordReplay($"invoice:{idempotencyKey}", inv);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return inv;
    }

    public async Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"match:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await _db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        var po = inv.PoNumber is null ? null : await _db.PurchaseOrders.FindAsync([inv.PoNumber], ct).ConfigureAwait(false);
        if (po is null) throw new InvalidOperationException($"Unknown PO {inv.PoNumber}.");
        var poLines = await _db.PurchaseOrderLines.Where(l => l.PoNumber == po.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        var current = await _db.VendorInvoiceLines
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
            var acceptedSoFar = await PoAcceptedInvoicedAsync(po.PoNumber, poLine.PoLineRef, ct).ConfigureAwait(false);
            var available = Math.Max(0, poLine.QtyReceived - poLine.QtyQuarantined - poLine.QtyRejected - acceptedSoFar);
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
                var why = acceptedQty <= 0 && available <= 0
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
        _db.VendorInvoiceLines.RemoveRange(current);
        _db.VendorInvoiceLines.AddRange(split);
        foreach (var r in results)
        {
            _db.MatchResults.Add(new MatchResultRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = r.PoLineRef, Matched = r.Matched, Message = r.Message
            });
        }
        inv.Status = InvoiceStatus.Matched.ToString();
        RecordReplay($"match:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task<decimal> PoAcceptedInvoicedAsync(string poNumber, string poLineRef, CancellationToken ct)
    {
        var numbers = await _db.VendorInvoices.Where(i => i.PoNumber == poNumber)
            .Select(i => new { i.VendorId, i.SupplierInvoiceNumber }).ToListAsync(ct).ConfigureAwait(false);
        var total = 0m;
        foreach (var n in numbers)
        {
            // Client-side sum: SQLite cannot aggregate decimals server-side.
            var billed = await _db.VendorInvoiceLines
                .Where(l => l.VendorId == n.VendorId && l.SupplierInvoiceNumber == n.SupplierInvoiceNumber &&
                    l.PoLineRef == poLineRef && l.Disposition == nameof(LineDisposition.Accepted))
                .ToListAsync(ct).ConfigureAwait(false);
            total += billed.Sum(l => l.Quantity);
        }
        return total;
    }

    public async Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<VendorInvoice>($"postinv:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await _db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        if (Enum<InvoiceStatus>(inv.Status) is InvoiceStatus.Posted or InvoiceStatus.Paid or InvoiceStatus.Closed)
            throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is already posted and immutable.");
        var docKey = InvoiceDoc(vendorId, supplierInvoiceNumber);
        var approved = await _db.Approvals.AnyAsync(a => a.DocumentType == "Invoice" && a.DocumentNumber == docKey && a.Approved, ct).ConfigureAwait(false);
        if (!approved) throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} must be approved before posting.");
        inv.Status = InvoiceStatus.Posted.ToString();
        inv.Voucher = await NextAsync("V", ct).ConfigureAwait(false);
        inv.PostedAt = Text(DateTimeOffset.UtcNow);
        if (inv.PoNumber is not null)
        {
            var po = await _db.PurchaseOrders.FindAsync([inv.PoNumber], ct).ConfigureAwait(false);
            var billed = await _db.VendorInvoiceLines
                .Where(l => l.VendorId == vendorId && l.SupplierInvoiceNumber == supplierInvoiceNumber).ToListAsync(ct).ConfigureAwait(false);
            if (po is not null)
            {
                var poLines = await _db.PurchaseOrderLines.Where(l => l.PoNumber == po.PoNumber).ToListAsync(ct).ConfigureAwait(false);
                foreach (var poLine in poLines)
                {
                    poLine.QtyInvoiced += billed.Where(x => x.PoLineRef == poLine.PoLineRef).Sum(x => x.Quantity);
                }
                po.DocStatus = DerivePoDoc(Enum<PoDocStatus>(po.DocStatus), poLines);
            }
        }
        var posted = await ReadInvoiceAsync(vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false);
        RecordReplay($"postinv:{idempotencyKey}", posted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return posted!;
    }

    public async Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<string>($"releaseline:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inv = await _db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
        if (Enum<InvoiceStatus>(inv.Status) is InvoiceStatus.Paid or InvoiceStatus.Closed) throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is terminal.");
        var held = await _db.VendorInvoiceLines
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
            else _db.VendorInvoiceLines.Remove(l);
            _db.VendorInvoiceLines.Add(new InvoiceLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
                PoLineRef = l.PoLineRef, Quantity = take, UnitPrice = l.UnitPrice,
                Disposition = LineDisposition.Accepted.ToString(), HoldReason = null
            });
        }
        _db.HeldReleases.Add(new HeldReleaseRow
        {
            RowId = Guid.NewGuid().ToString("N"), VendorId = vendorId, SupplierInvoiceNumber = supplierInvoiceNumber,
            PoLineRef = poLineRef, Quantity = quantity, ReleasedBy = releasedBy, ReleasedAt = Text(DateTimeOffset.UtcNow)
        });
        RecordReplay($"releaseline:{idempotencyKey}", OkPayload);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<Payment>($"paydraft:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (invoices.Count == 0) throw new InvalidOperationException("Payment needs at least one invoice.");
        var vendor = invoices[0].VendorId;
        if (invoices.Any(k => k.VendorId != vendor)) throw new InvalidOperationException("One payment pays one vendor.");
        var bank = await _db.VendorBanks.FindAsync([vendor], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No bank details for vendor {vendor}.");
        if (!bank.Verified) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
        var currency = string.Empty;
        var lines = new List<PaymentLine>();
        var due = DateOnly.MinValue;
        foreach (var key in invoices)
        {
            var inv = await _db.VendorInvoices.FindAsync([key.VendorId, key.SupplierInvoiceNumber], ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown invoice {key.SupplierInvoiceNumber}.");
            if (Enum<InvoiceStatus>(inv.Status) is not InvoiceStatus.Posted and not InvoiceStatus.PartiallyPaid)
                throw new InvalidOperationException($"Invoice {key.SupplierInvoiceNumber} must be posted before pay.");
            if (string.IsNullOrEmpty(currency)) currency = inv.Currency;
            else if (currency != inv.Currency) throw new InvalidOperationException("Payment lines must share one currency.");
            var vendorRow = await _db.Vendors.FindAsync([inv.VendorId], ct).ConfigureAwait(false);
            var terms = vendorRow is null ? null : await _db.PaymentTerms.FindAsync([vendorRow.PaymentTermsId], ct).ConfigureAwait(false);
            if (terms is null) throw new InvalidOperationException($"No payment terms for vendor {inv.VendorId}.");
            var postedAt = inv.PostedAt is null ? DateTimeOffset.UtcNow : DateTime(inv.PostedAt);
            var dueDate = DateOnly.FromDateTime(postedAt.DateTime).AddDays(terms.NetDays);
            if (due > dueDate || due == DateOnly.MinValue) due = dueDate;
            if (DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime) < dueDate) continue;
            // Client-side sum: SQLite cannot aggregate decimals server-side.
            var acceptedRows = await _db.VendorInvoiceLines
                .Where(l => l.VendorId == inv.VendorId && l.SupplierInvoiceNumber == inv.SupplierInvoiceNumber &&
                    l.Disposition == nameof(LineDisposition.Accepted))
                .ToListAsync(ct).ConfigureAwait(false);
            var accepted = acceptedRows.Sum(l => l.Quantity * l.UnitPrice);
            var paid = (await _db.PaidAmounts.FindAsync([inv.VendorId, inv.SupplierInvoiceNumber], ct).ConfigureAwait(false))?.Amount ?? 0m;
            var remaining = accepted - paid;
            if (remaining > 0) lines.Add(new PaymentLine(inv.VendorId, inv.SupplierInvoiceNumber, remaining));
        }
        if (lines.Count == 0) throw new InvalidOperationException("Nothing due: held value excluded, remainder stays open.");
        var draftId = await NextAsync("PAYDRAFT", ct).ConfigureAwait(false);
        _db.Payments.Add(new PaymentRow
        {
            PaymentNumber = draftId, Status = PaymentStatus.Draft.ToString(), Currency = currency,
            PayeeAccountRef = bank.AccountRef, PayeeVerified = bank.Verified, DueDate = Text(due),
            Source = source, CreatedBy = createdBy, IdempotencyKey = idempotencyKey
        });
        foreach (var l in lines)
        {
            _db.PaymentLines.Add(new PaymentLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), PaymentNumber = draftId,
                VendorId = l.VendorId, SupplierInvoiceNumber = l.SupplierInvoiceNumber, Amount = l.Amount
            });
        }
        _db.Requesters.Add(new RequesterRow { DocumentType = "Payment", DocumentNumber = draftId, Requester = createdBy });
        var draft = new Payment(draftId, PaymentStatus.Draft, currency,
            new VendorBankSnapshot(bank.AccountRef, bank.Verified), due, source, createdBy, idempotencyKey, lines);
        RecordReplay($"paydraft:{idempotencyKey}", draft);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return draft;
    }

    public async Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<Payment>($"paypost:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var draft = await _db.Payments.FindAsync([draftPaymentNumber], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown payment draft {draftPaymentNumber}.");
        if (Enum<PaymentStatus>(draft.Status) != PaymentStatus.Approved) throw new InvalidOperationException($"Payment {draftPaymentNumber} must be approved before posting.");
        var draftLines = await _db.PaymentLines.Where(l => l.PaymentNumber == draftPaymentNumber).OrderBy(l => l.RowId).ToListAsync(ct).ConfigureAwait(false);
        var bank = await _db.VendorBanks.FindAsync([draftLines[0].VendorId], ct).ConfigureAwait(false);
        if (bank is null || !bank.Verified) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
        if (bank.AccountRef != draft.PayeeAccountRef) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
        var payNumber = await NextAsync("PAY", ct).ConfigureAwait(false);
        _db.Payments.Add(new PaymentRow
        {
            PaymentNumber = payNumber, Status = PaymentStatus.Posted.ToString(), Currency = draft.Currency,
            PayeeAccountRef = bank.AccountRef, PayeeVerified = bank.Verified, DueDate = draft.DueDate,
            Source = draft.Source, CreatedBy = draft.CreatedBy, IdempotencyKey = idempotencyKey
        });
        var postedLines = new List<PaymentLine>();
        foreach (var line in draftLines)
        {
            _db.PaymentLines.Add(new PaymentLineRow
            {
                RowId = Guid.NewGuid().ToString("N"), PaymentNumber = payNumber,
                VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber, Amount = line.Amount
            });
            postedLines.Add(new PaymentLine(line.VendorId, line.SupplierInvoiceNumber, line.Amount));
            var paidRow = await _db.PaidAmounts.FindAsync([line.VendorId, line.SupplierInvoiceNumber], ct).ConfigureAwait(false);
            if (paidRow is null)
            {
                paidRow = new PaidAmountRow { VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber, Amount = 0 };
                _db.PaidAmounts.Add(paidRow);
            }
            paidRow.Amount += line.Amount;
            var inv = await _db.VendorInvoices.FindAsync([line.VendorId, line.SupplierInvoiceNumber], ct).ConfigureAwait(false);
            if (inv is null) throw new InvalidOperationException($"Unknown invoice {line.SupplierInvoiceNumber}.");
            var invLines = await _db.VendorInvoiceLines
                .Where(l => l.VendorId == line.VendorId && l.SupplierInvoiceNumber == line.SupplierInvoiceNumber).ToListAsync(ct).ConfigureAwait(false);
            var totalInvoiced = invLines.Sum(l => l.Quantity * l.UnitPrice);
            var remaining = totalInvoiced - paidRow.Amount;
            _db.Settlements.Add(new SettlementRow
            {
                RowId = Guid.NewGuid().ToString("N"), VendorId = line.VendorId, SupplierInvoiceNumber = line.SupplierInvoiceNumber,
                PaymentNumber = payNumber, SettledAmount = line.Amount, RemainingBalance = Math.Max(0, remaining)
            });
            var accepted = invLines.Where(l => l.Disposition == nameof(LineDisposition.Accepted)).Sum(l => l.Quantity * l.UnitPrice);
            var held = invLines.Where(l => l.Disposition == nameof(LineDisposition.Held)).Sum(l => l.Quantity * l.UnitPrice);
            inv.Status = (paidRow.Amount >= accepted && held <= 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid).ToString();
        }
        _db.PaymentLines.RemoveRange(draftLines);
        _db.Payments.Remove(draft);
        var routed = await _db.RoutedTo.FindAsync(["Payment", draftPaymentNumber], ct).ConfigureAwait(false);
        if (routed is not null) _db.RoutedTo.Remove(routed);
        var requester = await _db.Requesters.FindAsync(["Payment", draftPaymentNumber], ct).ConfigureAwait(false);
        if (requester is not null) _db.Requesters.Remove(requester);
        var posted = new Payment(payNumber, PaymentStatus.Posted, draft.Currency,
            new VendorBankSnapshot(bank.AccountRef, bank.Verified), Date(draft.DueDate),
            draft.Source, draft.CreatedBy, idempotencyKey, postedLines);
        RecordReplay($"paypost:{idempotencyKey}", posted);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return posted;
    }

    public async Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<VendorBankDto>($"bankchange:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var vendor = await _db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false);
        if (vendor is null) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        var bank = await _db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false);
        if (bank is null) _db.VendorBanks.Add(bank = new VendorBankRow { VendorId = vendorId, AccountRef = newAccountRef, Verified = false, VerifiedAt = null });
        else { bank.AccountRef = newAccountRef; bank.Verified = false; bank.VerifiedAt = null; }
        var dto = new VendorBankDto(vendorId, newAccountRef, false, null);
        RecordReplay($"bankchange:{idempotencyKey}", dto);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return dto;
    }

    public async Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default)
    {
        var replay = await ReplayAsync<VendorBankDto>($"bankverify:{idempotencyKey}", ct).ConfigureAwait(false);
        if (replay is not null) return replay;
        using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var bank = await _db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No bank details for vendor {vendorId}.");
        var at = DateTimeOffset.UtcNow;
        bank.Verified = true;
        bank.VerifiedAt = Text(at);
        var dto = new VendorBankDto(vendorId, bank.AccountRef, true, at);
        RecordReplay($"bankverify:{idempotencyKey}", dto);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return dto;
    }
}
