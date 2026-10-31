// Shared memo-lane test kit: harness plus inbox and Spark-JSON builders.
// Test-double data only: invented memo ids and texts, never demo numbers.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

internal static class MemoTestKit
{
    public sealed record Harness(
        InMemoryErpAdapter Erp,
        RecordingModelClient Models,
        RecordingComplianceEngine Compliance,
        MemoRunner Runner,
        string CacheDir,
        string InboxDir);

    public static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    public static Harness Create()
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var cacheDir = TempDir("gate-memo-test");
        var inboxDir = Path.Combine(TempDir("gate-memo-inbox"), "inbox");
        Directory.CreateDirectory(inboxDir);
        var runner = new MemoRunner(erp, models, compliance, cacheDir);
        return new Harness(erp, models, compliance, runner, cacheDir, inboxDir);
    }

    public static string WriteInboxMemo(
        string inboxDir,
        string memoId,
        string excerpt,
        string fullText,
        string author = "The board") =>
        WriteInboxFile(inboxDir, memoId, $$"""
            {"memoId":"{{memoId}}","receivedAt":"2026-11-01T09:00:00Z","author":"{{author}}","excerpt":"{{excerpt}}","fullText":"{{fullText}}" }
            """);

    public static string WriteInboxFile(string inboxDir, string memoId, string json)
    {
        Directory.CreateDirectory(inboxDir);
        var name = memoId + ".json";
        File.WriteAllText(Path.Combine(inboxDir, name), json);
        return name;
    }

    /// <summary>Valid cap-tightening proposal JSON against MemoFixtures.TestKb
    /// (kbv9): OriginShareCap 0.40 over 12 months, critical scope, one
    /// resolved statement, no questions.</summary>
    public static string CapProposalJson(
        string cap = "0.40",
        string window = "12",
        string scope = "critical",
        string owner = "Terry Owner") => $$$"""
        {
          "statements": [{"quote": "no more than 40% of critical battery cells from any single country", "interpretation": "cap the awarded-country share of critical battery cells at 40% over 12 months", "confidence": 0.95}],
          "resolutions": [{"phrase": "critical battery cells", "entityId": "BAT-9", "citation": "kb:items#BAT-9@kbv9"}],
          "proposedRule": {"ruleType": "OriginShareCap", "gate": "Vendor", "params": {"cap": "{{{cap}}}", "windowMonths": "{{{window}}}", "partScope": "{{{scope}}}"}},
          "notMappable": null,
          "questions": [],
          "citations": ["kb:items#BAT-9@kbv9", "kb:sourcingPolicies#origin-cap@kbv9"],
          "policyOwner": "{{{owner}}}"
        }
        """;

    /// <summary>Valid document proposal with one blocking region question
    /// against MemoFixtures.TestKb: the statement carries no resolution, so
    /// the blocking question is required.</summary>
    public static string QuestionProposalJson() => """
        {
          "statements": [{"quote": "all POs to northern suppliers for critical parts need a declaration on file before award", "interpretation": "require a COO declaration document for critical-part awards to northern-region vendors", "confidence": 0.7}],
          "resolutions": [],
          "proposedRule": {"ruleType": "DocumentPresence", "gate": "Vendor", "params": {"docKind": "COO-Declaration", "partScope": "critical"}},
          "notMappable": null,
          "questions": [{"questionId": "Q1", "question": "KB north = {US, MX}: confirm, or exclude MX?", "options": ["Confirm US+MX", "Exclude MX"], "blocking": true}],
          "citations": ["kb:regionRules#north@kbv9"],
          "policyOwner": "Terry Owner"
        }
        """;

    /// <summary>Valid NotMappable proposal against MemoFixtures.TestKb: no
    /// measurable threshold, with the blocking questions whose answers could
    /// make it mappable.</summary>
    public static string NotMappableJson() => """
        {
          "statements": [{"quote": "we should prefer sustainable suppliers going forward", "interpretation": "prefer sustainable suppliers, with no named threshold, gate, or signal", "confidence": 0.4}],
          "resolutions": [],
          "proposedRule": null,
          "notMappable": {"nearestType": "VendorShareCap", "whyNot": "no share, threshold, or gate is named", "missingCapability": "data"},
          "questions": [{"questionId": "Q1", "question": "What share threshold should the rule enforce?", "options": ["40%", "50%", "No threshold; guidance only"], "blocking": true}],
          "citations": [],
          "policyOwner": "Terry Owner"
        }
        """;

    /// <summary>Valid document proposal with every statement resolved and no
    /// questions: DocumentPresence COO-Declaration, no live conflicts.</summary>
    public static string DocumentProposalJson() => """
        {
          "statements": [{"quote": "all awards for critical parts need a declaration on file before award", "interpretation": "require a COO declaration document for critical battery cells before award", "confidence": 0.85}],
          "resolutions": [{"phrase": "critical battery cells", "entityId": "BAT-9", "citation": "kb:items#BAT-9@kbv9"}],
          "proposedRule": {"ruleType": "DocumentPresence", "gate": "Vendor", "params": {"docKind": "COO-Declaration", "partScope": "critical"}},
          "notMappable": null,
          "questions": [],
          "citations": ["kb:items#BAT-9@kbv9"],
          "policyOwner": "Terry Owner"
        }
        """;

    /// <summary>Valid NotMappable proposal with every statement resolved and
    /// no questions: the close-out round after the owner answered.</summary>
    public static string NotMappableResolvedJson() => """
        {
          "statements": [{"quote": "we should prefer sustainable suppliers going forward", "interpretation": "prefer sustainable suppliers of critical battery cells, with no named threshold", "confidence": 0.4}],
          "resolutions": [{"phrase": "critical battery cells", "entityId": "BAT-9", "citation": "kb:items#BAT-9@kbv9"}],
          "proposedRule": null,
          "notMappable": {"nearestType": "VendorShareCap", "whyNot": "no share or threshold is named", "missingCapability": "data"},
          "questions": [],
          "citations": ["kb:items#BAT-9@kbv9"],
          "policyOwner": "Terry Owner"
        }
        """;

    /// <summary>Valid pre-pay proposal that contradicts a live Tier-1 dock
    /// rule: dropping holds while BAT-LI-03 still produces them.</summary>
    public static string ContradictingPrePayJson() => """
        {
          "statements": [{"quote": "payments must wait only while quarantines or reviews are open", "interpretation": "block payment on quarantine and reviews for critical battery cells, ignoring open holds", "confidence": 0.8}],
          "resolutions": [{"phrase": "critical battery cells", "entityId": "BAT-9", "citation": "kb:items#BAT-9@kbv9"}],
          "proposedRule": {"ruleType": "PrePayBlockOnOpen", "gate": "PrePay", "params": {"holds": "false", "quarantine": "true", "reviews": "true"}},
          "notMappable": null,
          "questions": [],
          "citations": ["kb:items#BAT-9@kbv9"],
          "policyOwner": "Terry Owner"
        }
        """;

    public static async Task<MemoFile> IngestAsync(Harness h, string memoId = "MEMO-T1")
    {
        var file = WriteInboxMemo(h.InboxDir, memoId,
            "Cap the awarded-country share of critical battery cells.",
            "Effective 2026-12-01, no more than 40% of critical battery cells from any single country, trailing 12 months. Policy owner: Terry Owner.");
        return await h.Runner.IngestMemoAsync(h.InboxDir, file);
    }
}
