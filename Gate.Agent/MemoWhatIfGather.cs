// What-if input gather (WHAT-IF.md). Builds history rows from
// GetVolumeHistoryAsync plus pipeline rows from the pipeline list-reads:
// open PRs (Draft/Submitted), draft POs (Draft/InReview), open PO lines,
// unreceived ASNs, open invoices (Received/Matched), and open payments
// (Draft/Submitted). Per-row quarantine flags join quarantined lots from
// inventory transactions back to their POs through the lot-to-PO map
// (ASN cartons plus receipt lines). Pipeline inventory (against
// IErpAdapter, wave 2):
//   Available: GetVolumeHistoryAsync per item; GetOpenPurchaseOrdersAsync
//     per KB item; GetOpenPurchaseRequisitionsAsync;
//     GetDraftPurchaseOrdersAsync; GetOpenInvoicesAsync;
//     GetOpenPaymentsAsync; GetUnreceivedAsnsAsync; GetVendorCountryAsync;
//     GetVendorDocumentsAsync (DocumentPresence observables);
//     GetOpenHoldsAsync per document (pre-pay hold flags);
//     GetInventoryTransactionsAsync per item plus GetReceiptAsync per
//     receipt (the lot-to-PO map); GetPurchaseOrderAsync and
//     GetInvoiceAsync (the invoice and payment item/price joins).
//   Missing (genuinely uncovered, each caved): receipts with open holds
//     (no adapter list-query reads receipts); screening hits (the
//     screening snapshot lives compliance-side, never behind the adapter);
//     dock photo observables at what-if time; cross-case open reviews.
// Rollout tolerance: each of the five list-reads may still throw
// NotSupportedException on an ERP layer that has not implemented it yet.
// The gather never stalls: that read contributes zero rows plus its
// coverage reason (which lands on the memo timeline and the approval
// card). Once the ERP layer implements the read, real rows flow and the
// reason drops without a code change.
// History rows carry quantities only (the history contract has no prices,
// vendors, or dates); amounts come from the pipeline rows.
using Gate.Contracts;

namespace Gate.Agent;

public sealed record PipelineSeamResult(
    IReadOnlyList<WhatIfPipelineRow> Rows,
    string? CoverageReason);

/// <summary>One lot's home: the PO it arrived against, plus the PO line
/// when a posted receipt names one (ASN cartons name no line).</summary>
public sealed record LotPoLink(
    string PoNumber,
    string? PoLineRef);

/// <summary>Shared gather state: the pinned snapshot, the doc kind the
/// proposal reads, adapter caches, the lot-to-PO map with its quarantine
/// sets, and the uncovered-row counters behind the conditional caveats.
/// Public because the protected row builders take it.</summary>
public sealed class WhatIfGatherContext
{
    public required KbSnapshot Snapshot { get; init; }

    public required string? DocKind { get; init; }

