# ARCHIVED — early-demo history, not spec. Frozen, do not edit.

# ComplianceGate — POC Decision Record (Final)

Status: **Final** — accepted by you on this turn with the forecasting-data clarification, stretch gate, boundary amendment for docs/PRODUCT.md and docs/DECISIONS.md, and the 3-minute demo script.

## 1. Goal (as stated)
10-day solo POC for Global Meta AI Developer Hackathon, indie track. Smart-glasses company gets a large un-forecast customer order, runs short of key parts (e.g. batteries). An AI agent takes the shortfall through a full Procure-to-Pay cycle, with compliance enforced at three gates and humans approving anything involving money. All data fictional.

## 2. Settled so far (from your brief, still Draft)
- Flow: shortage detection → Purchase Requisition → vendor screening → Purchase Order → dock receipt → invoice matching → supplier payment.
- Compliance gates: (a) vendor pick, (b) goods arrival at dock from carton/pallet photos, (c) pre-pay invoice check. Failed checks block and cite the rule.
- Decision pattern: model reads and explains, but a rule-config in C# decides.
- Human approval required for anything involving money.
- Models: Muse Spark, Muse Image, etc. via Meta model API.
- Built-in mini-ERP UI instead of D365/NetSuite connectors: Sales Orders, Purch Req, Inventory, BOM, Payments, as needed for scope.
- Data: fully fictional.
- Deliverable shape (settled, still Draft): single C# ASP.NET Core Blazor app, all C#. Mini-ERP is its own module with its own DB tables. Agent and compliance code never touch ERP tables directly; they go through an adapter interface so a future D365/NetSuite connector can replace the mini-ERP adapter later.
- Adapter role (settled, still Draft): pure ERP data read/write only, no compliance or matching logic inside. Reads: SO, BOM, stock, invoice lines. Writes: create PR, submit PR/PO/invoice/payment into ERP workflows, post receipt, set hold/quarantine on cartons/shipments. Canonical plain-DTO P2P model (vendor, PR, PO, receipt, invoice, hold) so D365/NetSuite map to the same shapes.
- Separation (settled, still Draft): vendor screening lives in compliance module, not adapter. Shortfall math and 3-way matching run in agent code using data the adapter returns. Agent only submits into ERP workflow and waits for outcome.
- ERP controls (settled, still Draft): humans approve workflows assigned to them in ERP. ERP enforces and refuses violations: no PO without valid/approved PR, requester cannot approve own request, signing limits, delegations.
- Compliance shape (settled, still Draft): option 1 — versioned C# records loaded from JSON, C# decides pass/block, Muse explains and cites rule id. Rule list NOT fixed yet — pending researched proposal.
- Compliance sourcing rule (settled, still Draft): every compliance rule must trace to a real publicly available regulation for this product/part, with stored public source so each block can cite it. Generic AP controls (duplicates, price variance) are separate, not compliance rules.
- Compliance packs (settled, still Draft): rule packs independent and pluggable per industry/part, so a new part pack can be added or swapped later.
- Dock gate limit (settled, still Draft): dock gate checks only what a carton photo can reliably show; photos never set quantities.
- Company policy (settled, still Draft): include one genuine leadership-style sourcing policy rule (e.g. single-vendor concentration cap).

## 3. Unresolved (visibly marked)
- [SETTLED-DRAFT, APPROVED] Boundary: Blazor app with Decisions, Cases, Records, Policies, Audit plus simulation endpoints for order webhook, supplier ACK/ASN/invoices, and customer secured notification. Out-of-scope and Future lists recorded. See §4 and §11.
- [SETTLED-DRAFT, APPROVED] Adapter contract: raw-ERP reads, agent-side matching and explosion, compliance snapshot outside ERP, hold with reason code plus rule id plus case link, evidence on agent side. See §10.
- [SETTLED-DRAFT, APPROVED] Done checklist: partial pay, resolution loop with reshot and next-run pay, SoD with delegate routing, full audit timeline, rule and control unit tests, offline reset and replay. See §4.
- [SETTLED-DRAFT] Compliance catalog v0.2 approved with tiering: every rule classified fails-in-demo / passes-silently / documented-only; Tier 1 is CORP-02, BAT-LI-03, BAT-LI-05 conditional, ELEC-04; only CORP-02 company cap fires in demo; screening lists cached locally. BAT-LI-05 fallback recorded. See §6.
- [SETTLED-DRAFT, APPROVED] Block-vs-warn: Tier 1 hard blocks/holds with human review where recorded; Tier 2 passes silently but still shows in case timeline and audit log; signing limits, requester-cannot-approve, and delegation as hard ERP refusals with delegate routing; CORP-02 override only by logged exception from a named approver.
- [SETTLED-DRAFT] Dock-photo input: seeded pallet-level dock-door set, Muse Image generates once offline, Muse Spark reads every label, C# decides, results cached, upload for testing only. Size set to 4 images. See §7.
- [SETTLED-DRAFT] Mini-ERP scope: option 1 with your ERP field additions and three-surface layout plus Policies and Audit. See §8.
- [SETTLED-DRAFT, APPROVED] Fictional dataset: finished smart-glasses product with BOM, critical battery plus cheap non-critical short, three vendors with demo roles including Vendor C straight-through, people with limits and delegate, AP inbox with tricky invoices. See §9.
- [SETTLED-DRAFT, APPROVED] Demo: 3-minute video script with scene times per your order; remaining checklist proven by tests. See §12.

