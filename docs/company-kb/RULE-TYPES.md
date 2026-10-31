# Rule-Type Registry (Design)

Closed list. A memo maps to one of these or it does not become a rule.
Derived from what the engine enforces today; new types require code +
tests, never model invention.

## Registry

| Type | Gate | Parameters |
|---|---|---|
| `DocumentPresence` | Vendor | `docKind`, `partScope` (critical / all) |
| `OriginShareCap` | Vendor | `cap`, `windowMonths`, `partScope` |
| `VendorShareCap` | Vendor | `cap`, `windowMonths`, `partScope` |
| `ScreeningMatch` | Vendor | `listScope` (data-driven; memos cannot tune it) |
| `DockMarkPresence` | Dock | `markKind` |
| `DockHandlingPresence` | Dock | `markKind` |
| `DamageReview` | Dock | none — always routes to a human, never auto-decides |
| `PrePayBlockOnOpen` | PrePay | which open states block (holds, quarantine, reviews) |

Share-cap semantics (matches the current engine): the share checked is
the **awarded** country's (or vendor's) own share —
`(countryCells + award) / (total + award)` over the trailing window.
See the corrected math in [EXAMPLES.md](EXAMPLES.md).

## NotMappable

When no type fits, the report returns `NotMappable` with:

- `nearestType` — the closest registry type considered.
- `whyNot` — the exact mismatch (no threshold named, no list named, no
  gate named, unmeasurable phrasing).
- `missingCapability` — what would be needed: data (a new KB list or
  signal), sensor (a new observation, e.g. a photo mark that does not
  exist), gate (a checkpoint the flow does not have), or code (a new
  registry type with tests).
- `questions` — the blocking questions whose answers could make it
  mappable.

No forced mappings. An unclear memo waits with questions; an
unenforceable memo is labeled as such with reasons.
