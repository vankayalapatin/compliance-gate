// Spark proposal prompts (SPARK-PROPOSAL.md). Spark does ALL thinking:
// interpretation, entity resolutions, rule choice, questions, and the
// policy owner. C# only checks the structured JSON it returns. The registry
// table below mirrors the closed compliance registry (names, gates,
// parameter shapes); Gate.Eval/Agent pins every row against
// MemoRuleRegistry so drift fails loudly instead of misleading Spark.
using System.Text;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public static class MemoPrompts
{
    // Compact snapshot block: the KB JSON is the bulk of the prompt, and
    // reasoning models burn output budget on long contexts. Deterministic,
    // so cached replays still reproduce byte for byte.
    private static readonly JsonSerializerOptions CompactOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Closed registry rows for the prompt: type, gate, params.
    /// Pinned by test against the compliance registry; Spark must name one
    /// of these types or return NotMappable.</summary>
    public static IReadOnlyList<(string RuleType, string Gate, string Params, string Description)> RegistryRows { get; } =
    [
        (MemoRuleTypes.DocumentPresence, "Vendor", "docKind, partScope (critical|all)",
            "A document kind must be on file for the lot."),
        (MemoRuleTypes.OriginShareCap, "Vendor", "cap (0..1 exclusive), windowMonths (1..120), partScope (critical|all)",
            "Awarded-country share cap over the trailing window: (countryCells + award) / (total + award)."),
        (MemoRuleTypes.VendorShareCap, "Vendor", "cap (0..1 exclusive), windowMonths (1..120), partScope (critical|all)",
            "Awarded-vendor share cap over the trailing window: (vendorCells + award) / (total + award)."),
        (MemoRuleTypes.ScreeningMatch, "Vendor", "none (list scope is data-driven; carry no params)",
            "Restricted-party screening hit blocks."),
        (MemoRuleTypes.DockMarkPresence, "Dock", "markKind",
            "Photo-only: a mark kind must be visible and legible on the carton."),
        (MemoRuleTypes.DockHandlingPresence, "Dock", "markKind",
            "Photo-only: a handling mark must be visible when required."),
        (MemoRuleTypes.DamageReview, "Dock", "none (always routes to a human, never auto-decides)",
            "Photo-only: suspected damage routes to a human."),
        (MemoRuleTypes.PrePayBlockOnOpen, "PrePay", "holds, quarantine, reviews (booleans as 'true'/'false'; at least one 'true')",
            "Blocks payment while any selected open state is still open."),
    ];

    public static string SystemPrompt =>
        "You are the policy-proposal agent. Read one board memo against the pinned company-KB snapshot " +
        "and return exactly one structured rule proposal as JSON. Quote the memo verbatim in statements; " +
        "resolve every entity phrase to a KB entry with a citation in the exact wire form " +
        "kb:<section>#<id>@<version> using the pinned KB version shown below. Name exactly one closed-registry " +
        "rule type with its gate and string params, or NotMappable with the nearest type, the exact mismatch, " +
        "and what is missing (data, sensor, gate, or code). Ask blocking questions with explicit options " +
        "whenever any statement lacks a resolution or any boundary (region list, threshold, scope, gate) is " +
        "uncertain; the memo waits until the policy owner answers. Name the policy owner: it must be one of " +
        "the approval-matrix owners shown below. Never invent KB ids, countries, thresholds, or rule types: " +
        "every value you use must come from the memo text or the pinned snapshot. Reply with JSON only " +
        "(a single fenced ```json block is acceptable).";

    /// <summary>Builds the propose prompt: verbatim memo text plus the pinned
    /// snapshot sections (structured plus the selector-filtered wiki) plus
    /// the registry table plus the JSON schema plus, on re-drive, the
    /// answered questions. No timestamps: identical inputs reproduce the
    /// prompt byte for byte so cached replays make zero live calls.</summary>
    public static string BuildProposalPrompt(
        PolicyMemo memo,
        string fullText,
        KbSnapshot snapshot,
        IReadOnlyList<(string Question, string Answer)> priorAnswers)
    {
        var sb = new StringBuilder();
        sb.Append("Board memo ").Append(memo.MemoId).Append(" from ").Append(memo.Author).Append(":\n");
        sb.Append(fullText).Append("\n\n");
        sb.Append("Pinned KB snapshot (version ").Append(snapshot.KbVersion)
            .Append(", hash ").Append(snapshot.KbHash).Append("):\n");
        sb.Append(JsonSerializer.Serialize(new
        {
            snapshot.LegalEntities,
            snapshot.SitesWarehouses,
            snapshot.FiscalCalendar,
            snapshot.ApprovalMatrix,
            snapshot.Items,
            snapshot.SourcingPolicies,
            snapshot.RegionRules,
            snapshot.Documents,
            snapshot.Leadership,
            snapshot.Finance,
            snapshot.Procurement,
            snapshot.Hr,
            snapshot.Regulatory,
            snapshot.Operations,
        }, CompactOpts)).Append("\n\n");
        sb.Append("Closed rule-type registry (name exactly one type or NotMappable):\n");
        foreach (var row in RegistryRows)
        {
            sb.Append("- ").Append(row.RuleType).Append(" [").Append(row.Gate).Append("] params: ")
                .Append(row.Params).Append(". ").Append(row.Description).Append('\n');
        }

        sb.Append("""
            Return JSON with exactly this shape:
            {
              "statements": [{"quote": "<verbatim memo span>", "interpretation": "<what it means>", "confidence": 0.0-1.0}],
              "resolutions": [{"phrase": "<memo phrase>", "entityId": "<KB entry id>", "citation": "kb:<section>#<id>@<version>"}],
              "proposedRule": {"ruleType": "<registry type>", "gate": "Vendor|Dock|PrePay", "params": {"<name>": "<string value>"}},
              "notMappable": null | {"nearestType": "<registry type>", "whyNot": "<exact mismatch>", "missingCapability": "data|sensor|gate|code"},
              "questions": [{"questionId": "Q1", "question": "<question>", "options": ["<option>", "..."], "blocking": true}],
              "citations": ["kb:<section>#<id>@<version>"],
              "policyOwner": "<one approval-matrix owner>"
            }
            Set exactly one of proposedRule and notMappable; the other must be null. Every resolution citation
            must also appear in citations. Every statement should carry a resolution whose phrase occurs in its
            quote or interpretation; whenever any statement lacks one, include at least one blocking question.

            """);
        if (priorAnswers.Count > 0)
        {
            sb.Append("Answered policy-owner questions from earlier rounds (ground your proposal in these answers):\n");
            foreach (var (question, answer) in priorAnswers)
            {
                sb.Append("- Q: ").Append(question).Append("\n  A: ").Append(answer).Append('\n');
            }
        }

        return sb.ToString();
    }
}
