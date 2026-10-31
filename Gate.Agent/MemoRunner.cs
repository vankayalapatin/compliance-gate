// Memo-to-rule runner (MEMO-PIPELINE.md stages 1-7, agent side). Spark does
// ALL thinking (interpretation, resolutions, rule choice, questions, owner);
// C# only checks and enforces: strict parse, engine hash + validation,
// what-if gather, publish routing, and storage. Every step is picked from
// memo state (MemoPicker); no fixed step sequence lives in code. Spark and
// engine calls carry timeouts plus one retry via ResilientCalls; failures
// are logged with memo context, timelined, and rethrown, never swallowed.
// Nothing publishes past an open or declined block.
using Gate.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gate.Agent;

public sealed record MemoAdvanceOutcome(
    string MemoId,
    MemoNextStep Step,
    bool Complete,
    string Summary);

/// <summary>Human inputs behind one policy-approval resolution. The approver
/// sets the effective datetime, applicability, and optional in-registry
/// param edits; packId/ruleId override the agent's deterministic routing
/// when the engine names a better target; newRuleSpec (title/tier/label) is
/// the approver's call for brand-new rules; testEvidence names the passing
/// tests covering the rule (the engine refuses empty evidence).</summary>
public sealed record PolicyApprovalInput(
    bool Approved,
    string Approver,
    DateTimeOffset EffectiveAt,
    RuleApplicability Applicability,
    string? Comment = null,
    IReadOnlyDictionary<string, string>? EditedParams = null,
    string? PackId = null,
    string? RuleId = null,
    NewRuleSpec? NewRuleSpec = null,
    IReadOnlyList<string>? TestEvidence = null);

public sealed record PolicyApprovalOutcome(
    Decision Decision,
    RuleActivation? Activation);

public sealed class MemoRunner
{
    private const int MaxAdvanceTurns = 10;

    private readonly IErpAdapter _erp;
    private readonly IModelClient _models;
    private readonly IComplianceEngine _compliance;
    private readonly MemoWhatIfGather _gather;
    private readonly ILogger<MemoRunner> _logger;
    private readonly string _cacheDir;

    public MemoRunner(
        IErpAdapter erp,
        IModelClient models,
        IComplianceEngine compliance,
        string cacheDir,
        ILogger<MemoRunner>? logger = null,
        MemoWhatIfGather? gather = null)
    {
        _erp = erp;
        _models = models;
        _compliance = compliance;
        _cacheDir = cacheDir;
        _logger = logger ?? NullLogger<MemoRunner>.Instance;
        _gather = gather ?? new MemoWhatIfGather(erp);
    }

    /// <summary>Ingests one inbox file into the memo store (idempotent).</summary>
    public Task<MemoFile> IngestMemoAsync(
        string inboxDir, string inboxFileName, CancellationToken ct = default)
    {
        _ = ct;
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(inboxFileName);
        return Task.FromResult(
            MemoIntake.EnsureMemo(_cacheDir, Path.Combine(inboxDir, inboxFileName), _logger));
    }

