# Memo Pipeline (Design)

Seven stages. Spark proposes, C# validates, human approves, C# enforces.

## Stages

1. **Ingest.** Memo lands in `company/docs/inbox/` with memo id, date,
   authoring body/person. External input only — ingestion never encodes
   policy meaning.
2. **Propose (Spark + KB).** Spark reads the memo against the pinned KB
   snapshot and returns a structured proposal: per-statement quote +
   interpretation, resolved entities as KB IDs, proposed rule type +
   parameters, confidence, open questions with options, and a KB citation
   for every resolution. Schema in
   [SPARK-PROPOSAL.md](SPARK-PROPOSAL.md).
3. **Validate (C#).** Checks: schema conformance; every citation resolves
   in the pinned KB version; rule type is in the closed registry
   ([RULE-TYPES.md](RULE-TYPES.md)); parameters in range; conflicts
   computed against live packs (tighten/loosen, contradiction,
   subsumption). Failures reject loudly with reasons — never silently
   repaired.
4. **Question (blocking).** Unresolvable phrases ("risky regions" with no
   KB list, missing threshold, unnamed gate) become blocking questions on
   a Decision card. The memo waits; nothing proceeds past a block.
5. **What-if (C#).** Read-only old-vs-new engine re-runs over the history
   and pipeline windows. Report shape in [WHAT-IF.md](WHAT-IF.md).
6. **Approve (human).** One `PolicyApproval` Decision card shows: memo
   excerpt, Spark's interpretation with citations, resolved vs
   unresolved, conflicts, what-if flips, applicability mode, effective
   datetime. The approver (from the KB matrix) approves, edits params
   within the registry, or rejects. Approval pins proposal hash + KB
   version + approver + timestamp.
7. **Publish (C#).** Validated + approved + tested proposals ship as a
   version-bumped pack JSON with source = memo id + approver + effective
   date. Prior pack versions are retained for audit and rollback.

## Timing shape (minutes / hours / days)

Ingest + propose + validate + what-if run in minutes. Human approval
takes hours to days. Publish runs in minutes. The slow step is the human,
never the machinery.

## KB edits (separate path)

KB facts do not travel the seven stages above: adds and edits publish
directly through the engine (`PublishKbUpdateAsync`), one space per
version, space-owner approved. See [KB-SCHEMA.md](KB-SCHEMA.md). Pinned
proposals keep their KB version; a KB change after proposal still
requires re-grounding.
