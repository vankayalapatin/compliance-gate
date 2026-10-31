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
        IReadOnlyList<KbDocument>? documents = null) => new(
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
        documents ?? [new KbDocument("un383-template", "UN38.3", true)]);

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
        string version = "v0.2",
        string originCap = "0.60",
        string vendorCap = "0.50") =>
        $$"""
        {"pack":"CORP","version":"{{version}}","rules":[
        {"id":"CORP-01","tier":"Tier3","gate":"Vendor","title":"Single-vendor concentration cap.","source":"Fixture memo","enforced":false},
        {"id":"CORP-02","tier":"Tier1","gate":"Vendor","title":"Country-of-origin cap.","source":"Fixture memo","enforced":true}
        ],"config":{"originCap":{{originCap}},"vendorCap":{{vendorCap}},"policySource":"Fixture memo"}
        }
        """;

    public static string PackJson(string packId, string version, string rulesJson) =>
        $$"""
        {"pack":"{{packId}}","version":"{{version}}","rules":[{{rulesJson}}]
        }
        """;

    public static string RuleJson(
        string id, string tier = "Tier2", string gate = "Vendor", bool enforced = true) =>
        $$"""
        {"id":"{{id}}","tier":"{{tier}}","gate":"{{gate}}","title":"{{id}} title.","source":"Fixture source","enforced":{{(enforced ? "true" : "false")}}
        }
        """;

    public static List<(string Name, string Json)> FourPacks(
        string corpVersion = "v0.2", string corpOriginCap = "0.60") =>
    [
        ("Packs.bat-li.json", PackJson("BAT-LI", "v0.2", RuleJson("BAT-LI-01"))),
        ("Packs.elec.json", PackJson("ELEC", "v0.2", RuleJson("ELEC-01"))),
        ("Packs.scr.json", PackJson("SCR", "v0.2", RuleJson("SCR-01"))),
        ("Packs.corp.json", CorpPackJson(corpVersion, corpOriginCap)),
    ];
}
