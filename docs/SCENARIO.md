# ComplianceGate — Demo Scenario (Planned)

Status: **Planned, not built.** This file is the single source of truth for the
demo seed. No code was changed to write it. All names, prices, quantities, and
dates are fictional. Currency is USD, `decimal`, code on each header.

Source of decisions: [PROJECT.md](/Users/nikhilvankayalapati/Desktop/ComplianceGate/PROJECT.md)
(Final). Product view: [PRODUCT.md](/Users/nikhilvankayalapati/Desktop/ComplianceGate/docs/PRODUCT.md).
Decision log: [DECISIONS.md](/Users/nikhilvankayalapati/Desktop/ComplianceGate/docs/DECISIONS.md).
Prior planner notes are folded in here; nothing else carries scenario numbers.

## 1. File layout (design, to be built after foundation lands)

- `scenarios/demo-v1.json` — the demo. Intents only, never fixed document
  numbers. Swapped without touching code via `GATE_SCENARIO`.
- `scenarios/alt-v1.json` — second case with different numbers. Same shape.
  Proves nothing is hard-coded (see §15).
- `scenarios/run-manifest.json` — generated, git-ignored. Records what the
  number sequences actually assigned on this run.
- Loader in `Gate.Erp/` fills sqlite on startup when empty, and on the reset
  endpoint (wipe plus reload). Sequences assign every `SO/PR/PO/ASN/RCP/INV/PAY`
  number at posting. Code follows returned IDs. Replay uses idempotency keys.
- ASN cartons and carton labels both read the manifest PO number. Labels always
  match whatever first PO the sequences gave. No `PO-VB-0147` style literal
  appears in code or in either scenario file.

## 2. Canonical demo values

| Item | Value | Why |
|---|---|---|
| Finished item | GLASS-1, 1300 ordered | Keeps code part IDs |
| Critical part | BAT-1, 1 per glasses | Keeps code BOM |
| Non-critical | SCR-9, 2 per glasses | Straight-through path |
| Stock | 60 BAT-1, 2500 SCR-9 | Both netting terms nonzero |
| Open PO inbound | 40 BAT-1 | Proves open-PO netting |
| Shortfall BAT-1 | 1200 | 1300 minus 100, see §3 |
| Shortfall SCR-9 | 100 | 2600 minus 2500 |
| Cartons | 12 x 100 = 1200 | Divides evenly, see §4 |
| Lot template | LOT-{{PO}}-A | Resolved post-PO, on every label |
| Prices | V-A 14.20, V-B 18.50, V-C 0.40 | Gap makes choice genuine |
| Limits | planner 5000, director 50000, finance 100000, auto 500 | Routing, see §5 |
| Vendors | V-A CN, V-B MX, legacy CN, V-C TW | Origin decides, see §6 |
| History | 10000 cells, CN 5900 | Fails cheap, passes winner |

BOM alternative `650 x 2 per` was considered and rejected. It changes the code
BOM for no gain. `1300 x 1 per` keeps `SeededErp` IDs and still yields 1200.
`VN` for V-B was considered; `MX` is kept for continuity with the code seed.
Country is per-cell origin on the lot, never vendor HQ country.

## 3. Shortfall math (agent reads, never hard-codes)

Formulas, evaluated at runtime from adapter reads:

- `required = SO qty x qtyPer` per component.
- `covered = available stock + open PO qty` (available status only).
- `shortfall = max(0, required - covered)`, whole units. Zero means no PR.
- `orderCartons = ceil(shortfall / unitsPerCarton)` from the item pack size.

Demo run:

- BAT-1: required `1300 x 1 = 1300`. Covered `60 + 40 = 100`.
  Shortfall `1300 - 100 = 1200`. Cartons `1200 / 100 = 12`.
- SCR-9: required `1300 x 2 = 2600`. Covered `2500 + 0 = 2500`.
  Shortfall `100`. Value `100 x 0.40 = $40.00`, under `$500`, so it posts
  with no human and no pack applies.

## 4. Carton and label contents

`unitsPerCarton = 100` is scenario data from the vendor pack, read at runtime.
PO quantity `1200` must satisfy `shortfall mod 12 = 0`.

