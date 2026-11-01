// Spark relevance selector tests (offline, no live calls): structured
// sections pass through in full, p2pRelevant=false documents are excluded,
// wiki filters to the top overlap matches under the named cap,
// determinism holds, and the propose prompt is built from the filtered
// snapshot. Test-double data only: invented memo text and wiki entries,
// never demo numbers.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoSparkSelectorTests
{
    private static KbSnapshot Snapshot(
        IReadOnlyList<KbDocument>? documents = null,
        IReadOnlyList<KbWikiEntry>? procurement = null) =>
        MemoFixtures.TestKb(documents: documents, procurement: procurement);

    private static KbWikiEntry Wiki(string id, string title, string body) =>
        new(id, title, body);

    [Fact]
    public void StructuredSectionsPassThroughInFull()
    {
        var snapshot = Snapshot(
            documents:
            [
                new KbDocument("rel-template", "UN38.3", true),
                new KbDocument("hr-handbook", "HR-Policy", false),
            ],
            procurement: [Wiki("proc-a", "Lithium sourcing", "lithium cells")]);

        var filtered = MemoSparkSelector.SelectRelevant("lithium quarantine audit", snapshot);

        Assert.Equal(snapshot.KbVersion, filtered.KbVersion);
        Assert.Equal(snapshot.KbHash, filtered.KbHash);
        Assert.Equal(snapshot.LegalEntities.Count, filtered.LegalEntities.Count);
        Assert.Equal(snapshot.SitesWarehouses.Count, filtered.SitesWarehouses.Count);
        Assert.Equal(snapshot.ApprovalMatrix.Count, filtered.ApprovalMatrix.Count);
        Assert.Equal(snapshot.Items.Count, filtered.Items.Count);
        Assert.Equal(snapshot.SourcingPolicies.Count, filtered.SourcingPolicies.Count);
        Assert.Equal(snapshot.RegionRules.Count, filtered.RegionRules.Count);
        Assert.Equal("BAT-9", filtered.Items[0].Id);
        Assert.Equal("origin-cap", filtered.SourcingPolicies[0].Id);
    }

    [Fact]
    public void P2pIrrelevantDocumentsAreExcludedFromSparkContext()
    {
        var snapshot = Snapshot(documents:
        [
            new KbDocument("rel-template", "UN38.3", true),
            new KbDocument("hr-handbook", "HR-Policy", false),
        ]);

        var filtered = MemoSparkSelector.SelectRelevant("lithium quarantine audit", snapshot);

        var kept = Assert.Single(filtered.Documents);
        Assert.Equal("rel-template", kept.Id);
        Assert.True(kept.P2pRelevant);
    }

    [Fact]
    public void WikiFiltersToTopMatchesByKeywordOverlap()
    {
        var snapshot = Snapshot(procurement:
        [
            Wiki("proc-a", "Lithium sourcing", "lithium cells from qualified vendors"),
            Wiki("proc-b", "Quarantine handling", "quarantine lots pending review"),
            Wiki("proc-c", "Lithium quarantine audit", "lithium quarantine audit procedure"),
            Wiki("proc-d", "Unrelated", "payroll benefits enrollment"),
            Wiki("proc-e", "Audit trail", "audit logs retained"),
        ]);

        var filtered = MemoSparkSelector.SelectRelevant("lithium quarantine audit", snapshot);

        // proc-c scores 3, proc-a/proc-b/proc-e score 1, proc-d scores 0 and
        // drops. Cap 3 keeps proc-c plus the two lowest ids at score 1.
        var ids = filtered.Procurement.Select(e => e.Id).ToList();
        Assert.Equal(["proc-c", "proc-a", "proc-b"], ids);
    }

    [Fact]
    public void WikiCapIsEnforcedPerSection()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(i => Wiki($"proc-{i}", $"Lithium note {i}", "lithium cells"))
            .ToList();
        var snapshot = Snapshot(procurement: entries);

        var filtered = MemoSparkSelector.SelectRelevant("lithium", snapshot);

        Assert.Equal(MemoSparkSelector.MaxWikiEntriesPerSection, filtered.Procurement.Count);
        Assert.Equal(
            entries.Take(MemoSparkSelector.MaxWikiEntriesPerSection).Select(e => e.Id),
            filtered.Procurement.Select(e => e.Id));
    }

    [Fact]
    public void WikiSelectionIsDeterministic()
    {
        var snapshot = Snapshot(procurement:
        [
            Wiki("proc-b", "Quarantine handling", "quarantine lots"),
            Wiki("proc-a", "Lithium sourcing", "lithium cells"),
            Wiki("proc-c", "Lithium quarantine audit", "lithium quarantine audit"),
        ]);

        var first = MemoSparkSelector.SelectRelevant("lithium quarantine audit", snapshot);
        var second = MemoSparkSelector.SelectRelevant("lithium quarantine audit", snapshot);

        Assert.Equal(
            first.Procurement.Select(e => e.Id),
            second.Procurement.Select(e => e.Id));
        Assert.Equal(["proc-c", "proc-a", "proc-b"],
            first.Procurement.Select(e => e.Id).ToList());
    }

    [Fact]
    public void EmptyMemoYieldsEmptyWikiButFullStructured()
    {
        var snapshot = Snapshot(procurement:
            [Wiki("proc-a", "Lithium sourcing", "lithium cells")]);

        var filtered = MemoSparkSelector.SelectRelevant("   ", snapshot);

        Assert.Empty(filtered.Procurement);
        Assert.Empty(filtered.Leadership);
        Assert.Equal(snapshot.Items.Count, filtered.Items.Count);
        Assert.Single(filtered.Documents);
    }

    [Fact]
    public void NullInputsRefuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => MemoSparkSelector.SelectRelevant(null!, MemoFixtures.TestKb()));
        Assert.Throws<ArgumentNullException>(
            () => MemoSparkSelector.SelectRelevant("lithium", null!));
    }

    [Fact]
    public async Task ProposePromptIsBuiltFromTheFilteredSnapshot()
    {
        var h = MemoTestKit.Create();
        h.Compliance.Snapshot = Snapshot(
            documents:
            [
                new KbDocument("un383-template", "UN38.3", true),
                new KbDocument("hr-handbook", "HR-Policy", false),
            ],
            procurement:
            [
                Wiki("proc-c", "Lithium quarantine audit", "lithium quarantine audit procedure"),
                Wiki("proc-a", "Lithium sourcing", "lithium cells"),
                Wiki("proc-b", "Quarantine handling", "quarantine lots"),
                Wiki("proc-d", "Unrelated", "payroll benefits enrollment"),
            ]);
        var file = MemoTestKit.WriteInboxMemo(
            h.InboxDir, "MEMO-T9",
            "Lithium quarantine audit.",
            "Board memo: lithium quarantine audit procedure for critical battery cells.");
        await h.Runner.IngestMemoAsync(h.InboxDir, file);
        h.Models.QueueText(MemoTestKit.CapProposalJson());

        await h.Runner.ProposeAsync("MEMO-T9");

        var prompt = Assert.Single(h.Models.Requests).Prompt;
        Assert.Contains("BAT-9", prompt, StringComparison.Ordinal);
        Assert.Contains("origin-cap", prompt, StringComparison.Ordinal);
        Assert.Contains("un383-template", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("hr-handbook", prompt, StringComparison.Ordinal);
        Assert.Contains("proc-c", prompt, StringComparison.Ordinal);
        Assert.Contains("proc-a", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("proc-d", prompt, StringComparison.Ordinal);
    }
}
