// Seeds EF Core masters from a scenario file. All business values come
// from the scenario; code holds logic only. Called by ScenarioLoader for
// the EfCore adapter path and by tests with their own small data.
using System.Globalization;

namespace Gate.Erp;

public static class ErpSeeder
{
    /// <summary>Seeds masters only (currency, items, terms, vendors, banks,
    /// prices, history, users, delegations, stock, BOM). Documents are never
    /// inserted here: the loader creates every SO/PR/PO/ASN/receipt/invoice/
    /// payment through adapter writes so all ERP validation is enforced.</summary>
    public static void SeedMasters(ErpDbContext db, ScenarioFile scenario, DateTimeOffset now)
    {
        db.Config.Add(new ConfigRow { Key = EfCoreErpAdapter.BaseCurrencyKey, Value = scenario.Currency });
        db.Config.Add(new ConfigRow
        {
            Key = EfCoreErpAdapter.AutoApproveKey,
            Value = scenario.AutoApproveUnder.ToString(CultureInfo.InvariantCulture)
        });
        foreach (var item in scenario.Items)
        {
            db.Items.Add(new ItemRow
            {
                ItemId = item.Id, Description = item.Description, IsCritical = item.Critical,
                UnitsPerCarton = item.PackSize, Unit = item.Unit
            });
        }
        foreach (var terms in scenario.PaymentTerms)
        {
            db.PaymentTerms.Add(new PaymentTermsRow { TermsId = terms.Id, NetDays = terms.NetDays });
        }
        foreach (var b in scenario.Bom)
        {
            db.BomLines.Add(new BomRow { ItemId = b.Item, ComponentItemId = b.Component, QtyPer = b.QtyPer });
        }
        foreach (var s in scenario.Stock)
        {
            db.InventoryTransactions.Add(new InventoryRow
            {
                RowId = Guid.NewGuid().ToString("N"), ItemId = s.Item, ReceiptRef = "OPENING",
                LotRef = "OPENING", Status = nameof(Gate.Contracts.InventoryStatus.Available), Quantity = s.Quantity
            });
        }
        var lastPrices = new Dictionary<string, decimal>();
        foreach (var v in scenario.Vendors)
        {
            db.Vendors.Add(new VendorRow
            {
                VendorId = v.Id, Name = v.Name, CountryOfOrigin = v.Country,
                IsBlocked = false, BlockReason = null, PaymentTermsId = v.Terms, Email = v.Email
            });
            foreach (var d in v.Documents)
            {
                db.VendorDocuments.Add(new VendorDocumentRow
                {
                    RowId = Guid.NewGuid().ToString("N"), VendorId = v.Id,
                    Kind = d.Kind, Reference = d.Reference, Present = d.Present
                });
            }
            db.VendorBanks.Add(new VendorBankRow
            {
                VendorId = v.Id, AccountRef = v.BankAccount, Verified = true,
                VerifiedAt = now.ToString("O", CultureInfo.InvariantCulture)
            });
            db.VendorPrices.Add(new VendorPriceRow { VendorId = v.Id, ItemId = v.Item, UnitPrice = v.UnitPrice });
            lastPrices[v.Item] = v.UnitPrice;
        }
        foreach (var (itemId, price) in lastPrices)
        {
            db.LastPrices.Add(new LastPriceRow { ItemId = itemId, UnitPrice = price });
        }
        foreach (var h in scenario.HistoryCells)
        {
            db.VolumeSeeds.Add(new VolumeSeedRow
            {
                RowId = Guid.NewGuid().ToString("N"), ItemId = scenario.HistoryPart,
                Source = h.Source, Country = h.Country, Cells = h.Cells
            });
        }
        foreach (var u in scenario.Users)
        {
            db.Users.Add(new UserRow
            {
                UserId = u.Name, Role = u.Role, SigningLimit = u.SigningLimit, LimitCurrency = scenario.Currency
            });
        }
        foreach (var d in scenario.Delegations)
        {
            db.Delegations.Add(new DelegationRow
            {
                RowId = Guid.NewGuid().ToString("N"), FromUser = d.From, ToUser = d.To,
                Start = d.Start, End = d.End
            });
        }
        db.SaveChanges();
    }

    /// <summary>Applies scenario number-sequence rules to the Sequences rows:
    /// stores each prefix format and starts the counter at Start, or after
    /// the max seeded number when seeded rows already run higher.</summary>
    public static void ApplyNumberSequences(ErpDbContext db, IEnumerable<ScenarioNumberSequence> sequences)
    {
        foreach (var seq in sequences)
        {
            var next = Math.Max(seq.Start, MaxSeededNumber(db, seq.Prefix) + 1);
            var row = db.Sequences.Find(seq.Prefix);
            if (row is null)
            {
                db.Sequences.Add(new SequenceRow
                {
                    Name = seq.Prefix, Next = next, Format = seq.Format, Padding = seq.Padding
                });
            }
            else
            {
                row.Next = next;
                row.Format = seq.Format;
                row.Padding = seq.Padding;
            }
        }
        db.SaveChanges();
    }

    private static long MaxSeededNumber(ErpDbContext db, string prefix)
    {
        var max = 0L;
        void Scan(string? number)
        {
            if (number is not null
                && NumberSequences.TryParseNumber(prefix, number, out var n)
                && n > max) max = n;
        }
        foreach (var n in db.SalesOrders.Select(s => s.SoNumber).ToList()) Scan(n);
        foreach (var n in db.PurchaseRequisitions.Select(p => p.PrNumber).ToList()) Scan(n);
        foreach (var n in db.PurchaseOrders.Select(p => p.PoNumber).ToList()) Scan(n);
        foreach (var n in db.ShipmentReceipts.Select(r => r.ReceiptNumber).ToList()) Scan(n);
        foreach (var n in db.Asns.Select(a => a.AsnNumber).ToList()) Scan(n);
        foreach (var n in db.Holds.Select(h => h.HoldId).ToList()) Scan(n);
        foreach (var n in db.Vendors.Select(v => v.VendorId).ToList()) Scan(n);
        foreach (var n in db.Payments.Select(p => p.PaymentNumber).ToList()) Scan(n);
        foreach (var n in db.VendorInvoices.Select(i => i.Voucher).ToList()) Scan(n);
        return max;
    }

    public static void UpsertVendorPrice(ErpDbContext db, string vendorId, string itemId, decimal unitPrice)
    {
        var price = db.VendorPrices.Find(vendorId, itemId);
        if (price is null) db.VendorPrices.Add(new VendorPriceRow { VendorId = vendorId, ItemId = itemId, UnitPrice = unitPrice });
        else price.UnitPrice = unitPrice;
        var last = db.LastPrices.Find(itemId);
        if (last is null) db.LastPrices.Add(new LastPriceRow { ItemId = itemId, UnitPrice = unitPrice });
        else last.UnitPrice = unitPrice;
        db.SaveChanges();
    }
}
