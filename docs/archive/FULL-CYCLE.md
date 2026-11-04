# Full cycle run-book (UI)

One customer order from shop to payment, every step done in the app. Reset
first from the top bar (Reset + replay offline, confirms before it runs).

1. Customer order — Outside world → Customer shop (`/outside`, shop tab).
   Pick a seeded order and Place order, or fill the New shop order form
   (currency + lines). The ERP mints the sales order; the agent opens one
   case per shortfall item.
2. Agent case — Cases (`/cases`) then the case (`/cases/{id}`). The list
   shows real agent runs only with the order-to-payment chain strip; the
   detail shows the chain, what the agent did, what it was stopped from
   doing (rule + reason), open holds, decisions, vendor mail, timeline.
3. Approvals by person — Decisions (`/`). Switch Acting as in the top bar
   to each person and Approve/Reject their cards (Ask the agent why for
   the reasoning). Policy memos approve on Policies (`/policies`): answer
   the owner's questions, then approve with effective date + evidence.
4. Supplier ships — Outside world → Suppliers → vendor (`/outside`,
   supplier tab). Read our PO, Acknowledge it, then Send a shipment
   notice (ASN number, packing slip, editable cartons).
5. Dock photos — Outside world → Dock (`/outside`, dock tab). Each
   expected truck lists its cartons; tick counter images and/or upload
   files, set carton hints, Send dock photos (or Reshot for rechecks).
   Reads and holds land on the case and the ERP receipt.
6. Invoice — Outside world → Suppliers → vendor (`/outside`): Send an
   invoice (typed lines or invoice photos). Check it on ERP → Invoices
   (`/erp/list/invoice`) and the invoice record; the invoice approval is
   a Decisions card for the approver (never the requester).
7. Payment — ERP → New payment draft (`/erp/new/payment`): pick a
   pay-up-to date and the due invoices. The draft record carries submit,
   approve, and post actions; the payment approval is a Decisions card
   for finance (a different person than the invoice approver). Posted
   payments close the chain on the case.

ERP records behind every step live under ERP (`/erp`, opens on Sales
orders): lists, New forms, and record pages with header, lines, related
documents, and state-driven actions. Every refusal names its reason.
