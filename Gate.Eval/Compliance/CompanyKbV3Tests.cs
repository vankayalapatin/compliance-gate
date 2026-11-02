// kbv3 correction file: the checked-in company-kb.kbv3.json validates
// (hash plus schema plus audit) and carries the company-data section 10
// authors, approvers, and dates on every entry, prose bodies without master
// values, and an empty leadership space (company-identity retired).
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CompanyKbV3Tests
{
    private static string CheckedInKbv3Path()
    {
        var path = CompanyKbLoader.ResolvePath("company/company-kb.kbv3.json");
        Assert.True(File.Exists(path), $"Checked-in kbv3 file not found: {path}");
        return path;
    }

    private static CompanyKbFile LoadKbv3() =>
        CompanyKbLoader.Load(File.ReadAllText(CheckedInKbv3Path()));

    [Fact]
    public void Kbv3ValidatesWithPinnedHash()
    {
        var text = File.ReadAllText(CheckedInKbv3Path());
        var file = CompanyKbLoader.Load(text);
        Assert.Equal("kbv3", file.Version);
        Assert.Equal(CompanyKbHash.Compute(text), file.Hash);
        Assert.Null(file.FiscalCalendar);
    }

    [Fact]
    public void LeadershipSpaceStaysEmpty()
    {
        var file = LoadKbv3();
        var leadership = file.Leadership!;
        Assert.NotNull(leadership);
        Assert.Empty(leadership);
        Assert.Equal(3, file.Finance!.Count);
        Assert.Single(file.Procurement!);
        Assert.Single(file.Hr!);
        Assert.Equal(3, file.Regulatory!.Count);
        Assert.Equal(2, file.Operations!.Count);
    }

    [Fact]
    public void LegalEntityNamesLumenfield()
    {
        var file = LoadKbv3();
        var entity = Assert.Single(file.LegalEntities);
        Assert.Equal("LE-1", entity.Id);
        Assert.Equal("Lumenfield Eyewear Inc.", entity.Name);
        Assert.Equal("US", entity.Country);
        Assert.Equal("USD", entity.Currency);
    }

    [Fact]
    public void SourcingPolicyCitesMemoWithoutCapNumbers()
    {
        var file = LoadKbv3();
        var policy = file.Procurement!.Single(w => w.Id == "sourcing-policy");
        Assert.Contains("MEMO-0001", policy.Body);
        Assert.Contains("ACT-0012", policy.Body);
        Assert.Contains("ACT-0013", policy.Body);
        Assert.DoesNotContain("0.60", policy.Body);
        Assert.DoesNotContain("0.50", policy.Body);
        Assert.DoesNotContain("60%", policy.Body);
        Assert.DoesNotContain("50%", policy.Body);
        Assert.DoesNotContain("60 %", policy.Body);
        Assert.DoesNotContain("50 %", policy.Body);
    }

    [Fact]
    public void FinanceBodiesCarryNoMasterValues()
    {
        var file = LoadKbv3();
        var snapshot = file.ToSnapshot();
        string Body(string id) => snapshot.Finance.Single(w => w.Id == id).Body;

        var limits = Body("signing-limits");
        Assert.DoesNotContain("|", limits);
        Assert.DoesNotContain("5000", limits);
        Assert.DoesNotContain("50000", limits);
        Assert.DoesNotContain("100000", limits);

        var delegation = Body("delegation");
        Assert.DoesNotContain("2026-10-20", delegation);
        Assert.DoesNotContain("2026-11-10", delegation);
        Assert.DoesNotContain("Dana Director delegates", delegation);

        var terms = Body("payment-terms");
        Assert.DoesNotContain("NET30", terms);
        Assert.DoesNotContain("30 days", terms);

        Assert.All(snapshot.Finance, w => Assert.False(string.IsNullOrWhiteSpace(w.Body)));
    }

    [Fact]
    public void ApprovalRolesAndCriticalPartsCarryNoMasterValues()
    {
        var file = LoadKbv3();
        var snapshot = file.ToSnapshot();
        var roles = snapshot.Hr.Single(w => w.Id == "approval-roles").Body;
        Assert.DoesNotContain("Maya Planner", roles);
        Assert.DoesNotContain("Dana Director", roles);
        Assert.DoesNotContain("Del Delegate", roles);
        Assert.DoesNotContain("Amy AP Clerk", roles);
        Assert.DoesNotContain("Finn Finance", roles);

        var parts = snapshot.Operations.Single(w => w.Id == "critical-parts").Body;
        Assert.DoesNotContain("BAT-1", parts);
        Assert.DoesNotContain("SCR-9", parts);
        Assert.DoesNotContain("GLASS-1", parts);
    }

    [Fact]
    public void EveryEntryCarriesSection10Stamps()
    {
        var file = LoadKbv3();
        var created = new DateTimeOffset(2025, 12, 10, 0, 0, 0, TimeSpan.Zero);
        var effective = new DateOnly(2026, 1, 1);

        void Stamps(
            string id, string? version,
            string? createdBy, DateTimeOffset? createdAt,
            string? modifiedBy, DateTimeOffset? modifiedAt,
            string? approvedBy, string? owner,
            KbEntryStatus? status, DateOnly? effectiveDate,
            string expectedAuthor, string expectedApprover, string expectedOwner)
        {
            Assert.Equal("kbv3", version);
            Assert.Equal(expectedAuthor, createdBy);
            Assert.Equal(created, createdAt);
            Assert.Equal(expectedAuthor, modifiedBy);
            Assert.Equal(created, modifiedAt);
            Assert.Equal(expectedApprover, approvedBy);
            Assert.Equal(expectedOwner, owner);
            Assert.Equal(KbEntryStatus.Active, status);
            Assert.Equal(effective, effectiveDate);
        }

        foreach (var e in file.LegalEntities)
        {
            Stamps(e.Id, e.Version, e.CreatedBy, e.CreatedAt, e.ModifiedBy, e.ModifiedAt,
                e.ApprovedBy, e.Owner, e.Status, e.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var s in file.SitesWarehouses)
        {
            Stamps(s.Id, s.Version, s.CreatedBy, s.CreatedAt, s.ModifiedBy, s.ModifiedAt,
                s.ApprovedBy, s.Owner, s.Status, s.EffectiveDate,
                "Maya Planner", "Dana Director", "Maya Planner");
        }

        foreach (var a in file.ApprovalMatrix)
        {
            Assert.Equal("kbv3", a.Version);
            Assert.Equal("Finn Finance", a.CreatedBy);
            Assert.Equal(created, a.CreatedAt);
            Assert.Equal("Finn Finance", a.ModifiedBy);
            Assert.Equal(created, a.ModifiedAt);
            Assert.Equal("Dana Director", a.ApprovedBy);
            Assert.Equal(KbEntryStatus.Active, a.Status);
            Assert.Equal(effective, a.EffectiveDate);
        }

        foreach (var i in file.Items)
        {
            Stamps(i.Id, i.Version, i.CreatedBy, i.CreatedAt, i.ModifiedBy, i.ModifiedAt,
                i.ApprovedBy, i.Owner, i.Status, i.EffectiveDate,
                "Maya Planner", "Dana Director", "Maya Planner");
        }

        foreach (var p in file.SourcingPolicies)
        {
            Stamps(p.Id, p.Version, p.CreatedBy, p.CreatedAt, p.ModifiedBy, p.ModifiedAt,
                p.ApprovedBy, p.Owner, p.Status, p.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var r in file.RegionRules)
        {
            Stamps(r.Id, r.Version, r.CreatedBy, r.CreatedAt, r.ModifiedBy, r.ModifiedAt,
                r.ApprovedBy, r.Owner, r.Status, r.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var d in file.Documents)
        {
            Stamps(d.Id, d.Version, d.CreatedBy, d.CreatedAt, d.ModifiedBy, d.ModifiedAt,
                d.ApprovedBy, d.Owner, d.Status, d.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var w in file.Finance!)
        {
            Stamps(w.Id, w.Version, w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt,
                w.ApprovedBy, w.Owner, w.Status, w.EffectiveDate,
                "Finn Finance", "Dana Director", "Finn Finance");
        }

        foreach (var w in file.Hr!)
        {
            Stamps(w.Id, w.Version, w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt,
                w.ApprovedBy, w.Owner, w.Status, w.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var w in file.Regulatory!)
        {
            Stamps(w.Id, w.Version, w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt,
                w.ApprovedBy, w.Owner, w.Status, w.EffectiveDate,
                "Dana Director", "Finn Finance", "Dana Director");
        }

        foreach (var w in file.Operations!)
        {
            Stamps(w.Id, w.Version, w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt,
                w.ApprovedBy, w.Owner, w.Status, w.EffectiveDate,
                "Maya Planner", "Dana Director", "Maya Planner");
        }

        var policy = file.Procurement!.Single(w => w.Id == "sourcing-policy");
        Assert.Equal("kbv3", policy.Version);
        Assert.Equal("Dana Director", policy.CreatedBy);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), policy.CreatedAt);
        Assert.Equal("Dana Director", policy.ModifiedBy);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), policy.ModifiedAt);
        Assert.Equal("Finn Finance", policy.ApprovedBy);
        Assert.Equal("Dana Director", policy.Owner);
        Assert.Equal(KbEntryStatus.Active, policy.Status);
        Assert.Equal(new DateOnly(2026, 10, 1), policy.EffectiveDate);
    }
}
