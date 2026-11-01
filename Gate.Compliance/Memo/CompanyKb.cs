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
/// sections. ToSnapshot projects the citable contract view. Comment is the
/// optional $comment documentation line, carried by the writer. The six
/// wiki sections are null on the pre-wiki first version and read as empty;
/// the writer always ships them.</summary>
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
    [property: JsonPropertyName("leadership")] IReadOnlyList<KbWikiEntry>? Leadership = null,
    [property: JsonPropertyName("finance")] IReadOnlyList<KbWikiEntry>? Finance = null,
    [property: JsonPropertyName("procurement")] IReadOnlyList<KbWikiEntry>? Procurement = null,
    [property: JsonPropertyName("hr")] IReadOnlyList<KbWikiEntry>? Hr = null,
    [property: JsonPropertyName("regulatory")] IReadOnlyList<KbWikiEntry>? Regulatory = null,
    [property: JsonPropertyName("operations")] IReadOnlyList<KbWikiEntry>? Operations = null,
    [property: JsonPropertyName("mirrorUsers")] IReadOnlyList<KbMirrorUser> MirrorUsers = null!,
    [property: JsonPropertyName("mirrorVendors")] IReadOnlyList<KbMirrorVendor> MirrorVendors = null!,
    [property: JsonPropertyName("mirrorPaymentTerms")] IReadOnlyList<KbMirrorTerm> MirrorPaymentTerms = null!,
    [property: JsonPropertyName("mirrorHistory")] KbMirrorHistory MirrorHistory = null!,
    [property: JsonPropertyName("$comment")] string? Comment = null)
{
    public KbSnapshot ToSnapshot() => new(
        Version, Hash, LegalEntities, SitesWarehouses, FiscalCalendar,
        ApprovalMatrix, Items, SourcingPolicies, RegionRules, Documents,
        Leadership ?? [], Finance ?? [], Procurement ?? [],
        Hr ?? [], Regulatory ?? [], Operations ?? []);
}

