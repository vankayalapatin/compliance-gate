// Company-data baseline: enters the full company record once through ERP
// services, dated in the past, done by named people. Masters go in raw
// (no adapter surface exists for them); vendors, items, and every document
// go through adapter writes so all ERP validation is enforced. All company
// values below are the company record from docs/COMPANY-DATA.md; the code
// computes every derived value (document dates from the monthly rhythm,
// cartons from quantities, dues from terms, journals from receipts).
using System.Globalization;
using Gate.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp.Baseline;

public sealed record VendorBaseline(string VendorId, decimal Cells, decimal Spend);

public sealed record BaselineReport(
    IReadOnlyList<VendorBaseline> MonthlyVendors,
    decimal TrailingTotal,
    decimal TrailingCn,
    decimal TrailingCnPct,
    IReadOnlyDictionary<string, decimal> TrailingByCountry,
    decimal PaidToDate,
    decimal OpenPayables,
    decimal Corp02CnCells,
    decimal Corp02Total,
    decimal Corp02CnPct,
    decimal Corp02MxCells,
    decimal Corp02MxPct,
    IReadOnlyDictionary<string, int> DocCounts,
    IReadOnlyDictionary<string, long> Sequences,
    IReadOnlyDictionary<string, decimal> OnHand);

public static class BaselineBuilder
{
    private const string Currency = "USD";
    private const string Terms = "NET30";
    private const string Maya = "Maya Planner";
    private const string Dana = "Dana Director";
    private const string Del = "Del Delegate";
    private const string Amy = "Amy AP Clerk";
    private const string Finn = "Finn Finance";
    private const decimal Corp02Lot = 1200m;

    private sealed record VendorSpec(
        string ExpectedId, string Name, string Country, string Email,
        string Bank, string Item, decimal Price, string Short, string? DocSuffix);

    private static readonly VendorSpec[] Vendors =
    [
        new("VND-1001", "CinderVolt Energy Ltd", "CN", "sales@cindervolt.example",
            "BANK-CV-001", "BAT-1", 14.20m, "CV", "a"),
        new("VND-1002", "Meridian Cellworks Inc", "MX", "sales@meridian.example",
            "BANK-MC-001", "BAT-1", 18.50m, "MC", "b"),
        new("VND-1003", "Formosa Fasteners Co", "TW", "sales@formosa.example",
            "BANK-FF-001", "SCR-9", 0.40m, "FF", null),
        new("VND-1004", "DragonLeaf Cells Co Ltd", "CN", "sales@dragonleaf.example",
            "BANK-DL-001", "BAT-1", 14.60m, "DL", "d"),
        new("VND-1005", "ABC Parts", "US", "sales@abc.example",
            "BANK-AB-001", "BAT-1", 19.75m, "AB", "ab"),
    ];

    private static string Noon(DateOnly day) =>
        day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static string DayText(DateOnly day) =>
        day.ToString("O", CultureInfo.InvariantCulture);

    public static async Task<BaselineReport> BuildAsync(ErpDbContext db, IErpAdapter erp, CancellationToken ct = default)
    {
        var build = new Build(db, erp, ct);
        await build.RunAsync().ConfigureAwait(false);
        return await build.ReportAsync().ConfigureAwait(false);
    }

    private sealed class Build
    {
        private readonly ErpDbContext _db;
        private readonly IErpAdapter _erp;
        private readonly CancellationToken _ct;
        private int _chain = 1001;
        private int _run = 1001;
        private int _key;

        internal Build(ErpDbContext db, IErpAdapter erp, CancellationToken ct)
        {
            _db = db;
            _erp = erp;
            _ct = ct;
        }

        private string Key(string what) => $"baseline-{what}-{++_key}";

        internal async Task RunAsync()
        {
            SeedSequences();
            await SeedMastersAsync().ConfigureAwait(false);
            await SeedVendorsAsync().ConfigureAwait(false);

            // October 2025 closed chains (outside the trailing window).
            await FullChainAsync(Vendors[1], "BAT-1", 50m, new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 9),
                new DateOnly(2025, 10, 10), new DateOnly(2025, 10, 11), new DateOnly(2025, 10, 12),
                new DateOnly(2025, 10, 16), new DateOnly(2025, 10, 17),
                "MC-2025-10-05", new DateOnly(2025, 10, 5), Dana,
                new DateOnly(2025, 10, 24), new DateOnly(2025, 10, 25)).ConfigureAwait(false);
            await FullChainAsync(Vendors[1], "BAT-1", 40m, new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 9),
                new DateOnly(2025, 10, 10), new DateOnly(2025, 10, 11), new DateOnly(2025, 10, 12),
                new DateOnly(2025, 10, 17), new DateOnly(2025, 10, 18),
                "MC-2025-10-12", new DateOnly(2025, 10, 12), Dana,
                new DateOnly(2025, 10, 24), new DateOnly(2025, 10, 25)).ConfigureAwait(false);
            await FullChainAsync(Vendors[2], "SCR-9", 200m, new DateOnly(2025, 10, 8), new DateOnly(2025, 10, 9),
                new DateOnly(2025, 10, 10), new DateOnly(2025, 10, 11), new DateOnly(2025, 10, 12),
                new DateOnly(2025, 10, 17), new DateOnly(2025, 10, 18),
                "FF-2025-10-19", new DateOnly(2025, 10, 19), Dana,
                new DateOnly(2025, 10, 24), new DateOnly(2025, 10, 25)).ConfigureAwait(false);

