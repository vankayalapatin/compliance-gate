// KB ↔ scenario mirror (docs/company-kb/SYNC.md). company/company-kb.json
// v1 mirrors the scenario values on every overlap field: users + signing
// limits, items + critical flags, vendors + countries, payment terms,
// trailing history totals, CORP cap values. This test reads ERP state
// through the adapter (scenario-seeded) and asserts equality with the KB;
// any divergence fails with field, KB value, and ERP value side by side.
using Gate.Compliance;
using Gate.Erp;

namespace Gate.Eval;

public sealed class CompanyKbMirrorTests
{
    private static async Task<(CompanyKbFile Kb, InMemoryErpAdapter Erp)> LoadKbAndSeededErpAsync()
    {
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        Assert.True(File.Exists(kbPath), $"KB file not found: {kbPath}");
        var kb = CompanyKbLoader.LoadFromFile("company/company-kb.json");

        var erp = new InMemoryErpAdapter();
        var scenario = ScenarioLoader.ResolvePath("scenarios/demo-v1.json");
        Assert.True(File.Exists(scenario), $"Scenario file not found: {scenario}");
        var workDir = Path.Combine(Path.GetTempPath(), "gate-kb-mirror-test", Guid.NewGuid().ToString("N"));
        try
        {
            await ScenarioLoader.ResetAsync(
                erp, scenario,
                Path.Combine(workDir, "gate.db"),
                Path.Combine(workDir, "run-manifest.json"));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }

        return (kb, erp);
    }

    private static async Task<(CompanyKbFile Kb, InMemoryErpAdapter Erp)> LoadHighestKbAndSeededErpAsync()
    {
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        Assert.True(File.Exists(kbPath), $"KB file not found: {kbPath}");
        var dir = Path.GetDirectoryName(kbPath)!;
        var kb = CompanyKbLoader.LoadVersioned(
            Directory.GetFiles(dir, "company-kb*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
                .ToList());

        var (_, erp) = await LoadKbAndSeededErpAsync();
        return (kb, erp);
    }

    [Fact]
    public async Task KbMirrorsScenarioSeededErpOnEveryOverlapField()
    {
        var (kb, erp) = await LoadKbAndSeededErpAsync();
        await AssertOverlapAsync(kb, erp, "kbv1");
    }

    [Fact]
    public async Task HighestKbStillMirrorsScenarioSeededErp()
    {
        var (kb, erp) = await LoadHighestKbAndSeededErpAsync();
        await AssertOverlapAsync(kb, erp, "kbv2");
    }

    private static async Task AssertOverlapAsync(CompanyKbFile kb, InMemoryErpAdapter erp, string expectedVersion)
    {
        var mismatches = new List<string>();
        void Mismatch(string field, string kbValue, string erpValue) =>
            mismatches.Add($"{field}: KB '{kbValue}' vs ERP '{erpValue}'");

        Assert.Equal(expectedVersion, kb.Version);

        var erpUsers = await erp.GetUsersAsync();
        if (erpUsers.Count != kb.MirrorUsers.Count)
        {
            mismatches.Add($"users.count: KB '{kb.MirrorUsers.Count}' vs ERP '{erpUsers.Count}'");
        }

        foreach (var mirror in kb.MirrorUsers)
        {
            var user = await erp.GetUserAsync(mirror.Id);
            if (user is null)
            {
                Mismatch($"users.{mirror.Id}.exists", "present", "missing");
                continue;
            }

            if (user.Role != mirror.Role)
            {
                Mismatch($"users.{mirror.Id}.role", mirror.Role, user.Role);
            }

            if (user.SigningLimit != mirror.SigningLimit)
            {
                Mismatch($"users.{mirror.Id}.signingLimit", mirror.SigningLimit.ToString(), user.SigningLimit.ToString());
            }

            if (user.LimitCurrency != mirror.LimitCurrency)
            {
                Mismatch($"users.{mirror.Id}.limitCurrency", mirror.LimitCurrency, user.LimitCurrency);
            }
        }

        foreach (var item in kb.Items)
        {
            var erpItem = await erp.GetItemAsync(item.Id);
            if (erpItem is null)
            {
                Mismatch($"items.{item.Id}.exists", "present", "missing");
                continue;
            }

            if (erpItem.IsCritical != item.Critical)
            {
                Mismatch($"items.{item.Id}.critical", item.Critical.ToString(), erpItem.IsCritical.ToString());
            }
        }

        var erpVendors = await erp.GetVendorsAsync();
        foreach (var mirror in kb.MirrorVendors)
        {
            var vendor = erpVendors.FirstOrDefault(v => v.VendorId == mirror.Id);
            if (vendor is null)
            {
                Mismatch($"vendors.{mirror.Id}.exists", "present", "missing");
                continue;
            }

            if (vendor.Name != mirror.Name)
            {
                Mismatch($"vendors.{mirror.Id}.name", mirror.Name, vendor.Name);
            }

            if (vendor.CountryOfOrigin != mirror.Country)
            {
                Mismatch($"vendors.{mirror.Id}.country", mirror.Country, vendor.CountryOfOrigin);
            }
        }

        foreach (var mirror in kb.MirrorPaymentTerms)
        {
            var terms = await erp.GetPaymentTermsAsync(mirror.Id);
            if (terms is null)
            {
                Mismatch($"paymentTerms.{mirror.Id}.exists", "present", "missing");
                continue;
            }

            if (terms.NetDays != mirror.NetDays)
            {
                Mismatch($"paymentTerms.{mirror.Id}.netDays", mirror.NetDays.ToString(), terms.NetDays.ToString());
            }
        }

        var history = await erp.GetVolumeHistoryAsync(kb.MirrorHistory.ItemId);
        var erpByCountry = history
            .GroupBy(h => h.Country, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Cells));
        var kbByCountry = kb.MirrorHistory.Rows
            .GroupBy(h => h.Country, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Cells));
        foreach (var country in kbByCountry.Keys.Union(erpByCountry.Keys, StringComparer.Ordinal).OrderBy(c => c))
        {
            var kbCells = kbByCountry.GetValueOrDefault(country);
            var erpCells = erpByCountry.GetValueOrDefault(country);
            if (kbCells != erpCells)
            {
                Mismatch($"history.{kb.MirrorHistory.ItemId}.{country}", kbCells.ToString(), erpCells.ToString());
            }
        }

