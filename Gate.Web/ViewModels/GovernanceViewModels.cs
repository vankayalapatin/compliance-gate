// View models for the Governance area: sidebar modules, route helpers, rules
// board rows, KB entry shapes, used-by derivations, and exception rows.
// Display shapes plus pure derivations only; every domain value (spaces,
// owners, versions, rule ids, counts) arrives at runtime from the engine
// seam, the case cache, or the memo files. No business values in code.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

/// <summary>Governance sidebar: two modules mirroring ErpSidebar. Compliance
// links are static route vocabulary (like ErpModules slugs); Knowledge
// spaces arrive at runtime from the engine seam and are rendered from the
// sidebar's Spaces parameter, never from code.</summary>
public static class GovernanceModules
{
    public const string Compliance = "Compliance";

    public const string Knowledge = "Knowledge";

    public const string RulesSlug = "rules";

    public const string MemosSlug = "memos";

    public const string ExceptionsSlug = "exceptions";

    public const string VersionsSlug = "versions";

    public sealed record ListLink(string Slug, string Title);

    public sealed record Module(string Name, IReadOnlyList<ListLink> Lists);

    public static readonly IReadOnlyList<ListLink> ComplianceLinks =
    [
        new(RulesSlug, "Rules"),
        new(MemosSlug, "Memos"),
        new(ExceptionsSlug, "Exceptions"),
    ];

    public static string? ModuleFor(string key) =>
        ComplianceLinks.Any(l => string.Equals(l.Slug, key, StringComparison.Ordinal))
            ? Compliance
            : key.StartsWith("kb:", StringComparison.Ordinal)
                || string.Equals(key, VersionsSlug, StringComparison.Ordinal)
                ? Knowledge
                : null;

    public static string TitleFor(string key) =>
        ComplianceLinks.FirstOrDefault(l =>
            string.Equals(l.Slug, key, StringComparison.Ordinal))?.Title
        ?? (key.StartsWith("kb:", StringComparison.Ordinal)
            ? $"KB space {key["kb:".Length..]}"
            : key);

    public static bool IsKnownCompliance(string slug) =>
        ComplianceLinks.Any(l => string.Equals(l.Slug, slug, StringComparison.Ordinal));
}

/// <summary>Governance area routes. Citation links always pin the proposal's
// KB version: /governance/kb/{version}/{section}/{entryId}.</summary>
public static class GovernanceRoutes
{
    public const string Root = "governance";

    public static string RulesHref => $"{Root}/{GovernanceModules.RulesSlug}";

    public static string MemosHref => $"{Root}/{GovernanceModules.MemosSlug}";

    public static string ExceptionsHref => $"{Root}/{GovernanceModules.ExceptionsSlug}";

    public static string VersionsHref => $"{Root}/{GovernanceModules.VersionsSlug}";

    public static string KbSpaceHref(string space) =>
        $"{Root}/kb/{Uri.EscapeDataString(space)}";

    public static string KbEntryHref(string version, string section, string entryId) =>
        $"{Root}/kb/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(section)}/{Uri.EscapeDataString(entryId)}";

    public static string CitationHref(KbCitation citation)
    {
        ArgumentNullException.ThrowIfNull(citation);
        return KbEntryHref(citation.KbVersion, citation.Section, citation.EntryId);
    }
}

/// <summary>Fixed Governance phrases. Every id, version, and count arrives
// at runtime.</summary>
public static class GovernanceLabels
{
    public const string BlocksThisRun = "Blocks this run";

    public const string NoGroundedEntries = "No grounded entries.";

    public const string ShippedRuleGrounding = "Shipped rule; no proposal citations.";

    public const string DockNoCoverage =
        "No dock evaluations recorded; dock-gate counts are zero.";

    public static string DockCoverage(int files) =>
        $"Dock evaluated in {files} case(s).";

    public static string ScanLabel(int files) =>
        $"all cases ({files} scanned)";

    public static string NeedsReviewWhy(string proposalKb, string currentKb) =>
        $"Proposal pins KB {KbDisplay.Version(proposalKb)}; current is {KbDisplay.Version(currentKb)}. Re-ground before relying on it.";
}

