// Real ERP adapter: EF Core over sqlite with one transaction per write.
// Controls are enforced here in the ERP layer; the in-memory fake stays for
// unit tests only and never backs the app. Refusal texts match the fake so
// both adapters honor the same contract.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gate.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp;

public sealed partial class EfCoreErpAdapter : IErpAdapter
{
    internal const decimal PriceTolerance = 0.02m;
    internal const string BaseCurrencyKey = "base-currency";
    internal const string AutoApproveKey = "auto-approve-threshold";
    internal const string OkPayload = "\"ok\"";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // One context per call, minted from the factory: Blazor circuit handlers
    // interleave on the shared scoped adapter, and a single long-lived
    // context both throws "a second operation was started on this context"
    // and serves stale tracked reads. Per-call contexts keep overlapping
    // handlers independent and every read fresh.
    private readonly IDbContextFactory<ErpDbContext> _factory;

    public EfCoreErpAdapter(
        IDbContextFactory<ErpDbContext> factory,
        IErpPolicy? policy = null,
        IErpPolicyContext? policyContext = null)
    {
        _factory = factory;
        _policy = policy;
        _policyContext = policyContext;
    }

    /// <summary>Unit-test-only: mints a fresh context per call from the
    /// given options, same as the factory constructor. Production always
    /// uses the factory constructor.</summary>
    public EfCoreErpAdapter(
        DbContextOptions<ErpDbContext> options,
        IErpPolicy? policy = null,
        IErpPolicyContext? policyContext = null)
    {
        _factory = new OptionsContextFactory(options);
        _policy = policy;
        _policyContext = policyContext;
    }

    private sealed class OptionsContextFactory(DbContextOptions<ErpDbContext> options)
        : IDbContextFactory<ErpDbContext>
    {
        public ErpDbContext CreateDbContext() => new(options);
    }

    /// <summary>Test-loader-only factory access: the scenario loader seeds
    /// masters through ErpSeeder on a short-lived context it owns and
    /// disposes. Production code never reaches through the adapter.</summary>
    internal IDbContextFactory<ErpDbContext> DbFactory => _factory;

    /// <summary>Test-loader-only: wipes and re-migrates the database behind
    /// this adapter. Production code never resets the ERP this way.</summary>
    internal async Task ResetDatabaseForTestsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        await db.Database.EnsureDeletedAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    private static T Enum<T>(string value) where T : struct
        => System.Enum.Parse<T>(value, ignoreCase: false);

