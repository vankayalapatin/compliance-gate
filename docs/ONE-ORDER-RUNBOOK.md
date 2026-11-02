# One-order runbook: 1300 glasses to paid suppliers and secured customer

A single pass through the main flow on restored data: place a shop order
for 1300 display assemblies, cover the 1200-cell battery shortfall with
a new Meridian purchase order, receive twelve cartons, work the dock
outcomes (one held for a missing battery mark, one damage review, one
hand check), split the invoice, pay the passed cartons, release the held
carton with a relabeled photo, pay the remainder, and close the screws
case the same order opens.

Every step below was walked click by click through the real pages while
writing this. Page, acting user, exact clicks, and what to expect are
all listed.

Acting user: pick the person in the user switcher in the top bar before
each step that names one. Del Delegate covers Dana Director under a
delegation dated Oct 20 to Nov 10, 2026, which is why approvals route to
Del.

## 0. Restore saved data

- Page: Admin (`/admin`), Restore section.
- Act as: anyone.
- Click: "Restore saved data", then "Confirm restore". Wait for the done
  notice.
- See: the notice confirms both stores were restored. Nothing else moves
  until a person or an outside party acts: no new orders, no agent turns.

## 1. Place the shop order

- Page: Outside world (`/outside`), sidebar "Shop" (the customer shop).
- Act as: anyone; the order itself carries its own requester.
- Click: Requester: pick "Maya Planner". Item: "GLASS-1". Quantity:
  "1300". Then "Place new order".
- See: a notice that the ERP minted sales order SO-1001 from the shop
  order. If the shop answers "Not sent" instead (a model hiccup on the
  send), restore again and repeat this step; anywhere else a failed
  advance offers "Try again" on its settled decision card. Behind the
  sent order the agent opened two cases. The battery case
  (SO-1001-BAT-1) computed its shortfall (1300 cells needed, 100
  covered by stock plus open POs, 1200 short) and raised PR-1036 for
  1200 x BAT-1, routed to Del Delegate. The screws case (SO-1001-SCR-9,
  100 short) needed nothing from anyone: its PR-1037 auto-approved
  under threshold, Formosa Fasteners passed screening (no pack applies
  to screws), and PO-1036 for 40.00 USD sits waiting on the supplier
  acknowledgement. Drive the battery case first (steps 2-11), then the
  screws case (steps 12-14).

## 2. Approve the battery requisition

- Page: Decisions (`/`, also `/decisions`).
- Act as: Del Delegate.
- Click: on the "Approve PR PR-1036" card, "Approve".
- See: the card settles with an "approved by Del Delegate" outcome and
  a notice that the case advanced. The agent then screens the vendors:
  CinderVolt fails CORP-02 (a 1200-cell award would put China at 63.4%,
  over the 60% cap) and is blocked; Formosa Fasteners fails the battery
  document rules but stays buyable for its own trade (document-only
  failures lose the award without a vendor-master block); DragonLeaf is
  already blocked. Meridian Cellworks (44.6%) and ABC Parts pass, and
  the agent awards the 1200 cells to Meridian Cellworks, creates
  PO-1037 for 22,200.00 USD, and routes the PO approval to Del
  Delegate. If the agent names ABC Parts instead, use the ABC Parts
  portal and an AB- invoice number in the steps below.

## 3. Approve the purchase order

- Page: Decisions (`/decisions`).
- Act as: Del Delegate.
- Click: on the "Approve PO PO-1037" card, "Approve".
- See: the card settles and the notice says the case now waits on the
  supplier acknowledgement.

## 4. Acknowledge the order as the supplier

- Page: Outside world (`/outside`), sidebar "Meridian Cellworks Inc"
  (the supplier portal). PO selector: "PO-1037".
- Act as: anyone; this is the supplier's side of the counter.
- Click: "Acknowledge".
- See: an "acknowledged." outcome, an "Already sent" history showing
  the acknowledgement with its date, and only the next sensible step
  left: the acknowledgement section closes and only "Send a shipment
  notice" remains.

## 5. Ship twelve cartons as the supplier

- Page: same supplier portal, PO-1037.
- Act as: anyone (supplier side).
- Click: fill ASN number "ASN-1036" and packing slip "8814". The form
  pre-fills twelve rows of 100 units labelled the way the supplier
  stencils them: CTN-01 with LOT-001 through CTN-12 with LOT-012
  (each carton on its own lot, which is what lets the later hold touch
  one carton instead of the shipment). Verify the labels, then "Send
  shipment notice" — no retyping.
- See: "Shipment notice ASN-1036 recorded. Receipt RCT-1035 posted
  with the notice." The "Already sent" history now lists the
  acknowledgement and the shipment notice with dates; only invoicing
  remains.

## 6. Check the truck at the dock

- Page: Outside world (`/outside`), sidebar "Dock".
- Act as: anyone; this is the warehouse side.
- Click: tick the rows for photos IMG-01.jpg, IMG-02.jpg, and
  IMG-03.jpg (the counter shows pallet views only for a first check;
  no carton hints needed — the reads carry their own carton ids),
  then "Send dock photos".
