# Mini-ERP Design (D365 F&O thinking, simplified)

Status: **Implemented in `Gate.Erp/`.** This doc is the design record;
behavior lives in code.
Scope: `Gate.Erp/` tables only. Contracts (`Gate.Contracts/`, `schema.md`, adapter interface) change in the lead session only — gaps are flagged in §9, not fixed here.

## 1. How to read this doc

Each section mirrors a D365 table family with a simpler name. For each family: what the header owns, what the lines own, and which lifecycle states it moves through. Money is always decimal with a currency code on the header. All quantities, prices, names, and dates come from the scenario file or run manifest. Code holds logic only.

## 2. Masters: vendor, item, people

| Table | D365 mirror | Key fields |
|---|---|---|
| Vendor master | `VendTable` | Vendor id, name, default country of origin, blocked flag plus block reason, current bank details, payment terms |
| Vendor documents | Vendor docs | Vendor id, kind (UN38.3, SDS), reference, present flag |
| Item master | `InventTable` | Item id, description, critical-part flag, pack size (units per carton), unit |
| Sales order | `SalesTable` / `SalesLine` | SO number assigned at creation, status, one line per finished item with quantity |
| BOM | `BOMTable` / `BOMLine` | Finished item, one line per component with quantity per |
| On-hand inventory | `InventSum` (over `InventTrans`) | Item id, available quantity; quarantined and rejected rows never count as on-hand |
| Payment terms | `PaymTerm` | Terms id, net days; referenced by the vendor master and the PO header; the payment run pays only lines past their due date |
| People and limits | Worker plus signing limits | User, role, signing limit, currency of limit |
| Delegations | Delegation chain | From user, to user, start date, end date |
| Purchase history | `VendTrans` history view | Seeded opening rows (trailing-12-month volume per source and country) plus one row appended per posted receipt line; shares always computed from the union, never stored |

The shortfall check reads these tables: required quantities from the sales order times the BOM, covered quantities from on-hand plus open PO lines. Without them the demo cannot start.

Lot-level origin (the actual origin carried on each received lot, overriding the vendor default for CORP-02) is design-only for now. Until it lands, the vendor default country is the origin of record.

## 3. Requisition and purchase order

Both are header-plus-lines. There are no header-only documents.

| Table | D365 mirror | Header | Lines |
|---|---|---|---|
| Purchase requisition | `PurchReqTable` / `PurchReqLine` | PR number assigned at creation, status, requester, source audit column, created-by actor, currency, idempotency key | One row per item: item id, quantity, unit |
| Purchase order | `PurchTable` / `PurchLine` | PO number assigned at creation, PR reference, vendor id, payment terms, approval status, document status (derived from lines, never hand-set), currency, source, created-by, idempotency key | One row per item: PO line id, item id, quantity ordered, received, invoiced, quarantined, rejected, unit price, line amount (quantity ordered times unit price) |
| Vendor confirmation | `PurchConfirmJour` | PO reference, confirm date, source, idempotency key | One line per PO line id: confirmed quantity, backorder note |

Rules:

- PR lifecycle: draft, submitted (in review), approved, rejected (back to draft), closed once its lines are fully ordered. Cancelled is terminal before approval.
- PO approval status: draft, in review, approved, confirmed (vendor ACK recorded). This is the workflow side.
- PO document status is derived from line quantities, never hand-set: open, partially received, received, partially invoiced, invoiced, closed. A PO can be partly received and partly invoiced at the same time. Cancelled is terminal before any receipt.
- Numbers for PR and PO are assigned at creation so later documents can reference them from the draft stage.
- A PO is created only from an approved PR. Line quantities default from the PR; price comes from the scenario vendor price at ordering time.
- The supplier ACK (simulation endpoint) writes the vendor confirmation and moves the PO approval status to confirmed. A PO cannot receive until it is confirmed.
- Ordering from a blocked vendor is refused, with the block reason recorded.
- Partial delivery is normal. A PO stays open across many receipts and many invoices until fully received and invoiced, then it closes.
- Approved documents are immutable. Corrections go through new documents, never edits. Credit memos are design-only for now (see §5).

## 4. Receipts, packing slips, and inventory

The ASN is what the supplier said. The product receipt is what we booked. In D365 the product receipt is the packing slip journal, so there is one table, not two: the supplier's packing-slip number is a field on our receipt.

| Table | D365 mirror | Meaning |
|---|---|---|
| ASN (advance ship notice) | Inbound shipment | ASN number, PO reference, source, idempotency key; one line per carton with carton id, item id, units per carton, lot reference |
| Product receipt (packing slip journal) | `VendPackingSlipJour` / `VendPackingSlipTrans` | What we booked: receipt number minted at posting, PO reference, supplier packing-slip number, source, created-by, idempotency key; one line per PO line id with received, quarantined, and rejected quantities, condition note, lot reference |
| Inventory transactions | `InventTrans` | One row per item plus receipt line or lot: quantity with status available, quarantined, rejected, or consumed |

