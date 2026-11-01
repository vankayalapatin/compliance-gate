// Caseless ERP holds: the hold case tag is stored verbatim and never
// looked up, so ERP actions work the same with a case, without one, or
// with a tag the ERP has never seen. Runs against both adapters.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CaselessHoldTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task HoldCaseTagStoredVerbatim_Fake()
    {
        await CaseTagAsync(SeededErp.Create());
    }

    [Fact]
    public async Task HoldCaseTagStoredVerbatim_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await CaseTagAsync(fx.Adapter);
    }

    private static async Task CaseTagAsync(IErpAdapter erp)
    {
        var bare = await erp.SetHoldAsync("R-CASELESS", "No case on this hold.", null, "",
            "planner", null, null, null, null, null, Idem());
        Assert.Equal(string.Empty, bare.CaseId);
        var tagged = await erp.SetHoldAsync("R-CASELESS", "Tagged hold.", null, "CASE-1",
            "planner", null, null, null, null, null, Idem());
        Assert.Equal("CASE-1", tagged.CaseId);

        var open = await erp.GetOpenHoldsAsync(null);
        Assert.Contains(open, h => h.HoldId == bare.HoldId && h.CaseId == string.Empty);
        Assert.Contains(open, h => h.HoldId == tagged.HoldId && h.CaseId == "CASE-1");
    }
}
