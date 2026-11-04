# One order, start to finish

How a real order moves through ComplianceGate: shop order, agent work,
people approvals, supplier shipments, dock photos, invoices, payments,
and the customer message. Every number below comes from a live run
against the saved company data, not from older notes. Start each part
by restoring the saved data, so every document number matches.

App address: `http://localhost:5080`.

First, as anyone: open the Admin page, find the Saved data card, press
Restore saved data. The runbook starts here every time.

## A. 1300 glasses with split proposals on (the full walk)

Admin page first: in the Split proposals card the Propose split
awards box is checked. Leave it checked.

### 1. Place the shop order

Act as: Maya Planner. Screen: Outside, Shop panel.
Press: fill Item GLASS-1, Quantity 1300, leave the requester blank (the
ERP default), press Place new order.

Two cases open: SO-1001-BAT-1 (batteries) and SO-1001-SCR-9 (screws)
under sales order SO-1001. The screw case needs no attention until its
own shipment later. Open the battery case from the Cases page: the
Chain card links the sales order, and What the agent did shows the
shortfall and the raised request.

### 2. Approve the battery request

Act as: Del Delegate. Screen: Decisions page.
The card reads "Approve PR PR-1036". Press Approve.

PR-1036 asks for 1,200 battery cells. CinderVolt fails the country cap
for the whole quantity (a 1,200-cell award would put China past 60%),
so after screening the agent reads each supplier's headroom and
proposes a split instead of a single award.

### 3. Approve the split plan

Act as: Del Delegate. Screen: Decisions page.
The card reads "Approve split award (PR-1036)" and carries the plan:

> Split 1,200 battery cells (BAT-1): 800 to CinderVolt Energy Ltd, 400
> to Meridian Cellworks Inc. Final shares: China 59.6%, Mexico 37.7%.
> Split $18,760.00 against a single award of $22,200.00: saves
> $3,440.00. Agent note: 800x14.2+400x18.5=18760 beats single 22200

Press Approve. (The agent proposes a fresh plan each run and code
checks every line plus the combined country shares before the card
appears. Any plan shown always beats the single-vendor award or it
never appears.)

Approving creates two child cases under SO-1001-BAT-1. The parent case
page lists both children in the Split orders card with their progress;
each child links back to the parent.

### 4. Approve both child purchase orders

Act as: Del Delegate. Screen: Decisions page.
Two cards appear:

- "Approve PO PO-1037": 800 cells from CinderVolt Energy Ltd,
  $11,360.00. Press Approve.
- "Approve PO PO-1038": 400 cells from Meridian Cellworks Inc,
  $7,400.00. Press Approve.

Each PO passes the ERP country-cap check on its own at creation; a PO
that would breach the cap is refused here with the rule and the share.

### 5. Suppliers acknowledge and ship

Act as: the supplier clerk (any name). Screen: Outside, Supplier
portal. One card per PO.

Ship Meridian first so the numbers below match. Meridian card
(PO-1038): press Acknowledge. In the shipment notice form leave the
ASN number blank — the ERP mints ASN-1035 on send; accept the
suggested packing slip and the 4-carton plan; press Send shipment
notice. Receipt RCT-1035 is posted.

CinderVolt card (PO-1037): press Acknowledge. Leave the ASN number
blank — the ERP mints ASN-1036; accept the suggested packing slip
and the 8-carton plan; press Send shipment notice. Receipt RCT-1036
is posted.

### 6. Dock photos and reads, both shipments

Act as: dock staff (any name). Screen: Outside, Dock panel. One truck
card per ASN; each shipment is photographed from the scene library for
its own carton count.

Meridian truck (ASN-1035): tick the offered pallet view and
press Send dock photos. 4 carton reads come back, 0 cartons held, 0
for review.

CinderVolt truck (ASN-1036): tick the offered pallet view and
press Send dock photos. 8 carton reads come back, 0 cartons held, 0
for review.

Open either child case: the Decisions card shows no dock reviews, and
What the agent did carries the read counts.

### 7. Invoices and full payments

