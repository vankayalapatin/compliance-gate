// Live memo-rule probe (exploratory, video prep only). Submits one board memo
// exactly the way the Memo page does (MemoSubmitService: inbox file, intake,
// runner advance with a live Spark proposal call) and reports what comes
// back: the proposal, KB citations, questions, past plus open-order impact,
// and whether the owner can approve it into a live rule. Publish writes land
// in a temp pack directory (copies of the current packs), never in the
// source tree, so concurrent windows are unaffected. ERP history plus the
// open order are probe-only data; item ids match the KB so the what-if join
// has rows to evaluate. The full plain-text report is written to the temp
// report path below; the test fails with the exact stage plus reason when
// the memo cannot reach a published rule.
using System.Text;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Gate.Eval.VideoProbe;

public sealed class MemoLiveProbeTests
{
    public const string MemoText =
        "Effective immediately no single vendor supply more than 50% of our critical battery cells over a 12 month rolling period.";

    public static string ReportPath =>
        Path.Combine(Path.GetTempPath(), "gate-memo-video-probe-report.txt");

    private readonly ITestOutputHelper _output;

    public MemoLiveProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Live")]
    public async Task VendorCapMemo_LiveProbe()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        void Note(string line)
        {
            report.AppendLine(line);
            _output.WriteLine(line);
        }