- See: "Dock photos accepted: CTN-01, ... CTN-12." with the case
  waiting on the invoice. The reads split three ways: CTN-07 arrives
  with no battery mark, so its lot is held and quarantined for a
  relabel; CTN-04 shows suspected outer damage, so a damage review
  goes to Del Delegate (putaway and pay stay blocked until it closes);
  CTN-01 through CTN-06, CTN-08 through CTN-10, and CTN-12 pass.
  CTN-11 appears in no photo, so a dock review (hand check) goes to Del
  Delegate. The customer is not notified yet: a hold is still open.

## 7. Clear the damage review and the hand check

- Page: Decisions (`/decisions`).
- Act as: Del Delegate.
- Click: on the "Damage review for CTN-04" card, "Approve"; then on
  the "Dock review for CTN-11" card, "Approve".
- See: each card settles with an "approved by Del Delegate" outcome.
  Both cartons join the passed set. CTN-07 stays held and quarantined.

## 8. Invoice as the supplier, split by the hold

- Page: supplier portal for Meridian Cellworks, PO-1037.
- Act as: anyone (supplier side).
- Click: fill invoice number "MC-2026-11-01" (vendor and PO arrive
  prefilled, the line mirrors the 1200 ordered units), then "Send
  invoice".
- See: "Invoice VND-1002/MC-2026-11-01 intaken. Persisted: yes,
  accepted value 20350, held value 1850." The quarantine splits the
  line: 1100 passed units accepted, CTN-07's 100 units held with the
  cited rule. The "Already sent" history shows all three supplier
  steps with dates, and the portal offers nothing further for this
  order. An invoice approval is routed to Del Delegate.

## 9. Approve the invoice, pay the passed cartons

- Page: Decisions (`/decisions`).
- Act as: first Del Delegate, then Finn Finance.
- Click: as Del, on the invoice approval card, "Approve". The invoice
  posts to the ledger (voucher V-1035) and a partial payment of
  20,350.00 USD is drafted for Finn Finance. Switch to Finn Finance
  and on the payment card, "Approve".
- See: the payment PAY-1013 posts for 20,350.00 USD and the case moves
  to partial payment: paid 20,350.0 of 22,200.0 accepted, the held
  1,850.0 waiting on the relabeled carton.

## 10. Send the relabeled carton photo

- Page: Outside world (`/outside`), sidebar "Dock".
- Act as: anyone (warehouse side).
- Click: tick "Reshot" for ASN-1036, tick carton CTN-07, tick photo
  IMG-04.jpg (the counter shows the close-up only under Reshot; it is
  the close-up showing the mark present), then "Send reshot".
- See: "Reshot accepted: CTN-07." The hold and the quarantine lift,
  the held 100 units release for the next payment run, and the case
  timeline records "Customer notified: materials secured for SO-1001
  / BAT-1". A second payment approval for 1,850.00 USD is routed to
  Finn Finance.

## 11. Pay the remainder

- Page: Decisions (`/decisions`).
- Act as: Finn Finance.
- Click: on the payment approval card, "Approve".
- See: the card settles, the payment posts for 1,850.00 USD, and the
  notice says the case advanced to Resolved. Nothing further is owed:
  paid 22,200.0 of 22,200.0 accepted, no open holds or reviews. ERP
  → Invoices shows the invoice Posted with voucher V-1035 and no
  held lines; ERP → Payments shows both payments Posted.

## 12. Run the screws acknowledgement and shipment

- Page: Outside world (`/outside`), sidebar "Formosa Fasteners Co".
  PO selector: "PO-1036".
- Act as: anyone (supplier side).
- Click: "Acknowledge"; then fill ASN number "FF-ASN-1101", packing
  slip "FF-SLIP-1101", one row of 100 units in carton SCTN-01 on lot
  SLOT-001, and "Send shipment notice".
- See: "acknowledged.", then "Shipment notice FF-ASN-1101 recorded."
  with its receipt. The "Already sent" history tracks both.

## 13. Dock the screws and clear the hand check

- Page: Outside world (`/outside`), sidebar "Dock"; then Decisions.
- Act as: anyone at the dock; Maya Planner for the review.
- Click: tick IMG-01.jpg and "Send dock photos". The screw carton
  matches no labeled photo, so a dock review (hand check) routes to
  Maya Planner. Switch to Maya Planner and on the "Dock review for
  SCTN-01" card, "Approve".
- See: the review settles and the screws case timeline records its
  own customer notification for SO-1001 / SCR-9.

## 14. Invoice and pay the screws

- Page: supplier portal for Formosa Fasteners, then Decisions.
- Act as: anyone (supplier side); then Del Delegate; then Finn Finance.
- Click: fill invoice number "FF-2026-11-01" (100 x 0.40) and "Send
  invoice". As Del Delegate, approve the invoice (posts, 40.00 USD
  all accepted). As Finn Finance, approve the payment.
- See: the payment posts for 40.00 USD and the screws case resolves.
  Cases (`/cases`) shows both SO-1001 cases Resolved: the battery
  case with its award, twelve cartons, split invoice, two payments,
  and customer notification; the screws case straight through from
  auto-approved requisition to paid.
