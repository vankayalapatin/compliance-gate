// Memo intake (MEMO-PIPELINE.md stage 1). A memo lands in company/docs/inbox/
// as one JSON file; ingestion copies it into the agent-side memo store and
// never encodes policy meaning. The inbox dir is scenario/test-supplied
// (a parameter, never a constant). Inbox wire format (agent-owned):
//   { memoId, receivedAt, author, excerpt, fullText }
// PolicyMemo.StorageRef points back at the inbox file; the full text is
// copied into the memo file so later steps read the store, not the inbox.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public static class MemoIntake
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Lists the memo JSON files waiting in the inbox dir.</summary>
    public static IReadOnlyList<string> ListInboxFiles(string inboxDir)
    {
        if (!Directory.Exists(inboxDir))
        {
            return [];
        }

        return Directory.GetFiles(inboxDir, "*.json")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()!;
    }

    /// <summary>Reads one inbox file into a memo plus its full text. Every
    /// missing or blank field is refused loudly with the file quoted.</summary>
    public static (PolicyMemo Memo, string FullText) ReadInboxFile(string inboxPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(inboxPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Memo inbox file '{inboxPath}' cannot be read: {ex.Message}", ex);
        }

        InboxMemo? wire;
        try
        {
            wire = JsonSerializer.Deserialize<InboxMemo>(text, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Memo inbox file '{inboxPath}' is not valid JSON.", ex);
        }

        if (wire is null)
        {
            throw new InvalidOperationException(
                $"Memo inbox file '{inboxPath}' deserialized to null.");
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(wire.MemoId))
        {
            missing.Add("memoId");
        }

        if (wire.ReceivedAt is null)
        {
            missing.Add("receivedAt");
        }

        if (string.IsNullOrWhiteSpace(wire.Author))
        {
            missing.Add("author");
        }

        if (string.IsNullOrWhiteSpace(wire.Excerpt))
        {
            missing.Add("excerpt");
        }

        if (string.IsNullOrWhiteSpace(wire.FullText))
        {
            missing.Add("fullText");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Memo inbox file '{inboxPath}' is missing required fields: {string.Join(", ", missing)}.");
        }

        var memo = new PolicyMemo(
            wire.MemoId!, wire.ReceivedAt!.Value, wire.Author!, wire.Excerpt!,
            StorageRef: Path.GetFileName(inboxPath));
        return (memo, wire.FullText!);
    }

    /// <summary>Ingests one inbox file into the memo store. Idempotent:
    /// re-ingesting returns the stored file; ingesting different text under
    /// the same memo id is refused loudly (memos are external input, never
    /// silently replaced).</summary>
    public static MemoFile EnsureMemo(
        string cacheDir, string inboxPath, ILogger? logger = null)
    {
        var (memo, fullText) = ReadInboxFile(inboxPath);
        var existing = MemoStore.Load(cacheDir, memo.MemoId);
        if (existing is not null)
        {
            if (!string.Equals(existing.FullText, fullText, StringComparison.Ordinal)
                || !string.Equals(existing.Memo.Excerpt, memo.Excerpt, StringComparison.Ordinal)
                || !string.Equals(existing.Memo.Author, memo.Author, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Memo '{memo.MemoId}' is already ingested with different text; " +
                    $"inbox file '{inboxPath}' does not match the stored memo. Memos are never silently replaced.");
            }

            logger?.LogInformation(
                "Memo {MemoId} already ingested; replay returns the stored file.", memo.MemoId);
            return existing;
        }

        var file = new MemoFile(memo, fullText, [], [], [], []);
        var work = new MemoWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Memo {memo.MemoId} received from {memo.Author}: {memo.Excerpt}",
            documentType: "Memo", documentNumber: memo.MemoId,
            evidenceRef: memo.StorageRef);
        work.Save(cacheDir);
        logger?.LogInformation(
            "Memo {MemoId} ingested from {Inbox}.", memo.MemoId, inboxPath);
        return work.CurrentFile;
    }

    private sealed record InboxMemo(
        string? MemoId,
        DateTimeOffset? ReceivedAt,
        string? Author,
        string? Excerpt,
        string? FullText);
}