    public Dictionary<string, bool> KbCritical { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string?> VendorCountry { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<VendorDocumentDto>> VendorDocs { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PurchaseOrder?> PurchaseOrders { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string VendorId, string SupplierInvoiceNumber), VendorInvoice?> Invoices { get; } = new();

    public Dictionary<string, ItemDto?> Items { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, LotPoLink> LotMap { get; } = new(StringComparer.Ordinal);

    public HashSet<string> QuarantinedLots { get; } = new(StringComparer.Ordinal);

    public HashSet<(string PoNumber, string PoLineRef)> QuarantinedPoLines { get; } = new();

    public IReadOnlyList<AsnDto> UnreceivedAsns { get; set; } = [];

    public bool AsnReadMissing { get; set; }

    public int InvoiceRowsWithoutPoJoin { get; set; }

    public string? FirstInvoiceWithoutPoJoin { get; set; }

    public int PaymentRowsWithoutJoin { get; set; }

    public string? FirstPaymentWithoutJoin { get; set; }

    public int UnknownItemRows { get; set; }

    public string? FirstUnknownItem { get; set; }
}

public class MemoWhatIfGather
{
    public const string PrDocumentType = "PR";

    public const string PoDocumentType = "PO";

    public const string InvoiceDocumentType = "Invoice";

    public const string PaymentDocumentType = "Payment";

    public const string AsnDocumentType = "ASN";

    /// <summary>Standing caveat for the observables no what-if-time read
    /// covers: dock photos, screening hits, and open reviews evaluate as
    /// absent on every pipeline row.</summary>
    public const string ObservablesCaveat =
        "Pipeline rows evaluate dock observables, screening hits, and open reviews as absent: no what-if-time read covers them.";

    /// <summary>Standing seam: receipts with open holds. No adapter
    /// list-query reads receipts.</summary>
    public const string HeldReceiptsCaveat =
        "Receipts with open holds are absent from the pipeline window: no adapter list-query reads them (lead request: contract + ERP lane).";

    public const string OpenPrFallbackCaveat =
        "Open PRs are absent from the pipeline window: the ERP layer does not implement the open-PR list read yet.";

    public const string DraftPoFallbackCaveat =
        "Draft POs are absent from the pipeline window: the ERP layer does not implement the draft-PO list read yet.";

    public const string OpenInvoiceFallbackCaveat =
        "Open invoices are absent from the pipeline window: the ERP layer does not implement the open-invoice list read yet.";

    public const string OpenPaymentFallbackCaveat =
        "Draft payments are absent from the pipeline window: the ERP layer does not implement the open-payment list read yet.";

    public const string UnreceivedAsnFallbackCaveat =
        "ASNs without receipt are absent from the pipeline window: the ERP layer does not implement the unreceived-ASN list read yet.";

    private readonly IErpAdapter _erp;

    /// <summary>KB sourcing-policy id behind each share-cap type's live old
    /// config. Pinned by test against company-kb.json (the policy must exist
    /// with valid params for its type).</summary>
    public static readonly IReadOnlyDictionary<string, string> ShareCapPolicyIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MemoRuleTypes.OriginShareCap] = "origin-cap",
            [MemoRuleTypes.VendorShareCap] = "vendor-cap",
        };

    public MemoWhatIfGather(IErpAdapter erp) => _erp = erp;

    /// <summary>Gathers one history row per volume-history row for every KB
    /// item. Quantities only: UnitPrice is 0 with a null currency, VendorId
    /// and AwardDate are null, every observable is false.</summary>
    public virtual async Task<IReadOnlyList<WhatIfHistoryRow>> GatherHistoryAsync(
        KbSnapshot snapshot, CancellationToken ct = default)
    {
        var rows = new List<WhatIfHistoryRow>();
        foreach (var item in snapshot.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var history = await _erp.GetVolumeHistoryAsync(item.Id, ct).ConfigureAwait(false);
            var index = 0;
            foreach (var row in history)
            {
                rows.Add(new WhatIfHistoryRow(
                    $"{item.Id}:{index:000}:{row.Source}", item.Id, item.Critical,
                    null, row.Country, row.Cells, 0m, null));
                index++;
            }
        }

        return rows;
    }

    /// <summary>Gathers pipeline rows over the pipeline list-reads. Returns
    /// the rows plus the standing caveats for genuinely uncovered reads,
    /// one fallback reason per list-read the ERP layer has not implemented
    /// yet, and one conditional caveat per skipped-row kind (absent when
    /// every row joined).</summary>
    public virtual async Task<(IReadOnlyList<WhatIfPipelineRow> Rows, IReadOnlyList<string> Caveats)> GatherPipelineAsync(
        KbSnapshot snapshot, RuleProposal proposal, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(snapshot, proposal, ct).ConfigureAwait(false);
        var rows = new List<WhatIfPipelineRow>();
        var caveats = new List<string> { ObservablesCaveat };

        rows.AddRange(await GatherOpenPoRowsAsync(context, ct).ConfigureAwait(false));

        foreach (var seam in new Func<WhatIfGatherContext, CancellationToken, Task<PipelineSeamResult>>[]
            {
                ListOpenPrRowsAsync,
                ListDraftPoRowsAsync,
                ListOpenInvoiceRowsAsync,
                ListDraftPaymentRowsAsync,
                ListOpenAsnRowsAsync,
                ListHeldReceiptRowsAsync,
            })
        {
            var result = await seam(context, ct).ConfigureAwait(false);
            rows.AddRange(result.Rows);
            if (result.CoverageReason is not null)
            {
                caveats.Add(result.CoverageReason);
            }
        }

        AddJoinCaveats(context, caveats);
        return (rows, caveats);
    }

    /// <summary>Builds the lot-to-PO map: every unreceived ASN carton links
    /// its lot to its PO (no line), then every posted receipt line
    /// reachable through per-item inventory transactions links its lot to
    /// its PO line. Posted truth wins on conflict; pseudo receipt numbers
    /// (openings, quarantine moves) have no receipt and are skipped. Order
    /// is deterministic, so conflicts resolve identically on replay.</summary>
    public virtual async Task<IReadOnlyDictionary<string, LotPoLink>> BuildLotPoMapAsync(
        KbSnapshot snapshot, IReadOnlyList<AsnDto> unreceivedAsns, CancellationToken ct = default)
    {
        var map = new Dictionary<string, LotPoLink>(StringComparer.Ordinal);
        foreach (var asn in unreceivedAsns.OrderBy(a => a.AsnNumber, StringComparer.Ordinal))
        {
            foreach (var carton in asn.Cartons.OrderBy(c => c.CartonId, StringComparer.Ordinal))
            {
                map.TryAdd(carton.LotRef, new LotPoLink(asn.PoNumber, null));
            }
        }

        foreach (var item in snapshot.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var transactions = await _erp.GetInventoryTransactionsAsync(item.Id, ct).ConfigureAwait(false);
            var receiptNumbers = transactions
                .Select(t => t.ReceiptNumber)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);
            foreach (var receiptNumber in receiptNumbers)
            {
                var receipt = await _erp.GetReceiptAsync(receiptNumber, ct).ConfigureAwait(false);
                if (receipt is null)
                {
                    continue;
                }

                foreach (var line in receipt.Lines.OrderBy(l => l.PoLineRef, StringComparer.Ordinal))
                {
                    map[line.LotRef] = new LotPoLink(receipt.PoNumber, line.PoLineRef);
                }
            }
        }

        return map;
    }

    private async Task<WhatIfGatherContext> BuildContextAsync(
        KbSnapshot snapshot, RuleProposal proposal, CancellationToken ct)
    {
        var context = new WhatIfGatherContext
        {
            Snapshot = snapshot,
            DocKind = string.Equals(proposal.ProposedRule?.RuleType, MemoRuleTypes.DocumentPresence, StringComparison.Ordinal)
                && proposal.ProposedRule!.Params.TryGetValue("docKind", out var kind)
                ? kind
                : null,
        };
        foreach (var item in snapshot.Items)
        {
            context.KbCritical.TryAdd(item.Id, item.Critical);
        }

        IReadOnlyList<AsnDto> asns = [];
        try
        {
            asns = await _erp.GetUnreceivedAsnsAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            context.AsnReadMissing = true;
        }

        context.UnreceivedAsns = asns;
        var lotMap = await BuildLotPoMapAsync(snapshot, asns, ct).ConfigureAwait(false);
        foreach (var kv in lotMap)
        {
            context.LotMap[kv.Key] = kv.Value;
        }

        foreach (var item in snapshot.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var transactions = await _erp.GetInventoryTransactionsAsync(item.Id, ct).ConfigureAwait(false);
            var lots = transactions
                .Where(t => t.Status == InventoryStatus.Quarantined)
                .Select(t => t.LotRef)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(l => l, StringComparer.Ordinal);
            foreach (var lot in lots)
            {
                context.QuarantinedLots.Add(lot);
                if (context.LotMap.TryGetValue(lot, out var link) && link.PoLineRef is not null)
                {
                    context.QuarantinedPoLines.Add((link.PoNumber, link.PoLineRef));
                }
            }
        }

        return context;
    }

    /// <summary>Open-PO rows: one per PO line with outstanding quantity, for
    /// every KB item. DocumentPresence reads real vendor documents; the hold
    /// flag reads real open holds; the quarantine flag joins real lot
    /// quarantine through the lot-to-PO map plus the PO line level.
    /// Draft/InReview POs are skipped here: the draft-PO list read owns
    /// that slice, so each PO reports exactly once.</summary>
    protected virtual async Task<IReadOnlyList<WhatIfPipelineRow>> GatherOpenPoRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        var rows = new List<WhatIfPipelineRow>();
        foreach (var item in context.Snapshot.Items.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            var openPos = await _erp.GetOpenPurchaseOrdersAsync(item.Id, ct).ConfigureAwait(false);
            foreach (var po in openPos
                .Where(p => p.ApprovalStatus is not (PoApprovalStatus.Draft or PoApprovalStatus.InReview))
                .OrderBy(p => p.PoNumber, StringComparer.Ordinal))
            {
                rows.AddRange(await BuildPoLineRowsAsync(context, po, item.Id, ct).ConfigureAwait(false));
            }
        }

        return rows;
    }

