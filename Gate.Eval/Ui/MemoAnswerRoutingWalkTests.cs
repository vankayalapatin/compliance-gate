// Real-app walk for memo answer routing: fresh temp dirs stand in for a
// fresh Restore (empty packs, empty memo store). Dana Director submits a
// memo through the UI-called submit service; the real compliance engine
// reads the real company KB and packs. Canned Spark completions mirror
// validated live rounds (kbv3 citations, OriginShareCap 40%). Dana
// answers the blocking question through the UI-called outcome service;
// the approval card must reach Finn Finance (not Dana or her delegate),
// Dana's approval is refused, and Finn's approval publishes CORP v3.
// No live calls.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class MemoAnswerRoutingWalkTests
{
    [Fact]
    public async Task AnsweringRoutesApprovalToFinnAndPublishes()
    {
        var root = UiTestSupport.NewDir("gate-ui-memowalk");
        var cacheDir = Path.Combine(root, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var inboxDir = Path.Combine(root, "memo-inbox");
        Directory.CreateDirectory(inboxDir);
        var packsDir = Path.Combine(root, "packs");
        Directory.CreateDirectory(packsDir);
        var kbDir = Path.GetDirectoryName(CompanyKbLoader.ResolvePath("company/company-kb.json"))!;
        var engine = new ComplianceEngine(new ComplianceMemoOptions(kbDir, packsDir));
        var erp = SeededErp.Create();
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        erp.SeedDelegation(new DelegationDto("Dana Director", "Del Delegate", today.AddDays(-10), today.AddDays(10)));
        var models = new RecordingModelClient();
        var runner = new MemoRunner(erp, models, engine, cacheDir);
        var submit = new MemoSubmitService(
            runner, new MemoFileStore(cacheDir), inboxDir, UiTestLogger<MemoSubmitService>.Instance);
        var outcomes = new PolicyOutcomeService(
            new MemoFileStore(cacheDir), runner, UiTestLogger<PolicyOutcomeService>.Instance);

        // Opening state, as after a fresh Restore: no published packs.
        Assert.Empty(Directory.GetFiles(packsDir, "*.json"));

        models.QueueText(Round1WithBlockingQuestion());
        var placed = await submit.SubmitAsync(
            "Dana Director",
            "No single country of origin may account for more than 40% of our battery-cell " +
            "purchases over any 12-month period. This replaces the 60% limit set in MEMO-0001.");
        Assert.True(placed.Ok, placed.Error);
        Assert.Equal("MEMO-0001", placed.MemoId);
        Assert.Equal(MemoNextStep.AwaitAnswers.ToString(), placed.Step);
        var question = MemoStore.Load(cacheDir, placed.MemoId)!.Decisions
            .Single(d => d.Kind == DecisionKind.PolicyQuestion);
        Assert.Equal("Dana Director", question.RequestedTo);

        models.QueueText(Round2Clean());
        var answered = await outcomes.AnswerQuestionAsync(
            placed.MemoId, question.DecisionId, "critical", "Dana Director");
        Assert.True(answered.Ok, answered.Message);

        // Answering continued to the proposal and routed its approval past
        // the author to Finn Finance.
        var after = MemoStore.Load(cacheDir, placed.MemoId)!;
        var card = Assert.Single(after.Decisions.Where(d => d.Kind == DecisionKind.PolicyApproval));
        Assert.Equal(DecisionStatus.Pending, card.Status);
        Assert.Equal("Finn Finance", card.RequestedTo);
        Assert.Contains(after.Events, e =>
            e.Summary.Contains("approval routed to Finn Finance", StringComparison.Ordinal));

        // Dana (author) and Del (her delegate) are both refused; Finn publishes.
        var evidence = new[] { "MemoAnswerRoutingWalkTests.AnsweringRoutesApprovalToFinnAndPublishes" };
        var dana = await outcomes.DecideApprovalAsync(
            placed.MemoId, card.DecisionId, true, "Dana Director",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, testEvidence: evidence);
        Assert.False(dana.Ok);
        Assert.Contains("can't approve their own memo", dana.Message, StringComparison.Ordinal);
        var del = await outcomes.DecideApprovalAsync(
            placed.MemoId, card.DecisionId, true, "Del Delegate",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, testEvidence: evidence);
        Assert.False(del.Ok);
        Assert.Contains("active delegation", del.Message, StringComparison.Ordinal);

        var finn = await outcomes.DecideApprovalAsync(
            placed.MemoId, card.DecisionId, true, "Finn Finance",
            DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
            null, null, testEvidence: evidence);
        Assert.True(finn.Ok, finn.Message);
        var published = MemoStore.Load(cacheDir, placed.MemoId)!;
        var activation = Assert.Single(published.Activations);
        Assert.Equal("CORP-02", activation.RuleId);
        Assert.Equal("v2", activation.PriorPackVersion);
        Assert.Equal("v3", activation.NewPackVersion);
        var set = RulePackLoader.LoadCurrent(packsDir);
        Assert.Equal("v3", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        Assert.Equal(0.40m, set.Corp.OriginCap);
    }

    private static string Round1WithBlockingQuestion() => """
        {
          "statements": [{"quote": "no single country of origin may account for more than 40% of our battery-cell purchases over any 12-month period.", "interpretation": "Single country share of battery-cell purchases capped at 40% over trailing 12 months at vendor award.", "confidence": 0.95}, {"quote": "This replaces the 60% limit set in MEMO-0001.", "interpretation": "Supersedes prior 60% origin cap from MEMO-0001 with new 40% cap.", "confidence": 0.96}],
          "resolutions": [{"phrase": "battery-cell purchases", "entityId": "BAT-1", "citation": "kb:items#BAT-1@kbv3"}, {"phrase": "country of origin", "entityId": "origin-cap", "citation": "kb:sourcingPolicies#origin-cap@kbv3"}, {"phrase": "12-month period", "entityId": "origin-cap", "citation": "kb:sourcingPolicies#origin-cap@kbv3"}, {"phrase": "MEMO-0001", "entityId": "sourcing-policy", "citation": "kb:procurement#sourcing-policy@kbv3"}],
          "proposedRule": {"ruleType": "OriginShareCap", "gate": "Vendor", "params": {"cap": "0.40", "windowMonths": "12", "partScope": "critical"}},
          "notMappable": null,
          "questions": [{"questionId": "Q1", "question": "Does 'battery-cell purchases' scope mean critical parts only or all parts for partScope?", "options": ["critical", "all"], "blocking": true}],
          "citations": ["kb:items#BAT-1@kbv3", "kb:sourcingPolicies#origin-cap@kbv3", "kb:procurement#sourcing-policy@kbv3"],
          "policyOwner": "Dana Director"
        }
        """;

    private static string Round2Clean() => """
        {
          "statements": [{"quote": "no single country of origin may account for more than 40% of our battery-cell purchases over any 12-month period.", "interpretation": "Caps share of battery-cell purchases from any single country of origin at 40% over trailing 12 months.", "confidence": 0.95}, {"quote": "This replaces the 60% limit set in MEMO-0001.", "interpretation": "Supersedes prior 60% limit from MEMO-0001.", "confidence": 0.95}],
          "resolutions": [{"phrase": "battery-cell purchases", "entityId": "BAT-1", "citation": "kb:items#BAT-1@kbv3"}, {"phrase": "country of origin", "entityId": "origin-cap", "citation": "kb:sourcingPolicies#origin-cap@kbv3"}, {"phrase": "MEMO-0001", "entityId": "sourcing-policy", "citation": "kb:procurement#sourcing-policy@kbv3"}],
          "proposedRule": {"ruleType": "OriginShareCap", "gate": "Vendor", "params": {"cap": "0.40", "windowMonths": "12", "partScope": "critical"}},
          "notMappable": null,
          "questions": [],
          "citations": ["kb:items#BAT-1@kbv3", "kb:sourcingPolicies#origin-cap@kbv3", "kb:procurement#sourcing-policy@kbv3"],
          "policyOwner": "Dana Director"
        }
        """;
}
