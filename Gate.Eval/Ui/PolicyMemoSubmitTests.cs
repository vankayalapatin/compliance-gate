// Memo-submit service tests: pasted text becomes one inbox memo, runs
// intake, and advances into a Spark proposal through the real MemoRunner
// (scripted Spark JSON, recording compliance engine). Tests prove the
// proposal plus open questions come back, blanks are refused before any
// ingest, runner rejections surface as refusals, and every submit mints a
// fresh memo id. No live calls; no demo numbers.
using Gate.Agent;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class PolicyMemoSubmitTests
{
    private static (MemoTestKit.Harness Kit, MemoSubmitService Submit) Create()
    {
        var kit = MemoTestKit.Create();
        var submit = new MemoSubmitService(
            kit.Runner, new MemoFileStore(kit.CacheDir), kit.InboxDir,
            UiTestLogger<MemoSubmitService>.Instance);
        return (kit, submit);
    }

    [Fact]
    public async Task Submit_RunsIntakeAndReturnsProposal()
    {
        var (kit, submit) = Create();
        kit.Models.QueueText(MemoTestKit.CapProposalJson());

        var result = await submit.SubmitAsync(
            "The board", "Cap the awarded-country share of critical battery cells at 40%.");

        Assert.True(result.Ok, result.Error);
        Assert.StartsWith("MEMO-", result.MemoId);
        Assert.Equal(MemoNextStep.AwaitApproval.ToString(), result.Step);
        Assert.NotEmpty(result.Statements);
        Assert.Contains(result.Statements, s => s.Contains("40%"));
        Assert.Null(result.Error);
        Assert.NotNull(await new MemoFileStore(kit.CacheDir).ReadAsync(result.MemoId));
    }

    [Fact]
    public async Task Submit_ReturnsOpenQuestionsWhenAsked()
    {
        var (kit, submit) = Create();
        kit.Models.QueueText(MemoTestKit.QuestionProposalJson());

        var result = await submit.SubmitAsync(
            "The board", "Northern-supplier awards need a declaration on file.");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(MemoNextStep.AwaitAnswers.ToString(), result.Step);
        Assert.Contains(result.Questions, q => q.Contains("Q1") && q.Contains("blocking"));
    }

    [Theory]
    [InlineData("  ", "Some memo text.", "Author")]
    [InlineData("The board", "   ", "Memo text")]
    [InlineData(null, "Some memo text.", "Author")]
    [InlineData("The board", null, "Memo text")]
    public async Task Submit_RefusesBlanksBeforeIngest(
        string? author, string? text, string fragment)
    {
        var (kit, submit) = Create();

        var result = await submit.SubmitAsync(author, text);

        Assert.False(result.Ok);
        Assert.Contains(fragment, result.Error);
        Assert.Empty(await new MemoFileStore(kit.CacheDir).ListAsync());
    }

    [Fact]
    public async Task Submit_SurfacesRunnerRefusal()
    {
        var (kit, submit) = Create();
        // Garbage Spark JSON: the runner rejects the round and throws.
        kit.Models.QueueText("this is not proposal JSON {{{");

        var result = await submit.SubmitAsync("The board", "Prefer sustainable suppliers.");

        Assert.False(result.Ok);
        Assert.Contains("failed", result.Error);
        Assert.StartsWith("MEMO-", result.MemoId);
    }

    [Fact]
    public async Task Submit_MintsSequenceNumbersInOrder()
    {
        var (kit, submit) = Create();
        kit.Models.QueueText(MemoTestKit.CapProposalJson());
        kit.Models.QueueText(MemoTestKit.CapProposalJson());

        var first = await submit.SubmitAsync("The board", "First memo text here.");
        var second = await submit.SubmitAsync("The board", "Second memo text here.");

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.Equal("MEMO-0001", first.MemoId);
        Assert.Equal("MEMO-0002", second.MemoId);
    }

    [Fact]
    public async Task Submit_ContinuesPastNumberedMemosAndSkipsFreeFormIds()
    {
        var (kit, submit) = Create();
        await MemoTestKit.IngestAsync(kit, "MEMO-0001");
        await MemoTestKit.IngestAsync(kit, "MEMO-0003");
        await MemoTestKit.IngestAsync(kit, "MEMO-9f8e7d6c5b4a");
        kit.Models.QueueText(MemoTestKit.CapProposalJson());

        var result = await submit.SubmitAsync("The board", "Third memo text here.");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("MEMO-0004", result.MemoId);
    }

    [Theory]
    [InlineData("MEMO-0001", 1, true)]
    [InlineData("MEMO-0002", 2, true)]
    [InlineData("MEMO-12", 12, true)]
    [InlineData("MEMO-9f8e7d6c5b4a", 0, false)]
    [InlineData("MEMO-T1", 0, false)]
    [InlineData("MEMO-", 0, false)]
    [InlineData("MEMO-0000", 0, false)]
    [InlineData("", 0, false)]
    public void TryParseMemoNumber_ReadsSequenceIdsOnly(string memoId, int expected, bool parses)
    {
        var ok = MemoSubmitService.TryParseMemoNumber(memoId, out var value);

        Assert.Equal(parses, ok);
        Assert.Equal(expected, value);
    }
}