        Note($"Memo live probe at {DateTimeOffset.UtcNow:u}.");
        Note($"Memo text: {MemoText}");

        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            Note("SETUP BLOCKER: MetaApi:ApiKey or MetaApi:Model is missing (user secrets or env).");
            WriteReport(report);
            Assert.Fail("Live Spark needs MetaApi:ApiKey plus MetaApi:Model in user secrets or env.");
        }

        using var http = new HttpClient();
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!
        });
        Note($"Live model: {model}.");

        var sourcePacks = FindSourcePackDir();
        if (sourcePacks is null)
        {
            Note("SETUP BLOCKER: could not locate Gate.Compliance/Packs from the test directory.");
            WriteReport(report);
            Assert.Fail("Probe setup failed: Gate.Compliance/Packs not found.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "gate-memo-video-probe", Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(root, "cache");
        var inboxDir = Path.Combine(root, "inbox");
        var packDir = Path.Combine(root, "packs");
        Directory.CreateDirectory(packDir);
        foreach (var packFile in Directory.GetFiles(sourcePacks, "*.json", SearchOption.TopDirectoryOnly))
        {
            File.Copy(packFile, Path.Combine(packDir, Path.GetFileName(packFile)));
        }

        var compliance = new ComplianceEngine(new ComplianceMemoOptions(PackDirectory: packDir));
        KbSnapshot snapshot;
        try
        {
            snapshot = await compliance.LoadKbSnapshotAsync();
            Note($"KB snapshot: {snapshot.KbVersion} with {snapshot.Items.Count} items.");
        }
        catch (Exception ex)
        {
            Note($"KB LOAD BREAKS HERE: {ex.GetType().Name}: {ex.Message}");
            WriteReport(report);
            Assert.Fail($"KB snapshot load failed: {ex.Message}");
            return;
        }

        var erp = CreateProbeErp();
        try
        {
            await SeedOpenOrderAsync(erp);
            Note("Probe ERP: BAT-1 history (300 cells) plus one open PR and one draft PO.");
        }
        catch (Exception ex)
        {
            Note($"PROBE ERP SETUP BREAKS HERE: {ex.GetType().Name}: {ex.Message}");
            WriteReport(report);
            Assert.Fail($"Probe ERP setup failed: {ex.Message}");
            return;
        }

        var runner = new MemoRunner(erp, spark, compliance, cacheDir);
        var memos = new MemoFileStore(cacheDir);
        var submit = new MemoSubmitService(
            runner, memos, inboxDir, NullLogger<MemoSubmitService>.Instance);

        MemoSubmitResult result;
        try
        {
            result = await submit.SubmitAsync("Probe Board", MemoText);
        }
        catch (Exception ex)
        {
            Note($"SUBMIT BREAKS HERE (exception): {ex.GetType().Name}: {ex.Message}");
            WriteReport(report);
            Assert.Fail($"Memo submit threw: {ex.Message}");
            return;
        }

        Note($"Submit ok: {result.Ok}. Memo: {result.MemoId}. Settled at: {result.Step ?? "none"}.");
        Note($"Submit summary: {result.Summary}");
        if (!result.Ok)
        {
            Note($"SUBMIT REFUSED HERE: {result.Error}");
            WriteReport(report);
            Assert.Fail($"Memo submit refused: {result.Error}");
            return;
        }

        foreach (var statement in result.Statements)
        {
            Note($"Statement: {statement}");
        }

        foreach (var question in result.Questions)
        {
            Note($"Open question: {question}");
        }

        foreach (var error in result.ValidationErrors)
        {
            Note($"Validation error: {error}");
        }

        var file = MemoStore.Load(cacheDir, result.MemoId);
        if (file is null || file.Proposals.Count == 0)
        {
            Note("PROPOSAL BREAKS HERE: no proposal round was stored.");
            WriteReport(report);
            Assert.Fail("No proposal round stored after submit.");
            return;
        }

        var record = file.Proposals[^1];
        var proposal = record.Proposal;
        Note($"Proposal: {proposal.ProposalId} on {proposal.KbVersion}, hash {proposal.ProposalHash}.");
        Note($"Policy owner: {record.PolicyOwner}.");
        foreach (var statement in proposal.Statements)
        {
            Note($"Proposal statement: \"{statement.Quote}\" -> {statement.Interpretation} (confidence {statement.Confidence}).");
        }

        foreach (var resolution in proposal.Resolutions)
        {
            Note($"Resolution: \"{resolution.Phrase}\" -> {resolution.EntityId} ({Cite(resolution.Citation)}).");
        }

        if (proposal.Citations.Count == 0)
        {
            Note("Citations: none.");
        }

        foreach (var citation in proposal.Citations)
        {
            Note($"Citation: {Cite(citation)}.");
        }

        if (proposal.ProposedRule is not null)
        {
            var rule = proposal.ProposedRule;
            Note($"Proposed rule: {rule.RuleType} at {rule.Gate}.");
            foreach (var kv in rule.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                Note($"Rule param: {kv.Key} = {kv.Value}.");
            }
        }
        else if (proposal.NotMappable is not null)
        {
            Note($"NotMappable: nearest {proposal.NotMappable.NearestType}; {proposal.NotMappable.WhyNot} (missing: {proposal.NotMappable.MissingCapability}).");
        }

        foreach (var question in proposal.Questions)
        {
            Note($"Proposal question [{question.QuestionId}] {(question.Blocking ? "blocking" : "advisory")}: {question.Question} Options: {string.Join("; ", question.Options)}.");
        }

        Note($"Validation ok: {record.Validation.Ok}.");
        foreach (var error in record.Validation.Errors)
        {
            Note($"Validation error: {error}");
        }

        foreach (var conflict in record.Validation.Conflicts)
        {
            Note($"Live-pack conflict: {conflict.RuleId} ({conflict.Kind}): {conflict.Detail}");
        }

        if (!record.Validation.Ok)
        {
            failures.Add("validation refused the proposal; see validation errors above");
        }

        if (record.WhatIf is not null)
        {
            var whatIf = record.WhatIf;
            Note($"What-if: {whatIf.HistoryLabel}; {whatIf.HistoryRowCount} history rows; computed {whatIf.ComputedAt:u}. Unviable: {whatIf.Unviable}.");
            foreach (var flip in whatIf.HistoryFlips)
            {
                Note($"Past flip: {flip.DocumentType} {flip.DocumentNumber}: {flip.OldKind} -> {flip.NewKind} (blocked by {flip.BlockingRuleId ?? "none"}), qty {flip.Quantity}, {flip.Amount} {flip.Currency ?? "no-ccy"}.");
            }

            if (whatIf.HistoryFlips.Count == 0)
            {
                Note("Past flips: none.");
            }

            foreach (var row in whatIf.PipelineRows)
            {
                Note($"Open-order row: {row.DocumentType} {row.DocumentNumber}: {row.OldKind} -> {row.NewKind} (blocked by {row.BlockingRuleId ?? "none"}).");
            }

            if (whatIf.PipelineRows.Count == 0)
            {
                Note("Open-order rows: none.");
            }

            Note($"What-if totals: {whatIf.Totals.DocumentsEvaluated} evaluated, {whatIf.Totals.FlipsToBlock} to block, {whatIf.Totals.FlipsToPass} to pass, qty {whatIf.Totals.QuantityFlipped}, {whatIf.Totals.AmountFlipped} {whatIf.Totals.Currency ?? "no-ccy"}.");
        }
        else if (proposal.ProposedRule is not null)
        {
            Note("What-if: not computed.");
            failures.Add("what-if report missing for a mappable proposal");
        }
        else
        {
            Note("What-if: not computed (NotMappable carries none).");
        }

        var blocking = file.Decisions.Where(d =>
            d.Kind == DecisionKind.PolicyQuestion && d.Status == DecisionStatus.Pending).ToList();
        foreach (var card in blocking)
        {
            Note($"Blocking card: {card.DecisionId} to {card.RequestedTo}: {card.Reason}");
        }

        var approval = file.Decisions.FirstOrDefault(d =>
            d.Kind == DecisionKind.PolicyApproval && d.Status == DecisionStatus.Pending);
        if (approval is null)
        {
            Note("Approval card: none pending.");
            if (blocking.Count > 0)
            {
                failures.Add($"{blocking.Count} blocking question(s) park the memo; the owner must answer before approval");
            }
            else if (proposal.ProposedRule is null)
            {
                failures.Add("NotMappable proposal with no approval path; answers must make it mappable first");
            }
            else if (record.Validation.Ok)
            {
                failures.Add("no approval card was raised for a valid proposal");
            }
        }
        else
        {
            Note($"Approval card: {approval.DecisionId} to {approval.RequestedTo}; pin {approval.DocumentNumber}.");
            if (blocking.Count == 0 && record.Validation.Ok)
            {
                var update = record.Validation.Conflicts.FirstOrDefault(c =>
                    c.Kind is RuleConflictKind.Tightens or RuleConflictKind.Loosens or RuleConflictKind.Subsumes);
                try
                {
                    var outcome = await runner.ResolvePolicyApprovalAsync(
                        result.MemoId,
                        approval.DecisionId,
                        new PolicyApprovalInput(
                            true,
                            approval.RequestedTo,
                            DateTimeOffset.UtcNow,
                            RuleApplicability.ProspectiveOnly,
                            "Video probe approval.",
                            null, null, null,
                            update is null ? new NewRuleSpec("Single-vendor share cap on critical battery cells", RuleTier.Tier2) : null,
                            ["VideoProbe.MemoLiveProbeTests.VendorCapMemo_LiveProbe"]),
                        CancellationToken.None);
                    if (outcome.Activation is not null)
                    {
                        var activation = outcome.Activation;
                        Note($"APPROVED AND PUBLISHED (temp packs): {activation.RuleId} in {activation.PackId} {activation.PriorPackVersion} -> {activation.NewPackVersion}, effective {activation.EffectiveAt:u} ({activation.Applicability}).");
                        var published = Directory.GetFiles(packDir, "*.json", SearchOption.TopDirectoryOnly)
                            .Select(n => Path.GetFileName(n)!)
                            .OrderBy(n => n, StringComparer.Ordinal);
                        Note($"Temp pack dir now holds: {string.Join(", ", published)}.");
                    }
                    else
                    {
                        failures.Add("approval recorded without a publication");
                    }
                }
                catch (Exception ex)
                {
                    Note($"APPROVAL BREAKS HERE: {ex.GetType().Name}: {ex.Message}");
                    failures.Add($"owner approval refused: {ex.Message}");
                }
            }
            else if (blocking.Count > 0)
            {
                failures.Add($"{blocking.Count} blocking question(s) park the memo; the owner must answer before approval");
            }
        }

        WriteReport(report);
        Assert.True(
            failures.Count == 0,
            "Memo probe stops before a live rule:\n- " + string.Join("\n- ", failures));
    }

    private static string Cite(KbCitation citation) =>
        $"kb:{citation.Section}#{citation.EntryId}@{citation.KbVersion}";

    private static void WriteReport(StringBuilder report) =>
        File.WriteAllText(ReportPath, report.ToString());

    private static string? FindSourcePackDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Gate.Compliance", "Packs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static InMemoryErpAdapter CreateProbeErp()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("USD");
        erp.SeedAutoApproveThreshold(400m);
        erp.SeedPaymentTerms(new PaymentTermsDto("PROBE-MEMO-NET0", 0));
        erp.SeedItem(new ItemDto("BAT-1", "Probe battery cell", true, 10, "EA"));
        erp.SeedItem(new ItemDto("SCR-9", "Probe screw", false, 25, "EA"));
        erp.SeedItem(new ItemDto("GLASS-1", "Probe smart glasses", false, 1, "EA"));
        erp.SeedVendor(new VendorDto("VPROBE-CN", "Probe CinderVolt Cells", "CN", false, null, "PROBE-MEMO-NET0", "sales@probecv.example"));
        erp.SeedVendor(new VendorDto("VPROBE-MX", "Probe Meridian Cells", "MX", false, null, "PROBE-MEMO-NET0", "sales@probemw.example"));
        var now = DateTimeOffset.UtcNow;
        erp.SeedVendorBank(new VendorBankDto("VPROBE-CN", "PROBE-BANK-CV-002", true, now));
        erp.SeedVendorBank(new VendorBankDto("VPROBE-MX", "PROBE-BANK-MX-002", true, now));
        erp.SeedVendorPrice("VPROBE-CN", "BAT-1", 20.00m);
        erp.SeedVendorPrice("VPROBE-MX", "BAT-1", 22.00m);
        erp.SeedVolumeHistory("BAT-1", [
            new VolumeHistory("Probe CN awards", "CN", 120),
            new VolumeHistory("Probe MX awards", "MX", 140),
            new VolumeHistory("Probe other awards", "OTHER", 40)]);
        erp.SeedUser(new UserDto("m-requester", "requester", 5000m, "USD"));
        erp.SeedUser(new UserDto("m-approver", "approver", 100_000m, "USD"));
        return erp;
    }

    private static async Task SeedOpenOrderAsync(InMemoryErpAdapter erp)
    {
        await erp.CreatePrAsync(
            "m-requester", RecordSource.P2pAgent, "m-requester",
            [new PurchaseRequisitionLine("BAT-1", 30, "EA")],
            Guid.NewGuid().ToString("N"));
        var draft = await erp.CreatePrAsync(
            "m-requester", RecordSource.P2pAgent, "m-requester",
            [new PurchaseRequisitionLine("BAT-1", 20, "EA")],
            Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", draft.PrNumber, "m-approver", "m-requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PR", draft.PrNumber, "m-approver", true, "probe approval", Guid.NewGuid().ToString("N"));
        await erp.CreatePoAsync(draft.PrNumber, "VPROBE-MX", RecordSource.P2pAgent, "m-requester", Guid.NewGuid().ToString("N"));
    }
}
