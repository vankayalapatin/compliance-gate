# Example Memos (Design)

Four memos showing the pipeline end to end. All persons are scenario
users: Maya Planner (requester), Dana Director (approver; wears the COO
hat — no separate COO person exists), Del Delegate (Dana's dated
delegate), Amy AP Clerk, Finn Finance (payment approver). "The board" is
a body, not a person. All numbers are fictional and use the demo history
(10,000 cells, CN 5,900) for illustration. Memos 1–4 carry "observed
live" notes where a real Spark propose run verified the behavior; live
nearest-picks and question sets vary run to run, so the doc records the
outcome shape plus one observed instance each.

Share-cap semantics: the share checked is the **awarded** country's own
share. A 1,200-cell award to V-A CN gives 7,100/11,200 = 63.4% CN; to
V-B MX gives 5,000/11,200 = **44.6% MX** (the CN residual of 52.7% is
irrelevant to that verdict).

## 1. Enforceable cap tightening — zero viable supplier (observed live)

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
- Observed live through the approval-card step (`AwaitApproval`,
  `OriginShareCap` 0.40, Tightens CORP-02, validation ok, no
  questions). The what-if flips and approval action above are the
  designed continuation.

## 2. Region-scoped memo — NotMappable, two blocking questions (observed live)

Board memo: *"Effective immediately, all POs to Asian suppliers for
critical components need a COO declaration on file before award. Owner:
Dana Director."*

- Spark resolves: "critical components" → BAT-1
  (`kb:items#BAT-1@kbv1`); "Asian suppliers" → `asia`
  (`kb:regionRules#asia@kbv1`, {CN, TW}); owner Dana Director
  (`kb:approvalMatrix#sourcing-policy@kbv1`). Then it returns
  **NotMappable**: nearest type `DocumentPresence`, because
  `DocumentPresence` carries only `docKind` plus `partScope` and
  cannot limit enforcement to Asian suppliers; missing capability
  `Code` (a region/supplier-scope param the registry does not have).
  An earlier draft of this memo expected plain `DocumentPresence`;
  live Spark correctly refused the mapping.
- Two **blocking** questions: which KB document the "COO declaration"
  maps to (UN38.3 template, HR policy, or add it to the KB), and what
  "Asian suppliers" means for enforcement scope (KB asia {CN, TW}, a
  different list to be specified, or all suppliers with no region
  filter).
- C# validates the proposal shape (ok); the memo waits until Dana
  Director answers. Answering "all suppliers" would make the memo
  mappable as plain `DocumentPresence`; answering a country list
  keeps it waiting on a registry capability.
- No what-if, no approval, nothing publishes past the block.

## 3. Unclear and unenforceable (observed live)

Board note: *"We should prefer sustainable suppliers and be careful
about risky regions going forward."*

- Spark returns (confidence 0.2, zero resolutions): no KB citation
  supports "sustainable" (no sourcing policy entry, no approved
  signal/source) or "risky regions" (no region list named); no
  measurable threshold ("prefer" = block? score? tiebreak?); no gate
  named.
- Outcome `NotMappable`: one nearest type with reasons (observed:
  `OriginShareCap` — no cap, window, part scope, or country list to
  populate it; missing capability `Data`). The exact nearest pick
  varies run to run; the contract carries one with its why-not.
- Blocking questions to Dana Director (owner): region definition,
  threshold, part scope, and what defines "sustainable" — each with
  options drawn from live KB values (observed: asia |
  north-america; 0.60 | 0.50; BAT-1 | SCR-9 | GLASS-1; UN38.3 |
  hr-policy). A gate question may or may not appear; the question set
  varies, the blocking shape does not. Pipeline/history sections show
  "not computed — no mappable rule." Nothing publishes.

## 4. Real regulation text — UFLPA, NotMappable with a path (observed live)

Board memo carrying verbatim UFLPA §3 text (19 U.S.C. 1307, CBP
enforced): all goods mined, produced, or manufactured wholly or in
part in the Xinjiang Uyghur Autonomous Region are presumed made with
forced labor and prohibited from entry; "ensure our P2P flow blocks
in-scope goods"; owner Dana Director.

- Spark resolves what exists (BAT-1, the LE-1 legal entity, the
  `origin-cap` policy, all cited) and returns **NotMappable**:
  nearest `OriginShareCap`, because a share cap cannot enforce
  absolute prohibition of subnational-XUAR goods and the KB region
  list has no XUAR mapping; missing capability `Code`.
- Three **blocking** questions: how to resolve XUAR scope (CN-as-proxy,
  a new subnational origin feed, or no enforcement until mapped), part
  scope (all or critical), and which gate enforces (Vendor, Dock, or
  PrePay).
- C# validates the proposal shape (ok); the memo waits. This is the
  healthy NotMappable: the law is perfectly clear, and the report says
  exactly which data and code would make it enforceable.
