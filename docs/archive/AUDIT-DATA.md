# ComplianceGate — Data Audit (read-only)

Lead session, docs only. No code was changed for this audit. Every claim
below was read from the repo or from the live app databases at
`Gate.Web/data/erp.db` and `Gate.Web/data/gate.db` (content root is
`Gate.Web/`, so these are the files `dotnet run` actually uses; the
repo-root `data/` copies are stale). Judged against the product bar in
`AGENTS.md`.

## 1. Data inventory: what the UI shows, where it lives, what writes it

| # | UI data | Lives in | Written by | Reset touches it? |
|---|---|---|---|---|
| 1 | ERP documents + masters (SO/PR/PO/ASN/receipt/invoice/payment/hold/approval, items, vendors, banks, users, delegations, sequences) | `Gate.Web/data/erp.db` (EF Core sqlite, 44 tables) | `ScenarioLoader` on startup-when-empty and on reset; agent + UI through `IErpAdapter` afterwards | YES — wipe (`EnsureDeleted` + `Migrate`) plus full reload; every number regenerated |
| 2 | Manifest mirror: event→number rows, meta (incl. a full copy of the scenario JSON), sequences | `Gate.Web/data/gate.db` (sqlite, `documents`/`meta`/`sequences`) | `ScenarioLoader` only | YES — cleared plus rebuilt |
| 3 | Run manifest (`Scenario`, `Documents[]`) | `scenarios/run-manifest.json` (git-ignored working file) | `ScenarioLoader` | YES — rewritten |
| 4 | Cases (header, stage, doc links, block) | `Gate.Web/data/case-cache/<CaseId>.json` | Agent (webhook intake, pickup, advance, handlers); UI decision resolves | YES — all `*.json` deleted, then opening shells re-created from ERP pending approvals |
| 5 | Case events + decisions | Same case files (`events[]`, `decisions[]`) | Same as cases | YES — same as cases |
| 6 | Vendor mail threads | `Gate.Web/data/case-cache/<CaseId>.mail.json` | Agent composes queries; vendor replies arrive via `/simulation/supplier/email-reply` | YES — deleted |
| 7 | Memos + proposals + policy decisions + activations | `Gate.Web/data/case-cache/<MemoId>.memo.json` | `MemoRunner` + Policies UI | YES — deleted |
| 8 | Vendor-identity pendings | `Gate.Web/data/case-cache/<CaseId>.vendor-identity.json` | Agent intake | YES — deleted |
| 9 | Tool-loop transcripts | `Gate.Web/data/case-cache/<CaseId>.transcript.jsonl` | Agent tool loop | NO — `ClearAsync` deletes `*.json` only, so `.jsonl` transcripts survive reset |
| 10 | Memo inbox originals | `<content-root>/data/memo-inbox/` | `MemoSubmitService` on memo submit (dir does not exist yet — no memo has been submitted in this checkout) | NO |
| 11 | Knowledge base, whole-file versions | `company/company-kb.json` + `company/company-kb.kbv2.json` | `KbEditService` publish mints a new whole file; priors never overwritten | NO |
| 12 | Rule packs | `Gate.Compliance/Packs/*.json` (the runtime reads the source tree) | `MemoRunner` publish (stage 7) | NO |
| 13 | Screening snapshot | Embedded resource (`Screening.snapshot.json`), loaded from the assembly | Build only | N/A (not runtime data) |
| 14 | Model cache (Ask answers, tool turns, transcriptions) | OS temp `gate-model-cache/` (`GATE_MODEL_CACHE`) | `CachedModelClient` on every model call | NO — reset only counts entries |
| 15 | Vision read cache | `assets/read-cache/*.carton-read-v1.json` (repo files) | Dock/invoice transcription | NO |
| 16 | Dock photos + invoice photos | `assets/dock/` etc. (repo files) | Shipped with the repo; uploads are test-only bytes | NO |
| 17 | Carton→image display map | `scenarios/demo-v1.json` (`cartons[].visibleIn`), read at render time | Shipped with the repo | N/A (input file, but the UI reads it live — see §5) |
| 18 | Opening-row chips ("opening data") | `scenarios/run-manifest.json`, read at render time | `ScenarioLoader` | Rebuilt (see row 3) |
| 19 | Model spend readout | Process memory (`ModelSpendTracker`) | Metered live calls only | NO — only reported |
| 20 | Company identity (LE-1 "Fictional Smart-Glasses Co") | KB `legalEntities` + `leadership/company-identity` (files, see row 11) | Shipped with the repo | NO — and it is not stored in ERP and not shown in the top bar, on PO docs, or as sender context |

