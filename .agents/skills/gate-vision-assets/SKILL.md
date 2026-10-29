---
name: gate-vision-assets
description: Owns the dock image set, label reads, accuracy scoring, and offline read cache.
---

# Gate Vision Assets

Owns everything the dock camera sees. Deterministic files in, cached reads out, replayable offline.

## Ownership

- Owns: `assets/` dock images, the carton ground-truth seed file, the read-cache manifest, plus its future scoring checks under `Gate.Eval/Vision/`.
- Never touches: any file under `Gate.Erp/`, `Gate.Contracts/`, `Gate.Agent/`, `Gate.Compliance/`, `Gate.Web/`, contract DTOs, the adapter interface, `schema.md`.
- Contract rule: DTOs, the adapter interface, and `schema.md` change in the lead session only.

## Rules

- The set is 4 images: 3 pallet views (front-left, front-right, rear) plus 1 relabel reshot. Pallet-level, several cartons each, the whole shipment covered from more than one side.
- The ground-truth seed file lists every carton (12 total): carton id, part, PO, mark present or not. This file is the accuracy oracle.
- The reading model finds and reads every label on the visible cartons in each image. C# decides pass, hold, or human review per carton and cites the rule. Seeded failures: one carton missing the lithium-battery mark, one hidden or unreadable carton, one visibly damaged carton.
- Scoring is per carton against ground truth, reported as read rate plus correct-cite rate. No aggregate-only scores.
- Legibility backstop: if a pallet image stays unreadable after a few tries, composite labels from seed data and stop regenerating. Never burn credits on regeneration churn.
- Every model result is cached with image hash, prompt version, and timestamp. Demo replays read cache only. Upload stays for testing, never for the demo path.
- No business values in code. Quantities, limits, vendor names, carton ids, document numbers, dates, and similar domain values come from the scenario file or run manifest, never from C# constants or literals. Code holds logic only.
- Tests may carry their own small data but must not depend on the demo numbers.
- If the scenario is missing a value you need, ask the lead agent instead of inventing one.
- Never change policy to make numbers fit. On a data/policy mismatch, surface it; do not retune reads or scoring to force a pass.

## Done criteria

Done when 4 images sit offline with cached reads, the accuracy log scores all 12 cartons, and a full replay makes zero live calls.
