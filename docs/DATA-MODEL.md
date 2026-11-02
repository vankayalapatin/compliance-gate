# ComplianceGate — Data Model (design only)

Design for the product-bar rebuild. No code changes in this step. This doc
plus `docs/COMPANY-DATA.md` is the spec; `docs/archive/` is frozen history.

## 1. Two databases, no file data

| Db | Holds | Lives as |
|---|---|---|
| ERP db | Company identity, masters, documents, inventory, approvals, sequences, attachments pointers | One sqlite file (EF Core, migrated) |
| Platform db | Cases, events, decisions, KB articles, rules, memos, proposals, activations, exceptions, supplier messages, attachments pointers | One sqlite file (EF Core, migrated) |

Every screen reads from these two dbs. Files are never runtime data
except config (`appsettings*.json`, `GATE_*` env), the model cache
(keyed by request hash), and attachment bytes (dock/invoice photos, memo
uploads) that a db row points at by content hash. The agent reaches ERP
only through `IErpAdapter`. The two dbs refer to each other by ERP
document and account numbers only — never by internal row ids, and ERP
never stores platform ids (the `Holds.RuleId/CaseId` columns go away; a
hold is identified by document type + number + line/lot/carton, which is
how the case side references it back).

## 2. Platform db tables

- `cases` — one row per case: `case_no` (CASE sequence), sales order no,
  item, stage, linked doc numbers (PR/PO/inbound/receipt/invoices/
  payments), awarded vendor, block (rule + reason). No scenario field.
- `case_events` — append-only timeline: case no, line no, timestamp,
  actor (user id or `p2p-agent`), kind, summary, rule id, document ref,
  evidence ref, amount. No agent entries on opening shells.
- `decisions` — human cards: `decision_no` (DEC sequence), case no (or
  memo no for policy cards), kind, title, status, requested-to,
  requested-at, decided-by/at, comment, document ref, amount, rule id,
  reason, evidence refs. One human decision = one row; the ERP approval
  record stays the ERP's own row (see §6).
- `kb_articles` — one row per article version: stable article code
  (e.g. `data-residency`), `version` int (1, 2, …), section, title, body
  (prose only — §4), owner user, status, effective date, author +
  created-at, approver + approved-at, supersedes version. Whole-file
  `kbvN` versioning is deleted; each article versions alone.
- `kb_publications` — publish batches: `pub_no` (PUB sequence),
  article+version (or rule+version) published, approver, reason,
  published-at. One approval can publish many articles; each keeps its
  own version.
- `rules` — one row per rule version: stable rule id (e.g. `CORP-02`),
  `version` int, pack, tier, gate, title, source, owner, author +
  created-at, approver + approved-at, adopted date, enforced flag,
  status. Pack-file versions are deleted; each rule versions alone.
- `rule_params` — parameters live ON the rule: rule id + version, name,
  value. No parameter is copied into KB articles.
- `rule_citations` — rule version ↔ KB article+version (grounding),
  rule version ↔ memo/proposal/activation (origin). Both links render.
- `memos` — `memo_no` (MEMO sequence), author, received-at, full text,
  excerpt.
- `proposals` — `proposal_no` (PROP sequence), memo no, round, KB
  versions pinned, rule type + params, confidence, statement quotes,
  validation outcome, what-if report ref, policy owner, hash.
- `proposal_questions` — blocking/advisory questions with options,
  answers, decider.
- `activations` — `activation_no`, memo/proposal refs, rule + new
  version, prior version, approver, effective-at, applicability, test
  evidence, published-at.
- `exceptions` — rule overrides: `exception_no` (EXP sequence), rule,
  case/document, approver, reason, granted-at, expiry. CORP-02 override
  is one of these, never a flag.
- `supplier_threads` + `supplier_messages` — thread per case + vendor;
  messages: `message_no` (MSG sequence), direction in/out, from/to,
  subject, body, sent-at. No SMTP; the app is the thread of record.
- `attachments` — content hash, bytes path, uploaded-by/at; referenced
  from ERP rows (photos on inbound/receipt) and platform rows (memo
  uploads, decision evidence).

## 3. Platform number sequences (all new, all own-sequence)

CASE, DEC, MEMO, PROP, PUB, ACT, EXP, MSG. Format `{prefix}-{number}`,
width 4, start 1: `CASE-0001`, `MEMO-0001`, `ACT-0001`. Existing `MEMO-{guid}` and
`{scenario}-{doc}-{n}` ids do not survive; the company data opens with
`MEMO-0001` as its first memo and no cases except pending-approval
shells (which take CASE numbers like any case).

## 4. What stays in ERP and never in KB

