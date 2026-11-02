# Company Knowledge Base + Memo-to-Rule (Design)

Status: **Design only, not built.** No code exists for this feature. All
names, numbers, and dates are fictional and mirror the demo scenario.

## What this is

Two linked pieces that take a company policy from board memo to enforced
rule in minutes/hours/days instead of months, with a human approval step:

1. A **company knowledge base (KB)** — everything the agent needs to
   understand the company: legal entities, sites and warehouses, fiscal
   calendar, who approves what, critical parts, sourcing policies,
   region-specific rules, plus unrelated docs (HR policies, etc.).
2. A **memo-to-rule pipeline** — Spark reads a board memo against the KB,
   proposes what it means as an enforceable rule (or says it cannot be
   enforced), flags conflicts, shows what would have changed historically
   and in the open pipeline, and a human approves before anything goes live.

## Guardrail chain

Spark proposes → C# validates → human approves → C# enforces.
Spark does the ambiguous-language understanding; only validated, approved,
tested proposals become versioned rules. See
[MEMO-PIPELINE.md](MEMO-PIPELINE.md) and
[SPARK-PROPOSAL.md](SPARK-PROPOSAL.md).

## Folder index

- [KB-SCHEMA.md](KB-SCHEMA.md) — `company-kb.json` sections, versioning,
  citation IDs.
- [MEMO-PIPELINE.md](MEMO-PIPELINE.md) — stage flow and stage contracts.
- [SPARK-PROPOSAL.md](SPARK-PROPOSAL.md) — proposal JSON, citation
  requirements, ambiguity handling, hallucination rejection.
- [RULE-TYPES.md](RULE-TYPES.md) — closed registry of enforceable rule
  types plus `NotMappable` rules.
- [WHAT-IF.md](WHAT-IF.md) — retrospective + pipeline impact windows and
  report shape.
- [GO-LIVE.md](GO-LIVE.md) — what happens to open/approved/posted orders
  at go-live, effective dating, rollback.
- [EXAMPLES.md](EXAMPLES.md) — three memos: cap tightening, region rule
  with ambiguity, and one unclear/unenforceable.
- [SYNC.md](SYNC.md) — KB↔scenario sync contract (proven equal, never
  drifted).

## Boundaries

- Sibling docs (`docs/DECISIONS.md`, `PRODUCT.md`,
  `ERP-DESIGN.md`) plus `docs/DATA-MODEL.md`, `docs/COMPANY-DATA.md`,
  `schema.md`, and `Gate.Compliance/Packs/` are read-only for this work.
  (`docs/archive/` holds frozen early-demo history.)
- The KB data itself (`company/company-kb.json`, `company/docs/`) is
  created only on implementation go, never as part of this design drop.
- Nothing here is committed without explicit approval.