## 4. Scope contract (to be fixed in writing before interview ends)
- Artifact-level boundary (approved with simulation, amended per your docs request): single C# Blazor app in this repo plus decision documentation; in scope = Decisions, Cases, Records, Policies, Audit surfaces + mini-ERP module (own tables) + data-only adapter per §10 + agent (BOM explosion, shortfall, 3-way match) + compliance packs per §6 + ERP workflow approvals + fictional seed per §9 including seeded sample forecast data used to calculate the shortfall + 4-image dock set per §7 with cached results + simulation endpoints for incoming customer order webhook (Shopify-style), supplier PO acknowledgement, supplier ASN, supplier invoices, and customer notification once materials are secured + docs/PRODUCT.md and docs/DECISIONS.md as documentation artifacts. Out of scope = live camera, video, voice input, shipping and customer invoicing, production scheduling, forecasting tools (forecasting software only; seeded forecast data stays in scope), real D365/NetSuite connectors, real vendor data, live sanctions feeds, real payment rails, mobile app.
- Done means (approved): one seeded shortfall case runs from webhook order through BOM explosion to pay with Tier 1 cited blocks; partial payment works with passed units paid and failed units held with cited rule; resolution loop completes with the fixed carton rechecked from the reshot image, hold released, and remaining amount paid in the next run; invoice approver and payment approver are different people with one approval routed to the delegate because the approver is out; straight-through non-critical part completes with no human; AP inbox tricky items are caught; audit timeline shows every agent action, rule, result, photo, and human approval for the whole case; unit tests for rules and ERP controls pass; reset-to-start plus full offline run from cache works with no live calls in replay.
- Staged designs: accepting this record never approves later stages; each stage returns for its own interview.
- Execution words (go / do it all) authorize only the accepted boundary.

## 5. Constraints / risks / validation (to be refined)
- Constraints: 10 days, solo, hackathon indie track, fictional data only.
- Risks: scope creep across full P2P + compliance + mini-ERP in 10 days; photo-compliance accuracy; payment-approval holes.
- Validation: TBD — demo script + gate evidence.

## 6. Compliance catalog v0.2 (Draft — approved with tiering this round)

Principle: vendor gate checks documents for the lot, not who the vendor is; identity screening is separate against public lists. Packs stay independent and pluggable. C# decides, Muse reads and explains, every block cites rule id + source. All vendor/part data fictional. AP controls stay separate, never cite a regulation pack. Screening snapshots are cached as local files during build inside the compliance module, not ERP; the model never fetches lists at runtime, C# checks the local snapshot. Tiers: Tier 1 = hard blocks/holds with human review where recorded (CORP-02, BAT-LI-03, BAT-LI-05 conditional, ELEC-04); Tier 2 = enforced but passes silently in the Decisions queue while still showing in the case timeline and audit log; Tier 3 = documented only, not enforced in demo (includes CORP-01 after swap).