public static class CompanyKbLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
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
        var versionNumber = 0;
        if (string.IsNullOrWhiteSpace(file.Version))
        {
            errors.Add("KB version is empty; it is required.");
        }
        else
        {
            try
            {
                versionNumber = CompanyKbVersions.Parse(file.Version);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        RequireUnique(errors, file.LegalEntities.Select(e => e.Id), "legalEntities");
        RequireUnique(errors, file.SitesWarehouses.Select(s => s.Id), "sitesWarehouses");
        RequireUnique(errors, file.Items.Select(i => i.Id), "items");
        RequireUnique(errors, file.SourcingPolicies.Select(p => p.Id), "sourcingPolicies");
        RequireUnique(errors, file.RegionRules.Select(r => r.Id), "regionRules");
        RequireUnique(errors, file.Documents.Select(d => d.Id), "documents");
        RequireUnique(errors, (file.Leadership ?? []).Select(w => w.Id), KbCitations.WikiLeadership);
        RequireUnique(errors, (file.Finance ?? []).Select(w => w.Id), KbCitations.WikiFinance);
        RequireUnique(errors, (file.Procurement ?? []).Select(w => w.Id), KbCitations.WikiProcurement);
        RequireUnique(errors, (file.Hr ?? []).Select(w => w.Id), KbCitations.WikiHr);
        RequireUnique(errors, (file.Regulatory ?? []).Select(w => w.Id), KbCitations.WikiRegulatory);
        RequireUnique(errors, (file.Operations ?? []).Select(w => w.Id), KbCitations.WikiOperations);
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

        // The first version predates audit: its entries omit audit fields
        // and that is tolerated. From the second version on, every entry
        // in the fourteen citable sections carries version plus full
        // audit, entry owner, lifecycle status, and effective date;
        // anything missing refuses. Mirror sections stay unaudited at
        // every version.
        if (versionNumber >= 2)
        {
            RequireAudit(errors, file);
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

    /// <summary>Loads a versioned KB set from named file contents and
    /// returns the highest version. Every file is hash-verified, schema-
    /// validated, and audit-checked; filename/content version mismatches,
    /// malformed versions, and duplicate versions refuse loudly.</summary>
    public static CompanyKbFile LoadVersioned(IEnumerable<(string Name, string Json)> inputs)
    {
        var checked_ = LoadAndCheckAll(inputs);
        return checked_
            .OrderByDescending(c => c.File.Version, Comparer<string>.Create(CompanyKbVersions.Compare))
            .First()
            .File;
    }

    /// <summary>Loads one exact KB version for pinned display. An unknown
    /// version refuses loudly, naming what is available.</summary>
    public static CompanyKbFile LoadVersionedAt(
        IEnumerable<(string Name, string Json)> inputs, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        try
        {
            CompanyKbVersions.Parse(version);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Cannot load KB version '{version}': {ex.Message}", ex);
        }

        var checked_ = LoadAndCheckAll(inputs);
        var match = checked_.FirstOrDefault(c =>
            string.Equals(c.File.Version, version, StringComparison.Ordinal));
        if (match.File is null)
        {
            var available = string.Join(", ", checked_
                .Select(c => c.File.Version)
                .OrderBy(v => v, Comparer<string>.Create(CompanyKbVersions.Compare)));
            throw new InvalidOperationException(
                $"Unknown KB version '{version}' (available: {available}).");
        }

        return match.File;
    }

    /// <summary>Reads the declared version property from one KB file's
    /// JSON. Used to locate a base version among validated inputs.</summary>
    public static string ReadVersion(string name, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString()))
            {
                return version.GetString()!;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"KB file '{name}' is not valid JSON.", ex);
        }

        throw new InvalidOperationException(
            $"KB file '{name}' carries no version property; refusing to guess which version it is.");
    }

    private static List<(string Name, CompanyKbFile File)> LoadAndCheckAll(
        IEnumerable<(string Name, string Json)> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var list = inputs.ToList();
        if (list.Count == 0)
        {
            throw new InvalidOperationException("No KB files supplied; at least one versioned file is required.");
        }

        var checked_ = new List<(string Name, CompanyKbFile File)>();
        foreach (var (name, json) in list)
        {
            var expected = ExpectedVersionFor(name);
            CompanyKbFile file;
            try
            {
                file = Load(json);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException($"KB file '{name}': {ex.Message}", ex);
            }

            if (expected is not null
                && !string.Equals(expected, file.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"KB file '{name}' names version '{expected}' but declares '{file.Version}'; " +
                    "refusing to guess which wins.");
            }

            checked_.Add((name, file));
        }

        var duplicates = checked_
            .GroupBy(c => c.File.Version, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' ({string.Join(" and ", g.Select(c => $"'{c.Name}'"))})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"KB version {string.Join(", ", duplicates)} ships twice; " +
                "versions must be unique so exactly one wins.");
        }

        return checked_;
    }

    /// <summary>Version a KB file name claims: null for the base file
    /// (judged by its declared version), the suffix for versioned files.
    /// Anything else is not a KB file and refuses loudly.</summary>
    private static string? ExpectedVersionFor(string name)
    {
        if (string.Equals(name, CompanyKbWriter.BaseFileName, StringComparison.Ordinal))
        {
            return null;
        }

        if (name.StartsWith(CompanyKbWriter.FilePrefix, StringComparison.Ordinal)
            && name.EndsWith(CompanyKbWriter.FileSuffix, StringComparison.Ordinal)
            && name.Length > CompanyKbWriter.FilePrefix.Length + CompanyKbWriter.FileSuffix.Length)
        {
            return name[CompanyKbWriter.FilePrefix.Length..^CompanyKbWriter.FileSuffix.Length];
        }

        throw new InvalidOperationException(
            $"KB input '{name}' is not a KB file; expected '{CompanyKbWriter.BaseFileName}' or " +
            $"'{CompanyKbWriter.FilePrefix}<version>{CompanyKbWriter.FileSuffix}'.");
    }

    private static void RequireAudit(List<string> errors, CompanyKbFile file)
    {
        foreach (var e in file.LegalEntities)
        {
            RequireEntryAudit(errors, KbCitations.LegalEntities, e.Id, e.Version,
                e.CreatedBy, e.CreatedAt, e.ModifiedBy, e.ModifiedAt, e.ApprovedBy,
                e.Owner, e.Status, e.EffectiveDate);
        }

        foreach (var s in file.SitesWarehouses)
        {
            RequireEntryAudit(errors, KbCitations.SitesWarehouses, s.Id, s.Version,
                s.CreatedBy, s.CreatedAt, s.ModifiedBy, s.ModifiedAt, s.ApprovedBy,
                s.Owner, s.Status, s.EffectiveDate);
        }

        if (file.FiscalCalendar is not null)
        {
            RequireEntryAudit(errors, KbCitations.FiscalCalendar, KbCitations.FiscalCalendarCurrent,
                file.FiscalCalendar.Version, file.FiscalCalendar.CreatedBy, file.FiscalCalendar.CreatedAt,
                file.FiscalCalendar.ModifiedBy, file.FiscalCalendar.ModifiedAt, file.FiscalCalendar.ApprovedBy,
                file.FiscalCalendar.Owner, file.FiscalCalendar.Status, file.FiscalCalendar.EffectiveDate);
        }

        foreach (var a in file.ApprovalMatrix)
        {
            RequireEntryAudit(errors, KbCitations.ApprovalMatrix, a.Action, a.Version,
                a.CreatedBy, a.CreatedAt, a.ModifiedBy, a.ModifiedAt, a.ApprovedBy,
                OwnerOf(a), a.Status, a.EffectiveDate);
        }

        foreach (var i in file.Items)
        {
            RequireEntryAudit(errors, KbCitations.Items, i.Id, i.Version,
                i.CreatedBy, i.CreatedAt, i.ModifiedBy, i.ModifiedAt, i.ApprovedBy,
                i.Owner, i.Status, i.EffectiveDate);
        }

        foreach (var p in file.SourcingPolicies)
        {
            RequireEntryAudit(errors, KbCitations.SourcingPolicies, p.Id, p.Version,
                p.CreatedBy, p.CreatedAt, p.ModifiedBy, p.ModifiedAt, p.ApprovedBy,
                p.Owner, p.Status, p.EffectiveDate);
        }

        foreach (var r in file.RegionRules)
        {
            RequireEntryAudit(errors, KbCitations.RegionRules, r.Id, r.Version,
                r.CreatedBy, r.CreatedAt, r.ModifiedBy, r.ModifiedAt, r.ApprovedBy,
                r.Owner, r.Status, r.EffectiveDate);
        }

        foreach (var d in file.Documents)
        {
            RequireEntryAudit(errors, KbCitations.Documents, d.Id, d.Version,
                d.CreatedBy, d.CreatedAt, d.ModifiedBy, d.ModifiedAt, d.ApprovedBy,
                d.Owner, d.Status, d.EffectiveDate);
        }

        RequireWikiAudit(errors, file.Leadership, KbCitations.WikiLeadership);
        RequireWikiAudit(errors, file.Finance, KbCitations.WikiFinance);
        RequireWikiAudit(errors, file.Procurement, KbCitations.WikiProcurement);
        RequireWikiAudit(errors, file.Hr, KbCitations.WikiHr);
        RequireWikiAudit(errors, file.Regulatory, KbCitations.WikiRegulatory);
        RequireWikiAudit(errors, file.Operations, KbCitations.WikiOperations);
    }

    private static void RequireWikiAudit(
        List<string> errors, IReadOnlyList<KbWikiEntry>? entries, string section)
    {
        foreach (var w in entries ?? [])
        {
            RequireEntryAudit(errors, section, w.Id, w.Version,
                w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt, w.ApprovedBy,
                w.Owner, w.Status, w.EffectiveDate);
        }
    }

    /// <summary>Approval-matrix rows use Owner for the named person, so the
    /// entry-owner stamp has no room there; the row owner doubles as the
    /// entry owner for audit.</summary>
    private static string? OwnerOf(KbApprovalEntry row) => row.Owner;

    private static void RequireEntryAudit(
        List<string> errors,
        string section,
        string entryId,
        string? version,
        string? createdBy,
        DateTimeOffset? createdAt,
        string? modifiedBy,
        DateTimeOffset? modifiedAt,
        string? approvedBy,
        string? owner,
        KbEntryStatus? status,
        DateOnly? effectiveDate)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(version))
        {
            missing.Add("version");
        }
        else
        {
            try
            {
                CompanyKbVersions.Parse(version);
            }
            catch (InvalidOperationException)
            {
                missing.Add($"version '{version}' (not of the form kbvN)");
            }
        }

        if (string.IsNullOrWhiteSpace(createdBy))
        {
            missing.Add("createdBy");
        }

        if (!createdAt.HasValue || createdAt.Value == default)
        {
            missing.Add("createdAt");
        }

        if (string.IsNullOrWhiteSpace(modifiedBy))
        {
            missing.Add("modifiedBy");
        }

        if (!modifiedAt.HasValue || modifiedAt.Value == default)
        {
            missing.Add("modifiedAt");
        }

        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            missing.Add("approvedBy");
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            missing.Add("owner");
        }

        if (!status.HasValue)
        {
            missing.Add("status");
        }

        if (!effectiveDate.HasValue || effectiveDate.Value == default)
        {
            missing.Add("effectiveDate");
        }

        if (missing.Count > 0)
        {
            errors.Add(
                $"KB entry {section}#{entryId} is missing audit: {string.Join(", ", missing)}. " +
                "Entries from the second version on carry version plus full audit, owner, status, and effective date.");
        }
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
