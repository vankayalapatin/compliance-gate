// Company KB master file (docs/company-kb/KB-SCHEMA.md + SYNC.md).
// company/company-kb.json v1 mirrors the scenario values on every SYNC
// overlap field: users + signing limits, items + critical flags, vendors +
// countries, payment terms, trailing history totals, CORP cap values.
// Agreement is proven by the mirror test, never asserted.
//
// The file carries the KbSnapshot contract sections plus compliance-owned
// mirror sections (mirrorUsers, mirrorVendors, mirrorPaymentTerms,
// mirrorHistory). The mirror sections exist because SYNC.md requires
// exact equality on fields the KbSnapshot contract does not model; they
// are not citable (citations resolve against the snapshot sections only),
// and promoting them into the contract is a wave-2 lead decision.
//
// Integrity: the file pins version + hash. The hash is SHA-256 over the
// file bytes with the stored hash value redacted, so authoring tools in
// any language reproduce it byte-for-byte. The loader refuses a mismatch
// loudly; drift fails, never passes silently.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gate.Contracts;

namespace Gate.Compliance;

public sealed record KbMirrorUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("signingLimit")] decimal SigningLimit,
    [property: JsonPropertyName("limitCurrency")] string LimitCurrency);

public sealed record KbMirrorVendor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("country")] string Country);

public sealed record KbMirrorTerm(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("netDays")] int NetDays);

public sealed record KbMirrorHistoryRow(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("cells")] decimal Cells);

public sealed record KbMirrorHistory(
    [property: JsonPropertyName("itemId")] string ItemId,
    [property: JsonPropertyName("rows")] IReadOnlyList<KbMirrorHistoryRow> Rows);

/// <summary>The company-kb.json file root: contract sections plus mirror
/// sections. ToSnapshot projects the citable contract view.</summary>
public sealed record CompanyKbFile(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("legalEntities")] IReadOnlyList<KbLegalEntity> LegalEntities,
    [property: JsonPropertyName("sitesWarehouses")] IReadOnlyList<KbSite> SitesWarehouses,
    [property: JsonPropertyName("fiscalCalendar")] KbFiscalCalendar? FiscalCalendar,
    [property: JsonPropertyName("approvalMatrix")] IReadOnlyList<KbApprovalEntry> ApprovalMatrix,
    [property: JsonPropertyName("items")] IReadOnlyList<KbItem> Items,
    [property: JsonPropertyName("sourcingPolicies")] IReadOnlyList<KbSourcingPolicy> SourcingPolicies,
    [property: JsonPropertyName("regionRules")] IReadOnlyList<KbRegionRule> RegionRules,
    [property: JsonPropertyName("documents")] IReadOnlyList<KbDocument> Documents,
    [property: JsonPropertyName("mirrorUsers")] IReadOnlyList<KbMirrorUser> MirrorUsers,
    [property: JsonPropertyName("mirrorVendors")] IReadOnlyList<KbMirrorVendor> MirrorVendors,
    [property: JsonPropertyName("mirrorPaymentTerms")] IReadOnlyList<KbMirrorTerm> MirrorPaymentTerms,
    [property: JsonPropertyName("mirrorHistory")] KbMirrorHistory MirrorHistory)
{
    public KbSnapshot ToSnapshot() => new(
        Version, Hash, LegalEntities, SitesWarehouses, FiscalCalendar,
        ApprovalMatrix, Items, SourcingPolicies, RegionRules, Documents);
}

