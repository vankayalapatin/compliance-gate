// Blocking-question tests: cards, answering with re-drive, declining to
// close, invalid options, and nothing publishing past a block.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoQuestionTests
{
    [Fact]
    public async Task BlockingQuestionsBecomeDecisionCards()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());

        var file = await h.Runner.ProposeAsync("MEMO-T1");

        Assert.True(Assert.Single(file.Proposals).Validation.Ok);
        var card = Assert.Single(file.Decisions);
        Assert.Equal(DecisionKind.PolicyQuestion, card.Kind);
        Assert.Equal(DecisionStatus.Pending, card.Status);
        Assert.Equal("Terry Owner", card.RequestedTo);
        Assert.Equal("KB north = {US, MX}: confirm, or exclude MX?", card.Reason);
        Assert.Equal(["option:Confirm US+MX", "option:Exclude MX"], card.EvidenceRefs);
        Assert.Equal("PolicyQuestion", card.DocumentType);
        Assert.Equal("MEMO-T1-p1:Q1", card.DocumentNumber);
        Assert.Equal(MemoNextStep.AwaitAnswers, MemoPicker.Pick(file).Step);
    }

    [Fact]
    public async Task AnsweringRecordsTheOptionAndRedrives()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        h.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var card = Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions);

        var updated = await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T1", card.DecisionId, true, "Terry Owner", "Confirm US+MX");

        Assert.Equal(DecisionStatus.Approved, updated.Status);
        Assert.Equal("Confirm US+MX", updated.Comment);
        Assert.Equal("Terry Owner", updated.DecidedBy);
        Assert.Equal(2, h.Models.CallCount);
        var redrive = h.Models.Requests[1];
        Assert.Contains("KB north = {US, MX}: confirm, or exclude MX?", redrive.Prompt, StringComparison.Ordinal);
        Assert.Contains("Confirm US+MX", redrive.Prompt, StringComparison.Ordinal);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Equal(2, file.Proposals.Count);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("Re-driving the proposal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidOptionIsRefused()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var card = Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyQuestionAsync(
                "MEMO-T1", card.DecisionId, true, "Terry Owner", "Maybe"));

        Assert.Contains("Confirm US+MX", ex.Message, StringComparison.Ordinal);
        Assert.Equal(DecisionStatus.Pending,
            MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions.Single().Status);
        Assert.Equal(1, h.Models.CallCount);
    }

    [Fact]
    public async Task DecliningClosesTheMemoWithoutPublication()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var card = Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions);

        var updated = await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T1", card.DecisionId, false, "Terry Owner", "No answer.");

        Assert.Equal(DecisionStatus.Rejected, updated.Status);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("declined", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(MemoNextStep.Done, MemoPicker.Pick(file).Step);
        var outcome = await h.Runner.AdvanceAsync("MEMO-T1");
        Assert.True(outcome.Complete);
        Assert.DoesNotContain(file.Decisions, d => d.Kind == DecisionKind.PolicyApproval);
        Assert.Equal(1, h.Models.CallCount);
    }

    [Fact]
    public async Task NothingPublishesPastAnOpenBlock()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        var hash = file.Proposals.Single().Proposal.ProposalHash;
        // An approval card cannot resolve while the block stands: plant one
        // directly to prove the guard (the picker never raises it).
        var planted = new Decision(
            "MEMO-T1-d100", "MEMO-T1", DecisionKind.PolicyApproval, "Approve",
            DecisionStatus.Pending, "Terry Owner", DateTimeOffset.UtcNow,
            null, null, MemoPicker.ApprovalDocumentType, hash,
            null, null, null, "planted", [], null);
        MemoStore.Save(h.CacheDir, file with
        {
            Decisions = file.Decisions.Append(planted).ToList(),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", planted.DecisionId,
                new PolicyApprovalInput(true, "Terry Owner",
                    new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
                    RuleApplicability.ProspectiveOnly)));

        Assert.Contains("nothing publishes past a block", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ValidateApproval", h.Compliance.MemoCalls);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task SupersededQuestionsStopBlockingAfterRedrive()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        h.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var first = MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions.Single();
        await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T1", first.DecisionId, true, "Terry Owner", "Confirm US+MX");

        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Equal(2, file.Proposals.Count);
        Assert.Empty(MemoPicker.CurrentBlocking(file, file.Proposals.Last()));
    }

    [Fact]
    public async Task ReproposalSupersedesOlderPendingQuestions()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        h.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");

        await h.Runner.ProposeAsync("MEMO-T1");

        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        var stale = file.Decisions.Single(d =>
            string.Equals(d.DocumentNumber, "MEMO-T1-p1:Q1", StringComparison.Ordinal));
        Assert.Equal(DecisionStatus.Rejected, stale.Status);
        Assert.Contains("Superseded by MEMO-T1-p2", stale.Comment, StringComparison.Ordinal);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("no longer blocks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvingADecidedCardReturnsItUnchanged()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.QuestionProposalJson());
        h.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Runner.ProposeAsync("MEMO-T1");
        var card = MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions.Single();
        var first = await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T1", card.DecisionId, true, "Terry Owner", "Confirm US+MX");

        var replay = await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T1", card.DecisionId, true, "Terry Owner", "Confirm US+MX");

        Assert.Equal(first.DecisionId, replay.DecisionId);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.Comment, replay.Comment);
        Assert.Equal(first.DecidedBy, replay.DecidedBy);
        Assert.Equal(2, h.Models.CallCount);
    }
}