Act as: the supplier clerk. Screen: Outside, Supplier portal.
Meridian card: in Send an invoice accept the suggested supplier
invoice number (next in the vendor's own series) and the full 400
units at $18.50; press Send invoice. CinderVolt card: accept the
suggested number and the full 800 units at $14.20; press Send
invoice.

Act as: Del Delegate. Screen: Decisions page. Approve invoice
VND-1002/… ($7,400.00) and invoice VND-1001/… ($11,360.00).

Act as: Finn Finance. Screen: Decisions page. Two payment cards
appear: approve payment PAY-1013 for $7,400.00, then payment PAY-1014
for $11,360.00. Both children reach Resolved; both invoices read Paid.

### 8. The screw sibling resolves

The screw case ran alongside throughout: its purchase order PO-1036
($40.00) is acknowledged, shipped, read, invoiced under the suggested
vendor-series number, and paid the same way through the same three
screens. Its case reaches Resolved.

### 9. Parent resolves; the customer message goes out once

Open SO-1001-BAT-1: with both children paid, the parent case reads
Resolved. The customer message goes out exactly once for the whole
order, on the last case to finish: in this walk it lands on
SO-1001-SCR-9, reading "Order secured: every case for the order is
resolved." The walk used 29 live model calls.

## B. 1300 glasses with split proposals off (single award, a held carton, partial pay)

Restore the saved data again first. Admin page, Split proposals card:
uncheck Propose split awards. Steps 1 and 2 repeat exactly:
shop order for 1300 glasses as Maya Planner, then PR-1036 for 1,200
cells approved as Del Delegate.

### 3. Single-vendor award, no plan card

No split card appears. The agent awards all 1,200 cells to Meridian
Cellworks Inc, the cheapest vendor that passes the cap whole.

Act as: Del Delegate. Screen: Decisions page. The card reads "Approve
PO PO-1037": 1,200 cells, $22,200.00. Press Approve.

### 4. Acknowledge and ship 12 cartons

Act as: the supplier clerk. Screen: Outside, Supplier portal.
Press Acknowledge. Leave the ASN number blank — the ERP mints
ASN-1035 on send; accept the suggested packing slip and the 12-carton
plan (CTN-01 to CTN-12); press Send shipment notice. Receipt RCT-1035
is posted.

### 5. Stage a missing mark, then photograph

Act as: the supplier clerk. Screen: Outside, Supplier portal. The
shipment notice form carries a Simulated problem picker per carton;
it may be set before sending. (It was left blank here; the problem is
staged dockside instead.)

Act as: dock staff. Screen: Outside, Dock panel. Open "Rebuild views
with a simulated problem", pick carton CTN-07, pick the missing-mark
problem, press Rebuild views. The rebuilt views land on the counter:
one pallet view for the 12 cartons.

Tick the offered pallet view and press Send dock photos. 12 reads come
back; CTN-07 is held for the missing handling mark and the other 11
pass. Open the battery case: Open holds names CTN-07. No review card
is raised for a mark hold.

### 6. Invoice for the whole shipment, first payment partial

Act as: the supplier clerk. Screen: Outside, Supplier portal. In
Send an invoice accept the suggested supplier invoice number and the
full 1,200 units at $18.50; press Send invoice. Intake accepts
$20,350.00 and holds $1,850.00 (100 units on CTN-07 at $18.50).

Act as: Del Delegate. Screen: Decisions page. Approve invoice
VND-1002/….

Act as: Finn Finance. Screen: Decisions page. The payment card offers
$20,350.00 of $22,200.00: press Approve (payment PAY-1013). The held
$1,850.00 stays open.

### 7. Reshot clears the carton; second payment closes it

Act as: dock staff. Screen: Outside, Dock panel. Tick Reshot
(follow-up reads for picked cartons), tick CTN-07, press Mint
close-up for picked cartons, then press Send reshot. The corrected
read passes every check.

Act as: Finn Finance. Screen: Decisions page. A second payment card
offers the released $1,850.00: press Approve (payment PAY-1014).
Settled reads $22,200.00 in full; the invoice reads Paid; the
battery case reaches Resolved.

The screw sibling resolves as in part A, and the customer message
goes out exactly once for the order; in this walk it lands on
SO-1001-BAT-1. The walk used 21 live model calls.

## C. 200 glasses (small order, single award, one carton)

Restore the saved data again first. Split proposals may stay either
way; the award fits under the cap whole.

### 1. Place the shop order

Act as: Maya Planner. Screen: Outside, Shop panel. Item GLASS-1,
Quantity 200, requester blank, press Place new order.

One case opens: SO-1001-BAT-1 for 100 battery cells (200 needed, 60
on hand, 40 already on open orders). No screw case: 2,500 screws on
hand cover the 400 needed.

### 2. Approve the request and the purchase order

Act as: Del Delegate. Screen: Decisions page. The card reads "Approve
PR PR-1036" for 100 units: press Approve. No split card appears: the
whole 100 fits under the cap from CinderVolt, the cheapest vendor.

The card "Approve PO PO-1036" follows: 100 cells from CinderVolt
Energy Ltd, $1,420.00. Press Approve.

### 3. Ship, photograph, read

Act as: the supplier clerk. Screen: Outside, Supplier portal. Press
Acknowledge; leave the ASN number blank (the ERP mints ASN-1035) and
send the shipment notice with 1 carton. Receipt RCT-1035 is posted.

Act as: dock staff. Screen: Outside, Dock panel. The truck card
offers one scene-library view for the single carton: tick it and
press Send dock photos. 1 read comes back, 0 held.

### 4. Invoice, one payment, customer message

Act as: the supplier clerk. Screen: Outside, Supplier portal. In
Send an invoice accept the suggested supplier invoice number and
100 units at $14.20; press Send invoice.

Act as: Del Delegate. Screen: Decisions page. Approve invoice
VND-1001/… ($1,420.00).

Act as: Finn Finance. Screen: Decisions page. Approve payment
PAY-1013 for $1,420.00. Settled reads $1,420.00 in full; the case
reaches Resolved and the customer message goes out on it. The walk
used 12 live model calls.

## D. A person ordering CinderVolt over the cap is refused

The ERP control point applies to people and the agent alike. This
continues the part B data (after its order resolved): China sits near
the cap, so a big manual CinderVolt order must fail.

Act as: Maya Planner. Screen: ERP, Purchase requisitions. Press New
purchase requisition: requester Maya Planner, line BAT-1 × 2400,
unit EA; press Create purchase requisition. Request PR-1038 is
created in Draft — a hand-made request raises no Decisions card.

Still Maya: open PR-1038, press Submit for approval, route to Del
Delegate. Act as: Del Delegate. Open PR-1038, press Approve.

Act as: Maya Planner. Screen: ERP, Purchase orders. Press New
purchase order (or New purchase order from this PR on PR-1038),
pick PR-1038 and CinderVolt Energy Ltd, press Create purchase order.
The ERP refuses with "Refused." plus the sentence:

> CORP-02 (CORP v2): Awarding 2,400 battery cells (BAT-1) to CN puts
> its share at 60.9% (5,900+2,400 of 11,200+40 open+2,400 battery
> cells), including 40 battery cells on open orders, exceeding the 60%
> country-of-origin cap.

No purchase order is created. The sentence names open orders because
the cap counts not-yet-received purchase-order quantities in both the
country's share and the total.

## E. A memo moves the China cap to 40%

Act as: Dana Director. Screen: Governance, Memos. Press Submit memo
and file a memo cutting the China cap for battery cells to 40%. The
agent drafts the rule proposal (rule CORP-02, cap 0.40) and routes it
to a second owner.

Act as: Finn Finance. Screen: Governance, Memos. Open the proposal
card, type the passing test names as evidence (required), press
Approve. The pack publishes CORP v2 to v3, and the Rules page
shows the CORP-02 company row with its own pack version and adoption
date.

The published rule takes effect in the app's checks at once, with no
rebuild and no restart. The next battery order over the new cap
is refused at the ERP; the live memo run recorded the sentence:

> CORP-02 (CORP v3): Awarding 90 battery cells (BAT-1) to CN puts
> its share at 60.4% (6,000+90 of 10,000+90 battery cells), exceeding
> the 40% country-of-origin cap.

In the app, the follow-up order proves passing vendors still order:
after the memo, a 200-glasses order awards its 100 cells to Meridian
as PO-1036 ($1,850.00).
