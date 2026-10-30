// Case-store tests: the CaseFile JSON round trip plus the byte-exact cache
// convention (env override, deterministic safe ids, one file per case).
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CaseStoreTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-case-test", Guid.NewGuid().ToString("N"));

    private static CaseFile SampleFile(string caseId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CaseFile(
            new Case(caseId, "SO-1", "BAT-1", "demo", CaseStage.PoPlaced, now, now,
                "PR-1", "PO-1", null, null,
                [new InvoiceKey("V-B", "INV-1")], ["PAY-1"], null, null),
            [new CaseEvent("e1", caseId, now, RecordSource.P2pAgent, CaseEventKind.PoCreated,
                "PO PO-1 to V-B.", null, "PO", "PO-1", null, 1080m, "USD")],
            [new Decision("d1", caseId, DecisionKind.PoApproval, "Approve PO PO-1",
                DecisionStatus.Approved, "delegate", now, "delegate", now,
                "PO", "PO-1", 1080m, "USD", null, "PO PO-1 needs a human approval.", [], "ok")]);
    }

    [Fact]
    public void CaseFileJsonRoundTrip()
    {
        var dir = TempDir();
        var file = SampleFile("demo-SO-1-BAT-1");
        CaseStore.Save(dir, file);

        var path = Path.Combine(dir, "demo-SO-1-BAT-1.json");
        Assert.True(File.Exists(path));
        var reloaded = CaseStore.Load(dir, "demo-SO-1-BAT-1");
        Assert.NotNull(reloaded);
        Assert.Equal(file.Case.CaseId, reloaded.Case.CaseId);
        Assert.Equal(file.Case.Stage, reloaded.Case.Stage);
        Assert.Equal(file.Case.PrNumber, reloaded.Case.PrNumber);
        Assert.Equal(file.Case.PoNumber, reloaded.Case.PoNumber);
        Assert.Equal(file.Case.Invoices, reloaded.Case.Invoices);
        Assert.Equal(file.Case.PaymentNumbers, reloaded.Case.PaymentNumbers);
        var reloadedEvent = Assert.Single(reloaded.Events);
        Assert.Equal(CaseEventKind.PoCreated, reloadedEvent.Kind);
        Assert.Equal(1080m, reloadedEvent.Amount);
        Assert.Equal("USD", reloadedEvent.Currency);
        var reloadedDecision = Assert.Single(reloaded.Decisions);
        Assert.Equal(DecisionKind.PoApproval, reloadedDecision.Kind);
        Assert.Equal(DecisionStatus.Approved, reloadedDecision.Status);
        Assert.Equal("delegate", reloadedDecision.DecidedBy);
    }

    [Fact]
    public void MintedIdsAreDeterministicAndFilesystemSafe()
    {
        var first = CaseStore.MintCaseId("scen/ario", "SO:1", "IT*M?");
        var second = CaseStore.MintCaseId("scen/ario", "SO:1", "IT*M?");
        Assert.Equal(first, second);
        Assert.DoesNotContain("/", first);
        Assert.DoesNotContain(":", first);
        Assert.DoesNotContain("*", first);
        Assert.DoesNotContain("?", first);
        Assert.Equal("scen-ario-SO-1-IT-M-", first);

        var dir = TempDir();
        CaseStore.Save(dir, SampleFile(first));
        Assert.NotNull(CaseStore.Load(dir, first));
    }

    [Fact]
    public void ResolveCacheDirPrefersEnvThenContentRoot()
    {
        var previous = Environment.GetEnvironmentVariable(CaseStore.CacheDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CaseStore.CacheDirEnvVar, "/tmp/gate-env-cache");
            Assert.Equal("/tmp/gate-env-cache", CaseStore.ResolveCacheDir("/content"));

            Environment.SetEnvironmentVariable(CaseStore.CacheDirEnvVar, null);
            Assert.Equal(
                Path.Combine("/content", "data", "case-cache"),
                CaseStore.ResolveCacheDir("/content"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CaseStore.CacheDirEnvVar, previous);
        }
    }

    [Fact]
    public void MissingCaseLoadsNullAndEmptyDirListsNothing()
    {
        var dir = TempDir();
        Assert.Null(CaseStore.Load(dir, "nope"));
        Assert.Empty(CaseStore.ListCaseIds(dir));
    }

    [Fact]
    public void ListCaseIdsReturnsSortedSavedCases()
    {
        var dir = TempDir();
        CaseStore.Save(dir, SampleFile("b-case"));
        CaseStore.Save(dir, SampleFile("a-case"));
        Assert.Equal(["a-case", "b-case"], CaseStore.ListCaseIds(dir));
    }
}
