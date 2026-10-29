#!/usr/bin/env bash
# Full local gate: builds the solution and runs the suite minus live tests.
# Usage: ./scripts/check.sh
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== build =="
dotnet build ComplianceGate.sln --nologo -v q

echo "== tests (excluding Live) =="
dotnet test ComplianceGate.sln --no-build --filter "Category!=Live"

echo "OK: build + tests green (live tests excluded; run them separately)."
