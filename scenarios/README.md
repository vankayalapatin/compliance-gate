# Scenarios

`demo-v1.json` and `alt-v1.json` are intent-only order scripts for the
eval drivers: the same cases walk both files to prove nothing is
hard-coded. Loaders: `ScenarioLoader` plus `VisionFiles` in `Gate.Eval`,
used by the scenario, seed-consistency, vendor-reset, KB-mirror, and
tool-loop tests, and by `scripts/scenario-smoke.sh` (the check runner's
`smoke` kind). Run state lands in the git-ignored
`scenarios/run-manifest.json`.

The app's company data does not come from these files. It comes from
the shipped snapshot at `Gate.Web/data/baseline/`, which the Admin
restore copies into the live databases.
