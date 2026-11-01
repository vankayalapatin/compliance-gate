// kbv2 migration: the checked-in company-kb.kbv2.json equals a fresh
// writer-path build over the frozen kbv1 file, every carried entry keeps
// per-entry kbv1 with system backfill, the six kb-edit-* rows and eleven
// wiki seeds enter at kbv2, and grounded seed content pins to runtime
// scenario reads. Migration refusals use small fixture bases.
using System.Globalization;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CompanyKbMigrationTests
{
    private static IReadOnlyList<KbMigrationOwner> FixtureOwners() =>
    [
        new(CompanyKbSpaces.Leadership, MemoFixtures.FixtureLeadershipOwner, "approver"),
        new(CompanyKbSpaces.Finance, MemoFixtures.FixtureFinanceOwner, "payment-approver"),
        new(CompanyKbSpaces.Procurement, MemoFixtures.FixtureProcurementOwner, "approver"),
        new(CompanyKbSpaces.Hr, MemoFixtures.FixtureHrOwner, "approver"),
        new(CompanyKbSpaces.Regulatory, MemoFixtures.FixtureRegulatoryOwner, "approver"),
        new(CompanyKbSpaces.Operations, MemoFixtures.FixtureOperationsOwner, "requester"),
    ];

    private static IReadOnlyList<KbMigrationWikiSeed> FixtureSeeds() =>
    [
        new("finance", new KbWikiEntry("limits-9", "limits-9", "Body nine.")),
    ];

    private static string PreAuditBase() =>
        MemoFixtures.KbFileJson(MemoFixtures.TestKbFile("kbv1", audited: false, matrix: [
            new KbApprovalEntry("sourcing-policy", "approver", MemoFixtures.FixtureProcurementOwner),
        ]));

    private static KbPublication Migrate(
        string? baseJson = null,
        string baseVersion = "kbv1",
        IReadOnlyList<KbMigrationOwner>? owners = null,
        IReadOnlyList<KbMigrationWikiSeed>? seeds = null) =>
        CompanyKbWriter.BuildMigration(
            baseJson ?? PreAuditBase(), baseVersion,
            owners ?? FixtureOwners(), seeds ?? FixtureSeeds(),
            "Fixture migration.", "Fixture comment.",
            "KB-MIGRATION-9", MemoFixtures.FixedAt);

    private static string CheckedInKbv2Path()
    {
        var kbv1Path = CompanyKbLoader.ResolvePath("company/company-kb.json");
        var path = Path.Combine(Path.GetDirectoryName(kbv1Path)!, "company-kb.kbv2.json");
        Assert.True(File.Exists(path), $"Checked-in kbv2 file not found: {path}");
        return path;
    }

    [Fact]
    public async Task CheckedInKbv2MatchesWriterOutput()
    {
        var (erp, kbv1) = await Kbv2Seeds.LoadSourcesAsync();
        var owners = await Kbv2Seeds.SpaceOwnersAsync(erp);
        var seeds = await Kbv2Seeds.BuildSeedsAsync(erp, kbv1);

        var kbv1Path = CompanyKbLoader.ResolvePath("company/company-kb.json");
        var expected = CompanyKbWriter.BuildMigration(
            File.ReadAllText(kbv1Path), Kbv2Seeds.BaseVersion,
            owners, seeds, Kbv2Seeds.Reason, Kbv2Seeds.Comment,
            Kbv2Seeds.ActivationId, Kbv2Seeds.MigrationAt);

        Assert.Equal("company-kb.kbv2.json", expected.FileName);
        Assert.Equal("kbv2", expected.Activation.KbVersion);
        Assert.Equal("kbv1", expected.Activation.PriorKbVersion);
        Assert.Equal(17, expected.Activation.ChangedEntries.Count);

        var checkedIn = File.ReadAllText(CheckedInKbv2Path());
        Assert.Equal(expected.Json, checkedIn);

        var file = CompanyKbLoader.Load(checkedIn);
        Assert.Equal("kbv2", file.Version);
        Assert.Equal(CompanyKbHash.Compute(checkedIn), file.Hash);
    }

    [Fact]
    public void Kbv1StaysFrozenBesideKbv2()
    {
        var kbv1 = CompanyKbLoader.LoadFromFile("company/company-kb.json");
        Assert.Equal("kbv1", kbv1.Version);
        Assert.Empty(kbv1.ToSnapshot().Finance);

        var dir = Path.GetDirectoryName(CompanyKbLoader.ResolvePath("company/company-kb.json"))!;
        var inputs = Directory.GetFiles(dir, "company-kb*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        Assert.Equal("kbv2", CompanyKbLoader.LoadVersioned(inputs).Version);
        Assert.Equal("kbv1", CompanyKbLoader.LoadVersionedAt(inputs, "kbv1").Version);
        Assert.Equal("kbv2", CompanyKbLoader.LoadVersionedAt(inputs, "kbv2").Version);
    }

    [Fact]
    public void Kbv2BackfillsEveryCarriedEntry()
    {
        var file = CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv2Path()));

        void Stamps(
            string section, string id, string? version,
            string? createdBy, DateTimeOffset? createdAt,
            string? modifiedBy, DateTimeOffset? modifiedAt,
            string? approvedBy, string? owner, string? expectedOwner,
            KbEntryStatus? status, DateOnly? effectiveDate)
        {
            Assert.Equal("kbv1", version);
            Assert.Equal("system", createdBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, createdAt);
            Assert.Equal("system", modifiedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, modifiedAt);
            Assert.Equal("system", approvedBy);
            Assert.Equal(expectedOwner, owner);
            Assert.Equal(KbEntryStatus.Active, status);
            Assert.Equal(Kbv2Seeds.MigrationDate, effectiveDate);
        }

        foreach (var e in file.LegalEntities)
        {
            Stamps("legalEntities", e.Id, e.Version, e.CreatedBy, e.CreatedAt, e.ModifiedBy, e.ModifiedAt,
                e.ApprovedBy, e.Owner, Kbv2Seeds.DanaDirector, e.Status, e.EffectiveDate);
        }

        foreach (var s in file.SitesWarehouses)
        {
            Stamps("sitesWarehouses", s.Id, s.Version, s.CreatedBy, s.CreatedAt, s.ModifiedBy, s.ModifiedAt,
                s.ApprovedBy, s.Owner, Kbv2Seeds.MayaPlanner, s.Status, s.EffectiveDate);
        }

        Assert.Null(file.FiscalCalendar);

        var carriedMatrix = file.ApprovalMatrix.Where(a => !a.Action.StartsWith("kb-edit-", StringComparison.Ordinal));
        Assert.Equal(4, carriedMatrix.Count());
        foreach (var a in carriedMatrix)
        {
            Assert.Equal("kbv1", a.Version);
            Assert.Equal("system", a.CreatedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, a.CreatedAt);
            Assert.Equal("system", a.ModifiedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, a.ModifiedAt);
            Assert.Equal("system", a.ApprovedBy);
            Assert.Equal(KbEntryStatus.Active, a.Status);
            Assert.Equal(Kbv2Seeds.MigrationDate, a.EffectiveDate);
        }

        foreach (var i in file.Items)
        {
            Stamps("items", i.Id, i.Version, i.CreatedBy, i.CreatedAt, i.ModifiedBy, i.ModifiedAt,
                i.ApprovedBy, i.Owner, Kbv2Seeds.MayaPlanner, i.Status, i.EffectiveDate);
        }

        foreach (var p in file.SourcingPolicies)
        {
            Stamps("sourcingPolicies", p.Id, p.Version, p.CreatedBy, p.CreatedAt, p.ModifiedBy, p.ModifiedAt,
                p.ApprovedBy, p.Owner, Kbv2Seeds.DanaDirector, p.Status, p.EffectiveDate);
        }

        foreach (var r in file.RegionRules)
        {
            Stamps("regionRules", r.Id, r.Version, r.CreatedBy, r.CreatedAt, r.ModifiedBy, r.ModifiedAt,
                r.ApprovedBy, r.Owner, Kbv2Seeds.DanaDirector, r.Status, r.EffectiveDate);
        }

        foreach (var d in file.Documents)
        {
            Stamps("documents", d.Id, d.Version, d.CreatedBy, d.CreatedAt, d.ModifiedBy, d.ModifiedAt,
                d.ApprovedBy, d.Owner, Kbv2Seeds.DanaDirector, d.Status, d.EffectiveDate);
        }
    }

    [Fact]
    public async Task Kbv2SeedsSixKbEditRows()
    {
        var (erp, _) = await Kbv2Seeds.LoadSourcesAsync();
        var file = CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv2Path()));

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kb-edit-leadership"] = Kbv2Seeds.DanaDirector,
            ["kb-edit-finance"] = Kbv2Seeds.FinnFinance,
            ["kb-edit-procurement"] = Kbv2Seeds.DanaDirector,
            ["kb-edit-hr"] = Kbv2Seeds.DanaDirector,
            ["kb-edit-regulatory"] = Kbv2Seeds.DanaDirector,
            ["kb-edit-operations"] = Kbv2Seeds.MayaPlanner,
        };
        var rows = file.ApprovalMatrix.Where(a => a.Action.StartsWith("kb-edit-", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, rows.Count);
        foreach (var (action, owner) in expected)
        {
            var row = Assert.Single(rows, r => r.Action == action);
            Assert.Equal(owner, row.Owner);
            var user = await erp.GetUserAsync(owner);
            Assert.NotNull(user);
            Assert.Equal(user.Role, row.OwnerRole);
            Assert.Equal("kbv2", row.Version);
            Assert.Equal("system", row.CreatedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, row.CreatedAt);
            Assert.Equal("system", row.ModifiedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, row.ModifiedAt);
            Assert.Equal("system", row.ApprovedBy);
            Assert.Equal(KbEntryStatus.Active, row.Status);
            Assert.Equal(Kbv2Seeds.MigrationDate, row.EffectiveDate);
        }
    }

    [Fact]
    public void Kbv2SeedsElevenWikiEntries()
    {
        var file = CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv2Path()));
        var snapshot = file.ToSnapshot();

        var expected = new Dictionary<string, (string Section, string Owner)>
        {
            ["company-identity"] = ("leadership", Kbv2Seeds.DanaDirector),
            ["signing-limits"] = ("finance", Kbv2Seeds.FinnFinance),
            ["delegation"] = ("finance", Kbv2Seeds.FinnFinance),
            ["payment-terms"] = ("finance", Kbv2Seeds.FinnFinance),
            ["sourcing-policy"] = ("procurement", Kbv2Seeds.DanaDirector),
            ["approval-roles"] = ("hr", Kbv2Seeds.DanaDirector),
            ["data-residency"] = ("regulatory", Kbv2Seeds.DanaDirector),
            ["un38-3-summary"] = ("regulatory", Kbv2Seeds.DanaDirector),
            ["iata-marks"] = ("regulatory", Kbv2Seeds.DanaDirector),
            ["critical-parts"] = ("operations", Kbv2Seeds.MayaPlanner),
            ["dock-safety"] = ("operations", Kbv2Seeds.MayaPlanner),
        };

        IReadOnlyList<KbWikiEntry> Section(string section) => section switch
        {
            "leadership" => snapshot.Leadership,
            "finance" => snapshot.Finance,
            "procurement" => snapshot.Procurement,
            "hr" => snapshot.Hr,
            "regulatory" => snapshot.Regulatory,
            "operations" => snapshot.Operations,
            _ => throw new InvalidOperationException($"Unknown wiki section '{section}'."),
        };

        Assert.Single(snapshot.Leadership);
        Assert.Equal(3, snapshot.Finance.Count);
        Assert.Single(snapshot.Procurement);
        Assert.Single(snapshot.Hr);
        Assert.Equal(3, snapshot.Regulatory.Count);
        Assert.Equal(2, snapshot.Operations.Count);

        foreach (var (id, (section, owner)) in expected)
        {
            var entry = Assert.Single(Section(section), w => w.Id == id);
            Assert.Equal(id, entry.Title);
            Assert.False(string.IsNullOrWhiteSpace(entry.Body));
            Assert.Equal("kbv2", entry.Version);
            Assert.Equal("system", entry.CreatedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, entry.CreatedAt);
            Assert.Equal("system", entry.ModifiedBy);
            Assert.Equal(Kbv2Seeds.MigrationAt, entry.ModifiedAt);
            Assert.Equal("system", entry.ApprovedBy);
            Assert.Equal(owner, entry.Owner);
            Assert.Equal(KbEntryStatus.Active, entry.Status);
            Assert.Equal(Kbv2Seeds.MigrationDate, entry.EffectiveDate);
        }
    }

    [Fact]
    public async Task WikiSeedBodiesCarryRuntimeValues()
    {
        var (erp, kbv1) = await Kbv2Seeds.LoadSourcesAsync();
        var snapshot = CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv2Path())).ToSnapshot();
        string Body(IReadOnlyList<KbWikiEntry> section, string id) =>
            section.Single(w => w.Id == id).Body;

        var limits = Body(snapshot.Finance, "signing-limits");
        foreach (var user in await erp.GetUsersAsync())
        {
            Assert.Contains(user.UserId, limits);
            Assert.Contains(user.Role, limits);
            Assert.Contains(user.SigningLimit.ToString(CultureInfo.InvariantCulture), limits);
            Assert.Contains(user.LimitCurrency, limits);
        }

        var delegation = Body(snapshot.Finance, "delegation");
        foreach (var d in await erp.GetDelegationsAsync())
        {
            Assert.Contains(d.FromUser, delegation);
            Assert.Contains(d.ToUser, delegation);
            Assert.Contains(d.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), delegation);
            Assert.Contains(d.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), delegation);
        }

        var terms = Body(snapshot.Finance, "payment-terms");
        foreach (var mirror in kbv1.MirrorPaymentTerms)
        {
            var term = await erp.GetPaymentTermsAsync(mirror.Id);
            Assert.NotNull(term);
            Assert.Contains(term.TermsId, terms);
            Assert.Contains(term.NetDays.ToString(CultureInfo.InvariantCulture), terms);
        }

        var parts = Body(snapshot.Operations, "critical-parts");
        foreach (var item in kbv1.Items)
        {
            var erpItem = await erp.GetItemAsync(item.Id);
            Assert.NotNull(erpItem);
            Assert.Contains($"{erpItem.ItemId}: {(erpItem.IsCritical ? "critical" : "not critical")}.", parts);
        }

        var identity = Body(snapshot.Leadership, "company-identity");
        foreach (var entity in kbv1.LegalEntities)
        {
            Assert.Contains(entity.Id, identity);
            Assert.Contains(entity.Name, identity);
            Assert.Contains(entity.Country, identity);
            Assert.Contains(entity.Currency, identity);
        }

        var policy = Body(snapshot.Procurement, "sourcing-policy");
        foreach (var id in new[] { "origin-cap", "vendor-cap" })
        {
            var sourcing = kbv1.SourcingPolicies.Single(p => p.Id == id);
            foreach (var value in sourcing.Params.Values)
            {
                Assert.Contains(value, policy);
            }
        }

        Assert.Contains("CORP-02", policy);

        var roles = Body(snapshot.Hr, "approval-roles");
        foreach (var user in await erp.GetUsersAsync())
        {
            Assert.Contains(user.UserId, roles);
            Assert.Contains(user.Role, roles);
        }

        foreach (var row in kbv1.ApprovalMatrix)
        {
            Assert.Contains(row.Action, roles);
            Assert.Contains(row.OwnerRole, roles);
            Assert.Contains(row.Owner, roles);
        }

        var un383 = Body(snapshot.Regulatory, "un38-3-summary");
        var un383Rule = RuleCatalog.Find("BAT-LI-01");
        Assert.Contains("BAT-LI-01", un383);
        Assert.Contains(un383Rule.Title, un383);
        Assert.Contains(un383Rule.Source, un383);

        var marks = Body(snapshot.Regulatory, "iata-marks");
        foreach (var id in new[] { "BAT-LI-03", "BAT-LI-04" })
        {
            var rule = RuleCatalog.Find(id);
            Assert.Contains(id, marks);
            Assert.Contains(rule.Title, marks);
            Assert.Contains(rule.Source, marks);
        }
    }

    [Fact]
    public void ApprovedPolicyTextsAreVerbatim()
    {
        var snapshot = CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv2Path())).ToSnapshot();
        Assert.Equal(
            Kbv2Seeds.DockSafetyText,
            snapshot.Operations.Single(w => w.Id == "dock-safety").Body);
        Assert.Equal(
            Kbv2Seeds.DataResidencyText,
            snapshot.Regulatory.Single(w => w.Id == "data-residency").Body);
    }

    [Fact]
    public void MigrationReplaysByteIdentical()
    {
        var a = Migrate();
        var b = Migrate();
        Assert.Equal(a.Json, b.Json);
        Assert.Equal("company-kb.kbv2.json", a.FileName);
        Assert.Equal("kbv2", a.Activation.KbVersion);
        Assert.Equal("kbv1", a.Activation.PriorKbVersion);
        Assert.Equal("finance", a.Activation.Space);
        Assert.Equal("system", a.Activation.Approver);
        Assert.Null(a.Activation.ApprovedFor);
        Assert.Equal(7, a.Activation.ChangedEntries.Count);
        var loaded = CompanyKbLoader.Load(a.Json);
        Assert.Equal("kbv2", loaded.Version);
        Assert.Single(loaded.Finance!);
    }

    [Fact]
    public void MigrationRefusals()
    {
        var auditedBase = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile("kbv9"));
        var audited = Assert.Throws<InvalidOperationException>(() => Migrate(auditedBase, "kbv9"));
        Assert.Contains("already carries audit", audited.Message);

        var stale = Assert.Throws<InvalidOperationException>(() => Migrate(PreAuditBase(), "kbv2"));
        Assert.Contains("base mismatch", stale.Message);

        var drifted = PreAuditBase() + " ";
        var drift = Assert.Throws<InvalidOperationException>(() => Migrate(drifted));
        Assert.Contains("hash mismatch", drift.Message);

        var rowBase = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile("kbv1", audited: false, matrix: [
            new KbApprovalEntry("sourcing-policy", "approver", MemoFixtures.FixtureProcurementOwner),
            new KbApprovalEntry("kb-edit-finance", "payment-approver", MemoFixtures.FixtureFinanceOwner),
        ]));
        var row = Assert.Throws<InvalidOperationException>(() => Migrate(rowBase));
        Assert.Contains("kb-edit-finance", row.Message);

        var withWiki = MemoFixtures.TestKbFile("kbv1", audited: false);
        var wikiBase = MemoFixtures.KbFileJson(withWiki with
        {
            Finance = [new KbWikiEntry("limits-9", "limits-9", "Body nine.")],
        });
        var wiki = Assert.Throws<InvalidOperationException>(() => Migrate(wikiBase));
        Assert.Contains("wiki entries", wiki.Message);

        var missing = Assert.Throws<InvalidOperationException>(() => Migrate(
            owners: FixtureOwners().Where(o => o.Space != CompanyKbSpaces.Hr).ToList()));
        Assert.Contains("hr", missing.Message);

        var dupes = FixtureOwners().ToList();
        dupes.Add(FixtureOwners()[0]);
        var dupe = Assert.Throws<InvalidOperationException>(() => Migrate(owners: dupes));
        Assert.Contains("twice", dupe.Message);

        var swapped = FixtureOwners().ToList();
        swapped[0] = swapped[0] with { Space = "nope" };
        var unknownSpace = Assert.Throws<InvalidOperationException>(() => Migrate(owners: swapped));
        Assert.Contains("unknown space 'nope'", unknownSpace.Message);

        var blanked = FixtureOwners().ToList();
        blanked[0] = blanked[0] with { Owner = " " };
        var blank = Assert.Throws<InvalidOperationException>(() => Migrate(owners: blanked));
        Assert.Contains("no owner", blank.Message);

        var roled = FixtureOwners().ToList();
        roled[0] = roled[0] with { OwnerRole = " " };
        var role = Assert.Throws<InvalidOperationException>(() => Migrate(owners: roled));
        Assert.Contains("no owner role", role.Message);

        var nonWiki = Assert.Throws<InvalidOperationException>(() => Migrate(
            seeds: [new KbMigrationWikiSeed("items", new KbWikiEntry("BAT-9", "BAT-9", "Body."))]));
        Assert.Contains("non-wiki section", nonWiki.Message);

        var blankTitle = Assert.Throws<InvalidOperationException>(() => Migrate(
            seeds: [new KbMigrationWikiSeed("finance", new KbWikiEntry("id-9", " ", "Body."))]));
        Assert.Contains("empty title", blankTitle.Message);

        var dupeSeeds = Assert.Throws<InvalidOperationException>(() => Migrate(
            seeds: [.. FixtureSeeds(), .. FixtureSeeds()]));
        Assert.Contains("more than once", dupeSeeds.Message);
    }
}
