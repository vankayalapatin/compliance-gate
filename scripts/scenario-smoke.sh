#!/usr/bin/env bash
# Baseline smoke: boots the web app on wiped live databases, waits for the
# startup baseline restore, then checks the app answers HTTP 200.
# Usage: ./scripts/scenario-smoke.sh
# Requires: the app builds; port 5295 free (override with PORT=...).
set -euo pipefail
cd "$(dirname "$0")/.."

PORT="${PORT:-5295}"
APP_URL="http://localhost:${PORT}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="/tmp/gate-baseline-smoke.log"

dotnet build Gate.Web/Gate.Web.csproj --nologo -v q

# Wipe LIVE databases only. The shipped snapshot pair at
# Gate.Web/data/baseline/ is never touched; startup restore copies it over
# the live stores.
rm -f "${ROOT}/Gate.Web/data/erp.db" "${ROOT}/Gate.Web/data/erp.db-wal" \
  "${ROOT}/Gate.Web/data/erp.db-shm" "${ROOT}/Gate.Web/data/erp.db-journal"
rm -f "${ROOT}/data/erp.db" "${ROOT}/data/erp.db-wal" \
  "${ROOT}/data/erp.db-shm" "${ROOT}/data/erp.db-journal"
rm -f "${ROOT}/Gate.Web/data/gate.db" "${ROOT}/data/gate.db"
rm -f "${ROOT}/scenarios/run-manifest.json"

dotnet run --project Gate.Web --no-build --urls "${APP_URL}" > "${LOG}" 2>&1 &
APP_PID=$!
trap 'kill ${APP_PID} 2>/dev/null || true' EXIT

echo "-- waiting for 'Baseline restored' in ${LOG}"
RESTORED=0
for _ in $(seq 1 120); do
  if grep -q "Baseline restored" "${LOG}" 2>/dev/null; then RESTORED=1; break; fi
  if ! kill -0 "${APP_PID}" 2>/dev/null; then break; fi
  sleep 1
done
if [ "${RESTORED}" -ne 1 ]; then
  echo "FAIL: 'Baseline restored' never logged. Last log lines:"
  tail -n 40 "${LOG}" 2>/dev/null || true
  exit 1
fi

echo "-- waiting for app on ${APP_URL}"
for _ in $(seq 1 60); do
  if curl -sf "${APP_URL}/" > /dev/null 2>&1; then break; fi
  sleep 1
done

CODE="$(curl -s -o /dev/null -w "%{http_code}" "${APP_URL}/" || true)"
if [ "${CODE}" != "200" ]; then
  echo "FAIL: expected HTTP 200 on ${APP_URL}/, got ${CODE}."
  tail -n 40 "${LOG}" 2>/dev/null || true
  exit 1
fi

kill "${APP_PID}" 2>/dev/null || true
trap - EXIT
echo "OK: baseline restored at startup and / answers 200."
