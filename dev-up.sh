#!/usr/bin/env bash
# OASIS Sleek -- full-stack dev launcher (bash).
#
# Brings up SurrealDB + WebAPI + Frontend via docker-compose.dev.yml.
# Auto-detects docker compose v2, docker-compose v1, or podman-compose.
#
# Usage:
#   ./dev-up.sh                 # start the stack (build + up -d)
#   ./dev-up.sh --logs          # also tail combined logs after startup
#   ./dev-up.sh --rebuild       # force `--build` to rebuild images
#   ./dev-up.sh --clean         # `down -v` first so the DB volume is wiped
#
# After startup:
#   * WebAPI:     http://localhost:5000
#   * Frontend:   http://localhost:3000
#   * SurrealDB:  http://localhost:8000 (root / root)
#
# Tear down: `./dev-down.sh`
# Status:    docker compose -f docker-compose.dev.yml ps
# Logs:      docker compose -f docker-compose.dev.yml logs -f <service>

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.dev.yml"

if [ ! -f "$COMPOSE_FILE" ]; then
    echo "[dev-up] FATAL: $COMPOSE_FILE not found." >&2
    exit 1
fi

# ── Detect compose runtime ────────────────────────────────────────────────────

find_compose() {
    # Preferred: docker compose v2 (plugin baked into modern Docker Desktop).
    if command -v docker >/dev/null 2>&1; then
        if docker compose version >/dev/null 2>&1; then
            echo "docker compose"
            return 0
        fi
    fi
    # docker-compose v1 (standalone Python binary, still common in CI images).
    if command -v docker-compose >/dev/null 2>&1; then
        echo "docker-compose"
        return 0
    fi
    # podman-compose (Linux-native rootless alternative).
    if command -v podman-compose >/dev/null 2>&1; then
        echo "podman-compose"
        return 0
    fi
    # `podman compose` (newer podman with compose subcommand).
    if command -v podman >/dev/null 2>&1; then
        if podman compose version >/dev/null 2>&1; then
            echo "podman compose"
            return 0
        fi
    fi
    return 1
}

COMPOSE="$(find_compose)" || {
    echo "[dev-up] FATAL: no compose runtime found." >&2
    echo "[dev-up] Install one of: Docker Desktop, docker-compose, podman-compose, podman 4.x+." >&2
    exit 1
}

echo "[dev-up] Using compose runtime: $COMPOSE"

# ── Parse args ────────────────────────────────────────────────────────────────

TAIL_LOGS=0
REBUILD=0
CLEAN_VOLUMES=0
for arg in "$@"; do
    case "$arg" in
        --logs)    TAIL_LOGS=1 ;;
        --rebuild) REBUILD=1 ;;
        --clean)   CLEAN_VOLUMES=1 ;;
        -h|--help)
            sed -n '1,/^set -euo/p' "$0" | grep '^#' | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "[dev-up] WARN: unknown arg '$arg' -- ignored." >&2
            ;;
    esac
done

# ── Optional clean (drops the surrealdb_data volume) ──────────────────────────

if [ "$CLEAN_VOLUMES" -eq 1 ]; then
    echo "[dev-up] --clean: tearing down with -v (wipes SurrealDB volume) ..."
    $COMPOSE -f "$COMPOSE_FILE" down -v --remove-orphans || true
fi

# ── Detect a pre-existing SurrealDB on localhost:8000 ─────────────────────────
#
# If the host already has a healthy SurrealDB on the canonical port (the
# user's own dev instance, common in this repo), skip the bundled
# `surrealdb` service to avoid the port collision. The API container
# instead points at the host via `host.containers.internal` (podman) /
# `host.docker.internal` (docker).

EXTERNAL_SURREALDB=0
if command -v curl >/dev/null 2>&1; then
    if curl -sfo /dev/null --max-time 2 http://127.0.0.1:8000/health 2>/dev/null; then
        EXTERNAL_SURREALDB=1
        echo "[dev-up] Detected an existing SurrealDB on localhost:8000 -- reusing it."
    fi
fi

