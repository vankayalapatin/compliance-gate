// Shared memo-to-rule fixtures. Test-double data only: small invented KB
// entries, memos, and pack fragments, never the demo numbers.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

internal static class MemoFixtures
{
    public static readonly DateTimeOffset FixedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public static KbSnapshot TestKb(
        string version = "kbv9",
        string hash = "hash9",
        KbFiscalCalendar? fiscal = null,
        IReadOnlyList<KbDocument>? documents = null,
        IReadOnlyList<KbWikiEntry>? leadership = null,
        IReadOnlyList<KbWikiEntry>? finance = null,
        IReadOnlyList<KbWikiEntry>? procurement = null,
        IReadOnlyList<KbWikiEntry>? hr = null,
        IReadOnlyList<KbWikiEntry>? regulatory = null,
        IReadOnlyList<KbWikiEntry>? operations = null) => new(
        version,
        hash,
        [new KbLegalEntity("LE-9", "Fixture Co", "US", "USD")],
        [new KbSite("SITE-9", "LE-9", "north", ["dock-camera"])],
        fiscal,
        [new KbApprovalEntry("sourcing-policy", "approver", "Terry Owner")],
        [new KbItem("BAT-9", true), new KbItem("SCR-9", false)],
        [new KbSourcingPolicy("origin-cap", new Dictionary<string, string>
        {
            ["cap"] = "0.60",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        })],
        [new KbRegionRule("north", ["US", "MX"], version)],
        documents ?? [new KbDocument("un383-template", "UN38.3", true)],
        leadership ?? [],
        finance ?? [],
        procurement ?? [],
        hr ?? [],
        regulatory ?? [],
        operations ?? []);

    public static KbCitation Cite(string section, string id, string version = "kbv9") =>
        new(section, id, version);

    public static RuleProposal Proposal(
        ProposedRule? rule = null,
        NotMappableDetail? notMappable = null,
        IReadOnlyList<ProposalStatement>? statements = null,
        IReadOnlyList<EntityResolution>? resolutions = null,
        IReadOnlyList<OpenQuestion>? questions = null,
        IReadOnlyList<KbCitation>? citations = null,
        string kbVersion = "kbv9",
        string kbHash = "hash9",
        string proposalId = "PROP-1",
        string memoId = "MEMO-1")
    {
        var draft = new RuleProposal(
            proposalId, memoId, kbVersion, kbHash,
            statements ?? [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions ?? [],
            rule,
            notMappable,
            questions ?? [],
            citations ?? [],
            "<pending>",
            FixedAt);
        return draft with { ProposalHash = ProposalHasher.Hash(draft) };
    }

    public static ProposedRule CapRule(
        string cap = "0.40",
        string window = "12",
        string scope = "critical",
        string type = MemoRuleRegistry.OriginShareCap) => new(
        type, MemoRuleGate.Vendor,
        new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = window, ["partScope"] = scope });

    public static RuleApproval Approval(
        RuleProposal proposal,
        string approver = "Terry Owner",
        RuleApprovalOutcome outcome = RuleApprovalOutcome.Approved,
        DateTimeOffset? effectiveAt = null,
        RuleApplicability applicability = RuleApplicability.ProspectiveOnly,
        IReadOnlyDictionary<string, string>? editedParams = null,
        string? comment = null) => new(
        proposal.ProposalHash, proposal.KbVersion, approver, FixedAt,
        outcome, effectiveAt ?? FixedAt.AddDays(30), applicability, [],
        editedParams, comment);

    public static string CorpPackJson(
        string version = "v2",
        string originCap = "0.60",
        string vendorCap = "0.50",
        string owner = FixtureProcurementOwner,
        string adopted = FixtureAdoptedDate) =>
        $$"""
        {"pack":"CORP","version":"{{version}}","owner":"{{owner}}","adopted":"{{adopted}}","rules":[
        {"id":"CORP-01","tier":"Tier3","gate":"Vendor","title":"Single-vendor concentration cap.","source":"Fixture memo","enforced":false},
        {"id":"CORP-02","tier":"Tier1","gate":"Vendor","title":"Country-of-origin cap.","source":"Fixture memo","enforced":true}
        ],"config":{"originCap":{{originCap}},"vendorCap":{{vendorCap}},"policySource":"Fixture memo"}
        }
        """;