Key references: startup load `Gate.Web/Program.cs:139-144`;
`Gate.Web/ScenarioEndpoints.cs:12-56`;
`Gate.Erp/ScenarioLoader.cs:26-214` (load + `ResetAsync`);
`Gate.Web/Services/ReplayService.cs:72-157`;
`Gate.Web/Services/CaseCacheStore.cs:81-96`;
`Gate.Web/Services/CaseViewService.cs:259-269,601-616`;
`Gate.Compliance/Screening.cs:24-50`;
`Gate.Compliance/ComplianceEngineMemo.cs:40-48,371-398`.

## 2. Banned product-bar words in UI text

Rendered text the user can see (code identifiers excluded):

- Top bar on EVERY page (`Gate.Web/Components/Layout/MainLayout.razor:12`
  renders `ReplayControls`): "Reset", "Reset to scenario start?",
  "Confirm reset", "Replay", "Enter/Exit replay", spend badge titled
  "Model spend this run (live calls only; cached answers are free)"
  (`ReplayControls.razor:11-34`).
- Customer shop (`ShopPanel.razor`): "Customer shop — seeded orders",
  "No seeded orders in the run manifest yet.", "— pick a seeded order —",
  "Candidate vendors (from scenario):", "None in the scenario; the run
  proceeds without a candidate map.", "the minted sales order is not in
  the case cache yet."
- Cases list (`Cases.razor:26,48`): "No cases in the cache yet. Place an
  order from the outside world.", per-row "scenario @file.Case.ScenarioName".
- Case detail (`CaseDetail.razor:252`): "has no case file."
- Governance KB space (`GovernanceKbSpace.razor:81,102,143,147`):
  "unused" on every uncited row, "{user} approves as the {space} owner"
  (misleading — see §8), "Entry JSON (full entry as authored)", "Load
  current entry JSON".
- Governance rules (`GovernanceRules.razor:39,69` + `GovernanceViewModels.cs:91,95`):
  "Blocks this run", "Shipped rule; no proposal citations."
- Rendered notes from `CaseViewService`: "No run manifest yet; seed
  documents are unknown until a scenario load.", "Run manifest {path}
  did not parse.", "Run manifest read failed.", "Seeded invoice {n}
  (event '{e}') names no resolvable vendor…" (`CaseViewService.cs:576-593,329-330`).
- Rules page source column renders pack `source` text verbatim, including
  "Fictional COO memo and board minutes (company policy)" and the rule
  title "Single-vendor concentration cap (documented only in demo)."
  (`Gate.Compliance/Packs/corp.json`).
- KB tables render `createdBy/modifiedBy/approvedBy` = "system" with
  today's date on every row, and versions "kbv1"/"kbv2"
  (`company/company-kb.kbv2.json`, shown in `GovernanceKbSpace.razor:62-77`).

## 3. Banned words in stored data and document numbers

Observed in `Gate.Web/data/erp.db`, `Gate.Web/data/gate.db`,
`Gate.Web/data/case-cache/`, `company/`, `Gate.Compliance/`:

- `Approvals.Comment` = "scenario seed" on every loader-created approval.
- `VendorInvoices.SupplierInvoiceNumber` = `SEED-INV-PO-1002/1003/1004`
  (also contains the PO number — see §4).
- `Items`: `WID-7` = "Widget (lifecycle seed)".
- `Vendors.Email`: `sales@lifecycle.example` (V-L); the other three
  mismatch their vendor names (see §10).
- Lifecycle document numbers: `ASN-LC-1`, `PACK-LC-1` (packing slip on
  `RCT-1001`), `INV-LC-R/M/S/A/D/P/G/F`, carton `CTN-LC-1`, lot `LOT-LC-1`.
- `gate.db` `meta` holds `scenario` = "demo-v1", the loader machine's
  absolute `scenarioFile` path, and a full `scenarioJson` copy of
  `demo-v1.json` (whose own `$comment` says "demo scenario",
  "demo seed", "fictional").
