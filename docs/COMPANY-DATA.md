# ComplianceGate — Company Data (tonight's build scope)

The company the product runs for. Design for the implementation step; no
code changes here. Business today is **November 1, 2026**. Every number
and policy below is carried from the current build; nothing is retuned.
Items marked [A] are assumptions flagged for correction. Tonight's scope
replaces the full proposal: anything not listed in the build brief waits.

## 1. Company identity (ERP `company_info`, legal entity LE-1)

- Legal name: **Lumenfield Eyewear Inc.** [A: invented, clearly
  fictional; never a real company name]. Entity code `LE-1` (kept).
  Base currency USD. Incorporated US.
- Bill-to / HQ: 1000 Assembly Way, Suite 200, Reno, NV 89501 [A:
  invented fictional street].
- Ship-to / dock: Dock 4, 1000 Assembly Way, Reno, NV 89501 [A].
- Products: `GLASS-1` smart glasses (finished); `BAT-1` battery cell,
  `SCR-9` screw (components).
- D365 surfaces: top bar shows `LE-1 · Lumenfield Eyewear Inc.`; PO
  documents carry the company header plus bill-to/ship-to; supplier and
  customer messages send from `purchasing@lumenfield.example` [A:
  mechanical from the name] with the entity in context.
- Site: `SITE-DOCK-1` "Dock 1", Reno NV, capabilities dock-camera
  (moved from KB to ERP master).

## 2. People, limits, roles, delegations (ERP, created Oct 1 2025 by Finn Finance [A: first administrator])

| User | Role | Signing limit |
|---|---|---|
| Maya Planner | requester | 5,000 USD |
| Dana Director | approver | 50,000 USD |
| Del Delegate | delegate [A: was `dated-delegate-for-Dana`; the person-specific role value is normalized, the delegation row carries from/to/dates] | 50,000 USD |
| Amy AP Clerk | ap-clerk | 0 USD |
| Finn Finance | payment-approver | 100,000 USD |

Delegation: Dana Director → Del Delegate, Oct 20 – Nov 10 2026.
Approval matrix (ERP): sourcing-policy → Dana/approver;
payment-approval → Finn/payment-approver; purchase-request →
Maya/requester; ap-exception → Amy/ap-clerk; kb-edit-leadership/
procurement/hr/regulatory → Dana; kb-edit-finance → Finn;
kb-edit-operations → Maya. Fiscal year Jan 1 – Dec 31, FY2026 [A].
Config: base currency USD, auto-approve threshold 500, terms NET30.

## 3. Vendors (ERP masters; one account scheme: all mint from VND)

| Vendor | Name | Country | Email [A: mechanical `sales@<word>.example`] | Bank (verified by Finn) | Terms | Sells | Docs |
|---|---|---|---|---|---|---|---|
| VND-1001 | CinderVolt Energy Ltd | CN | sales@cindervolt.example | BANK-CV-001 [A: re-keyed from BANK-A-001 to the vendor short] | NET30 | BAT-1 @ 14.20 | UN38.3/SDS/RoHS/REACH present (`un383-a`… kept verbatim) |
| VND-1002 | Meridian Cellworks Inc | MX | sales@meridian.example | BANK-MC-001 | NET30 | BAT-1 @ 18.50 | UN38.3/SDS/RoHS/REACH present (`un383-b`…) |
| VND-1003 | Formosa Fasteners Co | TW | sales@formosa.example | BANK-FF-001 | NET30 | SCR-9 @ 0.40 | none (non-critical, no pack applies) |
| VND-1004 | DragonLeaf Cells Co Ltd | CN | sales@dragonleaf.example | BANK-DL-001 | NET30 | BAT-1 @ 14.60 | UN38.3/SDS/RoHS/REACH present (`un383-d`… [A: must have passed doc checks to sell]) |
| VND-1005 | ABC Parts (US electronic distributor) | US | sales@abc.example | BANK-AB-001 | NET30 | BAT-1 @ 19.75 | UN38.3/SDS/RoHS/REACH present (`un383-ab`… [A: same]) |