    public static string PackJson(
        string packId, string version, string rulesJson,
        string owner = FixtureProcurementOwner,
        string adopted = FixtureAdoptedDate) =>
        $$"""
        {"pack":"{{packId}}","version":"{{version}}","owner":"{{owner}}","adopted":"{{adopted}}","rules":[{{rulesJson}}]
        }
        """;

    public static string RuleJson(
        string id, string tier = "Tier2", string gate = "Vendor", bool enforced = true) =>
        $$"""
        {"id":"{{id}}","tier":"{{tier}}","gate":"{{gate}}","title":"{{id}} title.","source":"Fixture source","enforced":{{(enforced ? "true" : "false")}}
        }
        """;

    public static List<(string Name, string Json)> FourPacks(
        string corpVersion = "v2", string corpOriginCap = "0.60") =>
    [
        ("Packs.bat-li.json", PackJson("BAT-LI", "v1", RuleJson("BAT-LI-01"))),
        ("Packs.elec.json", PackJson("ELEC", "v1", RuleJson("ELEC-01"))),
        ("Packs.scr.json", PackJson("SCR", "v1", RuleJson("SCR-01"))),
        ("Packs.corp.json", CorpPackJson(corpVersion, corpOriginCap)),
    ];

    public const string FixtureLeadershipOwner = "Lena Leader";

    public const string FixtureFinanceOwner = "Fay Finance";

    public const string FixtureProcurementOwner = "Terry Owner";

    public const string FixtureHrOwner = "Hugo Hr";

    public const string FixtureRegulatoryOwner = "Rita Regulator";

    public const string FixtureOperationsOwner = "Owen Operations";

    public const string FixtureEditor = "Eddy Editor";

    public const string FixtureAdoptedDate = "2026-04-01";

    public static readonly DateOnly FixtureEffectiveDate = DateOnly.FromDateTime(FixedAt.UtcDateTime);

