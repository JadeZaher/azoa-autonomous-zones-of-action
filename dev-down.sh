#!/usr/bin/env bash
# OASIS Sleek -- full-stack dev teardown (bash).
#
# Usage:
#   ./dev-down.sh           # stop all services (keeps the surrealdb_data volume)
#   ./dev-down.sh --wipe    # also drop the volume so the next dev-up sees a fresh DB

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.dev.yml"

find_compose() {
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        echo "docker compose"; return 0
    fi
    if command -v docker-compose >/dev/null 2>&1; then echo "docker-compose"; return 0; fi
    if command -v podman-compose >/dev/null 2>&1; then echo "podman-compose"; return 0; fi
    if command -v podman >/dev/null 2>&1 && podman compose version >/dev/null 2>&1; then
        echo "podman compose"; return 0
    fi
    return 1
}

COMPOSE="$(find_compose)" || {
    echo "[dev-down] FATAL: no compose runtime found." >&2
    exit 1
}

WIPE=0
for arg in "$@"; do
    case "$arg" in
        --wipe) WIPE=1 ;;
        *)      echo "[dev-down] WARN: unknown arg '$arg' -- ignored." >&2 ;;
    esac
done

# Tolerate the missing-surrealdb-container error that podman-compose
# emits when dev-up.sh detected an external SurrealDB and skipped the
# bundled service. The API + frontend still get removed correctly;
# podman-compose just exits non-zero because it tried to remove a
# container that was never created.
if [ "$WIPE" -eq 1 ]; then
    echo "[dev-down] Tearing down with -v (wipes surrealdb_data volume) ..."
    $COMPOSE -f "$COMPOSE_FILE" down -v --remove-orphans || true
else
    echo "[dev-down] Tearing down (volume preserved) ..."
    $COMPOSE -f "$COMPOSE_FILE" down --remove-orphans || true
fi
exit 0
