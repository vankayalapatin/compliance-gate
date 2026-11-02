// KB versioned loading plus the KB engine seam: highest-wins default
// loads, exact-version pinned loads, the publish path with its stale
// guard, space lists, and live pack rules. Test-double KB sets and temp
// directories only; the real versioned files are covered by the
// migration tests.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CompanyKbVersioningTests
{
    private static string KbJson(string version, bool audited = true) =>
        MemoFixtures.KbFileJson(MemoFixtures.TestKbFile(version, audited));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-kb-version-test", Guid.NewGuid().ToString("N"));
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

    private static void WriteKbSet(string dir, params (string Name, string Json)[] files)
    {
        foreach (var (name, json) in files)
        {
            File.WriteAllText(Path.Combine(dir, name), json);
        }
    }

    [Fact]
    public void HighestVersionWinsNumerically()
    {
        var inputs = new List<(string Name, string Json)>
        {
            ("company-kb.json", KbJson("kbv1", audited: false)),
            ("company-kb.kbv9.json", KbJson("kbv9")),
            ("company-kb.kbv10.json", KbJson("kbv10")),
        };
        Assert.Equal("kbv10", CompanyKbLoader.LoadVersioned(inputs).Version);
        Assert.Equal("kbv9", CompanyKbLoader.LoadVersionedAt(inputs, "kbv9").Version);
        Assert.Equal("kbv1", CompanyKbLoader.LoadVersionedAt(inputs, "kbv1").Version);
    }

    [Fact]
    public void FilenameMismatchMalformedDuplicateAndUnknownRefuse()
    {
        var mismatch = new List<(string Name, string Json)>
        {
            ("company-kb.json", KbJson("kbv1", audited: false)),
            ("company-kb.kbv10.json", KbJson("kbv9")),
        };
        var mismatchEx = Assert.Throws<InvalidOperationException>(() => CompanyKbLoader.LoadVersioned(mismatch));
        Assert.Contains("kbv10", mismatchEx.Message);
        Assert.Contains("kbv9", mismatchEx.Message);

        var malformed = new List<(string Name, string Json)>
        {
            ("company-kb.json", KbJson("kbv1", audited: false)),
            ("company-kb.draft.json", KbJson("kbv9")),
        };
        var malformedEx = Assert.Throws<InvalidOperationException>(() => CompanyKbLoader.LoadVersioned(malformed));
        Assert.Contains("draft", malformedEx.Message);
        Assert.Contains("kbv9", malformedEx.Message);

        var duplicate = new List<(string Name, string Json)>
        {
            ("company-kb.kbv9.json", KbJson("kbv9")),
            ("company-kb.kbv9.json", KbJson("kbv9")),
        };
        var duplicateEx = Assert.Throws<InvalidOperationException>(() => CompanyKbLoader.LoadVersioned(duplicate));
        Assert.Contains("twice", duplicateEx.Message);
        Assert.Contains("kbv9", duplicateEx.Message);

        var inputs = new List<(string Name, string Json)>
        {
            ("company-kb.json", KbJson("kbv1", audited: false)),
            ("company-kb.kbv9.json", KbJson("kbv9")),
        };
        var unknownEx = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.LoadVersionedAt(inputs, "kbv10"));
        Assert.Contains("Unknown KB version 'kbv10'", unknownEx.Message);
        Assert.Contains("kbv1", unknownEx.Message);
        Assert.Contains("kbv9", unknownEx.Message);

        var badVersionEx = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.LoadVersionedAt(inputs, "draft"));
        Assert.Contains("kbvN", badVersionEx.Message);

        var strayEx = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.LoadVersioned([("notes.txt", "{}")]));
        Assert.Contains("not a KB file", strayEx.Message);

        Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.LoadVersioned([]));
    }

    [Fact]
    public void FirstVersionToleratesMissingAuditLaterVersionsEnforce()
    {
        var v1 = CompanyKbLoader.Load(KbJson("kbv1", audited: false));
        Assert.Null(v1.Items[0].CreatedBy);
        Assert.Empty(v1.ToSnapshot().Finance);

        var unauditedLater = MemoFixtures.TestKbFile("kbv9", audited: false);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.Load(MemoFixtures.KbFileJson(unauditedLater)));
        Assert.Contains("missing audit", ex.Message);
        Assert.Contains("items#BAT-9", ex.Message);
        Assert.Contains("owner", ex.Message);
        Assert.Contains("status", ex.Message);
        Assert.Contains("effectiveDate", ex.Message);

        var badEntryVersion = MemoFixtures.TestKbFile("kbv9");
        var items = badEntryVersion.Items.ToList();
        items[0] = items[0] with { Version = "draft" };
        var bad = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.Load(MemoFixtures.KbFileJson(badEntryVersion with { Items = items })));
        Assert.Contains("kbvN", bad.Message);
    }

    [Fact]
    public void ReadVersionReadsOrRefuses()
    {
        Assert.Equal("kbv9", CompanyKbLoader.ReadVersion("company-kb.kbv9.json", KbJson("kbv9")));
        var missing = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.ReadVersion("company-kb.kbv9.json", """{"hash":"x"}"""));
        Assert.Contains("no version property", missing.Message);
        var malformed = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.ReadVersion("company-kb.kbv9.json", "{nope"));
        Assert.Contains("not valid JSON", malformed.Message);
    }

    [Fact]
    public async Task EngineLoadsHighestAndExactVersions()
    {
        var dir = NewTempDir();
        try
        {
            WriteKbSet(dir,
                ("company-kb.json", KbJson("kbv1", audited: false)),
                ("company-kb.kbv9.json", KbJson("kbv9")));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));

            var highest = await engine.LoadKbSnapshotAsync();
            Assert.Equal("kbv9", highest.KbVersion);
            Assert.Equal(2, highest.Items.Count);

            var pinned = await engine.LoadKbSnapshotAtAsync("kbv1");
            Assert.Equal("kbv1", pinned.KbVersion);

            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.LoadKbSnapshotAtAsync("kbv10"));
            Assert.Contains("Unknown KB version", unknown.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EngineMissingDirectoryAndEmptyDirectoryRefuse()
    {
        var missing = new ComplianceEngine(new ComplianceMemoOptions(
            CompanyKbPath: Path.Combine(Path.GetTempPath(), "gate-kb-no-such-dir", Guid.NewGuid().ToString("N"))));
        var missingEx = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.LoadKbSnapshotAsync());
        Assert.Contains("KB directory not found", missingEx.Message);
        Assert.Contains("Compliance:CompanyKb", missingEx.Message);

        var dir = NewTempDir();
        try
        {
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));
            var emptyEx = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadKbSnapshotAsync());
            Assert.Contains("no KB files", emptyEx.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EnginePublishMintsNextFileBesidePriors()
    {
        var dir = NewTempDir();
        try
        {
            var baseJson = KbJson("kbv9");
            WriteKbSet(dir, ("company-kb.kbv9.json", baseJson));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));

            var publication = await engine.PublishKbUpdateAsync(
                "kbv9",
                [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))],
                MemoFixtures.FixtureOperationsOwner, "Fixture reason.", "KB-ACT-1", MemoFixtures.FixedAt);

            Assert.Equal("company-kb.kbv10.json", publication.FileName);
            Assert.Equal("kbv10", publication.Activation.KbVersion);
            Assert.True(File.Exists(Path.Combine(dir, "company-kb.kbv10.json")));
            Assert.Equal(baseJson, File.ReadAllText(Path.Combine(dir, "company-kb.kbv9.json")));
            Assert.Equal("kbv10", (await engine.LoadKbSnapshotAsync()).KbVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EnginePublishRefusesStaleBaseAndUnknownVersion()
    {
        var dir = NewTempDir();
        try
        {
            WriteKbSet(dir,
                ("company-kb.json", KbJson("kbv1", audited: false)),
                ("company-kb.kbv9.json", KbJson("kbv9")));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));
            var changes = new[]
            {
                MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9")),
            };

            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishKbUpdateAsync(
                "kbv1", changes, MemoFixtures.FixtureOperationsOwner,
                "Fixture reason.", "KB-ACT-1", MemoFixtures.FixedAt));
            Assert.Contains("Stale KB base", stale.Message);
            Assert.Contains("kbv9", stale.Message);
            Assert.False(File.Exists(Path.Combine(dir, "company-kb.kbv2.json")));

            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PublishKbUpdateAsync(
                "kbv10", changes, MemoFixtures.FixtureOperationsOwner,
                "Fixture reason.", "KB-ACT-1", MemoFixtures.FixedAt));
            Assert.Contains("Unknown KB version", unknown.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EnginePublishDelegatedSucceedsThroughSeam()
    {
        var dir = NewTempDir();
        try
        {
            WriteKbSet(dir, ("company-kb.kbv9.json", KbJson("kbv9")));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));

            var publication = await engine.PublishKbUpdateAsync(
                "kbv9",
                [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))],
                "Dora Delegate", "Fixture reason.", "KB-ACT-9", MemoFixtures.FixedAt,
                MemoFixtures.FixtureOperationsOwner);

            Assert.Equal("company-kb.kbv10.json", publication.FileName);
            Assert.Equal("Dora Delegate", publication.Activation.Approver);
            Assert.Equal(MemoFixtures.FixtureOperationsOwner, publication.Activation.ApprovedFor);
            Assert.True(File.Exists(Path.Combine(dir, "company-kb.kbv10.json")));
            var added = CompanyKbLoader.Load(publication.Json).Items.Single(i => i.Id == "GLASS-9");
            Assert.Equal("Dora Delegate", added.ApprovedBy);
            Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.Owner);
            Assert.Equal("kbv10", (await engine.LoadKbSnapshotAsync()).KbVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EnginePublishDirectLeavesApprovedForEmpty()
    {
        var dir = NewTempDir();
        try
        {
            WriteKbSet(dir, ("company-kb.kbv9.json", KbJson("kbv9")));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));

            var publication = await engine.PublishKbUpdateAsync(
                "kbv9",
                [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))],
                MemoFixtures.FixtureOperationsOwner, "Fixture reason.", "KB-ACT-1", MemoFixtures.FixedAt);

            Assert.Null(publication.Activation.ApprovedFor);
            Assert.Equal(MemoFixtures.FixtureOperationsOwner, publication.Activation.Approver);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EngineListsSpacesWithOwnersSectionsAndMembers()
    {
        var dir = NewTempDir();
        try
        {
            WriteKbSet(dir, ("company-kb.kbv9.json", KbJson("kbv9")));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));

            var spaces = await engine.ListKbSpacesAsync();
            Assert.Equal(
                ["leadership", "finance", "procurement", "hr", "regulatory", "operations"],
                spaces.Select(s => s.Space).ToList());
            var leadership = spaces[0];
            Assert.Equal(MemoFixtures.FixtureLeadershipOwner, leadership.Owner);
            Assert.Equal("approver", leadership.OwnerRole);
            Assert.Equal(["legalEntities", "leadership"], leadership.Sections);
            var finance = spaces[1];
            Assert.Equal(MemoFixtures.FixtureFinanceOwner, finance.Owner);
            Assert.Equal("payment-approver", finance.OwnerRole);
            Assert.Equal(["fiscalCalendar", "approvalMatrix", "finance"], finance.Sections);
            var procurement = spaces[2];
            Assert.Equal(MemoFixtures.FixtureProcurementOwner, procurement.Owner);
            Assert.Equal(
                ["sourcingPolicies", "regionRules", "documents", "procurement"],
                procurement.Sections);
            Assert.Equal("hr", spaces[3].Space);
            Assert.Equal(MemoFixtures.FixtureHrOwner, spaces[3].Owner);
            Assert.Equal("regulatory", spaces[4].Space);
            Assert.Equal(MemoFixtures.FixtureRegulatoryOwner, spaces[4].Owner);
            Assert.Equal("operations", spaces[5].Space);
            Assert.Equal(MemoFixtures.FixtureOperationsOwner, spaces[5].Owner);
            Assert.Equal(
                [
                    MemoFixtures.FixtureFinanceOwner,
                    MemoFixtures.FixtureHrOwner,
                    MemoFixtures.FixtureLeadershipOwner,
                    MemoFixtures.FixtureOperationsOwner,
                    MemoFixtures.FixtureRegulatoryOwner,
                    MemoFixtures.FixtureProcurementOwner,
                ],
                finance.Members);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EngineSpaceListRefusesMissingOwnerRow()
    {
        var dir = NewTempDir();
        try
        {
            var file = MemoFixtures.TestKbFile(matrix: [
                new KbApprovalEntry("sourcing-policy", "approver", MemoFixtures.FixtureProcurementOwner,
                    "kbv9", "Terry Owner", MemoFixtures.FixedAt, "Terry Owner", MemoFixtures.FixedAt, "Terry Owner",
                    KbEntryStatus.Active, MemoFixtures.FixtureEffectiveDate),
            ]);
            WriteKbSet(dir, ("company-kb.kbv9.json", MemoFixtures.KbFileJson(file)));
            var engine = new ComplianceEngine(new ComplianceMemoOptions(CompanyKbPath: dir));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListKbSpacesAsync());
            Assert.Contains("no approval-matrix row", ex.Message);
            Assert.Contains("kb-edit-leadership", ex.Message);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void WikiSectionsProjectToSnapshotAndEnforceUniquenessAndAudit()
    {
        var file = MemoFixtures.TestKbFile(finance: [MemoFixtures.TestWikiEntry("limits-9")]);
        var snapshot = CompanyKbLoader.Load(MemoFixtures.KbFileJson(file)).ToSnapshot();
        Assert.Equal("limits-9", Assert.Single(snapshot.Finance).Id);
        Assert.Empty(snapshot.Hr);

        var dupes = MemoFixtures.TestKbFile(finance: [
            MemoFixtures.TestWikiEntry("limits-9"),
            MemoFixtures.TestWikiEntry("limits-9"),
        ]);
        var dupeEx = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.Load(MemoFixtures.KbFileJson(dupes)));
        Assert.Contains("duplicate id 'limits-9'", dupeEx.Message);

        var unaudited = MemoFixtures.TestKbFile(finance: [
            MemoFixtures.TestWikiEntry("limits-9", audited: false),
        ]);
        var auditEx = Assert.Throws<InvalidOperationException>(() =>
            CompanyKbLoader.Load(MemoFixtures.KbFileJson(unaudited)));
        Assert.Contains("finance#limits-9", auditEx.Message);
        Assert.Contains("missing audit", auditEx.Message);
    }

    [Fact]
    public async Task EngineListsLivePackRulesFromDirectory()
    {
        var dir = NewTempDir();
        try
        {
            foreach (var (name, json) in MemoFixtures.FourPacks())
            {
                var file = name.StartsWith("Packs.", StringComparison.Ordinal) ? name["Packs.".Length..] : name;
                File.WriteAllText(Path.Combine(dir, file), json);
            }

            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var rules = await engine.ListLivePackRulesAsync();
            Assert.Equal(5, rules.Count);
            var corp = rules.Single(r => r.RuleId == "CORP-02");
            Assert.Equal("CORP", corp.PackId);
            Assert.Equal("v0.2", corp.PackVersion);
            Assert.Equal(MemoRuleGate.Vendor, corp.Gate);
            Assert.True(corp.Enforced);
            Assert.Equal(MemoFixtures.FixtureProcurementOwner, corp.Owner);
            Assert.Equal(DateOnly.Parse("2026-04-01"), corp.AdoptedDate);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EngineListsLivePackRulesFromHighestVersions()
    {
        var dir = NewTempDir();
        try
        {
            foreach (var (name, json) in MemoFixtures.FourPacks())
            {
                var file = name.StartsWith("Packs.", StringComparison.Ordinal) ? name["Packs.".Length..] : name;
                File.WriteAllText(Path.Combine(dir, file), json);
            }

            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
            var proposal = MemoFixtures.Proposal(
                rule: MemoFixtures.CapRule("0.40"),
                statements: [new ProposalStatement("cap the share", "cap the share lower", 0.9m)],
                resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
                citations: [citation]);
            var validation = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
            Assert.True(validation.Ok, string.Join("; ", validation.Errors));
            await engine.PublishRuleUpdateAsync(
                "CORP", proposal, validation, MemoFixtures.Approval(proposal), ["seam-test"],
                "CORP-02", "ACT-1", MemoFixtures.FixedAt);

            var rules = await engine.ListLivePackRulesAsync();
            Assert.Equal("v0.3", rules.Single(r => r.RuleId == "CORP-02").PackVersion);
            Assert.Equal("OriginShareCap", rules.Single(r => r.RuleId == "CORP-02").RuleType);
            Assert.Equal(MemoFixtures.FixtureProcurementOwner, rules.Single(r => r.RuleId == "CORP-02").Owner);
            Assert.Equal(DateOnly.Parse("2026-04-01"), rules.Single(r => r.RuleId == "CORP-02").AdoptedDate);
            Assert.Equal("v0.2", rules.Single(r => r.RuleId == "BAT-LI-01").PackVersion);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task EngineListsLivePackRulesFromBundledPacks()
    {
        var engine = new ComplianceEngine();
        var rules = await engine.ListLivePackRulesAsync();
        Assert.Contains(rules, r => r.RuleId == "CORP-02" && r.PackVersion == "v0.2");
        Assert.Contains(rules, r => r.RuleId == "BAT-LI-01" && r.Gate == MemoRuleGate.Vendor);
        Assert.Contains(rules, r => r.RuleId == "BAT-LI-03" && r.Gate == MemoRuleGate.Dock);
        Assert.Contains(rules, r => r.RuleId == "ELEC-04" && r.Gate == MemoRuleGate.PrePay);
        var janAdopted = DateOnly.Parse("2026-01-01");
        var corpAdopted = DateOnly.Parse("2026-10-01");
        Assert.Contains(rules, r => r.RuleId == "BAT-LI-01" && r.Owner == "Maya Planner" && r.AdoptedDate == janAdopted);
        Assert.Contains(rules, r => r.RuleId == "ELEC-01" && r.Owner == "Dana Director" && r.AdoptedDate == janAdopted);
        Assert.Contains(rules, r => r.RuleId == "SCR-01" && r.Owner == "Finn Finance" && r.AdoptedDate == janAdopted);
        Assert.Contains(rules, r => r.RuleId == "CORP-02" && r.Owner == "Dana Director" && r.AdoptedDate == corpAdopted);
    }

    [Fact]
    public async Task EngineListsLivePackRulesCarrySource()
    {
        var dir = NewTempDir();
        try
        {
            foreach (var (name, json) in MemoFixtures.FourPacks())
            {
                var file = name.StartsWith("Packs.", StringComparison.Ordinal) ? name["Packs.".Length..] : name;
                File.WriteAllText(Path.Combine(dir, file), json);
            }

            var engine = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: dir));
            var rules = await engine.ListLivePackRulesAsync();
            Assert.Equal("Fixture memo", rules.Single(r => r.RuleId == "CORP-02").Source);
            Assert.Equal("Fixture source", rules.Single(r => r.RuleId == "BAT-LI-01").Source);
            Assert.All(rules, r => Assert.False(string.IsNullOrWhiteSpace(r.Source)));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
