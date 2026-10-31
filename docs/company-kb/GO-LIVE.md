# Go-Live Applicability (Design)

What happens to existing orders when a new rule goes live.

## Real-world basis

ERP policies are effective-dated and prospective. Workflow engines
version instances, so in-flight items finish on the old version unless
explicitly migrated. Posted ledgers are immutable (audit). Regulated
industries freeze in-flight lots only through human-reviewed holds for
safety/embargo — never auto-cancel.

## Buckets at the effective datetime

- **Posted/closed** (receipts posted, invoices paid/closed, POs
  received/closed): never re-evaluated. What-if shows "would have" only.
- **Approved, not executed** (PR Approved, PO Approved/Confirmed, payment
  Approved): grandfathered (continue) + flagged in a review queue if the
  new rule would have blocked; a human dispositions each flag.
- **Open/draft** (PR Draft/Submitted, PO Draft/InReview, ASN without
  receipt, invoices Received/Matched, payments Draft/Submitted): the new
  rule applies at the **next gate transition** after the effective
  datetime.
- **In-transit** (ASN recorded, receipt not posted): treated as approved
  in-flight — grandfather + flag.

## Applicability modes (one per memo, default first)

- (a) **Prospective-only (default).** Buckets above apply as stated.
- (b) Prospective + review in-flight. Same as (a), plus every
  grandfathered flag requires an explicit human disposition (accept risk,
  renegotiate, hold) before the case closes.
- (c) Immediate hold on in-flight pending human review. Safety or
  regulatory Tier-1 only; requires the named approver + recorded reason;
  creates holds, never cancels.

## Effective dating and rollback

- Effective datetime is mandatory on every approval card. Never backdated
  without a recorded reason.
- Rollback = publish a new pack version restoring prior parameters.
  History is untouched; the audit trail shows both versions with their
  approvers and effective ranges.
