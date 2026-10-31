// Seeds EF Core masters from a scenario file. All business values come
// from the scenario; code holds logic only. Called by ScenarioLoader for
// the EfCore adapter path and by tests with their own small data.
using System.Globalization;

namespace Gate.Erp;

public static class ErpSeeder
{
    public static void SeedMasters(ErpDbContext db, ScenarioFile scenario, string soNumber, DateTimeOffset now)
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
        db.SalesOrders.Add(new SalesOrderRow
        {
            SoNumber = soNumber, Status = nameof(Gate.Contracts.SalesOrderStatus.Created), Currency = scenario.Currency
        });
        var soLine = 0;
        db.SalesOrderLines.Add(new SalesOrderLineRow
        {
            SoNumber = soNumber, LineNo = ++soLine,
            ItemId = scenario.SalesOrder.Item, Quantity = scenario.SalesOrder.Quantity
        });
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
