// Spark relevance selector for memo propose. Deterministic C# over (memo
// text, pinned snapshot): the 8 structured sections pass through in full
// except p2pRelevant=false documents (excluded from Spark context, extending
// the rule-mapping allowlist); the 6 wiki sections filter to the top matches
// by keyword overlap. The filtered snapshot keeps the KbSnapshot shape, so
// the prompt builder needs zero contract change. Same inputs select byte for
// byte so cached replays make zero live calls.
using Gate.Contracts;

namespace Gate.Agent;

public static class MemoSparkSelector
{
    /// <summary>Max wiki entries kept per wiki section. Named so tests pin
    /// the cap instead of the count.</summary>
    public const int MaxWikiEntriesPerSection = 3;

    private const int MinKeywordLength = 3;

    // Generic English stopwords for keyword overlap. Logic only, never
    // domain values: vendor names, ids, thresholds, and dates all pass
    // through as keywords.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "will", "shall",
        "must", "are", "was", "were", "has", "have", "had", "not", "but",
        "all", "any", "per", "via", "into", "over", "under", "more", "than",
        "then", "also", "such", "only", "our", "your", "their",
    };

    /// <summary>Selects the Spark context: structured sections in full
    /// (documents minus p2pRelevant=false), wiki sections to the top
    /// overlap matches per section. Entries scoring zero are dropped, so a
    /// memo with no wiki overlap yields empty wiki sections.</summary>
    public static KbSnapshot SelectRelevant(string memoText, KbSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(memoText);
        ArgumentNullException.ThrowIfNull(snapshot);

        var keywords = Tokenize(memoText);
        return snapshot with
        {
            Documents = snapshot.Documents.Where(d => d.P2pRelevant).ToList(),
            Leadership = TopMatches(snapshot.Leadership, keywords),
            Finance = TopMatches(snapshot.Finance, keywords),
            Procurement = TopMatches(snapshot.Procurement, keywords),
            Hr = TopMatches(snapshot.Hr, keywords),
            Regulatory = TopMatches(snapshot.Regulatory, keywords),
            Operations = TopMatches(snapshot.Operations, keywords),
        };
    }

    private static IReadOnlyList<KbWikiEntry> TopMatches(
        IReadOnlyList<KbWikiEntry> entries, HashSet<string> keywords)
    {
        if (keywords.Count == 0 || entries.Count == 0)
        {
            return [];
        }

        return entries
            .Select(e => (Entry: e, Score: Score(e, keywords)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(MaxWikiEntriesPerSection)
            .Select(x => x.Entry)
            .ToList();
    }

    private static int Score(KbWikiEntry entry, HashSet<string> keywords)
    {
        var text = entry.Id + " " + entry.Title + " " + entry.Body;
        if (entry.Tags is not null)
        {
            text += " " + string.Join(" ", entry.Tags);
        }

        var tokens = Tokenize(text);
        var score = 0;
        foreach (var keyword in keywords)
        {
            if (tokens.Contains(keyword))
            {
                score++;
            }
        }

        return score;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                AddToken(tokens, current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            AddToken(tokens, current.ToString());
        }

        return tokens;
    }

    private static void AddToken(HashSet<string> tokens, string token)
    {
        if (token.Length >= MinKeywordLength && !Stopwords.Contains(token))
        {
            tokens.Add(token);
        }
    }
}