Created by Maya Planner: VND-1001–1003 Oct 5–7 2025 [A: order],
VND-1004 Oct 28 2025, VND-1005 Jan 12 2026 [A: before its first PO].
Banks verified Oct 30 2025, ABC Jan 2026 [A]. The old `V-A…V-L` ids are
gone; V-L/Bluepine goes with the lifecycle purge (§7).
DragonLeaf hold set **July 1 2026 by Dana Director**, reason "Supplier
agreement ended" — after its June delivery and before its July payment;
blocked vendors take no new POs. Candidate map for new buys derives
from vendor master + prices (BAT-1: CinderVolt/Meridian; SCR-9:
Formosa).

## 4. Items, BOM, sequences

Items: `BAT-1` battery cell (critical, 100/carton, EA); `SCR-9` screw
(100/box, EA); `GLASS-1` smart glasses (EA). `WID-7` is purged with
all lifecycle data. BOM: 1 GLASS-1 = 1 BAT-1 + 2 SCR-9.

Supplier-document scheme [A: supplier numbers are recorded verbatim as
printed; the values below use one mechanical running form per vendor]:
invoice `{short}-{yyyy}-{mm}-{dd}` (shorts CV/MC/FF/DL/AB),
packing slip `{short}-PS-{yyyymmdd}`, cartons
`{short}-C-{yyyymmdd}-{nn}` (100 units, last carton takes the
remainder). ASNs are OUR documents and mint from the ASN sequence.

## 5. History I — closed October 2025 chains (fully received, invoiced, paid; outside the trailing window by design)

| PR/PO/ASN/RCT/Voucher | Vendor | Qty | Total | Invoice (supplier no.) | Paid |
|---|---|---|---|---|---|
| PR/PO/ASN/RCT/V-1001 | VND-1002 | 50 BAT-1 | 925.00 | MC-2025-10-05 | Nov 28 2025 run |
| PR/PO/ASN/RCT/V-1002 | VND-1002 | 40 BAT-1 | 740.00 | MC-2025-10-12 | Nov 28 2025 run |
| PR/PO/ASN/RCT/V-1003 | VND-1003 | 200 SCR-9 | 80.00 | FF-2025-10-19 | Nov 28 2025 run |

PRs approved by Dana; POs confirmed; receipts Oct 2025; Nov 28 2025
payment run `PAYDRAFT-1001`/`PAY-1001` pays 1,745.00 (approved Finn).

## 6. History II — monthly buys, Nov 2025 – Oct 2026 (each chain: PR→approve→PO→ACK→ASN→receipt→invoice→approve→post; requester Maya, PR/PO approver Dana, invoice approver Dana except Oct 2026 Del as delegate, payments Finn)

Monthly rhythm [A]: PR day 3, approved day 4; PO day 5, approved day 6,
ACK day 7; ASN day 18; receipt day 19; invoice day 20, approved day 24,
posted day 25. CinderVolt months: 283 cells (4,018.60), Oct 287
(4,075.40) [A: even spread, remainder in Oct]. Meridian months: 317
cells (5,864.50), Oct 313 (5,790.50) [A: same]. DragonLeaf: Nov 2025
800 (11,680.00), Feb 2026 850 (12,410.00), Jun 2026 850 (12,410.00).
ABC spot: Feb/May/Aug 2026 × 100 (1,970.00 each). Formosa Oct 2026:
2,500 SCR-9 (1,000.00), received Oct 12, invoiced Oct 15, paid early
Oct 28 (terms never refuse early payment).

Numbers mint in creation order: monthly chains take PR/PO/ASN/RCT/V
1004–1034 (CinderVolt then Meridian each month; DragonLeaf in Nov/Feb/
Jun; ABC in Feb/May/Aug; Formosa last in Oct). Payment runs pay the
prior month's invoices on the 10th: Dec 21,563.10 (with DragonLeaf);
Jan–Feb 9,883.10; Mar 24,263.10 (DragonLeaf + ABC); Apr–May 9,883.10;
Jun 11,853.10 (ABC); Jul 22,293.10 (DragonLeaf); Aug 9,883.10; Sep
11,853.10 (ABC); Oct 28 run pays Sep invoices + Formosa early =
10,883.10 (`PAYDRAFT/PAY-1001–1012` in run order).

