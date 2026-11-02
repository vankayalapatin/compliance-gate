// EfCore test fixture: scratch sqlite file, migrated schema, small test-only
// seed mirroring the shared fake. Proves Database.Migrate() applies cleanly.
using System.Globalization;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

internal sealed class EfCoreTestDb : IAsyncDisposable
{
    public ErpDbContext Db { get; }
    public EfCoreErpAdapter Adapter { get; }
    public string FilePath { get; }

    private EfCoreTestDb(ErpDbContext db, DbContextOptions<ErpDbContext> options, string filePath)
    {
        Db = db;
        FilePath = filePath;
        Adapter = new EfCoreErpAdapter(options);
    }

    public static async Task<EfCoreTestDb> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-efcore-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "erp.db");
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite($"Data Source={file}")
            .Options;
        var db = new ErpDbContext(options);
        await db.Database.MigrateAsync().ConfigureAwait(false);
        Seed(db);
        return new EfCoreTestDb(db, options, file);
    }

    private static void Seed(ErpDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.DateTime);
        string stamp = now.ToString("O", CultureInfo.InvariantCulture);
        db.Config.Add(new ConfigRow { Key = "base-currency", Value = "USD" });
        db.Config.Add(new ConfigRow { Key = "auto-approve-threshold", Value = "500" });
        db.PaymentTerms.Add(new PaymentTermsRow { TermsId = "NET0", NetDays = 0 });
        db.Items.Add(new ItemRow { ItemId = "BAT-1", Description = "Battery cell", IsCritical = true, UnitsPerCarton = 10, Unit = "EA" });
        db.Items.Add(new ItemRow { ItemId = "SCR-9", Description = "Screw", IsCritical = false, UnitsPerCarton = 10, Unit = "EA" });
        db.Items.Add(new ItemRow { ItemId = "GLASS-1", Description = "Smart glasses", IsCritical = false, UnitsPerCarton = 1, Unit = "EA" });
        db.SalesOrders.Add(new SalesOrderRow { SoNumber = "SO-1001", Status = "Confirmed", Currency = "USD" });
        db.SalesOrderLines.Add(new SalesOrderLineRow { SoNumber = "SO-1001", LineNo = 1, ItemId = "GLASS-1", Quantity = 100 });
        db.BomLines.Add(new BomRow { ItemId = "GLASS-1", ComponentItemId = "BAT-1", QtyPer = 1 });
        db.BomLines.Add(new BomRow { ItemId = "GLASS-1", ComponentItemId = "SCR-9", QtyPer = 2 });
        db.InventoryTransactions.Add(new InventoryRow { RowId = Guid.NewGuid().ToString("N"), ItemId = "BAT-1", ReceiptRef = "OPENING", LotRef = "OPENING", Status = "Available", Quantity = 10 });
        db.InventoryTransactions.Add(new InventoryRow { RowId = Guid.NewGuid().ToString("N"), ItemId = "SCR-9", ReceiptRef = "OPENING", LotRef = "OPENING", Status = "Available", Quantity = 50 });
        db.Vendors.Add(new VendorRow { VendorId = "V-A", Name = "CinderVolt Energy Ltd", CountryOfOrigin = "CN", IsBlocked = false, BlockReason = null, PaymentTermsId = "NET0" });
        db.Vendors.Add(new VendorRow { VendorId = "V-B", Name = "Meridian Cellworks Inc", CountryOfOrigin = "MX", IsBlocked = false, BlockReason = null, PaymentTermsId = "NET0" });
        db.Vendors.Add(new VendorRow { VendorId = "V-C", Name = "Formosa Fasteners Co", CountryOfOrigin = "US", IsBlocked = false, BlockReason = null, PaymentTermsId = "NET0" });
        db.VendorBanks.Add(new VendorBankRow { VendorId = "V-A", AccountRef = "BANK-A-TEST", Verified = true, VerifiedAt = stamp });
        db.VendorBanks.Add(new VendorBankRow { VendorId = "V-B", AccountRef = "BANK-B-TEST", Verified = true, VerifiedAt = stamp });
        db.VendorBanks.Add(new VendorBankRow { VendorId = "V-C", AccountRef = "BANK-C-TEST", Verified = true, VerifiedAt = stamp });
        db.VendorPrices.Add(new VendorPriceRow { VendorId = "V-A", ItemId = "BAT-1", UnitPrice = 11m });
        db.VendorPrices.Add(new VendorPriceRow { VendorId = "V-B", ItemId = "BAT-1", UnitPrice = 12m });
        db.VendorPrices.Add(new VendorPriceRow { VendorId = "V-B", ItemId = "SCR-9", UnitPrice = 1m });
        db.VendorPrices.Add(new VendorPriceRow { VendorId = "V-C", ItemId = "SCR-9", UnitPrice = 1m });
        db.LastPrices.Add(new LastPriceRow { ItemId = "BAT-1", UnitPrice = 12m });
        db.LastPrices.Add(new LastPriceRow { ItemId = "SCR-9", UnitPrice = 1m });
        foreach (var v in new[] { "V-A", "V-B", "V-C" })
        {
            db.VendorDocuments.Add(new VendorDocumentRow { RowId = Guid.NewGuid().ToString("N"), VendorId = v, Kind = "UN38.3", Reference = "un383-test", Present = true });
            db.VendorDocuments.Add(new VendorDocumentRow { RowId = Guid.NewGuid().ToString("N"), VendorId = v, Kind = "SDS", Reference = "sds-test", Present = true });
        }
        db.Users.Add(new UserRow { UserId = "planner", Role = "requester", SigningLimit = 5000m, LimitCurrency = "USD" });
        db.Users.Add(new UserRow { UserId = "approver", Role = "approver", SigningLimit = 100_000m, LimitCurrency = "USD" });
        db.Users.Add(new UserRow { UserId = "delegate", Role = "delegate", SigningLimit = 50_000m, LimitCurrency = "USD" });
        db.Users.Add(new UserRow { UserId = "finance", Role = "payment-approver", SigningLimit = 100_000m, LimitCurrency = "USD" });
        db.Delegations.Add(new DelegationRow
        {
            RowId = Guid.NewGuid().ToString("N"), FromUser = "approver", ToUser = "delegate",
            Start = today.AddDays(-30).ToString("O", CultureInfo.InvariantCulture),
            End = today.AddDays(30).ToString("O", CultureInfo.InvariantCulture)
        });
        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync().ConfigureAwait(false);
        var dir = Path.GetDirectoryName(FilePath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
