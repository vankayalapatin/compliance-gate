// View models for the Policies surface (memo-to-rule human steps). Display
// shapes only; every value arrives from the memo files in the case-cache
// dir at runtime. No business values in code.
//
// Agent-lane link conventions (MemoRunner/MemoPicker, read here, owned
// there): memo files are <MemoId>.memo.json; rounds are append-only with
// the last round current; PolicyQuestion cards carry DocumentType
// "PolicyQuestion" with DocumentNumber "<proposalId>:<questionId>", the
// question in Reason, and the offered options as "option:" evidence refs;
// PolicyApproval cards carry DocumentType "PolicyApproval" with
// DocumentNumber = proposal hash. The approval pin itself (hash + KB
// version + approver + timestamp) is reconstructed from the decided card
// plus its round: the runner stores no separate approval record.
using System.Globalization;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>One policy-question card: the decision plus its linked proposal
// question. A null question means the link did not resolve; Options then
// falls back to the "option:" evidence refs (unverified) and Notices always
// says why.</summary>
public sealed record PolicyQuestionCardVm(
    string MemoId,
    Decision Decision,
    OpenQuestion? Question,
    IReadOnlyList<string> Options,
    bool OptionsVerified,
    IReadOnlyList<string> Notices);

/// <summary>One policy-approval card: the decision plus its memo and current
// round. StaleCard means the card pins an older round's hash; Activation is
// the publication for the current round, if any; NeedsNewRuleSpec means no
// update-kind conflict names a live rule, so a brand-new rule needs the
// approver's title/tier/label.</summary>
public sealed record PolicyApprovalCardVm(
    string MemoId,
    Decision Decision,
    PolicyMemo Memo,
    string PolicyOwner,
    MemoProposalRecord? Current,
    bool StaleCard,
    MemoRuleTypeSpec? RuleSpec,
    IReadOnlyList<ProposalStatement> UnresolvedStatements,
    RuleActivation? Activation,
    bool NeedsNewRuleSpec,
    IReadOnlyList<string> Notices);

/// <summary>One activation row: the published pack version plus its source
// line (memo plus approver plus effective date) and rollback point.</summary>
public sealed record PolicyActivationVm(
    string MemoId,
    RuleActivation Activation,
    string SourceLine);

/// <summary>Display-only policy labels. Fixed phrases and formats; every
// domain value (ids, hashes, versions, amounts) arrives at runtime.</summary>
public static class PolicyLabels
{
    public const string BlockingWaits =
        "Blocking: the memo waits until the policy owner answers.";

    public const string AdvisoryQuestion =
        "Advisory question: the pipeline continues, but the answer is still recorded.";

    public const string MixedCurrencies = "mixed currencies";

    public const string CurrencyMissing = "currency missing";

    public const string ApprovedAsProposed = "as proposed";

    /// <summary>Evidence-ref prefix carrying one offered option on a
    // PolicyQuestion card (runner convention).</summary>
    public const string OptionEvidencePrefix = "option:";

    /// <summary>Offered options straight off the card's evidence refs.
// Unverified: the linked proposal round is the source of truth.</summary>
    public static IReadOnlyList<string> OptionsFromEvidence(Decision decision) =>
        decision.EvidenceRefs
            .Where(e => e.StartsWith(OptionEvidencePrefix, StringComparison.Ordinal))
            .Select(e => e[OptionEvidencePrefix.Length..])
            .ToList();

    /// <summary>Approval pin line reconstructed from the decided card plus
    // its round's KB version: proposal hash plus KB version plus approver
    // plus timestamp. Pending cards show the pin shape, not a pin.</summary>
    public static string DecisionPin(Decision decision, string? kbVersion) =>
        decision.Status == DecisionStatus.Pending
        || decision.DecidedBy is null
        || decision.DecidedAt is null
            ? "Not yet decided; the pin (proposal hash + KB version + approver + timestamp) lands on decision."
            : $"hash {decision.DocumentNumber} · KB {kbVersion ?? "—"} · " +
              $"{decision.DecidedBy} · {decision.DecidedAt:u}";

    /// <summary>Wire form kb:&lt;section&gt;#&lt;id&gt;@&lt;version&gt;.</summary>
    public static string Citation(KbCitation citation) => KbCitations.Format(citation);

    /// <summary>One flip's money, always with its currency.</summary>
    public static string FlipMoney(decimal amount, string? currency) =>
        $"{amount} {currency ?? CurrencyMissing}";

    /// <summary>What-if totals money. A null currency means the flipped set
    // spans more than one currency, so it is never blended.</summary>
    public static string TotalsMoney(decimal amount, string? currency) =>
        $"{amount} {currency ?? MixedCurrencies}";

    /// <summary>Activation source line, mirroring the publisher wording:
    // board memo plus approver plus effective date.</summary>
    public static string SourceLine(RuleActivation activation) =>
        $"Board memo {activation.MemoId} effective {activation.EffectiveAt:yyyy-MM-dd} " +
        $"approved by {activation.Approver}";

    /// <summary>Display-only resolved check mirroring the validator rule
    // (ProposalValidator): a statement counts as resolved when some
    // resolution phrase occurs in its quote or interpretation,
    // case-insensitive. Validation stays with the compliance lane; this
    // only decides the resolved/unresolved display buckets.</summary>
    public static bool IsResolved(
        ProposalStatement statement, IReadOnlyList<EntityResolution> resolutions) =>
        resolutions.Any(resolution =>
            !string.IsNullOrWhiteSpace(resolution.Phrase)
            && (statement.Quote.Contains(resolution.Phrase, StringComparison.OrdinalIgnoreCase)
                || statement.Interpretation.Contains(resolution.Phrase, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Parses a datetime-local input as UTC. Returns false (never
    // throws, never guesses) when the text is blank or unparsable.</summary>
    public static bool TryParseEffectiveUtc(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    /// <summary>One-line applicability-mode blurbs from GO-LIVE.md (design
    // text, not domain data).</summary>
    public static string ApplicabilityBlurb(RuleApplicability mode) =>
        mode switch
        {
            RuleApplicability.ProspectiveOnly =>
                "Prospective-only (default): open/draft docs evaluate at the next gate; " +
                "approved docs are grandfathered plus flagged for human review.",
            RuleApplicability.ProspectiveWithReview =>
                "Prospective + review: as above, plus every grandfathered flag needs an explicit " +
                "human disposition before its case closes.",
            RuleApplicability.ImmediateHold =>
                "Immediate hold (safety/regulatory Tier-1 dock-gate only): holds in-flight docs " +
                "pending human review; creates holds, never cancels. Requires a recorded reason.",
            _ => mode.ToString(),
        };
}
