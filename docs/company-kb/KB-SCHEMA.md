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
