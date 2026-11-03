// Published-pack overlay: the engine resolves the current pack set per
// evaluation (published files over embedded bases), so an approved memo
// enforces with no rebuild and no restart. Temp pack directories plus
// fixture pack JSON only; the embedded base asserts the shipped versions.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class PackOverlayTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-overlay-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void WritePacks(string dir)
    {
        foreach (var (name, json) in MemoFixtures.FourPacks())
        {
            var file = name.StartsWith("Packs.", StringComparison.Ordinal) ? name["Packs.".Length..] : name;
            File.WriteAllText(Path.Combine(dir, file), json);
        }
    }

    /// <summary>Copies the real shipped packs (with document kinds) so the
    /// engine can evaluate end to end against the temp directory.</summary>
    private static void WriteRealPacks(string dir)
    {
        var baseFile = CompanyKbLoader.ResolvePath("Gate.Compliance/Packs/corp.json");
        var source = Path.GetDirectoryName(baseFile)!;
        foreach (var file in Directory.GetFiles(source, "*.json", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }
    }

    private static (RuleProposal Proposal, ProposalValidation Validation, RuleApproval Approval) CapTrio(
        string cap = "0.40")
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = MemoFixtures.Proposal(
            rule: MemoFixtures.CapRule(cap),
            statements: [new ProposalStatement("cap the share", "cap the share lower", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
            citations: [citation]);
        var validation = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        var approval = MemoFixtures.Approval(proposal);
        return (proposal, validation, approval);
    }

    private static SourcingCheckInput AwardInput(decimal qty) => new(
        "V-9", "Vendor Nine", "CN", "NET0",
        "BAT-9", "Battery cell", true, qty,
        [
            new VendorDocumentDto("V-9", "UN38.3", "ref-1", true),
            new VendorDocumentDto("V-9", "SDS", "ref-2", true),
            new VendorDocumentDto("V-9", "RoHS", "ref-3", true),
            new VendorDocumentDto("V-9", "REACH", "ref-4", true),
        ],
        [
            new VolumeHistory("history", "CN", 5600),
            new VolumeHistory("history", "MX", 4000),
            new VolumeHistory("history", "OTHER", 400),
        ],
        null,
        []);

    [Fact]
    public void LoadCurrent_MissingDirLoadsEmbedded()
    {
        var set = RulePackLoader.LoadCurrent(Path.Combine(Path.GetTempPath(), "gate-no-such-dir", Guid.NewGuid().ToString("N")));
        Assert.Equal("v2", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        Assert.Equal(0.60m, set.Corp.OriginCap);
        Assert.Equal("v1", set.Rules.Single(r => r.RuleId == "ELEC-01").PackVersion);
    }

    [Fact]
    public void LoadCurrent_EmptyDirLoadsEmbedded()
    {
        var dir = NewTempDir();
        try
        {
            var set = RulePackLoader.LoadCurrent(dir);
            Assert.Equal("v2", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
            Assert.Equal(0.60m, set.Corp.OriginCap);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void LoadCurrent_PartialDirOverlaysOnlyThatPack()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "corp.v3.json"), MemoFixtures.CorpPackJson("v3", "0.40"));
            var set = RulePackLoader.LoadCurrent(dir);
            Assert.Equal("v3", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
            Assert.Equal(0.40m, set.Corp.OriginCap);
            Assert.Equal("v1", set.Rules.Single(r => r.RuleId == "ELEC-01").PackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task SameEngineEnforcesPublishedCapWithoutRebuild()
    {
        var dir = NewTempDir();
        try
        {
            WriteRealPacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var policy = new ErpPolicy(engine);

            var before = await policy.CheckSourcingAsync(AwardInput(90));
            Assert.True(before.Allowed);

            var (proposal, validation, approval) = CapTrio();
            var publication = await engine.PublishRuleUpdateAsync(
                "corp", proposal, validation, approval, ["PackOverlayTests.SameEngineEnforcesPublishedCapWithoutRebuild"],
                "CORP-02", "ACT-T1", MemoFixtures.FixedAt.AddDays(30));
            Assert.Equal("v2", publication.Activation.PriorPackVersion);
            Assert.Equal("v3", publication.Activation.NewPackVersion);

            // Same engine, same policy, no rebuild: the next check refuses.
            var after = await policy.CheckSourcingAsync(AwardInput(90));
            Assert.False(after.Allowed);
            Assert.NotNull(after.Refusal);
            Assert.Equal("CORP-02", after.Refusal.RuleId);
            Assert.Equal("CORP", after.Refusal.Pack);
            Assert.Equal("v3", after.Refusal.PackVersion);
            Assert.Contains("40%", after.Refusal.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishIntoEmptyDirBuildsOnEmbeddedBase()
    {
        var dir = NewTempDir();
        try
        {
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            var publication = await engine.PublishRuleUpdateAsync(
                "corp", proposal, validation, approval, ["PackOverlayTests.PublishIntoEmptyDirBuildsOnEmbeddedBase"],
                "CORP-02", "ACT-T1", MemoFixtures.FixedAt.AddDays(30));
            Assert.Equal("corp.v3.json", publication.FileName);
            Assert.Equal("v2", publication.Activation.PriorPackVersion);
            Assert.Equal("v3", publication.Activation.NewPackVersion);
            Assert.True(File.Exists(Path.Combine(dir, "corp.v3.json")));
            Assert.Equal("v3", RulePackLoader.LoadCurrent(dir).Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ListLivePackRules_PartialDirShowsOverlay()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "corp.v3.json"), MemoFixtures.CorpPackJson("v3", "0.40"));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var rules = await engine.ListLivePackRulesAsync();
            Assert.Equal("v3", rules.Single(r => r.RuleId == "CORP-02").PackVersion);
            Assert.Equal("v1", rules.Single(r => r.RuleId == "ELEC-01").PackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
