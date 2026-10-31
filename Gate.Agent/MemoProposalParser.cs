// Strict Spark-proposal parser (MEMO-PIPELINE.md stage 3, agent side).
// JSON-shape conformance only: every structural defect is rejected loudly
// with the offending value quoted, and nothing is ever repaired. The engine
// validator behind this decides semantics (registry membership, param
// ranges, citation resolution); this parser decides shape. Deterministic
// conveniences (fence extraction, scalar-to-string params, question-id
// minting) are documented below and pinned by tests; they never change
// policy meaning.
using System.Globalization;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public static class MemoProposalParser
{
    /// <summary>Parses one Spark completion into a proposal draft plus the
    /// Spark-named policy owner. The draft carries a "&lt;pending&gt;" hash:
    /// the runner stamps the canonical engine hash next, then validates.
    /// questionId values missing or blank are minted Q1.. in order;
    /// duplicates are rejected. JSON numbers and booleans in rule params
    /// normalize to invariant strings (wire typing, not policy meaning);
    /// objects, arrays, and nulls are rejected.</summary>
    public static (RuleProposal Draft, string PolicyOwner) Parse(
        string completionText,
        string proposalId,
        string memoId,
        KbSnapshot snapshot,
        DateTimeOffset proposedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoId);
        ArgumentNullException.ThrowIfNull(snapshot);

        using var doc = ParseJson(completionText, memoId);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: the top level must be a JSON object.");
        }

        var statements = ParseStatements(root, memoId);
        var resolutions = ParseResolutions(root, memoId);
        var (rule, notMappable) = ParseRuleOrNotMappable(root, memoId);
        var questions = ParseQuestions(root, memoId);
        var citations = ParseCitations(root, memoId);
        var owner = ParsePolicyOwner(root, memoId, snapshot);

        var draft = new RuleProposal(
            proposalId, memoId, snapshot.KbVersion, snapshot.KbHash,
            statements, resolutions, rule, notMappable, questions, citations,
            "<pending>", proposedAt);
        return (draft, owner);
    }

    private static JsonDocument ParseJson(string text, string memoId)
    {
        var json = ExtractJson(text);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: the completion is not valid JSON ({ex.Message}).", ex);
        }
    }

    /// <summary>Accepts raw JSON or one fenced block (```json ... ``` or
    /// ``` ... ```). Anything else is passed through to the JSON parser,
    /// whose error rejects it.</summary>
    internal static string ExtractJson(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence < 0 ? body : body[..fence]).Trim();
    }

    private static IReadOnlyList<ProposalStatement> ParseStatements(JsonElement root, string memoId)
    {
        if (!root.TryGetProperty("statements", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'statements' must be a JSON array.");
        }

        var statements = new List<ProposalStatement>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: statement {index} must be an object.");
            }

            var quote = ReadString(item, "quote");
            var interpretation = ReadString(item, "interpretation");
            if (string.IsNullOrWhiteSpace(quote))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: statement {index} carries an empty quote; the verbatim memo quote is required.");
            }

            if (string.IsNullOrWhiteSpace(interpretation))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: statement {index} carries an empty interpretation.");
            }

            if (!item.TryGetProperty("confidence", out var confidenceEl)
                || (confidenceEl.ValueKind != JsonValueKind.Number && confidenceEl.ValueKind != JsonValueKind.String)
                || !TryReadDecimal(confidenceEl, out var confidence))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: statement {index} confidence must be a number in 0..1.");
            }

            if (confidence < 0m || confidence > 1m)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: statement {index} confidence {confidence} is out of range 0..1.");
            }

            statements.Add(new ProposalStatement(quote, interpretation, confidence));
            index++;
        }

        if (statements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'statements' is empty; at least one memo statement is required.");
        }

        return statements;
    }

    private static IReadOnlyList<EntityResolution> ParseResolutions(JsonElement root, string memoId)
    {
        if (!root.TryGetProperty("resolutions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'resolutions' must be a JSON array.");
        }

        var resolutions = new List<EntityResolution>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: resolution {index} must be an object.");
            }

            var phrase = ReadString(item, "phrase");
            var entityId = ReadString(item, "entityId") ?? ReadString(item, "kbId");
            var citationRaw = ReadString(item, "citation");
            if (string.IsNullOrWhiteSpace(phrase))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: resolution {index} carries an empty phrase.");
            }

            if (string.IsNullOrWhiteSpace(entityId))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: resolution {index} carries an empty entity id.");
            }

            resolutions.Add(new EntityResolution(
                phrase, entityId!, ParseCitationWire(citationRaw, memoId, $"resolution {index}")));
            index++;
        }

        return resolutions;
    }

    private static (ProposedRule? Rule, NotMappableDetail? NotMappable) ParseRuleOrNotMappable(
        JsonElement root, string memoId)
    {
        var hasRule = root.TryGetProperty("proposedRule", out var ruleEl)
            && ruleEl.ValueKind != JsonValueKind.Null;
        var hasNotMappable = root.TryGetProperty("notMappable", out var notEl)
            && notEl.ValueKind != JsonValueKind.Null;
        if (hasRule == hasNotMappable)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: set exactly one of 'proposedRule' and 'notMappable'.");
        }

        if (hasNotMappable)
        {
            return (null, ParseNotMappable(notEl, memoId));
        }

        return (ParseProposedRule(ruleEl, memoId), null);
    }

    private static ProposedRule ParseProposedRule(JsonElement element, string memoId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'proposedRule' must be an object.");
        }

        var ruleType = ReadString(element, "ruleType");
        var gateRaw = ReadString(element, "gate");
        if (string.IsNullOrWhiteSpace(ruleType))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'proposedRule.ruleType' is empty; name one closed-registry type.");
        }

        if (!Enum.TryParse<MemoRuleGate>(gateRaw, ignoreCase: true, out var gate)
            || !Enum.IsDefined(gate))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: gate '{gateRaw}' is unknown; use Vendor, Dock, or PrePay.");
        }

        if (!element.TryGetProperty("params", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'proposedRule.params' must be an object (empty when the type takes no params).");
        }

        var paramsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in paramsEl.EnumerateObject())
        {
            paramsMap[property.Name] = NormalizeParam(property.Value, memoId, property.Name);
        }

        return new ProposedRule(ruleType, gate, paramsMap);
    }

    private static string NormalizeParam(JsonElement value, string memoId, string name) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: param '{name}' must be a string, number, or boolean; objects, arrays, and nulls are rejected."),
        };

    private static NotMappableDetail ParseNotMappable(JsonElement element, string memoId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'notMappable' must be an object.");
        }

        var nearestType = ReadString(element, "nearestType");
        var whyNot = ReadString(element, "whyNot");
        var missingRaw = ReadString(element, "missingCapability");
        if (string.IsNullOrWhiteSpace(nearestType))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'notMappable.nearestType' is empty; name the closest registry type.");
        }

        if (string.IsNullOrWhiteSpace(whyNot))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'notMappable.whyNot' is empty; the exact mismatch is required.");
        }

        if (!Enum.TryParse<MissingCapability>(missingRaw, ignoreCase: true, out var missing)
            || !Enum.IsDefined(missing))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: missingCapability '{missingRaw}' is unknown; use data, sensor, gate, or code.");
        }

        return new NotMappableDetail(nearestType, whyNot, missing);
    }

    private static IReadOnlyList<OpenQuestion> ParseQuestions(JsonElement root, string memoId)
    {
        if (!root.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'questions' must be a JSON array.");
        }

        var questions = new List<OpenQuestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        var minted = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: question {index} must be an object.");
            }

            var questionId = ReadString(item, "questionId");
            if (string.IsNullOrWhiteSpace(questionId))
            {
                minted++;
                questionId = $"Q{minted}";
            }

            if (!seen.Add(questionId))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: Duplicate question id '{questionId}'; question ids must be unique.");
            }

            var question = ReadString(item, "question");
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: question '{questionId}' carries empty question text.");
            }

            if (!item.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: question '{questionId}' must carry an 'options' array.");
            }

            var options = new List<string>();
            foreach (var option in optionsEl.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(option.GetString()))
                {
                    throw new InvalidOperationException(
                        $"Memo {memoId} proposal is rejected: question '{questionId}' carries a blank option; options must be non-empty strings.");
                }

                options.Add(option.GetString()!);
            }

            if (!item.TryGetProperty("blocking", out var blockingEl)
                || (blockingEl.ValueKind != JsonValueKind.True && blockingEl.ValueKind != JsonValueKind.False))
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: question '{questionId}' must carry a boolean 'blocking'.");
            }

            questions.Add(new OpenQuestion(questionId, question, options, blockingEl.ValueKind == JsonValueKind.True));
            index++;
        }

        return questions;
    }

    private static IReadOnlyList<KbCitation> ParseCitations(JsonElement root, string memoId)
    {
        if (!root.TryGetProperty("citations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'citations' must be a JSON array of kb:<section>#<id>@<version> strings.");
        }

        var citations = new List<KbCitation>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Memo {memoId} proposal is rejected: citation {index} must be a kb:<section>#<id>@<version> string.");
            }

            citations.Add(ParseCitationWire(item.GetString(), memoId, $"citation {index}"));
            index++;
        }

        return citations;
    }

    /// <summary>Parses one kb: wire citation. Format only; the engine
    /// resolves it against the pinned snapshot.</summary>
    internal static KbCitation ParseCitationWire(string? wire, string memoId, string where)
    {
        var parts = (wire ?? string.Empty).Split(['#', '@']);
        var section = parts.Length == 3 && parts[0].StartsWith("kb:", StringComparison.Ordinal)
            ? parts[0]["kb:".Length..]
            : null;
        if (section is null
            || string.IsNullOrWhiteSpace(section)
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2]))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: {where} citation '{wire}' is not kb:<section>#<id>@<version>.");
        }

        return new KbCitation(section, parts[1], parts[2]);
    }

    private static string ParsePolicyOwner(JsonElement root, string memoId, KbSnapshot snapshot)
    {
        var owner = ReadString(root, "policyOwner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: 'policyOwner' is empty; name one approval-matrix owner.");
        }

        if (snapshot.ApprovalMatrix.All(e => !string.Equals(e.Owner, owner, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} proposal is rejected: policy owner '{owner}' names nobody in the KB approval matrix " +
                $"(owners: {string.Join(", ", snapshot.ApprovalMatrix.Select(e => e.Owner))}).");
        }

        return owner;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && decimal.TryParse(element.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0m;
        return false;
    }
}