### Pack BAT-LI — lithium-battery transport and handling
- BAT-LI-01 [Tier 2 passes silently] Vendor gate, document check — UN38.3 test summary available for the cell/battery type. Block lot if missing. Source: UN Manual of Tests and Criteria Part III §38.3; IATA lithium battery guidance; 49 CFR 173.185.
- BAT-LI-02 [Tier 2 passes silently] Vendor gate, document check — SDS/MSDS plus correct UN classification declared (UN3480 alone vs UN3481 with/in equipment, Class 9). Block if missing or mismatched. Source: IATA DGR PI965/966/967; 49 CFR 172.101 Hazmat Table; IMDG SP188/SP230.
- BAT-LI-03 [Tier 1 fails in demo] Dock gate, photo-only — lithium-battery mark visible and legible on outer carton (red-hatched border, UN number). Block or hold if absent or illegible; photo never sets quantity. Source: IATA DGR Fig 7.1.C mark; 49 CFR 173.185(c)(3); IMDG SP188.
- BAT-LI-04 [Tier 2 passes silently] Dock gate, photo-only — Class 9 diamond and, where applicable, Cargo Aircraft Only handling visible when required. Hold if missing; quantity still comes from ERP receipt. Source: IATA DGR Class 9 handling; 49 CFR 173.185.
- BAT-LI-05 [Tier 1 conditionally, else Tier 2 — fallback recorded] Dock gate, photo-only, human review only — suspected outer damage (crushing, puncture, swelling, leakage, water damage) from photo routes to a human review task with Muse crops and explanation. C# never auto-quarantines on photo alone; human decides quarantine or release; putaway and pay stay blocked until review closes. Condition: stays Tier 1 only if a damaged-carton image can be generated by dock build day, checked on build day. Fallback: if not, BAT-LI-05 drops to Tier 2 and the demo human review comes from a carton the camera could not read clearly (BAT-LI-03 illegible case) instead. Source: IATA DGR / IMDG damaged-battery provisions as the reason to inspect, with human as decider.

### Pack ELEC — electronics product compliance (trimmed per your cut)
- ELEC-01 [Tier 2 passes silently] Vendor gate, document check — RoHS conformity declaration for the supplied part. Block if missing for EU scope. Source: EU RoHS 2011/65/EU as amended by (EU) 2015/863.
- ELEC-02 [Tier 2 passes silently] Vendor gate, document check — REACH SVHC Article 33 communication above 0.1% w/w with safe-use info. Block if declaration missing. Source: REACH (EC) 1907/2006 Article 33; ECHA Candidate List.
- ELEC-03 REMOVED from scope per your call (radio conformity evidence deferred, Tier 3 documented only).
- ELEC-04 [Tier 1 fails in demo] Pre-pay gate, compliance only — block payment while any compliance hold, quarantine, or open human-review task from the gates above is still open on that lot/shipment, or a required declaration is still missing. Source: internal gate dependency.

### Pack SCR — restricted-party and forced-labor screening (vendor identity only, compliance-side snapshot, not ERP)
- SCR-01 [Tier 2 passes silently] Vendor gate, identity screening against a compliance-side local snapshot seeded during build (OFAC SDN, BIS Entity, BIS Denied Persons, BIS Unverified via US Consolidated Screening List, EU Consolidated financial sanctions list, UN SC Consolidated List). Block on match, citing list and program. Model never fetches lists at runtime. Sources: OFAC SDN and consolidated lists; BIS lists and Consolidated Screening List; EU Consolidated list (DG FISMA); UN SC Consolidated List.
- SCR-02 [Tier 3 documented only] Vendor gate, identity and origin screening for US import scope against a compliance-side local UFLPA snapshot (DHS UFLPA Entity List plus XUAR presumption). Documented only in demo unless a reason to fire it appears. Sources: DHS UFLPA Entity List and FAQs; CBP UFLPA guidance; 19 U.S.C. §1307.

### Pack CORP — company policy from internal authority (gives the demo a real choice)
- CORP-01 [Tier 3 documented only] Vendor gate — single-vendor concentration cap from fictional COO memo and board minutes, approved by a named person: no single vendor supplies more than 50% of critical battery volume over the rolling window. Documented only in demo after your swap. Source stored in config: fictional memo id, meeting date, approver name.
- CORP-02 [Tier 1 fails in demo] Vendor gate — country-of-origin cap from the same internal authority: no more than 60% of critical battery volume from a single country of origin. The cheaper vendor fails this origin cap; a compliant alternate passes even at higher price. Override only by logged exception from a named approver. Source stored in config: same fictional memo and approver.
- Both CORP rules are leadership decisions from company domain knowledge, not industry practice, and both require exception approval to override.

### Separate AP controls (NOT compliance rules)
- AP-C1 duplicate-invoice check; AP-C2 price-vs-PO tolerance; AP-C3 goods-receipt-required-before-pay. Agent and ERP workflow only, no regulation citation.

## 7. Dock photo set (Draft — settled this round)