/// <summary>KB versions for display. Stored versions carry the kbvN file
/// tag; surfaces show "version N" and keep the tag for routes only.</summary>
public static class KbDisplay
{
    public static string Version(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version)
            && version!.StartsWith("kbv", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(version[3..], out var n))
        {
            return $"version {n}";
        }

        return string.IsNullOrWhiteSpace(version) ? "—" : version.Trim();
    }

    /// <summary>Activation ids for display: the embedded kbvN file tag
    /// shows as "version N". Ids stay raw in routes and keys.</summary>
    public static string Activation(string? activationId)
    {
        if (string.IsNullOrWhiteSpace(activationId))
        {
            return "—";
        }

        var segments = activationId!.Split('-');
        if (segments.Length >= 2
            && string.Equals(segments[0], "KB", StringComparison.OrdinalIgnoreCase))
        {
            segments[1] = Version(segments[1]);
        }

        return string.Join("-", segments);
    }
}

/// <summary>Pure case-cache block scan. Counts timeline events that carry a
// rule id at vendor, dock, and pre-pay gates. Vendor failures timeline as
// VendorScreened (failed) plus VendorBlocked; dock holds timeline as
// HoldSet, ReviewOpened, or agent-side Note with the rule id; pre-pay
// blocks timeline as Note with the rule id. Resolutions (HoldReleased,
// ReviewClosed) are never blocks and are excluded.</summary>
public static class RuleBlockScan
{
    public static readonly IReadOnlySet<CaseEventKind> BlockKinds =
        new HashSet<CaseEventKind>
        {
            CaseEventKind.VendorScreened,
            CaseEventKind.VendorBlocked,
            CaseEventKind.DockEvaluated,
            CaseEventKind.HoldSet,
            CaseEventKind.ReviewOpened,
            CaseEventKind.Note,
        };

    public sealed record BlockScanResult(
        IReadOnlyDictionary<string, int> Counts,
        int FilesScanned,
        int DockEvaluatedFiles,
        string DockNote);

    public static BlockScanResult CountBlocks(IEnumerable<CaseFile> files)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;
        var dockFiles = 0;
        foreach (var file in files)
        {
            scanned++;
            var docked = false;
            foreach (var e in file.Events)
            {
                if (e.Kind == CaseEventKind.DockEvaluated)
                {
                    docked = true;
                }

                if (e.RuleId is null || !BlockKinds.Contains(e.Kind))
                {
                    continue;
                }

                counts.TryGetValue(e.RuleId, out var seen);
                counts[e.RuleId] = seen + 1;
            }

            if (docked)
            {
                dockFiles++;
            }
        }

        return new BlockScanResult(
            counts, scanned, dockFiles,
            dockFiles == 0 ? GovernanceLabels.DockNoCoverage : GovernanceLabels.DockCoverage(dockFiles));
    }
}

/// <summary>One Rules-view row: a live pack rule (regulatory) or a published
// company rule (activation). Owner, version, and date are pack-level for
// regulatory rows and activation-level for company rows.</summary>
public sealed record GovernanceRuleRow(
    string RuleId,
    string PackId,
    string PackVersion,
    string Owner,
    string EffectiveOrAdopted,
    string Source,
    int BlockCount,
    IReadOnlyList<KbCitation> GroundedEntries,
    bool NeedsReview,
    string? NeedsReviewReason,
    bool IsCompanyRule,
    string? MemoId,
    string? KbVersion);