Every physical label shows, at minimum: carton id, part, actual PO number from
the manifest, lot code, units per carton, UN number block, lithium-battery mark
(red-hatched border) where present, Class 9 diamond where applicable. Only
CTN-07 lacks the mark. Only CTN-04 shows damage.

Spark carton-read fields are transcription only, never flags, never verdicts:
`carton_id, part, po_number, lot_code, units_per_carton, mark_present (bool),
mark_legible (bool), un_number, damage_visible (bool), damage_note, image_id,
read_confidence`. C# decides pass, hold, or human review and cites the rule.

## 5. Price, limits, and approval routing

PO total is `shortfall x unit price`, computed at runtime:

- Main PO: `1200 x 18.50 = $22,200.00`.
- Cheaper-foregone gap: `22,200 - 17,040 = $5,160` (V-A at 14.20).

People and limits are seed data in `Users` plus `Signing Limits`. ERP enforces
them with refusals. Limits are not compliance policy, so tuning them is allowed.

| Person | Role | Limit | Why |
|---|---|---|---|
| Maya Planner | requester, raises PR | $5,000 | 22,200 exceeds it |
| Dana Director | approver | $50,000 | 22,200 fits here |
| Del Delegate | Dana's dated delegate | acts at 50,000 | Dana is out |
| Amy AP Clerk | works inbox | $0, prepares only | approves nothing |
| Finn Finance | payment approver | $100,000 | approves both pay runs |

Routing the data forces: Maya raises the PR. ERP refuses Maya self-approval
(requester-cannot-approve, plus over-limit). The task routes to Dana. Dana is
out, so Del approves as delegate with the flag, timestamp, and limit checked.
Invoice INV is approved by Del. Payments are approved by Finn. Invoice approver
and payment approver differ, so SoD holds. The SCR-9 PO at `$40` auto-approves
under `$500` with no human.

## 6. CORP rules exactly as decided, history in cells

Rule text is unchanged from [DECISIONS.md](/Users/nikhilvankayalapati/Desktop/ComplianceGate/docs/DECISIONS.md)
and [PROJECT.md](/Users/nikhilvankayalapati/Desktop/ComplianceGate/PROJECT.md):

- CORP-01 (Tier 3, documented only): no single vendor supplies more than 50%
  of critical battery volume over the rolling window. Never blocks the demo.
- CORP-02 (Tier 1, vendor gate): no more than 60% of critical battery volume
  from a single country of origin over the rolling window. Cheaper vendor fails
  it. Compliant alternate wins at higher price. Override only by logged
  exception from a named approver.
- Both measure critical battery volume. Volume means cells (units), not money.
  ERP tracks receipt quantities in cells. Cartons convert at 100 cells each.
- Rolling window: trailing 12 months of posted received cells. Open POs are
  excluded so the denominator is deterministic and replayable.
- Both vendors pass Tier 2 document checks (UN38.3, SDS plus UN3480, RoHS,
  REACH) and SCR-01 screening silently in seed, so CORP-02 is the visible cause.

Seeded history, all fictional, all in cells:

| Source | Cells | Cartons at 100 | Share of 10000 |
|---|---|---|---|
| V-A CN active | 3400 | 34 | 34.0% |
| Legacy CN inactive | 2500 | 25 | 25.0% |
| V-B MX active | 3800 | 38 | 38.0% |
| Other non-CN | 300 | 3 | 3.0% |

CN total is `5900 / 10000 = 59.0%`. Compliant, with 100 cells headroom to the
60% cap. The legacy CN vendor exists so no single vendor sits near 50%: with
CN mapped to one vendor only, any CN share near 60% would also breach CORP-01
and muddy the story. Splitting CN into V-A plus legacy keeps every vendor
share clean under both options.

New need is `1200` cells. Denominator becomes `11,200`. Evaluation includes the
proposed lot, per policy intent (what would the share become if we award here):

| Award 1200 to | New CN share | New vendor share |
|---|---|---|
| V-A CN | 7100 / 11200 = 63.4%, BLOCK | 4600 / 11200 = 41.1%, pass |
| V-B MX | 5900 / 11200 = 52.7%, PASS | 5000 / 11200 = 44.6%, pass |