    /// <summary>Small KB file for writer/loader tests. Audited entries are
    /// stamped at the file version with their space owner, Active status,
    /// and the fixture effective date; pre-audit files omit stamps
    /// (region rules keep their version), like the real first version.</summary>
    public static CompanyKbFile TestKbFile(
        string version = "kbv9",
        bool audited = true,
        KbFiscalCalendar? fiscal = null,
        IReadOnlyList<KbApprovalEntry>? matrix = null,
        IReadOnlyList<KbWikiEntry>? leadership = null,
        IReadOnlyList<KbWikiEntry>? finance = null,
        IReadOnlyList<KbWikiEntry>? procurement = null,
        IReadOnlyList<KbWikiEntry>? hr = null,
        IReadOnlyList<KbWikiEntry>? regulatory = null,
        IReadOnlyList<KbWikiEntry>? operations = null)
    {
        string? V() => audited ? version : null;
        string? By() => audited ? FixtureProcurementOwner : null;
        DateTimeOffset? At() => audited ? FixedAt : null;
        string? SpaceOwner(string spaceOwner) => audited ? spaceOwner : null;
        KbEntryStatus? St() => audited ? KbEntryStatus.Active : null;
        DateOnly? Eff() => audited ? FixtureEffectiveDate : null;
        return new CompanyKbFile(
            version,
            new string('0', 64),
            [new KbLegalEntity("LE-9", "Fixture Co", "US", "USD", V(), By(), At(), By(), At(), By(),
                SpaceOwner(FixtureLeadershipOwner), St(), Eff())],
            [new KbSite("SITE-9", "LE-9", "north", ["dock-camera"], V(), By(), At(), By(), At(), By(),
                SpaceOwner(FixtureOperationsOwner), St(), Eff())],
            fiscal,
            matrix ?? [
                new KbApprovalEntry("sourcing-policy", "approver", FixtureProcurementOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-leadership", "approver", FixtureLeadershipOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-finance", "payment-approver", FixtureFinanceOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-procurement", "approver", FixtureProcurementOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-hr", "approver", FixtureHrOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-regulatory", "approver", FixtureRegulatoryOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
                new KbApprovalEntry("kb-edit-operations", "requester", FixtureOperationsOwner, V(), By(), At(), By(), At(), By(), St(), Eff()),
            ],
            [new KbItem("BAT-9", true, V(), By(), At(), By(), At(), By(),
                SpaceOwner(FixtureOperationsOwner), St(), Eff()),
                new KbItem("SCR-9", false, V(), By(), At(), By(), At(), By(),
                    SpaceOwner(FixtureOperationsOwner), St(), Eff())],
            [new KbSourcingPolicy("origin-cap", new Dictionary<string, string>
            {
                ["cap"] = "0.60",
                ["windowMonths"] = "12",
                ["partScope"] = "critical",
            }, V(), By(), At(), By(), At(), By(),
                SpaceOwner(FixtureProcurementOwner), St(), Eff())],
            [new KbRegionRule("north", ["US", "MX"], version, By(), At(), By(), At(), By(),
                SpaceOwner(FixtureProcurementOwner), St(), Eff())],
            [new KbDocument("un383-template", "UN38.3", true, null, null, V(), By(), At(), By(), At(), By(),
                SpaceOwner(FixtureProcurementOwner), St(), Eff())],
            leadership ?? [],
            finance ?? [],
            procurement ?? [],
            hr ?? [],
            regulatory ?? [],
            operations ?? [],
            [new KbMirrorUser("Terry Owner", "approver", 50000, "USD")],
            [new KbMirrorVendor("V-9", "Fixture Vendor", "US")],
            [new KbMirrorTerm("NET30", 30)],
            new KbMirrorHistory("BAT-9", []));
    }

    /// <summary>One stamped fixture wiki entry for file and snapshot
    /// fixtures. Audited entries carry the file version with full stamps;
    /// pre-audit entries omit them.</summary>
    public static KbWikiEntry TestWikiEntry(
        string id,
        string version = "kbv9",
        bool audited = true,
        string owner = FixtureFinanceOwner) => new(
        id, $"Title {id}", $"Body {id}.", null,
        audited ? version : null,
        audited ? FixtureEditor : null,
        audited ? FixedAt : null,
        audited ? FixtureEditor : null,
        audited ? FixedAt : null,
        audited ? owner : null,
        audited ? owner : null,
        audited ? KbEntryStatus.Active : null,
        audited ? FixtureEffectiveDate : null);

    /// <summary>Serializes a fixture KB file with its content hash pinned,
    /// through the same writer serialization the publications use.</summary>
    public static string KbFileJson(CompanyKbFile file) => CompanyKbWriter.Serialize(file);

    public static KbEntryChange KbChange(
        string section,
        string entryId,
        KbChangeKind kind,
        string entryJson,
        string changedBy = FixtureEditor,
        DateTimeOffset? changedAt = null) => new(
        section, entryId, kind, entryJson, changedBy, changedAt ?? FixedAt);

    public static string LegalEntityEntryJson(string id, string name = "Name Nine") =>
        $$"""{"id":"{{id}}","name":"{{name}}","country":"US","currency":"USD"}""";

    public static string SiteEntryJson(
        string id, string entityId = "LE-9", string region = "north") =>
        $$"""{"id":"{{id}}","entityId":"{{entityId}}","region":"{{region}}","capabilities":["dock-camera"]}""";

    public static string ItemEntryJson(string id, bool critical = true) =>
        $$"""{"id":"{{id}}","critical":{{(critical ? "true" : "false")}}}""";

    public static string PolicyEntryJson(
        string id, string cap = "0.40", string window = "12", string scope = "critical") =>
        "{\"id\":\"" + id + "\",\"params\":{\"cap\":\"" + cap + "\",\"windowMonths\":\"" + window
        + "\",\"partScope\":\"" + scope + "\"}}";

    public static string RegionEntryJson(string id, string countriesJson = """["US","MX"]""") =>
        $$"""{"id":"{{id}}","countries":{{countriesJson}},"version":"kbv9"}""";

    public static string DocumentEntryJson(string id, string kind = "COO-Declaration") =>
        $$"""{"id":"{{id}}","kind":"{{kind}}","p2pRelevant":true}""";

    public static string MatrixEntryJson(
        string action, string ownerRole = "approver", string owner = FixtureProcurementOwner) =>
        $$"""{"action":"{{action}}","ownerRole":"{{ownerRole}}","owner":"{{owner}}"}""";

    public static string FiscalEntryJson(
        string yearEnd = "12-31", string currentFy = "FY26") =>
        $$"""{"yearEnd":"{{yearEnd}}","currentFy":"{{currentFy}}","periods":["2026-01","2026-02"]}""";

    public static string WikiEntryJson(
        string id, string title = "Title Nine", string body = "Body nine.") =>
        $$"""{"id":"{{id}}","title":"{{title}}","body":"{{body}}"}""";
}
