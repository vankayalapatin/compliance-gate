# What-If Report (Design)

Every rule proposal carries a read-only impact report before the human
decides. History is never rewritten — the report shows what *would have*
changed.

## Windows (always reported separately, never blended)

- **Pipeline (available today).** Every doc in PR Draft/Submitted, PO
  Draft/InReview, ASN-without-receipt, receipts with open holds,
  invoices Received/Matched, payments Draft/Submitted — re-evaluated as
  if the rule were live.
- **History.** Current-fiscal-year posted receipts/awards once the KB
  fiscal calendar + receipt dates exist. Until then, v1 uses the
  trailing-12-month volume-history rows and labels the report
  "trailing-12-month approximation, FY window pending calendar."
  The label is mandatory — an approver must never mistake the
  approximation for the fiscal year.

## Method

For each historical award (vendor, country, qty, item, date) and each
pipeline doc, re-run the compliance engine with old vs proposed config.
Record flips: pass→block and block→pass, with quantities and amounts
(qty × price, currency from the document header).

## Report shape

- `historyWindow`: label + row count + flips + totals.
- `pipelineRows`: per-doc old verdict, new verdict, blocking rule id.
- `flips`: full list, both directions.
- `totals`: counts, quantities, amounts.
- `unviable`: explicit flag when no award path passes (the finding the
  board expects to see — see [EXAMPLES.md](EXAMPLES.md) memo 1).

## Determinism

Same inputs → byte-identical report on replay. The report pins the KB
version, pack versions, and proposal hash it was computed from.
