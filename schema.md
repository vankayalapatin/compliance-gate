# ComplianceGate — Contract Schema (Lead-owned)

Source of truth for the adapter contract and the ERP schema. DTOs, the
adapter interface, and this file change in the lead session only.

## Adapter contract

- `Gate.Contracts/IErpAdapter.cs` — the data-only boundary. Reads return raw
  ERP data (SO, BOM, stock, open POs, vendors, documents, history, ASN,
  receipts, invoices). Writes take intent plus audit actor plus idempotency
  key; ERP reads limits, amounts, dates, statuses, vouchers, and bank
  snapshots itself.
- `Gate.Contracts/Documents.cs` — canonical plain-DTO P2P model (vendor, PR,
  PO, confirmation, ASN with cartons, receipt, invoice with line
  dispositions, payment with bank snapshot, settlement, hold with rule id
  plus case link, approvals, inventory transactions).
- `Gate.Contracts/ICompliance.cs` — compliance verdicts. C# decides, every
  block cites rule id plus stored source. Document packs (BAT-LI, ELEC)
  apply to critical parts only (`ItemDto.IsCritical`); non-critical parts
  get not-applicable passes, vendor screening still applies.
- Dock reads (`CartonRead`) stay compliance-internal by decision (see
  `docs/DECISIONS.md` 29): transcription is agent-side evidence, the hold
  carries rule id, case link, and carton id. No dock DTO in contracts.

## ERP schema

The schema lives in code, not here: `Gate.Erp/Persistence/ErpDbContext.cs`
plus `ErpEntities.cs`, created by `Gate.Erp/Migrations/`. Table families:
config, sequences, idempotency keys, items, sales orders plus lines, BOM,
inventory transactions, vendors, vendor documents, payment terms, vendor
banks, vendor/last prices, legacy shares, users, delegations, volume seeds,
PR plus lines, PO plus lines plus notes, vendor confirmations plus lines,
ASN plus cartons, receipts plus lines, lot map, holds plus releases,
invoices plus lines, match results, held releases, payments plus lines,
settlements, paid amounts, approvals, requesters, routed-to.

Conventions (enforced in the ERP layer, tested per refusal): every business
document is header plus lines; money is decimal with a currency code on the
header; holds use native reason code plus note (rule id and case link travel
in the DTO, never as foreign keys); SO/PR/PO numbers assign at creation,
receipt/voucher/payment numbers mint at posting; every write carries an
idempotency key; posted rows are immutable, corrections go through new
documents or the held-release path.

Design-only (not in the contract surface): credit memos, lot-level origin
(vendor default country is the origin of record).

## ERP-DESIGN gap status

All 16 `docs/ERP-DESIGN.md` §9 gaps resolved in the lead session: gaps 1–15
verified closed in the current contracts plus EF Core build; gap 16 stays
design-only per above.