            // Monthly chains, November 2025 through October 2026.
            for (var m = 0; m < 12; m++)
            {
                var first = new DateOnly(2025, 11, 1).AddMonths(m);
                var year = first.Year;
                var month = first.Month;
                var oct = month == 10 && year == 2026;
                var invoiceApprover = oct ? Del : Dana;
                var cvQty = oct ? 287m : 283m;
                var mcQty = oct ? 313m : 317m;
                await RhythmChainAsync(Vendors[0], "BAT-1", cvQty, year, month, invoiceApprover).ConfigureAwait(false);
                await RhythmChainAsync(Vendors[1], "BAT-1", mcQty, year, month, invoiceApprover).ConfigureAwait(false);
                if ((month == 11 && year == 2025) || (month == 2 && year == 2026) || (month == 6 && year == 2026))
                {
                    var dlQty = month == 11 ? 800m : 850m;
                    await RhythmChainAsync(Vendors[3], "BAT-1", dlQty, year, month, Dana).ConfigureAwait(false);
                }
                if ((month == 2 || month == 5 || month == 8) && year == 2026)
                {
                    await RhythmChainAsync(Vendors[4], "BAT-1", 100m, year, month, Dana).ConfigureAwait(false);
                }
                if (oct)
                {
                    await FullChainAsync(Vendors[2], "SCR-9", 2500m, new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4),
                        new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7),
                        new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 12),
                        "FF-2026-10-15", new DateOnly(2026, 10, 15), Del,
                        new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 25)).ConfigureAwait(false);
                }
            }

            // Inbound cover: open PO, no ASN, no receipt, no invoice.
            await InboundChainAsync().ConfigureAwait(false);

            // DragonLeaf hold: after its June delivery, before its July payment.
            await _erp.BlockVendorAsync(Vendors[3].ExpectedId, "Supplier agreement ended", Dana, Key("hold-dl"), _ct).ConfigureAwait(false);
            var dl = await _db.Vendors.FindAsync([Vendors[3].ExpectedId], _ct).ConfigureAwait(false);
            if (dl is not null) dl.BlockedAt = Noon(new DateOnly(2026, 7, 1));
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);

            await PaymentRunsAsync().ConfigureAwait(false);
            await JournalsAsync().ConfigureAwait(false);
        }

        private void SeedSequences()
        {
            foreach (var prefix in new[] { "SO", "PR", "PO", "ASN", "RCT", "V", "PAYDRAFT", "PAY", "VND", "HLD" })
            {
                _db.Sequences.Add(new SequenceRow { Name = prefix, Next = 1001, Format = null, Padding = 4 });
            }
            _db.SaveChanges();
        }

        private async Task SeedMastersAsync()
        {
            _db.CompanyInfo.Add(new CompanyInfoRow
            {
                EntityCode = "LE-1",
                LegalName = "Lumenfield Eyewear Inc.",
                Currency = Currency,
                BillToAddress = "1000 Assembly Way, Suite 200, Reno, NV 89501",
                ShipToAddress = "Dock 4, 1000 Assembly Way, Reno, NV 89501",
                SenderEmail = "purchasing@lumenfield.example"
            });
            _db.Sites.Add(new SiteRow
            {
                SiteId = "SITE-DOCK-1",
                Name = "Dock 1",
                Address = "Dock 4, 1000 Assembly Way, Reno, NV 89501",
                Capabilities = "dock-camera"
            });
            _db.Config.Add(new ConfigRow { Key = "base-currency", Value = Currency });
            _db.Config.Add(new ConfigRow { Key = "auto-approve-threshold", Value = "500" });
            _db.Config.Add(new ConfigRow { Key = "default-payment-terms", Value = Terms });
            _db.PaymentTerms.Add(new PaymentTermsRow { TermsId = Terms, NetDays = 30 });
            _db.Users.Add(new UserRow { UserId = Maya, Role = "requester", SigningLimit = 5000m, LimitCurrency = Currency });
            _db.Users.Add(new UserRow { UserId = Dana, Role = "approver", SigningLimit = 50000m, LimitCurrency = Currency });
            _db.Users.Add(new UserRow { UserId = Del, Role = "delegate", SigningLimit = 50000m, LimitCurrency = Currency });
            _db.Users.Add(new UserRow { UserId = Amy, Role = "ap-clerk", SigningLimit = 0m, LimitCurrency = Currency });
            _db.Users.Add(new UserRow { UserId = Finn, Role = "payment-approver", SigningLimit = 100000m, LimitCurrency = Currency });
            _db.Delegations.Add(new DelegationRow
            {
                RowId = Guid.NewGuid().ToString("N"), FromUser = Dana, ToUser = Del,
                Start = DayText(new DateOnly(2026, 10, 20)), End = DayText(new DateOnly(2026, 11, 10))
            });
            _db.BomLines.Add(new BomRow { ItemId = "GLASS-1", ComponentItemId = "BAT-1", QtyPer = 1m });
            _db.BomLines.Add(new BomRow { ItemId = "GLASS-1", ComponentItemId = "SCR-9", QtyPer = 2m });
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);

            await _erp.CreateItemAsync("BAT-1", "Battery cell", "EA", true, 100,
                RecordSource.Human, Maya, Key("item-bat"), _ct).ConfigureAwait(false);
            await _erp.CreateItemAsync("SCR-9", "Screw", "EA", false, 100,
                RecordSource.Human, Maya, Key("item-scr"), _ct).ConfigureAwait(false);
            await _erp.CreateItemAsync("GLASS-1", "Smart glasses", "EA", false, 1,
                RecordSource.Human, Maya, Key("item-glass"), _ct).ConfigureAwait(false);
        }

        private async Task SeedVendorsAsync()
        {
            foreach (var spec in Vendors)
            {
                var vendor = await _erp.CreateVendorAsync(null, spec.Name, spec.Country, Terms,
                    spec.Email, RecordSource.Human, Maya, Key($"vendor-{spec.Short}"), _ct).ConfigureAwait(false);
                if (!string.Equals(vendor.VendorId, spec.ExpectedId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Vendor mint drift: expected {spec.ExpectedId}, minted {vendor.VendorId}.");
                if (spec.DocSuffix is not null)
                {
                    foreach (var (kind, stem) in new[] { ("UN38.3", "un383"), ("SDS", "sds"), ("RoHS", "rohs"), ("REACH", "reach") })
                    {
                        _db.VendorDocuments.Add(new VendorDocumentRow
                        {
                            RowId = Guid.NewGuid().ToString("N"), VendorId = vendor.VendorId,
                            Kind = kind, Reference = $"{stem}-{spec.DocSuffix}", Present = true
                        });
                    }
                }
                var verified = string.Equals(spec.ExpectedId, "VND-1005", StringComparison.Ordinal)
                    ? new DateOnly(2026, 1, 15)
                    : new DateOnly(2025, 10, 30);
                _db.VendorBanks.Add(new VendorBankRow
                {
                    VendorId = vendor.VendorId, AccountRef = spec.Bank, Verified = true, VerifiedAt = Noon(verified)
                });
                _db.VendorPrices.Add(new VendorPriceRow
                {
                    VendorId = vendor.VendorId, ItemId = spec.Item, UnitPrice = spec.Price
                });
            }
            _db.LastPrices.Add(new LastPriceRow { ItemId = "BAT-1", UnitPrice = 14.20m });
            _db.LastPrices.Add(new LastPriceRow { ItemId = "SCR-9", UnitPrice = 0.40m });
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private Task RhythmChainAsync(VendorSpec vendor, string itemId, decimal qty, int year, int month, string invoiceApprover)
        {
            var supplierInvoice = $"{vendor.Short}-{year}-{month:00}-20";
            return FullChainAsync(vendor, itemId, qty,
                new DateOnly(year, month, 3), new DateOnly(year, month, 4),
                new DateOnly(year, month, 5), new DateOnly(year, month, 6),
                new DateOnly(year, month, 7), new DateOnly(year, month, 18),
                new DateOnly(year, month, 19), supplierInvoice, new DateOnly(year, month, 20),
                invoiceApprover, new DateOnly(year, month, 24), new DateOnly(year, month, 25));
        }

        private async Task FullChainAsync(
            VendorSpec vendor, string itemId, decimal qty,
            DateOnly prDay, DateOnly prApprDay, DateOnly poDay, DateOnly poApprDay, DateOnly ackDay,
            DateOnly asnDay, DateOnly receiptDay, string supplierInvoice, DateOnly invoiceDay,
            string invoiceApprover, DateOnly invoiceApprDay, DateOnly postDay)
        {
            var seq = _chain;
            var stamp = $"{seq:0000}";
            var pr = await _erp.CreatePrAsync(Maya, RecordSource.Human, Maya,
                [new PurchaseRequisitionLine(itemId, qty, "EA")], Key($"pr-{stamp}"), _ct).ConfigureAwait(false);
            ExpectNumber("PR", pr.PrNumber, seq);
            var prApproved = pr.Status == PrStatus.Draft;
            if (prApproved)
            {
                await _erp.SubmitForApprovalAsync("PR", pr.PrNumber, Dana, Maya, Key($"prsub-{stamp}"), _ct).ConfigureAwait(false);
                await _erp.RecordApprovalAsync("PR", pr.PrNumber, Dana, true, null, Key($"prappr-{stamp}"), _ct).ConfigureAwait(false);
                await StampApprovalRowAsync("PR", pr.PrNumber, prApprDay).ConfigureAwait(false);
            }
            else
            {
                await StampApprovalRowAsync("PR", pr.PrNumber, prDay).ConfigureAwait(false);
            }
            await StampPrAsync(pr.PrNumber, prDay, prApproved ? Dana : ErpApprovalActors.AutoApprovePolicy, prApproved ? prApprDay : prDay).ConfigureAwait(false);
            var po = await _erp.CreatePoAsync(pr.PrNumber, vendor.ExpectedId, RecordSource.Human, Maya, Key($"po-{stamp}"), ct: _ct).ConfigureAwait(false);
            ExpectNumber("PO", po.PoNumber, seq);
            var poApproved = po.ApprovalStatus == PoApprovalStatus.Draft;
            if (poApproved)
            {
                await _erp.SubmitForApprovalAsync("PO", po.PoNumber, Dana, Maya, Key($"posub-{stamp}"), _ct).ConfigureAwait(false);
                await _erp.RecordApprovalAsync("PO", po.PoNumber, Dana, true, null, Key($"poappr-{stamp}"), _ct).ConfigureAwait(false);
                await StampApprovalRowAsync("PO", po.PoNumber, poApprDay).ConfigureAwait(false);
            }
            else
            {
                await StampApprovalRowAsync("PO", po.PoNumber, poDay).ConfigureAwait(false);
            }
            await StampPoAsync(po.PoNumber, poDay, poApproved ? Dana : ErpApprovalActors.AutoApprovePolicy, poApproved ? poApprDay : poDay).ConfigureAwait(false);
            var lineRef = po.Lines[0].PoLineRef;
            var unitPrice = po.Lines[0].UnitPrice;
            await _erp.RecordVendorConfirmationAsync(po.PoNumber, ackDay, RecordSource.Vendor, Maya,
                [new VendorConfirmLine(lineRef, qty, null)], Key($"ack-{stamp}"), _ct).ConfigureAwait(false);

            var dateTag = $"{receiptDay:yyyyMMdd}";
            var cartons = SplitCartons(vendor.Short, itemId, qty, dateTag);
            var asn = await _erp.CreateAsnAsync(null, po.PoNumber, RecordSource.Vendor, Maya, cartons, Key($"asn-{stamp}"), _ct).ConfigureAwait(false);
            ExpectNumber("ASN", asn.AsnNumber, seq);
            var lot = $"{vendor.Short}-LOT-{dateTag}";
            var receipt = await _erp.PostReceiptAsync(po.PoNumber, $"{vendor.Short}-PS-{dateTag}",
                RecordSource.Human, Maya, [new ReceiptLine(lineRef, qty, 0, 0, lot, null)],
                Key($"rct-{stamp}"), receiptDay, _ct).ConfigureAwait(false);
            ExpectNumber("RCT", receipt.ReceiptNumber, seq);
            await StampReceiptAsync(receipt.ReceiptNumber, receiptDay).ConfigureAwait(false);

            _ = await _erp.CreateInvoiceAsync(supplierInvoice, vendor.ExpectedId, po.PoNumber,
                RecordSource.Vendor, Amy, [new InvoiceLineInput(lineRef, qty, unitPrice)],
                Key($"inv-{stamp}"), invoiceDay, _ct).ConfigureAwait(false);
            await _erp.RecordMatchResultAsync(vendor.ExpectedId, supplierInvoice,
                [new LineMatchResult(lineRef, true, "Matched to posted receipt.")], Key($"match-{stamp}"), _ct).ConfigureAwait(false);
            var invKey = $"{vendor.ExpectedId}/{supplierInvoice}";
            await _erp.SubmitForApprovalAsync("Invoice", invKey, Dana, Amy, Key($"invsub-{stamp}"), _ct).ConfigureAwait(false);
            await _erp.RecordApprovalAsync("Invoice", invKey, invoiceApprover, true, null, Key($"invappr-{stamp}"), _ct).ConfigureAwait(false);
            await StampApprovalRowAsync("Invoice", invKey, invoiceApprDay).ConfigureAwait(false);
            var posted = await _erp.PostInvoiceAsync(vendor.ExpectedId, supplierInvoice, Amy, Key($"post-{stamp}"), postDay, _ct).ConfigureAwait(false);
            ExpectNumber("V", posted.Voucher ?? string.Empty, seq);
            await StampInvoiceAsync(vendor.ExpectedId, supplierInvoice, invoiceDay, invoiceApprover, invoiceApprDay, postDay).ConfigureAwait(false);
            _chain++;
        }

        private async Task InboundChainAsync()
        {
            var seq = _chain;
            var stamp = $"{seq:0000}";
            var vendor = Vendors[1];
            var pr = await _erp.CreatePrAsync(Maya, RecordSource.Human, Maya,
                [new PurchaseRequisitionLine("BAT-1", 40m, "EA")], Key($"pr-{stamp}"), _ct).ConfigureAwait(false);
            ExpectNumber("PR", pr.PrNumber, seq);
            await _erp.SubmitForApprovalAsync("PR", pr.PrNumber, Dana, Maya, Key($"prsub-{stamp}"), _ct).ConfigureAwait(false);
            await _erp.RecordApprovalAsync("PR", pr.PrNumber, Dana, true, null, Key($"prappr-{stamp}"), _ct).ConfigureAwait(false);
            await StampApprovalRowAsync("PR", pr.PrNumber, new DateOnly(2026, 10, 15)).ConfigureAwait(false);
            await StampPrAsync(pr.PrNumber, new DateOnly(2026, 10, 14), Dana, new DateOnly(2026, 10, 15)).ConfigureAwait(false);
            var po = await _erp.CreatePoAsync(pr.PrNumber, vendor.ExpectedId, RecordSource.Human, Maya, Key($"po-{stamp}"), ct: _ct).ConfigureAwait(false);
            ExpectNumber("PO", po.PoNumber, seq);
            await _erp.SubmitForApprovalAsync("PO", po.PoNumber, Dana, Maya, Key($"posub-{stamp}"), _ct).ConfigureAwait(false);
            await _erp.RecordApprovalAsync("PO", po.PoNumber, Dana, true, null, Key($"poappr-{stamp}"), _ct).ConfigureAwait(false);
            await StampApprovalRowAsync("PO", po.PoNumber, new DateOnly(2026, 10, 17)).ConfigureAwait(false);
            await StampPoAsync(po.PoNumber, new DateOnly(2026, 10, 16), Dana, new DateOnly(2026, 10, 17)).ConfigureAwait(false);
            await _erp.RecordVendorConfirmationAsync(po.PoNumber, new DateOnly(2026, 10, 18), RecordSource.Vendor, Maya,
                [new VendorConfirmLine(po.Lines[0].PoLineRef, 40m, null)], Key($"ack-{stamp}"), _ct).ConfigureAwait(false);
            await _erp.AddPoNoteAsync(po.PoNumber, "Delivery due November 15.", Maya, Key($"note-{stamp}"), _ct).ConfigureAwait(false);
            var note = await _db.PurchaseOrderNotes
                .Where(n => n.PoNumber == po.PoNumber).OrderBy(n => n.RowId).FirstAsync(_ct).ConfigureAwait(false);
            note.CreatedAt = Noon(new DateOnly(2026, 10, 18));
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
            _chain++;
        }

        private async Task PaymentRunsAsync()
        {
            var mc = Vendors[1].ExpectedId;
            var ff = Vendors[2].ExpectedId;
            var cv = Vendors[0].ExpectedId;
            var dl = Vendors[3].ExpectedId;
            var ab = Vendors[4].ExpectedId;
            string Inv(string shortCode, int year, int month, int day) => $"{shortCode}-{year}-{month:00}-{day:00}";
            var runs = new (DateOnly RunDay, InvoiceKey[] Keys)[]
            {
                (new DateOnly(2025, 11, 28), [new(mc, "MC-2025-10-05"), new(mc, "MC-2025-10-12"), new(ff, "FF-2025-10-19")]),
                (new DateOnly(2025, 12, 10), [new(cv, Inv("CV", 2025, 11, 20)), new(mc, Inv("MC", 2025, 11, 20)), new(dl, Inv("DL", 2025, 11, 20))]),
                (new DateOnly(2026, 1, 10), [new(cv, Inv("CV", 2025, 12, 20)), new(mc, Inv("MC", 2025, 12, 20))]),
                (new DateOnly(2026, 2, 10), [new(cv, Inv("CV", 2026, 1, 20)), new(mc, Inv("MC", 2026, 1, 20))]),
                (new DateOnly(2026, 3, 10), [new(cv, Inv("CV", 2026, 2, 20)), new(mc, Inv("MC", 2026, 2, 20)), new(dl, Inv("DL", 2026, 2, 20)), new(ab, Inv("AB", 2026, 2, 20))]),
                (new DateOnly(2026, 4, 10), [new(cv, Inv("CV", 2026, 3, 20)), new(mc, Inv("MC", 2026, 3, 20))]),
                (new DateOnly(2026, 5, 10), [new(cv, Inv("CV", 2026, 4, 20)), new(mc, Inv("MC", 2026, 4, 20))]),
                (new DateOnly(2026, 6, 10), [new(cv, Inv("CV", 2026, 5, 20)), new(mc, Inv("MC", 2026, 5, 20)), new(ab, Inv("AB", 2026, 5, 20))]),
                (new DateOnly(2026, 7, 10), [new(cv, Inv("CV", 2026, 6, 20)), new(mc, Inv("MC", 2026, 6, 20)), new(dl, Inv("DL", 2026, 6, 20))]),
                (new DateOnly(2026, 8, 10), [new(cv, Inv("CV", 2026, 7, 20)), new(mc, Inv("MC", 2026, 7, 20))]),
                (new DateOnly(2026, 9, 10), [new(cv, Inv("CV", 2026, 8, 20)), new(mc, Inv("MC", 2026, 8, 20)), new(ab, Inv("AB", 2026, 8, 20))]),
                (new DateOnly(2026, 10, 28), [new(cv, Inv("CV", 2026, 9, 20)), new(mc, Inv("MC", 2026, 9, 20)), new(ff, "FF-2026-10-15")]),
            };
            foreach (var (runDay, keys) in runs)
            {
                var stamp = $"{_run:0000}";
                var draft = await _erp.CreatePaymentDraftAsync(keys, RecordSource.Human, Amy, Key($"draft-{stamp}"), runDay, _ct).ConfigureAwait(false);
                ExpectNumber("PAYDRAFT", draft.PaymentNumber, _run);
                await _erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, Finn, Amy, Key($"paysub-{stamp}"), _ct).ConfigureAwait(false);
                await _erp.RecordApprovalAsync("Payment", draft.PaymentNumber, Finn, true, null, Key($"payappr-{stamp}"), _ct).ConfigureAwait(false);
                await StampApprovalRowAsync("Payment", draft.PaymentNumber, runDay).ConfigureAwait(false);
                var posted = await _erp.PostPaymentAsync(draft.PaymentNumber, Finn, Key($"paypost-{stamp}"), runDay, _ct).ConfigureAwait(false);
                ExpectNumber("PAY", posted.PaymentNumber, _run);
                var payRow = await _db.Payments.FindAsync([posted.PaymentNumber], _ct).ConfigureAwait(false);
                if (payRow is not null)
                {
                    payRow.CreatedAt = Noon(runDay);
                    payRow.ApprovedBy = Finn;
                    payRow.ApprovedAt = Noon(runDay);
                    payRow.PostedBy = Finn;
                    payRow.PostedAt = Noon(runDay);
                }
                await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
                _run++;
            }
        }

        private async Task JournalsAsync()
        {
            // Monthly production consumption: each month consumes that month's
            // BAT-1 receipts except October 2026, which consumes 540 of 600
            // (all of the CinderVolt lot, 253 of the Meridian lot, leaving 60
            // on hand). October 2025 also consumes its SCR-9 receipt.
            for (var m = 0; m < 13; m++)
            {
                var first = new DateOnly(2025, 10, 1).AddMonths(m);
                var monthEnd = new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
                var monthName = first.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
                var journalId = $"CONS-{first:yyyyMM}-01";
                _db.InventoryJournals.Add(new InventoryJournalRow
                {
                    JournalId = journalId, JournalType = "consumption",
                    PostingDate = DayText(monthEnd),
                    Reason = $"Monthly production consumption for {monthName}.",
                    CreatedBy = Maya, CreatedAt = Noon(monthEnd)
                });
                var lineNo = 0;
                var from = DayText(first);
                var to = DayText(monthEnd);
                var receipts = (await _db.ShipmentReceipts.OrderBy(r => r.ReceiptNumber).ToListAsync(_ct).ConfigureAwait(false))
                    .Where(r => r.PostingDate != null
                        && string.Compare(r.PostingDate, from, StringComparison.Ordinal) >= 0
                        && string.Compare(r.PostingDate, to, StringComparison.Ordinal) <= 0)
                    .ToList();
                foreach (var receipt in receipts)
                {
                    var lines = await _db.ShipmentReceiptLines
                        .Where(l => l.ReceiptNumber == receipt.ReceiptNumber).OrderBy(l => l.RowId).ToListAsync(_ct).ConfigureAwait(false);
                    foreach (var line in lines)
                    {
                        var lot = await _db.LotMap.FindAsync([line.LotRef], _ct).ConfigureAwait(false);
                        if (lot is null) continue;
                        if (string.Equals(lot.ItemId, "SCR-9", StringComparison.Ordinal)
                            && first != new DateOnly(2025, 10, 1))
                        {
                            continue;
                        }
                        var take = line.QtyReceived;
                        if (first == new DateOnly(2026, 10, 1)
                            && string.Equals(lot.ItemId, "BAT-1", StringComparison.Ordinal)
                            && string.Equals(line.LotRef, "MC-LOT-20261019", StringComparison.Ordinal))
                        {
                            take = 253m;
                        }
                        if (take <= 0) continue;
                        lineNo++;
                        _db.InventoryJournalLines.Add(new InventoryJournalLineRow
                        {
                            RowId = Guid.NewGuid().ToString("N"), JournalId = journalId, LineNo = lineNo,
                            ItemId = lot.ItemId, LotRef = line.LotRef, Quantity = take
                        });
                        await ApplyConsumptionAsync(lot.ItemId, line.LotRef, journalId, take).ConfigureAwait(false);
                    }
                }
                await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
            }
        }

        private async Task ApplyConsumptionAsync(string itemId, string lotRef, string journalId, decimal take)
        {
            var available = await _db.InventoryTransactions
                .Where(t => t.ItemId == itemId && t.LotRef == lotRef && t.Status == nameof(InventoryStatus.Available))
                .OrderBy(t => t.RowId).ToListAsync(_ct).ConfigureAwait(false);
            var have = available.Sum(t => t.Quantity);
            if (take > have)
                throw new InvalidOperationException($"Journal {journalId} consumes {take} from lot {lotRef} holding {have}.");
            _db.InventoryTransactions.RemoveRange(available);
            if (have - take > 0)
            {
                _db.InventoryTransactions.Add(new InventoryRow
                {
                    RowId = Guid.NewGuid().ToString("N"), ItemId = itemId, ReceiptRef = available[0].ReceiptRef,
                    LotRef = lotRef, Status = nameof(InventoryStatus.Available), Quantity = have - take
                });
            }
            _db.InventoryTransactions.Add(new InventoryRow
            {
                RowId = Guid.NewGuid().ToString("N"), ItemId = itemId, ReceiptRef = journalId,
                LotRef = lotRef, Status = nameof(InventoryStatus.Consumed), Quantity = take
            });
        }

        private static List<AsnCarton> SplitCartons(string shortCode, string itemId, decimal qty, string dateTag)
        {
            var perCarton = 100;
            var whole = (int)qty;
            var cartons = new List<AsnCarton>();
            var n = 0;
            var left = whole;
            while (left > 0)
            {
                n++;
                var take = Math.Min(perCarton, left);
                left -= take;
                cartons.Add(new AsnCarton($"{shortCode}-C-{dateTag}-{n.ToString("00", CultureInfo.InvariantCulture)}", itemId, take, $"{shortCode}-LOT-{dateTag}"));
            }
            return cartons;
        }

        private static void ExpectNumber(string prefix, string number, int seq)
        {
            var want = $"{prefix}-{seq.ToString("0000", CultureInfo.InvariantCulture)}";
            if (!string.Equals(number, want, StringComparison.Ordinal))
                throw new InvalidOperationException($"Number drift: expected {want}, minted {number}.");
        }

        private async Task StampPrAsync(string prNumber, DateOnly created, string? approvedBy, DateOnly? approved)
        {
            var row = await _db.PurchaseRequisitions.FindAsync([prNumber], _ct).ConfigureAwait(false);
            if (row is null) return;
            row.CreatedAt = Noon(created);
            row.ApprovedBy = approvedBy;
            row.ApprovedAt = approved.HasValue ? Noon(approved.Value) : null;
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private async Task StampPoAsync(string poNumber, DateOnly created, string? approvedBy, DateOnly? approved)
        {
            var row = await _db.PurchaseOrders.FindAsync([poNumber], _ct).ConfigureAwait(false);
            if (row is null) return;
            row.CreatedAt = Noon(created);
            row.ApprovedBy = approvedBy;
            row.ApprovedAt = approved.HasValue ? Noon(approved.Value) : null;
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private async Task StampReceiptAsync(string receiptNumber, DateOnly posted)
        {
            var row = await _db.ShipmentReceipts.FindAsync([receiptNumber], _ct).ConfigureAwait(false);
            if (row is null) return;
            row.CreatedAt = Noon(posted);
            row.PostedBy = Maya;
            row.PostedAt = Noon(posted);
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private async Task StampInvoiceAsync(string vendorId, string supplierInvoice, DateOnly created, string approvedBy, DateOnly approved, DateOnly posted)
        {
            var row = await _db.VendorInvoices.FindAsync([vendorId, supplierInvoice], _ct).ConfigureAwait(false);
            if (row is null) return;
            row.CreatedAt = Noon(created);
            row.ApprovedBy = approvedBy;
            row.ApprovedAt = Noon(approved);
            row.PostedBy = Amy;
            row.PostedAt = Noon(posted);
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        private async Task StampApprovalRowAsync(string documentType, string documentNumber, DateOnly decided)
        {
            var row = await _db.Approvals
                .Where(a => a.DocumentType == documentType && a.DocumentNumber == documentNumber)
                .OrderBy(a => a.RowId).FirstAsync(_ct).ConfigureAwait(false);
            row.Timestamp = Noon(decided);
            await _db.SaveChangesAsync(_ct).ConfigureAwait(false);
        }

        internal async Task<BaselineReport> ReportAsync()
        {
            // The build mixes direct writes with adapter calls that commit on
            // their own contexts: drop everything this context tracked along
            // the way so the report reads committed state, not stale rows.
            _db.ChangeTracker.Clear();
            // Trailing twelve months at November 1 2026: November 2025
            // through October 2026. October 2025 chains sit outside by design.
            var windowStart = DayText(new DateOnly(2025, 11, 1));
            var windowEnd = DayText(new DateOnly(2026, 10, 31));
            var poByNumber = (await _db.PurchaseOrders.ToListAsync(_ct).ConfigureAwait(false))
                .ToDictionary(p => p.PoNumber, StringComparer.Ordinal);
            var vendorById = (await _db.Vendors.ToListAsync(_ct).ConfigureAwait(false))
                .ToDictionary(v => v.VendorId, StringComparer.Ordinal);
            var poLines = await _db.PurchaseOrderLines.ToListAsync(_ct).ConfigureAwait(false);
            var itemByPoLine = poLines
                .GroupBy(l => (l.PoNumber, l.PoLineRef))
                .ToDictionary(g => g.Key, g => g.First().ItemId);

            var monthlyPos = new HashSet<string>(StringComparer.Ordinal);
            var cells = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var trailingByCountry = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var receipt in await _db.ShipmentReceipts.ToListAsync(_ct).ConfigureAwait(false))
            {
                if (receipt.PostingDate is null
                    || string.Compare(receipt.PostingDate, windowStart, StringComparison.Ordinal) < 0
                    || string.Compare(receipt.PostingDate, windowEnd, StringComparison.Ordinal) > 0)
                {
                    continue;
                }
                monthlyPos.Add(receipt.PoNumber);
                if (!poByNumber.TryGetValue(receipt.PoNumber, out var po)) continue;
                var country = vendorById.TryGetValue(po.VendorId, out var vendor) ? vendor.CountryOfOrigin : "OTHER";
                var lines = await _db.ShipmentReceiptLines
                    .Where(l => l.ReceiptNumber == receipt.ReceiptNumber).ToListAsync(_ct).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (!itemByPoLine.TryGetValue((receipt.PoNumber, line.PoLineRef), out var itemId)) continue;
                    if (!string.Equals(itemId, "BAT-1", StringComparison.Ordinal)) continue;
                    cells[po.VendorId] = cells.GetValueOrDefault(po.VendorId) + line.QtyReceived;
                    trailingByCountry[country] = trailingByCountry.GetValueOrDefault(country) + line.QtyReceived;
                }
            }

            var spend = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var inv in await _db.VendorInvoices.ToListAsync(_ct).ConfigureAwait(false))
            {
                if (inv.PoNumber is null || !monthlyPos.Contains(inv.PoNumber)) continue;
                var lines = await _db.VendorInvoiceLines
                    .Where(l => l.VendorId == inv.VendorId && l.SupplierInvoiceNumber == inv.SupplierInvoiceNumber)
                    .ToListAsync(_ct).ConfigureAwait(false);
                spend[inv.VendorId] = spend.GetValueOrDefault(inv.VendorId) + lines.Sum(l => l.Quantity * l.UnitPrice);
            }

            var vendors = Vendors
                .Select(v => new VendorBaseline(v.ExpectedId,
                    cells.GetValueOrDefault(v.ExpectedId),
                    spend.GetValueOrDefault(v.ExpectedId)))
                .ToList();
            var trailingTotal = trailingByCountry.Values.Sum();
            var trailingCn = trailingByCountry.GetValueOrDefault("CN");
            var trailingMx = trailingByCountry.GetValueOrDefault("MX");
            var trailingPct = trailingTotal <= 0 ? 0m : Math.Round(100m * trailingCn / trailingTotal, 1);
            var corpTotal = trailingTotal + Corp02Lot;
            var corpCnPct = corpTotal <= 0 ? 0m : Math.Round(100m * (trailingCn + Corp02Lot) / corpTotal, 1);
            var corpMxPct = corpTotal <= 0 ? 0m : Math.Round(100m * (trailingMx + Corp02Lot) / corpTotal, 1);

            var payments = await _db.Payments.ToListAsync(_ct).ConfigureAwait(false);
            var paymentLines = await _db.PaymentLines.ToListAsync(_ct).ConfigureAwait(false);
            var paid = paymentLines
                .Where(l => payments.Any(p => string.Equals(p.PaymentNumber, l.PaymentNumber, StringComparison.Ordinal)
                    && string.Equals(p.Status, nameof(PaymentStatus.Posted), StringComparison.Ordinal)))
                .Sum(l => l.Amount);

            var open = 0m;
            foreach (var inv in await _db.VendorInvoices.ToListAsync(_ct).ConfigureAwait(false))
            {
                if (!string.Equals(inv.Status, nameof(InvoiceStatus.Posted), StringComparison.Ordinal)) continue;
                var lines = await _db.VendorInvoiceLines
                    .Where(l => l.VendorId == inv.VendorId && l.SupplierInvoiceNumber == inv.SupplierInvoiceNumber)
                    .ToListAsync(_ct).ConfigureAwait(false);
                open += lines.Sum(l => l.Quantity * l.UnitPrice);
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            void Count(string type, string status)
            {
                counts[type] = counts.GetValueOrDefault(type) + 1;
                var key = $"{type}:{status}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            foreach (var pr in await _db.PurchaseRequisitions.ToListAsync(_ct).ConfigureAwait(false)) Count("PR", pr.Status);
            foreach (var po in await _db.PurchaseOrders.ToListAsync(_ct).ConfigureAwait(false))
            {
                Count("PO", po.ApprovalStatus);
                Count("PO-DOC", po.DocStatus);
            }
            foreach (var r in await _db.ShipmentReceipts.ToListAsync(_ct).ConfigureAwait(false)) Count("RCT", r.Status);
            foreach (var a in await _db.Asns.ToListAsync(_ct).ConfigureAwait(false)) Count("ASN", "Recorded");
            foreach (var i in await _db.VendorInvoices.ToListAsync(_ct).ConfigureAwait(false)) Count("INV", i.Status);
            foreach (var p in payments) Count("PAY", p.Status);
            counts["APPROVAL"] = await _db.Approvals.CountAsync(_ct).ConfigureAwait(false);
            counts["JOURNAL"] = await _db.InventoryJournals.CountAsync(_ct).ConfigureAwait(false);

            var sequences = (await _db.Sequences.ToListAsync(_ct).ConfigureAwait(false))
                .ToDictionary(s => s.Name, s => s.Next, StringComparer.Ordinal);

            var onHand = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var t in await _db.InventoryTransactions.ToListAsync(_ct).ConfigureAwait(false))
            {
                if (!string.Equals(t.Status, nameof(InventoryStatus.Available), StringComparison.Ordinal)) continue;
                onHand[t.ItemId] = onHand.GetValueOrDefault(t.ItemId) + t.Quantity;
            }

            return new BaselineReport(vendors, trailingTotal, trailingCn, trailingPct,
                trailingByCountry, paid, open,
                trailingCn + Corp02Lot, corpTotal, corpCnPct,
                trailingMx + Corp02Lot, corpMxPct,
                counts, sequences, onHand);
        }
    }
}
