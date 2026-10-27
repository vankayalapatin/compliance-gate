# ComplianceGate — Product (Final)

Source of truth for decisions: PROJECT.md (Final). This file is the product view; docs/DECISIONS.md is the decision-by-decision view.

## Problem (one line)
A carton arrived without battery marking and nobody noticed until a carrier rejected it.

## Why it matters
A big un-forecast order can force rush buying of regulated parts. Without cited, enforced checks at vendor pick, dock receipt, and pre-pay, a bad lot becomes a rejected shipment or a blocked payment. This POC shows an agent that moves the whole Procure-to-Pay chain while C# rule-config blocks what fails and says which rule.

## Story
Smart-glasses company. Large customer order not in the forecast. Battery shortfall after exploding the sales order BOM against forecast, stock, and open POs. A cheap non-critical component is short too and runs straight through. Agent takes the shortfall through shortage, PR, vendor screening, PO, dock receipt, invoice matching, and payment, with compliance at three gates and humans approving money. All data fictional. Models: Muse Spark (reading), Muse Image (one-time seeded generation), via Meta model API.

## Gates
- Vendor pick: document checks (UN38.3, SDS plus UN3480/3481, RoHS, REACH), identity screening against compliance-side snapshots (sanctions, UFLPA documented only), company origin cap CORP-02 (Tier 1, cheaper fails).
- Dock: photo-only checks (lithium-battery mark, Class 9 handling, damage to human review). Photos never set quantities. ASN-missing cartons go to human review.
- Pre-pay: block while any hold or review is open (ELEC-04). Partial pay: passed units paid, failed units held with cited rule. Relabel reshot releases the hold; remainder pays next run.

## Surfaces
Decisions home (approve, reject, ask the agent why, evidence attached). Cases (SO to payment chain, stage, timeline, block plus rule). Records (plain ERP ledgers, receipts with dock images). Policies (packs plus sources). Audit timeline (every agent action, rule, result, photo, approval). Agent-created records labeled Created by agent.

## Demo (3-minute video)
0:00–0:15 problem plus order arrives. 0:15–0:40 shortfall, cheap part straight through in background. 0:40–1:00 vendor choice, cheaper fails CORP-02, approval. 1:00–1:45 dock check, slowed down. 1:45–2:05 invoice intake with AP catches. 2:05–2:20 split partial payments. 2:20–2:40 relabel, hold released, customer notified. 2:40–3:00 audit close plus why it matters. Rest of Done checklist proven by tests.

## Scope notes
Seeded sample forecast data stays in scope for the shortfall math. Forecasting tools (software) are out of scope. Stretch list (open-weights rerun, second pack, animated views) never starts until core Done means are met. Build robustness: unreadable pallet images fall back to composited seed-data labels rather than regen churn, Spark accuracy is measured against seed ground truth, and the build runs in video order with Policies allowed thin.