- Source: simulated dock-door camera, pallet-level images each showing several cartons, covering the whole shipment from more than one side.
- Size: 4 images total — 3 pallet views (front-left, front-right, rear) plus 1 reshot of the fixed carton for the relabel step.
- Generation: Muse Image generates the seeded set once, then files are stored offline in the repo. No runtime generation in demo.
- Reading: Muse Spark finds and reads every label on the visible cartons in each image. C# decides pass, hold, or human review per carton and cites the rule.
- ASN reconciliation: cartons on the ASN but not found in any image go to human review. Photos never set quantities.
- Seeded failures inside the pallet views: one carton missing the lithium-battery mark, one hidden or unreadable carton, one visibly damaged carton for BAT-LI-05. The reshot image shows the fixed relabeled carton.
- BAT-LI-05 condition resolved by this set: damaged image is generatable, so BAT-LI-05 stays Tier 1; the recorded fallback still applies if generation fails on build day.
- Legibility backstop (approved): if a pallet image is not readable by Spark after a few tries, switch to compositing labels onto the image and do not keep regenerating. Composited labels come from the seed data (carton id, part, PO, mark present or not), so ground truth is known for every carton and Spark read accuracy is measured against it.
- Caching: every model result is cached so demo replays never call the API again.
- Optional upload stays for testing only, not for the demo path.

## 8. Mini-ERP scope and surfaces (Draft — settled this round)

ERP side owns its tables, agent never touches them directly. ERP entities: Item Master with critical-part flag, Users, Roles, Signing Limits, Delegations, Forecast, Vendor Documents, Vendor Bank Details, Purchase History by Vendor, Country of Origin for CORP-02, Inventory with status including Quarantined, plus Sales Orders, PRs, POs, ASN/Shipment with cartons, Receipts with dock images, Invoices, Payments, Holds. Agent side is not ERP: Cases each with own ID linked to SO and related documents, event and audit log, rule packs, screening results, model cache.

Surfaces, not one page per document. Decisions home: one card per decision a human has to do or owns, evidence attached from AI or dock image, actions Approve, Reject, Ask the agent why. Cases: each order chain SO to PR to PO to receipt to invoice to payment, current stage, agent timeline, what is blocked and by which rule. Records: plain ERP ledger views for sales orders, PRs, POs, receipts with dock images, invoices, payments, inventory. Plus Policies view for rule packs and their sources and an Audit timeline. Anything the agent created is labeled on the record, for example Created by agent.

## 9. Fictional dataset (Draft — settled this round)

- Finished product: fictional smart glasses with a full BOM. The big un-forecast sales order is exploded into components and netted against forecast, stock, and open POs to produce the shortfall.
- Short parts: one critical battery cell that runs through the full compliance gates, plus one cheap non-critical component that is also short and runs end to end with no human because it sits under the auto-approve threshold and outside the compliance packs.
- Vendors and demo roles. Vendor A is the cheapest battery source and fails Tier 1 CORP-02 origin cap in the demo, so the agent must reject it despite the price. Vendor B is the compliant battery source that passes vendor screening, wins the PO, and its shipment triggers the dock holds and the pay block. Vendor C is the non-critical component source that shows the straight-through path: cheap part, under threshold, no compliance pack applies, agent completes it with no human touch.
- People: planner who raises the need, approver with a signing limit, delegate who covers when the approver is out, AP clerk who works the inbox, and a separate finance approver for payment. Requester cannot approve, limits and delegations enforced by ERP.
- AP inbox: the main battery invoice for the demo shipment plus three tricky items for AP controls to catch — one duplicate of the main invoice, one with changed bank details versus the vendor master, one missing vendor and PO numbers. AP-C1 to AP-C3 catch these without citing regulation packs.

## 10. Adapter contract (Draft — settled this round)

Reads return raw ERP data only: BOM (not the explosion), stock, open POs, sales orders, vendors, vendor documents, purchase history, ASNs, receipts, invoices. BOM explosion and shortfall math run in the agent. Screening snapshots are compliance data, not ERP, so they sit outside the adapter and the adapter exposes no screening call. Writes: create PR, submit for approval, create PO, update sales order status, block vendor, add note to PO, post receipt, set and release hold or quarantine, create invoice and record match result. Invoice matching itself runs in the agent. Hold shape in ERP carries reason code, rule id, and link to the case. Evidence such as photos and model explanations stays on the agent side, linked from the hold.

## 11. Stretch list — not built unless core is done (Future list)

Rule: none of the items below start until every Done-means item in §4 is met.

- On-prem open-weights model rerun of the reading steps.
- A second industry rule pack.
- Animated view of inventory, back office, front office, dock, supplier office, and related scenes.

## 12. Demo script (Final — settled, 3-minute max)

