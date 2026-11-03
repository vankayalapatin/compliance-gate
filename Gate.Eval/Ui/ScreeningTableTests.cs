// Vendor-screening-table tests: one row per screened candidate with the
// vendor name, country, unit price, result, and rule plus one reason
// sentence; the awarded vendor is marked. Data is local (SeededErp plus
// one stored PR and two screening events).
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class ScreeningTableTests
{
    [Fact]
    public async Task TableShowsOneRowPerCandidateWithReasons()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 90m, "EA")],
            Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var file = new CaseFile(
            new Case("CASE-T1", "SO-1001", SeededErp.Battery, "ui-test", CaseStage.VendorScreened, now, now,
                pr.PrNumber, null, null, null, [], [], null, null, AwardedVendorId: "V-B"),
            [
                new CaseEvent("e1", "CASE-T1", now, RecordSource.P2pAgent, CaseEventKind.VendorScreened,
                    "Vendor V-A failed screening: CORP-02.", ComplianceEngine.CorpOriginCapId, null, null, null, null, null),
                new CaseEvent("e2", "CASE-T1", now, RecordSource.P2pAgent, CaseEventKind.VendorScreened,
                    "Vendor V-B passed screening.", null, null, null, null, null, null),
            ],
            []);
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var rows = await VendorScreeningTable.RowsAsync(erp, agent, file, null);

        Assert.Equal(2, rows.Count);
        var blocked = Assert.Single(rows.Where(r => r.VendorId == "V-A"));
        Assert.Equal("CinderVolt Energy Ltd", blocked.VendorName);
        Assert.Equal("CN", blocked.Country);
        Assert.Equal("$11.00", blocked.Price);
        Assert.False(blocked.Passed);
        Assert.Equal("Blocked by CORP-02", blocked.Result);
        Assert.StartsWith("CORP-02: ", blocked.Reason, StringComparison.Ordinal);
        Assert.Contains("60.4%", blocked.Reason, StringComparison.Ordinal);
        Assert.False(blocked.Awarded);
        var passed = Assert.Single(rows.Where(r => r.VendorId == "V-B"));
        Assert.Equal("Meridian Cellworks Inc", passed.VendorName);
        Assert.True(passed.Passed);
        Assert.Equal("Passed", passed.Result);
        Assert.StartsWith("CORP-02: ", passed.Reason, StringComparison.Ordinal);
        Assert.Contains("37.6%", passed.Reason, StringComparison.Ordinal);
        Assert.True(passed.Awarded);
    }

    [Fact]
    public async Task TableIsEmptyBeforeScreeningRuns()
    {
        var erp = SeededErp.Create();
        var now = DateTimeOffset.UtcNow;
        var file = new CaseFile(
            new Case("CASE-T2", "SO-1001", SeededErp.Battery, "ui-test", CaseStage.PrSubmitted, now, now,
                "PR-1", null, null, null, [], [], null, null),
            [], []);
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        Assert.Empty(await VendorScreeningTable.RowsAsync(erp, agent, file, null));
    }

    [Fact]
    public void ScreenedVendorParsesFromBothSummaries()
    {
        Assert.Equal("V-A", Gate.Web.ViewModels.OriginCapMath.TryParseScreenedVendor(
            "Vendor V-A failed screening: CORP-02."));
        Assert.Equal("V-B", Gate.Web.ViewModels.OriginCapMath.TryParseScreenedVendor(
            "Vendor V-B passed screening."));
        Assert.Null(Gate.Web.ViewModels.OriginCapMath.TryParseScreenedVendor("Something else happened."));
    }
}