public static class CompanyKbLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads and verifies the KB file text: parses, verifies the
    /// pinned hash, then validates schema plus site references. Every
    /// failure throws loudly with context.</summary>
    public static CompanyKbFile Load(string fileText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileText);

        CompanyKbFile? file;
        try
        {
            file = JsonSerializer.Deserialize<CompanyKbFile>(fileText, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("company-kb.json is not valid JSON.", ex);
        }

        if (file is null)
        {
            throw new InvalidOperationException("company-kb.json deserialized to null.");
        }

        var recomputed = CompanyKbHash.Compute(fileText);
        if (!string.Equals(file.Hash, recomputed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"company-kb.json hash mismatch: the file pins '{file.Hash}' but the content recomputes " +
                $"'{recomputed}'. The KB changed without re-pinning; drift fails, never passes silently.");
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(file.Version))
        {
            errors.Add("KB version is empty; it is required.");
        }

        RequireUnique(errors, file.LegalEntities.Select(e => e.Id), "legalEntities");
        RequireUnique(errors, file.SitesWarehouses.Select(s => s.Id), "sitesWarehouses");
        RequireUnique(errors, file.Items.Select(i => i.Id), "items");
        RequireUnique(errors, file.SourcingPolicies.Select(p => p.Id), "sourcingPolicies");
        RequireUnique(errors, file.RegionRules.Select(r => r.Id), "regionRules");
        RequireUnique(errors, file.Documents.Select(d => d.Id), "documents");
        RequireUnique(errors, file.MirrorUsers.Select(u => u.Id), "mirrorUsers");
        RequireUnique(errors, file.MirrorVendors.Select(v => v.Id), "mirrorVendors");
        RequireUnique(errors, file.MirrorPaymentTerms.Select(t => t.Id), "mirrorPaymentTerms");

        foreach (var site in file.SitesWarehouses)
        {
            if (!file.LegalEntities.Any(e => e.Id == site.EntityId))
            {
                errors.Add($"Site '{site.Id}' references unknown legal entity '{site.EntityId}'.");
            }

            if (!file.RegionRules.Any(r => r.Id == site.Region))
            {
                errors.Add($"Site '{site.Id}' references unknown region rule '{site.Region}'.");
            }
        }

        foreach (var region in file.RegionRules)
        {
            if (region.Countries.Count == 0)
            {
                errors.Add($"Region rule '{region.Id}' names no countries; region boundaries are explicit data.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "company-kb.json schema validation failed:\n- " + string.Join("\n- ", errors));
        }

        return file;
    }

    /// <summary>Loads the KB file from disk, resolving company/company-kb.json
    /// from the caller's directory upward like the scenario loader.</summary>
    public static CompanyKbFile LoadFromFile(string configuredPath)
    {
        var file = ResolvePath(configuredPath);
        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"KB file not found: {configuredPath} (resolved to {file}).");
        }

        return Load(File.ReadAllText(file));
    }

    public static string ResolvePath(string configured)
    {
        if (File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configured);
    }

    private static void RequireUnique(List<string> errors, IEnumerable<string> ids, string section)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add($"KB section '{section}' carries an empty id.");
            }
            else if (!seen.Add(id))
            {
                errors.Add($"KB section '{section}' carries duplicate id '{id}'.");
            }
        }
    }
}

public static class CompanyKbHash
{
    /// <summary>Computes the KB content hash: SHA-256 (hex, lowercase) over
    /// the file's UTF-8 bytes with the stored "hash" value redacted to 64
    /// zeros. Redaction keeps the op byte-exact across languages: any tool
    /// replaces the one "hash" value and hashes.</summary>
    public static string Compute(string fileText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileText);
        var redacted = Redact(fileText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(redacted))).ToLowerInvariant();
    }

    internal static string Redact(string fileText)
    {
        var key = fileText.IndexOf("\"hash\"", StringComparison.Ordinal);
        if (key < 0)
        {
            throw new InvalidOperationException("company-kb.json carries no \"hash\" property to verify.");
        }

        var colon = fileText.IndexOf(':', key);
        var openQuote = fileText.IndexOf('"', colon + 1);
        var closeQuote = fileText.IndexOf('"', openQuote + 1);
        if (colon < 0 || openQuote < 0 || closeQuote < 0)
        {
            throw new InvalidOperationException("company-kb.json \"hash\" property is malformed; cannot verify.");
        }

        return fileText[..(openQuote + 1)] + new string('0', 64) + fileText[closeQuote..];
    }
}
