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
        erp.SeedVolumeShare("V-A", 0.65m);
        erp.SeedVolumeShare("V-B", 0.30m);
        erp.SeedVolumeShare("V-C", 0.05m);
        erp.SeedSigningLimit("approver", 100_000m);
        erp.SeedSigningLimit("delegate", 100_000m);
        erp.SeedSigningLimit("finance", 100_000m);
        return erp;
    }
}
