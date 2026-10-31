// Live 3-memo demo: Spark judgment on the Asian-supplier memo, the
// sustainable-supplier memo, and a UFLPA board memo — one propose pass each
// against REAL Spark (parse, hash, validate stay C# via the seam). Each test
// logs the FULL proposal detail via ITestOutputHelper (visible in the check
// log when a test fails) AND dumps the same lines to the git-ignored file
// .agent-checks/outbox/memo-demo-<TAG>.proposal.log, because passing-test
// output never reaches the check log at default verbosity. The lead quotes
// the dump files. Single propose pass per memo; questions are never
// answered and proposals never re-driven here. The key comes from user
// secrets (MetaApi:ApiKey) or the environment — never from a file in the repo.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class MemoLiveDemoTests
{
    private readonly ITestOutputHelper _output;

    public MemoLiveDemoTests(ITestOutputHelper output) => _output = output;

    private sealed record LiveSecrets(string ApiKey, string Model, string BaseUrl);

    private static LiveSecrets Secrets()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web (plus MetaApi:Model).");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing. Set it with: dotnet user-secrets set \"MetaApi:Model\" \"<model>\" --project Gate.Web.");
        return new LiveSecrets(
            apiKey!, model!,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl);
    }

    private static KbSnapshot RealSnapshot()
    {
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        return CompanyKbLoader.Load(File.ReadAllText(kbPath)).ToSnapshot();
    }

    /// <summary>Test-local transport wrapper: raises the per-call MaxTokens
    /// budget on the live propose call only. The prompt, strict parse,
    /// engine hash, and validation are untouched — MemoRunner still builds
    /// the prompt and judges the completion exactly as production does.
    /// Needed because the reasoning model burns the production 8000-token
    /// budget thinking (observed: truncated JSON on ASIA/SUSTAIN, full
    /// starvation on UFLPA, memo-demo-1) before it finishes the answer.
    /// Production MemoRunner still carries 8000; that gap is a lead call.
    /// </summary>
    private sealed class BudgetRaisingClient(IModelClient inner, int maxTokens, Action<string> emit) : IModelClient
    {
        public string ModelName => inner.ModelName;

        public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            var bumped = request.MaxTokens < maxTokens ? request with { MaxTokens = maxTokens } : request;
            emit($"[memo-demo] transport: live propose call MaxTokens {request.MaxTokens} -> {bumped.MaxTokens} (test-local wrapper; prompt, strict parse, hash, and validation unchanged).");
            return inner.CompleteAsync(bumped, ct);
        }
    }

    private sealed class ModelTimeoutScope : IDisposable
    {
        private readonly TimeSpan _prior;

        public ModelTimeoutScope(TimeSpan timeout)
        {
            _prior = ResilientCalls.DefaultModelTimeout;
            ResilientCalls.DefaultModelTimeout = timeout;
        }

        public void Dispose() => ResilientCalls.DefaultModelTimeout = _prior;
    }

    private static string DumpPath(string tag)
    {
        var file = $"memo-demo-{tag}.proposal.log";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ".agent-checks", "outbox");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, file);
            }
        }

        var fallback = Path.Combine(Path.GetTempPath(), "gate-memo-demo");
        Directory.CreateDirectory(fallback);
        return Path.Combine(fallback, file);
    }

    private static void LogProposal(string tag, MemoProposalRecord record, Action<string> emit)
    {
        var p = record.Proposal;
        emit($"[memo-demo-{tag}] proposal {p.ProposalId} memo {p.MemoId} kb {p.KbVersion} hash {p.KbHash}");
        emit($"[memo-demo-{tag}] policyOwner: {record.PolicyOwner} proposalHash: {p.ProposalHash}");
        for (var i = 0; i < p.Statements.Count; i++)
        {
            var s = p.Statements[i];
            emit($"[memo-demo-{tag}] statement[{i}] quote: {s.Quote}");
            emit($"[memo-demo-{tag}] statement[{i}] interpretation: {s.Interpretation}");
            emit($"[memo-demo-{tag}] statement[{i}] confidence: {s.Confidence}");
        }

        for (var i = 0; i < p.Resolutions.Count; i++)
        {
            var r = p.Resolutions[i];
            emit($"[memo-demo-{tag}] resolution[{i}] phrase: {r.Phrase} entityId: {r.EntityId} citation: kb:{r.Citation.Section}#{r.Citation.EntryId}@{r.Citation.KbVersion}");
        }

        foreach (var c in p.Citations)
        {
            emit($"[memo-demo-{tag}] citation: kb:{c.Section}#{c.EntryId}@{c.KbVersion}");
        }

        if (p.ProposedRule is not null)
        {
            var rule = p.ProposedRule;
            emit($"[memo-demo-{tag}] proposedRule: {rule.RuleType} gate {rule.Gate}");
            foreach (var kv in rule.Params)
            {
                emit($"[memo-demo-{tag}] proposedRule param: {kv.Key}={kv.Value}");
            }
        }
        else
        {
            emit($"[memo-demo-{tag}] proposedRule: null");
        }

        if (p.NotMappable is not null)
        {
            emit($"[memo-demo-{tag}] notMappable nearestType: {p.NotMappable.NearestType}");
            emit($"[memo-demo-{tag}] notMappable whyNot: {p.NotMappable.WhyNot}");
            emit($"[memo-demo-{tag}] notMappable missingCapability: {p.NotMappable.MissingCapability}");
        }
        else
        {
            emit($"[memo-demo-{tag}] notMappable: null");
        }

        for (var i = 0; i < p.Questions.Count; i++)
        {
            var q = p.Questions[i];
            emit($"[memo-demo-{tag}] question[{i}] id: {q.QuestionId} blocking: {q.Blocking} text: {q.Question}");
            emit($"[memo-demo-{tag}] question[{i}] options: {string.Join(" | ", q.Options)}");
        }

        emit($"[memo-demo-{tag}] validation ok: {record.Validation.Ok}");
        foreach (var e in record.Validation.Errors)
        {
            emit($"[memo-demo-{tag}] validation error: {e}");
        }

        foreach (var c in record.Validation.Conflicts)
        {
            emit($"[memo-demo-{tag}] validation conflict: {c.RuleId} {c.Kind} :: {c.Detail}");
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task AsiaMemo_Live_AsksWhichCountries()
    {
        // Memo 2 verbatim from docs/company-kb/EXAMPLES.md: the COO
        // declaration for Asian suppliers of critical components.
        const string fullText = "Effective immediately, all POs to Asian suppliers for critical components need a COO declaration on file before award. Owner: Dana Director.";
        const string excerpt = "all POs to Asian suppliers for critical components need a COO declaration on file before award";
        const string memoId = "MEMO-DEMO-ASIA";

        var secrets = Secrets();
        using var http = new HttpClient();
        // Larger per-attempt timeout: a 16000-token reasoning completion
        // takes longer than the 2-minute production default. Restored on
        // dispose so sibling live tests keep production behavior.
        using var timeout = new ModelTimeoutScope(TimeSpan.FromMinutes(5));
        var dump = new List<string>();
        void Emit(string line)
        {
            dump.Add(line);
            _output.WriteLine(line);
        }

        var spark = new BudgetRaisingClient(new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey
        }), 16000, Emit);
        var snapshot = RealSnapshot();
        var asia = snapshot.RegionRules.FirstOrDefault(r =>
            string.Equals(r.Id, "asia", StringComparison.OrdinalIgnoreCase));
        if (asia is not null)
        {
            Emit($"[memo-demo-ASIA] KB asia list present: version {asia.Version} countries {{{string.Join(", ", asia.Countries)}}}; expect the confirm-or-exclude-TW blocking question.");
        }
        else
        {
            Emit("[memo-demo-ASIA] KB carries no asia region list; expect Spark to ask for the country list.");
        }

        var erp = SeededErp.Create();
        var compliance = new RecordingComplianceEngine { Snapshot = snapshot };
        var cacheDir = MemoTestKit.TempDir("gate-memo-demo-asia");
        var inboxDir = Path.Combine(MemoTestKit.TempDir("gate-memo-demo-asia-inbox"), "inbox");
        var runner = new MemoRunner(erp, spark, compliance, cacheDir);

        var name = MemoTestKit.WriteInboxMemo(inboxDir, memoId, excerpt, fullText);
        await runner.IngestMemoAsync(inboxDir, name);

        // Single propose pass; questions are reported, never answered here.
        var file = await runner.ProposeAsync(memoId);

        var record = Assert.Single(file.Proposals);
        LogProposal("ASIA", record, Emit);
        File.WriteAllText(DumpPath("ASIA"), string.Join("\n", dump) + "\n");
        Assert.Contains(record.Proposal.Questions, q => q.Blocking);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task SustainableMemo_Live_IsNotMappable()
    {
        // Memo 3 verbatim from docs/company-kb/EXAMPLES.md: prefer
        // sustainable suppliers, careful about risky regions.
        const string fullText = "We should prefer sustainable suppliers and be careful about risky regions going forward.";
        const string excerpt = "prefer sustainable suppliers and be careful about risky regions";
        const string memoId = "MEMO-DEMO-SUSTAIN";

        var secrets = Secrets();
        using var http = new HttpClient();
        using var timeout = new ModelTimeoutScope(TimeSpan.FromMinutes(5));
        var dump = new List<string>();
        void Emit(string line)
        {
            dump.Add(line);
            _output.WriteLine(line);
        }

        var spark = new BudgetRaisingClient(new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey
        }), 16000, Emit);
        var snapshot = RealSnapshot();
        var erp = SeededErp.Create();
        var compliance = new RecordingComplianceEngine { Snapshot = snapshot };
        var cacheDir = MemoTestKit.TempDir("gate-memo-demo-sustain");
        var inboxDir = Path.Combine(MemoTestKit.TempDir("gate-memo-demo-sustain-inbox"), "inbox");
        var runner = new MemoRunner(erp, spark, compliance, cacheDir);

        var name = MemoTestKit.WriteInboxMemo(inboxDir, memoId, excerpt, fullText);
        await runner.IngestMemoAsync(inboxDir, name);

        // Single propose pass; the expectation is NotMappable.
        var file = await runner.ProposeAsync(memoId);

        var record = Assert.Single(file.Proposals);
        LogProposal("SUSTAIN", record, Emit);
        File.WriteAllText(DumpPath("SUSTAIN"), string.Join("\n", dump) + "\n");
        Assert.NotNull(record.Proposal.NotMappable);
        Assert.Null(record.Proposal.ProposedRule);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task UflpaMemo_Live_ReportsWhateverSparkProposes()
    {
        // Real US government text (public domain): a SHORT verbatim core of
        // the Uyghur Forced Labor Prevention Act section 3 rebuttable
        // presumption — goods mined, produced, or manufactured wholly or in
        // part in the Xinjiang Uyghur Autonomous Region are presumed made
        // with forced labor and prohibited from entry. Statute: 19 U.S.C.
        // 1307 (Tariff Act section 307); enforced at the border by CBP.
        // Framed below as a board memo directing P2P compliance. No fixed
        // expectation: whatever Spark proposes (rule or NotMappable) is
        // reported honestly with its citations and reasons.
        const string fullText = "Board directive: comply with the Uyghur Forced Labor Prevention Act section 3 rebuttable presumption (19 U.S.C. 1307; enforced by CBP): all goods, wares, articles, and merchandise mined, produced, or manufactured wholly or in part in the Xinjiang Uyghur Autonomous Region are presumed made with forced labor and prohibited from entry. Ensure our P2P flow blocks in-scope goods. Owner: Dana Director.";
        const string excerpt = "block goods mined, produced, or manufactured wholly or in part in Xinjiang per UFLPA section 3";
        const string memoId = "MEMO-DEMO-UFLPA";

        var secrets = Secrets();
        using var http = new HttpClient();
        using var timeout = new ModelTimeoutScope(TimeSpan.FromMinutes(5));
        var dump = new List<string>();
        void Emit(string line)
        {
            dump.Add(line);
            _output.WriteLine(line);
        }

        var spark = new BudgetRaisingClient(new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey
        }), 16000, Emit);
        var snapshot = RealSnapshot();
        var erp = SeededErp.Create();
        var compliance = new RecordingComplianceEngine { Snapshot = snapshot };
        var cacheDir = MemoTestKit.TempDir("gate-memo-demo-uflpa");
        var inboxDir = Path.Combine(MemoTestKit.TempDir("gate-memo-demo-uflpa-inbox"), "inbox");
        var runner = new MemoRunner(erp, spark, compliance, cacheDir);

        var name = MemoTestKit.WriteInboxMemo(inboxDir, memoId, excerpt, fullText);
        await runner.IngestMemoAsync(inboxDir, name);

        // Single propose pass; no fixed rule expectation.
        var file = await runner.ProposeAsync(memoId);

        var record = Assert.Single(file.Proposals);
        LogProposal("UFLPA", record, Emit);
        Assert.NotNull(record.Validation);
        Assert.True(record.Validation.Ok || record.Validation.Errors.Count > 0,
            "Validation must run: pass or loud reject.");
        Emit(record.Validation.Ok
            ? "[memo-demo-UFLPA] round outcome: validation PASSED."
            : "[memo-demo-UFLPA] round outcome: validation LOUDLY REJECTED (see errors above).");
        File.WriteAllText(DumpPath("UFLPA"), string.Join("\n", dump) + "\n");
    }
}
