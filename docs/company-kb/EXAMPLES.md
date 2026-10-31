# Example Memos (Design)

Three memos showing the pipeline end to end. All persons are scenario
users: Maya Planner (requester), Dana Director (approver; wears the COO
hat — no separate COO person exists), Del Delegate (Dana's dated
delegate), Amy AP Clerk, Finn Finance (payment approver). "The board" is
a body, not a person. All numbers are fictional and use the demo history
(10,000 cells, CN 5,900) for illustration.

Share-cap semantics: the share checked is the **awarded** country's own
share. A 1,200-cell award to V-A CN gives 7,100/11,200 = 63.4% CN; to
V-B MX gives 5,000/11,200 = **44.6% MX** (the CN residual of 52.7% is
irrelevant to that verdict).

## 1. Enforceable cap tightening — zero viable supplier

Board memo, 2026-11-01: *"Effective 2026-12-01, no more than 40% of
critical battery cells from any single country, trailing 12 months.
Policy owner: Dana Director."*

- Spark proposes: "critical battery cells" → part BAT-1
  (`IsCritical=true`), citing `kb:items#BAT-1`; "any single country" →
  `OriginShareCap` per awarded country, citing
  `kb:sourcingPolicies#origin-cap` + the CORP-02 precedent; params
  `{cap: 0.40, windowMonths: 12, partScope: critical}`. No blocking
  questions — the phrasing is exact.
- C# validates citations, confirms registry membership, flags
  "tightens CORP-02 60% → 40%."
- What-if on corrected math: V-A CN award → 63.4% CN, block; V-B MX
  award → 44.6% MX, block. **Zero viable supplier.** The report
  recommends phasing (50% then 40%) or a new-vendor exemption.
- Approval card to Dana Director (Del Delegate covers per delegation
  dates if Dana is out); Finn Finance sees the money impact on the card.
  Mode (a) default: open/draft POs evaluate at the next gate; approved
  POs are grandfathered + flagged.

## 2. Enforceable region rule — "Asian suppliers" ambiguity resolved

Board memo: *"Effective immediately, all POs to Asian suppliers for
critical components need a COO declaration on file before award. Owner:
Dana Director."*

- Spark proposes: "critical components" → critical parts, citing
  `kb:items#critical-flag`; "Asian suppliers" → country list from
  `kb:regionRules#asia@kbv3`, citing the definition — plus one
  **blocking** question: "KB Asia v3 = {CN, TW, ...}: confirm, or
  exclude TW?" Rule type `DocumentPresence(docKind: COO-Declaration)`,
  new rule id, no conflicts.
- C# validates; the memo waits until Dana Director answers the TW
  question.
- What-if then lists pipeline POs missing the declaration (vendor + gap
  each) and the history "would-have-waited" count.
- Approval by Dana Director; Amy AP Clerk sees the resulting invoice
  holds in her queue.

## 3. Unclear and unenforceable

Board note: *"We should prefer sustainable suppliers and be careful
about risky regions going forward."*

- Spark returns: no KB citation supports "sustainable" (no sourcing
  policy entry, no approved signal/source) or "risky regions" (no region
  list named); no measurable threshold ("prefer" = block? score?
  tiebreak?); no gate named.
- Outcome `NotMappable`: nearest types rejected with reasons
  (`VendorShareCap` — no share named; `ScreeningMatch` — no list named).
- Blocking questions to Dana Director (owner), Maya Planner cc'd as
  requester: part scope, gate, threshold semantics, region definition +
  source. Pipeline/history sections show "not computed — no mappable
  rule." Nothing publishes.
