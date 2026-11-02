// Agent scorecard view models for the case page: tool calls the model
// issued, calls refused or failed with their reasons, steps completed by
// rule instead of an agent tool call, policy auto-approvals
// shown separately, and model calls with tokens plus a ballpark cost from
// configured per-token rates. Display shapes only; counts come from the
// case's own transcript, timeline, and ERP approvals at runtime.
using System.Globalization;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Web.ViewModels;

/// <summary>One refused or failed tool call: the tool name plus the reason
/// the transcript recorded.</summary>
public sealed record ScorecardAttempt(string Tool, string Reason);

/// <summary>One step completed by rule: deferred turns where the model
/// returned no tool calls, plus rule-fallback timeline notes.</summary>
public sealed record ScorecardRuleStep(string Title, string Detail);

/// <summary>One policy auto-approval on the case's documents, shown
/// separately from agent and rule work.</summary>
public sealed record ScorecardPolicyApproval(string Document, string Detail);

/// <summary>Vision reads for one case: photo reads with their tokens plus
/// a ballpark cost. Null when the case transcribed no photos.</summary>
public sealed record VisionScorecardVm(
    int Photos,
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCostUsd);

public sealed record ScorecardVm(
    int ToolCallsMade,
    int ToolCallsExecuted,
    IReadOnlyList<ScorecardAttempt> Refused,
    IReadOnlyList<ScorecardAttempt> Failed,
    IReadOnlyList<ScorecardRuleStep> RuleSteps,
    IReadOnlyList<ScorecardPolicyApproval> PolicyApprovals,
    int ModelCalls,
    int LiveModelCalls,
    int CachedModelCalls,
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCostUsd,
    bool RatesConfigured,
    VisionScorecardVm? VisionReads);

/// <summary>Pure scorecard builder. Tool counts come from the transcript
/// (deferred pseudo-calls count as rule steps, not model calls); rule
/// fallbacks also surface from timeline notes; policy approvals from the
/// ERP documents; cost from the token sums times configured rates.</summary>
public static class Scorecard
{
    public static ScorecardVm Build(
        IReadOnlyList<TranscriptTurn> turns,
        CaseFile file,
        PurchaseRequisition? pr,
        PurchaseOrder? po,
        decimal? autoApproveThreshold,
        decimal? inputCostPerMillionUsd,
        decimal? outputCostPerMillionUsd,
        IReadOnlyList<VisionPhotoUsage>? vision = null)
    {
        var calls = turns.SelectMany(t => t.Calls).ToList();
        var issued = calls.Where(c => c.Verdict != ToolCallVerdict.ModelDeferred).ToList();
        var ruleSteps = new List<ScorecardRuleStep>();
        foreach (var turn in turns.Where(t => t.Calls.Any(c => c.Verdict == ToolCallVerdict.ModelDeferred)))
        {
            ruleSteps.Add(new ScorecardRuleStep(
                StepTitle(turn.PickedStep),
                "The model returned no tool calls, so this step ran by rule."));
        }

        foreach (var e in file.Events
            .Where(e => e.Kind == CaseEventKind.Note
                && e.Summary.Contains("by rule", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Timestamp))
        {
            ruleSteps.Add(new ScorecardRuleStep("Rule fallback", e.Summary));
        }

        var inputTokens = turns.Sum(t => t.InputTokens ?? 0);
        var outputTokens = turns.Sum(t => t.OutputTokens ?? 0);
        var rates = inputCostPerMillionUsd is not null && outputCostPerMillionUsd is not null;
        decimal? cost = rates
            ? (inputTokens * inputCostPerMillionUsd!.Value + outputTokens * outputCostPerMillionUsd!.Value) / 1_000_000m
            : null;
        VisionScorecardVm? visionReads = null;
        if (vision is not null && vision.Count > 0)
        {
            var visionIn = vision.Sum(v => v.InputTokens ?? 0);
            var visionOut = vision.Sum(v => v.OutputTokens ?? 0);
            visionReads = new VisionScorecardVm(
                vision.Count, visionIn, visionOut,
                rates ? (visionIn * inputCostPerMillionUsd!.Value + visionOut * outputCostPerMillionUsd!.Value) / 1_000_000m : null);
        }

        return new ScorecardVm(
            issued.Count,
            issued.Count(c => c.Verdict == ToolCallVerdict.Executed),
            issued.Where(c => c.Verdict == ToolCallVerdict.Blocked)
                .Select(c => new ScorecardAttempt(c.Name, c.Detail)).ToList(),
            issued.Where(c => c.Verdict == ToolCallVerdict.Failed)
                .Select(c => new ScorecardAttempt(c.Name, c.Detail)).ToList(),
            ruleSteps,
            PolicyApprovals(pr, po, autoApproveThreshold),
            turns.Count,
            turns.Count(t => !t.FromCache && (t.InputTokens is not null || t.OutputTokens is not null)),
            turns.Count(t => t.FromCache),
            inputTokens,
            outputTokens,
            cost,
            rates,
            visionReads);
    }

    private static string StepTitle(string pickedStep) =>
        Enum.TryParse<CaseNextStep>(pickedStep, out var step)
            ? ActivityLines.StepWords(step)
            : pickedStep;

    private static IReadOnlyList<ScorecardPolicyApproval> PolicyApprovals(
        PurchaseRequisition? pr, PurchaseOrder? po, decimal? threshold)
    {
        var approvals = new List<ScorecardPolicyApproval>();
        if (pr is not null
            && string.Equals(pr.ApprovedBy, ErpApprovalActors.AutoApprovePolicy, StringComparison.Ordinal))
        {
            approvals.Add(new ScorecardPolicyApproval(
                $"Request {pr.PrNumber}", PolicyDetail(threshold)));
        }

        if (po is not null
            && po.ApprovalStatus == PoApprovalStatus.Approved
            && string.Equals(po.ApprovedBy, ErpApprovalActors.AutoApprovePolicy, StringComparison.Ordinal))
        {
            approvals.Add(new ScorecardPolicyApproval(
                $"Purchase order {po.PoNumber}", PolicyDetail(threshold)));
        }

        return approvals;
    }

    private static string PolicyDetail(decimal? threshold) =>
        threshold is { } limit
            ? $"Approved by policy (under {ActivityLines.Money(limit, null)})."
            : "Approved by policy.";

    /// <summary>Ballpark cost display: four decimals under a cent, two above.
    /// Invariant culture so every operator sees the same figure.</summary>
    public static string FormatCost(decimal cost) =>
        cost < 0.01m
            ? string.Create(CultureInfo.InvariantCulture, $"about ${cost:0.0000}")
            : string.Create(CultureInfo.InvariantCulture, $"about ${cost:0.00}");

    public static string FormatTokens(int tokens) =>
        tokens.ToString("N0", CultureInfo.InvariantCulture);
}
