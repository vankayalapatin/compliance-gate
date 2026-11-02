// Master writes: vendor and item creation with every validation refused
// on both adapters. Blank vendor ids mint from the VND sequence, explicit
// ids override; item ids are always manual. Refusal texts match across
// adapters.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MasterWritesTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task CreateVendorExplicitId_Fake()
    {
        await VendorExplicitAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateVendorExplicitId_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await VendorExplicitAsync(fx.Adapter);
    }

    private static async Task VendorExplicitAsync(IErpAdapter erp)
    {
        var vendor = await erp.CreateVendorAsync("V-T", "Test Vendor", "US", "NET0",
            "ap@test.example", RecordSource.Human, "planner", Idem());
        Assert.Equal("V-T", vendor.VendorId);
        Assert.Equal("Test Vendor", vendor.Name);
        Assert.Equal("US", vendor.CountryOfOrigin);
        Assert.Equal("NET0", vendor.PaymentTermsId);
        Assert.Equal("ap@test.example", vendor.Email);
        Assert.False(vendor.IsBlocked);
        var vendors = await erp.GetVendorsAsync();
        Assert.Contains(vendors, v => v.VendorId == "V-T");
        Assert.Equal("US", await erp.GetVendorCountryAsync("V-T"));
    }

    [Fact]
    public async Task CreateVendorBlankIdMintsPadded_Fake()
    {
        await VendorMintAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateVendorBlankIdMintsPadded_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await VendorMintAsync(fx.Adapter);
    }

    private static async Task VendorMintAsync(IErpAdapter erp)
    {
        var minted = new List<string>();
        foreach (var blank in new string?[] { null, "", "   " })
        {
            var vendor = await erp.CreateVendorAsync(blank, "Minted Vendor", "US", "NET0",
                null, RecordSource.Human, "planner", Idem());
            Assert.StartsWith("VND-", vendor.VendorId);
            Assert.Matches(@"^VND-\d{4,}$", vendor.VendorId);
            minted.Add(vendor.VendorId);
        }
        Assert.Equal(3, minted.Distinct(StringComparer.Ordinal).Count());
        var vendors = await erp.GetVendorsAsync();
        Assert.All(minted, id => Assert.Contains(vendors, v => v.VendorId == id));
    }

    [Fact]
    public async Task CreateVendorRefusals_Fake()
    {
        await VendorRefusalsAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateVendorRefusals_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await VendorRefusalsAsync(fx.Adapter);
    }

    private static async Task VendorRefusalsAsync(IErpAdapter erp)
    {
        foreach (var blank in new[] { "", "   " })
        {
            var name = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                erp.CreateVendorAsync("V-X1", blank, "US", "NET0", null, RecordSource.Human, "planner", Idem()));
            Assert.Contains("Vendor name", name.Message);
            var country = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                erp.CreateVendorAsync("V-X1", "Name", blank, "NET0", null, RecordSource.Human, "planner", Idem()));
            Assert.Contains("country of origin", country.Message);
            var terms = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                erp.CreateVendorAsync("V-X1", "Name", "US", blank, null, RecordSource.Human, "planner", Idem()));
            Assert.Contains("Payment terms", terms.Message);
        }
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateVendorAsync("V-X1", "Name", "US", "NET-NOPE", null, RecordSource.Human, "planner", Idem()));
        Assert.Contains("Unknown payment terms", unknown.Message);
        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateVendorAsync("V-A", "Name", "US", "NET0", null, RecordSource.Human, "planner", Idem()));
        Assert.Contains("already exists", dup.Message);
        await erp.CreateVendorAsync("V-X1", "Name", "US", "NET0", null, RecordSource.Human, "planner", Idem());
        var dupNew = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateVendorAsync("V-X1", "Other", "US", "NET0", null, RecordSource.Human, "planner", Idem()));
        Assert.Contains("already exists", dupNew.Message);
    }

    [Fact]
    public async Task CreateVendorIdempotent_Fake()
    {
        await VendorIdempotentAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateVendorIdempotent_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await VendorIdempotentAsync(fx.Adapter);
    }

    private static async Task VendorIdempotentAsync(IErpAdapter erp)
    {
        var key = Idem();
        var first = await erp.CreateVendorAsync(null, "Minted Once", "US", "NET0",
            null, RecordSource.Human, "planner", key);
        var second = await erp.CreateVendorAsync(null, "Minted Twice", "US", "NET0",
            null, RecordSource.Human, "planner", key);
        Assert.Equal(first.VendorId, second.VendorId);
        Assert.Equal("Minted Once", second.Name);
    }

    [Fact]
    public async Task CreateItemHappyPath_Fake()
    {
        await ItemHappyAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateItemHappyPath_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await ItemHappyAsync(fx.Adapter);
    }

    private static async Task ItemHappyAsync(IErpAdapter erp)
    {
        var item = await erp.CreateItemAsync("WID-T", "Test widget", "EA", false, 25,
            RecordSource.Human, "planner", Idem());
        Assert.Equal("WID-T", item.ItemId);
        Assert.Equal(25, item.UnitsPerCarton);
        var stored = await erp.GetItemAsync("WID-T");
        Assert.NotNull(stored);
        Assert.Equal("Test widget", stored.Description);
        Assert.Contains(await erp.GetItemsAsync(), i => i.ItemId == "WID-T");
    }

    [Fact]
    public async Task CreateItemRefusals_Fake()
    {
        await ItemRefusalsAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateItemRefusals_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await ItemRefusalsAsync(fx.Adapter);
    }

    private static async Task ItemRefusalsAsync(IErpAdapter erp)
    {
        foreach (var blank in new[] { "", "   " })
        {
            var id = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                erp.CreateItemAsync(blank, "Widget", "EA", false, 10, RecordSource.Human, "planner", Idem()));
            Assert.Contains("Item id", id.Message);
        }
        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateItemAsync("BAT-1", "Widget", "EA", false, 10, RecordSource.Human, "planner", Idem()));
        Assert.Contains("already exists", dup.Message);
        foreach (var pack in new[] { 0, -4 })
        {
            var zero = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                erp.CreateItemAsync("WID-Z", "Widget", "EA", false, pack, RecordSource.Human, "planner", Idem()));
            Assert.Contains("above zero", zero.Message);
        }
    }

    [Fact]
    public async Task CreateItemIdempotent_Fake()
    {
        await ItemIdempotentAsync(SeededErp.Create());
    }

    [Fact]
    public async Task CreateItemIdempotent_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await ItemIdempotentAsync(fx.Adapter);
    }

    private static async Task ItemIdempotentAsync(IErpAdapter erp)
    {
        var key = Idem();
        var first = await erp.CreateItemAsync("WID-I", "First", "EA", false, 10,
            RecordSource.Human, "planner", key);
        var second = await erp.CreateItemAsync("WID-I", "Second", "EA", false, 10,
            RecordSource.Human, "planner", key);
        Assert.Equal(first.ItemId, second.ItemId);
        Assert.Equal("First", second.Description);
    }
}
