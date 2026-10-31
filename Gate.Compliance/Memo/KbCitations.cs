// KB citation resolution (docs/company-kb/KB-SCHEMA.md + SPARK-PROPOSAL.md).
// Every Spark resolution cites kb:<section>#<id>@<version>. C# resolves
// each citation against the pinned KB snapshot; an unresolvable citation
// rejects the proposal loudly with the offending citation quoted.
//
// Sections are the KbSnapshot contract sections. Documents with
// p2pRelevant=false (HR policies and similar) are searchable agent
// context but are excluded from rule mapping by this C# check.
// The pseudo-entry items#critical-flag is the documented EXAMPLES.md
// memo-2 citation for the IsCritical flag across items; it resolves at
// the schema level whenever the items section is present.
using Gate.Contracts;

namespace Gate.Compliance;

public static class KbCitations
{
    public const string LegalEntities = "legalEntities";
    public const string SitesWarehouses = "sitesWarehouses";
    public const string FiscalCalendar = "fiscalCalendar";
    public const string ApprovalMatrix = "approvalMatrix";
    public const string Items = "items";
    public const string SourcingPolicies = "sourcingPolicies";
    public const string RegionRules = "regionRules";
    public const string Documents = "documents";

    /// <summary>Schema-level pseudo-entry (EXAMPLES.md memo 2): the critical
    /// flag across items, not one item row.</summary>
    public const string CriticalFlagEntry = "critical-flag";

    /// <summary>Singleton entry id for the fiscal calendar section.</summary>
    public const string FiscalCalendarCurrent = "current";

    public static readonly IReadOnlyList<string> KnownSections =
    [
        LegalEntities, SitesWarehouses, FiscalCalendar, ApprovalMatrix,
        Items, SourcingPolicies, RegionRules, Documents
    ];

    /// <summary>Wire form kb:&lt;section&gt;#&lt;id&gt;@&lt;version&gt;.</summary>
    public static string Format(KbCitation citation) =>
        $"kb:{citation.Section}#{citation.EntryId}@{citation.KbVersion}";

    /// <summary>Resolves one citation against the pinned snapshot. Returns
    /// null when it resolves, else the loud rejection reason quoting the
    /// offending citation. All id comparisons are ordinal (exact).</summary>
    public static string? Resolve(KbCitation citation, KbSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(snapshot);
        var wire = Format(citation);

        if (!string.Equals(citation.KbVersion, snapshot.KbVersion, StringComparison.Ordinal))
        {
            return $"Citation {wire} pins KB version '{citation.KbVersion}' but the pinned snapshot is '{snapshot.KbVersion}'; " +
                "re-ground the proposal against the current KB version before approval can proceed.";
        }

        return citation.Section switch
        {
            LegalEntities => snapshot.LegalEntities.Any(e => e.Id == citation.EntryId)
                ? null : $"Citation {wire} is unresolvable: no legal entity '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            SitesWarehouses => snapshot.SitesWarehouses.Any(s => s.Id == citation.EntryId)
                ? null : $"Citation {wire} is unresolvable: no site or warehouse '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            FiscalCalendar => ResolveFiscalCalendar(citation, snapshot, wire),
            ApprovalMatrix => snapshot.ApprovalMatrix.Any(a => a.Action == citation.EntryId)
                ? null : $"Citation {wire} is unresolvable: no approval-matrix action '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            Items => snapshot.Items.Any(i => i.Id == citation.EntryId)
                    || string.Equals(citation.EntryId, CriticalFlagEntry, StringComparison.Ordinal)
                ? null : $"Citation {wire} is unresolvable: no item '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            SourcingPolicies => snapshot.SourcingPolicies.Any(p => p.Id == citation.EntryId)
                ? null : $"Citation {wire} is unresolvable: no sourcing policy '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            RegionRules => snapshot.RegionRules.Any(r => r.Id == citation.EntryId)
                ? null : $"Citation {wire} is unresolvable: no region rule '{citation.EntryId}' in KB '{snapshot.KbVersion}'.",
            Documents => ResolveDocument(citation, snapshot, wire),
            _ => $"Citation {wire} is unresolvable: unknown KB section '{citation.Section}'. " +
                $"Known sections: {string.Join(", ", KnownSections)}.",
        };
    }

    private static string? ResolveFiscalCalendar(KbCitation citation, KbSnapshot snapshot, string wire)
    {
        if (!string.Equals(citation.EntryId, FiscalCalendarCurrent, StringComparison.Ordinal))
        {
            return $"Citation {wire} is unresolvable: the fiscal calendar is a singleton cited as '{FiscalCalendarCurrent}' " +
                $"(got '{citation.EntryId}').";
        }

        return snapshot.FiscalCalendar is not null
            ? null
            : $"Citation {wire} is unresolvable: KB '{snapshot.KbVersion}' has no fiscal calendar seeded yet.";
    }

    private static string? ResolveDocument(KbCitation citation, KbSnapshot snapshot, string wire)
    {
        var doc = snapshot.Documents.FirstOrDefault(d => d.Id == citation.EntryId);
        if (doc is null)
        {
            return $"Citation {wire} is unresolvable: no document '{citation.EntryId}' in KB '{snapshot.KbVersion}'.";
        }

        return doc.P2pRelevant
            ? null
            : $"Citation {wire} is rejected: document '{citation.EntryId}' carries p2pRelevant=false " +
                "(searchable agent context, excluded from rule mapping by the C# allowlist).";
    }
}
