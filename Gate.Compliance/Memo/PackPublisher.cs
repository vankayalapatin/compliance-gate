// Pack publish, stage 7 of docs/company-kb/MEMO-PIPELINE.md. Validated plus
// approved plus tested proposals ship as a version-bumped pack JSON with
// source = memo id plus approver plus effective date. Prior pack versions
// are retained (the new file lands beside the old one; a new pack never
// edits an existing pack file in place) and the loader enforces the
// highest version. Rollback is a new version restoring prior parameters;
// history is untouched and the audit trail shows both versions with their
// approvers and effective ranges.
//
// Pure by design: BuildUpdate/BuildNewRule/BuildRollback return the file
// name plus JSON plus activation record; only PublishToDirectory touches
// disk, and only to create the new file (refusing to overwrite).
using System.Text.Json;
using System.Text.Json.Serialization;
using Gate.Contracts;

namespace Gate.Compliance;

// NewRuleSpec and PackPublication live in Gate.Contracts; this file
// holds the publisher only.

public static class PackPublisher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Publishes a parameter change to a live rule (e.g. tightening
    /// CORP-02's cap). The rule must already exist in the prior pack JSON.</summary>
    public static PackPublication BuildUpdate(
        string priorPackJson,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        IReadOnlyList<RuleDefinition>? liveRules = null)
    {
        var (prior, effectiveParams) = RequirePublishable(
            priorPackJson, proposal, validation, approval, testEvidence, activationId);
        var ruleType = proposal.ProposedRule!.RuleType;
        RequirePackForType(ruleType, prior.Pack, effectiveParams, liveRules);

        var index = prior.Rules.FindIndex(r => r.Id == ruleId);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Cannot publish an update to rule '{ruleId}': pack '{prior.Pack}' {prior.Version} carries no such rule. " +
                "New rules publish via BuildNewRule with a rule spec.");
        }

        var live = prior.Rules[index];
        var rules = prior.Rules.ToList();
        rules[index] = live with
        {
            Source = UpdateSource(live.Source, proposal.MemoId, approval),
            RuleType = ruleType,
            Params = SortedParams(effectiveParams),
        };

        var config = prior.Config;
        if (string.Equals(prior.Pack, "CORP", StringComparison.Ordinal))
        {
            config = ApplyCorpConfig(
                prior, ruleType, effectiveParams, ruleId,
                $"Board memo {proposal.MemoId} effective {approval.EffectiveAt:yyyy-MM-dd} approved by {approval.Approver}");
        }

        return FinishPublication(prior, rules, config, proposal, approval, ruleId, activationId, publishedAt);
    }

    /// <summary>Publishes a brand-new rule into a pack. The rule id must not
    /// already exist in the prior pack JSON.</summary>
    public static PackPublication BuildNewRule(
        string priorPackJson,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        NewRuleSpec spec,
        string activationId,
        DateTimeOffset publishedAt,
        IReadOnlyList<RuleDefinition>? liveRules = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Title);

        var (prior, effectiveParams) = RequirePublishable(
            priorPackJson, proposal, validation, approval, testEvidence, activationId);
        var ruleType = proposal.ProposedRule!.RuleType;
        RequirePackForType(ruleType, prior.Pack, effectiveParams, liveRules);

        if (prior.Rules.Any(r => r.Id == ruleId))
        {
            throw new InvalidOperationException(
                $"Cannot publish new rule '{ruleId}': pack '{prior.Pack}' {prior.Version} already carries it. " +
                "Parameter changes publish via BuildUpdate.");
        }

        string? docKind = null;
        if (string.Equals(ruleType, MemoRuleRegistry.DocumentPresence, StringComparison.Ordinal))
        {
            docKind = effectiveParams["docKind"];
            if (string.IsNullOrWhiteSpace(spec.DocumentLabel))
            {
                throw new InvalidOperationException(
                    $"Cannot publish new DocumentPresence rule '{ruleId}' without a document label in the rule spec.");
            }
        }

        var rules = prior.Rules.ToList();
        rules.Add(new MutableRule(
            ruleId, spec.Tier, ToComplianceGate(proposal.ProposedRule.Gate),
            spec.Title, NewRuleSource(proposal.MemoId, approval), Enforced: true,
            docKind, spec.DocumentLabel, ruleType,
            SortedParams(effectiveParams)));

        return FinishPublication(prior, rules, prior.Config, proposal, approval, ruleId, activationId, publishedAt);
    }

    /// <summary>Rollback: a new version restoring prior parameters. The
    /// restored rules/config come from the chosen prior pack JSON; the new
    /// version, provenance, and activation record the rollback decision.
    /// History is untouched.</summary>
    public static PackPublication BuildRollback(
        string currentPackJson,
        string restorePackJson,
        string memoId,
        string approver,
        DateTimeOffset effectiveAt,
        RuleApplicability applicability,
        string proposalHash,
        string reason,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        var current = ParsePack(currentPackJson, "current");
        var restore = ParsePack(restorePackJson, "restore");
        if (!string.Equals(current.Pack, restore.Pack, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot roll back pack '{current.Pack}' from '{restore.Pack} {restore.Version}'; packs must match.");
        }

        if (current.Config is null != restore.Config is null)
        {
            throw new InvalidOperationException(
                $"Cannot roll back pack '{current.Pack}': config shape differs between {current.Version} and {restore.Version}.");
        }

        var newVersion = PackVersions.Next(current.Version);
        var rules = restore.Rules.Select(r => r with
        {
            Source = $"Rollback to {restore.Version} per memo {memoId} approved by {approver} " +
                $"effective {effectiveAt:yyyy-MM-dd}: {reason}. Prior source: {r.Source}",
        }).ToList();

        var pack = new MutablePack(
            current.Pack, newVersion, rules, restore.Config,
            new MutableProvenance(memoId, approver, effectiveAt.ToString("O")),
            restore.Owner, restore.Adopted);
        return new PackPublication(
            FileNameFor(current.Pack, newVersion),
            JsonSerializer.Serialize(pack, JsonOpts),
            new RuleActivation(
                activationId, ruleId, current.Pack, newVersion, current.Version,
                memoId, approver, effectiveAt, applicability, proposalHash, publishedAt));
    }

    /// <summary>Writes the publication into a directory, creating the new
    /// file. Refuses to overwrite: a new pack never edits an existing pack
    /// file in place, and neither does its writer.</summary>
    public static string PublishToDirectory(PackPublication publication, string directory)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, publication.FileName);
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Refusing to publish over existing pack file '{path}'; publications create new versions, never overwrite.");
        }

        File.WriteAllText(path, publication.Json);
        return path;
    }

    public static string FileNameFor(string packId, string newVersion) =>
        $"{packId.ToLowerInvariant()}.{newVersion}.json";

    private static (MutablePack Prior, IReadOnlyDictionary<string, string> EffectiveParams) RequirePublishable(
        string priorPackJson,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string activationId)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrWhiteSpace(activationId);

        if (!validation.Ok)
        {
            throw new InvalidOperationException(
                "Cannot publish an unvalidated proposal:\n- " + string.Join("\n- ", validation.Errors));
        }

        if (proposal.ProposedRule is null)
        {
            throw new InvalidOperationException("Cannot publish a NotMappable proposal: there is no rule to ship.");
        }

        if (approval.Outcome != RuleApprovalOutcome.Approved)
        {
            throw new InvalidOperationException(
                $"Cannot publish: the approval outcome is {approval.Outcome}, not Approved.");
        }

        if (!string.Equals(approval.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot publish: approval pins proposal hash '{approval.ProposalHash}' but the proposal hashes " +
                $"'{proposal.ProposalHash}'.");
        }

        if (!string.Equals(approval.KbVersion, proposal.KbVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot publish: approval pins KB '{approval.KbVersion}' but the proposal pins '{proposal.KbVersion}'. " +
                "Re-ground before publishing.");
        }

        if (testEvidence is null || testEvidence.Count == 0 || testEvidence.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Cannot publish an untested proposal: at least one passing test name covering the rule is required as evidence.");
        }

        var effective = approval.EditedParams ?? proposal.ProposedRule.Params;
        var errors = MemoRuleRegistry.ValidateParams(proposal.ProposedRule.RuleType, effective);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot publish: the effective params fail registry validation: " + string.Join("; ", errors));
        }

        return (ParsePack(priorPackJson, "prior"), effective);
    }

    private static PackPublication FinishPublication(
        MutablePack prior,
        List<MutableRule> rules,
        MutableConfig? config,
        RuleProposal proposal,
        RuleApproval approval,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt)
    {
        var newVersion = PackVersions.Next(prior.Version);
        var pack = new MutablePack(
            prior.Pack, newVersion, rules, config,
            new MutableProvenance(proposal.MemoId, approval.Approver, approval.EffectiveAt.ToString("O")),
            prior.Owner, prior.Adopted);
        return new PackPublication(
            FileNameFor(prior.Pack, newVersion),
            JsonSerializer.Serialize(pack, JsonOpts),
            new RuleActivation(
                activationId, ruleId, prior.Pack, newVersion, prior.Version,
                proposal.MemoId, approval.Approver, approval.EffectiveAt,
                approval.Applicability, proposal.ProposalHash, publishedAt,
                proposal.KbVersion));
    }

    private static string UpdateSource(string priorSource, string memoId, RuleApproval approval) =>
        $"Board memo {memoId} effective {approval.EffectiveAt:yyyy-MM-dd} approved by {approval.Approver}. " +
        $"Prior source: {priorSource}";

    private static string NewRuleSource(string memoId, RuleApproval approval) =>
        $"Board memo {memoId} effective {approval.EffectiveAt:yyyy-MM-dd} approved by {approval.Approver}.";

    private static MutableConfig ApplyCorpConfig(
        MutablePack prior,
        string ruleType,
        IReadOnlyDictionary<string, string> effectiveParams,
        string ruleId,
        string policySource)
    {
        if (prior.Config is null)
        {
            throw new InvalidOperationException(
                $"Cannot publish {ruleType} to pack '{prior.Pack}': the CORP config section is missing.");
        }

        if (string.Equals(ruleType, MemoRuleRegistry.OriginShareCap, StringComparison.Ordinal)
            && string.Equals(ruleId, RuleConflictDetector.CorpOriginRuleId, StringComparison.Ordinal))
        {
            return prior.Config with
            {
                OriginCap = ParseCap(effectiveParams["cap"]),
                PolicySource = policySource,
            };
        }

        if (string.Equals(ruleType, MemoRuleRegistry.VendorShareCap, StringComparison.Ordinal)
            && string.Equals(ruleId, RuleConflictDetector.CorpVendorRuleId, StringComparison.Ordinal))
        {
            return prior.Config with
            {
                VendorCap = ParseCap(effectiveParams["cap"]),
                PolicySource = policySource,
            };
        }

        throw new InvalidOperationException(
            $"Cannot publish {ruleType} as rule '{ruleId}': share-cap updates target " +
            $"{RuleConflictDetector.CorpOriginRuleId} (origin) or {RuleConflictDetector.CorpVendorRuleId} (vendor).");
    }

    private static decimal ParseCap(string raw) =>
        decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Each registry type publishes to exactly one pack family.
    /// DocumentPresence follows the live packs' own kind map (derived from
    /// the live catalog, never hardcoded): known doc kinds join their home
    /// pack, company-declared kinds join CORP.</summary>
    private static void RequirePackForType(
        string ruleType, string packId, IReadOnlyDictionary<string, string> paramsMap,
        IReadOnlyList<RuleDefinition>? liveRules = null)
    {
        var upper = packId.ToUpperInvariant();
        string expected = ruleType switch
        {
            MemoRuleRegistry.OriginShareCap or MemoRuleRegistry.VendorShareCap => "CORP",
            MemoRuleRegistry.ScreeningMatch => "SCR",
            MemoRuleRegistry.DockMarkPresence or MemoRuleRegistry.DockHandlingPresence or MemoRuleRegistry.DamageReview => "BAT-LI",
            MemoRuleRegistry.PrePayBlockOnOpen => "ELEC",
            MemoRuleRegistry.DocumentPresence => PackForDocKind(paramsMap["docKind"], liveRules),
            _ => throw new InvalidOperationException($"Cannot publish unknown rule type '{ruleType}'."),
        };

        if (!string.Equals(upper, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot publish {ruleType} to pack '{packId}': it belongs to pack '{expected}'.");
        }
    }

    private static string PackForDocKind(string docKind, IReadOnlyList<RuleDefinition>? liveRules = null)
    {
        var home = (liveRules ?? RuleCatalog.Rules).FirstOrDefault(r =>
            r.DocumentKind is not null && string.Equals(r.DocumentKind, docKind, StringComparison.Ordinal));
        return home?.Pack.ToUpperInvariant() ?? "CORP";
    }

    private static ComplianceGate ToComplianceGate(MemoRuleGate gate) => gate switch
    {
        MemoRuleGate.Vendor => ComplianceGate.Vendor,
        MemoRuleGate.Dock => ComplianceGate.Dock,
        MemoRuleGate.PrePay => ComplianceGate.PrePay,
        _ => throw new InvalidOperationException($"Unknown memo gate '{gate}'."),
    };

    /// <summary>Copies params into ordinal key order so published JSON
    /// serializes byte-identical on replay.</summary>
    private static Dictionary<string, string> SortedParams(IReadOnlyDictionary<string, string> effectiveParams)
    {
        var sorted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in effectiveParams.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sorted[key] = effectiveParams[key];
        }

        return sorted;
    }

    private static MutablePack ParsePack(string json, string role)
    {
        MutablePack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<MutablePack>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The {role} pack JSON is not valid JSON.", ex);
        }

        if (pack is null || string.IsNullOrWhiteSpace(pack.Pack) || pack.Rules is null)
        {
            throw new InvalidOperationException($"The {role} pack JSON deserialized to null.");
        }

        try
        {
            PackVersions.Parse(pack.Version);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"The {role} pack carries an invalid version: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(pack.Owner))
        {
            throw new InvalidOperationException(
                $"The {role} pack '{pack.Pack}' {pack.Version} carries no owner; publications carry the pack adoption forward.");
        }

        if (!DateOnly.TryParse(pack.Adopted, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            throw new InvalidOperationException(
                $"The {role} pack '{pack.Pack}' {pack.Version} carries no parseable adopted date (got '{pack.Adopted ?? "<missing>"}'); " +
                "publications carry the pack adoption forward.");
        }

        return pack;
    }

    private sealed record MutablePack(
        string Pack,
        string Version,
        List<MutableRule> Rules,
        MutableConfig? Config,
        MutableProvenance? Provenance,
        string Owner,
        string Adopted);

    private sealed record MutableRule(
        string Id,
        RuleTier Tier,
        ComplianceGate Gate,
        string Title,
        string Source,
        bool Enforced,
        string? DocumentKind,
        string? DocumentLabel,
        string? RuleType,
        Dictionary<string, string>? Params);

    private sealed record MutableConfig(decimal OriginCap, decimal VendorCap, string PolicySource);

    private sealed record MutableProvenance(string MemoId, string Approver, string EffectiveAt);
}
