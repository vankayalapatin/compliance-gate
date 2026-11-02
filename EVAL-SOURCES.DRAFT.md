# Eval sources (DRAFT — companion to README.DRAFT.md)

> Every number in the README draft traces to a file below, grouped by section. Items marked "author note" came from the author, not the repo.

## Opening and product intro

- `docs/PRODUCT.md` — problem, story, gates, surfaces.
- `docs/DECISIONS.md` — decision-by-decision record behind the product claims.
- `AGENTS.md` — two-db system of record, single adapter interface, human money approvals.

## Dock-label eval

- `assets/dock-eval-50/spark-report.md` — easy 50/50 and per-problem breakdown.
- `assets/dock-eval-50/build-record.json` — easy 10 bases, 50 finals, per-problem counts.
- `assets/dock-eval-50/accuracy-by-type.csv` — easy per-image rows.
- `assets/dock-eval-hard/spark-report.md` — hard 170/170 and per-problem breakdown.
- `assets/dock-eval-hard/build-record.json` — hard 30 finals, 170 cartons, 6 bases, per-effect counts.
- `assets/dock-eval-hard/accuracy-by-carton.csv` — hard per-carton rows.
- `assets/dock-eval-50/answer-key.json`, `assets/dock-eval-hard/answer-key.json` — oracles decided before any image was built; no reader loads them.
- `assets/dock-eval-50/eval-manifest.json`, `assets/dock-eval-hard/eval-manifest.json` — what readers actually see (filenames only).
- `assets/dock-eval-50/read-prompt.txt`, `assets/dock-eval-hard/read-prompt.txt` — shared transcription-only prompt.
- `assets/dock-eval-50/manual-check.md` — 14 photographic images, 36 code-certain.
- `assets/dock-eval-hard/manual-check.md` — 10 dent photos, per-base placement check, 6 text-free scenes.
- `assets/README.md` — 4000-token read budget, label drawing, scoring layout.
- `docs/DECISIONS.md` — truncation twice at 2000, unique-lot revision, mark redraw.
- `Gate.Eval/Vision/` — model-agnostic C# scoring.
- `assets/accuracy.csv` — receiving-flow photos: verdict 12/12, cite 12/12, fully-read 11/12.
- `assets/cartons.json` — receiving-flow ground truth: 12 cartons.

## Spark vs Spark Open on Baseten

- `assets/dock-eval-compare/spark-vs-baseten.md` — accuracy tables, avg ms/photo, avg in/out tokens.
- `assets/dock-eval-50/timings-spark-rerun.json`, `assets/dock-eval-50/timings-baseten.json` — easy per-read ms and token rows.
- `assets/dock-eval-hard/timings-spark-rerun.json`, `assets/dock-eval-hard/timings-baseten.json` — hard per-read ms and token rows.
- Cost figures use Meta's published Muse Spark rates: standard tier $1.25 per 1M input / $4.25 per 1M output; contributor tier $0.10 per 1M input / $0.20 per 1M output. Each figure names its rate.
- Per-shipment line: 4 reads (3 pallet photos + 1 reshot) times hard-tier per-photo token counts (3,345 in + 1,768 out), at both rate tiers.
- Baseten ran on hackathon credits, so no per-token figure: author note, no repo file.

## Muse Image findings

- `assets/dock/build-record.json` — IMG-04 AI-labeled 1/1; IMG-01/02/03 fallback 3/3 after 1 attempt each with verify-mismatch reasons.
- `assets/README.md` — 2/2 receiving-flow bases, 10/10 easy-eval bases, labels-stay-code-drawn conclusion.
- `assets/dock-eval-hard/manual-check.md` — 6 hard-tier text-free scenes, 1 call each, no retries.
- `assets/key-test.md` — 1-call endpoint proof, HTTP 200 with image payload on muse-image-1.0.
- `docs/DECISIONS.md` — image-model pinning, edits-call learnings, scratch-folder rule.
- `.gitignore` — generated_images/ is git-ignored scratch.

## Agent results

- Pending author fill-in; no files yet.

## How this was built

- `AGENTS.md` — 6 role skills, folder ownership, contract rule.
- `.agents/skills/gate-erp-architect/SKILL.md`, `.agents/skills/gate-compliance-engineer/SKILL.md`, `.agents/skills/gate-vision-assets/SKILL.md`, `.agents/skills/gate-agent-engineer/SKILL.md`, `.agents/skills/gate-ui-engineer/SKILL.md`, `.agents/skills/gate-reviewer/SKILL.md` — what each role owns.
- `docs/PRODUCT.md`, `docs/DECISIONS.md`, `assets/README.md` — Spark reading plus tool-calling orchestration; C# keeps verdicts and controls.
- `scripts/agent-check.sh` — checks run outside the sandbox because local sockets are blocked there (header comment); script authorship is an author note.
- Glimmer note (local MacBook trial too slow; hackathon key served only Spark Open): author note, no repo file. Repo names Glimmer only as the later second model in `assets/README.md`, `docs/DECISIONS.md`, `assets/dock-eval-50/answer-key.json`.
- SAM 3.1 rationale: author note; repo mentions the id only in the model-discovery list in `assets/key-test.md`.

## Limits

- Generated-photos-with-known-key claim: `assets/dock-eval-50/answer-key.json`, `assets/dock-eval-hard/answer-key.json`, `assets/README.md`.
- 100% figures: `assets/dock-eval-50/spark-report.md`, `assets/dock-eval-hard/spark-report.md`.
- Not tested on real docks: author statement, no repo file.
- Shared scene family: `assets/dock-eval-50/bases/`, `assets/dock-eval-hard/bases/` (spot-checked: clean, crush, pallet bases).
- 1 large label per easy photo, no rotation, crop+brightness only: `Gate.Eval/Vision/DockEval50Compositor.cs`, `assets/dock-eval-50/answer-key.json`.
- 5–6 small rotated labels per hard photo plus blur/glare/cool effects: `Gate.Eval/Vision/DockEvalHardCompositor.cs`, `assets/dock-eval-hard/answer-key.json`.