V-A breaches CORP-02 by 3.4 pts and passes CORP-01 by 8.9 pts, so only CORP-02
fires. V-B passes CORP-02 by 7.3 pts and CORP-01 by 5.4 pts. Nothing lands
exactly on either cap. The earlier draft put V-B at 50.0% exactly; this history
replaces it. Thresholds, scope, window, and authority memo are untouched.

Boundary tests to keep: exactly 60.0% passes, 60% plus 1 cell blocks, empty
history routes to human review instead of silent pass.

## 7. Dock image set (4 images, pallet-level, multi-side)

Layout assumed: 3 wide (L/C/R) x 2 deep (Front/Back) x 2 high (Bottom/Top).
Small inter-carton offsets (5-10 cm) so rear-center labels peek through.

| Image | View | Must show | Clearly visible |
|---|---|---|---|
| IMG-01 | Front-left 3/4 oblique | Front faces bottom-front plus top-front L/C, left faces, CTN-04 damage side, CTN-07 missing-mark face | 01, 02, 03, 04, 05 peek, 07, 08, 10 |
| IMG-02 | Front-right 3/4 oblique | Front faces, right faces, CTN-07 second angle proving absence | 01, 02, 03, 06, 07, 08, 09, 11 peek, 12 |
| IMG-03 | Rear 3/4 | Rear faces of all 6 back-row, CTN-04 damage from rear, CTN-11 sliver only | 03 edge, 04, 05, 06, 09 edge, 10, 11 sliver, 12 |
| IMG-04 | Relabel reshot close-up | CTN-07 only, relabeled, ID plus PO legible | 07 only |

Coverage rule: every carton except CTN-11 is clearly legible in 2 or more
images. CTN-11 is occluded in IMG-01 and IMG-02 and a sliver in IMG-03.
Each image shows printed carton ID, part, actual PO, UN block, lithium mark
where present, Class 9 diamond, and damage only on CTN-04.

## 8. Ground-truth seed table (12 cartons, the accuracy oracle)

One row per carton. `mark_present` is ground truth. Mark truth lives in the
vision-owned carton file. ERP seed carries carton IDs plus quantities only.

| Carton | Part | PO | Lot | Units | Mark | Damage | Visible in |
|---|---|---|---|---|---|---|---|
| CTN-01 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-01, IMG-03 |
| CTN-02 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-01, IMG-03 |
| CTN-03 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-01 |
| CTN-04 | BAT-1 | manifest | LOT | 100 | yes | crush outer, cells OK | IMG-01, IMG-03 |
| CTN-05 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-01, IMG-02 |
| CTN-06 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-01, IMG-02 edge |
| CTN-07 | BAT-1 | manifest | LOT | 100 | NO pre-reshot, yes IMG-04 | none | IMG-02, IMG-04 |
| CTN-08 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-02 |
| CTN-09 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-02, IMG-03 |
| CTN-10 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-03 |
| CTN-11 | BAT-1 | manifest | LOT | 100 | yes in truth | none, hidden | NONE legible |
| CTN-12 | BAT-1 | manifest | LOT | 100 | yes | none | IMG-03 |

PO and LOT columns resolve from the run manifest at build time. Totals check:
12 x 100 = 1200 = PO qty. So 9 normal-pass, 1 missing-mark, 1 damaged-marked,
1 hidden-but-marked-in-truth. Earlier drafts swapped CTN-04 and CTN-11 roles;
this table is the fixed assignment: CTN-04 is damage, CTN-11 is hidden.

## 9. Problems and endings (every problem carton closes)

Only CTN-07 waits for the relabel. The other two close by human review.

CTN-07 missing mark. Shown in IMG-01 plus IMG-02, two angles proving absence,
fixed in IMG-04. Read gives `mark_present=false` twice, then `true` on IMG-04.
C# holds citing BAT-LI-03, releases on the reshot pass. Sequence:
`hold BAT-LI-03 -> pass BAT-LI-03 on IMG-04`. Held value is 100 x 18.50.

CTN-11 hidden. Absent in IMG-01 and IMG-02, sliver only in IMG-03 with no
transcribable ID or mark. C# routes to human review (ASN reconciliation: on
ASN, in no image clearly). Ending: worker repositions on the dock, confirms
the lithium mark present and legible, review records reviewer plus timestamp.
Sequence: `review BAT-LI-03-unreadable -> pass BAT-LI-03 human-confirmed`.
No reshot image. No surviving hold.

