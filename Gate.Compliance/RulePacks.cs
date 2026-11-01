// Rule pack loading: versioned rule records plus pack thresholds load from
// per-pack JSON (embedded), never from C# literals. Pack order follows
// PROJECT.md section 6: BAT-LI, ELEC, SCR, CORP.
//
// Memo-to-rule publishing adds version-bumped pack files without ever
// editing an existing pack file in place: the base file (corp.json) keeps
// its version, and each publication lands as <packid>.vN.M.json
// (e.g. corp.v0.3.json). The loader reads every candidate per pack and
// enforces the highest version, so prior versions stay retained for audit
// and rollback while exactly one version enforces.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gate.Contracts;

namespace Gate.Compliance;

public sealed record CorpPackConfig(decimal OriginCap, decimal VendorCap, string PolicySource);

public sealed record RulePackSet(IReadOnlyList<RuleDefinition> Rules, CorpPackConfig Corp);

public static class RulePackLoader
{
    private static readonly string[] PackIdsInOrder =
    [
        "bat-li",
        "elec",
        "scr",
        "corp"
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static RulePackSet LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        var inputs = new List<(string Name, string Json)>();
        foreach (var packId in PackIdsInOrder)
        {
            foreach (var resource in names.Where(n => IsPackResource(n, packId)).OrderBy(n => n, StringComparer.Ordinal))
            {
                inputs.Add((resource, ReadResource(assembly, resource)));
            }
        }

        return LoadPacks(inputs);
    }

