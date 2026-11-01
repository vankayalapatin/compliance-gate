// Company KB writer, the KB edit path of docs/company-kb/KB-SCHEMA.md.
// KB edits go UI-to-engine direct through
// IComplianceEngine.PublishKbUpdateAsync, never through the memo stages:
// one space per version, approved by the space owner, editors never
// approving their own change. A delegate may approve for the space owner
// (the UI verifies the ERP delegation window; the writer only checks the
// algebra: the owner match runs on ApprovedFor ?? Approver, the acting
// approver never authored a touched entry, and ApprovedFor never names
// the acting approver). Every add/edit mints kbv(n+1) with the touched
// entries stamped to the new version: version plus audit plus entry
// owner (the space owner), Active status, and the publish-date
// effective date.
//
// Pure by design, mirroring PackPublisher/PackVersions: BuildNextVersion
// and BuildMigration return the file name plus JSON plus activation
// record; only WriteToDirectory touches disk, and only to create the new
// file (refusing to overwrite). Files are one per version:
// company-kb.json (kbv1, frozen) plus company-kb.kbvN.json for N >= 2.
// Priors stay on disk for pinned proposals and audit.
//
// No business values here: versions, hashes, owner names, roles, entry
// ids, timestamps, and wiki seed content arrive at runtime. The kbvN
// scheme, the company-kb file stem, the space map, and the system
// backfill actor are schema vocabulary (KB-SCHEMA.md), like the section
// constants in KbCitations.
using System.Text.Json;
using System.Text.Json.Serialization;
using Gate.Contracts;

namespace Gate.Compliance;

public static class CompanyKbVersions
{
    public const string Prefix = "kbv";

    /// <summary>Parses kbvN into N. Anything else throws loudly.</summary>
    public static int Parse(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(version[Prefix.Length..], out var n)
            && n >= 1)
        {
            return n;
        }

        throw new InvalidOperationException(
            $"KB version '{version}' is not of the form kbvN where N >= 1; refusing to guess.");
    }

    /// <summary>Compares two versions numerically (kbv10 beats kbv9).</summary>
    public static int Compare(string left, string right) =>
        Parse(left).CompareTo(Parse(right));

    /// <summary>Mints kbv(N+1). Every add/edit publishes a new version;
    /// files are never overwritten.</summary>
    public static string Next(string version) => $"{Prefix}{Parse(version) + 1}";
}

public static class CompanyKbSpaces
{
    public const string Leadership = "leadership";

    public const string Finance = "finance";

    public const string Procurement = "procurement";

    public const string Hr = "hr";

    public const string Regulatory = "regulatory";

    public const string Operations = "operations";

    /// <summary>Approval-matrix action prefix behind each space owner:
    /// kb-edit-leadership, kb-edit-finance, and so on.</summary>
    public const string OwnerActionPrefix = "kb-edit-";

    public static readonly IReadOnlyList<string> All =
    [
        Leadership, Finance, Procurement, Hr, Regulatory, Operations
    ];

    private static readonly IReadOnlyDictionary<string, string> SectionToSpace =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KbCitations.LegalEntities] = Leadership,
            [KbCitations.WikiLeadership] = Leadership,
            [KbCitations.FiscalCalendar] = Finance,
            [KbCitations.ApprovalMatrix] = Finance,
            [KbCitations.WikiFinance] = Finance,
            [KbCitations.SourcingPolicies] = Procurement,
            [KbCitations.RegionRules] = Procurement,
            [KbCitations.Documents] = Procurement,
            [KbCitations.WikiProcurement] = Procurement,
            [KbCitations.WikiHr] = Hr,
            [KbCitations.WikiRegulatory] = Regulatory,
            [KbCitations.SitesWarehouses] = Operations,
            [KbCitations.Items] = Operations,
            [KbCitations.WikiOperations] = Operations,
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SpaceSections =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Leadership] = [KbCitations.LegalEntities, KbCitations.WikiLeadership],
            [Finance] = [KbCitations.FiscalCalendar, KbCitations.ApprovalMatrix, KbCitations.WikiFinance],
            [Procurement] =
            [
                KbCitations.SourcingPolicies,
                KbCitations.RegionRules,
                KbCitations.Documents,
                KbCitations.WikiProcurement,
            ],
            [Hr] = [KbCitations.WikiHr],
            [Regulatory] = [KbCitations.WikiRegulatory],
            [Operations] = [KbCitations.SitesWarehouses, KbCitations.Items, KbCitations.WikiOperations],
        };

    /// <summary>Maps a citable section to its space. Mirror sections belong
    /// to no space; unknown sections refuse loudly.</summary>
    public static string SpaceFor(string section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (SectionToSpace.TryGetValue(section, out var space))
        {
            return space;
        }

        throw new InvalidOperationException(
            $"KB section '{section}' belongs to no knowledge space. " +
            $"Known sections: {string.Join(", ", KbCitations.KnownSections)}. " +
            "Mirror sections stay unaudited and are never edited through the writer.");
    }

    /// <summary>Sections per space in KB-SCHEMA.md order.</summary>
    public static IReadOnlyList<string> SectionsFor(string space)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(space);
        if (SpaceSections.TryGetValue(space, out var sections))
        {
            return sections;
        }

        throw new InvalidOperationException(
            $"Unknown knowledge space '{space}'. Known spaces: {string.Join(", ", All)}.");
    }

    /// <summary>True for the six wiki sections (each the whole of its
    /// space, or one section within a multi-section space).</summary>
    public static bool IsWikiSection(string section) =>
        KbCitations.WikiSections.Contains(section, StringComparer.Ordinal);

    /// <summary>Approval-matrix action naming a space owner
    /// (kb-edit-{space}).</summary>
    public static string OwnerActionFor(string space)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(space);
        if (!SpaceSections.ContainsKey(space))
        {
            throw new InvalidOperationException(
                $"Unknown knowledge space '{space}'. Known spaces: {string.Join(", ", All)}.");
        }

        return OwnerActionPrefix + space;
    }
}

/// <summary>One migration space owner: the space plus the matrix row's
/// owner name and role. All six arrive at runtime.</summary>
public sealed record KbMigrationOwner(string Space, string Owner, string OwnerRole);

/// <summary>One migration wiki seed: the wiki section plus the authored
/// entry (id, title, body, tags). The migration stamps version, audit,
/// owner, status, and effective date itself; authored stamps are
/// replaced, never trusted.</summary>
public sealed record KbMigrationWikiSeed(string Section, KbWikiEntry Entry);

public static class CompanyKbWriter
{
    /// <summary>Sentinel actor stamping the one-time kbv1 backfill, which
    /// predates audit. kbv1 entries were unattributed; the migration
    /// stamps system rather than inventing an author.</summary>
    public const string SystemActor = "system";