COMPOSE_UP_SERVICES=""
if [ "$EXTERNAL_SURREALDB" -eq 1 ]; then
    case "$COMPOSE" in
        *podman*) HOST_DB_INTERNAL="host.containers.internal" ;;
        *)        HOST_DB_INTERNAL="host.docker.internal" ;;
    esac
    # OASIS_SURREAL_URL drives BOTH the schema CLI's --connection and the
    # WebAPI's SurrealDb:Endpoint (the compose file interpolates the same
    # value into both). Single-underscore-safe -- podman-compose's
    # ${VAR:-default} parser drops names with double underscores.
    export OASIS_SURREAL_URL="http://${HOST_DB_INTERNAL}:8000"
    COMPOSE_UP_SERVICES="oasis-api oasis-frontend"
fi

# ── Workaround: podman-compose v1.5.0 silently ignores `dockerfile:` ──────────
#
# When two services share `context: .` but use different Dockerfiles,
# podman-compose builds BOTH with the same Dockerfile (the one it picks
# first) and tags both images identically. Detection: the runtime string
# contains `podman`. Workaround: hand-build each image with
# `podman build -f <dockerfile> -t <expected-tag> .` so compose `up`
# finds matching pre-built tags and skips its own broken build path.
#
# Docker Compose v2 / docker-compose v1 honour `dockerfile:` correctly,
# so for those runtimes we just let compose do the work.

PROJECT_NAME="$(basename "$SCRIPT_DIR")"

prebuild_for_podman() {
    if [ "$REBUILD" -ne 1 ] && podman image exists "localhost/${PROJECT_NAME}_oasis-api:latest" \
       && podman image exists "localhost/${PROJECT_NAME}_oasis-frontend:latest"; then
        echo "[dev-up] podman runtime: images already cached. Use --rebuild to force."
        return 0
    fi
    echo "[dev-up] podman runtime detected -- pre-building images per Dockerfile"
    echo "[dev-up]   (works around podman-compose v1.5.0 'dockerfile:' bug)"
    podman build -f Dockerfile -t "localhost/${PROJECT_NAME}_oasis-api:latest" "$SCRIPT_DIR"
    podman build -f frontend/Dockerfile -t "localhost/${PROJECT_NAME}_oasis-frontend:latest" "$SCRIPT_DIR"
}

case "$COMPOSE" in
    *podman*) prebuild_for_podman ;;
esac

# ── Build + start ────────────────────────────────────────────────────────────

BUILD_FLAG=""
# Don't pass --build for podman runtimes -- we already pre-built above,
# and triggering compose's broken builder would tag oasis-frontend with
# the wrong image content.
case "$COMPOSE" in
    *podman*) BUILD_FLAG="" ;;
    *)        [ "$REBUILD" -eq 1 ] && BUILD_FLAG="--build" ;;
esac

NO_DEPS_FLAG=""
# --no-deps tells compose to ignore depends_on when an external SurrealDB
# is in play, so it doesn't try to start the bundled surrealdb service
# (which would collide on port 8000).
[ -n "$COMPOSE_UP_SERVICES" ] && NO_DEPS_FLAG="--no-deps"

echo "[dev-up] Starting stack ..."
# shellcheck disable=SC2086
$COMPOSE -f "$COMPOSE_FILE" up -d --remove-orphans $BUILD_FLAG $NO_DEPS_FLAG $COMPOSE_UP_SERVICES

echo "[dev-up] Stack started. Service status:"
$COMPOSE -f "$COMPOSE_FILE" ps

echo ""
echo "[dev-up] Endpoints:"
echo "  WebAPI:    http://localhost:5000  (health: /health)"
echo "  Frontend:  http://localhost:3000"
echo "  SurrealDB: http://localhost:8000  (root / root)"
echo ""
echo "[dev-up] Tear down: ./dev-down.sh"
echo "[dev-up] Logs:      $COMPOSE -f $COMPOSE_FILE logs -f <service>"

if [ "$TAIL_LOGS" -eq 1 ]; then
    echo ""
    echo "[dev-up] Tailing combined logs (Ctrl-C to stop):"
    exec $COMPOSE -f "$COMPOSE_FILE" logs -f
fi
