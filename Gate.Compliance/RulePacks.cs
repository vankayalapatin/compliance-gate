// Rule pack loading: versioned rule records plus pack thresholds load from
// per-pack JSON (embedded), never from C# literals. Pack order follows
// PROJECT.md section 6: BAT-LI, ELEC, SCR, CORP.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gate.Compliance;

public sealed record CorpPackConfig(decimal OriginCap, decimal VendorCap, string PolicySource);

public sealed record RulePackSet(IReadOnlyList<RuleDefinition> Rules, CorpPackConfig Corp);

public static class RulePackLoader
{
    private static readonly string[] PackSuffixes =
    [
        "Packs.bat-li.json",
        "Packs.elec.json",
        "Packs.scr.json",
        "Packs.corp.json"
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
        var rules = new List<RuleDefinition>();
        CorpPackConfig? corp = null;
        foreach (var suffix in PackSuffixes)
        {
            var resource = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
            if (resource is null)
            {
                throw new InvalidOperationException(
                    $"Embedded rule pack ending in '{suffix}' was not found in {assembly.GetName().Name}.");
            }

            var pack = ReadPack(assembly, resource);
            if (!string.Equals(pack.Version, RuleCatalog.PackVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Rule pack '{resource}' version '{pack.Version}' does not match expected '{RuleCatalog.PackVersion}'.");
            }

            foreach (var rule in pack.Rules)
            {
                rules.Add(new RuleDefinition(
                    rule.Id, pack.Pack, pack.Version, rule.Tier, rule.Gate,
                    rule.Title, rule.Source, rule.Enforced,
                    rule.DocumentKind, rule.DocumentLabel));
            }

            if (pack.Config is not null
                && string.Equals(pack.Pack, "CORP", StringComparison.Ordinal))
            {
                corp = new CorpPackConfig(pack.Config.OriginCap, pack.Config.VendorCap, pack.Config.PolicySource);
            }
        }

        if (corp is null)
        {
            throw new InvalidOperationException("CORP pack config (originCap, vendorCap, policySource) is missing.");
        }

        return new RulePackSet(rules, corp);
    }

    private static PackJson ReadPack(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
        }

        PackJson? pack;
        try
        {
            pack = JsonSerializer.Deserialize<PackJson>(stream, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Embedded rule pack '{resource}' is not valid JSON.", ex);
        }

        if (pack is null || string.IsNullOrWhiteSpace(pack.Pack) || pack.Rules is null)
        {
            throw new InvalidOperationException($"Embedded rule pack '{resource}' deserialized to null.");
        }

        return pack;
    }

    private sealed record PackJson(string Pack, string Version, List<PackRuleJson> Rules, PackConfigJson? Config);

    private sealed record PackRuleJson(
        string Id, RuleTier Tier, ComplianceGate Gate, string Title, string Source, bool Enforced,
        string? DocumentKind, string? DocumentLabel);

    private sealed record PackConfigJson(decimal OriginCap, decimal VendorCap, string PolicySource);
}