Dock reconciliation runs carton to carton: every carton on the ASN must appear in a dock image, and every photographed carton must be on the ASN. Cartons on the ASN but missing from every image go to human review. Photos never set quantities; booked quantities come from the product receipt lines. Every posted receipt line also appends a purchase-history row (§2). Model carton reads (mark present, damage visible) arrive through the adapter payload and drive quarantine flips; the photo evidence itself stays on the agent side, linked from the hold.

Quarantine is a status change on the inventory transaction, not a separate table. A dock hold flips the affected lot rows from available to quarantined. Release flips them back. Rejected rows stay rejected and can never be invoiced or paid. Consumed rows are issued to the sales order.

## 5. Invoice and payment

| Table | D365 mirror | Header | Lines |
|---|---|---|---|
| Vendor invoice | `VendInvoiceJour` / `VendInvoiceTrans` | Supplier invoice number (duplicate-checked per vendor), internal voucher minted at posting, PO reference, status, currency, source, created-by, idempotency key | One row per PO line id: quantity, unit price, disposition (accepted or held), hold reason per line |
| Payment journal | Payment journal header and lines | Payment number minted at posting, status, currency, payee bank snapshot plus bank-verified flag, terms due date, source, created-by, idempotency key | One row per invoice: invoice number, amount paid this run |
| Settlement | `VendTrans` settlement | Invoice number, payment number, settled amount, remaining balance | Derived, never hand-edited |

- Invoice lifecycle: received (entered), matched, approved, posted (voucher minted), partially paid, paid, closed. Held lines wait instead of blocking the accepted value.
- The payment run pays accepted value on approved, posted invoices to verified bank details; terms set the due date shown on the draft but, like D365, never refuse early payment. A bank change received after verification blocks payment until re-verified; paying against an unverified bank change is refused.
- One invoice line against one PO line splits by quantity into an accepted part and a held part, each its own invoice row with its own disposition and hold reason. Example: 8 units billed against a PO line become one accepted row for 6 units and one held row for 2 units citing quarantine. Check 3 (partial pay with remainder run) depends on this split: the payment run sums only accepted-part value, and the held part stays open for the next run.
- When quarantine clears, the held parts are released (disposition flips to accepted with a release record) and the next payment run pays them. No credit-and-rebill.
- Credit memos (vendor invoices with negative quantities against the same PO line ids, reversing value without editing the posted invoice) are design-only for now. Until they land, posted-invoice corrections wait for the release path above.

## 6. Worked example (balances by hand)

A PO line orders 12 units at 10.00 each (line amount 120.00). The ASN lists the cartons covering those units.

- Receipt 1 books 8 units: 6 available, 2 quarantined (damaged carton). PO line now shows received 8, quarantined 2. Inventory holds 6 available rows and 2 quarantined rows for that lot.
- Invoice 1 bills 8 units against that PO line in two rows: accepted 6 (value 60.00), held 2 citing the quarantine reason. Payment run 1 pays 60.00 (bank verified; terms ride as the draft due date). Settlement shows invoice 1 settled 60.00, held value 20.00 still open.
- Resolution: quarantine clears, the lot rows flip back to available, and the held 2 units are released to accepted with a release record. Payment run 2 pays the remaining 20.00. Total paid 80.00 against 8 received. Nothing was edited after posting.

## 7. Number timing and per-document statuses

Number timing follows D365: SO, PR, and PO numbers are assigned at creation so later documents can reference them from the draft stage. Product receipt, invoice voucher, and payment numbers are minted at posting from one sequence per document type. Simulation and offline replays send an idempotency key with every write; a retried key returns the already-minted number instead of posting twice.

Each document has its own statuses:

- Sales order: created, confirmed, partially delivered, delivered, invoiced, closed. Cancelled is terminal before delivery.
- Purchase requisition: draft, submitted, approved, rejected (returns to draft), closed once its lines are fully ordered. Cancelled is terminal before approval.
- Purchase order approval status: draft, in review, approved, confirmed (vendor ACK). Purchase order document status, derived from line quantities: open, partially received, received, partially invoiced, invoiced, closed. Cancelled is terminal before any receipt.
- Product receipt (packing slip journal): posted (number minted at posting), carrying the supplier packing-slip number. Receipt lines may later move through quarantine and release without editing the posted journal.
- Vendor invoice: received, matched, approved, posted (voucher minted), partially paid, paid, closed.
- Payment journal: draft, submitted, approved, posted (number minted). Posted journals are immutable.
- Holds: set, released. History is append-only.

Posted and closed rows are read-only.

## 8. Approvals, delegations, and the nine refusals

Approval records carry: document type and number, routed-to user, acted-by user, acting-as-delegate flag, timestamp, decision (approve or reject), comment, and the signing limit value checked at decision time. Delegation rows carry from user, to user, start date, end date. A delegate approval counts only inside its date window and is recorded with the delegate flag set.

