// Memo-seam engine tests: every IComplianceEngine memo method delegates to
// the Memo core, snapshot loading resolves company-kb.json (overridable),
// and publish loads the current (highest-version) pack JSON, builds,
// writes, and returns the publication. Failure paths included: invalid
// proposals, hash mismatches, KB drift, unknown packs/versions, missing
// rules. Test-double proposals and temp pack directories only; the real
// company-kb.json is read for self-consistency, never hardcoded.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ComplianceEngineSeamTests
{
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

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-seam-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePacks(string dir)
    {
        foreach (var (name, json) in MemoFixtures.FourPacks())
        {
            var file = name.StartsWith("Packs.", StringComparison.Ordinal) ? name["Packs.".Length..] : name;
            File.WriteAllText(Path.Combine(dir, file), json);
        }
    }

    private static void DeleteTempDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadKbSnapshot_ReturnsPinnedSnapshot()
    {
        var engine = new ComplianceEngine();
        var snapshot = await engine.LoadKbSnapshotAsync();
        var companyDir = Path.GetDirectoryName(CompanyKbLoader.ResolvePath("company/company-kb.json"))!;
        var expected = CompanyKbLoader.LoadVersioned(
            Directory.GetFiles(companyDir, "company-kb*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
                .ToList()).ToSnapshot();
        Assert.Equal(expected.KbVersion, snapshot.KbVersion);
        Assert.Equal(expected.KbHash, snapshot.KbHash);
        Assert.Equal(expected.Items.Count, snapshot.Items.Count);
        Assert.Equal(expected.RegionRules.Count, snapshot.RegionRules.Count);
        Assert.Equal(expected.Documents.Count, snapshot.Documents.Count);
    }

    [Fact]
    public async Task LoadKbSnapshot_OverridePathLoadsThatFile()
    {
        var dir = NewTempDir();
        try
        {
            var source = File.ReadAllText(CompanyKbLoader.ResolvePath("company/company-kb.json"));
            var copy = Path.Combine(dir, "company-kb.json");
            File.WriteAllText(copy, source);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: copy));
            var snapshot = await engine.LoadKbSnapshotAsync();
            var expected = CompanyKbLoader.LoadFromFile("company/company-kb.json").ToSnapshot();
            Assert.Equal(expected.KbVersion, snapshot.KbVersion);
            Assert.Equal(expected.KbHash, snapshot.KbHash);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task LoadKbSnapshot_TamperedFileRefusesDrift()
    {
        var dir = NewTempDir();
        try
        {
            var source = File.ReadAllText(CompanyKbLoader.ResolvePath("company/company-kb.json"));
            var tampered = source.Replace("Fictional Smart-Glasses Co", "Xictional Smart-Glasses Co");
            Assert.NotEqual(source, tampered);
            var copy = Path.Combine(dir, "company-kb.json");
            File.WriteAllText(copy, tampered);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: copy));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadKbSnapshotAsync());
            Assert.Contains("hash mismatch", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ComputeProposalHash_MatchesCore()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, _) = CapTrio();
        Assert.Equal(proposal.ProposalHash, await engine.ComputeProposalHashAsync(proposal));
        Assert.Equal(ProposalHasher.Hash(proposal), await engine.ComputeProposalHashAsync(proposal));
    }

    [Fact]
    public async Task ValidateProposal_ValidProposalOkWithConflicts()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, _) = CapTrio();
        var validation = await engine.ValidateProposalAsync(proposal, MemoFixtures.TestKb());
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
        var tighten = Assert.Single(validation.Conflicts, c => c.RuleId == "CORP-02");
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
    }

    [Fact]
    public async Task ValidateProposal_BothSidesSetReportsErrors()
    {
        var engine = new ComplianceEngine();
        var proposal = MemoFixtures.Proposal(
            rule: MemoFixtures.CapRule(),
            notMappable: new NotMappableDetail(MemoRuleRegistry.OriginShareCap, "mismatch", MissingCapability.Data));
        var validation = await engine.ValidateProposalAsync(proposal, MemoFixtures.TestKb());
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("exactly one"));
    }

    [Fact]
    public async Task ValidateProposal_HashMismatchReportsErrors()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, _) = CapTrio();
        var altered = proposal with
        {
            Statements = [new ProposalStatement("cap everything", "cap everything lower", 0.9m)],
        };
        var validation = await engine.ValidateProposalAsync(altered, MemoFixtures.TestKb());
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("hash mismatch"));
    }

    [Fact]
    public async Task ValidateProposal_KbDriftRefuses()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, _) = CapTrio();
        var drifted = MemoFixtures.TestKb(version: "kbv10", hash: "hash10");
        var validation = await engine.ValidateProposalAsync(proposal, drifted);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("Re-ground"));
    }

    [Fact]
    public async Task DetectConflicts_LowerCapTightens()
    {
        var engine = new ComplianceEngine();
        var conflicts = await engine.DetectConflictsAsync(MemoFixtures.CapRule("0.40"));
        var tighten = Assert.Single(conflicts, c => c.RuleId == "CORP-02");
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Contains("60", tighten.Detail);
        Assert.Contains("40", tighten.Detail);
    }

    [Fact]
    public async Task DetectConflicts_UnknownTypeThrows()
    {
        var engine = new ComplianceEngine();
        var rule = new ProposedRule("Nope", MemoRuleGate.Vendor, new Dictionary<string, string>());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DetectConflictsAsync(rule));
        Assert.Contains("unknown rule type", ex.Message);
    }

    [Fact]
    public async Task ComputeWhatIf_TighteningFlipsPassToBlock()
    {
        var engine = new ComplianceEngine();
        var history = new List<WhatIfHistoryRow>
        {
            new("PO-1", "BAT-9", true, "V-A", "CN", 340, 10m, "USD"),
            new("PO-2", "BAT-9", true, "V-A", "CN", 250, 10m, "USD"),
            new("PO-3", "BAT-9", true, "V-B", "MX", 380, 10m, "USD"),
            new("PO-4", "BAT-9", true, "V-C", "OTHER", 30, 10m, "USD"),
        };
        WhatIfRuleConfig Cap(string cap) => new(
            MemoRuleRegistry.OriginShareCap,
            new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = "12", ["partScope"] = "critical" });
        var report = await engine.ComputeWhatIfAsync(
            history, [], Cap("0.60"), Cap("0.40"), "CORP-02", null,
            "proposal-hash-9", "kbv9", new Dictionary<string, string> { ["CORP"] = "v0.2" },
            MemoFixtures.FixedAt, null);
        // (380 + 380) / (1000 + 380) = 55.1%: passes at 60%, blocks at 40%.
        var flip = Assert.Single(report.HistoryFlips, f => f.DocumentNumber == "PO-3");
        Assert.Equal(VerdictKind.Pass, flip.OldKind);
        Assert.Equal(VerdictKind.Block, flip.NewKind);
        Assert.Equal("proposed:OriginShareCap", flip.BlockingRuleId);
    }

    [Fact]
    public async Task ComputeWhatIf_TypeMismatchThrows()
    {
        var engine = new ComplianceEngine();
        var oldConfig = new WhatIfRuleConfig(
            MemoRuleRegistry.OriginShareCap,
            new Dictionary<string, string> { ["cap"] = "0.60", ["windowMonths"] = "12", ["partScope"] = "critical" });
        var newConfig = new WhatIfRuleConfig(
            MemoRuleRegistry.VendorShareCap,
            new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ComputeWhatIfAsync(
            [], [], oldConfig, newConfig, "CORP-02", null,
            "h", "kbv9", new Dictionary<string, string>(), MemoFixtures.FixedAt, null));
        Assert.Contains("same registry type", ex.Message);
    }

    [Fact]
    public async Task ComputeWhatIf_EmptyMapResolvesLiveVersions()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            Assert.True(validation.Ok, string.Join("; ", validation.Errors));
            await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);

            WhatIfRuleConfig Cap(string cap) => new(
                MemoRuleRegistry.OriginShareCap,
                new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = "12", ["partScope"] = "critical" });
            var report = await engine.ComputeWhatIfAsync(
                [], [], Cap("0.60"), Cap("0.40"), "CORP-02", null,
                "proposal-hash-9", "kbv9", new Dictionary<string, string>(),
                MemoFixtures.FixedAt, null);

            Assert.Equal(4, report.PackVersions.Count);
            Assert.Equal("v0.3", report.PackVersions["CORP"]);
            Assert.Equal("v0.2", report.PackVersions["BAT-LI"]);
            Assert.Equal("v0.2", report.PackVersions["ELEC"]);
            Assert.Equal("v0.2", report.PackVersions["SCR"]);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ComputeWhatIf_ExplicitMapWinsUntouched()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            Assert.True(validation.Ok, string.Join("; ", validation.Errors));
            await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);

            WhatIfRuleConfig Cap(string cap) => new(
                MemoRuleRegistry.OriginShareCap,
                new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = "12", ["partScope"] = "critical" });
            var report = await engine.ComputeWhatIfAsync(
                [], [], Cap("0.60"), Cap("0.40"), "CORP-02", null,
                "proposal-hash-9", "kbv9", new Dictionary<string, string> { ["CORP"] = "v0.2" },
                MemoFixtures.FixedAt, null);

            var pinned = Assert.Single(report.PackVersions);
            Assert.Equal("CORP", pinned.Key);
            Assert.Equal("v0.2", pinned.Value);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ComputeWhatIf_EmptyMapWithBrokenPacksRefuses()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            File.WriteAllText(Path.Combine(dir, "corp.v0.2.json"), MemoFixtures.CorpPackJson("v0.2"));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var newConfig = new WhatIfRuleConfig(
                MemoRuleRegistry.OriginShareCap,
                new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ComputeWhatIfAsync(
                [], [], null, newConfig, null, null,
                "h", "kbv9", new Dictionary<string, string>(), MemoFixtures.FixedAt, null));
            Assert.Contains("twice", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ValidateApproval_ValidOk()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, approval) = CapTrio();
        var validation = await engine.ValidateApprovalAsync(approval, proposal, "kbv9");
        Assert.True(validation.Ok, string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task ValidateApproval_HashMismatchRefuses()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, approval) = CapTrio();
        var pinned = approval with { ProposalHash = "deadbeef" };
        var validation = await engine.ValidateApprovalAsync(pinned, proposal, "kbv9");
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("proposal hash"));
    }

    [Fact]
    public async Task ValidateApproval_KbDriftRefuses()
    {
        var engine = new ComplianceEngine();
        var (proposal, _, approval) = CapTrio();
        var validation = await engine.ValidateApprovalAsync(approval, proposal, "kbv10");
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, e => e.Contains("KB version change after proposal"));
    }

    [Fact]
    public async Task PublishRuleUpdate_WritesNewVersionedFile()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var prior = File.ReadAllText(Path.Combine(dir, "corp.json"));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            Assert.True(validation.Ok, string.Join("; ", validation.Errors));

            var publication = await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);

            Assert.Equal("corp.v0.3.json", publication.FileName);
            Assert.Equal("v0.3", publication.Activation.NewPackVersion);
            Assert.Equal("v0.2", publication.Activation.PriorPackVersion);
            Assert.Equal("CORP-02", publication.Activation.RuleId);
            Assert.True(File.Exists(Path.Combine(dir, "corp.v0.3.json")));
            Assert.Equal(prior, File.ReadAllText(Path.Combine(dir, "corp.json")));
            Assert.Contains("0.40", publication.Json);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishRuleUpdate_BuildsOnHighestVersion()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();

            var first = await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);
            var second = await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-2", MemoFixtures.FixedAt);

            Assert.Equal("corp.v0.3.json", first.FileName);
            Assert.Equal("corp.v0.4.json", second.FileName);
            Assert.Equal("v0.3", second.Activation.PriorPackVersion);
            Assert.Equal("v0.4", second.Activation.NewPackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishRuleUpdate_UnknownPackThrows()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishRuleUpdateAsync(
                "NOPE", proposal, validation, approval, ["seam-test"],
                "NOPE-01", "ACT-1", MemoFixtures.FixedAt));
            Assert.Contains("Unknown pack 'NOPE'", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishRuleUpdate_MissingRuleThrowsWithoutWriting()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-99", "ACT-1", MemoFixtures.FixedAt));
            Assert.Contains("carries no such rule", ex.Message);
            Assert.Equal(4, Directory.GetFiles(dir, "*.json").Length);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishNewRule_AppendsRuleToCurrentPack()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
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

            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var publication = await engine.PublishNewRuleAsync(
                "CORP", proposal, validation, approval, ["seam-test"], "CORP-03",
                new NewRuleSpec("COO declaration on file before award.", RuleTier.Tier1, "COO declaration"),
                "ACT-2", MemoFixtures.FixedAt);

            Assert.Equal("corp.v0.3.json", publication.FileName);
            Assert.True(File.Exists(Path.Combine(dir, "corp.v0.3.json")));

            var inputs = Directory.GetFiles(dir, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
                .ToList();
            var set = RulePackLoader.LoadPacks(inputs);
            var added = set.Rules.Single(r => r.RuleId == "CORP-03");
            Assert.Equal(RuleTier.Tier1, added.Tier);
            Assert.Equal("v0.3", added.PackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishNewRule_DuplicateRuleThrows()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var citation = MemoFixtures.Cite("documents", "un383-template");
            var proposal = MemoFixtures.Proposal(
                rule: new ProposedRule(
                    MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
                    new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" }),
                statements: [new ProposalStatement("declaration on file", "COO declaration on file", 0.9m)],
                resolutions: [new EntityResolution("declaration on file", "un383-template", citation)],
                citations: [citation]);
            var validation = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
            var approval = MemoFixtures.Approval(proposal);

            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishNewRuleAsync(
                "CORP", proposal, validation, approval, ["seam-test"], "CORP-02",
                new NewRuleSpec("Duplicate.", RuleTier.Tier1, "COO declaration"),
                "ACT-2", MemoFixtures.FixedAt));
            Assert.Contains("already carries", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishRollback_RestoresPriorAsNewVersion()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var (proposal, validation, approval) = CapTrio();
            var first = await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, approval, ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);
            Assert.Contains("0.40", first.Json);

            var rollback = await engine.PublishRollbackAsync(
                "CORP", "v0.2", "MEMO-2", "Terry Owner", MemoFixtures.FixedAt.AddDays(60),
                RuleApplicability.ProspectiveOnly, "rollback-hash-9",
                "Board paused the tightening; restore 60%.",
                "CORP-02", "ACT-9", MemoFixtures.FixedAt.AddDays(61));

            Assert.Equal("corp.v0.4.json", rollback.FileName);
            Assert.Equal("v0.4", rollback.Activation.NewPackVersion);
            Assert.Equal("v0.3", rollback.Activation.PriorPackVersion);
            Assert.Contains("0.60", rollback.Json);
            Assert.Contains("MEMO-2", rollback.Json);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task PublishRollback_UnknownVersionThrows()
    {
        var dir = NewTempDir();
        try
        {
            WritePacks(dir);
            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishRollbackAsync(
                "CORP", "v9.9", "MEMO-2", "Terry Owner", MemoFixtures.FixedAt,
                RuleApplicability.ProspectiveOnly, "rollback-hash-9", "no such version",
                "CORP-02", "ACT-9", MemoFixtures.FixedAt));
            Assert.Contains("v9.9", ex.Message);
            Assert.Contains("not found", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void OptionsFromConfig_BindsDocumentedKeys()
    {
        Assert.Equal("Compliance:CompanyKb", ComplianceEngine.CompanyKbConfigKey);
        Assert.Equal("Compliance:PackDirectory", ComplianceEngine.PackDirectoryConfigKey);
        Assert.Equal("company/company-kb.json", ComplianceEngine.DefaultCompanyKbPath);

        var requested = new List<string>();
        var options = ComplianceEngine.OptionsFromConfig(key =>
        {
            requested.Add(key);
            return key == ComplianceEngine.CompanyKbConfigKey ? "kb.json" : "packs";
        });
        Assert.Equal(["Compliance:CompanyKb", "Compliance:PackDirectory"], requested);
        Assert.Equal("kb.json", options.CompanyKbPath);
        Assert.Equal("packs", options.PackDirectory);

        var empty = ComplianceEngine.OptionsFromConfig(_ => null);
        Assert.Null(empty.CompanyKbPath);
        Assert.Null(empty.PackDirectory);
    }
}
