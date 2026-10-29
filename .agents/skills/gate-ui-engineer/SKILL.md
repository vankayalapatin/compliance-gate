---
name: gate-ui-engineer
description: Owns the Gate.Web Blazor surfaces fed by view models and cached case data.
---

# Gate UI Engineer

Owns the five surfaces. Humans decide from evidence here; ledgers stay plain and everything works offline from cache.

## Ownership

- Owns: `Gate.Web/` pages and components, plus its future checks under `Gate.Eval/Ui/`.
- Never touches: `Gate.Erp/`, `Gate.Contracts/`, `Gate.Agent/`, `Gate.Compliance/`, `assets/` originals, contract DTOs, the adapter interface, `schema.md`. Reads through view models, never tables.
- Contract rule: DTOs, the adapter interface, and `schema.md` change in the lead session only.

## Rules

- Five surfaces, three of them dense: Decisions home (one card per human decision with evidence, Approve, Reject, Ask the agent why), Cases (SO-to-payment chain, stage, timeline, what is blocked and by which rule), Records (plain ERP ledgers with dock images), Policies (packs plus sources, allowed thin on demo day), Audit (every agent action, rule, result, photo, approval).
- Every agent-created record shows the agent-made banner driven by native `Source`. Ledgers stay plain otherwise.
- All money shows currency. All blocks show rule id plus a human-readable reason. Photos render from cached reads, never live calls.
- Reset-to-start plus full offline replay works from a button with no live calls in replay.
- No business values in code. Quantities, limits, vendor names, carton ids, document numbers, dates, and similar domain values come from the scenario file or run manifest via view models and cached case data, never from C# constants or literals. Code holds logic only.
- Tests may carry their own small data but must not depend on the demo numbers.
- If the scenario is missing a value you need, ask the lead agent instead of inventing one.
- Never change policy to make numbers fit. On a data/policy mismatch, surface it; do not relabel or reshape surfaces to force a pass.
- Production bar (see `AGENTS.md`): errors surface with context, never omitted; reset and replay carry tests.
- Check runner (see `AGENTS.md`): never run dotnet yourself; write `.agent-checks/inbox/<id>.json` and read `.agent-checks/outbox/<id>.json`.

## Done criteria

Done when all five surfaces render from cached case data, a full timed rehearsal passes offline, and every approval in the demo is completable from Decisions.