    /// <summary>One row per PO line with outstanding quantity. A null item
    /// filter covers every line (the draft-PO list read); otherwise only
    /// that KB item's lines (each per-item open-PO call covers its own).</summary>
    protected virtual async Task<IReadOnlyList<WhatIfPipelineRow>> BuildPoLineRowsAsync(
        WhatIfGatherContext context, PurchaseOrder po, string? onlyItemId, CancellationToken ct)
    {
        var country = await GetCountryAsync(context, po.VendorId, ct).ConfigureAwait(false);
        var docPresent = await GetDocPresentAsync(context, po.VendorId, ct).ConfigureAwait(false);
        var holds = await _erp.GetOpenHoldsAsync(po.PoNumber, ct).ConfigureAwait(false);
        var rows = new List<WhatIfPipelineRow>();
        foreach (var line in po.Lines
            .Where(l => onlyItemId is null
                || string.Equals(l.ItemId, onlyItemId, StringComparison.Ordinal))
            .OrderBy(l => l.PoLineRef, StringComparer.Ordinal))
        {
            var outstanding = line.QtyOrdered - line.QtyReceived;
            if (outstanding <= 0)
            {
                continue;
            }

            var critical = await ResolveCriticalAsync(context, line.ItemId, ct).ConfigureAwait(false);
            if (critical is null)
            {
                NoteUnknownItem(context, line.ItemId);
                continue;
            }

            rows.Add(new WhatIfPipelineRow(
                PoDocumentType, po.PoNumber, line.ItemId, critical.Value,
                po.VendorId, country, outstanding, line.UnitPrice, po.Currency,
                DocPresent: docPresent,
                HasOpenHold: holds.Count > 0,
                HasQuarantine: IsPoLineQuarantined(context, po.PoNumber, line)));
        }

        return rows;
    }

