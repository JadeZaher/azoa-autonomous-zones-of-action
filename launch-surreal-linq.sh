#!/usr/bin/env bash
#
# surreal-linq-graph-query -- focused build + test loop for the SurrealDB
# LINQ / graph / live-query package work (bash twin of launch-surreal-linq.ps1).
#
# Iterate on the typed query layer (Azoa.SurrealDb.Client / .Schema /
# .Analyzer) WITHOUT spinning the full docker dev stack. Builds the three
# packages, runs their unit-test projects, and optionally brings up a
# SurrealDB so the live-query (LIVE SELECT over WebSocket) and graph
# integration tests can run end-to-end.
#
# Track: conductor/tracks/surreal-linq-graph-query/{spec,plan}.md
#
# Usage:
#   ./launch-surreal-linq.sh                      # build + all package unit tests
#   ./launch-surreal-linq.sh --filter '~Graph'    # just the graph tests
#   ./launch-surreal-linq.sh --live --integration # spin DB, run live/graph integration
#   ./launch-surreal-linq.sh --watch              # TDD watch loop on the client tests
#
# Flags:
#   --filter <expr>   xUnit --filter passed through to dotnet test
#   --live            bring up a throwaway in-memory SurrealDB on :8000 first
#   --no-build        skip the package build; test the last build (implies --no-build on dotnet test)
#   --watch           dotnet watch test on the client test project (TDD loop)
#   --integration     run ONLY the integration project (implies --live unless :8000 is up)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

FILTER=""
LIVE=0
NO_BUILD=0
WATCH=0
INTEGRATION_ONLY=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)      FILTER="$2"; shift 2 ;;
    --live)        LIVE=1; shift ;;
    --no-build)    NO_BUILD=1; shift ;;
    --watch)       WATCH=1; shift ;;
    --integration) INTEGRATION_ONLY=1; shift ;;
    *) echo "[launch] unknown flag: $1" >&2; exit 2 ;;
  esac
done

PACKAGES=(
  "packages/Azoa.SurrealDb.Client/Azoa.SurrealDb.Client.csproj"
  "packages/Azoa.SurrealDb.Schema/Azoa.SurrealDb.Schema.csproj"
  "packages/Azoa.SurrealDb.Analyzer/Azoa.SurrealDb.Analyzer.csproj"
)
UNIT_TEST_PROJECTS=(
  "tests/Azoa.SurrealDb.Client.Tests/Azoa.SurrealDb.Client.Tests.csproj"
  "tests/Azoa.SurrealDb.Schema.Tests/Azoa.SurrealDb.Schema.Tests.csproj"
  "tests/Azoa.SurrealDb.Analyzer.Tests/Azoa.SurrealDb.Analyzer.Tests.csproj"
)
INTEGRATION_PROJECT="tests/Azoa.SurrealDb.Client.IntegrationTests/Azoa.SurrealDb.Client.IntegrationTests.csproj"
CLIENT_TEST_PROJECT="tests/Azoa.SurrealDb.Client.Tests/Azoa.SurrealDb.Client.Tests.csproj"

SURREAL_CONTAINER="surreal-linq-devdb"
STARTED_DB=0

find_runtime() {
  for rt in docker podman; do
    if command -v "$rt" >/dev/null 2>&1; then echo "$rt"; return 0; fi
  done
  return 1
}

surreal_up() {
  curl -sf --max-time 2 http://127.0.0.1:8000/health >/dev/null 2>&1
}

start_surreal() {
  if surreal_up; then
    echo "[launch] SurrealDB already reachable on :8000 -- reusing it."
    return
  fi
  local rt
  if ! rt="$(find_runtime)"; then
    echo "[launch] --live/--integration needs SurrealDB but no docker/podman on PATH." >&2
    echo "[launch] Install one, or start SurrealDB on :8000 yourself." >&2
    exit 1
  fi
  echo "[launch] Starting throwaway SurrealDB ($rt, in-memory, root/root) on :8000 ..."
  "$rt" run -d --rm --name "$SURREAL_CONTAINER" -p 8000:8000 \
    surrealdb/surrealdb:latest start --user root --pass root memory >/dev/null
  STARTED_DB=1
  for _ in $(seq 1 30); do
    if surreal_up; then echo "[launch] SurrealDB is up."; return; fi
    sleep 0.4
  done
  echo "[launch] SurrealDB container started but never became healthy on :8000." >&2
  "$rt" logs "$SURREAL_CONTAINER" || true
  exit 1
}

stop_surreal() {
  if [[ "$STARTED_DB" -ne 1 ]]; then return; fi
  local rt
  if rt="$(find_runtime)"; then
    echo "[launch] Tearing down throwaway SurrealDB ..."
    "$rt" rm -f "$SURREAL_CONTAINER" >/dev/null 2>&1 || true
  fi
}
trap stop_surreal EXIT

# ── Build ────────────────────────────────────────────────────────────────
if [[ "$NO_BUILD" -ne 1 ]]; then
  echo "[launch] Building SurrealDB packages (zero-new-warnings gate) ..."
  for p in "${PACKAGES[@]}"; do
    echo "  build: $p"
    dotnet build "$p" -c Debug --nologo
  done
else
  echo "[launch] --no-build: skipping package build."
fi

# ── Watch loop (TDD inner loop) ──────────────────────────────────────────
if [[ "$WATCH" -eq 1 ]]; then
  echo "[launch] dotnet watch test on $CLIENT_TEST_PROJECT (Ctrl-C to stop) ..."
  if [[ -n "$FILTER" ]]; then
    exec dotnet watch test --project "$CLIENT_TEST_PROJECT" --filter "$FILTER"
  else
    exec dotnet watch test --project "$CLIENT_TEST_PROJECT"
  fi
fi

# ── DB (only when graph/live integration coverage is requested) ──────────
if [[ "$LIVE" -eq 1 || "$INTEGRATION_ONLY" -eq 1 ]]; then start_surreal; fi

# ── Test ─────────────────────────────────────────────────────────────────
if [[ "$INTEGRATION_ONLY" -eq 1 ]]; then
  TARGETS=("$INTEGRATION_PROJECT")
elif [[ "$LIVE" -eq 1 ]]; then
  TARGETS=("${UNIT_TEST_PROJECTS[@]}" "$INTEGRATION_PROJECT")
else
  TARGETS=("${UNIT_TEST_PROJECTS[@]}")
fi

FAILED=()
for t in "${TARGETS[@]}"; do
  echo ""
  echo "[launch] test: $t"
  ARGS=(test "$t" --nologo)
  [[ "$NO_BUILD" -eq 1 ]] && ARGS+=(--no-build)
  [[ -n "$FILTER" ]] && ARGS+=(--filter "$FILTER")
  if ! dotnet "${ARGS[@]}"; then FAILED+=("$t"); fi
done

echo ""
if [[ "${#FAILED[@]}" -gt 0 ]]; then
  echo "[launch] FAILED projects:" >&2
  for f in "${FAILED[@]}"; do echo "  - $f" >&2; done
  exit 1
fi
echo "[launch] All targeted tests green."
