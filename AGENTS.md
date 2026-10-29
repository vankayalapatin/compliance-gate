# AGENTS.md

Project rules for ComplianceGate. Full record: `PROJECT.md`.

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

- No business values in code. Quantities, limits, vendor names, carton ids, document numbers, dates, and similar domain values come from the scenario file or run manifest, never from C# constants or literals. Code holds logic only.
- Tests may carry their own small data but must not depend on the demo numbers.
- If a scenario is missing a value an agent needs, the agent asks the lead agent instead of inventing one.
- Never change policy to make numbers fit. If data and policy disagree, surface the mismatch; do not retune thresholds, rules, or packs to force a pass.

## Production bar

- Simulation endpoints inject external inputs only: webhooks, camera images, ASNs, supplier emails. They never encode, sequence, or bypass internal logic.
- The built-in ERP is real EF Core with migrations and transactions. Controls are enforced in the ERP layer. The in-memory fake ERP is for unit tests only and never backs the app.
- The agent picks its next tool from case state. No fixed step sequence lives in code.
- External calls carry timeouts and retries. Errors are logged with context and surfaced; nothing is silently swallowed or omitted.
- Every rule and every refusal has a test, including its failure path.

## Agent check runner

The sandbox compiles but cannot run tests (local sockets are blocked there),
so a watcher on your machine runs checks on agents' behalf.

- Start it once and leave it running: `./scripts/agent-check.sh`
- Agents never run `dotnet build` or `dotnet test` themselves. They write one
  request file per check: `.agent-checks/inbox/<unique-id>.json`
  `{ "kind": "check", "target": "Gate.Eval/Gate.Eval.csproj", "filter": "Category!=Live", "scenario": "scenarios/demo-v1.json" }`
  Kinds: `check` (build + tests), `build` (optional `target`), `test`
  (optional `target`/`filter`, defaults to the suite minus Live), `live`
  (live tests only), `smoke` (scenario smoke, optional `scenario`).
- Results land in `.agent-checks/outbox/<unique-id>.json` plus the full log
  `<unique-id>.log`; the request moves to `.agent-checks/archive/`. Agents
  read the outbox file on a later turn — never poll in a loop.
- Keep ids unique (`<date>-<n>`), one check per file. Inbox, outbox, and
  archive traffic is git-ignored.

## Git rule

Never git push. Commits happen only when explicitly asked.
