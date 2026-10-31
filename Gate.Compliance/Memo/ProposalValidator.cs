// Proposal validation, stages 3-4 of docs/company-kb/MEMO-PIPELINE.md:
// C# checks schema conformance, citation resolution, registry membership,
// param ranges, and question blocking. Failures reject loudly with reasons,
// never silently repaired.
//
// Two entry points: ValidateStructure (no KB needed: exactly one of
// ProposedRule/NotMappable, rule type in registry, gate matches, params in
// range, questions well-formed with a blocking question whenever any
// statement lacks a resolution) and ValidateAgainstKb (additionally pins
// the snapshot version/hash and resolves every citation).
using Gate.Contracts;

namespace Gate.Compliance;

// ProposalValidation lives in Gate.Contracts; this file holds the
// validator only.

public static class ProposalValidator
{
    /// <summary>Structural validation without a KB snapshot.</summary>
    public static ProposalValidation ValidateStructure(RuleProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var errors = new List<string>();

        RequireNonEmpty(errors, proposal.ProposalId, "ProposalId");
        RequireNonEmpty(errors, proposal.MemoId, "MemoId");
        RequireNonEmpty(errors, proposal.KbVersion, "KbVersion");
        RequireNonEmpty(errors, proposal.KbHash, "KbHash");
        RequireNonEmpty(errors, proposal.ProposalHash, "ProposalHash");

        if (proposal.Statements.Count == 0)
        {
            errors.Add("The proposal carries no statements; at least one memo statement is required.");
        }

        for (var i = 0; i < proposal.Statements.Count; i++)
        {
            var statement = proposal.Statements[i];
            if (string.IsNullOrWhiteSpace(statement.Quote))
            {
                errors.Add($"Statement {i} carries an empty quote; the verbatim memo quote is required.");
            }

            if (string.IsNullOrWhiteSpace(statement.Interpretation))
            {
                errors.Add($"Statement {i} carries an empty interpretation.");
            }

            if (statement.Confidence < 0m || statement.Confidence > 1m)
            {
                errors.Add($"Statement {i} confidence {statement.Confidence} is out of range 0..1.");
            }
        }

        for (var i = 0; i < proposal.Resolutions.Count; i++)
        {
            var resolution = proposal.Resolutions[i];
            if (string.IsNullOrWhiteSpace(resolution.Phrase))
            {
                errors.Add($"Resolution {i} carries an empty phrase.");
            }

            if (string.IsNullOrWhiteSpace(resolution.EntityId))
            {
                errors.Add($"Resolution {i} carries an empty entity id.");
            }

            if (resolution.Citation is null)
            {
                errors.Add($"Resolution {i} carries no citation; every resolution must cite kb:<section>#<id>@<version>.");
            }
        }

        if (proposal.ProposedRule is not null && proposal.NotMappable is not null)
        {
            errors.Add("The proposal sets both ProposedRule and NotMappable; exactly one must be set.");
        }
        else if (proposal.ProposedRule is null && proposal.NotMappable is null)
        {
            errors.Add("The proposal sets neither ProposedRule nor NotMappable; exactly one must be set.");
        }

        if (proposal.ProposedRule is not null)
        {
            ValidateProposedRule(proposal.ProposedRule, errors);
        }

        if (proposal.NotMappable is not null)
        {
            if (!MemoRuleRegistry.IsKnownType(proposal.NotMappable.NearestType))
            {
                errors.Add($"NotMappable nearestType '{proposal.NotMappable.NearestType}' is not in the closed registry: " +
                    $"{string.Join(", ", MemoRuleRegistry.All.Select(s => s.RuleType))}.");
            }

            if (string.IsNullOrWhiteSpace(proposal.NotMappable.WhyNot))
            {
                errors.Add("NotMappable whyNot is empty; the exact mismatch is required.");
            }
        }

        ValidateQuestions(proposal, errors);

        foreach (var citation in proposal.Citations)
        {
            if (!string.Equals(citation.KbVersion, proposal.KbVersion, StringComparison.Ordinal))
            {
                errors.Add($"Citation {KbCitations.Format(citation)} pins KB version '{citation.KbVersion}' " +
                    $"but the proposal pins '{proposal.KbVersion}'; mixed pins are rejected.");
            }
        }

        foreach (var resolution in proposal.Resolutions)
        {
            if (resolution.Citation is null)
            {
                continue;
            }

            var present = proposal.Citations.Any(c =>
                c.Section == resolution.Citation.Section
                && c.EntryId == resolution.Citation.EntryId
                && c.KbVersion == resolution.Citation.KbVersion);
            if (!present)
            {
                errors.Add($"Resolution phrase '{resolution.Phrase}' cites {KbCitations.Format(resolution.Citation)} " +
                    "but that citation is absent from the proposal citations list.");
            }
        }

        if (!ProposalHasher.Verify(proposal))
        {
            errors.Add($"Proposal hash mismatch: the proposal claims '{proposal.ProposalHash}' but the canonical " +
                "recomputation differs. The proposal was altered after hashing; re-ground it.");
        }

        if (errors.Count > 0)
        {
            return new ProposalValidation(false, errors, []);
        }

        var conflicts = proposal.ProposedRule is not null
            ? RuleConflictDetector.Compute(proposal.ProposedRule)
            : [];
        return new ProposalValidation(true, [], conflicts);
    }

