// Memo intake tests: inbox read, idempotent ingest, loud refusals.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoIntakeTests
{
    [Fact]
    public void ReadsAMemoFromTheInboxDir()
    {
        var h = MemoTestKit.Create();
        var name = MemoTestKit.WriteInboxMemo(h.InboxDir, "MEMO-T1",
            "Cap the share.", "Effective soon, cap the share at 40%.");

        var (memo, fullText) = MemoIntake.ReadInboxFile(Path.Combine(h.InboxDir, name));

        Assert.Equal("MEMO-T1", memo.MemoId);
        Assert.Equal("The board", memo.Author);
        Assert.Equal("Cap the share.", memo.Excerpt);
        Assert.Equal(name, memo.StorageRef);
        Assert.Equal("Effective soon, cap the share at 40%.", fullText);
    }

    [Fact]
    public void ListsInboxFilesInOrder()
    {
        var h = MemoTestKit.Create();
        MemoTestKit.WriteInboxMemo(h.InboxDir, "MEMO-T2", "Two.", "Two.");
        MemoTestKit.WriteInboxMemo(h.InboxDir, "MEMO-T1", "One.", "One.");

        Assert.Equal(["MEMO-T1.json", "MEMO-T2.json"], MemoIntake.ListInboxFiles(h.InboxDir));
        Assert.Empty(MemoIntake.ListInboxFiles(Path.Combine(h.InboxDir, "missing")));
    }

    [Fact]
    public async Task IngestStoresTheMemoNextToCaseFiles()
    {
        var h = MemoTestKit.Create();

        var file = await MemoTestKit.IngestAsync(h);

        Assert.Equal("MEMO-T1", file.Memo.MemoId);
        Assert.Empty(file.Proposals);
        Assert.True(File.Exists(Path.Combine(h.CacheDir, "MEMO-T1.memo.json")));
        var reloaded = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Equal(file.Memo, reloaded.Memo);
        Assert.Equal(file.FullText, reloaded.FullText);
        Assert.Empty(reloaded.Proposals);
        Assert.Single(reloaded.Events);
        Assert.DoesNotContain("MEMO-T1", CaseStore.ListCaseIds(h.CacheDir));
        Assert.Equal(["MEMO-T1"], MemoStore.ListMemoIds(h.CacheDir));
        var received = Assert.Single(file.Events);
        Assert.Equal(CaseEventKind.Note, received.Kind);
        Assert.Contains("MEMO-T1", received.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReingestIsIdempotent()
    {
        var h = MemoTestKit.Create();
        var first = await MemoTestKit.IngestAsync(h);

        var second = await MemoTestKit.IngestAsync(h);

        Assert.Equal(first.Memo, second.Memo);
        Assert.Equal(first.FullText, second.FullText);
        Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Events);
    }

    [Fact]
    public async Task ReingestWithDifferentTextIsRefused()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        var name = MemoTestKit.WriteInboxMemo(h.InboxDir, "MEMO-T1",
            "A different excerpt.", "A different text.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.IngestMemoAsync(h.InboxDir, name));

        Assert.Contains("different text", ex.Message, StringComparison.Ordinal);
        Assert.Contains("never silently replaced", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFieldsAreRefusedLoudly()
    {
        var h = MemoTestKit.Create();
        var name = MemoTestKit.WriteInboxFile(h.InboxDir, "MEMO-T9",
            """{"memoId":"MEMO-T9","author":"The board"}""");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MemoIntake.ReadInboxFile(Path.Combine(h.InboxDir, name)));

        Assert.Contains("receivedAt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("excerpt", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fullText", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonIsRefusedLoudly()
    {
        var h = MemoTestKit.Create();
        var name = MemoTestKit.WriteInboxFile(h.InboxDir, "MEMO-T9", "{oops");

        var ex = Assert.Throws<InvalidOperationException>(
            () => MemoIntake.ReadInboxFile(Path.Combine(h.InboxDir, name)));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInboxFileIsRefusedLoudly()
    {
        var h = MemoTestKit.Create();

        var ex = Assert.Throws<InvalidOperationException>(
            () => MemoIntake.ReadInboxFile(Path.Combine(h.InboxDir, "MEMO-NOPE.json")));

        Assert.Contains("cannot be read", ex.Message, StringComparison.Ordinal);
    }
}
