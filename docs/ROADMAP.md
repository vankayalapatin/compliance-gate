# ComplianceGate — Build Roadmap (Final)

Eight days, numbered one-session tasks. No task spans more than one stage.
Each task runs later in its own fresh session with /plan then /goal.
Total model credit ceiling $110 of $150. Nov 4 is submission only.

## T1 — Schema plan, reviewed before any tables (Oct 27)
Draft schema.md covering DTOs, tables, and hold shape. Credit cap $0.
Done when you have reviewed and approved schema.md and no table exists yet.

## T2 — Project skeleton plus bare pages (Oct 27)
Single Blazor app shell with one bare page per surface:
Decisions, Cases, Records, Policies, Audit. Credit cap $0.
Done when the skeleton runs and every bare page renders.

## T3 — Carton seed file (Oct 27)
Small seed file listing every carton: carton id, part, PO,
mark present or not. This file is ground truth for accuracy scoring.
Credit cap $0. Done when the file is reviewed and covers all 12 cartons.

## T4 — Dock images plus Spark accuracy (Oct 28)
Generate 3 pallet views plus the relabel reshot plan; Spark reads once;
score per-carton accuracy against T3; compositing backstop on illegibility;
cache every result. Credit cap $60.
Done when 4 images sit offline with cached reads and an accuracy log.

## T5 — ERP seed data (Oct 29)
Items, BOM, vendors A/B/C, 5 people, forecast, stock, open POs,
12-carton ASN, 4 inbox invoices. Credit cap $0.
Done when the seed loads and the shortfall is hand-verifiable.

## T6 — Adapter plus ERP controls (Oct 29)
Raw reads, listed writes, hold shape, SoD refusals, limits, delegation.
Credit cap $0. Done when dotnet test for adapter and controls passes.

## T7 — Compliance engine (Oct 30)
Packs, tiers, compliance-side snapshots, CORP-02 exception path,
cited blocks. Credit cap $10.
Done when Tier 1 rules fire on the failing seed and unit tests pass.

## T8a — Order to ASN (Oct 31)
Webhook order intake, BOM explosion, shortfall, PR, vendor screening
with Vendor A blocked citing CORP-02, PO to Vendor B, simulated
supplier ACK, ASN recorded. Runs on cache only. Credit cap $10.
Done when the ASN exists with Vendor A blocked and cited
and the shortfall matches the hand check, zero live calls.

## T8b — Receipt to audit (Oct 31)
Receipt posting, invoice creation with match result recorded, payment
request, case timeline plus audit covering every step so far.
Runs on cache only. Credit cap $0.
Done when the timeline shows order to payment request unbroken
with every block cited, zero live calls.

## T9 — AP inbox (Oct 31)
Spark reads each inbox invoice once and caches; AP-C1 to C-3 catch
the duplicate, the bank-change, and the missing-fields invoices.
Credit cap $30.
Done when all three tricky invoices are caught offline from cache.

## T10 — Dock plus pay integration (Nov 1)
Photo reads to holds, damage review, partial pay with cited held amount,
reshot release, remainder next run, customer notification, full audit.
Credit cap $10. Done when the resolution loop completes on cache.

## T11 — Polish (Nov 2)
Styling, copy, timed rehearsal, reset plus replay button, all tests
green, credit audit against the caps above. Credit cap $0.
Done when a full timed rehearsal passes offline.

## T12 — Record the video (Nov 3)
Replays only. Credit cap $0. Done when the 3-minute video is in the can.
