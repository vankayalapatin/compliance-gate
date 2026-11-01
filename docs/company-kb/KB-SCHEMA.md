# KB Schema (Design)

The KB master file is `company/company-kb.json` (created on implementation
go, not in this drop). Versioned, lead-owned schema. This file defines its
sections and citation format.

## Sections

- `version`, `hash` — KB version string plus content hash. Every proposal
  pins both; any KB change after a proposal requires re-grounding.
- `legalEntities` — id, name, country of incorporation, base currency.
- `sitesWarehouses` — id, legal entity ref, region ref, capabilities
  (e.g. dock camera present or not). Region refs resolve via `regionRules`.
- `fiscalCalendar` — year-end month/day, period list, current FY label.
  Until seeded, history reports use the labeled trailing-12-month
  approximation (see [WHAT-IF.md](WHAT-IF.md)).
- `approvalMatrix` — policy/action → owner role → named person. Money
  limits always defer to ERP signing limits; the matrix names *who*, the
  ERP enforces *how much*.
- `items` — item id, critical flag, pack scope. Mirrors scenario `Items`;
  equality is tested (see [SYNC.md](SYNC.md)).
- `sourcingPolicies` — caps, thresholds, windows, part scope. Mirrors
  CORP pack config values; equality is tested.
- `regionRules` — named region → explicit country list + version
  (e.g. `asia@kbv3 = {CN, TW, VN, ...}`). Region boundaries are data, so
  "Asian suppliers" always resolves to a cited, versioned list — or to a
  blocking question when the memo's intent does not match the stored list.
- `documents` — id, kind, `p2pRelevant` flag, content hash, storage ref.
  HR policies and similar carry `p2pRelevant: false`: searchable agent
  context, excluded from rule mapping by a C# allowlist.
- `leadership`, `finance`, `procurement`, `hr`, `regulatory`,
  `operations` — company-wiki entries: id, title, markdown body, tags.
  Seeded from scenario data where a source exists (signing limits,
  delegation, critical parts, payment terms, company identity, sourcing
  policy, approval roles); company-authored policy elsewhere (data
  residency, dock safety, regulatory summaries).

## Shape sketch (illustrative, not data)

```json
{
  "version": "kbv3",
  "legalEntities": [{ "id": "LE-1", "name": "...", "country": "..", "currency": "USD" }],
  "sitesWarehouses": [{ "id": "SITE-1", "entity": "LE-1", "region": "north-america" }],
  "fiscalCalendar": { "yearEnd": "12-31", "currentFy": "FY26" },
  "approvalMatrix": [{ "action": "sourcing-policy", "owner": "Dana Director" }],
  "items": [{ "id": "BAT-1", "critical": true }],
  "sourcingPolicies": [{ "id": "origin-cap", "cap": 0.60, "windowMonths": 12 }],
  "regionRules": [{ "id": "asia", "countries": ["CN", "TW"], "version": "kbv3" }],
  "documents": [{ "id": "hr-001", "kind": "hr-policy", "p2pRelevant": false }]
}
```

## Citation IDs

Every Spark resolution cites `kb:<section>#<id>@<version>`
(e.g. `kb:items#BAT-1@kbv3`). C# resolves each citation against the
pinned KB snapshot; an unresolvable citation rejects the proposal loudly
(see [SPARK-PROPOSAL.md](SPARK-PROPOSAL.md)).

## Per-entry versions and audit

Every entry in every citable section carries `version` plus
`createdBy`, `createdAt`, `modifiedBy`, `modifiedAt`, `approvedBy`,
entry `owner`, lifecycle `status` (`Active`/`Retired`), and
`effectiveDate`.
kbv1 predates audit: its entries omit these fields (region rules keep
their `version`). kbv2 backfills audit on every carried entry with
per-entry versions staying `kbv1`; new `kb-edit-*` matrix rows enter at
`kbv2`. From kbv2 on, the loader refuses entries missing audit.

## Versioned files

One file per version: `company-kb.json` (kbv1, frozen) plus
`company-kb.kbvN.json` for N ≥ 2. Files are never overwritten; priors
stay for pinned proposals and audit. The loader picks the highest
version by default and refuses filename/content mismatch, malformed
versions, and duplicate versions. Exact-version loads serve citations.

## Knowledge spaces and the edit gate

Sections group into six spaces: `leadership` (legalEntities,
leadership), `finance` (fiscalCalendar, approvalMatrix, finance),
`procurement` (sourcingPolicies, regionRules, documents, procurement),
`hr` (hr), `regulatory` (regulatory), and `operations`
(sitesWarehouses, items, operations). Mirror sections belong to no
space and stay unaudited. Each space has an owner from the approval
matrix (`kb-edit-<space>` rows): Dana Director owns leadership,
procurement, hr, and regulatory; Finn Finance owns finance; Maya
Planner owns operations. Del Delegate may approve for Dana Director
while their ERP delegation is active; the activation records the acting
approver plus `approvedFor`.

One KB version touches one space. The space owner approves; the editor
never approves their own change. Unknown approvers, missing matrix
rows, mixed-space changes, and stale bases refuse loudly.

## KB edit path

KB edits go UI-to-engine direct through
`IComplianceEngine.PublishKbUpdateAsync` — not through memo stages
(memos propose rules, not KB facts). Add refuses collision, edit
refuses unknown entries, and every add/edit mints kbv(n+1) with the
touched entries stamped to the new version.