CTN-04 damaged. Crush visible in IMG-01 plus IMG-03, mark present and legible
in both. Read gives `mark_present=true, damage_suspected=true` with crop
coordinates. C# routes to human review citing BAT-LI-05, never auto-quarantines
on photo alone. Ending: worker reviews, accepts as cosmetic outer damage with
cells intact, note attached. Sequence: `review BAT-LI-05 -> pass accepted`.
Quarantine released, pay unblocked.

The 9 remaining cartons read `mark_present=true, legible, no damage` and pass
citing BAT-LI-03.

## 10. Held amount and the two payment runs

Carton price is `100 x 18.50 = $1,850`. Invoice total is `12 x 1850 = $22,200`,
which must equal PO total at matched price. ELEC-04 blocks full payment while
any hold or review stays open.

Demo timing (reviews for CTN-04 and CTN-11 close before the pay run, per the
agreed endings, so only CTN-07 is still held):

- Held: `1 x 1850 = $1,850` (CTN-07, BAT-LI-03).
- First payment PAY-1: `11 x 1850 = $20,350`.
- Resolution: relabel plus IMG-04 reshot passes, hold released.
- Second payment PAY-2 next run: `1 x 1850 = $1,850`.
- Check: `20,350 + 1,850 = $22,200`. Balances by hand.

If the pay run happens while reviews are still open, the same formulas give
held `3 x 1850 = $5,550`, first pay `9 x 1850 = $16,650`, second pay
`$5,550` (2 review-released plus 1 reshot-released). Either timing balances
because every amount is accepted cartons times matched unit price. Partial
settlement records two `VendTrans` applications against the same invoice.

## 11. Scoring (per carton, never aggregate-only)

Checks live under `Gate.Eval/Vision/`. For each of the 12 cartons, the cached
Spark read is compared against the seed row:

- Read rate: carton found with correct ID, part, PO, lot, units.
- Correct-cite rate: C# outcome matches the expected verdict with the right
  rule id (BAT-LI-03 versus BAT-LI-05). Right verdict with wrong rule is a miss.

Accuracy log shape, one row per carton plus a summary line:
`carton_id, expected_mark, read_mark, mark_match, expected_verdict,
actual_verdict, verdict_match, rule_expected, rule_actual, cite_match,
images_seen_in, notes`. Target: 12 of 12 verdict plus cite correct, 11 of 12
fully read (hidden CTN-11 excepted by design). IMG-04 is scored separately as
the CTN-07 flip from false to true.

Legibility backstop: at most 3 read attempts per image. Past that, stop
regenerating and composite clean labels from the seed rows, record
`composited=true`, hash the composited file as canonical, and do one
validation read. Never burn credits on regeneration churn.

## 12. AP invoices (vision creates, Spark reads once, never verdicts)

Storage is `assets/invoices/`. One cache entry per document keyed by doc hash
plus `invoice-read-v1` plus timestamp plus model id. Demo replays cache only.
Upload stays test-only, never on the demo path. Reads below are transcription
only. No `duplicate_suspected`, `bank_mismatch`, or `missing_fields` flags
live in the read JSON. Those judgments belong to the separate AP check list.

Shared truth for the main invoice: vendor V-B, PO is the actual battery PO
from the manifest, part BAT-1, qty 1200, unit price 18.50, total $22,200,
bank details matching the V-B vendor-bank master.

Main invoice shows vendor id plus name, PO number, invoice number and date,
lines `BAT-1 x 1200 @ 18.50 = $22,200`, and matching bank details. Correct
read transcribes every field verbatim. No verdict.

Each tricky invoice tests exactly one problem and points at its own small
closed PO, never at the main battery PO, so none looks like over-billing:

| Doc | Points at | Shows | Correct read |
|---|---|---|---|
| Duplicate | own small PO, reuses a posted number | same vendor plus number as a posted invoice | verbatim transcription |
| Bank-change | own small PO, new bank | same vendor and PO, different remittance bank | new bank verbatim |
| Missing-fields | own small PO, blanks | lines present, vendor and PO blank | blanks as null |

Small-PO intents (fictional, pre-seeded history, all posted and closed): one
50-cell BAT-1 PO for the duplicate test, one 40-cell BAT-1 PO for the bank
test, one 200-unit SCR-9 PO for the missing-fields test. Actual numbers come
from sequences; the tricky invoices reference the manifest numbers.

