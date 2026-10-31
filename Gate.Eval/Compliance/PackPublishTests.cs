// Pack publish: validated plus approved plus tested proposals ship as
// version-bumped pack JSON; prior versions stay retained; rollback is a
// new version restoring prior parameters. Test-double proposals, packs,
// and temp directories only.
using System.Text.Json;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class PackPublishTests
{
    private static (RuleProposal Proposal, ProposalValidation Validation, RuleApproval Approval) CapTrio(
        string cap = "0.40",
        IReadOnlyDictionary<string, string>? editedParams = null,
        RuleApprovalOutcome outcome = RuleApprovalOutcome.Approved)
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = MemoFixtures.Proposal(
            rule: MemoFixtures.CapRule(cap),
            statements: [new ProposalStatement("cap the share", "cap the share lower", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
            citations: [citation]);
        var validation = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        var approval = MemoFixtures.Approval(proposal, editedParams: editedParams, outcome: outcome);
        return (proposal, validation, approval);
    }

    [Fact]
    public void UpdateBumpsVersionAndCitesMemoApproverDate()
    {
        var (proposal, validation, approval) = CapTrio();
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));

        var publication = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["MemoConflictTests.LowerCapTightensQuotingBothCaps"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);

        Assert.Equal("corp.v0.3.json", publication.FileName);
        Assert.Equal("CORP-02", publication.Activation.RuleId);
        Assert.Equal("CORP", publication.Activation.PackId);
        Assert.Equal("v0.3", publication.Activation.NewPackVersion);
        Assert.Equal("v0.2", publication.Activation.PriorPackVersion);
        Assert.Equal("MEMO-1", publication.Activation.MemoId);
        Assert.Contains("MEMO-1", publication.Json);
        Assert.Contains("Terry Owner", publication.Json);
        Assert.Contains(approval.EffectiveAt.ToString("yyyy-MM-dd"), publication.Json);
        Assert.Contains("0.40", publication.Json);

        var inputs = MemoFixtures.FourPacks();
        inputs.Add((publication.FileName, publication.Json));
        var set = RulePackLoader.LoadPacks(inputs);
        Assert.Equal(0.40m, set.Corp.OriginCap);
        Assert.Equal(0.50m, set.Corp.VendorCap);
        Assert.Equal("v0.3", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
    }

    [Fact]
    public void UpdateHonorsApproverEdits()
    {
        var (proposal, validation, approval) = CapTrio(editedParams: new Dictionary<string, string>
        {
            ["cap"] = "0.50",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        });
        var publication = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["edit-test"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);
        var inputs = MemoFixtures.FourPacks();
        inputs.Add((publication.FileName, publication.Json));
        Assert.Equal(0.50m, RulePackLoader.LoadPacks(inputs).Corp.OriginCap);
    }

    [Fact]
    public void NewRuleAppendsWithoutTouchingLiveRules()
    {
        var citation = MemoFixtures.Cite("documents", "un383-template");
        var proposal = MemoFixtures.Proposal(
            rule: new ProposedRule(
                MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
                new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" }),
            statements: [new ProposalStatement("declaration on file", "COO declaration on file", 0.9m)],
            resolutions: [new EntityResolution("declaration on file", "un383-template", citation)],
            citations: [citation]);
        var validation = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        var approval = MemoFixtures.Approval(proposal);

        var prior = MemoFixtures.CorpPackJson();
        var publication = PackPublisher.BuildNewRule(
            prior, proposal, validation, approval,
            ["doc-test"], "CORP-03",
            new NewRuleSpec("COO declaration on file before award.", RuleTier.Tier1, "COO declaration"),
            "ACT-2", MemoFixtures.FixedAt);

        Assert.Equal("corp.v0.3.json", publication.FileName);
        Assert.DoesNotContain("CORP-03", prior);
        Assert.Contains("CORP-03", publication.Json);
        Assert.Contains("COO-Declaration", publication.Json);

        var inputs = MemoFixtures.FourPacks();
        inputs.Add((publication.FileName, publication.Json));
        var set = RulePackLoader.LoadPacks(inputs);
        Assert.Equal(3, set.Rules.Count(r => r.Pack == "CORP"));
        var added = set.Rules.Single(r => r.RuleId == "CORP-03");
        Assert.Equal(RuleTier.Tier1, added.Tier);
        Assert.Equal("DocumentPresence", added.RuleType);
        Assert.Equal("COO declaration", added.DocumentLabel);
        Assert.True(added.Enforced);
    }

    [Fact]
    public void WrongPackRefuses()
    {
        var (proposal, validation, approval) = CapTrio();
        var batli = MemoFixtures.PackJson("BAT-LI", "v0.2", MemoFixtures.RuleJson("BAT-LI-01"));
        var ex = Assert.Throws<InvalidOperationException>(() => PackPublisher.BuildUpdate(
            batli, proposal, validation, approval, ["t"], "BAT-LI-01", "ACT-1", MemoFixtures.FixedAt));
        Assert.Contains("CORP", ex.Message);
    }

    [Fact]
    public void UnvalidatedUnapprovedUntestedRefuse()
    {
        var (proposal, validation, approval) = CapTrio();
        var prior = MemoFixtures.CorpPackJson();

        var badValidation = new ProposalValidation(false, ["broken"], []);
        Assert.Throws<InvalidOperationException>(() => PackPublisher.BuildUpdate(
            prior, proposal, badValidation, approval, ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt));

        var rejected = approval with { Outcome = RuleApprovalOutcome.Rejected };
        var ex = Assert.Throws<InvalidOperationException>(() => PackPublisher.BuildUpdate(
            prior, proposal, validation, rejected, ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt));
        Assert.Contains("not Approved", ex.Message);

        var untested = Assert.Throws<InvalidOperationException>(() => PackPublisher.BuildUpdate(
            prior, proposal, validation, approval, [], "CORP-02", "ACT-1", MemoFixtures.FixedAt));
        Assert.Contains("untested", untested.Message);
    }

    [Fact]
    public void RollbackRestoresPriorsAsANewVersion()
    {
        var (proposal, validation, approval) = CapTrio();
        var first = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);
        Assert.Contains("0.40", first.Json);

        var rollback = PackPublisher.BuildRollback(
            first.Json, MemoFixtures.CorpPackJson(),
            "MEMO-2", "Terry Owner", MemoFixtures.FixedAt.AddDays(60),
            RuleApplicability.ProspectiveOnly, "rollback-hash-9",
            "Board paused the tightening; restore 60%.",
            "CORP-02", "ACT-9", MemoFixtures.FixedAt.AddDays(61));

        Assert.Equal("corp.v0.4.json", rollback.FileName);
        Assert.Equal("v0.4", rollback.Activation.NewPackVersion);
        Assert.Equal("v0.3", rollback.Activation.PriorPackVersion);
        Assert.Contains("0.60", rollback.Json);
        Assert.Contains("MEMO-2", rollback.Json);
        Assert.Contains("Terry Owner", rollback.Json);

        var inputs = MemoFixtures.FourPacks();
        inputs.Add((first.FileName, first.Json));
        inputs.Add((rollback.FileName, rollback.Json));
        Assert.Equal(0.60m, RulePackLoader.LoadPacks(inputs).Corp.OriginCap);
    }

    [Fact]
    public void PublishToDirectoryCreatesAndNeverOverwrites()
    {
        var (proposal, validation, approval) = CapTrio();
        var publication = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);
        var dir = Path.Combine(Path.GetTempPath(), "gate-publish-test", Guid.NewGuid().ToString("N"));
        try
        {
            var priorPath = Path.Combine(dir, "corp.json");
            Directory.CreateDirectory(dir);
            File.WriteAllText(priorPath, MemoFixtures.CorpPackJson());

            var path = PackPublisher.PublishToDirectory(publication, dir);
            Assert.True(File.Exists(path));
            Assert.Equal(MemoFixtures.CorpPackJson(), File.ReadAllText(priorPath));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                PackPublisher.PublishToDirectory(publication, dir));
            Assert.Contains("never overwrite", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void PublicationSerializesByteIdentical()
    {
        var (proposal, validation, approval) = CapTrio();
        var a = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);
        var b = PackPublisher.BuildUpdate(
            MemoFixtures.CorpPackJson(), proposal, validation, approval,
            ["t"], "CORP-02", "ACT-1", MemoFixtures.FixedAt);
        Assert.Equal(a.Json, b.Json);
    }
}
