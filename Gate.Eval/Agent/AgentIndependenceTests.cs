// Proves the core P2P logic is independent from ERP and the database:
// the agent assembly references contracts only, and the full flow runs on fakes.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class AgentIndependenceTests
{
    [Fact]
    public void AgentAssemblyDoesNotReferenceErp()
    {
        var refs = typeof(P2pAgent).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain("Gate.Erp", refs);
        Assert.Contains("Gate.Contracts", refs);
    }

    [Fact]
    public void AgentDependsOnInterfacesOnly()
    {
        var ctors = typeof(P2pAgent).GetConstructors();
        Assert.Single(ctors);
        foreach (var p in ctors[0].GetParameters())
        {
            Assert.True(p.ParameterType.IsInterface, $"{p.Name} must be an interface, was {p.ParameterType.Name}.");
        }
    }

    [Fact]
    public async Task ShortfallMathRunsOnAdapterData()
    {
        var agent = new P2pAgent(SeededErp.Create(), new FakeModelClient(), new ComplianceEngine());
        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        var battery = shortfall.Single(s => s.ItemId == SeededErp.Battery);
        var screw = shortfall.Single(s => s.ItemId == SeededErp.Screw);
        Assert.Equal(100, battery.Required);
        Assert.Equal(10, battery.Covered);
        Assert.Equal(90, battery.Shortfall);
        Assert.Equal(200, screw.Required);
        Assert.Equal(50, screw.Covered);
        Assert.Equal(150, screw.Shortfall);
    }
}