    /// <summary>Frozen kbv1 file. The writer only ever creates versioned
    /// files; it never writes this name.</summary>
    public const string BaseFileName = "company-kb.json";

    public const string FilePrefix = "company-kb.";

    public const string FileSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions EntryOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Versioned file name for a minted version
    /// (company-kb.kbvN.json).</summary>
    public static string FileNameFor(string newVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newVersion);
        return $"{FilePrefix}{newVersion}{FileSuffix}";
    }

    /// <summary>Deterministic file serialization with the content hash
    /// pinned: same file object serializes byte-identical on replay, and
    /// the pinned hash verifies via CompanyKbHash.</summary>
    public static string Serialize(CompanyKbFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var placeholder = file with { Hash = new string('0', 64) };
        var hash = CompanyKbHash.Compute(JsonSerializer.Serialize(placeholder, JsonOpts));
        return JsonSerializer.Serialize(file with { Hash = hash }, JsonOpts);
    }

    /// <summary>Builds one KB version from a base version plus changes.
    /// The base JSON carries the authority: its pinned hash must verify
    /// (drift fails) and its version must equal the claimed base version
    /// (stale bases refuse). Single-space edits only, approved by the
    /// space owner taken from the BASE snapshot matrix, editors never
    /// approving their own change. A delegate may approve for the owner:
    /// the owner match runs on approvedFor ?? approvedBy, the acting
    /// approver still never authored a touched entry, and approvedFor
    /// never names the acting approver (the UI verifies the ERP
    /// delegation window; the writer checks the algebra only). Touched
    /// entries are stamped to the new version with audit plus the space
    /// owner, Active status (edits keep a prior status), and the
    /// publish-date effective date; carried entries keep their stamps
    /// untouched. Pre-audit bases refuse: the one-time migration owns
    /// the first audited version.</summary>
    public static KbPublication BuildNextVersion(
        string baseKbJson,
        string baseVersion,
        IReadOnlyList<KbEntryChange> changes,
        string approvedBy,
        string reason,
        string activationId,
        DateTimeOffset publishedAt,
        string? approvedFor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKbJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseVersion);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationId);
        if (publishedAt == default)
        {
            throw new ArgumentException("The publication timestamp is required.", nameof(publishedAt));
        }

        var delegationFor = string.IsNullOrWhiteSpace(approvedFor) ? null : approvedFor;

        if (changes.Count == 0)
        {
            throw new InvalidOperationException(
                "No KB changes supplied; a KB version touches at least one entry in exactly one space.");
        }

        CompanyKbFile baseFile;
        try
        {
            baseFile = CompanyKbLoader.Load(baseKbJson);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"KB publish base '{baseVersion}' failed to load: {ex.Message}", ex);
        }

        if (!string.Equals(baseFile.Version, baseVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stale KB base: the caller pins '{baseVersion}' but the base content is '{baseFile.Version}'. " +
                "Re-base on the current version before publishing.");
        }

        var unaudited = FirstUnauditedEntry(baseFile);
        if (unaudited is not null)
        {
            throw new InvalidOperationException(
                $"KB publish from base '{baseVersion}' refuses: entry {unaudited} predates audit. " +
                "The one-time migration (BuildMigration) owns the first audited version; publish edits on top of it.");
        }

        var parsed = changes.Select((change, index) => ParseChange(change, index)).ToList();

        var duplicates = parsed
            .GroupBy(p => $"{p.Change.Section}#{p.Change.EntryId}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"KB publish touches {string.Join(", ", duplicates)} more than once in one version; " +
                "one change per entry per version, so the audit trail stays unambiguous.");
        }

        var spaces = parsed
            .Select(p => CompanyKbSpaces.SpaceFor(p.Change.Section))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (spaces.Count > 1)
        {
            throw new InvalidOperationException(
                $"KB publish spans spaces {string.Join(", ", spaces)}; " +
                "one KB version touches one space. Split the changes and publish each space separately.");
        }

        var space = spaces[0];
        var ownerAction = CompanyKbSpaces.OwnerActionFor(space);
        var ownerRows = baseFile.ApprovalMatrix
            .Where(a => string.Equals(a.Action, ownerAction, StringComparison.Ordinal))
            .ToList();
        if (ownerRows.Count == 0)
        {
            throw new InvalidOperationException(
                $"KB publish in space '{space}' refuses: base '{baseVersion}' carries no approval-matrix row " +
                $"for '{ownerAction}', so the space has no owner to approve.");
        }

        if (ownerRows.Count > 1)
        {
            throw new InvalidOperationException(
                $"KB publish in space '{space}' refuses: base '{baseVersion}' carries {ownerRows.Count} " +
                $"approval-matrix rows for '{ownerAction}', so the owner is ambiguous.");
        }

        var owner = ownerRows[0];
        if (delegationFor is not null
            && string.Equals(delegationFor, approvedBy, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"KB publish in space '{space}' refuses: approver '{approvedBy}' approves for themselves; " +
                "a delegate approves for someone else, and direct approvals leave ApprovedFor empty.");
        }

        var ownerMatch = delegationFor ?? approvedBy;
        if (!string.Equals(ownerMatch, owner.Owner, StringComparison.Ordinal))
        {
            var actor = delegationFor is null
                ? $"approver '{approvedBy}'"
                : $"approver '{approvedBy}' for '{delegationFor}'";
            throw new InvalidOperationException(
                $"KB publish in space '{space}' refuses: {actor} is not the space owner " +
                $"'{owner.Owner}' (matrix row '{ownerAction}' on base '{baseVersion}').");
        }

        var selfApproval = parsed.FirstOrDefault(p =>
            string.Equals(p.Change.ChangedBy, approvedBy, StringComparison.Ordinal));
        if (selfApproval is not null)
        {
            throw new InvalidOperationException(
                $"KB publish in space '{space}' refuses: approver '{approvedBy}' authored " +
                $"{selfApproval.Change.Section}#{selfApproval.Change.EntryId}; editors never approve their own change.");
        }

        var newVersion = CompanyKbVersions.Next(baseVersion);
        var stamps = new KbStamps(newVersion, approvedBy, owner.Owner, EffectiveDateFor(publishedAt));
        var next = new KbMutableFile(baseFile);
        foreach (var entry in parsed)
        {
            entry.Apply(next, stamps);
        }

        var file = next.ToFile(newVersion, baseFile.Comment);
        string json;
        try
        {
            json = Serialize(file);
            CompanyKbLoader.Load(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Built KB '{newVersion}' failed validation: {ex.Message}", ex);
        }

        var changedEntries = parsed
            .Select(p => $"{p.Change.Section}#{p.Change.EntryId}")
            .ToList();
        return new KbPublication(
            FileNameFor(newVersion),
            json,
            new KbVersionActivation(
                activationId, newVersion, baseVersion, space, approvedBy,
                publishedAt, reason, changedEntries, delegationFor));
    }

    /// <summary>Touched-entry effective date: the UTC calendar date of the
    /// publication timestamp.</summary>
    internal static DateOnly EffectiveDateFor(DateTimeOffset publishedAt) =>
        DateOnly.FromDateTime(publishedAt.UtcDateTime);

    /// <summary>The stamps one publication applies to every touched entry:
    /// the minted version, the acting approver, the space owner (entry
    /// owner), and the publish-date effective date.</summary>
    internal sealed record KbStamps(
        string NewVersion, string ApprovedBy, string Owner, DateOnly EffectiveDate);

    /// <summary>One-time backfill from a pre-audit base (the first version)
    /// to the next version. Every carried entry keeps its per-entry
    /// version (the base version where it had none) and gains system
    /// audit stamped at the migration clock, the section's space owner,
    /// Active status, and the migration-date effective date; the six
    /// kb-edit-* owner rows and the supplied wiki seeds enter at the new
    /// version with the same stamps. The base must be pre-audit and
    /// wiki-free: any entry already carrying audit, any kb-edit-* row,
    /// or any wiki entry refuses, so the migration can never run twice.
    /// Space owners, wiki seeds, the reason, and the file comment arrive
    /// at runtime. The activation space is finance (home of the matrix
    /// rows that arm the edit gate); the reason documents the all-space
    /// backfill, and the changed entries list every new row and seed.</summary>
    public static KbPublication BuildMigration(
        string baseKbJson,
        string baseVersion,
        IReadOnlyList<KbMigrationOwner> spaceOwners,
        IReadOnlyList<KbMigrationWikiSeed> wikiSeeds,
        string reason,
        string comment,
        string activationId,
        DateTimeOffset publishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseKbJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseVersion);
        ArgumentNullException.ThrowIfNull(spaceOwners);
        ArgumentNullException.ThrowIfNull(wikiSeeds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationId);
        if (publishedAt == default)
        {
            throw new ArgumentException("The migration timestamp is required.", nameof(publishedAt));
        }

        CompanyKbFile baseFile;
        try
        {
            baseFile = CompanyKbLoader.Load(baseKbJson);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"KB migration base '{baseVersion}' failed to load: {ex.Message}", ex);
        }

        if (!string.Equals(baseFile.Version, baseVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"KB migration base mismatch: the caller pins '{baseVersion}' but the base content is " +
                $"'{baseFile.Version}'.");
        }

        var audited = FirstAuditedEntry(baseFile);
        if (audited is not null)
        {
            throw new InvalidOperationException(
                $"KB migration refuses: base '{baseVersion}' entry {audited} already carries audit. " +
                "The migration backfills pre-audit KBs only; audited bases publish through BuildNextVersion.");
        }

        var ownerBySpace = RequireMigrationOwners(spaceOwners);
        RequireWikiFreeBase(baseFile, baseVersion);
        var seeds = RequireMigrationSeeds(wikiSeeds);

        var newVersion = CompanyKbVersions.Next(baseVersion);
        var effectiveDate = EffectiveDateFor(publishedAt);
        string SpaceOwner(string section) => ownerBySpace[CompanyKbSpaces.SpaceFor(section)].Owner;

        KbLegalEntity BackfillEntity(KbLegalEntity e) => e with
        {
            Version = e.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.LegalEntities),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbSite BackfillSite(KbSite s) => s with
        {
            Version = s.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.SitesWarehouses),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbApprovalEntry BackfillMatrix(KbApprovalEntry a) => a with
        {
            Version = a.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbItem BackfillItem(KbItem i) => i with
        {
            Version = i.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.Items),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbSourcingPolicy BackfillPolicy(KbSourcingPolicy p) => p with
        {
            Version = p.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.SourcingPolicies),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbRegionRule BackfillRegion(KbRegionRule r) => r with
        {
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.RegionRules),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbDocument BackfillDocument(KbDocument d) => d with
        {
            Version = d.Version ?? baseVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(KbCitations.Documents),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };
        KbWikiEntry StampSeed(KbMigrationWikiSeed seed) => seed.Entry with
        {
            Version = newVersion,
            CreatedBy = SystemActor,
            CreatedAt = publishedAt,
            ModifiedBy = SystemActor,
            ModifiedAt = publishedAt,
            ApprovedBy = SystemActor,
            Owner = SpaceOwner(seed.Section),
            Status = KbEntryStatus.Active,
            EffectiveDate = effectiveDate,
        };

        var matrix = baseFile.ApprovalMatrix.Select(BackfillMatrix).ToList();
        var changedEntries = new List<string>();
        foreach (var space in CompanyKbSpaces.All)
        {
            var action = CompanyKbSpaces.OwnerActionFor(space);
            var spaceOwner = ownerBySpace[space];
            matrix.Add(new KbApprovalEntry(
                action, spaceOwner.OwnerRole, spaceOwner.Owner, newVersion,
                SystemActor, publishedAt, SystemActor, publishedAt, SystemActor,
                KbEntryStatus.Active, effectiveDate));
            changedEntries.Add($"{KbCitations.ApprovalMatrix}#{action}");
        }

        var wikiBySection = seeds
            .GroupBy(s => s.Section, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<KbWikiEntry>)g.Select(StampSeed).ToList(),
                StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            changedEntries.Add($"{seed.Section}#{seed.Entry.Id}");
        }

        IReadOnlyList<KbWikiEntry> WikiFor(string section) =>
            wikiBySection.TryGetValue(section, out var entries) ? entries : [];

        var file = baseFile with
        {
            Version = newVersion,
            Comment = comment,
            LegalEntities = baseFile.LegalEntities.Select(BackfillEntity).ToList(),
            SitesWarehouses = baseFile.SitesWarehouses.Select(BackfillSite).ToList(),
            FiscalCalendar = baseFile.FiscalCalendar is null
                ? null
                : baseFile.FiscalCalendar with
                {
                    Version = baseFile.FiscalCalendar.Version ?? baseVersion,
                    CreatedBy = SystemActor,
                    CreatedAt = publishedAt,
                    ModifiedBy = SystemActor,
                    ModifiedAt = publishedAt,
                    ApprovedBy = SystemActor,
                    Owner = SpaceOwner(KbCitations.FiscalCalendar),
                    Status = KbEntryStatus.Active,
                    EffectiveDate = effectiveDate,
                },
            ApprovalMatrix = matrix,
            Items = baseFile.Items.Select(BackfillItem).ToList(),
            SourcingPolicies = baseFile.SourcingPolicies.Select(BackfillPolicy).ToList(),
            RegionRules = baseFile.RegionRules.Select(BackfillRegion).ToList(),
            Documents = baseFile.Documents.Select(BackfillDocument).ToList(),
            Leadership = WikiFor(KbCitations.WikiLeadership),
            Finance = WikiFor(KbCitations.WikiFinance),
            Procurement = WikiFor(KbCitations.WikiProcurement),
            Hr = WikiFor(KbCitations.WikiHr),
            Regulatory = WikiFor(KbCitations.WikiRegulatory),
            Operations = WikiFor(KbCitations.WikiOperations),
        };

        string json;
        try
        {
            json = Serialize(file);
            CompanyKbLoader.Load(json);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Built KB '{newVersion}' failed validation: {ex.Message}", ex);
        }

        return new KbPublication(
            FileNameFor(newVersion),
            json,
            new KbVersionActivation(
                activationId, newVersion, baseVersion, CompanyKbSpaces.Finance,
                SystemActor, publishedAt, reason, changedEntries));
    }

    /// <summary>Validates the migration space owners: exactly the six
    /// known spaces, each once, each with a named owner and role.</summary>
    private static IReadOnlyDictionary<string, KbMigrationOwner> RequireMigrationOwners(
        IReadOnlyList<KbMigrationOwner> spaceOwners)
    {
        var bySpace = new Dictionary<string, KbMigrationOwner>(StringComparer.Ordinal);
        for (var i = 0; i < spaceOwners.Count; i++)
        {
            var owner = spaceOwners[i];
            if (owner is null || string.IsNullOrWhiteSpace(owner.Space))
            {
                throw new InvalidOperationException(
                    $"KB migration owner {i} names no space; every knowledge space needs its owner.");
            }

            if (!CompanyKbSpaces.All.Contains(owner.Space, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"KB migration owner {i} names unknown space '{owner.Space}'. " +
                    $"Known spaces: {string.Join(", ", CompanyKbSpaces.All)}.");
            }

            if (!bySpace.TryAdd(owner.Space, owner))
            {
                throw new InvalidOperationException(
                    $"KB migration names space '{owner.Space}' twice; one owner row per space.");
            }

            if (string.IsNullOrWhiteSpace(owner.Owner))
            {
                throw new InvalidOperationException(
                    $"KB migration names no owner for space '{owner.Space}'; every space needs its owner.");
            }

            if (string.IsNullOrWhiteSpace(owner.OwnerRole))
            {
                throw new InvalidOperationException(
                    $"KB migration names no owner role for space '{owner.Space}' owner '{owner.Owner}'.");
            }
        }

        var missing = CompanyKbSpaces.All.Where(s => !bySpace.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"KB migration is missing owners for {string.Join(", ", missing)}; " +
                "all six spaces seed their kb-edit-* rows at once.");
        }

        return bySpace;
    }

    /// <summary>Pre-audit bases carry no wiki sections; any wiki entry on
    /// the base means the migration already ran (or the base was hand
    /// edited past it), so the migration refuses.</summary>
    private static void RequireWikiFreeBase(CompanyKbFile baseFile, string baseVersion)
    {
        var present = new List<string>();
        void Check(IReadOnlyList<KbWikiEntry>? entries, string section)
        {
            if (entries is not null && entries.Count > 0)
            {
                present.Add($"{section}#{entries[0].Id}");
            }
        }

        Check(baseFile.Leadership, KbCitations.WikiLeadership);
        Check(baseFile.Finance, KbCitations.WikiFinance);
        Check(baseFile.Procurement, KbCitations.WikiProcurement);
        Check(baseFile.Hr, KbCitations.WikiHr);
        Check(baseFile.Regulatory, KbCitations.WikiRegulatory);
        Check(baseFile.Operations, KbCitations.WikiOperations);

        if (present.Count > 0)
        {
            throw new InvalidOperationException(
                $"KB migration refuses: base '{baseVersion}' already carries wiki entries ({string.Join(", ", present)}). " +
                "The migration seeds the wiki once; later wiki edits publish through BuildNextVersion.");
        }

        var kbEdit = baseFile.ApprovalMatrix.FirstOrDefault(a =>
            a.Action.StartsWith(CompanyKbSpaces.OwnerActionPrefix, StringComparison.Ordinal));
        if (kbEdit is not null)
        {
            throw new InvalidOperationException(
                $"KB migration refuses: base '{baseVersion}' already carries approval-matrix row '{kbEdit.Action}'. " +
                "The migration seeds the kb-edit-* rows once.");
        }
    }

    /// <summary>Validates the migration wiki seeds: wiki sections only,
    /// complete entries, no duplicate section#id in the batch.</summary>
    private static List<KbMigrationWikiSeed> RequireMigrationSeeds(IReadOnlyList<KbMigrationWikiSeed> wikiSeeds)
    {
        var seeds = new List<KbMigrationWikiSeed>();
        for (var i = 0; i < wikiSeeds.Count; i++)
        {
            var seed = wikiSeeds[i];
            if (seed is null || string.IsNullOrWhiteSpace(seed.Section))
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} names no section; seeds land in the six wiki sections.");
            }

            if (!KbCitations.WikiSections.Contains(seed.Section, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} names non-wiki section '{seed.Section}'. " +
                    $"Wiki sections: {string.Join(", ", KbCitations.WikiSections)}.");
            }

            if (seed.Entry is null)
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} ({seed.Section}) carries no entry.");
            }

            if (string.IsNullOrWhiteSpace(seed.Entry.Id))
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} ({seed.Section}) carries an empty entry id.");
            }

            if (string.IsNullOrWhiteSpace(seed.Entry.Title))
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} ({seed.Section}#{seed.Entry.Id}) carries an empty title.");
            }

            if (string.IsNullOrWhiteSpace(seed.Entry.Body))
            {
                throw new InvalidOperationException(
                    $"KB migration wiki seed {i} ({seed.Section}#{seed.Entry.Id}) carries an empty body.");
            }

            seeds.Add(seed);
        }

        var duplicates = seeds
            .GroupBy(s => $"{s.Section}#{s.Entry.Id}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"KB migration seeds {string.Join(", ", duplicates)} more than once; one seed per entry.");
        }

        return seeds;
    }

    /// <summary>Writes the publication into a directory, creating the new
    /// file. Refuses to overwrite: a new KB version never edits an
    /// existing file in place, and neither does its writer.</summary>
    public static string WriteToDirectory(KbPublication publication, string directory)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, publication.FileName);
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Refusing to publish over existing KB file '{path}'; publications create new versions, never overwrite.");
        }

        File.WriteAllText(path, publication.Json);
        return path;
    }

    private sealed record ParsedChange(KbEntryChange Change, Action<KbMutableFile, KbStamps> Apply);

    private static ParsedChange ParseChange(KbEntryChange change, int index)
    {
        if (change is null)
        {
            throw new InvalidOperationException($"KB change {index} is null; changes must be complete records.");
        }

        if (!KbCitations.KnownSections.Contains(change.Section, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"KB change {index} names unknown section '{change.Section}'. " +
                $"Known sections: {string.Join(", ", KbCitations.KnownSections)}.");
        }

        if (string.IsNullOrWhiteSpace(change.EntryId))
        {
            throw new InvalidOperationException(
                $"KB change {index} in section '{change.Section}' carries an empty entry id.");
        }

        if (string.IsNullOrWhiteSpace(change.ChangedBy))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) names no author; every change records who made it.");
        }

        if (change.ChangedAt == default)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries no change timestamp.");
        }

        if (string.IsNullOrWhiteSpace(change.EntryJson))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries no entry JSON; " +
                "add and edit both ship the full entry as authored.");
        }

        if (CompanyKbSpaces.IsWikiSection(change.Section))
        {
            return ParseEntry<KbWikiEntry>(change, index, w => w.Id, RequireWiki);
        }

        return change.Section switch
        {
            KbCitations.LegalEntities => ParseEntry<KbLegalEntity>(
                change, index, e => e.Id, RequireLegalEntity),
            KbCitations.SitesWarehouses => ParseEntry<KbSite>(
                change, index, s => s.Id, RequireSite),
            KbCitations.Items => ParseEntry<KbItem>(
                change, index, i => i.Id, RequireItem),
            KbCitations.SourcingPolicies => ParseEntry<KbSourcingPolicy>(
                change, index, p => p.Id, RequirePolicy),
            KbCitations.RegionRules => ParseEntry<KbRegionRule>(
                change, index, r => r.Id, RequireRegion),
            KbCitations.Documents => ParseEntry<KbDocument>(
                change, index, d => d.Id, RequireDocument),
            KbCitations.ApprovalMatrix => ParseEntry<KbApprovalEntry>(
                change, index, a => a.Action, RequireMatrixRow),
            KbCitations.FiscalCalendar => ParseFiscal(change, index),
            _ => throw new InvalidOperationException(
                $"KB change {index} names unknown section '{change.Section}'."),
        };
    }

    private static ParsedChange ParseEntry<TEntry>(
        KbEntryChange change,
        int index,
        Func<TEntry, string?> idOf,
        Action<TEntry, KbEntryChange, int> require)
        where TEntry : class
    {
        var entry = DeserializeEntry<TEntry>(change, index);
        if (!string.Equals(idOf(entry), change.EntryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) ships entry id '{idOf(entry)}'; " +
                "the authored entry id must match the change entry id.");
        }

        require(entry, change, index);
        return new ParsedChange(change, (file, stamps) =>
            file.ApplyList(change, entry, stamps));
    }

    private static ParsedChange ParseFiscal(KbEntryChange change, int index)
    {
        if (!string.Equals(change.EntryId, KbCitations.FiscalCalendarCurrent, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) refuses: the fiscal calendar is a " +
                $"singleton addressed as '{KbCitations.FiscalCalendarCurrent}'.");
        }

        var entry = DeserializeEntry<KbFiscalCalendar>(change, index);
        if (string.IsNullOrWhiteSpace(entry.YearEnd))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty year end.");
        }

        if (string.IsNullOrWhiteSpace(entry.CurrentFy))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty current fiscal year.");
        }

        if (entry.Periods is null)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries no periods list.");
        }

        return new ParsedChange(change, (file, stamps) =>
            file.ApplyFiscal(change, entry, stamps));
    }

    private static TEntry DeserializeEntry<TEntry>(KbEntryChange change, int index)
        where TEntry : class
    {
        TEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<TEntry>(change.EntryJson, EntryOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) entry JSON is not a " +
                $"well-formed {SectionEntryName(change.Section)}: {ex.Message}", ex);
        }

        if (entry is null)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) entry JSON deserialized to null.");
        }

        return entry;
    }

    private static string SectionEntryName(string section)
    {
        if (CompanyKbSpaces.IsWikiSection(section))
        {
            return "wiki entry";
        }

        return section switch
        {
            KbCitations.LegalEntities => "legal entity",
            KbCitations.SitesWarehouses => "site or warehouse",
            KbCitations.FiscalCalendar => "fiscal calendar",
            KbCitations.ApprovalMatrix => "approval-matrix row",
            KbCitations.Items => "item",
            KbCitations.SourcingPolicies => "sourcing policy",
            KbCitations.RegionRules => "region rule",
            KbCitations.Documents => "document",
            _ => "KB entry",
        };
    }

    private static void RequireLegalEntity(KbLegalEntity entry, KbEntryChange change, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty name.");
        }

        if (string.IsNullOrWhiteSpace(entry.Country))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty country.");
        }

        if (string.IsNullOrWhiteSpace(entry.Currency))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty currency.");
        }
    }

    private static void RequireSite(KbSite entry, KbEntryChange change, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.EntityId))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty legal entity ref.");
        }

        if (string.IsNullOrWhiteSpace(entry.Region))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty region ref.");
        }

        if (entry.Capabilities is null)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries no capabilities list.");
        }
    }

    private static void RequireItem(KbItem entry, KbEntryChange change, int index)
    {
        _ = entry;
        _ = change;
        _ = index;
    }

    private static void RequirePolicy(KbSourcingPolicy entry, KbEntryChange change, int index)
    {
        if (entry.Params is null)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries no params map.");
        }
    }

    private static void RequireRegion(KbRegionRule entry, KbEntryChange change, int index)
    {
        if (entry.Countries is null || entry.Countries.Count == 0)
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) names no countries; " +
                "region boundaries are explicit data.");
        }
    }

    private static void RequireDocument(KbDocument entry, KbEntryChange change, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.Kind))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty kind.");
        }
    }

    private static void RequireMatrixRow(KbApprovalEntry entry, KbEntryChange change, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.OwnerRole))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty owner role.");
        }

        if (string.IsNullOrWhiteSpace(entry.Owner))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty owner.");
        }
    }

    private static void RequireWiki(KbWikiEntry entry, KbEntryChange change, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty title.");
        }

        if (string.IsNullOrWhiteSpace(entry.Body))
        {
            throw new InvalidOperationException(
                $"KB change {index} ({change.Section}#{change.EntryId}) carries an empty body.");
        }
    }

    private static string? FirstAuditedEntry(CompanyKbFile file)
    {
        static bool Audited(
            string? createdBy, DateTimeOffset? createdAt,
            string? modifiedBy, DateTimeOffset? modifiedAt,
            string? approvedBy, string? owner,
            KbEntryStatus? status, DateOnly? effectiveDate) =>
            !string.IsNullOrWhiteSpace(createdBy) || createdAt.HasValue
            || !string.IsNullOrWhiteSpace(modifiedBy) || modifiedAt.HasValue
            || !string.IsNullOrWhiteSpace(approvedBy) || !string.IsNullOrWhiteSpace(owner)
            || status.HasValue || effectiveDate.HasValue;

        foreach (var e in file.LegalEntities)
        {
            if (Audited(e.CreatedBy, e.CreatedAt, e.ModifiedBy, e.ModifiedAt, e.ApprovedBy,
                e.Owner, e.Status, e.EffectiveDate))
            {
                return $"{KbCitations.LegalEntities}#{e.Id}";
            }
        }

        foreach (var s in file.SitesWarehouses)
        {
            if (Audited(s.CreatedBy, s.CreatedAt, s.ModifiedBy, s.ModifiedAt, s.ApprovedBy,
                s.Owner, s.Status, s.EffectiveDate))
            {
                return $"{KbCitations.SitesWarehouses}#{s.Id}";
            }
        }

        if (file.FiscalCalendar is not null && Audited(
            file.FiscalCalendar.CreatedBy, file.FiscalCalendar.CreatedAt,
            file.FiscalCalendar.ModifiedBy, file.FiscalCalendar.ModifiedAt,
            file.FiscalCalendar.ApprovedBy, file.FiscalCalendar.Owner,
            file.FiscalCalendar.Status, file.FiscalCalendar.EffectiveDate))
        {
            return $"{KbCitations.FiscalCalendar}#{KbCitations.FiscalCalendarCurrent}";
        }

        foreach (var a in file.ApprovalMatrix)
        {
            if (Audited(a.CreatedBy, a.CreatedAt, a.ModifiedBy, a.ModifiedAt, a.ApprovedBy,
                null, a.Status, a.EffectiveDate))
            {
                return $"{KbCitations.ApprovalMatrix}#{a.Action}";
            }
        }

        foreach (var i in file.Items)
        {
            if (Audited(i.CreatedBy, i.CreatedAt, i.ModifiedBy, i.ModifiedAt, i.ApprovedBy,
                i.Owner, i.Status, i.EffectiveDate))
            {
                return $"{KbCitations.Items}#{i.Id}";
            }
        }

        foreach (var p in file.SourcingPolicies)
        {
            if (Audited(p.CreatedBy, p.CreatedAt, p.ModifiedBy, p.ModifiedAt, p.ApprovedBy,
                p.Owner, p.Status, p.EffectiveDate))
            {
                return $"{KbCitations.SourcingPolicies}#{p.Id}";
            }
        }

        foreach (var r in file.RegionRules)
        {
            if (Audited(r.CreatedBy, r.CreatedAt, r.ModifiedBy, r.ModifiedAt, r.ApprovedBy,
                r.Owner, r.Status, r.EffectiveDate))
            {
                return $"{KbCitations.RegionRules}#{r.Id}";
            }
        }

        foreach (var d in file.Documents)
        {
            if (Audited(d.CreatedBy, d.CreatedAt, d.ModifiedBy, d.ModifiedAt, d.ApprovedBy,
                d.Owner, d.Status, d.EffectiveDate))
            {
                return $"{KbCitations.Documents}#{d.Id}";
            }
        }

        return FirstAuditedWiki(file);
    }

    private static string? FirstAuditedWiki(CompanyKbFile file)
    {
        static bool Audited(KbWikiEntry w) =>
            !string.IsNullOrWhiteSpace(w.CreatedBy) || w.CreatedAt.HasValue
            || !string.IsNullOrWhiteSpace(w.ModifiedBy) || w.ModifiedAt.HasValue
            || !string.IsNullOrWhiteSpace(w.ApprovedBy) || !string.IsNullOrWhiteSpace(w.Owner)
            || w.Status.HasValue || w.EffectiveDate.HasValue;

        string? Check(IReadOnlyList<KbWikiEntry>? entries, string section)
        {
            foreach (var w in entries ?? [])
            {
                if (Audited(w))
                {
                    return $"{section}#{w.Id}";
                }
            }

            return null;
        }

        return Check(file.Leadership, KbCitations.WikiLeadership)
            ?? Check(file.Finance, KbCitations.WikiFinance)
            ?? Check(file.Procurement, KbCitations.WikiProcurement)
            ?? Check(file.Hr, KbCitations.WikiHr)
            ?? Check(file.Regulatory, KbCitations.WikiRegulatory)
            ?? Check(file.Operations, KbCitations.WikiOperations);
    }

    /// <summary>First entry missing any stamp: version, audit, owner,
    /// status, or effective date. Edit publishes refuse pre-audit bases;
    /// only the migration backfills them. Matrix rows read their owner
    /// from the named person (the record carries no separate entry
    /// owner).</summary>
    private static string? FirstUnauditedEntry(CompanyKbFile file)
    {
        static bool Stamped(
            string? version,
            string? createdBy, DateTimeOffset? createdAt,
            string? modifiedBy, DateTimeOffset? modifiedAt,
            string? approvedBy, string? owner,
            KbEntryStatus? status, DateOnly? effectiveDate) =>
            !string.IsNullOrWhiteSpace(version)
            && !string.IsNullOrWhiteSpace(createdBy) && createdAt.HasValue && createdAt.Value != default
            && !string.IsNullOrWhiteSpace(modifiedBy) && modifiedAt.HasValue && modifiedAt.Value != default
            && !string.IsNullOrWhiteSpace(approvedBy) && !string.IsNullOrWhiteSpace(owner)
            && status.HasValue && effectiveDate.HasValue && effectiveDate.Value != default;

        foreach (var e in file.LegalEntities)
        {
            if (!Stamped(e.Version, e.CreatedBy, e.CreatedAt, e.ModifiedBy, e.ModifiedAt,
                e.ApprovedBy, e.Owner, e.Status, e.EffectiveDate))
            {
                return $"{KbCitations.LegalEntities}#{e.Id}";
            }
        }

        foreach (var s in file.SitesWarehouses)
        {
            if (!Stamped(s.Version, s.CreatedBy, s.CreatedAt, s.ModifiedBy, s.ModifiedAt,
                s.ApprovedBy, s.Owner, s.Status, s.EffectiveDate))
            {
                return $"{KbCitations.SitesWarehouses}#{s.Id}";
            }
        }

        if (file.FiscalCalendar is not null && !Stamped(
            file.FiscalCalendar.Version, file.FiscalCalendar.CreatedBy, file.FiscalCalendar.CreatedAt,
            file.FiscalCalendar.ModifiedBy, file.FiscalCalendar.ModifiedAt,
            file.FiscalCalendar.ApprovedBy, file.FiscalCalendar.Owner,
            file.FiscalCalendar.Status, file.FiscalCalendar.EffectiveDate))
        {
            return $"{KbCitations.FiscalCalendar}#{KbCitations.FiscalCalendarCurrent}";
        }

        foreach (var a in file.ApprovalMatrix)
        {
            if (!Stamped(a.Version, a.CreatedBy, a.CreatedAt, a.ModifiedBy, a.ModifiedAt,
                a.ApprovedBy, a.Owner, a.Status, a.EffectiveDate))
            {
                return $"{KbCitations.ApprovalMatrix}#{a.Action}";
            }
        }

        foreach (var i in file.Items)
        {
            if (!Stamped(i.Version, i.CreatedBy, i.CreatedAt, i.ModifiedBy, i.ModifiedAt,
                i.ApprovedBy, i.Owner, i.Status, i.EffectiveDate))
            {
                return $"{KbCitations.Items}#{i.Id}";
            }
        }

        foreach (var p in file.SourcingPolicies)
        {
            if (!Stamped(p.Version, p.CreatedBy, p.CreatedAt, p.ModifiedBy, p.ModifiedAt,
                p.ApprovedBy, p.Owner, p.Status, p.EffectiveDate))
            {
                return $"{KbCitations.SourcingPolicies}#{p.Id}";
            }
        }

        foreach (var r in file.RegionRules)
        {
            if (!Stamped(r.Version, r.CreatedBy, r.CreatedAt, r.ModifiedBy, r.ModifiedAt,
                r.ApprovedBy, r.Owner, r.Status, r.EffectiveDate))
            {
                return $"{KbCitations.RegionRules}#{r.Id}";
            }
        }

        foreach (var d in file.Documents)
        {
            if (!Stamped(d.Version, d.CreatedBy, d.CreatedAt, d.ModifiedBy, d.ModifiedAt,
                d.ApprovedBy, d.Owner, d.Status, d.EffectiveDate))
            {
                return $"{KbCitations.Documents}#{d.Id}";
            }
        }

        string? CheckWiki(IReadOnlyList<KbWikiEntry>? entries, string section)
        {
            foreach (var w in entries ?? [])
            {
                if (!Stamped(w.Version, w.CreatedBy, w.CreatedAt, w.ModifiedBy, w.ModifiedAt,
                    w.ApprovedBy, w.Owner, w.Status, w.EffectiveDate))
                {
                    return $"{section}#{w.Id}";
                }
            }

            return null;
        }

        return CheckWiki(file.Leadership, KbCitations.WikiLeadership)
            ?? CheckWiki(file.Finance, KbCitations.WikiFinance)
            ?? CheckWiki(file.Procurement, KbCitations.WikiProcurement)
            ?? CheckWiki(file.Hr, KbCitations.WikiHr)
            ?? CheckWiki(file.Regulatory, KbCitations.WikiRegulatory)
            ?? CheckWiki(file.Operations, KbCitations.WikiOperations);
    }

    /// <summary>Copies params into ordinal key order so touched entries
    /// serialize byte-identical on replay regardless of authored key order.</summary>
    internal static SortedDictionary<string, string> SortedParams(IReadOnlyDictionary<string, string> paramsMap)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in paramsMap)
        {
            sorted[key] = value;
        }

        return sorted;
    }

    /// <summary>Mutable working copy of the citable sections. Applies one
    /// parsed change at a time: adds append (refusing collision), edits
    /// replace in place (refusing unknown entries). The writer stamps
    /// audit plus owner, status, and effective date on every touched
    /// entry itself; authored stamps are replaced, never trusted.</summary>
    private sealed class KbMutableFile
    {
        private readonly List<KbLegalEntity> _entities;
        private readonly List<KbSite> _sites;
        private KbFiscalCalendar? _fiscal;
        private readonly List<KbApprovalEntry> _matrix;
        private readonly List<KbItem> _items;
        private readonly List<KbSourcingPolicy> _policies;
        private readonly List<KbRegionRule> _regions;
        private readonly List<KbDocument> _documents;
        private readonly List<KbWikiEntry> _leadership;
        private readonly List<KbWikiEntry> _finance;
        private readonly List<KbWikiEntry> _procurement;
        private readonly List<KbWikiEntry> _hr;
        private readonly List<KbWikiEntry> _regulatory;
        private readonly List<KbWikiEntry> _operations;
        private readonly CompanyKbFile _base;

        internal KbMutableFile(CompanyKbFile baseFile)
        {
            _base = baseFile;
            _entities = baseFile.LegalEntities.ToList();
            _sites = baseFile.SitesWarehouses.ToList();
            _fiscal = baseFile.FiscalCalendar;
            _matrix = baseFile.ApprovalMatrix.ToList();
            _items = baseFile.Items.ToList();
            _policies = baseFile.SourcingPolicies.ToList();
            _regions = baseFile.RegionRules.ToList();
            _documents = baseFile.Documents.ToList();
            _leadership = (baseFile.Leadership ?? []).ToList();
            _finance = (baseFile.Finance ?? []).ToList();
            _procurement = (baseFile.Procurement ?? []).ToList();
            _hr = (baseFile.Hr ?? []).ToList();
            _regulatory = (baseFile.Regulatory ?? []).ToList();
            _operations = (baseFile.Operations ?? []).ToList();
        }

        internal CompanyKbFile ToFile(string newVersion, string? comment) => _base with
        {
            Version = newVersion,
            Comment = comment,
            LegalEntities = _entities,
            SitesWarehouses = _sites,
            FiscalCalendar = _fiscal,
            ApprovalMatrix = _matrix,
            Items = _items,
            SourcingPolicies = _policies,
            RegionRules = _regions,
            Documents = _documents,
            Leadership = _leadership,
            Finance = _finance,
            Procurement = _procurement,
            Hr = _hr,
            Regulatory = _regulatory,
            Operations = _operations,
        };

        internal void ApplyList<TEntry>(
            KbEntryChange change, TEntry authored, KbStamps stamps)
            where TEntry : class
        {
            switch (authored)
            {
                case KbLegalEntity e:
                    ApplyToList(_entities, e.Id, change, prior =>
                        StampEntity(prior, e, change, stamps));
                    break;
                case KbSite s:
                    ApplyToList(_sites, s.Id, change, prior =>
                        StampSite(prior, s, change, stamps));
                    break;
                case KbApprovalEntry a:
                    ApplyToList(_matrix, a.Action, change, prior =>
                        StampMatrix(prior, a, change, stamps));
                    break;
                case KbItem i:
                    ApplyToList(_items, i.Id, change, prior =>
                        StampItem(prior, i, change, stamps));
                    break;
                case KbSourcingPolicy p:
                    ApplyToList(_policies, p.Id, change, prior =>
                        StampPolicy(prior, p, change, stamps));
                    break;
                case KbRegionRule r:
                    ApplyToList(_regions, r.Id, change, prior =>
                        StampRegion(prior, r, change, stamps));
                    break;
                case KbDocument d:
                    ApplyToList(_documents, d.Id, change, prior =>
                        StampDocument(prior, d, change, stamps));
                    break;
                case KbWikiEntry w:
                    ApplyToList(WikiListFor(change.Section), w.Id, change, prior =>
                        StampWiki(prior, w, change, stamps));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"KB change ({change.Section}#{change.EntryId}) ships an unsupported entry type.");
            }
        }

        private List<KbWikiEntry> WikiListFor(string section) => section switch
        {
            KbCitations.WikiLeadership => _leadership,
            KbCitations.WikiFinance => _finance,
            KbCitations.WikiProcurement => _procurement,
            KbCitations.WikiHr => _hr,
            KbCitations.WikiRegulatory => _regulatory,
            KbCitations.WikiOperations => _operations,
            _ => throw new InvalidOperationException(
                $"KB section '{section}' is not a wiki section; the parser routes wiki entries by section."),
        };

        internal void ApplyFiscal(
            KbEntryChange change, KbFiscalCalendar authored, KbStamps stamps)
        {
            switch (change.Kind)
            {
                case KbChangeKind.Add:
                    if (_fiscal is not null)
                    {
                        throw new InvalidOperationException(
                            $"KB add {change.Section}#{change.EntryId} refuses: the fiscal calendar is already " +
                            "seeded; seed once, then edit.");
                    }

                    _fiscal = authored with
                    {
                        Version = stamps.NewVersion,
                        CreatedBy = change.ChangedBy,
                        CreatedAt = change.ChangedAt,
                        ModifiedBy = change.ChangedBy,
                        ModifiedAt = change.ChangedAt,
                        ApprovedBy = stamps.ApprovedBy,
                        Owner = stamps.Owner,
                        Status = KbEntryStatus.Active,
                        EffectiveDate = stamps.EffectiveDate,
                    };
                    break;
                case KbChangeKind.Edit:
                    if (_fiscal is null)
                    {
                        throw new InvalidOperationException(
                            $"KB edit {change.Section}#{change.EntryId} refuses: no fiscal calendar is seeded yet; " +
                            "add it first.");
                    }

                    _fiscal = authored with
                    {
                        Version = stamps.NewVersion,
                        CreatedBy = _fiscal.CreatedBy ?? change.ChangedBy,
                        CreatedAt = _fiscal.CreatedAt ?? change.ChangedAt,
                        ModifiedBy = change.ChangedBy,
                        ModifiedAt = change.ChangedAt,
                        ApprovedBy = stamps.ApprovedBy,
                        Owner = stamps.Owner,
                        Status = _fiscal.Status ?? KbEntryStatus.Active,
                        EffectiveDate = stamps.EffectiveDate,
                    };
                    break;
                default:
                    throw new InvalidOperationException(
                        $"KB change ({change.Section}#{change.EntryId}) carries unknown kind '{change.Kind}'; " +
                        "no delete in v1: entries retire by superseding versions, never by removal.");
            }
        }

        private void ApplyToList<TEntry>(
            List<TEntry> list,
            string entryId,
            KbEntryChange change,
            Func<TEntry?, TEntry> stamp)
            where TEntry : class
        {
            var index = list.FindIndex(e => EntryIdOf(e) == entryId);
            switch (change.Kind)
            {
                case KbChangeKind.Add:
                    if (index >= 0)
                    {
                        throw new InvalidOperationException(
                            $"KB add {change.Section}#{change.EntryId} refuses: the entry already exists; " +
                            "add refuses collision, edit replaces.");
                    }

                    list.Add(stamp(null));
                    break;
                case KbChangeKind.Edit:
                    if (index < 0)
                    {
                        throw new InvalidOperationException(
                            $"KB edit {change.Section}#{change.EntryId} refuses: no such entry; " +
                            "edit refuses unknown entries, add creates.");
                    }

                    list[index] = stamp(list[index]);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"KB change ({change.Section}#{change.EntryId}) carries unknown kind '{change.Kind}'; " +
                        "no delete in v1: entries retire by superseding versions, never by removal.");
            }
        }

        private static string EntryIdOf<TEntry>(TEntry entry) => entry switch
        {
            KbLegalEntity e => e.Id,
            KbSite s => s.Id,
            KbApprovalEntry a => a.Action,
            KbItem i => i.Id,
            KbSourcingPolicy p => p.Id,
            KbRegionRule r => r.Id,
            KbDocument d => d.Id,
            KbWikiEntry w => w.Id,
            _ => throw new InvalidOperationException("Unsupported KB entry type."),
        };

        private static KbLegalEntity StampEntity(
            KbLegalEntity? prior, KbLegalEntity authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbSite StampSite(
            KbSite? prior, KbSite authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbApprovalEntry StampMatrix(
            KbApprovalEntry? prior, KbApprovalEntry authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbItem StampItem(
            KbItem? prior, KbItem authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbSourcingPolicy StampPolicy(
            KbSourcingPolicy? prior, KbSourcingPolicy authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Params = SortedParams(authored.Params),
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbRegionRule StampRegion(
            KbRegionRule? prior, KbRegionRule authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbDocument StampDocument(
            KbDocument? prior, KbDocument authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };

        private static KbWikiEntry StampWiki(
            KbWikiEntry? prior, KbWikiEntry authored, KbEntryChange change,
            KbStamps stamps) => authored with
            {
                Version = stamps.NewVersion,
                CreatedBy = prior?.CreatedBy ?? change.ChangedBy,
                CreatedAt = prior?.CreatedAt ?? change.ChangedAt,
                ModifiedBy = change.ChangedBy,
                ModifiedAt = change.ChangedAt,
                ApprovedBy = stamps.ApprovedBy,
                Owner = stamps.Owner,
                Status = prior?.Status ?? KbEntryStatus.Active,
                EffectiveDate = stamps.EffectiveDate,
            };
    }
}
