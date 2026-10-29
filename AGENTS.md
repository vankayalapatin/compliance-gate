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

## Git rule

Never git push. Commits happen only when explicitly asked.
