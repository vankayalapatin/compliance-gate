# ComplianceGate — Decisions (Final)

Each entry: decision plus reason. Full record: PROJECT.md (Final).

1. Single C# Blazor app, all C#. Reason: solo plus 10 days favors one codebase and one demo URL.
2. Mini-ERP is its own module with its own tables; agent and compliance go through a data-only adapter. Reason: a future D365/NetSuite connector can replace the adapter without touching agent logic.
3. Adapter reads raw ERP only (BOM not explosion, stock, open POs, SOs, vendors, documents, history, ASN, receipts, invoices). Reason: explosion, shortfall math, and invoice matching belong in the agent and must be tested there.
4. Adapter writes: create PR, submit for approval, create PO, update SO status, block vendor, add PO note, post receipt, set/release hold, create invoice plus record match result. Reason: covers shortfall to pay including blocks with no logic leaks.
5. ERP hold carries reason code plus rule id plus case link; photos and explanations stay agent-side. Reason: ERP stays lean while evidence stays citable from the case.
6. Screening snapshots live compliance-side, never in ERP; model never fetches lists at runtime. Reason: sanctions and UFLPA data are compliance inputs, and replays must run offline from cache.
7. Compliance shape: versioned C# records from JSON decide; Muse explains and cites rule id. Reason: demo needs deterministic blocks with readable explanations.
8. Every compliance rule traces to a real public regulation with stored source; AP controls stay separate. Reason: each block must cite authority, and duplicates plus price variance are controls, not compliance.
9. Packs independent and pluggable per industry/part. Reason: a new part pack can be added or swapped later.
10. Dock is photo-only and photos never set quantities; ASN-missing cartons go to human review. Reason: a carton photo cannot reliably count.
11. BAT-LI-05 damage never auto-quarantines; human decides from Muse crops. Reason: photo damage judgment is too risky to automate. Fallback recorded: if no damaged image by build day, BAT-LI-05 drops to Tier 2 and review comes from the unclear carton.
12. ELEC-03 radio evidence removed. Reason: out of scope for this part story; keep the build small.
13. Tiering: Tier 1 fails in demo (CORP-02, BAT-LI-03, BAT-LI-05 conditional, ELEC-04); Tier 2 passes silently but shows in timeline and audit; Tier 3 documented only. Reason: four demo failures cover all gates without overload.
14. CORP-02 country-origin cap is the Tier 1 company rule; CORP-01 single-vendor cap is documented only. Reason: origin gives the cheaper-fails tradeoff; only one company cap fires. Both are fictional memo plus board minutes from a named approver, overridable only by logged exception.
15. Restricted-party screening SCR-01 enforced silently; UFLPA SCR-02 documented only. Reason: identity screening is mandatory background work, not demo drama.
16. Block-vs-warn: Tier 1 hard blocks; Tier 2 silent with ledger; limits, no-self-approve, and delegation are hard ERP refusals with delegate routing. Reason: money always stays behind a human.
17. Dock set: 4 images (3 pallet views plus 1 relabel reshot), Muse Image generates once offline, Spark reads every label, all results cached, upload for testing only. Reason: deterministic multi-side coverage with replayable cache.
18. ERP fields plus three surfaces (Decisions, Cases, Records) plus Policies and Audit, agent work labeled Created by agent. Reason: humans decide from evidence; ledgers stay plain.
19. Dataset: glasses BOM, critical battery plus auto-approved non-critical short, Vendor A fails origin, Vendor B wins and carries dock holds, Vendor C shows straight-through, five seeded people, AP inbox with duplicate plus bank-change plus missing-fields invoices. Reason: smallest seed that fires every gate plus SoD plus AP catches.
20. Simulation in scope (order webhook, supplier ACK/ASN/invoices, secured notification); live camera, video, voice, shipping, customer invoicing, scheduling, forecasting tools out; seeded forecast data in. Reason: simulate the chain without live dependencies.
21. Done checklist with partial pay, reshot resolution loop, delegate SoD, full audit, unit tests, offline reset and replay. Reason: everything is observable or tested.
22. 3-minute video per the approved scene order and times; rest proven by tests. Reason: fits the official limit and slows down only at the dock.
23. Stretch list gated until core Done means are met. Reason: protects the 10-day core.
24. Dock legibility backstop: if a pallet image is not readable by Spark after a few tries, switch to compositing labels and stop regenerating. Reason: generated carton text garbles often and credits cannot survive regen churn.
25. Composited labels come from seed data (carton id, part, PO, mark present or not); Spark read accuracy is measured against that ground truth. Reason: turns a demo risk into a measured accuracy claim judges remember.
26. Build in video order; Policies may stay thin. Reason: the scarcest resource is solo days, so the thinnest surface must be the one the video needs least.

Judge review recorded with these decisions: top failure risk is generated label legibility, top schedule risk is the five-surface build, main credit burn is per-label Spark reads (read once, cache, develop against cache). Memorable moments: carrier-rejection opener, cheaper-vendor origin rejection, slowed-down dock with cited holds, partial pay with cited held amount, audit close.
