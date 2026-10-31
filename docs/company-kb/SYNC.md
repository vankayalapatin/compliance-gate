# KB ↔ Scenario Sync (Design)

The KB and the scenario seed must agree exactly where they overlap.
Agreement is proven by test, never asserted.

## Master direction

The KB is company truth; scenario files are demo intents. Scenarios
reference the KB by `kbVersion` + `kbHash` (fields staged, applied only
with explicit approval since scenarios are read-only). The loader
refuses a version/hash mismatch loudly — drift fails the build, never
passes silently.

## Overlap fields (must match exactly)

Users + signing limits, items + critical flags, vendors + countries,
payment terms, trailing history totals, CORP cap values. The v1 KB ships
as a mirror of today's scenario values.

## Mirror test

An Eval test reads ERP state through the adapter and asserts equality
with the KB on every overlap field. Any divergence — a renamed vendor,
a retuned cap, a flipped critical flag — fails with the field, the KB
value, and the ERP value quoted side by side.

## Non-overlap (KB-only, no scenario counterpart)

Legal entities, sites/warehouses, fiscal calendar, approval matrix,
region rules, and unrelated documents (HR, etc.) live only in the KB.
They need no scenario mirror; they need KB schema validation plus
citation-resolution tests.
