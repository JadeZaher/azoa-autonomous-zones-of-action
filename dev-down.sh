#!/usr/bin/env bash
# AZOA Sleek -- full-stack dev teardown (bash).
#
# Usage:
#   ./dev-down.sh             # stop all services (keeps the surrealdb_data volume)
#   ./dev-down.sh --wipe      # also drop the volume so the next dev-up sees a fresh DB
#   ./dev-down.sh --reset-db  # alias for --wipe (matches dev-up.sh vocabulary)

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
        --wipe)     WIPE=1 ;;
        --reset-db) WIPE=1 ;;   # alias matching dev-up.sh vocabulary
        *)          echo "[dev-down] WARN: unknown arg '$arg' -- ignored." >&2 ;;
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

    # Belt + braces (same pattern as dev-up.sh): purge stray project-labeled
    # volumes that survived a rename or detached from a long-gone container.
    PROJECT_NAME="$(basename "$SCRIPT_DIR")"
    if [ "${COMPOSE%% *}" = "podman" ] || [ "${COMPOSE%% *}" = "podman-compose" ]; then
        VOL_RUNTIME=podman
    else
        VOL_RUNTIME=docker
    fi
    if command -v "$VOL_RUNTIME" >/dev/null 2>&1; then
        stale="$("$VOL_RUNTIME" volume ls \
            --filter "label=com.docker.compose.project=$PROJECT_NAME" \
            -q 2>/dev/null || true)"
        if [ -n "$stale" ]; then
            echo "[dev-down] Pruning stray project volume(s)..."
            # shellcheck disable=SC2086
            "$VOL_RUNTIME" volume rm -f $stale >/dev/null 2>&1 || true
        fi
    fi
else
    echo "[dev-down] Tearing down (volume preserved -- pass --wipe / --reset-db to drop) ..."
    $COMPOSE -f "$COMPOSE_FILE" down --remove-orphans || true
fi

echo ""
echo "[dev-down] Flags (run ./dev-down.sh <flag>):"
echo "  --wipe        Also drop the SurrealDB volume so next dev-up is fresh."
echo "  --reset-db    Alias for --wipe (matches dev-up.sh vocabulary)."
echo ""
echo "  Default behavior (no flags):"
echo "    * Stops + removes containers"
echo "    * PRESERVES the SurrealDB volume so data survives"
echo ""

exit 0
