// kbv2 wiki seed composition: the eleven company-wiki seeds as markdown
// built from runtime reads (the scenario-seeded ERP, the kbv1 file, the
// rule packs), plus the two brief-approved policy texts verbatim. Values
// come from runtime; only the markdown shape, the artifact metadata
// (migration clock, activation id, reason, comment), and the brief
// texts live here. The one-time file generator and the migration pin
// tests compose through this helper, so the checked-in file always
// equals a fresh writer-path build.
using System.Globalization;
using System.Text;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

internal static class Kbv2Seeds
{
    public const string BaseVersion = "kbv1";

    /// <summary>kbv2 migration clock: the repo date the file was minted
    /// (run-manifest and session date 2026-11-01, inside the scenario
    /// delegation window). Effective dates and backfill audit stamp from
    /// it; the lead can re-mint with a different clock.</summary>
    public static readonly DateTimeOffset MigrationAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly MigrationDate = DateOnly.FromDateTime(MigrationAt.UtcDateTime);

    public const string ActivationId = "KB-MIGRATION-kbv2";

    public const string Reason =
        "One-time kbv1 backfill: system audit plus space owners on every carried entry, " +
        "six kb-edit-* owner rows, and eleven company-wiki seeds.";

    public const string Comment =
        "Company KB v2 (company truth). Carried kbv1 entries keep per-entry kbv1 with system backfill audit; " +
        "kb-edit-* owner rows and wiki seeds enter at kbv2. " +
        "SYNC overlap sections (items, sourcingPolicies, mirror*) mirror scenarios/demo-v1.json exactly; " +
        "KB-only sections are KB truth with no scenario counterpart. " +
        "All names and numbers fictional.";

    public const string DanaDirector = "Dana Director";

    public const string FinnFinance = "Finn Finance";

    public const string MayaPlanner = "Maya Planner";

    public const string CorpOriginRuleId = "CORP-02";

    public const string Un383RuleId = "BAT-LI-01";

    public const string BatteryMarkRuleId = "BAT-LI-03";

    public const string HandlingRuleId = "BAT-LI-04";

    /// <summary>Brief-approved dock safety text, verbatim.</summary>
    public const string DockSafetyText =
        "The dock gate is photo-only. Every inbound carton is checked from its photos: " +
        "the lithium-battery mark must be visible and legible (BAT-LI-03), " +
        "the Class 9 diamond and Cargo Aircraft Only handling must be visible when required (BAT-LI-04), " +
        "and any suspected outer damage goes to human review (BAT-LI-05). " +
        "Photos never set quantities — ordered and received counts come from the ASN and ERP receipt only. " +
        "An ASN carton missing from every image goes to human review, and illegible labels are re-shot and " +
        "re-transcribed before any decision. Nothing auto-quarantines on a photo alone: a human decides from the " +
        "model crops, quarantine flips the lot's inventory status with a paired hold, and putaway plus payment " +
        "stay blocked until every review on that lot closes.";

    /// <summary>Brief-approved data residency text, verbatim.</summary>
    public const string DataResidencyText =
        "Data residency. Company data stays within approved systems: the ERP, the case store, and the approved " +
        "model endpoint. Operational documents — purchase orders, invoices, shipment notices, dock photos, and " +
        "board memos — may be processed by the approved model endpoint for transcription, matching, screening, " +
        "and explanation. HR documents and other restricted records must never be sent to the model and can never " +
        "ground a rule. Vendor bank details are used only to match and pay the invoice that carries them. " +
        "Personal data is minimized in every model request, and employee records are never bulk-exported.";

    /// <summary>Seeds the scenario ERP in memory and loads the kbv1 file.
    /// Temp seed files are cleaned up before returning.</summary>
    public static async Task<(InMemoryErpAdapter Erp, CompanyKbFile Kbv1)> LoadSourcesAsync()
    {
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        if (!File.Exists(kbPath))
        {
            throw new InvalidOperationException($"KB file not found: {kbPath}");
        }

        var kbv1 = CompanyKbLoader.LoadFromFile("company/company-kb.json");

        var scenario = ScenarioLoader.ResolvePath("scenarios/demo-v1.json");
        if (!File.Exists(scenario))
        {
            throw new InvalidOperationException($"Scenario file not found: {scenario}");
        }

        var erp = new InMemoryErpAdapter();
        var workDir = Path.Combine(Path.GetTempPath(), "gate-kbv2-seeds", Guid.NewGuid().ToString("N"));
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

        return (erp, kbv1);
    }

