// Closed rule-type registry: the 8 RULE-TYPES.md types, their gates, and
// param shape/range validation. Test-double params only.
using Gate.Compliance;

namespace Gate.Eval;

public sealed class MemoRegistryTests
{
    [Fact]
    public void RegistryHoldsExactlyTheEightDesignTypes()
    {
        var types = MemoRuleRegistry.All.Select(s => s.RuleType).OrderBy(t => t).ToList();
        Assert.Equal(
        [
            "DamageReview",
            "DockHandlingPresence",
            "DockMarkPresence",
            "DocumentPresence",
            "OriginShareCap",
            "PrePayBlockOnOpen",
            "ScreeningMatch",
            "VendorShareCap",
        ], types);
    }

    [Fact]
    public void GatesMatchDesign()
    {
        Assert.Equal(Gate.Contracts.MemoRuleGate.Vendor, MemoRuleRegistry.Find("DocumentPresence").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Vendor, MemoRuleRegistry.Find("OriginShareCap").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Vendor, MemoRuleRegistry.Find("VendorShareCap").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Vendor, MemoRuleRegistry.Find("ScreeningMatch").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Dock, MemoRuleRegistry.Find("DockMarkPresence").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Dock, MemoRuleRegistry.Find("DockHandlingPresence").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.Dock, MemoRuleRegistry.Find("DamageReview").Gate);
        Assert.Equal(Gate.Contracts.MemoRuleGate.PrePay, MemoRuleRegistry.Find("PrePayBlockOnOpen").Gate);
    }

    [Fact]
    public void UnknownTypeLookupThrowsNamingTheClosedList()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MemoRuleRegistry.Find("CarbonScore"));
        Assert.Contains("CarbonScore", ex.Message);
        Assert.Contains("OriginShareCap", ex.Message);
    }

    [Fact]
    public void ShareCapHappyPathValidates()
    {
        var errors = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        });
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    [InlineData("forty percent")]
    public void ShareCapRejectsOutOfRangeCaps(string cap)
    {
        var errors = MemoRuleRegistry.ValidateParams("VendorShareCap", new Dictionary<string, string>
        {
            ["cap"] = cap,
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        });
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("cap"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("twelve")]
    public void ShareCapRejectsOutOfRangeWindows(string window)
    {
        var errors = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = window,
            ["partScope"] = "critical",
        });
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("windowMonths"));
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("all")]
    public void ShareCapAcceptsBothScopes(string scope)
    {
        var errors = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = "12",
            ["partScope"] = scope,
        });
        Assert.Empty(errors);
    }

    [Fact]
    public void ShareCapRejectsBadScope()
    {
        var errors = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = "12",
            ["partScope"] = "some",
        });
        Assert.Contains(errors, e => e.Contains("partScope"));
    }

    [Fact]
    public void ShareCapRejectsMissingAndUnknownParams()
    {
        var missing = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = "12",
        });
        Assert.Contains(missing, e => e.Contains("partScope"));

        var unknown = MemoRuleRegistry.ValidateParams("OriginShareCap", new Dictionary<string, string>
        {
            ["cap"] = "0.40",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
            ["listScope"] = "everything",
        });
        Assert.Contains(unknown, e => e.Contains("listScope"));
    }

    [Fact]
    public void DocumentPresenceRequiresKindAndScope()
    {
        var ok = MemoRuleRegistry.ValidateParams("DocumentPresence", new Dictionary<string, string>
        {
            ["docKind"] = "COO-Declaration",
            ["partScope"] = "critical",
        });
        Assert.Empty(ok);

        var blank = MemoRuleRegistry.ValidateParams("DocumentPresence", new Dictionary<string, string>
        {
            ["docKind"] = "  ",
            ["partScope"] = "critical",
        });
        Assert.Contains(blank, e => e.Contains("docKind"));
    }

    [Fact]
    public void ScreeningMatchRejectsAnyParamAsMemoTuning()
    {
        Assert.Empty(MemoRuleRegistry.ValidateParams("ScreeningMatch", new Dictionary<string, string>()));
        var errors = MemoRuleRegistry.ValidateParams("ScreeningMatch", new Dictionary<string, string>
        {
            ["listScope"] = "OFAC-only",
        });
        Assert.Contains(errors, e => e.Contains("listScope") && e.Contains("data-driven"));
    }

    [Fact]
    public void DamageReviewTakesNoParams()
    {
        Assert.Empty(MemoRuleRegistry.ValidateParams("DamageReview", new Dictionary<string, string>()));
        var errors = MemoRuleRegistry.ValidateParams("DamageReview", new Dictionary<string, string>
        {
            ["threshold"] = "0.5",
        });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void DockMarksRequireMarkKind()
    {
        Assert.Empty(MemoRuleRegistry.ValidateParams("DockMarkPresence", new Dictionary<string, string>
        {
            ["markKind"] = "lithium-battery-mark",
        }));
        Assert.Empty(MemoRuleRegistry.ValidateParams("DockHandlingPresence", new Dictionary<string, string>
        {
            ["markKind"] = "class-9-diamond",
        }));
        var errors = MemoRuleRegistry.ValidateParams("DockMarkPresence", new Dictionary<string, string>
        {
            ["markKind"] = "",
        });
        Assert.Contains(errors, e => e.Contains("markKind"));
    }

    [Fact]
    public void PrePayRequiresAtLeastOneTrueState()
    {
        var ok = MemoRuleRegistry.ValidateParams("PrePayBlockOnOpen", new Dictionary<string, string>
        {
            ["holds"] = "true",
            ["quarantine"] = "false",
            ["reviews"] = "false",
        });
        Assert.Empty(ok);

        var none = MemoRuleRegistry.ValidateParams("PrePayBlockOnOpen", new Dictionary<string, string>
        {
            ["holds"] = "false",
        });
        Assert.Contains(none, e => e.Contains("at least one"));

        var empty = MemoRuleRegistry.ValidateParams("PrePayBlockOnOpen", new Dictionary<string, string>());
        Assert.Contains(empty, e => e.Contains("at least one"));

        var badBool = MemoRuleRegistry.ValidateParams("PrePayBlockOnOpen", new Dictionary<string, string>
        {
            ["holds"] = "yes",
        });
        Assert.Contains(badBool, e => e.Contains("boolean"));
    }
}