- Case files: `caseId` + `scenarioName` = "demo-v1-…", e.g.
  `demo-v1-PO-PO-1001`; event ids `…-e001`; decision ids `…-d001`.
- Opening decision cards carry reason "Seeded pending approval for…"
  (`Gate.Agent/CaseRunner.OpeningSeed.cs:139`).
- KB: every entry `createdBy/modifiedBy/approvedBy` = "system",
  `createdAt/modifiedAt` = today, versions "kbv1"/"kbv2"; legal entity
  name "Fictional Smart-Glasses Co" (`company/company-kb*.json`).
- Activation ids `KB-{version}-{section}-{entry}-{timestamp}`
  (`Gate.Web/Services/KbEditService.cs:126`) embed the kbv version.
- Screening snapshot id "screening-demo-v1"
  (`Gate.Compliance/Screening/snapshot.json:3`).
- Stored JSON shape leaks internal flags: case `stage`/`kind` serialize
  as integers (`"stage": 8`, `"kind": 23`).

## 4. Document numbers built from another document's number

- `SEED-INV-{PoNumber}` — supplier invoice numbers minted from the PO
  number (`Gate.Erp/ScenarioLoader.cs:151`, stored, §3).
- `ASN-{PoNumber}` / `PACK-{PoNumber}` — ASN prefill minted from the PO
  number (`Gate.Web/Services/SimulationUiService.cs:695`); live rows
  exist: `Asns.ASN-PO-1001` on `PO-1001`, `PACK-PO-1001` as
  `SupplierPackingSlipNumber` on `RCT-1002`.
- Case ids `{scenario}-{docType}-{docNumber}` embed the scenario name
  plus the ERP number (`Gate.Agent/CaseStore.cs:37-43`).
- Event/decision ids `{caseId}-eNNN` / `{caseId}-dNNN` derive from the
  case id (`CaseRunner.OpeningSeed.cs:132-134`, memo/CaseWork equivalents).
- Otherwise each ERP type mints from its own sequence (`SO/PR/PO/RCT/V/
  PAYDRAFT/PAY/HLD`, all `{prefix}-{number}`, width 4, start 1001 —
  `Sequences` table). Gaps: vendor masters `V-A…V-L` predate the `V`
  sequence; `HLD` never mints (`Holds` table is empty; `HoldId` shape
  unverified); payments show both `PAYDRAFT-*` and `PAY-*` prefixes.

## 5. Files the running app reads as data (bar: files are never runtime data)

- `scenarios/demo-v1.json` — read by the loader AND at UI render time
  for the carton→image map (`CaseViewService.cs:601-616`).
- `scenarios/run-manifest.json` — read at UI render time for opening
  chips and the Records ledger (`CaseViewService.cs:569-595`).
- `Gate.Web/data/case-cache/*.json` (+ `*.mail.json`, `*.memo.json`,
  `*.vendor-identity.json`) — the entire platform side: cases, events,
  decisions, mail, memos, proposals, activations.
- `company/company-kb*.json` — KB (default path
  `ComplianceEngineMemo.cs:40`).
- `Gate.Compliance/Packs/*.json` — live rules, including publish writes
  into the source tree (default dir `ComplianceEngineMemo.cs:45`).
- `assets/dock/`, `assets/read-cache/` — photos + reads (allowed shape:
  attachments + model cache, but no db row points at them).
- Config (allowed): `appsettings*.json` + `GATE_*` env vars.
- Model cache (allowed): OS-temp `gate-model-cache/`.

## 6. Which test fixtures reach the app db

None write the app databases. Every suite that needs ERP or files uses
temp dirs or the in-memory fake:

- EF suites use temp-dir sqlite (`EfCoreTestSupport.cs:24-27`,
  `SeedConsistencyTests.cs:50-72`, `NumberSequenceTests.cs:129`,
  `VendorResetTests.cs:69,166`, `EfCoreBatch3Tests.cs:290`,
  `EfCoreControlTests.cs:340`).
- Scenario/full-cycle suites load `scenarios/*.json` into temp
  `gate.db` + temp manifest + in-memory ERP + temp case dir
  (`ScenarioCaseTests.cs:99-111`); live suites add a temp model cache
  (`NewOrderFullCycleLiveTests.cs:156-160`).