Build order (approved): build in video order — shortfall, vendor choice with CORP-02, dock holds with review, partial pay with resolution loop, then everything else. Policies is the surface allowed to be thin on demo day.

Open with the problem in one line: a carton arrived without battery marking and nobody noticed until a carrier rejected it. Close with why it matters. Rough times total 180 seconds. Sales order arrives 0:00 to 0:15. Shortfall with cheap part handled straight through in background 0:15 to 0:40. Vendor choice with cheaper vendor failed by CORP rule plus approval 0:40 to 1:00. Dock check, slowed down, 1:00 to 1:45. Invoice intake with AP catches 1:45 to 2:05. Split partial payments 2:05 to 2:20. Relabel with hold released and customer notified 2:20 to 2:40. Audit timeline closes 2:40 to 3:00. Everything else in the checklist is proven by tests, not shown in the video.

## Journal
- Source: your message describing the smart-glasses shortfall POC, compliance gates, C# rule-config, human approval, fictional data, Muse models, mini-ERP. Scope: whole POC.
- Source: your choice of option 1 with Blazor-everywhere + mini-ERP own tables + adapter-only access for agent/compliance. Scope: deliverable shape.
- Source: your correction that adapter is data-only (reads SO/BOM/stock/invoice lines; writes PR/workflow submits/receipt/hold-quarantine), vendor screening in compliance, shortfall and 3-way match in agent, ERP enforces PR/SoD/limits/delegations, canonical DTOs. Scope: adapter and separation.
- Source: web research for battery + electronics regulations and concentration-risk practice, written as §6 proposal with public sources. Scope: compliance catalog v0.1.
- Source: your edit round (ELEC-03 out, BAT-LI-05 to human review, CORP to memo/board authority with cheaper-fails tradeoff and origin cap, vendor-doc-only plus screening-list research) plus sanctions/UFLPA list research, rewritten as §6 v0.2 proposal. Scope: compliance catalog v0.2.
- Source: your approval of v0.2 with tiering (fails-in-demo / passes-silently / documented-only, 4 in Tier 1, only CORP-01 fires, local screening snapshot). Scope: catalog tiers.
- Source: your swap making CORP-02 country-origin the Tier 1 company rule with CORP-01 to documented, plus BAT-LI-05 conditional Tier 1 with build-day check and fallback to unclear-carton review. Scope: tier swap and fallback.
- Source: your option-1 dock refinement (Muse Image generates once offline, Spark reads every label, C# decides, pallet-level multi-side set, ASN-missing to review, seeded failures plus reshot, cached results, upload for testing only) with size set to 4. Scope: dock photo set.
- Source: your option-1 ERP choice with added ERP fields and agent-side case store plus three surfaces with Decisions home, Cases chain, Records ledgers, Policies, Audit, and Created-by-agent labeling. Scope: mini-ERP scope and surfaces.
- Source: your option-1 dataset choice with BOM explosion, critical plus auto-approved non-critical shorts, three vendor roles, people with delegate, AP inbox with tricky invoices. Scope: fictional dataset.
- Source: your approval of Vendor C as straight-through non-critical source. Scope: dataset vendor roles.
- Source: your approval of hard Tier 1 blocks with Tier 2 silent-but-logged plus CORP-02 override only by logged named-approver exception. Scope: block-vs-warn.
- Source: your option-2 adapter correction (raw ERP reads, BOM explosion and matching in agent, screening snapshot outside ERP, create-invoice plus match result, added SO status plus block-vendor plus PO note, ERP hold with reason code plus rule id plus case link, evidence on agent side). Scope: adapter contract.
- Source: your option-2 boundary choice with simulation endpoints, out-of-scope list, and Future list. Scope: boundary and future.
- Source: your option-1 done checklist with partial pay, reshot resolution loop, SoD with delegate, full audit timeline, unit tests, offline reset and replay. Scope: done means.
- Source: your 3-minute video rework with problem open, story order, scene times, and tests-cover-the-rest. Scope: demo script.
- Source: your approval of the legibility backstop (composite after a few failed reads, no regen churn), seed-data ground truth with Spark accuracy measured against it, video-order build with Policies allowed thin, plus the judge review that proposed them. Scope: build robustness.
- Source: your final amendments (out-of-scope means forecasting tools/software while seeded forecast data stays in scope; stretch list gated until core done; boundary widened to include docs/PRODUCT.md and docs/DECISIONS.md) plus your mark-Final instruction. Scope: final acceptance. Acceptance quoted: need few more changes and then mark Final.