    private static string Text(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static string Text(DateOnly value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset DateTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset? Stamp(string? value) => value is null ? null : DateTime(value);
    private static DateOnly? Day(string? value) => value is null ? null : Date(value);
    private static DateTimeOffset BusinessStamp(DateOnly? businessDate) =>
        businessDate.HasValue
            ? new DateTimeOffset(businessDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;

    private static string InvoiceDoc(string vendorId, string supplierNumber) => $"{vendorId}/{supplierNumber}";

    private static void ThrowIfTerminal(string what, bool terminal)
    {
        if (terminal) throw new InvalidOperationException($"{what} is terminal and immutable.");
    }

    private async Task<string> NextAsync(ErpDbContext db, string prefix, CancellationToken ct)
    {
        var row = await db.Sequences.FindAsync([prefix], ct).ConfigureAwait(false);
        long n;
        string? format;
        int padding;
        if (row is null)
        {
            n = NumberSequences.DefaultStart;
            format = null;
            padding = NumberSequences.DefaultPadding;
            db.Sequences.Add(new SequenceRow
            {
                Name = prefix, Next = n + 1, Format = format, Padding = padding
            });
        }
        else
        {
            n = row.Next;
            row.Next = n + 1;
            format = row.Format;
            // Rows written before the format migration carry padding 0, which
            // means unpadded; only loader-configured and new rows are padded.
            padding = row.Padding;
        }
        return NumberSequences.FormatNumber(prefix, format, padding, n);
    }

    private async Task<T?> ReplayAsync<T>(ErpDbContext db, string key, CancellationToken ct)
    {
        var row = await db.IdempotencyKeys.FindAsync([key], ct).ConfigureAwait(false);
        if (row is null) return default;
        return JsonSerializer.Deserialize<T>(row.Payload, JsonOpts);
    }

    private void RecordReplay(ErpDbContext db, string key, object? result)
    {
        db.IdempotencyKeys.Add(new IdempotencyRow
        {
            Key = key,
            Payload = JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object), JsonOpts),
            CreatedAt = Text(DateTimeOffset.UtcNow)
        });
    }

    private static string DerivePoDoc(PoDocStatus current, IReadOnlyList<PoLineRow> lines)
    {
        if (current is PoDocStatus.Closed or PoDocStatus.Cancelled) return current.ToString();
        var ordered = lines.Sum(l => l.QtyOrdered);
        var received = lines.Sum(l => l.QtyReceived);
        var invoiced = lines.Sum(l => l.QtyInvoiced);
        if (ordered <= 0) return PoDocStatus.Open.ToString();
        var fullyReceived = received >= ordered;
        var fullyInvoiced = invoiced >= ordered;
        if (fullyReceived && fullyInvoiced) return PoDocStatus.Closed.ToString();
        if (fullyReceived) return PoDocStatus.Received.ToString();
        if (fullyInvoiced) return PoDocStatus.Invoiced.ToString();
        if (received > 0 && invoiced > 0) return PoDocStatus.PartiallyInvoiced.ToString();
        if (received > 0) return PoDocStatus.PartiallyReceived.ToString();
        if (invoiced > 0) return PoDocStatus.PartiallyInvoiced.ToString();
        return PoDocStatus.Open.ToString();
    }

    private async Task<PurchaseOrder?> ReadPoAsync(ErpDbContext db, string poNumber, CancellationToken ct)
    {
        var po = await db.PurchaseOrders.FindAsync([poNumber], ct).ConfigureAwait(false);
        if (po is null) return null;
        var lines = await db.PurchaseOrderLines
            .Where(l => l.PoNumber == poNumber).OrderBy(l => l.LineNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        return new PurchaseOrder(po.PoNumber, po.PrNumber, po.VendorId, po.PaymentTermsId,
            Enum<PoApprovalStatus>(po.ApprovalStatus), Enum<PoDocStatus>(po.DocStatus),
            po.Currency, po.Source, po.CreatedBy, po.IdempotencyKey,
            lines.Select(l => new PurchaseOrderLine(l.PoLineRef, l.LineNumber, l.ItemId,
                l.QtyOrdered, l.QtyReceived, l.QtyInvoiced, l.QtyQuarantined, l.QtyRejected,
                l.UnitPrice, l.LineAmount)).ToList(),
            CreatedAt: Stamp(po.CreatedAt), ApprovedBy: po.ApprovedBy, ApprovedAt: Stamp(po.ApprovedAt));
    }

    private async Task<VendorInvoice?> ReadInvoiceAsync(ErpDbContext db, string vendorId, string supplierInvoiceNumber, CancellationToken ct)
    {
        var inv = await db.VendorInvoices.FindAsync([vendorId, supplierInvoiceNumber], ct).ConfigureAwait(false);
        if (inv is null) return null;
        var lines = await db.VendorInvoiceLines
            .Where(l => l.VendorId == vendorId && l.SupplierInvoiceNumber == supplierInvoiceNumber)
            .OrderBy(l => l.RowId).ToListAsync(ct).ConfigureAwait(false);
        var matches = await db.MatchResults
            .Where(m => m.VendorId == vendorId && m.SupplierInvoiceNumber == supplierInvoiceNumber)
            .OrderBy(m => m.RowId).ToListAsync(ct).ConfigureAwait(false);
        return new VendorInvoice(inv.SupplierInvoiceNumber, inv.VendorId, inv.PoNumber,
            Enum<InvoiceStatus>(inv.Status), inv.Voucher, inv.Currency, inv.Source, inv.CreatedBy,
            inv.IdempotencyKey,
            lines.Select(l => new VendorInvoiceLine(l.PoLineRef, l.Quantity, l.UnitPrice,
                Enum<LineDisposition>(l.Disposition), l.HoldReason)).ToList(),
            matches.Select(m => new LineMatchResult(m.PoLineRef, m.Matched, m.Message)).ToList(),
            CreatedAt: Stamp(inv.CreatedAt), ApprovedBy: inv.ApprovedBy, ApprovedAt: Stamp(inv.ApprovedAt),
            PostedBy: inv.PostedBy, PostedAt: Stamp(inv.PostedAt), DueDate: Day(inv.DueDate));
    }

    private async Task<Payment?> ReadPaymentAsync(ErpDbContext db, string paymentNumber, CancellationToken ct)
    {
        var pay = await db.Payments.FindAsync([paymentNumber], ct).ConfigureAwait(false);
        if (pay is null) return null;
        var lines = await db.PaymentLines
            .Where(l => l.PaymentNumber == paymentNumber).OrderBy(l => l.RowId)
            .ToListAsync(ct).ConfigureAwait(false);
        return new Payment(pay.PaymentNumber, Enum<PaymentStatus>(pay.Status), pay.Currency,
            new VendorBankSnapshot(pay.PayeeAccountRef, pay.PayeeVerified), Date(pay.DueDate),
            pay.Source, pay.CreatedBy, pay.IdempotencyKey,
            lines.Select(l => new PaymentLine(l.VendorId, l.SupplierInvoiceNumber, l.Amount)).ToList(),
            CreatedAt: Stamp(pay.CreatedAt), ApprovedBy: pay.ApprovedBy, ApprovedAt: Stamp(pay.ApprovedAt),
            PostedBy: pay.PostedBy, PostedAt: Stamp(pay.PostedAt));
    }

    public async Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var so = await db.SalesOrders.FindAsync([soNumber], ct).ConfigureAwait(false);
        if (so is null) return null;
        var lines = await db.SalesOrderLines
            .Where(l => l.SoNumber == soNumber).OrderBy(l => l.LineNo)
            .ToListAsync(ct).ConfigureAwait(false);
        return new SalesOrder(so.SoNumber, Enum<SalesOrderStatus>(so.Status), so.Currency,
            lines.Select(l => new SalesOrderLine(l.ItemId, l.Quantity)).ToList());
    }

    public async Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.BomLines.Where(b => b.ItemId == itemId)
            .OrderBy(b => b.ComponentItemId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(b => new BomLine(b.ComponentItemId, b.QtyPer)).ToList();
    }

    public async Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        // Client-side sum: SQLite cannot aggregate decimals server-side.
        var rows = await db.InventoryTransactions
            .Where(t => t.ItemId == itemId && t.Status == nameof(InventoryStatus.Available))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Sum(t => t.Quantity);
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var closed = new[] { nameof(PoDocStatus.Closed), nameof(PoDocStatus.Cancelled) };
        // Client-side remainder check: SQLite cannot do decimal math server-side.
        var numbers = (await db.PurchaseOrderLines
            .Where(l => l.ItemId == itemId).ToListAsync(ct).ConfigureAwait(false))
            .Where(l => l.QtyOrdered - l.QtyReceived > 0)
            .Select(l => l.PoNumber).Distinct().ToList();
        var result = new List<PurchaseOrder>();
        foreach (var number in numbers)
        {
            var po = await ReadPoAsync(db, number, ct).ConfigureAwait(false);
            if (po is not null && !closed.Contains(po.DocStatus.ToString())) result.Add(po);
        }
        return result;
    }

