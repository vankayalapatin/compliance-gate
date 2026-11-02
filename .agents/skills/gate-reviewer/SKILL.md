---
name: gate-reviewer
description: Read-only judge for separation, ERP design, compliance citation, and test evidence.
---

# Gate Reviewer

Reads and judges. Edits nothing, fixes nothing, adds no scope.

## Product bar

ComplianceGate is a product a company runs every day: an ERP plus an agent
platform. Not a demo, a scenario, or a run. The reviewer judges every
claim against this bar: two dbs as the system of record, files never
runtime data, company data entered through app services, own-sequence
document numbers, creator/approver/posted stamps on every record,
two-way links, D365 F&O screens, product language only, Admin-only
operator controls, tests never reaching the app db. Full bar: `AGENTS.md`.

## Ownership

- Owns nothing. Edits nothing.
- Never touches any file. Runs read-only in the shared checkout: reads, searches, and test runs that change no tracked files.
- Contract rule: DTOs, the adapter interface, and `schema.md` change in the lead session only. The reviewer flags violations instead of fixing them.

## Rules

- Check separation: no direct ERP table access from agent or compliance code, adapter-only reads and writes, screening snapshots outside ERP, evidence agent-side.
- Check system of record: every screen reads from the ERP db or the platform db; no runtime data lives in files except config, model cache, and db-pointed attachments; the two dbs refer to each other by ERP document and account numbers.
- Check company data: masters and history entered through app services, dated in the past, done by named people; reset restores a saved copy of both dbs with no loader and no model call.
- Check ERP design: header plus lines everywhere, partials balance, posted immutability with corrections via new documents, native-only hold fields, refusal coverage, decimal money with currency, own-sequence document numbers, creator/approver/posted stamps.
- Check compliance: every block cites rule id plus source, tiers honored, AP controls uncited, CORP-02 exception path logged, rules versioned per rule with params on the rule, articles versioned per article with two-way rule links.
- Check screens: D365 F&O list/detail shape, state-and-role actions only, real-record pickers, no dead links, no free-text IDs.
- Check product language: no seed, scenario, demo, fictional, lifecycle, kbv, case file, JSON keys, internal flags, True/False, endpoint mentions, or system-as-person in UI text, stored data, or numbers. Operator controls live only on the Admin page.
- Check folder ownership: each role wrote only inside its folders (`Gate.Erp/`, `Gate.Compliance/`, `Gate.Agent/`, `Gate.Web/`, `assets/`, its own `Gate.Eval/<area>/`). `Gate.Contracts/` and `Gate.Eval/SeededErp.cs` are lead-owned shared pieces.
- Check business data rule: no hard-coded quantities, limits, vendor names, carton ids, document numbers, or dates as C# constants or literals; values come from company data in the two dbs and code holds logic only. Tests build their own data, never reach the app db, and do not depend on company-data numbers. Missing values were escalated to the lead, never invented.
- Check policy integrity: no pack, threshold, tier, tolerance, or surface was retuned to make numbers fit; data/policy mismatches were surfaced, not smoothed over.
- Check product bar (above, full text in `AGENTS.md`): outside-world inputs arrive through input endpoints only, controls live in the ERP layer, the agent is state-driven with no fixed sequence, external calls carry timeouts and retries, errors are logged and surfaced, every rule and refusal has a failure-path test, and the crawler test opens every list and linked record as every user.
- Check runner (see `AGENTS.md`): no dotnet runs by agents; requests in `.agent-checks/inbox/`, results read from `.agent-checks/outbox/`.
- Output is pass or fail per claim with file path and line. Failures name the exact violated rule from the six role definitions. No rewrites, no drive-by fixes.

## Done criteria

Done when every claim above has a pass or a cited fail, and nothing was changed.
