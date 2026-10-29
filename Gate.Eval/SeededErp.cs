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
        erp.SeedSalesOrder(new SalesOrder("SO-1001", DocStatus.Submitted,
            [new SalesOrderLine(Glasses, 100)]));
        erp.SeedBom(Glasses, [new BomLine(Battery, 1), new BomLine(Screw, 2)]);
        erp.SeedStock(Battery, 10);
        erp.SeedStock(Screw, 50);
        erp.SeedVendor(new VendorDto("V-A", "Cheap Cells Ltd", "CN", false, null));
        erp.SeedVendor(new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null));
        erp.SeedVendor(new VendorDto("V-C", "Screws Co", "US", false, null));
        erp.SeedVendorDocuments("V-A", [new VendorDocumentDto("V-A", "UN38.3", "un383-a", true), new VendorDocumentDto("V-A", "SDS", "sds-a", true)]);
        erp.SeedVendorDocuments("V-B", [new VendorDocumentDto("V-B", "UN38.3", "un383-b", true), new VendorDocumentDto("V-B", "SDS", "sds-b", true)]);
        erp.SeedVendorDocuments("V-C", [new VendorDocumentDto("V-C", "UN38.3", "n/a", true), new VendorDocumentDto("V-C", "SDS", "n/a", true)]);
        // Raw trailing-12-month history in cells (total 10000, CN 6000).
        // The 90-cell test award puts CN at 6090/10090 = 60.4% (blocks V-A)
        // and MX at 3790/10090 = 37.6% (passes V-B). Test-double data only.
        erp.SeedVolumeHistory(Battery, [
            new VolumeHistory("V-A CN active", "CN", 3500),
            new VolumeHistory("Legacy CN inactive", "CN", 2500),
            new VolumeHistory("V-B MX active", "MX", 3700),
            new VolumeHistory("Other non-CN", "OTHER", 300)]);
        erp.SeedSigningLimit("approver", 100_000m);
        erp.SeedSigningLimit("delegate", 100_000m);
        erp.SeedSigningLimit("finance", 100_000m);
        return erp;
    }
}