    /// <summary>Advances the memo until it parks in a waiting step (answers,
    /// approval) or finishes (published, refused, rejected, closed). Each
    /// turn re-reads the file and re-picks from state.</summary>
    public async Task<MemoAdvanceOutcome> AdvanceAsync(
        string memoId, CancellationToken ct = default)
    {
        for (var turn = 1; turn <= MaxAdvanceTurns; turn++)
        {
            var file = MemoStore.Load(_cacheDir, memoId)
                ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
            var (step, reason) = MemoPicker.Pick(file);
            _logger.LogInformation(
                "Memo {MemoId} with {Rounds} round(s): next step {Step} ({Reason}).",
                memoId, file.Proposals.Count, step, reason);
            if (step.IsWaiting())
            {
                return new MemoAdvanceOutcome(memoId, step, step == MemoNextStep.Done, reason);
            }

            try
            {
                switch (step)
                {
                    case MemoNextStep.Propose:
                        await ProposeAsync(memoId, ct).ConfigureAwait(false);
                        break;
                    case MemoNextStep.ComputeWhatIf:
                        await ComputeWhatIfAsync(memoId, ct).ConfigureAwait(false);
                        break;
                    case MemoNextStep.RaiseApproval:
                        RaiseApproval(memoId);
                        break;
                    default:
                        throw new InvalidOperationException($"Step {step} is not runnable.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memo {MemoId} step {Step} failed.", memoId, step);
                RecordFailureNote(memoId, step, ex);
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Memo {memoId} did not settle within {MaxAdvanceTurns} advance turns.");
    }

    /// <summary>One propose round: loads the pinned snapshot, asks Spark,
    /// parses strictly (reject loudly, never repair), stamps the canonical
    /// engine hash, validates via the engine, stores the record, supersedes
    /// older pending questions, and raises the new blocking-question cards.
    /// Parse rejections timeline and throw without storing; engine-invalid
    /// proposals are stored as rejected (terminal for the round).</summary>
    public async Task<MemoFile> ProposeAsync(string memoId, CancellationToken ct = default)
    {
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var round = file.Proposals.Count + 1;
        var proposalId = $"{memoId}-p{round}";

        var snapshot = await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        var prompt = MemoPrompts.BuildProposalPrompt(
            file.Memo, file.FullText, snapshot, PriorAnswers(file));
        // Propose calls need headroom: the prompt carries the whole KB
        // snapshot, and reasoning models can burn a 4000-token budget
        // thinking (observed live) and starve the answer.
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models,
            new ModelRequest(prompt, MemoPrompts.SystemPrompt, MaxTokens: 8000),
            _logger, $"memo proposal for {memoId}", ct).ConfigureAwait(false);

        RuleProposal draft;
        string owner;
        try
        {
            (draft, owner) = MemoProposalParser.Parse(
                result.Text, proposalId, memoId, snapshot, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memo {MemoId} round {Round}: Spark completion rejected.", memoId, round);
            var rejected = new MemoWork(MemoStore.Load(_cacheDir, memoId)
                ?? throw new InvalidOperationException($"Memo {memoId} vanished during propose."));
            rejected.AddEvent(CaseEventKind.Note,
                $"Proposal round {round} rejected before validation: {ex.Message}",
                documentType: "Memo", documentNumber: memoId);
            rejected.Save(_cacheDir);
            throw;
        }

        var hash = await _compliance.ComputeProposalHashAsync(draft, ct).ConfigureAwait(false);
        var proposal = draft with { ProposalHash = hash };
        var validation = await _compliance.ValidateProposalAsync(proposal, snapshot, ct).ConfigureAwait(false);

        var work = new MemoWork(MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Memo {memoId} vanished during propose."));
        work.AddProposal(new MemoProposalRecord(proposal, validation, null, owner));
        SupersedeOlderQuestions(work, work.CurrentFile, proposalId);
        if (validation.Ok)
        {
            foreach (var question in proposal.Questions.Where(q => q.Blocking))
            {
                work.FindOrCreateDecision(
                    DecisionKind.PolicyQuestion,
                    $"Answer blocking question {question.QuestionId} on {memoId}",
                    owner,
                    MemoPicker.QuestionDocumentType,
                    MemoPicker.QuestionDocNumber(proposalId, question.QuestionId),
                    reason: question.Question,
                    evidenceRefs: question.Options.Select(o => $"option:{o}").ToList());
            }
        }

        work.AddEvent(CaseEventKind.Note,
            DescribeProposal(proposal, validation, owner),
            documentType: "Memo", documentNumber: memoId,
            evidenceRef: $"proposal:{proposalId}");
        work.Save(_cacheDir);
        _logger.LogInformation(
            "Memo {MemoId} round {Round}: proposal {ProposalId} stored (valid: {Valid}).",
            memoId, round, proposalId, validation.Ok);
        return work.CurrentFile;
    }

    /// <summary>Answers (approve + chosen option) or declines (reject) one
    /// blocking-question card. Reason carries the question, Comment records
    /// the chosen option on approve. Approving re-drives the proposal with
    /// the answer in context; declining closes the memo: a declined block
    /// can never publish. Resolving a decided card returns it unchanged.</summary>
    public async Task<Decision> ResolvePolicyQuestionAsync(
        string memoId, string decisionId, bool approved, string decidedBy,
        string? chosenOption = null, CancellationToken ct = default)
    {
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown decision {decisionId} on memo {memoId}.");
        if (decision.Status != DecisionStatus.Pending)
        {
            _logger.LogInformation(
                "Decision {DecisionId} on memo {MemoId} already {Status}; replay returns it unchanged.",
                decisionId, memoId, decision.Status);
            return decision;
        }

        if (decision.Kind != DecisionKind.PolicyQuestion)
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} is a {decision.Kind} card, not a PolicyQuestion card.");
        }

        var question = FindQuestion(file, decision);
        var work = new MemoWork(file);
        Decision updated;
        if (!approved)
        {
            updated = work.Decide(decision, false, decidedBy, chosenOption);
            work.AddEvent(CaseEventKind.Note,
                $"Blocking question {question.QuestionId} declined by {decidedBy}: " +
                "the memo closes without publication; nothing publishes past a declined block.",
                documentType: "Memo", documentNumber: memoId);
            work.Save(_cacheDir);
            return updated;
        }

        if (string.IsNullOrWhiteSpace(chosenOption)
            || !question.Options.Contains(chosenOption, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} must choose one of the offered options " +
                $"[{string.Join("; ", question.Options)}]; got '{chosenOption ?? "missing"}'.");
        }

        updated = work.Decide(decision, true, decidedBy, chosenOption);
        work.AddEvent(CaseEventKind.Note,
            $"Blocking question {question.QuestionId} answered by {decidedBy}: {chosenOption}. Re-driving the proposal.",
            documentType: "Memo", documentNumber: memoId);
        work.Save(_cacheDir);
        await ProposeAsync(memoId, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Resolves one policy-approval card. Refuses stale cards, open
    /// or declined blocks, and invalid proposals before the engine ever sees
    /// them; then the engine validates the approval pin and, on approve,
    /// the matching publish path ships (parameter update when a conflict
    /// names the live rule, brand-new rule otherwise; contradictions refuse).
    /// Reject records the refusal. The card decides only after publish
    /// succeeds, so a publish refusal leaves it pending.</summary>
    public async Task<PolicyApprovalOutcome> ResolvePolicyApprovalAsync(
        string memoId, string decisionId, PolicyApprovalInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown decision {decisionId} on memo {memoId}.");
        if (decision.Status != DecisionStatus.Pending)
        {
            _logger.LogInformation(
                "Decision {DecisionId} on memo {MemoId} already {Status}; replay returns it unchanged.",
                decisionId, memoId, decision.Status);
            var activation = file.Activations.FirstOrDefault(a =>
                string.Equals(a.ProposalHash, decision.DocumentNumber, StringComparison.Ordinal));
            return new PolicyApprovalOutcome(decision, activation);
        }

        if (decision.Kind != DecisionKind.PolicyApproval)
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} is a {decision.Kind} card, not a PolicyApproval card.");
        }

        var current = file.Proposals.LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} has no proposal to resolve against.");
        if (!string.Equals(decision.DocumentNumber, current.Proposal.ProposalHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} pins proposal hash '{decision.DocumentNumber}' " +
                $"but the current proposal is {current.Proposal.ProposalId} ('{current.Proposal.ProposalHash}'); " +
                "the card is stale (a re-proposal superseded it) and cannot resolve.");
        }