Reconciliation: CinderVolt 3,400 / 48,280.00; Meridian 3,800 /
70,300.00; DragonLeaf 2,500 / 36,500.00; ABC 300 / 5,910.00; Formosa
1,000.00. Trailing total **10,000 cells, CN 5,900 = 59.0%**.
Paid-to-date 153,869.10; open and due Nov 19: Oct CinderVolt 4,075.40
+ Oct Meridian 5,790.50 (posted, approved, no draft — no agent work
attached).

Consumption (ERP inventory journals, one line per month [A:
mechanical]): each month consumes that month's BAT-1 receipts except
October, which consumes 540 of 600. On-hand Oct 31: BAT-1 60
(October Meridian lot), SCR-9 2,500 (Formosa October receipt).

## 7. Open and waiting at Nov 1 2026 (all lifecycle data purged: no V-L, no WID-7, no lc.* docs, no SEED-/LC- numbers)

- Inbound cover: PR-1035 (Oct 14, approved Oct 15 Dana, 740.00);
  PO-1035 (Oct 16, approved Oct 17 Dana, ACK Oct 18, Meridian 40 BAT-1
  @ 18.50 = 740.00, delivery due Nov 15) — the open-PO cover in the
  shortfall math.
- Open payables: Oct CinderVolt 4,075.40 + Oct Meridian 5,790.50
  (posted, approved, due Nov 19, no payment drafted).
- Pending approvals: NONE. Opening is fully quiet — no decision shells,
  no cases, no agent work until the shop order arrives or a person acts.

## 8. CORP-02 position (implementation-verified, recon P1)

Rule: (history cells for the awarded country + lot) / (total + lot),
block iff > 60%. Baseline CN 5,900/10,000 = 59.0%. New 1,200-cell
award: to CinderVolt (CN) 7,100/11,200 = **63.4% BLOCK**; to Meridian
(MX) 5,000/11,200 = **44.6% PASS**. CORP-01 is Tier 3 documented-only
and emits no verdict. The earlier 300-cell open item is closed by ABC
Parts (US).

## 9. First outside event (documented expectation, NOT stored data)

No sales order sits in opening. The documented first order is 1,300
GLASS-1 through the shop (any quantity works — the agent nets from
data). Expected math on arrival: BAT-1 required 1,300, covered 60 +
40, shortfall 1,200 → PR → CORP-02 blocks CinderVolt, Meridian wins →
PO 1,200 @ 18.50 = 22,200.00 → 12 cartons × 100. SCR-9 required 2,600,
covered 2,500, shortfall 100 → 40.00, auto-approved under 500, no pack
applies. No carton outcomes are preassigned; dock rules judge whatever
arrives.

## 10. Memos, rules, KB articles

MEMO-0001 (first of the MEMO sequence): from Dana Director, dated
**Sept 15 2026**. Excerpt [A: excerpt-form from the rule params]:
"Single-country and single-vendor concentration on critical battery
cells must stay capped: no more than 60% of trailing-12-month critical
cell volume from one country of origin, no more than 50% from one
vendor. The caps apply at vendor award including the prospective lot.
Exceptions need Finance approval and stay logged." Proposal PROP-0001
(clean round, validated, what-if run); approved by **Finn Finance
Sept 24 2026**; adopted **Oct 1 2026** (PUB-0004; ACT-0012 CORP-01 v1,
ACT-0013 CORP-02 v1; KB article `sourcing-policy` v1 same approval,
PUB-0007).

Rules (all v1; params live on the rule; pack `adopted` corrected —
CORP Oct 1 2026, rest Jan 1 2026; banned words scrubbed from titles):

- BAT-LI-01/02 (vendor docs) Tier 2, 03 (mark) Tier 1, 04 (Class 9)
  Tier 2, 05 (damage→review) Tier 1. Author Maya Planner, approved
  Dana Director Dec 12 2025, adopted Jan 1 2026 (PUB-0001,
  ACT-0001–0005). Sources: UN Test Manual III §38.3 + IATA guidance +
  49 CFR 173.185 (01); IATA DGR PI965/966/967 + 49 CFR 172.101 + IMDG
  SP188/SP230 (02); IATA DGR Fig 7.1.C + 49 CFR 173.185(c)(3) + IMDG
  SP188 (03); IATA DGR Class 9 handling + 49 CFR 173.185 (04); IATA DGR
  / IMDG damage provisions, human decider (05).