    public async Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var open = new[] { nameof(PrStatus.Draft), nameof(PrStatus.Submitted) };
        var numbers = await db.PurchaseRequisitions
            .Where(p => open.Contains(p.Status)).OrderBy(p => p.PrNumber)
            .Select(p => p.PrNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PurchaseRequisition>();
        foreach (var number in numbers)
        {
            var pr = await GetPrAsync(number, ct).ConfigureAwait(false);
            if (pr is not null) result.Add(pr);
        }
        return result;
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var open = new[] { nameof(PoApprovalStatus.Draft), nameof(PoApprovalStatus.InReview) };
        var numbers = await db.PurchaseOrders
            .Where(p => open.Contains(p.ApprovalStatus)).OrderBy(p => p.PoNumber)
            .Select(p => p.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PurchaseOrder>();
        foreach (var number in numbers)
        {
            var po = await ReadPoAsync(db, number, ct).ConfigureAwait(false);
            if (po is not null) result.Add(po);
        }
        return result;
    }

    public async Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var open = new[] { nameof(InvoiceStatus.Received), nameof(InvoiceStatus.Matched) };
        var keys = await db.VendorInvoices
            .Where(i => open.Contains(i.Status))
            .OrderBy(i => i.VendorId).ThenBy(i => i.SupplierInvoiceNumber)
            .Select(i => new { i.VendorId, i.SupplierInvoiceNumber }).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<VendorInvoice>();
        foreach (var key in keys)
        {
            var inv = await ReadInvoiceAsync(db, key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (inv is not null) result.Add(inv);
        }
        return result;
    }

    public async Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var open = new[] { nameof(PaymentStatus.Draft), nameof(PaymentStatus.Submitted) };
        var numbers = await db.Payments
            .Where(p => open.Contains(p.Status)).OrderBy(p => p.PaymentNumber)
            .Select(p => p.PaymentNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<Payment>();
        foreach (var number in numbers)
        {
            var pay = await ReadPaymentAsync(db, number, ct).ConfigureAwait(false);
            if (pay is not null) result.Add(pay);
        }
        return result;
    }

    public async Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        // An ASN is unreceived while no receipt is posted against its PO.
        var receivedPo = (await db.ShipmentReceipts.Select(r => r.PoNumber)
            .Distinct().ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var numbers = await db.Asns.OrderBy(a => a.AsnNumber)
            .Select(a => new { a.AsnNumber, a.PoNumber }).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<AsnDto>();
        foreach (var asn in numbers.Where(a => !receivedPo.Contains(a.PoNumber)))
        {
            var dto = await GetAsnAsync(asn.AsnNumber, ct).ConfigureAwait(false);
            if (dto is not null) result.Add(dto);
        }
        return result;
    }

    public async Task<IReadOnlyList<SalesOrder>> GetSalesOrdersAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.SalesOrders.OrderBy(s => s.SoNumber)
            .Select(s => s.SoNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<SalesOrder>();
        foreach (var number in numbers)
        {
            var so = await GetSalesOrderAsync(number, ct).ConfigureAwait(false);
            if (so is not null) result.Add(so);
        }
        return result;
    }

    public async Task<IReadOnlyList<PurchaseRequisition>> GetPurchaseRequisitionsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.PurchaseRequisitions.OrderBy(p => p.PrNumber)
            .Select(p => p.PrNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PurchaseRequisition>();
        foreach (var number in numbers)
        {
            var pr = await GetPrAsync(number, ct).ConfigureAwait(false);
            if (pr is not null) result.Add(pr);
        }
        return result;
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.PurchaseOrders.OrderBy(p => p.PoNumber)
            .Select(p => p.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PurchaseOrder>();
        foreach (var number in numbers)
        {
            var po = await ReadPoAsync(db, number, ct).ConfigureAwait(false);
            if (po is not null) result.Add(po);
        }
        return result;
    }

    public async Task<IReadOnlyList<VendorInvoice>> GetVendorInvoicesAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var keys = await db.VendorInvoices
            .OrderBy(i => i.VendorId).ThenBy(i => i.SupplierInvoiceNumber)
            .Select(i => new { i.VendorId, i.SupplierInvoiceNumber }).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<VendorInvoice>();
        foreach (var key in keys)
        {
            var inv = await ReadInvoiceAsync(db, key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (inv is not null) result.Add(inv);
        }
        return result;
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.Payments.OrderBy(p => p.PaymentNumber)
            .Select(p => p.PaymentNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<Payment>();
        foreach (var number in numbers)
        {
            var pay = await ReadPaymentAsync(db, number, ct).ConfigureAwait(false);
            if (pay is not null) result.Add(pay);
        }
        return result;
    }

    public async Task<IReadOnlyList<ShipmentReceipt>> GetShipmentReceiptsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.ShipmentReceipts.OrderBy(r => r.ReceiptNumber)
            .Select(r => r.ReceiptNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<ShipmentReceipt>();
        foreach (var number in numbers)
        {
            var receipt = await GetReceiptAsync(number, ct).ConfigureAwait(false);
            if (receipt is not null) result.Add(receipt);
        }
        return result;
    }

    public async Task<IReadOnlyList<AsnDto>> GetAsnsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.Asns.OrderBy(a => a.AsnNumber)
            .Select(a => a.AsnNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<AsnDto>();
        foreach (var number in numbers)
        {
            var asn = await GetAsnAsync(number, ct).ConfigureAwait(false);
            if (asn is not null) result.Add(asn);
        }
        return result;
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersByVendorAsync(string vendorId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var numbers = await db.PurchaseOrders.Where(p => p.VendorId == vendorId)
            .OrderBy(p => p.PoNumber).Select(p => p.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PurchaseOrder>();
        foreach (var number in numbers)
        {
            var po = await ReadPoAsync(db, number, ct).ConfigureAwait(false);
            if (po is not null) result.Add(po);
        }
        return result;
    }

    public async Task<IReadOnlyList<ItemDto>> GetItemsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Items.OrderBy(i => i.ItemId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(i => new ItemDto(i.ItemId, i.Description, i.IsCritical, i.UnitsPerCarton, i.Unit)).ToList();
    }

    public async Task<IReadOnlyList<InventoryTransaction>> GetAllInventoryTransactionsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.InventoryTransactions
            .OrderBy(t => t.ItemId).ThenBy(t => t.ReceiptRef).ThenBy(t => t.LotRef).ThenBy(t => t.Status)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(t => new InventoryTransaction(t.ItemId, t.ReceiptRef, t.LotRef, Enum<InventoryStatus>(t.Status), t.Quantity)).ToList();
    }

    public async Task<IReadOnlyList<PendingApprovalDto>> GetPendingApprovalsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var routed = await db.RoutedTo
            .OrderBy(r => r.DocumentType).ThenBy(r => r.DocumentNumber)
            .ToListAsync(ct).ConfigureAwait(false);
        var result = new List<PendingApprovalDto>();
        foreach (var row in routed)
        {
            if (!await IsAwaitingDecisionAsync(db, row.DocumentType, row.DocumentNumber, ct).ConfigureAwait(false)) continue;
            var submittedBy = row.SubmittedBy;
            if (string.IsNullOrWhiteSpace(submittedBy))
            {
                submittedBy = (await db.Requesters
                    .FindAsync([row.DocumentType, row.DocumentNumber], ct).ConfigureAwait(false))?.Requester
                    ?? string.Empty;
            }
            var amount = await ResolveDocumentAmountAsync(db, row.DocumentType, row.DocumentNumber, ct).ConfigureAwait(false);
            result.Add(new PendingApprovalDto(row.DocumentType, row.DocumentNumber, row.RoutedTo, submittedBy, amount));
        }
        return result;
    }

    private async Task<bool> IsAwaitingDecisionAsync(ErpDbContext db, string documentType, string documentNumber, CancellationToken ct)
    {
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                return pr is not null && Enum<PrStatus>(pr.Status) == PrStatus.Submitted;
            }
            case "PO":
            {
                var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false);
                return po is not null && Enum<PoApprovalStatus>(po.ApprovalStatus) == PoApprovalStatus.InReview;
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) return false;
                var inv = await db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false);
                return inv is not null && Enum<InvoiceStatus>(inv.Status) == InvoiceStatus.Matched;
            }
            case "Payment":
            {
                var pay = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false);
                return pay is not null && Enum<PaymentStatus>(pay.Status) == PaymentStatus.Submitted;
            }
            default: return false;
        }
    }

    public async Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Vendors.OrderBy(v => v.VendorId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(v => new VendorDto(v.VendorId, v.Name, v.CountryOfOrigin,
            v.IsBlocked, v.BlockReason, v.PaymentTermsId, v.Email)).ToList();
    }

    public async Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.VendorDocuments.Where(d => d.VendorId == vendorId)
            .OrderBy(d => d.Kind).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(d => new VendorDocumentDto(d.VendorId, d.Kind, d.Reference, d.Present)).ToList();
    }

    public async Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false);
        return vendor?.CountryOfOrigin;
    }

    public async Task<IReadOnlyList<string>> GetPricedVendorIdsAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await db.VendorPrices
            .Where(p => p.ItemId == itemId)
            .OrderBy(p => p.VendorId)
            .Select(p => p.VendorId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<decimal?> GetVendorPriceAsync(string vendorId, string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.VendorPrices.FindAsync([vendorId, itemId], ct).ConfigureAwait(false);
        return row?.UnitPrice;
    }

    public async Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.LegacyShares.FindAsync([vendorId, itemId], ct).ConfigureAwait(false);
        return row?.Share ?? 0m;
    }

    public async Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await VolumeHistoryForAsync(db, itemId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OpenOrderQty>> GetOpenOrderQuantitiesAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await OpenOrdersForAsync(db, itemId, null, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<VolumeHistory>> VolumeHistoryForAsync(
        ErpDbContext db, string itemId, CancellationToken ct)
    {
        // Posted receipt lines only: history is real documents, never seeds.
        // Trailing 12 months per the CORP-02 contract: receipts posted before
        // the window (e.g. opening chains) stay out of the denominator.
        // Unknown posting dates stay in: only a known-old date excludes.
        var windowStart = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime).AddMonths(-12);
        var result = new List<VolumeHistory>();
        var receipts = await db.ShipmentReceipts.OrderBy(r => r.ReceiptNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var receipt in receipts)
        {
            if (Day(receipt.PostingDate) is { } posted && posted < windowStart)
            {
                continue;
            }

            var po = await db.PurchaseOrders.FindAsync([receipt.PoNumber], ct).ConfigureAwait(false);
            var vendor = po is null ? null : await db.Vendors.FindAsync([po.VendorId], ct).ConfigureAwait(false);
            var country = vendor?.CountryOfOrigin ?? "OTHER";
            var lines = await db.ShipmentReceiptLines
                .Where(l => l.ReceiptNumber == receipt.ReceiptNumber).ToListAsync(ct).ConfigureAwait(false);
            var poLines = po is null
                ? []
                : await db.PurchaseOrderLines.Where(l => l.PoNumber == po.PoNumber).ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var lineItem = poLines.FirstOrDefault(x => x.PoLineRef == line.PoLineRef)?.ItemId ?? itemId;
                if (lineItem == itemId) result.Add(new VolumeHistory($"Receipt {receipt.ReceiptNumber}", country, line.QtyReceived));
            }
        }
        return result;
    }

    /// <summary>Open, not-yet-received PO quantity for an item: ordered minus
    /// received on every PO line whose PO is neither closed nor cancelled.
    /// The PO under check is excluded so an approval re-check never counts
    /// the line's own quantity twice (once open, once as the award).</summary>
    private static async Task<IReadOnlyList<OpenOrderQty>> OpenOrdersForAsync(
        ErpDbContext db, string itemId, string? excludePoNumber, CancellationToken ct)
    {
        var closed = new[] { nameof(PoDocStatus.Closed), nameof(PoDocStatus.Cancelled) };
        var result = new List<OpenOrderQty>();
        var pos = await db.PurchaseOrders.OrderBy(p => p.PoNumber).ToListAsync(ct).ConfigureAwait(false);
        foreach (var po in pos)
        {
            if (closed.Contains(po.DocStatus))
            {
                continue;
            }

            if (excludePoNumber is not null && po.PoNumber == excludePoNumber)
            {
                continue;
            }

            var vendor = await db.Vendors.FindAsync([po.VendorId], ct).ConfigureAwait(false);
            var country = vendor?.CountryOfOrigin ?? "OTHER";
            var lines = await db.PurchaseOrderLines
                .Where(l => l.PoNumber == po.PoNumber && l.ItemId == itemId)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in lines)
            {
                var open = line.QtyOrdered - line.QtyReceived;
                if (open > 0)
                {
                    result.Add(new OpenOrderQty($"PO {po.PoNumber}", country, open));
                }
            }
        }

        return result;
    }

    public async Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var item = await db.Items.FindAsync([itemId], ct).ConfigureAwait(false);
        return item is null ? null : new ItemDto(item.ItemId, item.Description, item.IsCritical, item.UnitsPerCarton, item.Unit);
    }

    public async Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var terms = await db.PaymentTerms.FindAsync([termsId], ct).ConfigureAwait(false);
        return terms is null ? null : new PaymentTermsDto(terms.TermsId, terms.NetDays);
    }

    public async Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var bank = await db.VendorBanks.FindAsync([vendorId], ct).ConfigureAwait(false);
        return bank is null ? null
            : new VendorBankDto(bank.VendorId, bank.AccountRef, bank.Verified,
                bank.VerifiedAt is null ? null : DateTime(bank.VerifiedAt));
    }

    public async Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var user = await db.Users.FindAsync([userId], ct).ConfigureAwait(false);
        return user is null ? null : new UserDto(user.UserId, user.Role, user.SigningLimit, user.LimitCurrency);
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Users.OrderBy(u => u.UserId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(u => new UserDto(u.UserId, u.Role, u.SigningLimit, u.LimitCurrency)).ToList();
    }

    public async Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.Config.FindAsync([AutoApproveKey], ct).ConfigureAwait(false);
        return row is null ? 0m : decimal.Parse(row.Value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Delegations.OrderBy(d => d.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(d => new DelegationDto(d.FromUser, d.ToUser, Date(d.Start), Date(d.End))).ToList();
    }

    public async Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await ReadPoAsync(db, poNumber, ct).ConfigureAwait(false);
    }

    public async Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var receipt = await db.ShipmentReceipts.FindAsync([receiptNumber], ct).ConfigureAwait(false);
        if (receipt is null) return null;
        var lines = await db.ShipmentReceiptLines
            .Where(l => l.ReceiptNumber == receiptNumber).OrderBy(l => l.RowId)
            .ToListAsync(ct).ConfigureAwait(false);
        return new ShipmentReceipt(receipt.ReceiptNumber, receipt.PoNumber, receipt.SupplierPackingSlipNumber,
            Enum<ReceiptStatus>(receipt.Status), receipt.Source, receipt.CreatedBy, receipt.IdempotencyKey,
            lines.Select(l => new ReceiptLine(l.PoLineRef, l.QtyReceived, l.QtyQuarantined, l.QtyRejected, l.LotRef, l.ConditionNote)).ToList(),
            CreatedAt: Stamp(receipt.CreatedAt), PostedBy: receipt.PostedBy, PostedAt: Stamp(receipt.PostedAt),
            PostingDate: Day(receipt.PostingDate));
    }

    public async Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await ReadInvoiceAsync(db, vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false);
    }

    public async Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        return await ReadPaymentAsync(db, paymentNumber, ct).ConfigureAwait(false);
    }

    public async Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var conf = await db.VendorConfirmations.FindAsync([poNumber], ct).ConfigureAwait(false);
        if (conf is null) return null;
        var lines = await db.VendorConfirmationLines
            .Where(l => l.PoNumber == poNumber).OrderBy(l => l.PoLineRef)
            .ToListAsync(ct).ConfigureAwait(false);
        return new VendorConfirmation(conf.PoNumber, Date(conf.ConfirmDate), conf.Source, conf.CreatedBy,
            conf.IdempotencyKey,
            lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.ConfirmedQty, l.BackorderNote)).ToList());
    }

    public async Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var asn = await db.Asns.FindAsync([asnNumber], ct).ConfigureAwait(false);
        if (asn is null) return null;
        var cartons = await db.AsnCartons.Where(c => c.AsnNumber == asnNumber)
            .OrderBy(c => c.CartonId).ToListAsync(ct).ConfigureAwait(false);
        return new AsnDto(asn.AsnNumber, asn.PoNumber, asn.Source, asn.CreatedBy, asn.IdempotencyKey,
            cartons.Select(c => new AsnCarton(c.CartonId, c.ItemId, c.UnitsPerCarton, c.LotRef)).ToList());
    }

    public async Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.InventoryTransactions.Where(t => t.ItemId == itemId)
            .OrderBy(t => t.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(t => new InventoryTransaction(t.ItemId, t.ReceiptRef, t.LotRef, Enum<InventoryStatus>(t.Status), t.Quantity)).ToList();
    }

    public async Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Settlements
            .Where(s => s.VendorId == vendorId && s.SupplierInvoiceNumber == supplierInvoiceNumber)
            .OrderBy(s => s.PaymentNumber).ThenBy(s => s.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(s => new Settlement(s.VendorId, s.SupplierInvoiceNumber, s.PaymentNumber, s.SettledAmount, s.RemainingBalance)).ToList();
    }

    public async Task<IReadOnlyList<HeldRelease>> GetHeldReleasesAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.HeldReleases
            .Where(r => r.VendorId == vendorId && r.SupplierInvoiceNumber == supplierInvoiceNumber)
            .OrderBy(r => r.ReleasedAt).ThenBy(r => r.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new HeldRelease(
            r.VendorId, r.SupplierInvoiceNumber, r.PoLineRef, r.Quantity, r.ReleasedBy,
            DateTimeOffset.Parse(r.ReleasedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))).ToList();
    }

    public async Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Holds.Where(h => h.Status == nameof(HoldStatus.Set))
            .OrderBy(h => h.HoldId).ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(h => documentNumber == null || h.DocumentNumber == documentNumber)
            .Select(h => new HoldDto(h.HoldId, h.ReasonCode, h.Note, h.RuleId, h.CaseId, h.SetBy,
                DateTime(h.SetAt), Enum<HoldStatus>(h.Status), h.DocumentType, h.DocumentNumber,
                h.PoLineRef, h.LotRef, h.CartonId, h.AsnNumber)).ToList();
    }

    public async Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.HoldReleases.Where(h => h.HoldId == holdId)
            .OrderBy(h => h.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(h => new HoldRelease(h.HoldId, h.ReleasedBy, DateTime(h.ReleasedAt), h.ApprovalRef)).ToList();
    }

    public async Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Approvals
            .Where(a => a.DocumentType == documentType && a.DocumentNumber == documentNumber)
            .OrderBy(a => a.RowId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(a => new ApprovalDto(a.DocumentType, a.DocumentNumber, a.RoutedTo, a.ActedBy,
            a.ActingAsDelegate, DateTime(a.Timestamp), a.Approved, a.Comment, a.DocumentAmount, a.LimitChecked)).ToList();
    }

    public async Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var pr = await db.PurchaseRequisitions.FindAsync([prNumber], ct).ConfigureAwait(false);
        if (pr is null) return null;
        var lines = await db.PurchaseRequisitionLines
            .Where(l => l.PrNumber == prNumber).OrderBy(l => l.LineNo)
            .ToListAsync(ct).ConfigureAwait(false);
        return new PurchaseRequisition(pr.PrNumber, Enum<PrStatus>(pr.Status), pr.Requester, pr.Currency,
            pr.EstimatedAmount, pr.Source, pr.CreatedBy, pr.IdempotencyKey,
            lines.Select(l => new PurchaseRequisitionLine(l.ItemId, l.Quantity, l.Unit)).ToList(),
            CreatedAt: Stamp(pr.CreatedAt), ApprovedBy: pr.ApprovedBy, ApprovedAt: Stamp(pr.ApprovedAt));
    }

    public async Task<CompanyInfoDto?> GetCompanyInfoAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var row = await db.CompanyInfo.OrderBy(c => c.EntityCode).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new CompanyInfoDto(row.EntityCode, row.LegalName, row.Currency,
            row.BillToAddress, row.ShipToAddress, row.SenderEmail);
    }

    public async Task<IReadOnlyList<SiteDto>> GetSitesAsync(CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        var rows = await db.Sites.OrderBy(s => s.SiteId).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(s => new SiteDto(s.SiteId, s.Name, s.Address,
            s.Capabilities.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))).ToList();
    }

    public async Task<ConsequencePreview?> GetConsequencePreviewAsync(string documentType, string documentNumber, CancellationToken ct = default)
    {
        using var db = _factory.CreateDbContext();
        // Computed, read-only: what approval would post, the due date, the
        // payment run the invoice joins, who approves next, and held value.
        var routed = await db.RoutedTo.FindAsync([documentType, documentNumber], ct).ConfigureAwait(false);
        switch (documentType)
        {
            case "PR":
            {
                var pr = await db.PurchaseRequisitions.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pr is null || Enum<PrStatus>(pr.Status) != PrStatus.Submitted) return null;
                return new ConsequencePreview(documentType, documentNumber,
                    [FormattableString.Invariant($"PR {documentNumber} approved for {pr.EstimatedAmount} {pr.Currency}; PO creation unlocks.")],
                    null, null, routed?.RoutedTo, 0m, null, pr.Currency);
            }
            case "PO":
            {
                var po = await db.PurchaseOrders.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (po is null || Enum<PoApprovalStatus>(po.ApprovalStatus) != PoApprovalStatus.InReview) return null;
                var lines = await db.PurchaseOrderLines.Where(l => l.PoNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                var total = lines.Sum(l => l.LineAmount);
                return new ConsequencePreview(documentType, documentNumber,
                    [FormattableString.Invariant($"PO {documentNumber} approved for {total} {po.Currency}; vendor confirmation and receipt unlock.")],
                    null, null, routed?.RoutedTo, 0m, null, po.Currency);
            }
            case "Invoice":
            {
                var parts = documentNumber.Split('/', 2);
                if (parts.Length != 2) return null;
                var inv = await db.VendorInvoices.FindAsync([parts[0], parts[1]], ct).ConfigureAwait(false);
                if (inv is null) return null;
                var status = Enum<InvoiceStatus>(inv.Status);
                if (status is InvoiceStatus.Posted or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid or InvoiceStatus.Closed) return null;
                var lines = await db.VendorInvoiceLines
                    .Where(l => l.VendorId == parts[0] && l.SupplierInvoiceNumber == parts[1]).ToListAsync(ct).ConfigureAwait(false);
                var accepted = lines.Where(l => l.Disposition == nameof(LineDisposition.Accepted)).ToList();
                var held = lines.Where(l => l.Disposition == nameof(LineDisposition.Held)).ToList();
                var acceptedQty = accepted.Sum(l => l.Quantity);
                var acceptedValue = accepted.Sum(l => l.Quantity * l.UnitPrice);
                var heldValue = held.Sum(l => l.Quantity * l.UnitPrice);
                return new ConsequencePreview(documentType, documentNumber,
                    [FormattableString.Invariant($"Voucher posts {acceptedValue} {inv.Currency} for {acceptedQty} units against PO {inv.PoNumber}.")],
                    Day(inv.DueDate), await PeekSequenceAsync(db, "PAYDRAFT", ct).ConfigureAwait(false),
                    routed?.RoutedTo, heldValue, held.FirstOrDefault()?.HoldReason, inv.Currency);
            }
            case "Payment":
            {
                var pay = await db.Payments.FindAsync([documentNumber], ct).ConfigureAwait(false);
                if (pay is null) return null;
                var status = Enum<PaymentStatus>(pay.Status);
                if (status is not PaymentStatus.Draft and not PaymentStatus.Submitted) return null;
                var lines = await db.PaymentLines.Where(l => l.PaymentNumber == documentNumber).ToListAsync(ct).ConfigureAwait(false);
                var total = lines.Sum(l => l.Amount);
                return new ConsequencePreview(documentType, documentNumber,
                    [FormattableString.Invariant($"Payment {documentNumber} posts {total} {pay.Currency} across {lines.Count} invoice line(s).")],
                    Date(pay.DueDate), pay.PaymentNumber, routed?.RoutedTo, 0m, null, pay.Currency);
            }
            default: return null;
        }
    }

    private async Task<string> PeekSequenceAsync(ErpDbContext db, string prefix, CancellationToken ct)
    {
        var row = await db.Sequences.FindAsync([prefix], ct).ConfigureAwait(false);
        var next = row?.Next ?? NumberSequences.DefaultStart;
        return NumberSequences.FormatNumber(prefix, row?.Format, row?.Padding ?? NumberSequences.DefaultPadding, next);
    }
}
