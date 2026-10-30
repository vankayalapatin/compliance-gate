// Shared seeded fake used by every Eval test. Mirrors the demo dataset:
// critical battery short plus a cheap non-critical short, three vendors,
// people with limits and a delegate.
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

internal static class SeededErp
{
    public const string Battery = "BAT-1";
    public const string Screw = "SCR-9";
    public const string Glasses = "GLASS-1";

    public static InMemoryErpAdapter Create()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("USD");
        erp.SeedAutoApproveThreshold(500m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedItem(new ItemDto(Battery, "Battery cell", true, 10, "EA"));
        erp.SeedItem(new ItemDto(Screw, "Screw", false, 10, "EA"));
        erp.SeedItem(new ItemDto(Glasses, "Smart glasses", false, 1, "EA"));
        erp.SeedSalesOrder(new SalesOrder("SO-1001", SalesOrderStatus.Confirmed, "USD",
            [new SalesOrderLine(Glasses, 100)]));
        erp.SeedBom(Glasses, [new BomLine(Battery, 1), new BomLine(Screw, 2)]);
        erp.SeedStock(Battery, 10);
        erp.SeedStock(Screw, 50);
        erp.SeedVendor(new VendorDto("V-A", "Cheap Cells Ltd", "CN", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-C", "Screws Co", "US", false, null, "NET0"));
        erp.SeedVendorBank(new VendorBankDto("V-A", "BANK-A-TEST", true, DateTimeOffset.UtcNow));
        erp.SeedVendorBank(new VendorBankDto("V-B", "BANK-B-TEST", true, DateTimeOffset.UtcNow));
        erp.SeedVendorBank(new VendorBankDto("V-C", "BANK-C-TEST", true, DateTimeOffset.UtcNow));
        erp.SeedVendorPrice("V-A", Battery, 11m);
        erp.SeedVendorPrice("V-B", Battery, 12m);
        erp.SeedVendorPrice("V-B", Screw, 1m);
        erp.SeedVendorPrice("V-C", Screw, 1m);
        erp.SeedVendorDocuments("V-A", [new VendorDocumentDto("V-A", "UN38.3", "un383-a", true), new VendorDocumentDto("V-A", "SDS", "sds-a", true), new VendorDocumentDto("V-A", "RoHS", "rohs-a", true), new VendorDocumentDto("V-A", "REACH", "reach-a", true)]);
        erp.SeedVendorDocuments("V-B", [new VendorDocumentDto("V-B", "UN38.3", "un383-b", true), new VendorDocumentDto("V-B", "SDS", "sds-b", true), new VendorDocumentDto("V-B", "RoHS", "rohs-b", true), new VendorDocumentDto("V-B", "REACH", "reach-b", true)]);
        // V-C carries no pack documents: SCR-9 is non-critical, so no pack applies.
        // Raw trailing-12-month history in cells (total 10000, CN 6000).
        // The 90-cell test award puts CN at 6090/10090 = 60.4% (blocks V-A)
        // and MX at 3790/10090 = 37.6% (passes V-B). Test-double data only.
        erp.SeedVolumeHistory(Battery, [
            new VolumeHistory("V-A CN active", "CN", 3500),
            new VolumeHistory("Legacy CN inactive", "CN", 2500),
            new VolumeHistory("V-B MX active", "MX", 3700),
            new VolumeHistory("Other non-CN", "OTHER", 300)]);
        erp.SeedUser(new UserDto("planner", "requester", 5000m, "USD"));
        erp.SeedUser(new UserDto("approver", "approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto("delegate", "delegate", 50_000m, "USD"));
        erp.SeedUser(new UserDto("finance", "payment-approver", 100_000m, "USD"));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        erp.SeedDelegation(new DelegationDto("approver", "delegate", today.AddDays(-30), today.AddDays(30)));
        return erp;
    }
}