- ELEC-01 (RoHS) / 02 (REACH) Tier 2 vendor, 03 (radio) Tier 3
  documented-only, 04 (pre-pay gate) Tier 1. Author Dana Director,
  approved Finn Finance Dec 12 2025, adopted Jan 1 2026 (PUB-0002,
  ACT-0006–0009). Sources: EU RoHS 2011/65/EU + (EU) 2015/863 (01);
  REACH (EC) 1907/2006 Art 33 + ECHA Candidate List (02); internal
  gate dependency (04).
- SCR-01 (restricted-party screening) Tier 2, 02 (UFLPA) Tier 3
  documented-only. Author Finn Finance, approved Dana Director Dec 12
  2025, adopted Jan 1 2026 (PUB-0003, ACT-0010/0011). Sources: OFAC SDN
  + US Consolidated Screening List + BIS lists + EU Consolidated
  (DG FISMA) + UN SC Consolidated (01); DHS UFLPA Entity List + FAQs +
  CBP guidance + 19 USC §1307 (02).
- CORP-01 (vendor cap 0.50, Tier 3) / CORP-02 (origin cap 0.60, Tier 1,
  window 12 months, critical scope). Memo Dana Sept 15 2026, approved
  Finn Sept 24 2026, adopted Oct 1 2026. Nobody approves their own pack.

KB articles (all v1; created Dec 10 2025, approved Dec 12 2025,
effective Jan 1 2026 except `sourcing-policy` above; author = space
owner; approver Dana except Dana-owned → Finn):

- Reference: `region-asia` (CN, TW), `region-north-america` (US, MX),
  `doc-un383-template` (UN38.3, P2P), `doc-hr-001` (HR, non-P2P, never
  grounds a rule, never sent to the model).
- Finance (Finn): `signing-limits`, `delegation`, `payment-terms` —
  rewritten as prose WITHOUT the limit table, delegation dates, or
  terms values (those live in ERP, linked).
- Procurement (Dana): `sourcing-policy` — rewritten as CORP grounding
  prose citing MEMO-0001/ACT-0012/ACT-0013, no cap numbers.
- HR (Dana): `approval-roles` — prose without user/role lists.
- Regulatory (Dana): `data-residency`, `un38-3-summary`, `iata-marks`
  (kept — no ERP values inside; the latter two ground BAT-LI-01–04).
- Operations (Maya): `critical-parts` (prose; flags live on items),
  `dock-safety` (kept).
- `company-identity`: RETIRED — superseded by ERP `company_info`
  (leadership space opens empty; memos are the leadership record).

KB publications: PUB-0005 (5 Finn/Maya-authored articles, approved
Dana), PUB-0006 (8 Dana-authored articles, approved Finn), PUB-0007
(`sourcing-policy`, approved Finn Sept 24 2026).

## 11. Sequences after opening (all start 1001/0001; next values)

SO→1001 (unused) · PR→1036 · PO→1036 · ASN→1035 · RCT→1035 · V→1035 ·
PAYDRAFT→1013 · PAY→1013 · VND→1006 · HLD→1001 (unused) ·
CASE→0001 · DEC→0001 · MEMO→0002 · PROP→0002 · PUB→0008 · ACT→0014 ·
EXP→0001 · MSG→0001.

## 12. Assumptions ledger [A] (mechanical fill-ins, correct freely)

Invented identity (name, addresses, sender email); first-administrator
history entry (Finn creations Oct 1 2025, Maya masters Oct 2025,
ABC Jan 12 2026); delegate-role normalization; monthly even-spread
splits and DragonLeaf 800/850/850; monthly rhythm dates; supplier
running-number scheme + carton rule; bank/doc-ref schemes; MEMO-0001
as first memo number + memo excerpt-form; fiscal year; vendor-email
rule; on-hand lot linkage; consumption journals. Tonight's deltas from
the owner: ABC Parts vendor + 3×100 spot POs; VND scheme for all
vendors; DragonLeaf last PO June 2026, hold July 1; full lifecycle
purge; CORP-02 figures confirmed 63.4/44.6.