- UI suites use temp dirs (`UiTestSupport.cs:26-27`); KB/pack suites
  use temp dirs (`CompanyKbWriterTests.cs:570`,
  `PackPublishTests.cs:182`).
- `SeededErp.cs` builds the in-memory fake only (never sqlite).

Reads of repo files (no app-db contact): `scenarios/*.json` (12 test
files), `company/company-kb.json` (`Kbv2Seeds.cs:81-87`,
`CompanyKbMirrorTests.cs:21`), `assets/` (vision suites). Watch item:
`RecordsViewTests.cs:15-21,33-53` mutates `GATE_MANIFEST`/`GATE_SCENARIO`
process env (restored in teardown) — a parallel-run hazard, not app-db
contact. No test references `Gate.Web/data`.

## 7. Live calls and posts with no clicks

Two paths write with zero clicks, and both are model-free by
construction: startup `EnsureLoadedAsync` (loads the ERP when the store
is empty — `Program.cs:139-144`) and top-bar Reset (wipe + reload +
clear + opening shells + pickup — `ReplayService.cs:72-157`, "zero live
calls" header comment). Startup posts ~40 documents; reset re-posts all
of them plus case shells. All stamps land on today (§9).

Every live model call traces to exactly one click that fans out through
`CaseRunner.AdvanceAsync` (Spark tool loop, up to 50 turns per advance,
each turn executing C#-validated tools that post to ERP —
`CaseRunner.ToolLoop.cs:21-79`):

- Each of the 8 `/simulation/*` endpoints runs its handler and then
  advances (`SimulationEndpoints.cs:20-154`).
- Each Decisions resolve advances after applying
  (`DecisionOutcomeService.cs:70-80`).
- Each memo submit drives propose→validate→what-if
  (`MemoSubmitService.cs:43-46`); each Ask sends one request (§11).

So "2 live calls and posted with no clicks" is one input/decision click
fanning out: the advance loop placed 2 calls and posted the resulting
writes without further clicks. Verify per case in its
`<CaseId>.transcript.jsonl` (which survives reset, §1 row 9) plus the
spend badge. No page-load path posts or calls the model: all
`OnInitializedAsync` handlers only reload lists
(`Cases.razor:66-79`, `Decisions.razor:188-192`, `CaseDetail.razor:245-259`,
`Outside.razor:65-68`, `DockPanel.razor:174-177`, governance pages).

## 8. Can the AP clerk approve a regulatory KB edit?

No. `KbEditService.PublishAsync` publishes only when the acting user is
the space owner or their active dated delegate, else it refuses with
"Refused: {user} is not the {space} owner…" (`KbEditService.cs:98-110`).
`kb-edit-regulatory` is owned by Dana Director and the only delegation
is Dana→Del Delegate (`company-kb.kbv2.json` approval matrix), so Amy AP
Clerk's publish is refused. What the UI says is misleading: the edit
form renders "{user} approves as the {space} owner ({owner})" for
whoever is acting (`GovernanceKbSpace.razor:101-104`), which reads as
permission. The service is the control; the sentence is the defect.

## 9. Decision cards: source, routing, approval storage

- Cards come from files, not tables: Decisions cards from every
  `decisions[]` entry in every case file
  (`CaseViewService.cs:35-51`); policy cards from memo files
  (`MemoFileStore.cs:29-36`). Routing is an exact `RequestedTo` match
  per acting user (`DecisionBoard.cs:44-51`); opening shells add one
  card per ERP pending approval at reset (`CaseRunner.OpeningSeed.cs`).
- Case-document approvals are stored TWICE: once as an ERP `Approvals`
  row via `RecordApprovalAsync`, once as the updated `Decision` in the
  case file (`CaseRunner.Handlers.cs:873-934`, same shape for
  PR/PO/Invoice/Payment).
- Policy approvals are stored THREE times: the decided `Decision` plus
  the `RuleActivation` in the memo file (`MemoStore.cs:27-34`,
  `MemoRules.cs:474-486`), plus the version-bumped pack JSON on disk.

## 10. Vendor emails vs vendor names (stored)

| Vendor | Name | Stored email | Match? |
|---|---|---|---|
| V-A | CinderVolt Energy Ltd | sales@cheapcells.example | No |
| V-B | Meridian Cellworks Inc | sales@compliantcells.example | No |
| V-C | Formosa Fasteners Co | sales@screwsco.example | No |
| V-L | Bluepine Parts Co | sales@lifecycle.example | No (+ banned word) |

Source: `Vendors` table (populated from `scenarios/demo-v1.json` vendor
`email` fields). Emails are used as reply routing
(`SimulationUiService.cs:476-510`).

## 11. Exactly what Spark receives from the Ask bar

The "Ask the agent why" box per decision card calls
`AskWhyService.AskAsync(caseId, decisionId, question)`
(`AskWhyService.cs:46-86`), which builds exactly one request:

- `ModelRequest(question, "Decide using only this evidence: {evidence}")`
  — the typed question is the prompt; the system text is that fixed
  prefix (`P2pAgent.cs:329-331`). No history, no tools, default token
  budget. Photos are NOT sent — only file names plus cached-read text.
- `{evidence}` = "Case {id} (SO {so}, item {item}, stage {stage}).
  Decision {id}: {Kind} '{title}', status {status}, requested to {user}.
  Rule {rule}. Reason: {reason}. Amount {n} {cur}. Document {type}
  {number}." plus one "Photo {file}. Cached read: {text}." per evidence
  ref that resolves (`AskWhyService.cs:90-133`).
- Transport: `CachedModelClient` → `MeteredModelClient` → `SparkModelClient`
  (live), or `CachedModelClient` → `FakeModelClient` when no API key
  (`Program.cs:34-46`). In replay mode a cache miss refuses instead of
  calling (`AskWhyService.cs:69-74`). "Spend so far: …" is appended to
  the SHOWN answer only, never sent (`AskWhyService.cs:88`).

## 12. Links, citations, versions (user-reported defects, confirmed)

- KB "Used by" column: plain "unused" text when uncited; counts
  ("N proposal(s), M activation(s) · rules …") when cited — no links
  either way (`GovernanceKbSpace.razor:78-91`). The entry page lists
  memo/proposal/rule uses as text, also unlinked
  (`GovernanceKbEntry.razor:82-92`).
- Rules page: shipped rules show "Shipped rule; no proposal citations."
  (`GovernanceViewModels.cs:95`); company rules link grounded KB
  entries but nothing links a rule to its memo/proposal/activation
  (`GovernanceRules.razor:48-90`). The activation record carries
  `MemoId` + `ProposalHash` (`MemoRules.cs:474-486`) but no surface
  renders it, and CORP-01/02 carry only the "Fictional COO memo…"
  source string with no memo id, author, approver, or date.
- KB versions as a whole: each publish mints a new `company-kb.kbvN.json`
  (engine loads `company-kb*.json` from the dir —
  `ComplianceEngineMemo.cs:371-378`); per-entry `version` fields exist
  but ride the file version. Rules version per pack file (`"version":
  "v2"`), not per rule.

## 13. Dates, actors, stamps (why everything looks like today)

- The loader stamps `DateTimeOffset.UtcNow` on every document, approval,
  receipt, and confirmation it creates; there is no business-date input,
  so a fresh load dates the entire company history today
  (`ScenarioLoader.cs:44`, `CreateApprovedPoAsync`, `SeedLifecycleAsync`).
- KB entries: all `createdAt/modifiedAt` 2026-11-01, all actors "system"
  (§3). Case events: actor `p2p-agent` (or the deciding user on
  decisions), timestamps of the run. Receipts: `Source` = "p2p-agent" with
  `CreatedBy` = "Maya Planner" — agent work attributed to a person.
- Stamp coverage per table: `Approvals` has `ActedBy`+`Timestamp`;
  `VendorInvoices` has `PostedAt`; `PurchaseOrders`,
  `PurchaseRequisitions`, `Payments` have `CreatedBy` but NO created/at,
  approved-by/at, or posted-by/at columns (§1 queries). "Who approved,
  when" exists only in `Approvals`; "who posted, when" mostly nowhere.
- Date ranges that DO vary: delegation window 2026-10-20–2026-11-10
  (scenario data), pack `adopted` 2026-03-16 (pack data).
