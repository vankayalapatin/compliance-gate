// Policies surface over the real memo pipeline: memo files, the memo
// runner, scripted Spark JSON, and the recording compliance engine (real
// deterministic C# cores, in-memory packs). Tests prove the cards show
// every section, answers re-drive, approvals publish, declines and
// rejections close, rollbacks restore, and refusals surface every reason.
// No live calls; assertions are structural, never demo numbers.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.Services;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class PolicyUiTests
{
    private static readonly DateTimeOffset FixedAt =
        new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Past =
        new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private const string MemoId = "MEMO-T1";

    private sealed record Harness(
        MemoTestKit.Harness Kit, MemoFileStore Memos,
        PolicyViewService Views, PolicyOutcomeService Outcomes);

    private static Harness Create()
    {
        var kit = MemoTestKit.Create();
        var memos = new MemoFileStore(kit.CacheDir);
        return new Harness(
            kit, memos,
            new PolicyViewService(memos, UiTestLogger<PolicyViewService>.Instance),
            new PolicyOutcomeService(memos, kit.Runner, UiTestLogger<PolicyOutcomeService>.Instance));
    }

    private static async Task<Decision> SettleQuestionsAsync(Harness h)
    {
        await MemoTestKit.IngestAsync(h.Kit, MemoId);
        h.Kit.Models.QueueText(MemoTestKit.QuestionProposalJson());
        var settled = await h.Kit.Runner.AdvanceAsync(MemoId);
        Assert.Equal(MemoNextStep.AwaitAnswers, settled.Step);
        var file = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(file);
        return file.Decisions.Single(d =>
            d.Kind == DecisionKind.PolicyQuestion && d.Status == DecisionStatus.Pending);
    }

    private static async Task<Decision> SettleApprovalAsync(Harness h, string? sparkJson = null)
    {
        await MemoTestKit.IngestAsync(h.Kit, MemoId);
        h.Kit.Models.QueueText(sparkJson ?? MemoTestKit.CapProposalJson());
        var settled = await h.Kit.Runner.AdvanceAsync(MemoId);
        Assert.Equal(MemoNextStep.AwaitApproval, settled.Step);
        var file = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(file);
        return file.Decisions.Single(d =>
            d.Kind == DecisionKind.PolicyApproval && d.Status == DecisionStatus.Pending);
    }

    [Fact]
    public async Task ListsQuestionCardsWithOptionsAndBlocking()
    {
        var h = Create();
        await SettleQuestionsAsync(h);

        var cards = await h.Views.ListQuestionCardsAsync();

        var card = Assert.Single(cards);
        Assert.Equal(MemoId, card.MemoId);
        Assert.NotNull(card.Question);
        Assert.True(card.Question.Blocking);
        Assert.Equal(["Confirm US+MX", "Exclude MX"], card.Options);
        Assert.True(card.OptionsVerified);
        Assert.Contains(card.Notices, n => n.Contains("waits"));
    }

    [Fact]
    public async Task AnswerRecordsOptionAndRedrives()
    {
        var h = Create();
        var question = await SettleQuestionsAsync(h);
        h.Kit.Models.QueueText(MemoTestKit.CapProposalJson());

        var outcome = await h.Outcomes.AnswerQuestionAsync(
            MemoId, question.DecisionId, "Confirm US+MX", "Terry Owner");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("Confirm US+MX", outcome.Message);
        var reread = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(reread);
        var decided = reread.Decisions.Single(d => d.DecisionId == question.DecisionId);
        Assert.Equal(DecisionStatus.Approved, decided.Status);
        Assert.Equal("Terry Owner", decided.DecidedBy);
        Assert.Equal("Confirm US+MX", decided.Comment);
        Assert.Equal(2, reread.Proposals.Count);
    }

    [Fact]
    public async Task AnswerRejectsUnofferedOption()
    {
        var h = Create();
        var question = await SettleQuestionsAsync(h);

        var outcome = await h.Outcomes.AnswerQuestionAsync(
            MemoId, question.DecisionId, "Maybe", "Terry Owner");

        Assert.False(outcome.Ok);
        Assert.Contains("offered", outcome.Message);
    }

    [Fact]
    public async Task AnswerRequiresActedBy()
    {
        var h = Create();
        var question = await SettleQuestionsAsync(h);

        var outcome = await h.Outcomes.AnswerQuestionAsync(
            MemoId, question.DecisionId, "Confirm US+MX", "  ");

        Assert.False(outcome.Ok);
        Assert.Contains("Acted-by", outcome.Message);
    }

    [Fact]
    public async Task DeclineClosesWithoutPublication()
    {
        var h = Create();
        var question = await SettleQuestionsAsync(h);

        var outcome = await h.Outcomes.DeclineQuestionAsync(
            MemoId, question.DecisionId, "Terry Owner", "Boundary unknowable.");

        Assert.True(outcome.Ok, outcome.Message);
        var reread = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Rejected, reread.Decisions.Single(d =>
            d.DecisionId == question.DecisionId).Status);
        var settled = await h.Kit.Runner.AdvanceAsync(MemoId);
        Assert.Equal(MemoNextStep.Done, settled.Step);
        Assert.Empty(reread.Activations);
    }

    [Fact]
    public async Task MalformedQuestionRefShowsNoticeAndRefuses()
    {
        var h = Create();
        var decision = new Decision(
            "MEMO-BAD-d001", "MEMO-BAD", DecisionKind.PolicyQuestion, "Bad ref",
            DecisionStatus.Pending, "owner", FixedAt, null, null,
            "PolicyQuestion", "no-colon-here", null, null, null,
            "Question text?", ["option:A", "option:B", "memo:MEMO-BAD"], null);
        await h.Memos.WriteAsync(new MemoFile(
            new PolicyMemo("MEMO-BAD", FixedAt, "Test Board", "Excerpt."),
            "Full text.", [], [decision], [], []));

        var cards = await h.Views.ListQuestionCardsAsync();

        var card = Assert.Single(cards);
        Assert.Null(card.Question);
        Assert.Equal(["A", "B"], card.Options);
        Assert.False(card.OptionsVerified);
        Assert.NotEmpty(card.Notices);

        var outcome = await h.Outcomes.AnswerQuestionAsync(
            "MEMO-BAD", "MEMO-BAD-d001", "A", "owner");

        Assert.False(outcome.Ok);
        Assert.Contains("unknown question", outcome.Message);
    }

    [Fact]
    public async Task ApprovalCardShowsAllSections()
    {
        var h = Create();
        await SettleApprovalAsync(h);

        var cards = await h.Views.ListApprovalCardsAsync();

        var card = Assert.Single(cards);
        Assert.Equal(MemoId, card.MemoId);
        Assert.Contains("awarded-country share", card.Memo.Excerpt);
        Assert.NotNull(card.Current);
        Assert.NotEmpty(card.Current.Proposal.Statements);
        Assert.Empty(card.UnresolvedStatements);
        Assert.NotEmpty(card.Current.Proposal.Citations);
        Assert.True(card.Current.Validation.Ok);
        Assert.NotEmpty(card.Current.Validation.Conflicts);
        Assert.All(card.Current.Validation.Conflicts, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.RuleId));
            Assert.False(string.IsNullOrWhiteSpace(c.Detail));
        });
        Assert.NotNull(card.Current.WhatIf);
        Assert.False(string.IsNullOrWhiteSpace(card.Current.WhatIf.HistoryLabel));
        Assert.NotNull(card.RuleSpec);
        Assert.False(card.StaleCard);
        Assert.False(card.NeedsNewRuleSpec);
        Assert.Null(card.Activation);
        Assert.Contains(
            "Not yet decided",
            PolicyLabels.DecisionPin(card.Decision, card.Current.Proposal.KbVersion));
    }

    [Fact]
    public async Task ApprovePublishesUpdate()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, null, ["PolicyUiTests.ApprovePublishesUpdate"]);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("published", outcome.Message);
        var reread = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(reread);
        var activation = Assert.Single(reread.Activations);
        Assert.Equal("CORP", activation.PackId);
        Assert.NotEqual(activation.PriorPackVersion, activation.NewPackVersion);

        var cards = await h.Views.ListApprovalCardsAsync();
        var card = Assert.Single(cards);
        Assert.NotNull(card.Current);
        Assert.NotNull(card.Activation);
        var pin = PolicyLabels.DecisionPin(card.Decision, card.Current.Proposal.KbVersion);
        Assert.Contains(card.Current.Proposal.ProposalHash, pin);
        Assert.Contains(
            Gate.Web.ViewModels.KbDisplay.Version(card.Current.Proposal.KbVersion), pin);
        Assert.Contains("Terry Owner", pin);
    }

    [Fact]
    public async Task ApproveRequiresEffective()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner", null,
            RuleApplicability.ProspectiveOnly, null, null, null, ["evidence"]);

        Assert.False(outcome.Ok);
        Assert.Contains("effective datetime", outcome.Message);
    }

    [Fact]
    public async Task ApproveRequiresTestEvidence()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null);

        Assert.False(outcome.Ok);
        Assert.Contains("evidence", outcome.Message);
    }

    [Fact]
    public async Task BackdateWithoutReasonRefusesAndWithReasonApproves()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var refused = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner", Past,
            RuleApplicability.ProspectiveOnly, null, null, null, ["evidence"]);

        Assert.False(refused.Ok);
        Assert.Contains("backdat", refused.Message);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner", Past,
            RuleApplicability.ProspectiveOnly, null, "Board minute 12 backdates this.",
            null, ["evidence"]);

        Assert.True(outcome.Ok, outcome.Message);
    }

    [Fact]
    public async Task EditOutsideRegistryIsRefused()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            new Dictionary<string, string>
            {
                ["cap"] = "9.9",
                ["windowMonths"] = "12",
                ["partScope"] = "critical",
            },
            null, null, ["evidence"]);

        Assert.False(outcome.Ok);
        Assert.Contains("out of range", outcome.Message);
    }

    [Fact]
    public async Task ValidEditShips()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            new Dictionary<string, string>
            {
                ["cap"] = "0.45",
                ["windowMonths"] = "12",
                ["partScope"] = "critical",
            },
            null, null, ["evidence"]);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains(outcome.Warnings, w => w.Contains("edited params"));
        Assert.Contains("0.45", h.Kit.Compliance.Published.Last().Json);
    }

    [Fact]
    public async Task StaleCardIsRefused()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);
        h.Kit.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Kit.Runner.ProposeAsync(MemoId);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, null, ["evidence"]);

        Assert.False(outcome.Ok);
        Assert.Contains("stale", outcome.Message);

        var cards = await h.Views.ListApprovalCardsAsync();
        Assert.Contains(cards.Single().Notices, n => n.Contains("Stale card"));
    }

    [Fact]
    public async Task KbChangeRefusesUntilRegrounded()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);
        h.Kit.Compliance.Snapshot = MemoFixtures.TestKb(version: "kbv10", hash: "hash10");

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, null, ["evidence"]);

        Assert.False(outcome.Ok);
        Assert.Contains("kbv10", outcome.Message);
    }

    [Fact]
    public async Task NotMappableApproveRefusedRejectCloses()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h, MemoTestKit.NotMappableResolvedJson());

        var refused = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(7), RuleApplicability.ProspectiveOnly,
            null, null, null, ["evidence"]);

        Assert.False(refused.Ok);
        Assert.Contains("NotMappable", refused.Message);

        var outcome = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, false, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(7), RuleApplicability.ProspectiveOnly,
            null, "No measurable threshold.");

        Assert.True(outcome.Ok, outcome.Message);
        var reread = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(reread);
        Assert.Empty(reread.Activations);
        Assert.Equal(DecisionStatus.Rejected, reread.Decisions.Single(d =>
            d.DecisionId == approval.DecisionId).Status);
    }

    [Fact]
    public async Task RollbackRestoresPriorVersion()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);
        var approved = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, null, ["evidence"]);
        Assert.True(approved.Ok, approved.Message);
        var before = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(before);
        var first = Assert.Single(before.Activations);

        var outcome = await h.Outcomes.RequestRollbackAsync(
            MemoId, first.PriorPackVersion, "Cap too tight.", "Terry Owner");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains("rolled back", outcome.Message);
        var reread = await h.Memos.ReadAsync(MemoId);
        Assert.NotNull(reread);
        Assert.Equal(2, reread.Activations.Count);
        Assert.Equal(3, h.Kit.Compliance.PackHistory["CORP"].Count);

        var activations = await h.Views.ListActivationsAsync();
        Assert.Equal(2, activations.Count);
        var row = activations.First(a => a.Activation.ActivationId == reread.Activations[1].ActivationId);
        Assert.Contains(MemoId, row.SourceLine);
        Assert.Contains("Terry Owner", row.SourceLine);
    }

    [Fact]
    public async Task RollbackRequiresVersionAndReason()
    {
        var h = Create();
        var approval = await SettleApprovalAsync(h);
        var approved = await h.Outcomes.DecideApprovalAsync(
            MemoId, approval.DecisionId, true, "Terry Owner",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, null, ["evidence"]);
        Assert.True(approved.Ok, approved.Message);

        var noVersion = await h.Outcomes.RequestRollbackAsync(
            MemoId, "  ", "Cap too tight.", "Terry Owner");
        Assert.False(noVersion.Ok);
        Assert.Contains("version to restore", noVersion.Message);

        var noReason = await h.Outcomes.RequestRollbackAsync(
            MemoId, "v0.2", "  ", "Terry Owner");
        Assert.False(noReason.Ok);
        Assert.Contains("reason", noReason.Message);
    }

    [Fact]
    public async Task CaseCacheSkipsMemoFiles()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache-skip");
        var cases = new CaseCacheStore(dir);
        var memos = new MemoFileStore(dir);
        await cases.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        await memos.WriteAsync(new MemoFile(
            new PolicyMemo("MEMO-1", FixedAt, "Board", "Excerpt."),
            "Full text.", [], [], [], []));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "CASE-1.vendor-identity.json"), """{"pending":true}""");

        var files = await cases.ListAsync();

        Assert.Single(files);
        var stored = await memos.ReadAsync("MEMO-1");
        Assert.NotNull(stored);
    }

    [Fact]
    public void PolicyLabelsFormatHonestly()
    {
        var pending = UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PolicyApproval);
        Assert.Contains("Not yet decided", PolicyLabels.DecisionPin(pending, "kbv9"));
        var decided = pending with
        {
            Status = DecisionStatus.Approved,
            DecidedBy = "owner",
            DecidedAt = FixedAt,
            DocumentNumber = "hash9",
        };
        var pin = PolicyLabels.DecisionPin(decided, "kbv9");
        Assert.Contains("hash9", pin);
        Assert.Contains("version 9", pin);
        Assert.DoesNotContain("kbv9", pin);
        Assert.Contains("owner", pin);

        var withOptions = pending with { EvidenceRefs = ["option:A", "memo:M", "option:B"] };
        Assert.Equal(["A", "B"], PolicyLabels.OptionsFromEvidence(withOptions));

        Assert.True(PolicyLabels.TryParseEffectiveUtc("2026-12-01T10:30", out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.False(PolicyLabels.TryParseEffectiveUtc(null, out _));
        Assert.False(PolicyLabels.TryParseEffectiveUtc("not-a-date", out _));
        Assert.Equal("10 TST", PolicyLabels.FlipMoney(10m, "TST"));
        Assert.Contains("missing", PolicyLabels.FlipMoney(10m, null));
        Assert.Contains("mixed", PolicyLabels.TotalsMoney(10m, null));
    }
}
