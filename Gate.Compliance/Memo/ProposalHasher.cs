// Canonical proposal hash (docs/company-kb/SPARK-PROPOSAL.md). The hash
// covers the whole proposal; approval pins it, and a KB version change
// after proposal requires re-grounding before approval can proceed.
// The canonical form is explicit field concatenation (fixed order,
// ordinal list order, invariant decimals), never serializer output, so
// the hash is stable across runtimes and replayable byte-for-byte.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Gate.Contracts;

namespace Gate.Compliance;

public static class ProposalHasher
{
    /// <summary>Computes the canonical hash over every proposal field except
    /// ProposalHash itself. Hex SHA-256, lowercase.</summary>
    public static string Hash(RuleProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var sb = new StringBuilder();
        sb.Append("proposalId:").Append(proposal.ProposalId).Append('\n');
        sb.Append("memoId:").Append(proposal.MemoId).Append('\n');
        sb.Append("kbVersion:").Append(proposal.KbVersion).Append('\n');
        sb.Append("kbHash:").Append(proposal.KbHash).Append('\n');
        sb.Append("proposedAt:").Append(proposal.ProposedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("statements:").Append(proposal.Statements.Count).Append('\n');
        foreach (var s in proposal.Statements)
        {
            sb.Append("quote:").Append(s.Quote).Append('\n');
            sb.Append("interpretation:").Append(s.Interpretation).Append('\n');
            sb.Append("confidence:").Append(s.Confidence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        sb.Append("resolutions:").Append(proposal.Resolutions.Count).Append('\n');
        foreach (var r in proposal.Resolutions)
        {
            sb.Append("phrase:").Append(r.Phrase).Append('\n');
            sb.Append("entityId:").Append(r.EntityId).Append('\n');
            sb.Append("citation:").Append(KbCitations.Format(r.Citation)).Append('\n');
        }

        if (proposal.ProposedRule is not null)
        {
            sb.Append("proposedRule:").Append(proposal.ProposedRule.RuleType).Append('|')
                .Append(proposal.ProposedRule.Gate).Append('\n');
            foreach (var key in proposal.ProposedRule.Params.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append("param:").Append(key).Append('=').Append(proposal.ProposedRule.Params[key]).Append('\n');
            }
        }
        else
        {
            sb.Append("proposedRule:<none>\n");
        }

        if (proposal.NotMappable is not null)
        {
            sb.Append("notMappable:").Append(proposal.NotMappable.NearestType).Append('|')
                .Append(proposal.NotMappable.WhyNot).Append('|')
                .Append(proposal.NotMappable.MissingCapability).Append('\n');
        }
        else
        {
            sb.Append("notMappable:<none>\n");
        }

        sb.Append("questions:").Append(proposal.Questions.Count).Append('\n');
        foreach (var q in proposal.Questions)
        {
            sb.Append("questionId:").Append(q.QuestionId).Append('\n');
            sb.Append("question:").Append(q.Question).Append('\n');
            sb.Append("blocking:").Append(q.Blocking ? "true" : "false").Append('\n');
            foreach (var option in q.Options)
            {
                sb.Append("option:").Append(option).Append('\n');
            }
        }

        sb.Append("citations:").Append(proposal.Citations.Count).Append('\n');
        foreach (var c in proposal.Citations)
        {
            sb.Append("citation:").Append(KbCitations.Format(c)).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>True when the stored ProposalHash matches the recomputed
    /// canonical hash (ordinal).</summary>
    public static bool Verify(RuleProposal proposal) =>
        string.Equals(proposal.ProposalHash, Hash(proposal), StringComparison.Ordinal);
}
