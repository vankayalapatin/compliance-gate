// PR limit routing: estimates price lines at the item's last purchase price,
// so a requisition over the requester's limit routes up the approval chain
// while one under the auto-approve threshold never needs a human.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class PrRoutingTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task OverPlannerLimitRoutesToDirectorChain()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        // 500 cells at the last BAT-1 price of 12 = 6000: over Maya's 5000.
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 500, "EA")], Idem());
        Assert.Equal(6000m, pr.EstimatedAmount);
        Assert.Equal(PrStatus.Draft, pr.Status);

        // Lowest covering signer is Dana at 50000, but Dana is out, so the
        // active delegation sends it to Del.
        var routedTo = await agent.RoutePrAsync(pr.PrNumber);
        Assert.Equal("delegate", routedTo);

        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, routedTo!, "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("PR", pr.PrNumber, "planner", true, "self", Idem()));
        var approval = await erp.RecordApprovalAsync("PR", pr.PrNumber, routedTo!, true, "ok", Idem());
        Assert.True(approval.Approved);
        Assert.Equal(6000m, approval.DocumentAmount);
        Assert.Equal(50000m, approval.LimitChecked);
    }

    [Fact]
    public async Task UnderThresholdNeedsNoHuman()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        // 10 screws at the last SCR-9 price of 1 = 10: under the 500 threshold.
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine(SeededErp.Screw, 10, "EA")], Idem());
        Assert.Equal(10m, pr.EstimatedAmount);
        Assert.Equal(PrStatus.Approved, pr.Status);

        Assert.Null(await agent.RoutePrAsync(pr.PrNumber));

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-C", RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(PoApprovalStatus.Approved, po.ApprovalStatus);
    }
}