    /// <summary>Structural validation plus KB pinning: the snapshot must
    /// match the proposal's pinned version/hash (a KB change after proposal
    /// requires re-grounding), and every citation must resolve in it.</summary>
    public static ProposalValidation ValidateAgainstKb(RuleProposal proposal, KbSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);

        var structural = ValidateStructure(proposal);
        if (!structural.Ok)
        {
            return structural;
        }

        var errors = new List<string>();
        if (!string.Equals(snapshot.KbVersion, proposal.KbVersion, StringComparison.Ordinal)
            || !string.Equals(snapshot.KbHash, proposal.KbHash, StringComparison.Ordinal))
        {
            errors.Add($"KB version change after proposal: the proposal pins KB '{proposal.KbVersion}' hash '{proposal.KbHash}' " +
                $"but the current snapshot is '{snapshot.KbVersion}' hash '{snapshot.KbHash}'. " +
                "Re-ground the proposal against the current KB before approval can proceed.");
            return new ProposalValidation(false, errors, []);
        }

        foreach (var citation in proposal.Citations)
        {
            var failure = KbCitations.Resolve(citation, snapshot);
            if (failure is not null)
            {
                errors.Add(failure);
            }
        }

        return errors.Count == 0
            ? structural
            : new ProposalValidation(false, errors, []);
    }

    private static void ValidateProposedRule(ProposedRule rule, List<string> errors)
    {
        if (!MemoRuleRegistry.IsKnownType(rule.RuleType))
        {
            errors.Add($"Proposed rule type '{rule.RuleType}' is not in the closed registry: " +
                $"{string.Join(", ", MemoRuleRegistry.All.Select(s => s.RuleType))}. " +
                "New types require code plus tests, never model invention.");
            return;
        }

        var spec = MemoRuleRegistry.Find(rule.RuleType);
        if (rule.Gate != spec.Gate)
        {
            errors.Add($"Proposed rule type '{rule.RuleType}' enforces at the {spec.Gate} gate, " +
                $"but the proposal claims gate {rule.Gate}.");
        }

        errors.AddRange(MemoRuleRegistry.ValidateParams(rule.RuleType, rule.Params));
    }

    private static void ValidateQuestions(RuleProposal proposal, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in proposal.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.QuestionId))
            {
                errors.Add("A question carries an empty question id.");
            }
            else if (!seen.Add(question.QuestionId))
            {
                errors.Add($"Duplicate question id '{question.QuestionId}'; question ids must be unique.");
            }

            if (string.IsNullOrWhiteSpace(question.Question))
            {
                errors.Add($"Question '{question.QuestionId}' carries empty question text.");
            }

            if (question.Options is null)
            {
                errors.Add($"Question '{question.QuestionId}' carries a null options list.");
            }
        }

        if (AnyStatementLacksResolution(proposal)
            && !proposal.Questions.Any(q => q.Blocking))
        {
            errors.Add("At least one statement lacks a resolution, so at least one blocking question is required; " +
                "the proposal carries none. The memo waits until the policy owner answers.");
        }
    }

    /// <summary>A statement counts as resolved when some resolution phrase
    /// occurs in its quote or interpretation (case-insensitive). Phrases
    /// are Spark's quoted memo spans, so containment is the deterministic
    /// link between statements and resolutions.</summary>
    internal static bool AnyStatementLacksResolution(RuleProposal proposal) =>
        proposal.Statements.Any(statement =>
            !proposal.Resolutions.Any(resolution =>
                !string.IsNullOrWhiteSpace(resolution.Phrase)
                && (statement.Quote.Contains(resolution.Phrase, StringComparison.OrdinalIgnoreCase)
                    || statement.Interpretation.Contains(resolution.Phrase, StringComparison.OrdinalIgnoreCase))));

    private static void RequireNonEmpty(List<string> errors, string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is empty; it is required.");
        }
    }
}