    /// <summary>The six space owners: brief-named owners with their
    /// scenario roles read from the ERP. A missing ERP user surfaces
    /// loudly; roles are never defaulted.</summary>
    public static async Task<IReadOnlyList<KbMigrationOwner>> SpaceOwnersAsync(IErpAdapter erp)
    {
        async Task<string> RoleOf(string owner)
        {
            var user = await erp.GetUserAsync(owner);
            if (user is null)
            {
                throw new InvalidOperationException(
                    $"kbv2 migration needs the scenario role of space owner '{owner}', " +
                    "but the ERP has no such user; refusing to default it.");
            }

            return user.Role;
        }

        return
        [
            new KbMigrationOwner(CompanyKbSpaces.Leadership, DanaDirector, await RoleOf(DanaDirector)),
            new KbMigrationOwner(CompanyKbSpaces.Finance, FinnFinance, await RoleOf(FinnFinance)),
            new KbMigrationOwner(CompanyKbSpaces.Procurement, DanaDirector, await RoleOf(DanaDirector)),
            new KbMigrationOwner(CompanyKbSpaces.Hr, DanaDirector, await RoleOf(DanaDirector)),
            new KbMigrationOwner(CompanyKbSpaces.Regulatory, DanaDirector, await RoleOf(DanaDirector)),
            new KbMigrationOwner(CompanyKbSpaces.Operations, MayaPlanner, await RoleOf(MayaPlanner)),
        ];
    }

    /// <summary>Composes the eleven wiki seeds from runtime reads. Titles
    /// repeat the brief-named entry ids verbatim (no invented titles);
    /// bodies interpolate scenario, KB, and pack values.</summary>
    public static async Task<IReadOnlyList<KbMigrationWikiSeed>> BuildSeedsAsync(
        IErpAdapter erp, CompanyKbFile kbv1)
    {
        var users = await erp.GetUsersAsync();
        var delegations = await erp.GetDelegationsAsync();
        if (delegations.Count == 0)
        {
            throw new InvalidOperationException(
                "kbv2 migration needs at least one ERP delegation for the delegation seed; refusing to seed it empty.");
        }

        var terms = new List<(string Id, int NetDays)>();
        foreach (var mirror in kbv1.MirrorPaymentTerms)
        {
            var term = await erp.GetPaymentTermsAsync(mirror.Id);
            if (term is null)
            {
                throw new InvalidOperationException(
                    $"kbv2 migration needs ERP payment terms '{mirror.Id}' for the payment-terms seed; refusing to invent it.");
            }

            terms.Add((term.TermsId, term.NetDays));
        }

        var parts = new List<(string Id, bool Critical)>();
        foreach (var item in kbv1.Items)
        {
            var erpItem = await erp.GetItemAsync(item.Id);
            if (erpItem is null)
            {
                throw new InvalidOperationException(
                    $"kbv2 migration needs ERP item '{item.Id}' for the critical-parts seed; refusing to invent it.");
            }

            parts.Add((erpItem.ItemId, erpItem.IsCritical));
        }

        return
        [
            new KbMigrationWikiSeed("finance", Wiki("signing-limits", SigningLimitsBody(users))),
            new KbMigrationWikiSeed("finance", Wiki("delegation", DelegationBody(delegations))),
            new KbMigrationWikiSeed("finance", Wiki("payment-terms", PaymentTermsBody(terms))),
            new KbMigrationWikiSeed("operations", Wiki("critical-parts", CriticalPartsBody(parts))),
            new KbMigrationWikiSeed("operations", Wiki("dock-safety", DockSafetyText)),
            new KbMigrationWikiSeed("leadership", Wiki("company-identity", CompanyIdentityBody(kbv1))),
            new KbMigrationWikiSeed("procurement", Wiki("sourcing-policy", SourcingPolicyBody(kbv1))),
            new KbMigrationWikiSeed("hr", Wiki("approval-roles", ApprovalRolesBody(users, kbv1))),
            new KbMigrationWikiSeed("regulatory", Wiki("data-residency", DataResidencyText)),
            new KbMigrationWikiSeed("regulatory", Wiki("un38-3-summary", Un383Body())),
            new KbMigrationWikiSeed("regulatory", Wiki("iata-marks", IataMarksBody())),
        ];
    }

    private static KbWikiEntry Wiki(string id, string body) => new(id, id, body);

    private static string SigningLimitsBody(IReadOnlyList<UserDto> users)
    {
        var body = new StringBuilder();
        body.AppendLine("Per-user signing limits. The approval matrix names who; the ERP enforces how much.");
        body.AppendLine();
        body.AppendLine("| User | Role | Limit |");
        body.AppendLine("|---|---|---|");
        foreach (var user in users)
        {
            body.AppendLine(CultureInfo.InvariantCulture,
                $"| {user.UserId} | {user.Role} | {user.SigningLimit} {user.LimitCurrency} |");
        }

        return body.ToString().TrimEnd();
    }