| Refusal | Enforced where | Recorded reason |
|---|---|---|
| Approving your own request | Approval posting | Requester equals approver |
| Approving above your signing limit | Approval posting, limit read server-side | Limit checked exceeds approver limit |
| PO without an approved PR | PO creation | PR missing or not approved |
| Ordering from a blocked vendor | PO creation | Vendor blocked with reason |
| Paying a held invoice value | Payment creation | Held line value excluded, remainder stays open |
| Paying against an unverified bank change | Payment creation | Bank details changed after verification |
| Same person approving invoice and payment | Payment approval | Invoice approver equals payment approver |
| Receiving against a closed PO | Receipt posting | PO closed to receipt |
| Posting a duplicate invoice number | Invoice creation, per vendor | Vendor invoice number already posted |

Every refusal is returned as an error and written to the workflow history with the document reference and the reason above. Approval history is append-only and queryable per document.

## 9. Contract gaps (lead session only, do not build around them)

Current contracts do not cover everything above. Flagged for lead decision; no adapter or DTO change is made from this doc.

1. **No sales order, BOM, or on-hand tables.** The adapter exposes sales-order, BOM, and stock reads, but no `SalesTable`, `BOMTable`, or `InventSum` backs them — today they are scenario-seeded fake state. The shortfall check (SO times BOM minus on-hand minus open POs) needs first-class tables or the demo cannot run. Nothing appends posted receipt lines to purchase history either; the design derives shares from seeded openings plus posted receipts.
2. **No ASN or cartons.** There is no ASN shape at all, so the dock check has nothing to reconcile photographed cartons against. Carton identity must land in ERP per §4.
3. **No inventory transactions.** There is no lot or `InventTrans` shape; quarantine exists only as counters. Quarantine-as-status-change, rejected lots, and consumed issues have nowhere to land.
4. **Supplier packing-slip number missing.** The design merged receipt and packing slip into one product receipt journal; the contract's single receipt shape now matches that, but it carries no supplier packing-slip number field.
5. **No settlement.** Payments post immediately as posted with no draft, submit, or approve states and no remaining-balance record. Partial pay plus remainder run is computed by convention, not stored.
6. **Payment journal is thin.** Payment lines carry invoice plus amount only: no payee bank snapshot, no verified flag, no terms due date, no journal approval state.
7. **No vendor confirmation or payment terms.** Nothing records the supplier ACK, and terms exist nowhere, so receivability gating and due-date payment have no basis.
8. **Approval workflow is PR-only.** Submit-for-approval accepts PR documents; PO, invoice, and payment approvals have no submit path. Delegations, users, roles, and signing limits have no read API, and the limit-checked value is caller-supplied instead of server-read. The contract also gives every document one `DocStatus`; the design gives the PO an approval status plus a derived document status with the per-document lifecycles in §7.
9. **Number timing is inverted in the fake.** The fake mints PR, PO, receipt, and payment numbers at creation. The design assigns SO, PR, and PO at creation but mints product receipt, invoice voucher, and payment numbers at posting.
10. **No idempotency keys.** No write carries one, so replay protection is unenforceable.
11. **No item master or pack size.** Item id, critical flag, and units-per-carton have no read API; carton math cannot be validated ERP-side.
12. **Vendor bank details missing.** The bank-change AP case has no vendor-bank read to compare against, no bank snapshot on payment, and no re-verification step — the unverified-bank refusal has nothing to check.
13. **Holds float free.** Holds carry no document, lot, carton, or PO-line reference, so a hold cannot name what it blocks. No hold history or release approval exists.
14. **Invoice identity is ambiguous.** The duplicate check keys on the internal number, not the supplier's invoice number per vendor. Missing vendor or PO numbers are caught by convention, not by a required-field refusal. Match results key on PO line with a single matched flag, so a quantity split (accepted part plus held part against one PO line) has no result shape.
15. **Price and vendor blocking live outside the interface.** PO creation takes no prices; the only price setter is a seed backdoor. The blocked-vendor refusal exists in the fake but has no failure-path test and no unblock path. There is no close-PO or created-by actor field anywhere.
16. **Credit memos and lot-level origin are design-only.** Both are described in §5 and §2 but intentionally excluded from the contract surface until the lead promotes them.

## 10. Build order after approval

First the EF Core tables with migrations for sales orders, BOM, items with on-hand, vendors with terms and bank details, people, PR and PO with vendor confirmation (approval status plus derived document status), ASN with cartons, product receipts (packing slip journal) with lots, invoices with quantity-split line dispositions, payment journals with settlement, approvals with delegations, sequences, and holds with document references. Credit memos and lot-level origin stay out until the lead promotes them from design-only. Then the adapter mapped onto them, keeping the same read and write intent. Then one failure-path test per refusal in §8, plus the §6 partial-plus-remainder balance test. The in-memory fake stays for unit tests only and never backs the app.
