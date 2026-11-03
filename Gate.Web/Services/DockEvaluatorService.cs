// Production dock adapter: the runner's IDockEvaluator backed by the real C#
// dock rules. Mapping only; every judgment runs in DockEvaluator.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed class DockEvaluatorService : IDockEvaluator
{
    private readonly ComplianceEngine _engine;

    public DockEvaluatorService(ComplianceEngine engine)
    {
        _engine = engine;
    }

    public string MarkRuleId => DockEvaluator.MarkRuleId;

    public string Class9RuleId => DockEvaluator.Class9RuleId;

    public string DamageRuleId => DockEvaluator.DamageRuleId;

    public string LabelRuleId => DockEvaluator.LabelRuleId;

    public IReadOnlyDictionary<string, IReadOnlyList<Verdict>> EvaluateDock(
        IReadOnlyList<string> asnCartonIds,
        IReadOnlyList<DockCartonRead> reads,
        string expectedPoNumber,
        IReadOnlyDictionary<string, string> cartonLots) =>
        new DockEvaluator(_engine.CurrentRules()).EvaluateDock(
            asnCartonIds, reads.Select(r => new CartonRead(
                r.CartonId, r.Part, r.PoNumber, r.LotCode, r.UnitsPerCarton,
                r.MarkPresent, r.MarkLegible, r.UnNumber, r.Class9Required,
                r.Class9Visible, r.DamageVisible, r.DamageNote, r.ImageId,
                r.ReadConfidence)).ToList(), expectedPoNumber, cartonLots);
}