    private static string DelegationBody(IReadOnlyList<DelegationDto> delegations)
    {
        var body = new StringBuilder();
        body.AppendLine("Active ERP delegations. While a delegation is active, the delegate approves for the owner");
        body.AppendLine("and the activation records both parties.");
        body.AppendLine();
        foreach (var delegation in delegations)
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "- {0} delegates to {1} from {2:yyyy-MM-dd} through {3:yyyy-MM-dd}.",
                delegation.FromUser, delegation.ToUser, delegation.Start, delegation.End);
            body.AppendLine(line);
        }

        return body.ToString().TrimEnd();
    }

    private static string PaymentTermsBody(IReadOnlyList<(string Id, int NetDays)> terms)
    {
        var body = new StringBuilder();
        body.AppendLine("Company payment terms from the ERP vendor master.");
        body.AppendLine();
        foreach (var (id, netDays) in terms)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"- {id}: net {netDays} days.");
        }

        return body.ToString().TrimEnd();
    }

    private static string CriticalPartsBody(IReadOnlyList<(string Id, bool Critical)> parts)
    {
        var body = new StringBuilder();
        body.AppendLine("Critical-part flags from the scenario item master. Compliance document packs apply");
        body.AppendLine("to critical parts only; non-critical parts run straight through.");
        body.AppendLine();
        foreach (var (id, critical) in parts)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"- {id}: {(critical ? "critical" : "not critical")}.");
        }

        return body.ToString().TrimEnd();
    }

    private static string CompanyIdentityBody(CompanyKbFile kbv1)
    {
        var body = new StringBuilder();
        body.AppendLine("Legal entities from the company KB.");
        body.AppendLine();
        foreach (var entity in kbv1.LegalEntities)
        {
            body.AppendLine(CultureInfo.InvariantCulture,
                $"- {entity.Id}: {entity.Name}, incorporated in {entity.Country}, base currency {entity.Currency}.");
        }

        return body.ToString().TrimEnd();
    }

    private static string SourcingPolicyBody(CompanyKbFile kbv1)
    {
        var origin = kbv1.SourcingPolicies.SingleOrDefault(p => p.Id == "origin-cap");
        var vendor = kbv1.SourcingPolicies.SingleOrDefault(p => p.Id == "vendor-cap");
        if (origin is null || vendor is null)
        {
            throw new InvalidOperationException(
                "kbv2 migration needs the KB origin-cap and vendor-cap sourcing policies for the sourcing-policy seed.");
        }

        var originCap = ParseCap(origin, "cap");
        var vendorCap = ParseCap(vendor, "cap");
        if (originCap != RuleCatalog.Corp.OriginCap || vendorCap != RuleCatalog.Corp.VendorCap)
        {
            throw new InvalidOperationException(
                $"kbv2 sourcing-policy seed refuses: KB caps {originCap}/{vendorCap} disagree with the CORP pack " +
                $"{RuleCatalog.Corp.OriginCap}/{RuleCatalog.Corp.VendorCap}; surfacing the mismatch instead of seeding it.");
        }

        var body = new StringBuilder();
        body.AppendLine("Company sourcing policy from the KB sourcing policies (mirrored in the CORP pack config).");
        body.AppendLine();
        body.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "- origin-cap ({0}): cap {1} over {2} months, {3} scope — no more than {4} of critical battery " +
            "volume from a single country of origin.",
            CorpOriginRuleId, origin.Params["cap"], origin.Params["windowMonths"],
            origin.Params["partScope"], Percent(origin.Params["cap"])));
        body.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "- vendor-cap: cap {0} over {1} months, {2} scope — no more than {3} of critical battery " +
            "volume from a single vendor (documented only).",
            vendor.Params["cap"], vendor.Params["windowMonths"],
            vendor.Params["partScope"], Percent(vendor.Params["cap"])));
        return body.ToString().TrimEnd();
    }

    private static decimal ParseCap(KbSourcingPolicy policy, string param)
    {
        if (!policy.Params.TryGetValue(param, out var raw)
            || !decimal.TryParse(raw, CultureInfo.InvariantCulture, out var cap))
        {
            throw new InvalidOperationException(
                $"kbv2 migration needs sourcing policy '{policy.Id}' param '{param}' as a decimal; got '{raw ?? "<missing>"}'.");
        }

        return cap;
    }

    private static string Percent(string raw) =>
        (decimal.Parse(raw, CultureInfo.InvariantCulture) * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string ApprovalRolesBody(IReadOnlyList<UserDto> users, CompanyKbFile kbv1)
    {
        var body = new StringBuilder();
        body.AppendLine("Who approves what: scenario users with their roles, plus the KB approval matrix.");
        body.AppendLine();
        body.AppendLine("Users:");
        body.AppendLine();
        foreach (var user in users)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"- {user.UserId}: {user.Role}.");
        }

        body.AppendLine();
        body.AppendLine("Approval matrix:");
        body.AppendLine();
        foreach (var row in kbv1.ApprovalMatrix)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"- {row.Action}: {row.OwnerRole}, {row.Owner}.");
        }

        return body.ToString().TrimEnd();
    }

    private static string Un383Body()
    {
        var rule = RuleCatalog.Find(Un383RuleId);
        return "A UN38.3 test summary is required for the cell/battery type " +
            $"({rule.RuleId}: {rule.Title} Source: {rule.Source})";
    }

    private static string IataMarksBody()
    {
        var mark = RuleCatalog.Find(BatteryMarkRuleId);
        var handling = RuleCatalog.Find(HandlingRuleId);
        return "Lithium-battery marks on outer cartons must be visible and legible " +
            $"({mark.RuleId}: {mark.Title} Source: {mark.Source} " +
            $"{handling.RuleId}: {handling.Title} Source: {handling.Source})";
    }
}