    /// <summary>Loads a pack set from named JSON contents. Embedded loading
    /// and tests share this core: names select the pack (plus an optional
    /// .vN.M filename version), contents carry the authority. For each pack
    /// the highest content version wins; ties, malformed versions, and
    /// filename/content version mismatches refuse loudly.</summary>
    public static RulePackSet LoadPacks(IEnumerable<(string Name, string Json)> inputs)
    {
        var byPack = new Dictionary<string, List<(string Name, PackJson Pack)>>(StringComparer.Ordinal);
        foreach (var (name, json) in inputs)
        {
            var packId = PackIdsInOrder.FirstOrDefault(id => IsPackResource(name, id));
            if (packId is null)
            {
                throw new InvalidOperationException(
                    $"Pack input '{name}' matches no known pack id ({string.Join(", ", PackIdsInOrder)}).");
            }

            var pack = ParsePack(name, json);
            if (!byPack.TryGetValue(packId, out var list))
            {
                list = [];
                byPack[packId] = list;
            }

            list.Add((name, pack));
        }

        var rules = new List<RuleDefinition>();
        CorpPackConfig? corp = null;
        foreach (var packId in PackIdsInOrder)
        {
            if (!byPack.TryGetValue(packId, out var candidates) || candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Rule pack '{packId}' was not found; every pack ({string.Join(", ", PackIdsInOrder)}) must ship at least one file.");
            }

            var winner = PickWinner(packId, candidates);
            if (!string.Equals(winner.Pack, packId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Pack file for '{packId}' declares pack '{winner.Pack}'; refusing to mix packs.");
            }

            var (packOwner, packAdopted) = RequireAdoption(winner);
            foreach (var rule in winner.Rules)
            {
                rules.Add(new RuleDefinition(
                    rule.Id, winner.Pack, winner.Version, rule.Tier, rule.Gate,
                    rule.Title, rule.Source, rule.Enforced, packOwner, packAdopted,
                    rule.DocumentKind, rule.DocumentLabel, rule.RuleType, rule.Params));
            }

            if (winner.Config is not null
                && string.Equals(winner.Pack, "CORP", StringComparison.Ordinal))
            {
                corp = new CorpPackConfig(winner.Config.OriginCap, winner.Config.VendorCap, winner.Config.PolicySource);
            }
        }

        if (corp is null)
        {
            throw new InvalidOperationException("CORP pack config (originCap, vendorCap, policySource) is missing.");
        }

        return new RulePackSet(rules, corp);
    }

    /// <summary>True when the name addresses this pack's base file or a
    /// versioned file, as an embedded resource name
    /// (Gate.Compliance.Packs.corp.v0.3.json) or a plain file name
    /// (corp.v0.3.json).</summary>
    private static bool IsPackResource(string name, string packId)
    {
        var file = FilePart(name);
        return string.Equals(file, $"{packId}.json", StringComparison.Ordinal)
            || (file.StartsWith($"{packId}.v", StringComparison.Ordinal)
                && file.EndsWith(".json", StringComparison.Ordinal));
    }

    private static string FilePart(string name)
    {
        var packsAt = name.IndexOf("Packs.", StringComparison.Ordinal);
        if (packsAt >= 0)
        {
            return name[(packsAt + "Packs.".Length)..];
        }

        var slash = name.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    private static PackJson PickWinner(string packId, List<(string Name, PackJson Pack)> candidates)
    {
        var scored = new List<(string Name, PackJson Pack, int Major, int Minor)>();
        foreach (var (name, pack) in candidates)
        {
            var fileVersion = FileVersionSuffix(name, packId);
            if (fileVersion is not null
                && !string.Equals(fileVersion, pack.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pack file '{name}' names version '{fileVersion}' but declares '{pack.Version}'; refusing to guess which wins.");
            }

            int major, minor;
            try
            {
                (major, minor) = PackVersions.Parse(pack.Version);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Pack file '{name}' carries an invalid version: {ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(pack.Adopted))
            {
                throw new InvalidOperationException(
                    $"Pack file '{name}' for pack '{packId}' carries no adopted date; " +
                    "every pack records the company adoption date as yyyy-MM-dd.");
            }

            if (!DateOnly.TryParse(pack.Adopted, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                throw new InvalidOperationException(
                    $"Pack file '{name}' for pack '{packId}' carries no parseable adopted date (got '{pack.Adopted}'); " +
                    "every pack records the company adoption date as yyyy-MM-dd.");
            }

            scored.Add((name, pack, major, minor));
        }

        var ordered = scored
            .OrderByDescending(s => s.Major)
            .ThenByDescending(s => s.Minor)
            .ToList();
        if (ordered.Count > 1 && ordered[0].Major == ordered[1].Major && ordered[0].Minor == ordered[1].Minor)
        {
            throw new InvalidOperationException(
                $"Pack '{packId}' ships version '{ordered[0].Pack.Version}' twice ('{ordered[0].Name}' and '{ordered[1].Name}'); " +
                "versions must be unique so exactly one wins.");
        }

        return ordered[0].Pack;
    }

    /// <summary>Extracts the .vN.M filename version from versioned pack
    /// files (corp.v0.3.json), or null for base files (corp.json).</summary>
    private static string? FileVersionSuffix(string name, string packId)
    {
        var file = FilePart(name);
        var prefix = $"{packId}.";
        if (!file.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = file[prefix.Length..];
        if (string.Equals(rest, "json", StringComparison.Ordinal))
        {
            return null;
        }

        return rest.EndsWith(".json", StringComparison.Ordinal) ? rest[..^".json".Length] : null;
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static PackJson ParsePack(string name, string json)
    {
        PackJson? pack;
        try
        {
            pack = JsonSerializer.Deserialize<PackJson>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Rule pack '{name}' is not valid JSON.", ex);
        }

        if (pack is null || string.IsNullOrWhiteSpace(pack.Pack) || pack.Rules is null)
        {
            throw new InvalidOperationException($"Rule pack '{name}' deserialized to null.");
        }

        return pack;
    }

    /// <summary>Pack-level company adoption: the named owner plus the date
    /// the company adopted the pack. Both are required; a pack without an
    /// owner or with an unparseable date refuses loudly.</summary>
    private static (string Owner, DateOnly Adopted) RequireAdoption(PackJson winner)
    {
        if (string.IsNullOrWhiteSpace(winner.Owner))
        {
            throw new InvalidOperationException(
                $"Rule pack '{winner.Pack}' {winner.Version} carries no owner; every pack names the company owner who adopted it.");
        }

        if (!DateOnly.TryParse(winner.Adopted, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var adopted))
        {
            throw new InvalidOperationException(
                $"Rule pack '{winner.Pack}' {winner.Version} carries no parseable adopted date (got '{winner.Adopted ?? "<missing>"}'); " +
                "every pack records the company adoption date as yyyy-MM-dd.");
        }

        return (winner.Owner, adopted);
    }

    private sealed record PackJson(
        string Pack,
        string Version,
        List<PackRuleJson> Rules,
        PackConfigJson? Config,
        PackProvenanceJson? Provenance,
        string? Owner = null,
        string? Adopted = null);

    private sealed record PackRuleJson(
        string Id, RuleTier Tier, ComplianceGate Gate, string Title, string Source, bool Enforced,
        string? DocumentKind, string? DocumentLabel, string? RuleType, Dictionary<string, string>? Params);

    private sealed record PackConfigJson(decimal OriginCap, decimal VendorCap, string PolicySource);

    private sealed record PackProvenanceJson(string MemoId, string Approver, string EffectiveAt);
}