ERP masters (tables, owned by ERP services): legal entity
(`company_info`: code, name, addresses, currency, sender identities),
sites/warehouses, items + BOM, vendors + banks + documents, payment
terms, users + signing limits, delegations, approval matrix
(action-or-space → owner user + owner role), fiscal calendar, number
sequences, config (base currency, auto-approve threshold).

KB articles are prose knowledge only: region references, document
requirements, wiki guidance. An article MUST NOT carry a master value
(no limit tables, no delegation dates, no user lists, no cap numbers,
no critical flags, no entity names). Where prose needs a value it links
to the ERP record or the rule; the UI resolves the link. Current
violations to rewrite at build: `signing-limits`, `delegation`,
`payment-terms`, `approval-roles`, `sourcing-policy`, `critical-parts`,
`company-identity` (retired — superseded by `company_info`).

## 5. ERP db changes

- New tables: `company_info`, `sites`, `approval_matrix`,
  `fiscal_calendar`, `inbound_shipments` (see below), `doc_attachments`
  (doc type + number → content hash), `inventory_journals` (consumption,
  counts, adjustments with reason — the honest way to explain on-hand).
- Inbound split: supplier ASN and packing-slip numbers are EXTERNAL
  references. New `inbound_shipments` take our own INB sequence
  (`INB-1001`…); supplier ASN ref, packing-slip ref, and supplier carton
  ids ride as fields. `ASN-{PO}` / `PACK-{PO}` derivation is impossible
  by construction.
- Vendor accounts: new `VND` sequence for vendor masters
  (`VND-1001`…). Existing `V-A…V-L` manual ids are kept (D365 allows
  manual accounts) and predate the sequence. The shared `V` sequence
  stays vouchers-only.
- Stamps on EVERY document table: `created_by` + `created_at`,
  `approved_by` + `approved_at` (null until approved),
  `posted_by` + `posted_at` (null until posted). Approvals keep their
  own rows too (§6).
- Vendor hold: `blocked` + `block_reason` + `blocked_by` +
  `blocked_at`. No silent flags.
- Business date: every posting write takes an explicit posting date
  (default today; past dates allowed with the reason recorded), so
  history posts in the past. Created/approved timestamps are action
  time; the one-time history entry passes explicit past datetimes
  through the same services.
- `HoldId` mints from HLD (currently unused). `SO` sequence stays for
  sales orders (first shop order mints `SO-1001`).
- Delete: `Holds.RuleId`, `Holds.CaseId` (platform refs — §1);
  `VolumeSeeds` (history becomes real documents); scenario/meta side
  tables die with `gate.db`, which is deleted.

## 6. Approvals: stored twice on purpose, never three times

Delivery slip, not duplication: the ERP `approvals` row is the control
record (document, routed-to, acted-by, delegate flag, timestamp,
decision, comment, limit checked); the platform `decisions` row is the
human card (evidence, question thread, case link). Policy approvals add
the `activations` row as the publish record. Rule approvals and KB
publishes never touch ERP tables.

## 7. Two-way links (all rendered, none dead)

- Vendor ↔ POs, invoices, payments, documents, holds, messages.
- Item ↔ stock, open POs, demand (open SO lines), BOM (used-in).
- PO ↔ PR, inbound, receipts, invoices, payments, case.
- Rule ↔ source articles + origin memo/proposal/activation; ↔ blocked
  documents (from case events citing the rule version).
- Article ↔ rules citing it (from `rule_citations`); memo ↔ proposals
  ↔ activations ↔ rule versions.
- Every link resolves to a list or record page. "Unused" and counts
  without links are defects.

## 8. Screens, language, reset, proof

- ERP screens follow D365 F&O: list page → details page (header, lines,
  related docs); actions offered only for the record's state and the
  user's role; inputs pick real records, no free-text IDs.
- Product language only (bar list). Operator controls (reset, replay,
  pickup, spend) live only on one Admin page; the top bar shows legal
  entity code + name, acting user, and case-insensitive search.
- Reset restores a saved copy of BOTH dbs from the shipped snapshot
  pair. It runs no loader and no model call. The snapshot is built once
  by the company-data build (implementation step) and versioned with
  the code.
- Quiet opening: no cases, events, supplier messages, or exceptions;
  decisions exist only as shells for ERP pending approvals (no agent
  entries). Resolving a decision may adopt its document into a live
  case — that is the person-acts path; outside events arrive through
  the shop, supplier, dock, and memo inputs. Nothing else starts agent
  work.
- Proof: a crawler test opens every list and every record linked from
  it as every user and fails on any error page. Tests build their own
  data in temp stores and never reach the app dbs.

## 9. Explicitly out of this design

Live camera/video/voice, shipping + customer invoicing, production
scheduling, forecasting tools, real D365/NetSuite connectors, real
vendor data, live sanctions feeds, real payment rails, mobile app.
Credit memos and lot-level origin stay future work; vendor default
country remains the origin of record.
