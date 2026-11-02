# AGENTS.md

## Product bar

ComplianceGate is the software name. It is a product a company runs every
day: an ERP plus an agent platform. Not a demo, a scenario, or a run.

The ERP stays strict and simple. The agent does the thinking. People make
the decisions. The ERP's controls and the company's rules apply to people
and agents alike.

- System of record: the ERP has its own db; the agent platform has its own
  db for cases, events, decisions, knowledge articles, rules, memos,
  proposals, and exceptions. Every screen reads from these two dbs. The
  agent reaches ERP only through `IErpAdapter`. The two dbs refer to each
  other by ERP document and account numbers.
- Files are never runtime data, except config, model cache, and attachments
  that a db row points to.
- Company data, not seeded: masters and history enter once through the ERP
  and platform services, dated in the past, done by named people. Data
  changes only through actions in the app. Reset restores a saved copy of
  both dbs; it runs no loader and no model call.
- Business values live in company data, never in code. Tests build their
  own data and never reach the app db.
- Numbers and history: every document number comes from its own number
  sequence, never from another document's number. Every record shows who
  created, approved, and posted it, and when.
- Data links both ways: vendor <-> POs, invoices, payments, documents;
  item <-> stock, open POs, demand, BOM; PO <-> PR, ASN, receipts,
  invoices, payments, case; rule <-> source: KB articles, blocked docs;
  article <-> rules citing it.
- ERP screens follow D365 F&O: list page to details page with header,
  lines, and related docs; actions offered only for the record's state
  and the user's role. Inputs pick real records, no free-text IDs.
- Product language only. Never in UI text, stored data, or numbers: seed,
  seeded, scenario, demo, fictional, lifecycle, "run" meaning a demo run
  (payment run is fine), system-as-person, kbv, case file, JSON keys,
  internal flags, True/False, endpoint mentions.
- Operator controls such as reset and replay live only on one Admin page.
- All company, vendor, and person names are fictional. Never use a real
  company name.
- Crawler test: opens every list and every record linked from it, as every
  user; fails on any error page.
- Outside-world inputs (order webhook, supplier ACK/ASN/invoices, dock
  photos, secured notification) arrive through the app's input endpoints
  only; they never encode, sequence, or bypass internal logic.
- Controls are enforced in the ERP layer. The agent picks its next tool
  from case state; no fixed step sequence lives in code. External calls
  carry timeouts and retries. Errors are logged with context and surfaced;
  nothing is silently swallowed or omitted. Every rule and every refusal
  has a test, including its failure path.

Spec: `docs/DATA-MODEL.md` plus `docs/COMPANY-DATA.md`. Audit:
`docs/AUDIT-DATA.md`. Early-demo history, frozen, not spec:
`docs/archive/`.

## Standing roles

Six reusable skills in `.agents/skills/`. Invoke by skill id. Run several in
parallel with one spawn per role. Writers get worktree isolation. The reviewer
stays read-only in the shared checkout.

- `gate-erp-architect`: true D365-style P2P data design.
- `gate-compliance-engineer`: regulation packs, tiers, cited verdicts.
- `gate-vision-assets`: dock images, reads, accuracy, cache.
- `gate-agent-engineer`: P2P orchestration, shortfall math, matching.
- `gate-ui-engineer`: Blazor surfaces from view models and cache.
- `gate-reviewer`: judges only, edits nothing.

## Folder ownership

- `gate-erp-architect`: `Gate.Erp/`, `Gate.Eval/Erp/`.
- `gate-compliance-engineer`: `Gate.Compliance/`, `Gate.Eval/Compliance/`.
- `gate-vision-assets`: `assets/`, `Gate.Eval/Vision/` (reserved).
- `gate-agent-engineer`: `Gate.Agent/`, `Gate.Eval/Agent/`.
- `gate-ui-engineer`: `Gate.Web/`, `Gate.Eval/Ui/` (reserved).
- `gate-reviewer`: owns nothing, edits nothing.
- Lead session only: `Gate.Contracts/`, `Gate.Eval/SeededErp.cs`, `schema.md`, the ERP adapter interface.

## Contract rule

DTOs, the ERP adapter interface, and `schema.md` change in the lead session
only. Member roles read them and request changes in chat.

## Business data rule

- No business values in code. Quantities, limits, vendor names, carton ids,
  document numbers, dates, and similar domain values come from company data
  in the two dbs, never from C# constants or literals. Code holds logic only.
- Tests build their own data and never reach the app db. They must not
  depend on company-data numbers.
- If company data is missing a value an agent needs, the agent asks the
  lead agent instead of inventing one.
- Never change policy to make numbers fit. If data and policy disagree,
  surface the mismatch; do not retune thresholds, rules, or packs to force
  a pass.

## Agent check runner

The sandbox compiles but cannot run tests (local sockets are blocked there),
so a watcher on your machine runs checks on agents' behalf.

- Start it once and leave it running: `./scripts/agent-check.sh`
- Agents never run `dotnet build` or `dotnet test` themselves. They write one
  request file per check: `.agent-checks/inbox/<unique-id>.json`
  `{ "kind": "check", "target": "Gate.Eval/Gate.Eval.csproj", "filter": "Category!=Live" }`
  Kinds: `check` (build + tests), `build` (optional `target`), `test`
  (optional `target`/`filter`, defaults to the suite minus Live), `live`
  (live tests only), `smoke` (app smoke).
- Results land in `.agent-checks/outbox/<unique-id>.json` plus the full log
  `<unique-id>.log`; the request moves to `.agent-checks/archive/`. Agents
  read the outbox file on a later turn — never poll in a loop.
- Keep ids unique (`<date>-<n>`), one check per file. Inbox, outbox, and
  archive traffic is git-ignored.

## Git rule

Never git push. Commits happen only when explicitly asked.
