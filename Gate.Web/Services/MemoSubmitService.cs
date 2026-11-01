// Memo submit behind the Policies "New memo" card: pasted text or an
// uploaded file becomes one inbox memo, runs intake, and advances into a
// Spark proposal through the existing MemoRunner path. This service writes
// the inbox file and shapes the outcome for display only — all thinking
// (interpretation, rule choice, questions) stays in the runner. Refusals
// (blank input, intake mismatch, runner rejection) surface with context.
using System.Text.Json;
using Gate.Agent;

namespace Gate.Web.Services;

public sealed record MemoSubmitResult(
    bool Ok,
    string MemoId,
    string Summary,
    IReadOnlyList<string> Statements,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> ValidationErrors,
    string? Step,
    string? Error);

public sealed class MemoSubmitService
{
    private const int ExcerptChars = 200;

    private readonly MemoRunner _runner;
    private readonly MemoFileStore _memos;
    private readonly string _inboxDir;
    private readonly ILogger<MemoSubmitService> _log;

    public MemoSubmitService(
        MemoRunner runner, MemoFileStore memos, string inboxDir, ILogger<MemoSubmitService> log)
    {
        _runner = runner;
        _memos = memos;
        _inboxDir = inboxDir;
        _log = log;
    }

    public static string ResolveDir(string contentRoot) =>
        Path.Combine(contentRoot, "data", "memo-inbox");

    /// <summary>Submits one memo and drives it to its waiting step. Returns
    /// the proposal statements plus open questions, or the refusal.</summary>
    public async Task<MemoSubmitResult> SubmitAsync(
        string? author, string? fullText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            return new MemoSubmitResult(
                false, string.Empty, string.Empty, [], [], [], null,
                "Author is required; name who sent the memo.");
        }

        if (string.IsNullOrWhiteSpace(fullText))
        {
            return new MemoSubmitResult(
                false, string.Empty, string.Empty, [], [], [], null,
                "Memo text is required; paste the memo or upload a file.");
        }

        var memoId = $"MEMO-{Guid.NewGuid():N}";
        var text = fullText.Trim();
        var wire = new
        {
            memoId,
            receivedAt = DateTimeOffset.UtcNow,
            author = author.Trim(),
            excerpt = Excerpt(text),
            fullText = text,
        };
        try
        {
            Directory.CreateDirectory(_inboxDir);
            var fileName = memoId + ".json";
            await File.WriteAllTextAsync(
                Path.Combine(_inboxDir, fileName),
                JsonSerializer.Serialize(wire, MemoFileStore.JsonOpts), ct).ConfigureAwait(false);
            await _runner.IngestMemoAsync(_inboxDir, fileName, ct).ConfigureAwait(false);
            var advance = await _runner.AdvanceAsync(memoId, ct).ConfigureAwait(false);
            var file = await _memos.ReadAsync(memoId, ct).ConfigureAwait(false);
            var current = file?.Proposals.LastOrDefault();
            var statements = current?.Proposal.Statements
                .Select(s => $"\"{s.Quote}\" → {s.Interpretation} (confidence {s.Confidence})")
                .ToList() ?? [];
            var questions = current?.Proposal.Questions
                .Select(q => $"[{q.QuestionId}] {(q.Blocking ? "blocking" : "advisory")}: {q.Question}")
                .ToList() ?? [];
            IReadOnlyList<string> errors = current?.Validation.Errors ?? [];
            _log.LogInformation(
                "Memo {MemoId} submitted by {Author}; settled at {Step}.",
                memoId, author.Trim(), advance.Step);
            return new MemoSubmitResult(
                true, memoId,
                $"Settled at {advance.Step}: {advance.Summary}",
                statements, questions, errors, advance.Step.ToString(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Memo submit failed for {MemoId}.", memoId);
            return new MemoSubmitResult(
                false, memoId, string.Empty, [], [], [], null,
                $"Memo intake failed: {ex.Message}");
        }
    }

    private static string Excerpt(string text)
    {
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return first.Length <= ExcerptChars ? first : first[..ExcerptChars];
    }
}
