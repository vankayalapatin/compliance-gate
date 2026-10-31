# Vision assets (vision lane owns this folder)

Simulated dock-camera inputs. All fictional: no real companies, cartons, or
document numbers. PO/lot on labels always resolve from
`scenarios/run-manifest.json` at build time.

## Layout

- `cartons.json` — ground-truth seed: 12 cartons, mark/damage/visibility truth.
  Accuracy oracle; ids mirror `scenarios/demo-v1.json`.
- `image-specs.json` — 4 image specs: 3 pallet views + 1 relabel reshot.
- `dock/` — pallet images. `*-base.jpg` are the clean unmarked bases
  (IMG-01 local tool; IMG-02/03 from `muse-image-1.0`, one call each, no
  text). Finals `IMG-01.jpg`..`IMG-04.jpg` are built at ASN time by the
  dock pipeline (Gate.Agent): the image model applies each carton's label
  first, and any image whose Spark read mismatches the manifest falls back
  to compositing (docs/DECISIONS.md 24-25, 37). Label geometry lives in
  `image-specs.json`; `build-record.json` records which path each image
  took. IMG-04 is a crop of the base with the CTN-07 relabel.
- `accuracy.csv` — per-carton score of cached reads vs `cartons.json`,
  written by the offline accuracy test.
- `read-cache/` — offline read cache (see below). Demo and replay read cache
  only; full replay makes zero live calls.

## Image endpoints (double-checked)

Repo model access is OpenAI-compatible: `SparkModelClient` posts
`{BaseUrl}/chat/completions` with `BaseUrl` default `https://api.meta.ai/v1`
(`Gate.Web/appsettings.json`, `SparkOptions`). Applying the same layout:

- fresh generation: `{BaseUrl}/images/generations`
- edits: `{BaseUrl}/images/edits`

`BaseUrl` already ends in `/v1`, so appending `/images/...` yields the same
absolute URL as host-root + `/v1/images/...` without doubling `/v1`.

## Read cache

One entry per image read, keyed by image sha256 + prompt version
(`carton-read-v1` for dock, `invoice-read-v1` for invoices), each carrying
model id, timestamp, and result hash. A hash mismatch invalidates loudly
instead of reusing a stale read. Reads are transcription only
(`carton_id, part, po_number, lot_code, units_per_carton, mark_present,
mark_legible, un_number, class9_visible, damage_visible, damage_note,
read_confidence`); C# decides pass/hold/review and cites the rule.
Every entry here came from exactly one live Spark call; nothing is
hand-written. PO and lot on labels always equal the ASN values the ERP
minted; image outputs cache by input hash and reads by image hash, so a
repeat run for the same PO makes zero new calls. Build order:
`GenerateExtraAngles`, then the golden pipeline run (explicit filter),
then the offline accuracy test replays cache only.

## Key test

`Gate.Eval/Vision/VisionImageProofTests.cs` (Category=Live) makes exactly ONE
`images/generations` call with a minimal fictional probe prompt. Model comes
from `MetaApi:ImageModel`, falling back to `MetaApi:Model`; key and base URL
from user secrets or env, never logged.

## Findings

Muse Image, factual counts from `dock/build-record.json`: close-up
single-label AI edits verified 1/1 (IMG-04 reshot); pallet multi-label AI
edits failed verification 3/3 after one attempt each (IMG-01/02/03) and
fell back to compositing; text-free base scenes succeeded first-try 2/2
(demo angles) plus 10/10 (`dock-eval-50/bases/`, no retries). Close-ups
are fine; multi-label pallet text is not — labels stay code-drawn, the
image model stays on scenes (docs/DECISIONS.md 47-48).

## Dock eval 50

`dock-eval-50/`: 50 finals for Spark now, Glimmer later. Oracle
(`answer-key.json`) was decided before any image was built and no reader
loads it; label problems are code-drawn and certain; 10 text-free
Muse-image bases carry scenes only. Finals plus `read-prompt.txt` and
`read-schema.json` are the second-model handoff; each model caches under
its own `read-cache-<model>/`. Scoring is model-agnostic
(`Gate.Eval/Vision/DockEval50Scoring.cs`) and reports catches by problem
type to `accuracy-by-type.csv` plus `spark-report.md`. Crush/hide truth
is photographic: see `manual-check.md` (14 images) and tick before
trusting damage/hidden scores.

## Dock eval hard

`dock-eval-hard/`: 30 pallet photos, 170 cartons, scored per carton.
Same oracle-first rules on realistic density: 5-6 small rotated labels
per photo over 6 text-free Muse pallet scenes, photo effects
(none/blur/glare/cool) applied after labeling, subtle dents photographic
only. Spark: one read per photo at 4000 max tokens (6-label reads
truncated at 2000), results to `accuracy-by-carton.csv` plus
`spark-report.md`. Manual checks: `manual-check.md` (10 dent photos plus
a per-base placement spot check).
