#!/usr/bin/env bash
# Agent check runner: leave this running in a terminal on your machine.
# The sandbox can compile but cannot run tests (local sockets are blocked),
# so this watcher runs checks here on agents' behalf.
#
# Usage:
#   ./scripts/agent-check.sh            # watch loop, leave running
#   ./scripts/agent-check.sh --once     # process the inbox once and exit
#
# Protocol (see AGENTS.md "Agent check runner"):
#   Request: .agent-checks/inbox/<unique-id>.json
#     { "kind": "check|build|test|live|smoke",
#       "target": "Gate.Eval/Gate.Eval.csproj",
#       "filter": "Category!=Live",
#       "scenario": "scenarios/demo-v1.json" }
#     build: dotnet build <target|ComplianceGate.sln>
#     test:  dotnet test <target|sln> --filter <filter|Category!=Live>
#     check: build, then test --no-build
#     live:  dotnet test --filter Category=Live
#     smoke: ./scripts/scenario-smoke.sh [scenario]
#   Result:  .agent-checks/outbox/<unique-id>.json + <unique-id>.log
#     { "id", "kind", "exit", "summary", "log", "finishedUtc" }
#   Processed requests move to .agent-checks/archive/.
#
# Env knobs: AGENT_CHECK_INTERVAL (poll seconds, default 5),
# AGENT_CHECK_MSBUILD=1 (single-node msbuild instead of dotnet build).
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)/.agent-checks"
INBOX="$ROOT/inbox"
OUTBOX="$ROOT/outbox"
ARCHIVE="$ROOT/archive"
INTERVAL="${AGENT_CHECK_INTERVAL:-5}"
SLN="ComplianceGate.sln"

mkdir -p "$INBOX" "$OUTBOX" "$ARCHIVE"

log() { echo "[$(date '+%H:%M:%S')] $*"; }

json_get() { # $1=file $2=key -> value or ""
  sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$1" | head -1
}

run_build() { # $1=target $2=logfile
  local target="${1:-$SLN}"
  if [ "${AGENT_CHECK_MSBUILD:-0}" = "1" ]; then
    dotnet msbuild "$target" /m:1 /nodeReuse:false /v:q >> "$2" 2>&1
  else
    dotnet build "$target" --nologo -v q >> "$2" 2>&1
  fi
}

run_one() { # $1=request file
  local req="$1" id kind target filter scenario out result exit_code summary
  id="$(basename "$req" .json)"
  kind="$(json_get "$req" kind)"
  target="$(json_get "$req" target)"
  filter="$(json_get "$req" filter)"
  scenario="$(json_get "$req" scenario)"
  out="$OUTBOX/$id.log"
  : > "$out"
  exit_code=0

  case "$kind" in
    build)
      run_build "${target:-$SLN}" "$out" || exit_code=$?
      ;;
    test)
      dotnet test "${target:-$SLN}" --filter "${filter:-Category!=Live}" >> "$out" 2>&1 || exit_code=$?
      ;;
    check)
      run_build "${target:-$SLN}" "$out" || exit_code=$?
      if [ "$exit_code" -eq 0 ]; then
        dotnet test "${target:-$SLN}" --no-build --filter "${filter:-Category!=Live}" >> "$out" 2>&1 || exit_code=$?
      fi
      ;;
    live)
      dotnet test "$SLN" --filter "Category=Live" >> "$out" 2>&1 || exit_code=$?
      ;;
    smoke)
      ./scripts/scenario-smoke.sh ${scenario:-scenarios/demo-v1.json} >> "$out" 2>&1 || exit_code=$?
      ;;
    *)
      echo "unknown kind: $kind (want check|build|test|live|smoke)" >> "$out"
      exit_code=2
      ;;
  esac

  summary="$(tail -2 "$out" | tr '\n' ';' | tr -d '"')"
  result="$OUTBOX/$id.json"
  printf '{"id":"%s","kind":"%s","exit":%d,"summary":"%s","log":"%s.log","finishedUtc":"%s"}\n' \
    "$id" "$kind" "$exit_code" "$summary" "$id" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$result"
  mv "$req" "$ARCHIVE/"
  log "$kind $id -> exit $exit_code"
  return "$exit_code"
}

process_all() {
  local req fails=0
  for req in "$INBOX"/*.json; do
    [ -e "$req" ] || break
    run_one "$req" || fails=$((fails + 1))
  done
  return "$fails"
}

if [ "${1:-}" = "--once" ]; then
  process_all
  exit "$?"
fi

trap 'rmdir "$ROOT/.lock" 2>/dev/null || true' EXIT
if ! mkdir "$ROOT/.lock" 2>/dev/null; then
  log "another watcher holds the lock; exiting"
  exit 1
fi

log "watching $INBOX every ${INTERVAL}s (Ctrl-C to stop)"
while true; do
  process_all
  sleep "$INTERVAL"
done
