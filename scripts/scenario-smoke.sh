#!/usr/bin/env bash
# Scenario smoke: boots the web app with a scenario file, shows the generated
# manifest, exercises wipe-plus-reload, then stops the app.
# Usage: ./scripts/scenario-smoke.sh [scenarios/demo-v1.json]
# Requires: the app builds; port 5295 free (override with PORT=...).
set -euo pipefail
cd "$(dirname "$0")/.."

SCENARIO="${1:-scenarios/demo-v1.json}"
PORT="${PORT:-5295}"
APP_URL="http://localhost:${PORT}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build Gate.Web/Gate.Web.csproj --nologo -v q

# Absolute paths: dotnet run executes with the project directory as its
# working directory, so relative store paths would land under Gate.Web/
# while cleanup here would miss them (stale store, permanent no-op load).
rm -f "${ROOT}/data/gate.db" "${ROOT}/data/erp.db" "${ROOT}/scenarios/run-manifest.json"

GATE_SCENARIO="${SCENARIO}" GATE_SQLITE="${ROOT}/data/gate.db" GATE_ERP_SQLITE="${ROOT}/data/erp.db" GATE_MANIFEST="${ROOT}/scenarios/run-manifest.json" \
  dotnet run --project Gate.Web --no-build --urls "${APP_URL}" > /tmp/gate-scenario-smoke.log 2>&1 &
APP_PID=$!
trap 'kill ${APP_PID} 2>/dev/null || true' EXIT

echo "-- waiting for app on ${APP_URL}"
for i in $(seq 1 60); do
  if curl -sf "${APP_URL}/" > /dev/null 2>&1; then break; fi
  sleep 1
done

echo "-- manifest after startup load --"
cat scenarios/run-manifest.json

echo "-- POST /scenario/reset (wipe plus reload) --"
curl -sf -X POST "${APP_URL}/scenario/reset"
echo

echo "-- manifest after reset --"
cat scenarios/run-manifest.json

kill "${APP_PID}" 2>/dev/null || true
trap - EXIT
echo "OK: startup load + reset both wrote the manifest."