    /// <summary>PR rows: one per PR line over the open-PR list read
    /// (Draft/Submitted). PRs name no vendor or price, so those fields are
    /// null and the currency comes from the PR header. On an ERP layer
    /// without the read, empty with the coverage reason.</summary>
    protected virtual async Task<PipelineSeamResult> ListOpenPrRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        IReadOnlyList<PurchaseRequisition> prs;
        try
        {
            prs = await _erp.GetOpenPurchaseRequisitionsAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return new PipelineSeamResult([], OpenPrFallbackCaveat);
        }

        var rows = new List<WhatIfPipelineRow>();
        foreach (var pr in prs.OrderBy(p => p.PrNumber, StringComparer.Ordinal))
        {
            var holds = await _erp.GetOpenHoldsAsync(pr.PrNumber, ct).ConfigureAwait(false);
            foreach (var line in pr.Lines.OrderBy(l => l.ItemId, StringComparer.Ordinal))
            {
                var critical = await ResolveCriticalAsync(context, line.ItemId, ct).ConfigureAwait(false);
                if (critical is null)
                {
                    NoteUnknownItem(context, line.ItemId);
                    continue;
                }

                rows.Add(new WhatIfPipelineRow(
                    PrDocumentType, pr.PrNumber, line.ItemId, critical.Value,
                    null, null, line.Quantity, null, pr.Currency,
                    HasOpenHold: holds.Count > 0));
            }
        }