        RequireApprovable(file, current, decisionId, memoId);

        if (string.IsNullOrWhiteSpace(input.Approver))
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} names no approver; the KB approval matrix names who must decide.");
        }

        var snapshot = await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        var approval = new RuleApproval(
            current.Proposal.ProposalHash, current.Proposal.KbVersion,
            input.Approver, DateTimeOffset.UtcNow,
            input.Approved ? RuleApprovalOutcome.Approved : RuleApprovalOutcome.Rejected,
            input.EffectiveAt, input.Applicability, current.Validation.Conflicts,
            input.EditedParams, input.Comment);
        var gate = await _compliance.ValidateApprovalAsync(
            approval, current.Proposal, snapshot.KbVersion, ct).ConfigureAwait(false);
        gate.EnsureValid();

        var work = new MemoWork(MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Memo {memoId} vanished during approval."));
        if (!input.Approved)
        {
            var refused = work.Decide(decision, false, input.Approver, input.Comment);
            work.AddEvent(CaseEventKind.Note,
                $"Proposal {current.Proposal.ProposalId} refused by {input.Approver}: " +
                $"{input.Comment ?? "no reason given"}. Nothing publishes.",
                documentType: "Memo", documentNumber: memoId);
            work.Save(_cacheDir);
            return new PolicyApprovalOutcome(refused, null);
        }

        var publication = await PublishAsync(
            memoId, current, approval, input, ct).ConfigureAwait(false);
        work.AddActivation(publication.Activation);
        var decided = work.Decide(decision, true, input.Approver, input.Comment);
        work.AddEvent(CaseEventKind.Note,
            $"Proposal {current.Proposal.ProposalId} approved by {input.Approver} and published as " +
            $"{publication.Activation.RuleId} in pack {publication.Activation.PackId} " +
            $"{publication.Activation.PriorPackVersion} -> {publication.Activation.NewPackVersion}, " +
            $"effective {publication.Activation.EffectiveAt:yyyy-MM-dd} ({publication.Activation.Applicability})." +
            (input.EditedParams is null ? string.Empty : " Approver-edited params shipped."),
            documentType: "Pack", documentNumber: publication.Activation.PackId,
            evidenceRef: publication.FileName);
        work.Save(_cacheDir);
        return new PolicyApprovalOutcome(decided, publication.Activation);
    }

    /// <summary>Rolls back the memo's latest publication: a new pack version
    /// restoring the named prior version's parameters. History is untouched;
    /// the audit trail shows both versions. Refuses loudly when the memo has
    /// nothing to roll back or the engine cannot restore the version.</summary>
    public async Task<RuleActivation> RequestRollbackAsync(
        string memoId, string restoreVersion, string reason, string approver,
        CancellationToken ct = default,
        DateTimeOffset? effectiveAt = null,
        RuleApplicability? applicability = null)
    {
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var latest = file.Activations.LastOrDefault()
            ?? throw new InvalidOperationException(
                $"Memo {memoId} has no publication to roll back.");
        if (string.IsNullOrWhiteSpace(restoreVersion))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} rollback names no version to restore.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} rollback records no reason; rollbacks require one.");
        }

        if (string.IsNullOrWhiteSpace(approver))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} rollback names no approver.");
        }

        var now = DateTimeOffset.UtcNow;
        var activationId = $"{memoId}-rb{file.Activations.Count + 1}";
        var publication = await _compliance.PublishRollbackAsync(
            latest.PackId, restoreVersion, memoId, approver, effectiveAt ?? now,
            applicability ?? latest.Applicability, latest.ProposalHash, reason,
            latest.RuleId, activationId, now, ct).ConfigureAwait(false);

        var work = new MemoWork(MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Memo {memoId} vanished during rollback."));
        work.AddActivation(publication.Activation);
        work.AddEvent(CaseEventKind.Note,
            $"Pack {publication.Activation.PackId} rolled back to {restoreVersion} " +
            $"as {publication.Activation.NewPackVersion} per {approver}: {reason}.",
            documentType: "Pack", documentNumber: publication.Activation.PackId,
            evidenceRef: publication.FileName);
        work.Save(_cacheDir);
        return publication.Activation;
    }

    private async Task ComputeWhatIfAsync(string memoId, CancellationToken ct)
    {
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var current = file.Proposals.LastOrDefault()
            ?? throw new InvalidOperationException($"Memo {memoId} has no proposal to evaluate.");
        if (current.Proposal.ProposedRule is null)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} is NotMappable: the what-if report is not computed (no mappable rule).");
        }

        var snapshot = await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        if (!string.Equals(snapshot.KbVersion, current.Proposal.KbVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Memo {memoId} what-if is refused: the proposal pins KB '{current.Proposal.KbVersion}' " +
                $"but the current KB is '{snapshot.KbVersion}'. Re-ground the proposal before approval can proceed.");
        }

        var history = await _gather.GatherHistoryAsync(snapshot, ct).ConfigureAwait(false);
        var (pipeline, caveats) = await _gather.GatherPipelineAsync(
            snapshot, current.Proposal, ct).ConfigureAwait(false);
        var newConfig = _gather.BuildNewConfig(current.Proposal);
        var (oldConfig, oldRuleId, baselineCaveat) = _gather.BuildOldConfig(
            current.Proposal, current.Validation, snapshot);
        var allCaveats = baselineCaveat is null ? caveats : caveats.Append(baselineCaveat).ToList();

        // Pack versions are engine-owned; the agent knows none. The empty map
        // is explicit (lead request: the seam should source them internally).
        var report = await _compliance.ComputeWhatIfAsync(
            history, pipeline, oldConfig, newConfig, oldRuleId,
            snapshot.FiscalCalendar, current.Proposal.ProposalHash, current.Proposal.KbVersion,
            new Dictionary<string, string>(), DateTimeOffset.UtcNow,
            pipeline.Count > 0 ? pipeline : null, ct).ConfigureAwait(false);

        var work = new MemoWork(MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Memo {memoId} vanished during what-if."));
        work.SetWhatIf(current.Proposal.ProposalHash, report);
        work.AddEvent(CaseEventKind.Note,
            DescribeWhatIf(report, history.Count, pipeline.Count, allCaveats),
            documentType: "Memo", documentNumber: memoId,
            amount: report.Totals.Currency is null ? null : report.Totals.AmountFlipped,
            currency: report.Totals.Currency);
        work.Save(_cacheDir);
    }

    private void RaiseApproval(string memoId)
    {
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Unknown memo {memoId}.");
        var current = file.Proposals.LastOrDefault()
            ?? throw new InvalidOperationException($"Memo {memoId} has no proposal to approve.");
        var work = new MemoWork(file);
        var card = work.FindOrCreateDecision(
            DecisionKind.PolicyApproval,
            $"Approve rule proposal {current.Proposal.ProposalId}",
            current.PolicyOwner,
            MemoPicker.ApprovalDocumentType,
            current.Proposal.ProposalHash,
            current.WhatIf?.Totals.Currency is null ? null : current.WhatIf.Totals.AmountFlipped,
            current.WhatIf?.Totals.Currency,
            BuildApprovalReason(file.Memo, current),
            evidenceRefs: [$"memo:{memoId}", $"proposal:{current.Proposal.ProposalId}"]);
        work.AddEvent(CaseEventKind.Note,
            $"Policy approval card {card.DecisionId} raised to {current.PolicyOwner} " +
            $"for proposal {current.Proposal.ProposalId}.",
            documentType: "Memo", documentNumber: memoId);
        work.Save(_cacheDir);
    }

    private async Task<PackPublication> PublishAsync(
        string memoId, MemoProposalRecord current, RuleApproval approval,
        PolicyApprovalInput input, CancellationToken ct)
    {
        var rule = current.Proposal.ProposedRule
            ?? throw new InvalidOperationException(
                $"Memo {memoId} is NotMappable: there is no rule to publish. " +
                "Record Rejected to close the memo, or re-propose once the blocking questions are answered.");
        var contradictions = current.Validation.Conflicts
            .Where(c => c.Kind == RuleConflictKind.Contradicts)
            .ToList();
        if (contradictions.Count > 0)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} publish is refused: the proposal contradicts " +
                $"{string.Join(", ", contradictions.Select(c => c.RuleId))} " +
                $"({string.Join("; ", contradictions.Select(c => c.Detail))}). " +
                "A contradiction cannot ship; reject the card or re-propose.");
        }

        var evidence = (input.TestEvidence ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        var file = MemoStore.Load(_cacheDir, memoId)
            ?? throw new InvalidOperationException($"Memo {memoId} vanished during publish.");
        var activationId = $"{memoId}-a{file.Activations.Count + 1}";
        var now = DateTimeOffset.UtcNow;

        var update = current.Validation.Conflicts.FirstOrDefault(c =>
            c.Kind is RuleConflictKind.Tightens or RuleConflictKind.Loosens or RuleConflictKind.Subsumes);
        if (update is not null)
        {
            var packId = input.PackId ?? PackFromRuleId(update.RuleId, memoId);
            _logger.LogInformation(
                "Memo {MemoId}: publishing an update to {RuleId} in pack {PackId} ({Conflict}).",
                memoId, update.RuleId, packId, update.Kind);
            return await _compliance.PublishRuleUpdateAsync(
                packId, current.Proposal, current.Validation, approval, evidence,
                update.RuleId, activationId, now, ct).ConfigureAwait(false);
        }

        var newPackId = input.PackId ?? PackForRuleType(rule.RuleType);
        var ruleId = input.RuleId ?? MintRuleId(newPackId, memoId);
        var spec = input.NewRuleSpec
            ?? throw new InvalidOperationException(
                $"Memo {memoId} publish is refused: a brand-new rule needs the approver's title, tier, and document label.");
        _logger.LogInformation(
            "Memo {MemoId}: publishing new rule {RuleId} in pack {PackId}.",
            memoId, ruleId, newPackId);
        return await _compliance.PublishNewRuleAsync(
            newPackId, current.Proposal, current.Validation, approval, evidence,
            ruleId, spec, activationId, now, ct).ConfigureAwait(false);
    }

    /// <summary>Routes a rule type to its pack family. DocumentPresence
    /// defaults to CORP (company-declared kinds); the engine refines by the
    /// live catalog and refuses a wrong pack loudly, naming the right one —
    /// retry with an explicit packId then.</summary>
    public static string PackForRuleType(string ruleType)
    {
        return ruleType switch
        {
            MemoRuleTypes.OriginShareCap or MemoRuleTypes.VendorShareCap => "CORP",
            MemoRuleTypes.ScreeningMatch => "SCR",
            MemoRuleTypes.DockMarkPresence or MemoRuleTypes.DockHandlingPresence or MemoRuleTypes.DamageReview => "BAT-LI",
            MemoRuleTypes.PrePayBlockOnOpen => "ELEC",
            MemoRuleTypes.DocumentPresence => "CORP",
            _ => throw new InvalidOperationException(
                $"Publish is refused: rule type '{ruleType}' names no pack family."),
        };
    }

    /// <summary>Routes an update to its pack from the live rule id
    /// (PACK-NN rule ids name their pack prefix).</summary>
    public static string PackFromRuleId(string ruleId, string memoId)
    {
        var dash = ruleId.LastIndexOf('-');
        if (dash <= 0)
        {
            throw new InvalidOperationException(
                $"Memo {memoId} publish is refused: live rule id '{ruleId}' names no pack prefix; " +
                "pass packId explicitly.");
        }

        return ruleId[..dash];
    }

    /// <summary>Mints a deterministic new-rule id from pack plus memo. The
    /// engine refuses collisions loudly; retry with an explicit ruleId then.</summary>
    public static string MintRuleId(string packId, string memoId)
    {
        var raw = $"{packId}-{memoId}".ToUpperInvariant();
        return new string(raw.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' ? c : '-').ToArray());
    }

    private static void RequireApprovable(
        MemoFile file, MemoProposalRecord current, string decisionId, string memoId)
    {
        if (!current.Validation.Ok)
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} cannot resolve: the current proposal " +
                $"{current.Proposal.ProposalId} failed validation:\n- " +
                string.Join("\n- ", current.Validation.Errors));
        }

        var blocking = MemoPicker.CurrentBlocking(file, current);
        if (blocking.Count > 0)
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} cannot resolve: {blocking.Count} blocking question(s) " +
                $"are still open ({string.Join(", ", blocking.Select(d => d.DecisionId))}); " +
                "nothing publishes past a block.");
        }

        var declined = MemoPicker.CurrentDeclined(file, current);
        if (declined.Count > 0)
        {
            throw new InvalidOperationException(
                $"Decision {decisionId} on memo {memoId} cannot resolve: blocking question(s) " +
                $"{string.Join(", ", declined.Select(d => d.DecisionId))} were declined; " +
                "the memo is closed without publication.");
        }
    }

    private static OpenQuestion FindQuestion(MemoFile file, Decision decision)
    {
        var docNumber = decision.DocumentNumber ?? string.Empty;
        var separator = docNumber.LastIndexOf(':');
        var proposalId = separator < 0 ? string.Empty : docNumber[..separator];
        var questionId = separator < 0 ? docNumber : docNumber[(separator + 1)..];
        var record = file.Proposals.FirstOrDefault(p =>
            string.Equals(p.Proposal.ProposalId, proposalId, StringComparison.Ordinal));
        var question = record?.Proposal.Questions.FirstOrDefault(q =>
            string.Equals(q.QuestionId, questionId, StringComparison.Ordinal));
        if (question is null)
        {
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} on memo {file.Memo.MemoId} names unknown question '{docNumber}'.");
        }

        return question;
    }

    private static void SupersedeOlderQuestions(MemoWork work, MemoFile file, string currentProposalId)
    {
        foreach (var stale in file.Decisions.Where(d =>
            d.Kind == DecisionKind.PolicyQuestion
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.DocumentType, MemoPicker.QuestionDocumentType, StringComparison.Ordinal)
            && !(d.DocumentNumber ?? string.Empty).StartsWith(
                currentProposalId + ":", StringComparison.Ordinal)).ToList())
        {
            work.Decide(stale, false, RecordSource.P2pAgent, $"Superseded by {currentProposalId}.");
            work.AddEvent(CaseEventKind.Note,
                $"Question card {stale.DecisionId} superseded by {currentProposalId}: " +
                "it asked about an older round and no longer blocks.",
                documentType: "Memo", documentNumber: file.Memo.MemoId);
        }
    }

    private static IReadOnlyList<(string Question, string Answer)> PriorAnswers(MemoFile file) =>
        file.Decisions
            .Where(d => d.Kind == DecisionKind.PolicyQuestion
                && d.Status == DecisionStatus.Approved
                && d.Reason is not null
                && d.Comment is not null)
            .Select(d => (d.Reason!, d.Comment!))
            .ToList();

    private static string DescribeProposal(
        RuleProposal proposal, ProposalValidation validation, string owner)
    {
        var shape = proposal.ProposedRule is not null
            ? $"{proposal.ProposedRule.RuleType} at the {proposal.ProposedRule.Gate} gate " +
              $"({string.Join(", ", proposal.ProposedRule.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))})"
            : $"NotMappable (nearest {proposal.NotMappable!.NearestType}: {proposal.NotMappable.WhyNot})";
        var verdict = validation.Ok
            ? proposal.ProposedRule is null
                ? "validated; nothing to publish."
                : validation.Conflicts.Count == 0
                    ? "validated with no live-pack conflicts."
                    : $"validated with conflicts: {string.Join("; ", validation.Conflicts.Select(c => $"{c.RuleId} {c.Kind}"))}."
            : "REJECTED:\n- " + string.Join("\n- ", validation.Errors);
        var questions = proposal.Questions.Count(q => q.Blocking);
        return $"Proposal {proposal.ProposalId} by Spark for {owner}: {shape} {verdict} " +
            $"{proposal.Statements.Count} statement(s), {proposal.Resolutions.Count} resolution(s), " +
            $"{questions} blocking question(s).";
    }

    private static string DescribeWhatIf(
        WhatIfReport report, int historyRows, int pipelineRows, IReadOnlyList<string> caveats)
    {
        var text = $"What-if {report.ProposalHash[..Math.Min(8, report.ProposalHash.Length)]}: " +
            $"{report.HistoryFlips.Count} history flip(s) over {historyRows} row(s) ({report.HistoryLabel}), " +
            $"{report.PipelineRows.Count} pipeline row(s) over {pipelineRows} gathered, " +
            $"{report.Totals.FlipsToBlock} to block / {report.Totals.FlipsToPass} to pass, " +
            $"unviable={report.Unviable}.";
        if (caveats.Count > 0)
        {
            text += " Caveats: " + string.Join(" ", caveats);
        }

        return text;
    }

    private static string BuildApprovalReason(PolicyMemo memo, MemoProposalRecord record)
    {
        var proposal = record.Proposal;
        var lines = new List<string> { $"Memo excerpt: {memo.Excerpt}" };
        foreach (var statement in proposal.Statements)
        {
            lines.Add($"- \"{statement.Quote}\" => {statement.Interpretation} (confidence {statement.Confidence})");
        }

        foreach (var resolution in proposal.Resolutions)
        {
            lines.Add($"- \"{resolution.Phrase}\" => {resolution.EntityId} " +
                $"(kb:{resolution.Citation.Section}#{resolution.Citation.EntryId}@{resolution.Citation.KbVersion})");
        }

        if (proposal.ProposedRule is not null)
        {
            var rule = proposal.ProposedRule;
            lines.Add($"Proposed rule: {rule.RuleType} at the {rule.Gate} gate " +
                $"({string.Join(", ", rule.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))}).");
            lines.Add(record.Validation.Conflicts.Count == 0
                ? "Conflicts: none against the live packs."
                : "Conflicts: " + string.Join("; ",
                    record.Validation.Conflicts.Select(c => $"{c.RuleId} {c.Kind}: {c.Detail}")));
            if (record.WhatIf is not null)
            {
                var report = record.WhatIf;
                lines.Add($"What-if: {report.HistoryFlips.Count} history flip(s) ({report.HistoryLabel}), " +
                    $"{report.PipelineRows.Count} pipeline row(s), " +
                    $"{report.Totals.FlipsToBlock} to block / {report.Totals.FlipsToPass} to pass, " +
                    $"unviable={report.Unviable}.");
            }
            else
            {
                lines.Add("What-if: not computed yet.");
            }
        }
        else
        {
            lines.Add($"NotMappable: nearest {proposal.NotMappable!.NearestType}; {proposal.NotMappable.WhyNot} " +
                $"(missing: {proposal.NotMappable.MissingCapability}). What-if: not computed (no mappable rule). " +
                "Approve is refused; reject closes the memo.");
        }

        lines.Add("Effective datetime and applicability are set at decision time.");
        return string.Join("\n", lines);
    }

    private void RecordFailureNote(string memoId, MemoNextStep step, Exception ex)
    {
        try
        {
            var file = MemoStore.Load(_cacheDir, memoId);
            if (file is null)
            {
                return;
            }

            var work = new MemoWork(file);
            work.AddEvent(CaseEventKind.Note,
                $"Step {step} failed: {ex.Message}",
                documentType: "Memo", documentNumber: memoId);
            work.Save(_cacheDir);
        }
        catch (Exception noteEx)
        {
            _logger.LogError(noteEx, "Memo {MemoId}: could not record the failure note.", memoId);
        }
    }
}
