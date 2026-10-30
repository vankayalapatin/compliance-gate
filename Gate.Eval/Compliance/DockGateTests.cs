// Dock gate: photo-only carton verdicts for BAT-LI-03/04/05 plus ASN
// reconciliation. Photos never set quantities; damage never auto-quarantines.
// Carton ids here are test doubles, not demo data.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class DockGateTests
{
    private readonly DockEvaluator _dock = new();

    private static CartonRead Read(
        string carton, string image,
        bool markPresent = true, bool markLegible = true,
        bool class9Required = true, bool class9Visible = true,
        bool damage = false, string? damageNote = null) =>
        new(carton, "BAT-9", "PO-T", "LOT-T", 10, markPresent, markLegible, "UN3480",
            class9Required, class9Visible, damage, damageNote, image, 0.99m);

    private static Verdict Rule(IReadOnlyList<Verdict> verdicts, string ruleId) =>
        verdicts.Single(v => v.RuleId == ruleId);

    [Fact]
    public void ClearCartonPassesAllThreeDockRules()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T1", [Read("CTN-T1", "IMG-T1"), Read("CTN-T1", "IMG-T2")]);
        Assert.Equal(3, verdicts.Count);
        Assert.All(verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
        Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v.Source)));
    }

    [Fact]
    public void UnanimousMissingMarkHoldsBatLi03()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T7", [Read("CTN-T7", "IMG-T1", markPresent: false), Read("CTN-T7", "IMG-T2", markPresent: false)]);
        var mark = Rule(verdicts, "BAT-LI-03");
        Assert.Equal(VerdictKind.Hold, mark.Kind);
        Assert.Contains("CTN-T7", mark.Reason);
        Assert.Equal(VerdictKind.Pass, Rule(verdicts, "BAT-LI-04").Kind);
        Assert.Equal(VerdictKind.Pass, Rule(verdicts, "BAT-LI-05").Kind);
    }

    [Fact]
    public void IllegibleMarkRoutesToHumanReview()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T2", [Read("CTN-T2", "IMG-T1", markLegible: false)]);
        Assert.Equal(VerdictKind.HumanReview, Rule(verdicts, "BAT-LI-03").Kind);
    }

    [Fact]
    public void ConflictingReadsRouteToHumanReview()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T3",
            [Read("CTN-T3", "IMG-T1", markPresent: true), Read("CTN-T3", "IMG-T2", markPresent: false)]);
        Assert.Equal(VerdictKind.HumanReview, Rule(verdicts, "BAT-LI-03").Kind);
    }

    [Fact]
    public void AsnCartonInNoImageRoutesToHumanReview()
    {
        var result = _dock.EvaluateDock(["CTN-T9"], []);
        var mark = Rule(result["CTN-T9"], "BAT-LI-03");
        Assert.Equal(VerdictKind.HumanReview, mark.Kind);
        Assert.Contains("no image", mark.Reason);
    }

    [Fact]
    public void Class9RequiredButMissingHoldsBatLi04()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T4", [Read("CTN-T4", "IMG-T1", class9Visible: false)]);
        Assert.Equal(VerdictKind.Hold, Rule(verdicts, "BAT-LI-04").Kind);
    }

    [Fact]
    public void Class9NotRequiredPassesBatLi04()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T5", [Read("CTN-T5", "IMG-T1", class9Required: false, class9Visible: false)]);
        Assert.Equal(VerdictKind.Pass, Rule(verdicts, "BAT-LI-04").Kind);
    }

    [Fact]
    public void DamageRoutesToHumanReviewAndNeverBlocks()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T6", [Read("CTN-T6", "IMG-T1", damage: true, damageNote: "crush outer")]);
        var damage = Rule(verdicts, "BAT-LI-05");
        Assert.Equal(VerdictKind.HumanReview, damage.Kind);
        Assert.NotEqual(VerdictKind.Block, damage.Kind);
        Assert.Contains("human decides", damage.Reason);
        Assert.Contains("blocked until review closes", damage.Reason);
    }

    [Fact]
    public void DamagedButMarkedCartonPassesMarkAndReviewsDamage()
    {
        var verdicts = _dock.EvaluateCarton("CTN-T8",
        [
            Read("CTN-T8", "IMG-T1", damage: true, damageNote: "crush outer, cells OK"),
            Read("CTN-T8", "IMG-T3", damage: true, damageNote: "crush outer, cells OK")
        ]);
        Assert.Equal(VerdictKind.Pass, Rule(verdicts, "BAT-LI-03").Kind);
        Assert.Equal(VerdictKind.HumanReview, Rule(verdicts, "BAT-LI-05").Kind);
    }

    [Fact]
    public void ReshotFlipsHoldToPassOnReevaluation()
    {
        var before = _dock.EvaluateCarton("CTN-T7", [Read("CTN-T7", "IMG-T1", markPresent: false)]);
        Assert.Equal(VerdictKind.Hold, Rule(before, "BAT-LI-03").Kind);

        var after = _dock.EvaluateCarton("CTN-T7", [Read("CTN-T7", "IMG-T4", markPresent: true)]);
        Assert.Equal(VerdictKind.Pass, Rule(after, "BAT-LI-03").Kind);
    }

    [Fact]
    public void EvaluateDockOrdersByAsnThenExtras()
    {
        var result = _dock.EvaluateDock(
            ["CTN-B", "CTN-A"],
            [Read("CTN-A", "IMG-T1"), Read("CTN-Z", "IMG-T1")]);
        Assert.Equal(["CTN-B", "CTN-A", "CTN-Z"], result.Keys.ToList());
        Assert.Equal(VerdictKind.HumanReview, Rule(result["CTN-B"], "BAT-LI-03").Kind);
    }

    [Fact]
    public void EveryDockVerdictCitesRuleAndSource()
    {
        var result = _dock.EvaluateDock(
            ["CTN-T1", "CTN-T7"],
            [Read("CTN-T1", "IMG-T1"), Read("CTN-T7", "IMG-T1", markPresent: false)]);
        foreach (var verdicts in result.Values)
        {
            Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v.RuleId)));
            Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v.Source)));
            Assert.All(verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v.Reason)));
        }
    }
}
