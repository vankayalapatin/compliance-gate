// Strict parser tests: every structural defect rejects loudly with the
// offending value quoted; conveniences (fences, scalar params, minted
// question ids) are pinned exactly.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoProposalParserTests
{
    private static readonly DateTimeOffset FixedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static (RuleProposal Draft, string Owner) Parse(string json) =>
        MemoProposalParser.Parse(json, "MEMO-T1-p1", "MEMO-T1", MemoFixtures.TestKb(), FixedAt);

    [Fact]
    public void ParsesAValidProposal()
    {
        var (draft, owner) = Parse(MemoTestKit.CapProposalJson());

        Assert.Equal("MEMO-T1-p1", draft.ProposalId);
        Assert.Equal("MEMO-T1", draft.MemoId);
        Assert.Equal("kbv9", draft.KbVersion);
        Assert.Equal("hash9", draft.KbHash);
        Assert.Equal("<pending>", draft.ProposalHash);
        Assert.Equal("Terry Owner", owner);
        var statement = Assert.Single(draft.Statements);
        Assert.Contains("40%", statement.Quote, StringComparison.Ordinal);
        Assert.Equal(0.95m, statement.Confidence);
        var resolution = Assert.Single(draft.Resolutions);
        Assert.Equal("BAT-9", resolution.EntityId);
        Assert.Equal(new KbCitation("items", "BAT-9", "kbv9"), resolution.Citation);
        Assert.Equal("OriginShareCap", draft.ProposedRule!.RuleType);
        Assert.Equal(MemoRuleGate.Vendor, draft.ProposedRule.Gate);
        Assert.Equal("0.40", draft.ProposedRule.Params["cap"]);
        Assert.Null(draft.NotMappable);
        Assert.Empty(draft.Questions);
        Assert.Equal(2, draft.Citations.Count);
    }

    [Fact]
    public void AcceptsAFencedBlock()
    {
        var (draft, _) = Parse("```json\n" + MemoTestKit.CapProposalJson() + "\n```");

        Assert.Equal("OriginShareCap", draft.ProposedRule!.RuleType);
    }

    [Fact]
    public void NormalizesScalarParamsToStrings()
    {
        var json = MemoTestKit.CapProposalJson()
            .Replace("\"cap\": \"0.40\"", "\"cap\": 0.4")
            .Replace("\"windowMonths\": \"12\"", "\"windowMonths\": 12");

        var (draft, _) = Parse(json);

        Assert.Equal("0.4", draft.ProposedRule!.Params["cap"]);
        Assert.Equal("12", draft.ProposedRule.Params["windowMonths"]);
    }

    [Fact]
    public void MintsBlankQuestionIdsInOrder()
    {
        var json = MemoTestKit.QuestionProposalJson().Replace("\"questionId\": \"Q1\"", "\"questionId\": \"\"");

        var (draft, _) = Parse(json);

        Assert.Equal("Q1", Assert.Single(draft.Questions).QuestionId);
    }

    [Fact]
    public void ParsesNotMappableWithMissingCapability()
    {
        var (draft, _) = Parse(MemoTestKit.NotMappableJson());

        Assert.Null(draft.ProposedRule);
        Assert.Equal("VendorShareCap", draft.NotMappable!.NearestType);
        Assert.Equal(MissingCapability.Data, draft.NotMappable.MissingCapability);
        Assert.True(Assert.Single(draft.Questions).Blocking);
    }

    [Fact]
    public void InvalidJsonRejects()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("{oops"));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonObjectRejects()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("[1,2]"));

        Assert.Contains("must be a JSON object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStatementsReject()
    {
        var json = MemoTestKit.CapProposalJson()
            .Replace("\"statements\": [{", "\"statements\": [{\"quote\": \"\", \"interpretation\": \"x\", \"confidence\": 0.5}, {");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("empty quote", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfidenceOutOfRangeRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("\"confidence\": 0.95", "\"confidence\": 1.5");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("out of range 0..1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothRuleAndNotMappableRejects()
    {
        var json = MemoTestKit.CapProposalJson()
            .Replace("\"notMappable\": null,", "\"notMappable\": {\"nearestType\": \"VendorShareCap\", \"whyNot\": \"x\", \"missingCapability\": \"data\"},");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherRuleNorNotMappableRejects()
    {
        var json = """
            {"statements": [{"quote": "q", "interpretation": "i", "confidence": 0.5}],
             "resolutions": [], "proposedRule": null, "notMappable": null,
             "questions": [], "citations": [], "policyOwner": "Terry Owner"}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownGateRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("\"gate\": \"Vendor\"", "\"gate\": \"Customs\"");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("Customs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Vendor, Dock, or PrePay", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectParamRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("\"cap\": \"0.40\"", "\"cap\": {\"v\": 1}");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("param 'cap'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMissingCapabilityRejects()
    {
        var json = MemoTestKit.NotMappableJson().Replace("\"missingCapability\": \"data\"", "\"missingCapability\": \"vibes\"");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("vibes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateQuestionIdsReject()
    {
        var json = MemoTestKit.QuestionProposalJson()
            .Replace("\"blocking\": true", "\"blocking\": true}, {\"questionId\": \"Q1\", \"question\": \"Again?\", \"options\": [\"a\"], \"blocking\": false");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("Duplicate question id 'Q1'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankOptionRejects()
    {
        var json = MemoTestKit.QuestionProposalJson().Replace("\"Exclude MX\"", "\" \"");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("blank option", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedCitationRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("kb:items#BAT-9@kbv9", "items/BAT-9");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("items/BAT-9", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kb:<section>#<id>@<version>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOwnerRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("Terry Owner", "Nobody Here");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("Nobody Here", ex.Message, StringComparison.Ordinal);
        Assert.Contains("approval matrix", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOwnerRejects()
    {
        var json = MemoTestKit.CapProposalJson().Replace("\"policyOwner\": \"Terry Owner\"", "\"policyOwner\": \"\"");

        var ex = Assert.Throws<InvalidOperationException>(() => Parse(json));

        Assert.Contains("policyOwner", ex.Message, StringComparison.Ordinal);
    }
}
