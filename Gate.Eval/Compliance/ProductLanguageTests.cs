// Product-language scan over the governance data set: rule packs, the
// latest company KB file, and the MEMO-0001 platform baseline. Every
// human-authored text field must be free of the product-bar tokens (same
// token list the ERP lane scans with): whole-word, case-insensitive.
// Structural version ids are excluded by scanning deserialized text fields,
// never raw version or hash values.
using System.Text.RegularExpressions;
using Gate.Agent;
using Gate.Compliance;

namespace Gate.Eval;

public sealed class ProductLanguageTests
{
    private static readonly IReadOnlyList<string> Tokens =
    [
        "seed", "seeded", "scenario", "demo", "fictional", "lifecycle", "kbv", "system"
    ];

    private static void AssertClean(string origin, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var token in Tokens)
        {
            Assert.False(
                Regex.IsMatch(text, $@"\b{token}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                $"Banned token '{token}' in {origin}: '{text}'.");
        }
    }

    [Fact]
    public void PackTitlesAndSourcesAreClean()
    {
        var set = RulePackLoader.LoadEmbedded();
        foreach (var rule in set.Rules)
        {
            AssertClean($"{rule.Pack} {rule.RuleId} title", rule.Title);
            AssertClean($"{rule.Pack} {rule.RuleId} source", rule.Source);
        }

        AssertClean("CORP policySource", set.Corp.PolicySource);
    }

    [Fact]
    public void LatestKbProseIsClean()
    {
        var text = File.ReadAllText(CompanyKbLoader.ResolvePath("company/company-kb.kbv3.json"));
        var file = CompanyKbLoader.Load(text);
        AssertClean("KB comment", file.Comment);
        foreach (var entity in file.LegalEntities)
        {
            AssertClean($"legalEntities#{entity.Id} name", entity.Name);
        }

        var snapshot = file.ToSnapshot();
        var wikis = snapshot.Leadership
            .Concat(snapshot.Finance)
            .Concat(snapshot.Procurement)
            .Concat(snapshot.Hr)
            .Concat(snapshot.Regulatory)
            .Concat(snapshot.Operations);
        foreach (var entry in wikis)
        {
            AssertClean($"wiki#{entry.Id} title", entry.Title);
            AssertClean($"wiki#{entry.Id} body", entry.Body);
        }
    }

    [Fact]
    public void MemoBaselineProseIsClean()
    {
        var file = CompanyKbLoader.ResolvePath("Gate.Web/data/baseline/platform/memo-0001.memo.json");
        var memo = MemoStore.Load(Path.GetDirectoryName(file)!, "memo-0001")!;
        Assert.NotNull(memo);
        AssertClean("MEMO-0001 excerpt", memo.Memo.Excerpt);
        AssertClean("MEMO-0001 fullText", memo.FullText);

        foreach (var record in memo.Proposals)
        {
            foreach (var statement in record.Proposal.Statements)
            {
                AssertClean("PROP-0001 quote", statement.Quote);
                AssertClean("PROP-0001 interpretation", statement.Interpretation);
            }

            foreach (var resolution in record.Proposal.Resolutions)
            {
                AssertClean("PROP-0001 phrase", resolution.Phrase);
            }
        }

        foreach (var decision in memo.Decisions)
        {
            AssertClean($"{decision.DecisionId} title", decision.Title);
            AssertClean($"{decision.DecisionId} reason", decision.Reason);
            AssertClean($"{decision.DecisionId} comment", decision.Comment);
        }

        foreach (var entry in memo.Events)
        {
            AssertClean($"{entry.EventId} summary", entry.Summary);
        }
    }
}