        var kbTotal = kbByCountry.Values.Sum();
        var erpTotal = erpByCountry.Values.Sum();
        if (kbTotal != erpTotal)
        {
            Mismatch($"history.{kb.MirrorHistory.ItemId}.total", kbTotal.ToString(), erpTotal.ToString());
        }

        var originCap = kb.SourcingPolicies.FirstOrDefault(p => p.Id == "origin-cap");
        var vendorCap = kb.SourcingPolicies.FirstOrDefault(p => p.Id == "vendor-cap");
        if (originCap is null || vendorCap is null)
        {
            mismatches.Add("sourcingPolicies.origin-cap/vendor-cap: KB 'both present' vs KB 'missing'");
        }
        else
        {
            if (decimal.Parse(originCap.Params["cap"], System.Globalization.CultureInfo.InvariantCulture)
                != RuleCatalog.Corp.OriginCap)
            {
                Mismatch("corp.originCap", originCap.Params["cap"], RuleCatalog.Corp.OriginCap.ToString());
            }

            if (decimal.Parse(vendorCap.Params["cap"], System.Globalization.CultureInfo.InvariantCulture)
                != RuleCatalog.Corp.VendorCap)
            {
                Mismatch("corp.vendorCap", vendorCap.Params["cap"], RuleCatalog.Corp.VendorCap.ToString());
            }
        }

        foreach (var entry in kb.ApprovalMatrix)
        {
            var owner = await erp.GetUserAsync(entry.Owner);
            if (owner is null)
            {
                Mismatch($"approvalMatrix.{entry.Action}.owner", entry.Owner, "missing from ERP users");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"KB/ERP drift on {mismatches.Count} field(s):\n- " + string.Join("\n- ", mismatches));
    }

    [Fact]
    public void TamperedKbRefusesWithBothHashes()
    {
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        Assert.True(File.Exists(kbPath), $"KB file not found: {kbPath}");
        var text = File.ReadAllText(kbPath);
        // Trailing whitespace keeps the JSON valid while changing the bytes,
        // so the failure must come from the hash pin, not the parser. The
        // tamper names no KB content: any byte change must refuse.
        var tampered = text + " ";
        var ex = Assert.Throws<InvalidOperationException>(() => CompanyKbLoader.Load(tampered));
        Assert.Contains("hash mismatch", ex.Message);
        Assert.Contains(CompanyKbLoader.Load(text).Hash, ex.Message);
        Assert.Contains(CompanyKbHash.Compute(tampered), ex.Message);
    }

    [Fact]
    public void KbSnapshotProjectsForValidators()
    {
        var kb = CompanyKbLoader.LoadFromFile("company/company-kb.json");
        var snapshot = kb.ToSnapshot();
        Assert.Equal(kb.Version, snapshot.KbVersion);
        Assert.Equal(kb.Hash, snapshot.KbHash);
        Assert.Equal(kb.Items.Count, snapshot.Items.Count);
        Assert.Null(snapshot.FiscalCalendar);
    }
}