Separate AP check list, decided later in agent and ERP workflow code, never
cited as regulation packs:

- AP-C1 duplicate invoice number refused by ERP and matched by agent.
- AP-C2 price beyond 2% tolerance held.
- AP-C3 zero receipt or no PO line match held.
- Bank details different from vendor master held, master never auto-updated.
- Vendor or PO missing or unresolvable held to the AP clerk.

## 13. Cache and offline replay (dock plus invoices, same mechanism)

Manifest holds one entry per image read (keyed by image sha256 plus
`carton-read-v1`) and one per invoice (keyed by doc sha256 plus
`invoice-read-v1`), each with timestamp plus model id plus result hash.
Build flow: generate or composite once, read once per file, cache. Demo and
replay read cache only. Full replay makes zero live calls. A replay test fails
on any live call. Hash mismatch invalidates that entry loudly instead of
reusing a stale read.

## 14. Second case alt-v1 (different numbers, same mechanics)

`scenarios/alt-v1.json` proves the code is generic. Same shape as demo, every
absolute number different, same story beats. All fictional.

| Item | Demo | Alt |
|---|---|---|
| SO intent | 1300 GLASS-1 | 1100 GLASS-1 |
| Stock plus open PO | 60 plus 40 | 100 plus 40 |
| Shortfall BAT-1 | 1200 | 960 |
| Units per carton | 100 | 80 |
| Cartons | 12 x 100 | 12 x 80 |
| Unit price | 18.50 | 21.00 |
| PO total | $22,200 | $20,160 |
| Carton price | $1,850 | $1,680 |
| Held CTN-07 | $1,850 | $1,680 |
| First pay 11 | $20,350 | $18,480 |
| Second pay 1 | $1,850 | $1,680 |

Alt history is demo history times 0.8, all in cells: V-A 2720, legacy 2000,
V-B 3040, other 240, total 8000, CN 4720 at 59.0%. New need 960, denominator
8960. V-A award gives CN 5680 / 8960 = 63.4% block with vendor 41.1% pass.
V-B award gives CN 4720 / 8960 = 52.7% pass with vendor 44.6% pass. Same
margins as demo, no exact caps, thresholds untouched. Limits stay 5000 plus
50000, so `$20,160` still routes to director plus delegate. Carton roles,
images, endings, invoice patterns, and checks are identical, only the numbers
change. Alt passes if and only if the code reads everything from data.

## 15. What stays generic versus what is seed-only

Generic (agent and ERP read at runtime through the adapter, works for any
order): BOM qtyPer, required and covered and shortfall netting, carton
rounding via unitsPerCarton, PR and PO totals as qty times price, approval
routing outcomes, line-level match checks with read tolerance, held and pay
split counted from live holds and reviews, sequence-assigned numbers followed
by returned IDs, idempotency keys on writes and replays.

Seed-only (never literals in code): SO quantities, stock and open-PO values,
unit prices, vendor countries and shares, limits and delegations, carton IDs
and the three seeded failures, the 4-image list, invoice particulars and small
POs, CORP-01 50% and CORP-02 60% inputs (thresholds themselves live in versioned
rule config). Change the file and the same code produces a different shortfall,
winner, holds, and payments.

## 16. Build handoff (after foundation lands, in video order)

1. Loader plus sequences plus manifest, demo file first, alt file second.
2. Carton seed plus label spec reading the manifest, then the 4 image specs.
3. One Spark read per image and per invoice, cache entries written.
4. Per-carton scorer plus zero-live-call replay check.
5. Demo walk plus alt walk, both balancing by hand.

No-spend guardrails: 4 final images max, attempt caps above, compositing over
regeneration, prompt versions pinned, replay fails on any live call, upload
path separated from demo, every cached result carrying hash plus version plus
timestamp.

## Journal

- 2026-10-28: scenario consolidated here from the vision, ERP, agent, and
  compliance planner notes. Fixed: history in cells with clean margins, carton
  roles pinned to CTN-04 damage plus CTN-11 hidden, reads transcription-only,
  tricky invoices on separate small POs, PO numbers from sequences via manifest.
  Docs only. No code changed.