        return new PipelineSeamResult(rows, null);
    }

    /// <summary>Draft-PO rows: one per line with outstanding quantity over
    /// the draft-PO list read (Draft/InReview). On an ERP layer without the
    /// read, empty with the coverage reason.</summary>
    protected virtual async Task<PipelineSeamResult> ListDraftPoRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        IReadOnlyList<PurchaseOrder> drafts;
        try
        {
            drafts = await _erp.GetDraftPurchaseOrdersAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return new PipelineSeamResult([], DraftPoFallbackCaveat);
        }

        var rows = new List<WhatIfPipelineRow>();
        foreach (var po in drafts.OrderBy(p => p.PoNumber, StringComparer.Ordinal))
        {
            rows.AddRange(await BuildPoLineRowsAsync(context, po, null, ct).ConfigureAwait(false));
        }

        return new PipelineSeamResult(rows, null);
    }

    /// <summary>Invoice rows: one per invoice line over the open-invoice
    /// list read (Received/Matched). Item, criticality, and quarantine join
    /// through the invoice's PO line; lines without a resolvable PO line
    /// are skipped and counted into a conditional caveat. On an ERP layer
    /// without the read, empty with the coverage reason.</summary>
    protected virtual async Task<PipelineSeamResult> ListOpenInvoiceRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        IReadOnlyList<VendorInvoice> invoices;
        try
        {
            invoices = await _erp.GetOpenInvoicesAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return new PipelineSeamResult([], OpenInvoiceFallbackCaveat);
        }

        var rows = new List<WhatIfPipelineRow>();
        foreach (var invoice in invoices
            .OrderBy(i => i.VendorId, StringComparer.Ordinal)
            .ThenBy(i => i.SupplierInvoiceNumber, StringComparer.Ordinal))
        {
            var docNumber = InvoiceDocumentNumber(invoice);
            var country = await GetCountryAsync(context, invoice.VendorId, ct).ConfigureAwait(false);
            var docPresent = await GetDocPresentAsync(context, invoice.VendorId, ct).ConfigureAwait(false);
            var holds = await _erp.GetOpenHoldsAsync(docNumber, ct).ConfigureAwait(false);
            var po = invoice.PoNumber is null
                ? null
                : await GetPoAsync(context, invoice.PoNumber, ct).ConfigureAwait(false);
            foreach (var line in invoice.Lines)
            {
                var poLine = line.PoLineRef is null || po is null
                    ? null
                    : po.Lines.FirstOrDefault(l =>
                        string.Equals(l.PoLineRef, line.PoLineRef, StringComparison.Ordinal));
                if (poLine is null || po is null)
                {
                    context.InvoiceRowsWithoutPoJoin++;
                    context.FirstInvoiceWithoutPoJoin ??= docNumber;
                    continue;
                }

                var critical = await ResolveCriticalAsync(context, poLine.ItemId, ct).ConfigureAwait(false);
                if (critical is null)
                {
                    NoteUnknownItem(context, poLine.ItemId);
                    continue;
                }

                rows.Add(new WhatIfPipelineRow(
                    InvoiceDocumentType, docNumber, poLine.ItemId, critical.Value,
                    invoice.VendorId, country, line.Quantity, line.UnitPrice, invoice.Currency,
                    DocPresent: docPresent,
                    HasOpenHold: holds.Count > 0,
                    HasQuarantine: IsPoLineQuarantined(context, po.PoNumber, poLine)));
            }
        }

        return new PipelineSeamResult(rows, null);
    }

    /// <summary>Payment rows: each open payment (Draft/Submitted) expanded
    /// over its invoices' lines, so every row carries a real item,
    /// quantity, and price. Quantities are the invoice lines' quantities:
    /// the payment pays the invoice value and partial-pay slices are not
    /// modeled. Lines whose invoice or PO line does not resolve are skipped
    /// and counted into a conditional caveat. On an ERP layer without the
    /// read, empty with the coverage reason.</summary>
    protected virtual async Task<PipelineSeamResult> ListDraftPaymentRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        IReadOnlyList<Payment> payments;
        try
        {
            payments = await _erp.GetOpenPaymentsAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return new PipelineSeamResult([], OpenPaymentFallbackCaveat);
        }

        var rows = new List<WhatIfPipelineRow>();
        foreach (var payment in payments.OrderBy(p => p.PaymentNumber, StringComparer.Ordinal))
        {
            var holds = await _erp.GetOpenHoldsAsync(payment.PaymentNumber, ct).ConfigureAwait(false);
            foreach (var payLine in payment.Lines
                .OrderBy(l => l.VendorId, StringComparer.Ordinal)
                .ThenBy(l => l.SupplierInvoiceNumber, StringComparer.Ordinal))
            {
                var country = await GetCountryAsync(context, payLine.VendorId, ct).ConfigureAwait(false);
                var docPresent = await GetDocPresentAsync(context, payLine.VendorId, ct).ConfigureAwait(false);
                var invoice = await GetInvoiceAsync(
                    context, payLine.VendorId, payLine.SupplierInvoiceNumber, ct).ConfigureAwait(false);
                var po = invoice?.PoNumber is null
                    ? null
                    : await GetPoAsync(context, invoice.PoNumber, ct).ConfigureAwait(false);
                if (invoice is null || po is null)
                {
                    context.PaymentRowsWithoutJoin++;
                    context.FirstPaymentWithoutJoin ??= payment.PaymentNumber;
                    continue;
                }

                foreach (var invLine in invoice.Lines)
                {
                    var poLine = invLine.PoLineRef is null
                        ? null
                        : po.Lines.FirstOrDefault(l =>
                            string.Equals(l.PoLineRef, invLine.PoLineRef, StringComparison.Ordinal));
                    if (poLine is null)
                    {
                        context.PaymentRowsWithoutJoin++;
                        context.FirstPaymentWithoutJoin ??= payment.PaymentNumber;
                        continue;
                    }

                    var critical = await ResolveCriticalAsync(context, poLine.ItemId, ct).ConfigureAwait(false);
                    if (critical is null)
                    {
                        NoteUnknownItem(context, poLine.ItemId);
                        continue;
                    }

                    rows.Add(new WhatIfPipelineRow(
                        PaymentDocumentType, payment.PaymentNumber, poLine.ItemId, critical.Value,
                        payLine.VendorId, country, invLine.Quantity, invLine.UnitPrice, payment.Currency,
                        DocPresent: docPresent,
                        HasOpenHold: holds.Count > 0,
                        HasQuarantine: IsPoLineQuarantined(context, po.PoNumber, poLine)));
                }
            }
        }

        return new PipelineSeamResult(rows, null);
    }

    /// <summary>ASN rows: one per carton over the unreceived-ASN list read.
    /// Vendor, price, and currency join through the ASN's PO; when the PO
    /// does not resolve the row still carries the real carton facts (item,
    /// units) with a null vendor and price. The quarantine flag reads the
    /// carton's own lot directly. On an ERP layer without the read, empty
    /// with the coverage reason.</summary>
    protected virtual async Task<PipelineSeamResult> ListOpenAsnRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        if (context.AsnReadMissing)
        {
            return new PipelineSeamResult([], UnreceivedAsnFallbackCaveat);
        }

        var rows = new List<WhatIfPipelineRow>();
        foreach (var asn in context.UnreceivedAsns.OrderBy(a => a.AsnNumber, StringComparer.Ordinal))
        {
            var po = await GetPoAsync(context, asn.PoNumber, ct).ConfigureAwait(false);
            var country = po is null
                ? null
                : await GetCountryAsync(context, po.VendorId, ct).ConfigureAwait(false);
            var docPresent = po is null
                ? false
                : await GetDocPresentAsync(context, po.VendorId, ct).ConfigureAwait(false);
            var holds = await _erp.GetOpenHoldsAsync(asn.AsnNumber, ct).ConfigureAwait(false);
            foreach (var carton in asn.Cartons.OrderBy(c => c.CartonId, StringComparer.Ordinal))
            {
                var critical = await ResolveCriticalAsync(context, carton.ItemId, ct).ConfigureAwait(false);
                if (critical is null)
                {
                    NoteUnknownItem(context, carton.ItemId);
                    continue;
                }

                var poLine = po?.Lines
                    .Where(l => string.Equals(l.ItemId, carton.ItemId, StringComparison.Ordinal))
                    .OrderBy(l => l.PoLineRef, StringComparer.Ordinal)
                    .FirstOrDefault();
                rows.Add(new WhatIfPipelineRow(
                    AsnDocumentType, asn.AsnNumber, carton.ItemId, critical.Value,
                    po?.VendorId, country, carton.UnitsPerCarton, poLine?.UnitPrice, po?.Currency,
                    DocPresent: docPresent,
                    HasOpenHold: holds.Count > 0,
                    HasQuarantine: context.QuarantinedLots.Contains(carton.LotRef)));
            }
        }

        return new PipelineSeamResult(rows, null);
    }

    /// <summary>Seam: receipts with open holds. No adapter list-query reads
    /// receipts; returns empty with the coverage reason.</summary>
    protected virtual Task<PipelineSeamResult> ListHeldReceiptRowsAsync(
        WhatIfGatherContext context, CancellationToken ct)
    {
        _ = context;
        _ = ct;
        return Task.FromResult(new PipelineSeamResult([], HeldReceiptsCaveat));
    }

    /// <summary>Invoice pipeline identity: vendor plus supplier number, the
    /// same composite the ERP uses for invoice approvals.</summary>
    public static string InvoiceDocumentNumber(VendorInvoice invoice) =>
        $"{invoice.VendorId}/{invoice.SupplierInvoiceNumber}";

    /// <summary>Builds the new-side what-if config from the proposed rule.
    /// Refuses NotMappable proposals (their report is "not computed").</summary>
    public virtual WhatIfRuleConfig BuildNewConfig(RuleProposal proposal)
    {
        if (proposal.ProposedRule is null)
        {
            throw new InvalidOperationException(
                $"Memo {proposal.MemoId} is NotMappable: the what-if report is not computed (no mappable rule).");
        }

        return new WhatIfRuleConfig(
            proposal.ProposedRule.RuleType,
            new Dictionary<string, string>(proposal.ProposedRule.Params, StringComparer.Ordinal));
    }

    /// <summary>Builds the old-side what-if config: share-cap types read
    /// their live params from the KB sourcing policy (which mirrors the pack
    /// config); every other type has no agent-readable live baseline, so old
    /// verdicts are Pass with a recorded caveat when conflicts exist.</summary>
    public virtual (WhatIfRuleConfig? OldConfig, string? OldRuleId, string? Caveat) BuildOldConfig(
        RuleProposal proposal, ProposalValidation validation, KbSnapshot snapshot)
    {
        if (proposal.ProposedRule is null)
        {
            return (null, null, null);
        }

        if (ShareCapPolicyIds.TryGetValue(proposal.ProposedRule.RuleType, out var policyId))
        {
            // No conflict names a live rule: the engine sees nothing live to
            // compare against, so the old side is honestly absent.
            var liveRuleId = validation.Conflicts.FirstOrDefault()?.RuleId;
            if (liveRuleId is null)
            {
                return (null, null, null);
            }

            var policy = snapshot.SourcingPolicies.FirstOrDefault(p =>
                string.Equals(p.Id, policyId, StringComparison.Ordinal));
            if (policy is null)
            {
                throw new InvalidOperationException(
                    $"Memo {proposal.MemoId} what-if is refused: KB sourcing policy '{policyId}' behind " +
                    $"{proposal.ProposedRule.RuleType} is absent from snapshot {snapshot.KbVersion}; " +
                    "an honest old-vs-new comparison needs it.");
            }

            return (new WhatIfRuleConfig(
                    proposal.ProposedRule.RuleType,
                    new Dictionary<string, string>(policy.Params, StringComparer.Ordinal)),
                liveRuleId, null);
        }

        if (validation.Conflicts.Count == 0)
        {
            return (null, null, null);
        }

        return (null, null,
            $"What-if baseline caveat: the proposal conflicts with {string.Join(", ", validation.Conflicts.Select(c => c.RuleId))} " +
            "but no agent-readable live baseline exists for rule type " +
            $"{proposal.ProposedRule.RuleType}; old verdicts evaluate as Pass, so block-to-pass flips are not computed.");
    }

    private async Task<string?> GetCountryAsync(
        WhatIfGatherContext context, string vendorId, CancellationToken ct)
    {
        if (!context.VendorCountry.TryGetValue(vendorId, out var country))
        {
            country = await _erp.GetVendorCountryAsync(vendorId, ct).ConfigureAwait(false);
            context.VendorCountry[vendorId] = country;
        }

        return country;
    }

    private async Task<bool> GetDocPresentAsync(
        WhatIfGatherContext context, string vendorId, CancellationToken ct)
    {
        if (context.DocKind is null)
        {
            return false;
        }

        if (!context.VendorDocs.TryGetValue(vendorId, out var docs))
        {
            docs = await _erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            context.VendorDocs[vendorId] = docs;
        }

        return docs.Any(d =>
            string.Equals(d.Kind, context.DocKind, StringComparison.Ordinal) && d.Present);
    }

    private async Task<PurchaseOrder?> GetPoAsync(
        WhatIfGatherContext context, string poNumber, CancellationToken ct)
    {
        if (!context.PurchaseOrders.TryGetValue(poNumber, out var po))
        {
            po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            context.PurchaseOrders[poNumber] = po;
        }

        return po;
    }

    private async Task<VendorInvoice?> GetInvoiceAsync(
        WhatIfGatherContext context, string vendorId, string supplierInvoiceNumber, CancellationToken ct)
    {
        var key = (vendorId, supplierInvoiceNumber);
        if (!context.Invoices.TryGetValue(key, out var invoice))
        {
            invoice = await _erp.GetInvoiceAsync(vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false);
            context.Invoices[key] = invoice;
        }

        return invoice;
    }

    /// <summary>Resolves an item's critical flag: the pinned KB snapshot
    /// first (deterministic on the proposal's pin), the ERP item master as
    /// fallback. Null when neither knows the item; the caller skips the row
    /// and the miss lands in a conditional caveat.</summary>
    private async Task<bool?> ResolveCriticalAsync(
        WhatIfGatherContext context, string itemId, CancellationToken ct)
    {
        if (context.KbCritical.TryGetValue(itemId, out var critical))
        {
            return critical;
        }

        if (!context.Items.TryGetValue(itemId, out var item))
        {
            item = await _erp.GetItemAsync(itemId, ct).ConfigureAwait(false);
            context.Items[itemId] = item;
        }

        return item?.IsCritical;
    }

    private static bool IsPoLineQuarantined(
        WhatIfGatherContext context, string poNumber, PurchaseOrderLine line) =>
        line.QtyQuarantined > 0
        || context.QuarantinedPoLines.Contains((poNumber, line.PoLineRef));

    private static void NoteUnknownItem(WhatIfGatherContext context, string itemId)
    {
        context.UnknownItemRows++;
        context.FirstUnknownItem ??= itemId;
    }

    private static void AddJoinCaveats(WhatIfGatherContext context, List<string> caveats)
    {
        if (context.InvoiceRowsWithoutPoJoin > 0)
        {
            caveats.Add(
                $"{context.InvoiceRowsWithoutPoJoin} invoice row(s) are absent from the pipeline window: " +
                $"their lines name no resolvable PO line (first: {context.FirstInvoiceWithoutPoJoin}). " +
                "Item, quantity, and price joins need the PO line.");
        }

        if (context.PaymentRowsWithoutJoin > 0)
        {
            caveats.Add(
                $"{context.PaymentRowsWithoutJoin} payment row(s) are absent from the pipeline window: " +
                $"their invoices name no resolvable invoice or PO line (first: {context.FirstPaymentWithoutJoin}). " +
                "Item, quantity, and price joins need the invoice and PO lines.");
        }

        if (context.UnknownItemRows > 0)
        {
            caveats.Add(
                $"{context.UnknownItemRows} pipeline row(s) are absent from the pipeline window: " +
                $"their items are in neither the pinned KB snapshot nor the ERP item master (first: {context.FirstUnknownItem}). " +
                "Criticality cannot be evaluated without the item.");
        }
    }
}
