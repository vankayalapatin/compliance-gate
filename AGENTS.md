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

## Git rule

Never git push. Commits happen only when explicitly asked.
