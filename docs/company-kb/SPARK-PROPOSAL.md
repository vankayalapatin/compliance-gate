# Spark Proposal Contract (Design)

Spark does the ambiguous-language understanding. C# checks every claim.
A proposal without valid citations is not a proposal — it is rejected.

## Spark input

- Memo text (verbatim).
- Pinned KB snapshot: version + hash + the sections relevant to the memo
  (items, region rules, sourcing policies, approval matrix).
- Rule-type registry descriptions (names, gates, parameter shapes — the
  closed list in [RULE-TYPES.md](RULE-TYPES.md)).

## Spark output (structured proposal JSON)

- `statements[]`: `{ quote, interpretation, confidence }`.
- `resolutions[]`: `{ phrase, kbId, citation }` — e.g. "critical battery
  cells" → `kb:items#BAT-1@kbv3`.
- `proposedRule`: `{ ruleType, gate, params }` — must name a registry
  type, or `NotMappable` with `nearestType`, `whyNot`, `missingCapability`
  (data, sensor, gate, or code).
- `questions[]`: `{ question, options[], blocking }` — blocking questions
  stop the pipeline until answered by the policy owner.
- `citations[]`: every resolution and every parameter cites
  `kb:<section>#<id>@<version>`.

## Worked ambiguity (illustrative)

- "Critical components" → proposes `IsCritical = true` items, citing
  `kb:items#critical-flag`. No question if the KB flag exists and is
  unambiguous.
- "Asian suppliers" → proposes the country list from
  `kb:regionRules#asia@kbv3`, citing the definition version, and raises
  one blocking question when the boundary matters: "KB Asia v3 =
  {CN, TW, ...}: confirm, or exclude TW?" with explicit options.
- "Prefer sustainable suppliers" → no KB entry, no measurable threshold:
  `NotMappable`, with blocking questions for scope, gate, threshold
  semantics, and signal source. See [EXAMPLES.md](EXAMPLES.md).

## C# validation (reject loudly, never repair silently)

- JSON schema conformance.
- Every citation resolves in the pinned KB version/hash. Unresolvable =
  hallucination = proposal rejected with the offending citation quoted.
- `ruleType` is in the closed registry; params in range for that type.
- Questions well-formed; at least one blocking question whenever any
  statement lacks a resolution.
- Proposal hash recorded; a KB version change after proposal requires
  re-grounding before approval can proceed.

## What Spark cannot do

Invent rule types, change the registry, publish rules, set effective
dates in the past without an explicit flag, approve its own output, or
bypass tests. Enforcement stays deterministic C# with pass/block/boundary
tests per rule, same bar as existing packs.