/// <summary>Pure Rules-board builder. Live rules come from the seam with
// their pack owner, adoption date, and public source; company rows come
// from memo-file activations joined to their proposals by proposal hash
// (published rules show the approval effective date), block counts from
// the case-cache scan. A live rule without a source refuses loudly: the
// pack loader guarantees one, so a miss is an engine bug, never a guess.</summary>
public static class GovernanceRuleBoard
{
    public static IReadOnlyList<GovernanceRuleRow> BuildRows(
        IReadOnlyList<LivePackRule> liveRules,
        IReadOnlyList<MemoFile> memoFiles,
        IReadOnlyDictionary<string, int> blockCounts,
        string currentKbVersion)
    {
        ArgumentNullException.ThrowIfNull(liveRules);
        var rows = new List<GovernanceRuleRow>();

        foreach (var rule in liveRules.OrderBy(r => r.RuleId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(rule.Source))
            {
                throw new InvalidOperationException(
                    $"Live rule '{rule.RuleId}' in pack '{rule.PackId}' {rule.PackVersion} carries no source; " +
                    "every pack rule records its public source.");
            }

            blockCounts.TryGetValue(rule.RuleId, out var blocks);
            rows.Add(new GovernanceRuleRow(
                rule.RuleId, rule.PackId, rule.PackVersion, rule.Owner,
                rule.AdoptedDate.ToString("yyyy-MM-dd"),
                rule.Source,
                blocks, [], false, null, false, null, null));
        }

        var proposalsByHash = memoFiles
            .SelectMany(f => f.Proposals.Select(p => (File: f, Record: p)))
            .GroupBy(t => t.Record.Proposal.ProposalHash, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var packOwner = liveRules
            .GroupBy(r => r.PackId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Owner, StringComparer.Ordinal);

        foreach (var file in memoFiles.OrderBy(f => f.Memo.MemoId, StringComparer.Ordinal))
        {
            foreach (var activation in file.Activations
                .OrderBy(a => a.PublishedAt)
                .ThenBy(a => a.ActivationId, StringComparer.Ordinal))
            {
                proposalsByHash.TryGetValue(activation.ProposalHash, out var match);
                var citations = match.Record?.Proposal.Citations ?? [];
                var needsReview = KbVersionCompare.NeedsReview(activation.KbVersion, currentKbVersion);
                blockCounts.TryGetValue(activation.RuleId, out var blocks);
                packOwner.TryGetValue(activation.PackId, out var owner);
                rows.Add(new GovernanceRuleRow(
                    activation.RuleId, activation.PackId, activation.NewPackVersion,
                    owner ?? activation.Approver,
                    activation.EffectiveAt.ToString("yyyy-MM-dd"),
                    PolicyLabels.SourceLine(activation),
                    blocks, citations,
                    needsReview,
                    needsReview && activation.KbVersion is not null
                        ? GovernanceLabels.NeedsReviewWhy(activation.KbVersion, currentKbVersion)
                        : null,
                    true, activation.MemoId, activation.KbVersion));
            }
        }

        return rows;
    }
}

/// <summary>KB version staleness: a proposal (or activation pin) needs review
// when its KB version is older than the current one. Unparseable versions
// flag on any mismatch rather than silently passing.</summary>
public static class KbVersionCompare
{
    public static bool NeedsReview(string? proposalKb, string currentKb)
    {
        if (string.IsNullOrWhiteSpace(proposalKb) || string.IsNullOrWhiteSpace(currentKb))
        {
            return false;
        }

        if (string.Equals(proposalKb, currentKb, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return CompanyKbVersions.Compare(proposalKb, currentKb) < 0;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}

/// <summary>One KB entry for the space and entry views: identity, one-line
// summary, and the full audit stamps. All values arrive from the snapshot.</summary>
public sealed record KbEntryVm(
    string Section,
    string EntryId,
    string Title,
    string Summary,
    string? Detail,
    string? Owner,
    string? Status,
    string? Version,
    string? Effective,
    string? CreatedBy,
    string? CreatedAt,
    string? ModifiedBy,
    string? ModifiedAt,
    string? ApprovedBy);

/// <summary>Pure KB-entry projection: every citable section of a snapshot to
// entry rows. Summaries are one line; Detail carries the longer body (wiki
// markdown, sourcing params, region countries) for the entry view.</summary>
public static class KbEntries
{
    public static IReadOnlyList<KbEntryVm> FromSnapshot(KbSnapshot snapshot, string section)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        return section switch
        {
            KbCitations.LegalEntities => snapshot.LegalEntities
                .Select(e => new KbEntryVm(
                    section, e.Id, e.Name, $"{e.Country} · {e.Currency}", null,
                    e.Owner, e.Status?.ToString(), e.Version, DateLabel(e.EffectiveDate),
                    e.CreatedBy, AtLabel(e.CreatedAt), e.ModifiedBy, AtLabel(e.ModifiedAt), e.ApprovedBy))
                .ToList(),
            KbCitations.SitesWarehouses => snapshot.SitesWarehouses
                .Select(s => new KbEntryVm(
                    section, s.Id, s.Id, $"{s.EntityId} · {s.Region} · {string.Join(", ", s.Capabilities)}", null,
                    s.Owner, s.Status?.ToString(), s.Version, DateLabel(s.EffectiveDate),
                    s.CreatedBy, AtLabel(s.CreatedAt), s.ModifiedBy, AtLabel(s.ModifiedAt), s.ApprovedBy))
                .ToList(),
            KbCitations.FiscalCalendar => FiscalRow(snapshot, section),
            KbCitations.ApprovalMatrix => snapshot.ApprovalMatrix
                .Select(a => new KbEntryVm(
                    section, a.Action, a.Action, $"{a.OwnerRole} · {a.Owner}", null,
                    a.Owner, a.Status?.ToString(), a.Version, DateLabel(a.EffectiveDate),
                    a.CreatedBy, AtLabel(a.CreatedAt), a.ModifiedBy, AtLabel(a.ModifiedAt), a.ApprovedBy))
                .ToList(),
            KbCitations.Items => snapshot.Items
                .Select(i => new KbEntryVm(
                    section, i.Id, i.Id, i.Critical ? "critical" : "not critical", null,
                    i.Owner, i.Status?.ToString(), i.Version, DateLabel(i.EffectiveDate),
                    i.CreatedBy, AtLabel(i.CreatedAt), i.ModifiedBy, AtLabel(i.ModifiedAt), i.ApprovedBy))
                .ToList(),
            KbCitations.SourcingPolicies => snapshot.SourcingPolicies
                .Select(p => new KbEntryVm(
                    section, p.Id, p.Id,
                    string.Join("; ", p.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")),
                    null,
                    p.Owner, p.Status?.ToString(), p.Version, DateLabel(p.EffectiveDate),
                    p.CreatedBy, AtLabel(p.CreatedAt), p.ModifiedBy, AtLabel(p.ModifiedAt), p.ApprovedBy))
                .ToList(),
            KbCitations.RegionRules => snapshot.RegionRules
                .Select(r => new KbEntryVm(
                    section, r.Id, r.Id, string.Join(", ", r.Countries), null,
                    r.Owner, r.Status?.ToString(), r.Version, DateLabel(r.EffectiveDate),
                    r.CreatedBy, AtLabel(r.CreatedAt), r.ModifiedBy, AtLabel(r.ModifiedAt), r.ApprovedBy))
                .ToList(),
            KbCitations.Documents => snapshot.Documents
                .Select(d => new KbEntryVm(
                    section, d.Id, d.Id, $"{d.Kind} · p2pRelevant={d.P2pRelevant}", null,
                    d.Owner, d.Status?.ToString(), d.Version, DateLabel(d.EffectiveDate),
                    d.CreatedBy, AtLabel(d.CreatedAt), d.ModifiedBy, AtLabel(d.ModifiedAt), d.ApprovedBy))
                .ToList(),
            _ when string.Equals(section, KbCitations.WikiLeadership, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Leadership),
            _ when string.Equals(section, KbCitations.WikiFinance, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Finance),
            _ when string.Equals(section, KbCitations.WikiProcurement, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Procurement),
            _ when string.Equals(section, KbCitations.WikiHr, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Hr),
            _ when string.Equals(section, KbCitations.WikiRegulatory, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Regulatory),
            _ when string.Equals(section, KbCitations.WikiOperations, StringComparison.Ordinal) =>
                WikiRows(section, snapshot.Operations),
            _ => throw new InvalidOperationException(
                $"Unknown KB section '{section}'. Known sections: {string.Join(", ", KbCitations.KnownSections)}."),
        };
    }

    private static IReadOnlyList<KbEntryVm> FiscalRow(KbSnapshot snapshot, string section)
    {
        var fiscal = snapshot.FiscalCalendar;
        if (fiscal is null)
        {
            return [];
        }

        return [new KbEntryVm(
            section, KbCitations.FiscalCalendarCurrent, "Fiscal calendar",
            $"{fiscal.YearEnd} · {fiscal.CurrentFy}",
            string.Join(", ", fiscal.Periods),
            fiscal.Owner, fiscal.Status?.ToString(),
            fiscal.Version, DateLabel(fiscal.EffectiveDate),
            fiscal.CreatedBy, AtLabel(fiscal.CreatedAt),
            fiscal.ModifiedBy, AtLabel(fiscal.ModifiedAt),
            fiscal.ApprovedBy)];
    }

    private static IReadOnlyList<KbEntryVm> WikiRows(string section, IReadOnlyList<KbWikiEntry> entries) =>
        entries.Select(w => new KbEntryVm(
            section, w.Id, w.Title, FirstLine(w.Body), w.Body,
            w.Owner, w.Status?.ToString(), w.Version, DateLabel(w.EffectiveDate),
            w.CreatedBy, AtLabel(w.CreatedAt), w.ModifiedBy, AtLabel(w.ModifiedAt), w.ApprovedBy))
        .ToList();

    private static readonly System.Text.Json.JsonSerializerOptions WebOpts = new(
        System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Current entry JSON for the edit form: serializes the live
    // entry with web defaults (the writer parses case-insensitively).
    // Null when the section or entry is unknown.</summary>
    public static string? EntryJson(KbSnapshot snapshot, string section, string entryId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        object? entry = section switch
        {
            KbCitations.LegalEntities => snapshot.LegalEntities.FirstOrDefault(e =>
                string.Equals(e.Id, entryId, StringComparison.Ordinal)),
            KbCitations.SitesWarehouses => snapshot.SitesWarehouses.FirstOrDefault(s =>
                string.Equals(s.Id, entryId, StringComparison.Ordinal)),
            KbCitations.FiscalCalendar => string.Equals(entryId, KbCitations.FiscalCalendarCurrent, StringComparison.Ordinal)
                ? snapshot.FiscalCalendar : null,
            KbCitations.ApprovalMatrix => snapshot.ApprovalMatrix.FirstOrDefault(a =>
                string.Equals(a.Action, entryId, StringComparison.Ordinal)),
            KbCitations.Items => snapshot.Items.FirstOrDefault(i =>
                string.Equals(i.Id, entryId, StringComparison.Ordinal)),
            KbCitations.SourcingPolicies => snapshot.SourcingPolicies.FirstOrDefault(p =>
                string.Equals(p.Id, entryId, StringComparison.Ordinal)),
            KbCitations.RegionRules => snapshot.RegionRules.FirstOrDefault(r =>
                string.Equals(r.Id, entryId, StringComparison.Ordinal)),
            KbCitations.Documents => snapshot.Documents.FirstOrDefault(d =>
                string.Equals(d.Id, entryId, StringComparison.Ordinal)),
            _ => WikiEntry(snapshot, section, entryId),
        };
        return entry is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(entry, WebOpts);
    }

    private static KbWikiEntry? WikiEntry(KbSnapshot snapshot, string section, string entryId)
    {
        var list = section switch
        {
            _ when string.Equals(section, KbCitations.WikiLeadership, StringComparison.Ordinal) => snapshot.Leadership,
            _ when string.Equals(section, KbCitations.WikiFinance, StringComparison.Ordinal) => snapshot.Finance,
            _ when string.Equals(section, KbCitations.WikiProcurement, StringComparison.Ordinal) => snapshot.Procurement,
            _ when string.Equals(section, KbCitations.WikiHr, StringComparison.Ordinal) => snapshot.Hr,
            _ when string.Equals(section, KbCitations.WikiRegulatory, StringComparison.Ordinal) => snapshot.Regulatory,
            _ when string.Equals(section, KbCitations.WikiOperations, StringComparison.Ordinal) => snapshot.Operations,
            _ => null,
        };
        return list?.FirstOrDefault(w => string.Equals(w.Id, entryId, StringComparison.Ordinal));
    }

    private static string FirstLine(string body)
    {
        var line = (body ?? string.Empty).Split('\n', 2)[0].Trim().TrimEnd('\r');
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private static string? DateLabel(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    private static string? AtLabel(DateTimeOffset? at) => at?.ToString("u");
}

/// <summary>One used-by hit: a proposal citing an entry, or an activation
// published from a citing proposal. Versions are the citing proposal's pin,
// which may be older than the current KB.</summary>
public sealed record KbProposalUse(string MemoId, string ProposalId, string KbVersion);

public sealed record KbActivationUse(
    string MemoId, string ActivationId, string RuleId, string PackId, string NewPackVersion);

public sealed record KbUsedByVm(
    IReadOnlyList<KbProposalUse> Proposals,
    IReadOnlyList<KbActivationUse> Activations,
    IReadOnlyList<string> Rules);

/// <summary>Pure used-by index over memo files: every proposal citation and
// resolution plus every activation joined by proposal hash. Any citing
// version counts; the pin is shown so stale grounding stays visible.</summary>
public static class UsedByIndex
{
    public static KbUsedByVm FindUsedBy(
        string section, string entryId, IReadOnlyList<MemoFile> memoFiles)
    {
        var proposals = new List<KbProposalUse>();
        var citingHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in memoFiles)
        {
            foreach (var record in file.Proposals)
            {
                var proposal = record.Proposal;
                var cites = proposal.Citations
                    .Concat(proposal.Resolutions.Select(r => r.Citation))
                    .Any(c => string.Equals(c.Section, section, StringComparison.Ordinal)
                        && string.Equals(c.EntryId, entryId, StringComparison.Ordinal));
                if (cites)
                {
                    proposals.Add(new KbProposalUse(file.Memo.MemoId, proposal.ProposalId, proposal.KbVersion));
                    citingHashes.Add(proposal.ProposalHash);
                }
            }
        }

        var activations = new List<KbActivationUse>();
        foreach (var file in memoFiles)
        {
            foreach (var activation in file.Activations)
            {
                if (citingHashes.Contains(activation.ProposalHash))
                {
                    activations.Add(new KbActivationUse(
                        file.Memo.MemoId, activation.ActivationId, activation.RuleId,
                        activation.PackId, activation.NewPackVersion));
                }
            }
        }

        return new KbUsedByVm(
            proposals
                .OrderBy(p => p.MemoId, StringComparer.Ordinal)
                .ThenBy(p => p.ProposalId, StringComparer.Ordinal)
                .ToList(),
            activations
                .OrderBy(a => a.MemoId, StringComparer.Ordinal)
                .ThenBy(a => a.ActivationId, StringComparer.Ordinal)
                .ToList(),
            activations
                .Select(a => a.RuleId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList());
    }
}

/// <summary>One exception row: an ExceptionApproval decision card. Vendor
// travels in DocumentNumber, the blocking rule in RuleId, the grant reason
// in Comment on approval.</summary>
public sealed record ExceptionRowVm(
    string CaseId,
    string DecisionId,
    string? Vendor,
    string? RuleId,
    string RequestedTo,
    string? DecidedBy,
    string? DecidedAt,
    string Outcome,
    string? Reason,
    string? Comment);

/// <summary>Pure exception board: every ExceptionApproval card across cases,
// newest first.</summary>
public static class ExceptionBoard
{
    public static IReadOnlyList<ExceptionRowVm> Rows(IEnumerable<CaseFile> files) =>
        files
            .SelectMany(f => f.Decisions
                .Where(d => d.Kind == DecisionKind.ExceptionApproval)
                .Select(d => new ExceptionRowVm(
                    f.Case.CaseId, d.DecisionId, d.DocumentNumber, d.RuleId,
                    d.RequestedTo, d.DecidedBy, d.DecidedAt?.ToString("u"),
                    d.Status.ToString(), d.Reason, d.Comment)))
            .OrderByDescending(r => r.DecidedAt ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(r => r.DecisionId, StringComparer.Ordinal)
            .ToList();
}

/// <summary>KB approval resolution: the acting user approves as the space
// owner, directly or as their active dated delegate. Delegation bounds are
// inclusive, mirroring the ERP approval check.</summary>
public sealed record OwnerApproval(string Owner, string ActingApprover, string? ApprovedFor, bool IsDirect);

public static class GovernanceDelegation
{
    public static OwnerApproval? TryResolve(
        string owner, string actingUser, IReadOnlyList<DelegationDto> delegations, DateOnly today)
    {
        if (string.Equals(owner, actingUser, StringComparison.OrdinalIgnoreCase))
        {
            return new OwnerApproval(owner, actingUser, null, true);
        }

        var covering = delegations.Any(d =>
            string.Equals(d.FromUser, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.ToUser, actingUser, StringComparison.OrdinalIgnoreCase)
            && today >= d.Start && today <= d.End);
        return covering
            ? new OwnerApproval(owner, actingUser, owner, false)
            : null;
    }
}
