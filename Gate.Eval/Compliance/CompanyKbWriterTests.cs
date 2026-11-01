// Company KB writer: one space per version, space-owner approval (direct
// or delegated), no self-approval, loud refusals for every gate
// violation. Test-double KB files and temp directories only; the real
// versioned files are covered by the migration tests.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CompanyKbWriterTests
{
    private const string FixtureDelegate = "Dora Delegate";

    private static string BaseJson(string version = "kbv9") =>
        MemoFixtures.KbFileJson(MemoFixtures.TestKbFile(version));

    private static KbPublication Build(
        string baseJson,
        string baseVersion,
        IReadOnlyList<KbEntryChange> changes,
        string approvedBy = MemoFixtures.FixtureOperationsOwner,
        string reason = "Fixture reason.",
        string activationId = "KB-ACT-1",
        DateTimeOffset? publishedAt = null,
        string? approvedFor = null) =>
        CompanyKbWriter.BuildNextVersion(
            baseJson, baseVersion, changes, approvedBy, reason,
            activationId, publishedAt ?? MemoFixtures.FixedAt, approvedFor);

    [Fact]
    public void VersionsParseCompareAndBumpNumerically()
    {
        Assert.Equal(9, CompanyKbVersions.Parse("kbv9"));
        Assert.Equal(2, CompanyKbVersions.Parse("kbv2"));
        Assert.True(CompanyKbVersions.Compare("kbv10", "kbv9") > 0);
        Assert.Equal(0, CompanyKbVersions.Compare("kbv9", "kbv9"));
        Assert.True(CompanyKbVersions.Compare("kbv9", "kbv10") < 0);
        Assert.Equal("kbv10", CompanyKbVersions.Next("kbv9"));
        Assert.Equal("company-kb.kbv10.json", CompanyKbWriter.FileNameFor("kbv10"));
    }

    [Fact]
    public void MalformedVersionsRefuse()
    {
        Assert.Throws<InvalidOperationException>(() => CompanyKbVersions.Parse("v9"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbVersions.Parse("kbv0"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbVersions.Parse("kbv"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbVersions.Parse("draft"));
        var ex = Assert.Throws<InvalidOperationException>(() => CompanyKbVersions.Next("draft"));
        Assert.Contains("kbvN", ex.Message);
    }

    [Fact]
    public void SpaceMapGroupsSectionsPerSchema()
    {
        Assert.Equal(
            ["leadership", "finance", "procurement", "hr", "regulatory", "operations"],
            CompanyKbSpaces.All);
        Assert.Equal("leadership", CompanyKbSpaces.SpaceFor("legalEntities"));
        Assert.Equal("leadership", CompanyKbSpaces.SpaceFor("leadership"));
        Assert.Equal("finance", CompanyKbSpaces.SpaceFor("fiscalCalendar"));
        Assert.Equal("finance", CompanyKbSpaces.SpaceFor("approvalMatrix"));
        Assert.Equal("finance", CompanyKbSpaces.SpaceFor("finance"));
        Assert.Equal("procurement", CompanyKbSpaces.SpaceFor("sourcingPolicies"));
        Assert.Equal("procurement", CompanyKbSpaces.SpaceFor("regionRules"));
        Assert.Equal("procurement", CompanyKbSpaces.SpaceFor("documents"));
        Assert.Equal("procurement", CompanyKbSpaces.SpaceFor("procurement"));
        Assert.Equal("hr", CompanyKbSpaces.SpaceFor("hr"));
        Assert.Equal("regulatory", CompanyKbSpaces.SpaceFor("regulatory"));
        Assert.Equal("operations", CompanyKbSpaces.SpaceFor("sitesWarehouses"));
        Assert.Equal("operations", CompanyKbSpaces.SpaceFor("items"));
        Assert.Equal("operations", CompanyKbSpaces.SpaceFor("operations"));
        Assert.Equal(
            ["legalEntities", "leadership"],
            CompanyKbSpaces.SectionsFor("leadership"));
        Assert.Equal(
            ["fiscalCalendar", "approvalMatrix", "finance"],
            CompanyKbSpaces.SectionsFor("finance"));
        Assert.Equal(
            ["sourcingPolicies", "regionRules", "documents", "procurement"],
            CompanyKbSpaces.SectionsFor("procurement"));
        Assert.Equal(["hr"], CompanyKbSpaces.SectionsFor("hr"));
        Assert.Equal(["regulatory"], CompanyKbSpaces.SectionsFor("regulatory"));
        Assert.Equal(
            ["sitesWarehouses", "items", "operations"],
            CompanyKbSpaces.SectionsFor("operations"));
        Assert.Equal("kb-edit-leadership", CompanyKbSpaces.OwnerActionFor("leadership"));
        Assert.Equal("kb-edit-finance", CompanyKbSpaces.OwnerActionFor("finance"));
        Assert.Equal("kb-edit-procurement", CompanyKbSpaces.OwnerActionFor("procurement"));
        Assert.Equal("kb-edit-hr", CompanyKbSpaces.OwnerActionFor("hr"));
        Assert.Equal("kb-edit-regulatory", CompanyKbSpaces.OwnerActionFor("regulatory"));
        Assert.Equal("kb-edit-operations", CompanyKbSpaces.OwnerActionFor("operations"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbSpaces.SpaceFor("mirrorUsers"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbSpaces.SectionsFor("nope"));
        Assert.Throws<InvalidOperationException>(() => CompanyKbSpaces.OwnerActionFor("nope"));
    }

    [Fact]
    public void AddMintsNextVersionWithStampedAudit()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9", false))]);

        Assert.Equal("company-kb.kbv10.json", publication.FileName);
        Assert.Equal("kbv10", publication.Activation.KbVersion);
        Assert.Equal("kbv9", publication.Activation.PriorKbVersion);
        Assert.Equal("operations", publication.Activation.Space);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, publication.Activation.Approver);
        Assert.Null(publication.Activation.ApprovedFor);
        Assert.Equal(["items#GLASS-9"], publication.Activation.ChangedEntries);

        var next = CompanyKbLoader.Load(publication.Json);
        Assert.Equal("kbv10", next.Version);
        var added = next.Items.Single(i => i.Id == "GLASS-9");
        Assert.False(added.Critical);
        Assert.Equal("kbv10", added.Version);
        Assert.Equal(MemoFixtures.FixtureEditor, added.CreatedBy);
        Assert.Equal(MemoFixtures.FixedAt, added.CreatedAt);
        Assert.Equal(MemoFixtures.FixtureEditor, added.ModifiedBy);
        Assert.Equal(MemoFixtures.FixedAt, added.ModifiedAt);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.ApprovedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.Owner);
        Assert.Equal(KbEntryStatus.Active, added.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, added.EffectiveDate);

        var carried = next.Items.Single(i => i.Id == "BAT-9");
        Assert.Equal("kbv9", carried.Version);
        Assert.Equal(MemoFixtures.FixtureProcurementOwner, carried.CreatedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, carried.Owner);
        Assert.Equal(KbEntryStatus.Active, carried.Status);
    }

    [Fact]
    public void TouchedEntriesCarryTheirSpaceOwner()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "legalEntities", "LE-10", KbChangeKind.Add, MemoFixtures.LegalEntityEntryJson("LE-10"))],
            approvedBy: MemoFixtures.FixtureLeadershipOwner);

        Assert.Equal("leadership", publication.Activation.Space);
        var added = CompanyKbLoader.Load(publication.Json).LegalEntities.Single(e => e.Id == "LE-10");
        Assert.Equal(MemoFixtures.FixtureLeadershipOwner, added.Owner);
        Assert.Equal(KbEntryStatus.Active, added.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, added.EffectiveDate);
    }

    [Fact]
    public void EditReplacesWholesalePreservingCreation()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "sourcingPolicies", "origin-cap", KbChangeKind.Edit,
                MemoFixtures.PolicyEntryJson("origin-cap", "0.40"))],
            approvedBy: MemoFixtures.FixtureProcurementOwner);

        Assert.Equal("procurement", publication.Activation.Space);
        var next = CompanyKbLoader.Load(publication.Json);
        var policy = next.SourcingPolicies.Single(p => p.Id == "origin-cap");
        Assert.Equal("0.40", policy.Params["cap"]);
        Assert.Equal("kbv10", policy.Version);
        Assert.Equal(MemoFixtures.FixtureProcurementOwner, policy.CreatedBy);
        Assert.Equal(MemoFixtures.FixedAt, policy.CreatedAt);
        Assert.Equal(MemoFixtures.FixtureEditor, policy.ModifiedBy);
        Assert.Equal(MemoFixtures.FixtureProcurementOwner, policy.ApprovedBy);
        Assert.Equal(MemoFixtures.FixtureProcurementOwner, policy.Owner);
        Assert.Equal(KbEntryStatus.Active, policy.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, policy.EffectiveDate);
    }

    [Fact]
    public void EditPreservesPriorStatus()
    {
        var file = MemoFixtures.TestKbFile();
        var items = file.Items.ToList();
        items[0] = items[0] with { Status = KbEntryStatus.Retired };
        var retiredBase = MemoFixtures.KbFileJson(file with { Items = items });

        var publication = Build(
            retiredBase, "kbv9",
            [MemoFixtures.KbChange(
                "items", "BAT-9", KbChangeKind.Edit, MemoFixtures.ItemEntryJson("BAT-9", false))]);

        var edited = CompanyKbLoader.Load(publication.Json).Items.Single(i => i.Id == "BAT-9");
        Assert.False(edited.Critical);
        Assert.Equal("kbv10", edited.Version);
        Assert.Equal(KbEntryStatus.Retired, edited.Status);
        Assert.Equal(MemoFixtures.FixtureProcurementOwner, edited.CreatedBy);
        Assert.Equal(MemoFixtures.FixtureEditor, edited.ModifiedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, edited.Owner);
    }

    [Fact]
    public void FinanceEditNeedsFinanceOwner()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "approvalMatrix", "sourcing-policy", KbChangeKind.Edit,
                MemoFixtures.MatrixEntryJson("sourcing-policy", "approver", "Nina New"))],
            approvedBy: MemoFixtures.FixtureFinanceOwner);

        Assert.Equal("finance", publication.Activation.Space);
        var next = CompanyKbLoader.Load(publication.Json);
        var row = next.ApprovalMatrix.Single(a => a.Action == "sourcing-policy");
        Assert.Equal("Nina New", row.Owner);
        Assert.Equal(KbEntryStatus.Active, row.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, row.EffectiveDate);
    }

    [Fact]
    public void HrWikiAddNeedsHrOwner()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "hr", "roles-9", KbChangeKind.Add, MemoFixtures.WikiEntryJson("roles-9"))],
            approvedBy: MemoFixtures.FixtureHrOwner);

        Assert.Equal("hr", publication.Activation.Space);
        Assert.Equal(["hr#roles-9"], publication.Activation.ChangedEntries);
        var added = CompanyKbLoader.Load(publication.Json).Hr!.Single(w => w.Id == "roles-9");
        Assert.Equal("Title Nine", added.Title);
        Assert.Equal("Body nine.", added.Body);
        Assert.Equal("kbv10", added.Version);
        Assert.Equal(MemoFixtures.FixtureHrOwner, added.Owner);
        Assert.Equal(KbEntryStatus.Active, added.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, added.EffectiveDate);
    }

    [Fact]
    public void WikiEditPreservesCreationAndTags()
    {
        var seeded = MemoFixtures.TestWikiEntry("limits-9") with { Tags = ["limits", "erp"] };
        var baseJson = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile(finance: [seeded]));

        var publication = Build(
            baseJson, "kbv9",
            [MemoFixtures.KbChange(
                "finance", "limits-9", KbChangeKind.Edit,
                """{"id":"limits-9","title":"Title limits-9","body":"Body revised.","tags":["limits","erp"]}""")],
            approvedBy: MemoFixtures.FixtureFinanceOwner);

        var edited = CompanyKbLoader.Load(publication.Json).Finance!.Single(w => w.Id == "limits-9");
        Assert.Equal("Body revised.", edited.Body);
        Assert.Equal(["limits", "erp"], edited.Tags);
        Assert.Equal("kbv10", edited.Version);
        Assert.Equal(MemoFixtures.FixtureEditor, edited.CreatedBy);
        Assert.Equal(MemoFixtures.FixedAt, edited.CreatedAt);
        Assert.Equal(MemoFixtures.FixtureFinanceOwner, edited.ApprovedBy);
        Assert.Equal(MemoFixtures.FixtureFinanceOwner, edited.Owner);
    }

    [Fact]
    public void FiscalCalendarAddsOnceThenEdits()
    {
        var added = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "fiscalCalendar", "current", KbChangeKind.Add, MemoFixtures.FiscalEntryJson())],
            approvedBy: MemoFixtures.FixtureFinanceOwner);
        var seeded = CompanyKbLoader.Load(added.Json);
        Assert.NotNull(seeded.FiscalCalendar);
        Assert.Equal("kbv10", seeded.FiscalCalendar.Version);
        Assert.Equal(MemoFixtures.FixtureFinanceOwner, seeded.FiscalCalendar.Owner);

        var edited = Build(
            added.Json, "kbv10",
            [MemoFixtures.KbChange(
                "fiscalCalendar", "current", KbChangeKind.Edit,
                MemoFixtures.FiscalEntryJson("12-31", "FY27"))],
            approvedBy: MemoFixtures.FixtureFinanceOwner);
        Assert.Equal("FY27", CompanyKbLoader.Load(edited.Json).FiscalCalendar!.CurrentFy);

        var collision = Assert.Throws<InvalidOperationException>(() => Build(
            added.Json, "kbv10",
            [MemoFixtures.KbChange(
                "fiscalCalendar", "current", KbChangeKind.Add, MemoFixtures.FiscalEntryJson())],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("already", collision.Message);

        var unknown = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "fiscalCalendar", "current", KbChangeKind.Edit, MemoFixtures.FiscalEntryJson())],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("no fiscal calendar", unknown.Message);

        var badId = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "fiscalCalendar", "FY26", KbChangeKind.Add, MemoFixtures.FiscalEntryJson())],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("singleton", badId.Message);
    }

    [Fact]
    public void MixedSpaceChangesRefuse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9")),
             MemoFixtures.KbChange(
                "approvalMatrix", "sourcing-policy", KbChangeKind.Edit,
                MemoFixtures.MatrixEntryJson("sourcing-policy"))]));
        Assert.Contains("one space", ex.Message);
        Assert.Contains("operations", ex.Message);
        Assert.Contains("finance", ex.Message);
    }

    [Fact]
    public void MixedWikiSpacesRefuse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("leadership", "id-9", KbChangeKind.Add, MemoFixtures.WikiEntryJson("id-9")),
             MemoFixtures.KbChange("finance", "id-9", KbChangeKind.Add, MemoFixtures.WikiEntryJson("id-9"))],
            approvedBy: MemoFixtures.FixtureLeadershipOwner));
        Assert.Contains("one space", ex.Message);
        Assert.Contains("leadership", ex.Message);
        Assert.Contains("finance", ex.Message);
    }

    [Fact]
    public void WrongApproverMissingRowAndSelfApprovalRefuse()
    {
        var change = MemoFixtures.KbChange(
            "items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"));

        var wrong = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change], approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("not the space owner", wrong.Message);
        Assert.Contains(MemoFixtures.FixtureOperationsOwner, wrong.Message);

        var unknown = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change], approvedBy: "Zed Stranger"));
        Assert.Contains("not the space owner", unknown.Message);

        var self = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change with { ChangedBy = MemoFixtures.FixtureOperationsOwner }]));
        Assert.Contains("never approve their own", self.Message);

        var noRowBase = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile(
            matrix: [new KbApprovalEntry("sourcing-policy", "approver", MemoFixtures.FixtureProcurementOwner,
                "kbv9", "Terry Owner", MemoFixtures.FixedAt, "Terry Owner", MemoFixtures.FixedAt, "Terry Owner",
                KbEntryStatus.Active, MemoFixtures.FixtureEffectiveDate)]));
        var missing = Assert.Throws<InvalidOperationException>(() => Build(noRowBase, "kbv9", [change]));
        Assert.Contains("no approval-matrix row", missing.Message);
        Assert.Contains("kb-edit-operations", missing.Message);
    }

    [Fact]
    public void DuplicateOwnerRowsRefuseAsAmbiguous()
    {
        KbApprovalEntry Row(string action, string owner) => new(
            action, "approver", owner, "kbv9",
            "Terry Owner", MemoFixtures.FixedAt, "Terry Owner", MemoFixtures.FixedAt, "Terry Owner",
            KbEntryStatus.Active, MemoFixtures.FixtureEffectiveDate);
        var baseJson = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile(matrix: [
            Row("kb-edit-operations", "Owen Operations"),
            Row("kb-edit-operations", "Olive Other"),
            Row("kb-edit-procurement", "Terry Owner"),
        ]));
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            baseJson, "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))]));
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public void DelegatedApprovalSucceedsAndRecordsBothParties()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))],
            approvedBy: FixtureDelegate,
            approvedFor: MemoFixtures.FixtureOperationsOwner);

        Assert.Equal("operations", publication.Activation.Space);
        Assert.Equal(FixtureDelegate, publication.Activation.Approver);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, publication.Activation.ApprovedFor);

        var added = CompanyKbLoader.Load(publication.Json).Items.Single(i => i.Id == "GLASS-9");
        Assert.Equal(FixtureDelegate, added.ApprovedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.Owner);
    }

    [Fact]
    public void DelegationRefusals()
    {
        var change = MemoFixtures.KbChange(
            "items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"));

        var forSelf = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change],
            approvedBy: FixtureDelegate, approvedFor: FixtureDelegate));
        Assert.Contains("for themselves", forSelf.Message);

        var forNonOwner = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change],
            approvedBy: FixtureDelegate, approvedFor: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("not the space owner", forNonOwner.Message);
        Assert.Contains(FixtureDelegate, forNonOwner.Message);
        Assert.Contains(MemoFixtures.FixtureFinanceOwner, forNonOwner.Message);

        var delegateAuthored = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9", [change with { ChangedBy = FixtureDelegate }],
            approvedBy: FixtureDelegate, approvedFor: MemoFixtures.FixtureOperationsOwner));
        Assert.Contains("never approve their own", delegateAuthored.Message);
    }

    [Fact]
    public void PreAuditBaseRefusesWithMigrationPointer()
    {
        var preAudit = MemoFixtures.KbFileJson(MemoFixtures.TestKbFile("kbv1", audited: false));
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            preAudit, "kbv1",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))],
            approvedBy: MemoFixtures.FixtureOperationsOwner));
        Assert.Contains("predates audit", ex.Message);
        Assert.Contains("BuildMigration", ex.Message);
    }

    [Fact]
    public void StaleBaseAndHashDriftRefuse()
    {
        var change = MemoFixtures.KbChange(
            "items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"));

        var stale = Assert.Throws<InvalidOperationException>(() => Build(BaseJson(), "kbv8", [change]));
        Assert.Contains("Stale KB base", stale.Message);
        Assert.Contains("kbv8", stale.Message);
        Assert.Contains("kbv9", stale.Message);

        var drifted = BaseJson() + " ";
        var drift = Assert.Throws<InvalidOperationException>(() => Build(drifted, "kbv9", [change]));
        Assert.Contains("hash mismatch", drift.Message);
    }

    [Fact]
    public void AddCollisionAndEditUnknownRefuse()
    {
        var collision = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "BAT-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("BAT-9"))]));
        Assert.Contains("already exists", collision.Message);

        var unknown = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "NOPE-1", KbChangeKind.Edit, MemoFixtures.ItemEntryJson("NOPE-1"))]));
        Assert.Contains("no such entry", unknown.Message);

        var wikiUnknown = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("finance", "NOPE-1", KbChangeKind.Edit, MemoFixtures.WikiEntryJson("NOPE-1"))],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("no such entry", wikiUnknown.Message);
    }

    [Fact]
    public void MalformedEntriesRefuseAsSchemaDrift()
    {
        var malformed = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, "{not json")]));
        Assert.Contains("not a well-formed item", malformed.Message);

        var mismatch = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("OTHER-1"))]));
        Assert.Contains("must match the change entry id", mismatch.Message);

        var blankField = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "legalEntities", "LE-10", KbChangeKind.Add,
                MemoFixtures.LegalEntityEntryJson("LE-10", ""))]));
        Assert.Contains("empty name", blankField.Message);

        var blankWikiTitle = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "finance", "id-9", KbChangeKind.Add, MemoFixtures.WikiEntryJson("id-9", title: " "))],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("empty title", blankWikiTitle.Message);

        var malformedWiki = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("finance", "id-9", KbChangeKind.Add, "{not json")],
            approvedBy: MemoFixtures.FixtureFinanceOwner));
        Assert.Contains("not a well-formed wiki entry", malformedWiki.Message);

        var unknownSection = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("mirrorUsers", "U-1", KbChangeKind.Add, "{}")]));
        Assert.Contains("unknown section", unknownSection.Message);

        var blankAuthor = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"), changedBy: " ")]));
        Assert.Contains("no author", blankAuthor.Message);

        var duplicateInBatch = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9")),
             MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))]));
        Assert.Contains("more than once", duplicateInBatch.Message);

        var empty = Assert.Throws<InvalidOperationException>(() => Build(BaseJson(), "kbv9", []));
        Assert.Contains("No KB changes", empty.Message);

        var badKind = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", (KbChangeKind)99, MemoFixtures.ItemEntryJson("GLASS-9"))]));
        Assert.Contains("unknown kind", badKind.Message);
    }

    [Fact]
    public void BrokenSiteRefInResultRefuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "sitesWarehouses", "SITE-10", KbChangeKind.Add,
                MemoFixtures.SiteEntryJson("SITE-10", "LE-9", "nowhere"))]));
        Assert.Contains("unknown region rule 'nowhere'", ex.Message);
    }

    [Fact]
    public void TouchedParamsSerializeKeySorted()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "sourcingPolicies", "origin-cap", KbChangeKind.Edit,
                """{"id":"origin-cap","params":{"partScope":"critical","cap":"0.40","windowMonths":"12"}}""")],
            approvedBy: MemoFixtures.FixtureProcurementOwner);
        var capAt = publication.Json.IndexOf("\"cap\"", StringComparison.Ordinal);
        var scopeAt = publication.Json.IndexOf("\"partScope\"", StringComparison.Ordinal);
        var windowAt = publication.Json.IndexOf("\"windowMonths\"", StringComparison.Ordinal);
        Assert.True(0 < capAt && capAt < scopeAt && scopeAt < windowAt);
    }

    [Fact]
    public void AuthoredAuditIsReplacedNeverTrusted()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange(
                "items", "GLASS-9", KbChangeKind.Add,
                """{"id":"GLASS-9","critical":true,"version":"kbv1","createdBy":"Sneaky","approvedBy":"Sneaky","owner":"Sneaky","status":"Retired","effectiveDate":"2020-01-01"}""")]);
        var added = CompanyKbLoader.Load(publication.Json).Items.Single(i => i.Id == "GLASS-9");
        Assert.Equal("kbv10", added.Version);
        Assert.Equal(MemoFixtures.FixtureEditor, added.CreatedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.ApprovedBy);
        Assert.Equal(MemoFixtures.FixtureOperationsOwner, added.Owner);
        Assert.Equal(KbEntryStatus.Active, added.Status);
        Assert.Equal(MemoFixtures.FixtureEffectiveDate, added.EffectiveDate);
    }

    [Fact]
    public void WriteToDirectoryCreatesAndNeverOverwrites()
    {
        var publication = Build(
            BaseJson(), "kbv9",
            [MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9"))]);
        var dir = Path.Combine(Path.GetTempPath(), "gate-kb-write-test", Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(dir).FullName, "company-kb.json"), BaseJson());
            var path = CompanyKbWriter.WriteToDirectory(publication, dir);
            Assert.True(File.Exists(path));
            Assert.Equal(BaseJson(), File.ReadAllText(Path.Combine(dir, "company-kb.json")));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                CompanyKbWriter.WriteToDirectory(publication, dir));
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
        var changes = new[]
        {
            MemoFixtures.KbChange("items", "GLASS-9", KbChangeKind.Add, MemoFixtures.ItemEntryJson("GLASS-9")),
        };
        var a = Build(BaseJson(), "kbv9", changes);
        var b = Build(BaseJson(), "kbv9", changes);
        Assert.Equal(a.Json, b.Json);
        Assert.Equal(CompanyKbHash.Compute(a.Json), CompanyKbLoader.Load(a.Json).Hash);
    }
}
