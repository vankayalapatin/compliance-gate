// Registry and routing pins: the agent-owned type names, prompt rows, pack
// routing, and KB policy map are pinned against the compliance registry,
// the real publisher, and the real company-kb.json. Drift fails here,
// loudly, instead of misleading Spark or misrouting publish.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoRegistryPinTests
{
    [Fact]
    public void AgentTypeNamesMatchTheComplianceRegistry()
    {
        var registry = MemoRuleRegistry.All.Select(s => s.RuleType).Order(StringComparer.Ordinal).ToList();
        var agent = MemoRuleTypes.All.Order(StringComparer.Ordinal).ToList();

        Assert.Equal(8, registry.Count);
        Assert.Equal(registry, agent);
    }

    [Fact]
    public void PromptRowsMirrorRegistryGatesAndParams()
    {
        Assert.Equal(8, MemoPrompts.RegistryRows.Count);
        foreach (var row in MemoPrompts.RegistryRows)
        {
            var spec = MemoRuleRegistry.Find(row.RuleType);
            Assert.Equal(spec.Gate.ToString(), row.Gate);
            foreach (var required in spec.RequiredParams)
            {
                Assert.Contains(required, row.Params, StringComparison.Ordinal);
            }

            Assert.False(string.IsNullOrWhiteSpace(row.Description));
        }
    }

    [Fact]
    public void PromptMentionsEveryRegistryType()
    {
        var h = MemoTestKit.Create();
        var prompt = MemoPrompts.BuildProposalPrompt(
            new PolicyMemo("MEMO-T1", DateTimeOffset.UtcNow, "The board", "Excerpt."),
            "Full text.", MemoFixtures.TestKb(), []);

        foreach (var type in MemoRuleRegistry.All.Select(s => s.RuleType))
        {
            Assert.Contains(type, prompt, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("DocumentPresence", "CORP")]
    [InlineData("OriginShareCap", "CORP")]
    [InlineData("VendorShareCap", "CORP")]
    [InlineData("ScreeningMatch", "SCR")]
    [InlineData("DockMarkPresence", "BAT-LI")]
    [InlineData("DockHandlingPresence", "BAT-LI")]
    [InlineData("DamageReview", "BAT-LI")]
    [InlineData("PrePayBlockOnOpen", "ELEC")]
    public void PackRoutingMatchesThePublisher(string ruleType, string expectedPack)
    {
        Assert.Equal(expectedPack, MemoRunner.PackForRuleType(ruleType));
    }

    [Fact]
    public async Task RoutedPacksPublishNewRulesWithoutRefusal()
    {
        // Every agent-routed pack accepts a brand-new rule of its types: the
        // round-trip through the real publisher proves the routing table.
        foreach (var (ruleType, gate, paramsMap) in RuleParams())
        {
            var compliance = new RecordingComplianceEngine();
            var proposal = MemoFixtures.Proposal(
                rule: new ProposedRule(
                    ruleType, gate, new Dictionary<string, string>(paramsMap)),
                questions: [new OpenQuestion("Q1", "Pin question?", ["Yes"], true)]);
            var validation = ProposalValidator.ValidateAgainstKb(proposal, compliance.Snapshot);
            Assert.True(validation.Ok);
            var approval = MemoFixtures.Approval(proposal);

            var publication = await compliance.PublishNewRuleAsync(
                MemoRunner.PackForRuleType(ruleType), proposal, validation, approval,
                ["MemoRegistryPinTests.RoutedPacks"], $"T-{ruleType}",
                new NewRuleSpec($"{ruleType} title.", RuleTier.Tier2, "Pin label."),
                $"pin-{ruleType}", MemoFixtures.FixedAt);

            Assert.Equal(MemoRunner.PackForRuleType(ruleType), publication.Activation.PackId);
        }
    }

    private static IReadOnlyList<(string RuleType, MemoRuleGate Gate, IReadOnlyDictionary<string, string> Params)> RuleParams() =>
    [
        (MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "Pin-Kind", ["partScope"] = "critical" }),
        (MemoRuleRegistry.OriginShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" }),
        (MemoRuleRegistry.VendorShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["cap"] = "0.45", ["windowMonths"] = "12", ["partScope"] = "critical" }),
        (MemoRuleRegistry.ScreeningMatch, MemoRuleGate.Vendor,
            new Dictionary<string, string>()),
        (MemoRuleRegistry.DockMarkPresence, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["markKind"] = "pin-mark" }),
        (MemoRuleRegistry.DockHandlingPresence, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["markKind"] = "pin-mark" }),
        (MemoRuleRegistry.DamageReview, MemoRuleGate.Dock,
            new Dictionary<string, string>()),
        (MemoRuleRegistry.PrePayBlockOnOpen, MemoRuleGate.PrePay,
            new Dictionary<string, string> { ["holds"] = "true" }),
    ];

    [Theory]
    [InlineData("CORP-02", "CORP")]
    [InlineData("CORP-01", "CORP")]
    [InlineData("SCR-01", "SCR")]
    [InlineData("ELEC-04", "ELEC")]
    [InlineData("BAT-LI-03", "BAT-LI")]
    [InlineData("BAT-LI-05", "BAT-LI")]
    public void PackFromLiveRuleId(string ruleId, string expectedPack)
    {
        Assert.Equal(expectedPack, MemoRunner.PackFromRuleId(ruleId, "MEMO-T1"));
    }

    [Fact]
    public void PackFromRuleIdWithoutPrefixRefuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MemoRunner.PackFromRuleId("NOPACK", "MEMO-T1"));

        Assert.Contains("packId explicitly", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MintedRuleIdsAreDeterministicAndSafe()
    {
        Assert.Equal("CORP-MEMO-T1", MemoRunner.MintRuleId("CORP", "MEMO-T1"));
        Assert.Equal(
            MemoRunner.MintRuleId("BAT-LI", "MEMO-T1"),
            MemoRunner.MintRuleId("BAT-LI", "MEMO-T1"));
        Assert.Equal("CORP-MEMO-7-X", MemoRunner.MintRuleId("corp", "memo 7/x"));
    }

    [Fact]
    public void ShareCapPolicyMapPinsAgainstTheRealKb()
    {
        var path = CompanyKbLoader.ResolvePath("company/company-kb.json");
        var kb = CompanyKbLoader.Load(File.ReadAllText(path));

        foreach (var (ruleType, policyId) in MemoWhatIfGather.ShareCapPolicyIds)
        {
            var policy = kb.SourcingPolicies.SingleOrDefault(p => p.Id == policyId);
            Assert.True(policy is not null, $"Real KB has no sourcing policy '{policyId}' behind {ruleType}.");
            Assert.Empty(MemoRuleRegistry.ValidateParams(ruleType, policy!.Params));
        }
    }

    [Fact]
    public void UnknownPackTypeRefuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MemoRunner.PackForRuleType("VibesCap"));

        Assert.Contains("VibesCap", ex.Message, StringComparison.Ordinal);
    }
}
