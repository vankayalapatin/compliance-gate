// Compliance verdicts are pure C#: no model client, no live/replay mode,
// no spend. These pins hold the compliance lane's share of the
// live-by-default contract: verdicts never depend on model state at all.
using System.Reflection;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ComplianceModelIndependenceTests
{
    [Fact]
    public void EngineExposesNoModelClientDependency()
    {
        var ctorParams = typeof(ComplianceEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => typeof(IModelClient).IsAssignableFrom(p.ParameterType))
            .Select(p => p.Name ?? "?");
        Assert.Empty(ctorParams);

        var methodParams = typeof(ComplianceEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .Where(p => typeof(IModelClient).IsAssignableFrom(p.ParameterType))
            .Select(p => $"{p.Member.DeclaringType?.Name}.{p.Member.Name}({p.Name})");
        Assert.Empty(methodParams);
    }

    [Fact]
    public async Task VendorVerdictsAreDeterministicWithoutModel()
    {
        var engine = new ComplianceEngine(new List<ScreeningEntry>());
        var vendor = new VendorDto("T-V1", "Test Vendor One", "XX", false, null, "NET0");
        var item = new ItemDto("T-CELL", "Test cell", true, 10, "EA");
        var docs = new[]
        {
            new VendorDocumentDto("T-V1", "UN38.3", "t-un383", true),
            new VendorDocumentDto("T-V1", "SDS", "t-sds", true),
            new VendorDocumentDto("T-V1", "RoHS", "t-rohs", true),
            new VendorDocumentDto("T-V1", "REACH", "t-reach", true)
        };
        var award = new ProposedAward("T-CELL", 10, "XX");
        var history = new[]
        {
            new VolumeHistory("T history XX", "XX", 30),
            new VolumeHistory("T history YY", "YY", 70)
        };

        var first = await engine.EvaluateVendorAsync(vendor, item, docs, award, history);
        var second = await engine.EvaluateVendorAsync(vendor, item, docs, award, history);
        var fresh = await new ComplianceEngine(new List<ScreeningEntry>())
            .EvaluateVendorAsync(vendor, item, docs, award, history);

        Assert.Equal(Shape(first), Shape(second));
        Assert.Equal(Shape(first), Shape(fresh));
    }

    [Fact]
    public async Task PaymentVerdictsAreDeterministicWithoutModel()
    {
        var engine = new ComplianceEngine(new List<ScreeningEntry>());
        var first = await engine.EvaluatePaymentAsync(new PrePayState([], [], [], []));
        var second = await engine.EvaluatePaymentAsync(new PrePayState([], [], [], []));
        Assert.Equal(Shape(first), Shape(second));
    }

    private static List<string> Shape(IReadOnlyList<Verdict> verdicts) =>
        verdicts.Select(v => $"{v.RuleId}|{v.Kind}|{v.Reason}|{v.Source}").ToList();
}
