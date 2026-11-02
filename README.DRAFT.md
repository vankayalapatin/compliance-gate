# ComplianceGate (DRAFT — uncommitted, for review)

> Draft note: this file is a review draft only. It was written from files already in the repo plus author-provided notes. Nothing else in the repo was changed to make it.

AI agents are starting to spend company money. Who makes sure they follow the rules?

ComplianceGate is the application that answers that: an agent runs Procure-to-Pay inside an ERP, and every step it takes passes company and regulatory controls before any money moves. The ERP holds the business records while a separate agent platform holds cases, decisions, and evidence, and the agent reaches the ERP only through one adapter interface. Compliance checks are deterministic and every block cites its rule and authority; money approvals stay human, and the agent never approves its own payment.

## Results

### Dock-label eval: easy and hard tiers

Easy tier — 50 close-up photos, scored per photo:

- clean 10/10, crushed 8/8, hidden 6/6, missing_mark 7/7, smudged_lot 6/6, wrong_part 6/6, wrong_po 7/7; overall 50/50 (100%).
- Built from 10 text-free scene bases over 50 finals.

Hard tier — 30 pallet photos, 170 cartons, scored per carton:

- clean 110/110, missing_mark 13/13, smudged_lot 12/12, subtle_dent 10/10, wrong_part 12/12, wrong_po 13/13; overall 170/170 (100%).
- 5–6 small rotated labels per photo on 6 text-free pallet scenes; photo effects per final: none 10, blur 7, glare 6, cool 7.
- One read per photo at 4000 max tokens.

How the answer key was made (oracle-first):

- The oracle (answer-key.json) was decided before any image was built, and no reader loads it: readers see only the finals plus the shared prompt; offline C# scoring compares cached reads against the oracle.
- Label problems are code-drawn and certain; crush/hide/dent truth is photographic and ships with manual-check lists: 14 images on the easy tier (36 code-certain need no eyeball) and 10 dent photos plus a per-base placement spot check on the hard tier.
- The shared read prompt asks for transcription only and forbids guessing.

What missed where and why:

- Nothing missed in the final runs: 50/50 on easy, 170/170 on hard, for every reported run.
- Two measurement bugs were fixed before those finals: a 6-label read truncated twice at 2000 max tokens, so the budget went to 4000; and a shared lot let smudged reads copy neighbours, so every hard-tier carton got a unique lot and the battery mark was redrawn without the Class 9 diamond so it is the only symbol.
- The manual-check boxes still ship unticked, so damage/hidden scores rest on photographic pixels awaiting an eyeball tick (sources: `assets/dock-eval-50/manual-check.md`, `assets/dock-eval-hard/manual-check.md`).
- For context, the dock photos used in the app's receiving flow (12 cartons across 4 images: 3 pallet views plus 1 relabel reshot) score verdict 12/12, cite 12/12, fully-read 11/12 with 1 carton hidden by design.

### Spark vs Spark Open on Baseten

Same photos, same prompt, same scorer; Spark originals are the committed caches, spark-rerun is a fresh timed re-read, Baseten is the open model via Baseten inference.

Accuracy — identical across all 3 runs on both tiers:

- Easy (50 photos): spark-orig 50/50, spark-rerun 50/50, baseten 50/50.
- Hard (170 cartons): spark-orig 170/170, spark-rerun 170/170, baseten 170/170.

Time and tokens per photo:

- Easy, 50 photos: spark-rerun (muse-spark-1.3-contributor) avg 6,824 ms/photo, 3,150 in-tokens, 934 out-tokens; baseten (meta-models/muse-spark-open-1026) avg 10,690 ms/photo, 3,287 in-tokens, 1,061 out-tokens.
- Hard, 30 photos: spark-rerun avg 11,628 ms/photo, 3,345 in-tokens, 1,768 out-tokens; baseten avg 15,847 ms/photo, 3,482 in-tokens, 2,805 out-tokens.
- Spark rerun matches the original on both tiers: 50/50 and 170/170.

Cost per photo, computed from the token counts above at Meta's published Spark rates:

- At standard rates ($1.25 per 1M input tokens, $4.25 per 1M output tokens): easy spark-rerun about $0.0079/photo; hard spark-rerun about $0.0117/photo.
- At contributor-tier rates ($0.10 per 1M input, $0.20 per 1M output): the same reads come to about $0.00050/photo easy and $0.00069/photo hard.
- Per shipment in the receiving flow (3 pallet photos + 1 reshot, using hard-tier per-photo token counts: 13,380 in-tokens + 7,072 out-tokens): about $0.047 at standard rates, about $0.0028 at contributor-tier rates.
- Baseten ran on hackathon credits, so no per-token figure.

### Muse Image findings

What verified:

- Close-up single-label AI edit: verified 1/1 (IMG-04 reshot, AI-labeled path, 1 label attempt).
- Text-free base scenes: first-try 2/2 for the receiving flow pallet angles plus 10/10 for the easy-eval scenes with no retries; the hard tier adds 6 text-free pallet scenes, 1 call each, no retries.
- Endpoint plus model proven end to end with exactly 1 call: HTTP 200 with an image payload on muse-image-1.0.

What failed:

- Pallet multi-label AI edits failed field-by-field verification 3/3 after 1 attempt each (IMG-01/02/03) with "verify read mismatched the label manifest" and fell back to compositing.

Consequence: labels stay code-drawn, the image model stays on scenes. Working image files live in git-ignored tool scratch, not in the deliverable folder.

### Agent results

Pending — the author will fill this in after the data rework.

## How this was built

- Muse Code with 6 role skills did the implementation, 1 skill per area: ERP architect (Gate.Erp/), compliance engineer (Gate.Compliance/), vision assets (assets/), agent engineer (Gate.Agent/), UI engineer (Gate.Web/), and a read-only reviewer that edits nothing.
- Muse Spark did the reading and drove the Procure-to-Pay flow through tool calling; C# kept ERP enforcement plus compliance verdicts, and every model write executed through a validated tool.
- Spark Open on Baseten did the second-model rerun: the same eval photos re-read and scored with the same scorer for the accuracy/time/token comparison above.
- Muse Image did one-time generation: text-free pallet/scene bases, the single-label reshot path, and the endpoint proof call.
- Tests ran outside the Muse sandbox through scripts/agent-check.sh, because the sandbox cannot open local sockets — and Muse made that script as well.
- Glimmer was tried locally on a MacBook M3 Pro (36GB) and was too slow to use. The hackathon Baseten key served only Spark Open, so Spark Open became the open-weights comparison.
- SAM 3.1 was not added because the eval showed no counting weakness for it to fix: Spark finds and reads the labels itself.

## Limits

- Every eval photo is generated with a known answer key.
- 100% on generated photos; not tested on real docks.
- The easy and hard tiers share the same scene family (cartons on a pallet, blurred dock background); the hard tier's difficulty comes from the labels, not the scenes: 5–6 small rotated labels per photo instead of 1 large centered one, plus blur/glare/cool photo effects.

## How it works

Pending — to be filled in.

## Running it

Pending — to be filled in.

## For ERP people

Pending — to be filled in.

Eval sources: every number above traces to a file listed in [EVAL-SOURCES.DRAFT.md](EVAL-SOURCES.DRAFT.md).
