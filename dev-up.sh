#!/usr/bin/env bash
# AZOA -- full-stack dev launcher (bash).
#
# Brings up SurrealDB + WebAPI + Frontend via docker-compose.dev.yml.
# Auto-detects docker compose v2, docker-compose v1, or podman-compose.
#
# Usage:
#   ./dev-up.sh                 # default: rebuild images + SDK, keep DB volume, apply pending schema
#   ./dev-up.sh --no-build      # fast restart, reuse cached images
#   ./dev-up.sh --reset-db      # DESTRUCTIVE: wipe database + cursor-key volumes before bringing up
#   ./dev-up.sh --reset         # keep volume but wipe + re-apply schema
#   ./dev-up.sh --logs          # tail combined logs after startup
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
#
# Defaults mirror dev-up.ps1:
#   * Rebuild is ON  (opt out via --no-build).
#   * Volume wipe is OFF (opt in via --reset-db; --clean kept as legacy alias).
#   * --rebuild / --preserve kept as no-op aliases so older muscle memory
#     and scripts don't error.

TAIL_LOGS=0
NO_BUILD=0
DO_WIPE=0
DO_RESET_SCHEMA=0
for arg in "$@"; do
    case "$arg" in
        --logs)      TAIL_LOGS=1 ;;
        --no-build)  NO_BUILD=1 ;;
        --reset-db)  DO_WIPE=1 ;;
        --clean)     DO_WIPE=1 ;;       # legacy alias
        --reset)     DO_RESET_SCHEMA=1 ;;
        --rebuild)   ;;                 # no-op: rebuild is the default
        --preserve)  ;;                 # no-op: preservation is the default
        -h|--help)
            sed -n '1,/^set -euo/p' "$0" | grep '^#' | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "[dev-up] WARN: unknown arg '$arg' -- ignored." >&2
            ;;
    esac
done

DO_REBUILD=1
[ "$NO_BUILD" -eq 1 ] && DO_REBUILD=0

PROJECT_NAME="$(basename "$SCRIPT_DIR")"

# ── Teardown (default: preserve volume; --reset-db to wipe) ──────────────────

if [ "$DO_WIPE" -eq 1 ]; then
    echo "[dev-up] --reset-db: tearing down stack + wiping database and cursor-key volumes..."
    $COMPOSE -f "$COMPOSE_FILE" down -v --remove-orphans || true
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
            echo "[dev-up] Pruning stray project volume(s)..."
            # shellcheck disable=SC2086
            "$VOL_RUNTIME" volume rm -f $stale >/dev/null 2>&1 || true
        fi
    fi
else
    echo "[dev-up] Preserving database + cursor-key volumes (pass --reset-db to wipe)."
fi

# ── Detect a pre-existing SurrealDB on localhost:8000 ─────────────────────────
#
# If the host already has a healthy SurrealDB on the canonical port (the
# user's own dev instance, common in this repo), skip the bundled
# `surrealdb` service to avoid the port collision. The API container
# instead points at the host via `host.containers.internal` (podman) /
# `host.docker.internal` (docker).

SURREALDB_HOST_PORT="${SURREALDB_HOST_PORT:-8000}"
export SURREALDB_HOST_PORT

# A responder on the host port is only "external" when it ISN'T our own
# bundled container. After a `dev-up` restart the bundled surrealdb is
# already up and answering on the mapped host port; treating that as an
# external instance would wrongly skip the surrealdb service and point the
# API at host.docker.internal.
case "$COMPOSE" in
    *podman*) PS_RUNTIME=podman ;;
    *)        PS_RUNTIME=docker ;;
esac
BUNDLED_RUNNING=0
if command -v "$PS_RUNTIME" >/dev/null 2>&1; then
    if [ -n "$("$PS_RUNTIME" ps --filter 'name=azoa-dev-surrealdb' --format '{{.Names}}' 2>/dev/null)" ]; then
        BUNDLED_RUNNING=1
    fi
fi

EXTERNAL_SURREALDB=0
if [ "$BUNDLED_RUNNING" -eq 0 ] && command -v curl >/dev/null 2>&1; then
    if curl -sfo /dev/null --max-time 2 "http://127.0.0.1:${SURREALDB_HOST_PORT}/health" 2>/dev/null; then
        EXTERNAL_SURREALDB=1
        echo "[dev-up] Detected an existing SurrealDB on localhost:${SURREALDB_HOST_PORT} -- reusing it."
    fi
fi

COMPOSE_UP_SERVICES=""
if [ "$EXTERNAL_SURREALDB" -eq 1 ]; then
    case "$COMPOSE" in
        *podman*) HOST_DB_INTERNAL="host.containers.internal" ;;
        *)        HOST_DB_INTERNAL="host.docker.internal" ;;
    esac
    # SURREALFORGE_URL drives BOTH the schema CLI's --connection and the
    # WebAPI's SurrealDb:Endpoint (the compose file interpolates the same
    # value into both). Single-underscore-safe -- podman-compose's
    # ${VAR:-default} parser drops names with double underscores.
    export SURREALFORGE_URL="http://${HOST_DB_INTERNAL}:${SURREALDB_HOST_PORT}"
    COMPOSE_UP_SERVICES="azoa-api azoa-frontend"
fi

# ── Guard: container shell scripts MUST be LF ─────────────────────────────────
#
# A Windows checkout with core.autocrlf=true rewrites *.sh to CRLF on disk. A
# CRLF shebang gets baked into the image and the container dies at startup with
#   /usr/bin/env: 'sh\r': No such file or directory
# .gitattributes now pins these to eol=lf, but a stale working-tree copy (or a
# checkout before the attribute landed) can still slip through -- so normalise
# in-place here, BEFORE any image build, and warn loudly so it gets noticed.

CONTAINER_SCRIPTS="docker-entrypoint.sh"   # scripts COPYd into images (see Dockerfile)
for rel in $CONTAINER_SCRIPTS; do
    f="$SCRIPT_DIR/$rel"
    [ -f "$f" ] || continue
    if grep -qU $'\r' "$f" 2>/dev/null; then
        echo "[dev-up] WARN: $rel had CRLF line endings -- normalising to LF before build." >&2
        echo "[dev-up]       (a CRLF shebang would crash the container: \"env: 'sh\\r'...\")" >&2
        # Strip trailing CR on every line, in place.
        sed -i 's/\r$//' "$f"
    fi
done

# ── SDK rebuild (host-side dist; container build does its own tsup pass) ────

SDK_DIR="$SCRIPT_DIR/sdk/azoa-wallet"
if [ "$DO_REBUILD" -eq 1 ] && [ -d "$SDK_DIR" ]; then
    echo "[dev-up] Rebuilding @azoa/wallet-sdk (host-side dist)..."
    pushd "$SDK_DIR" >/dev/null
    if [ ! -d node_modules ]; then
        npm install --silent || { popd >/dev/null; echo "[dev-up] FATAL: SDK npm install failed." >&2; exit 1; }
    fi
    npm run build --silent || { popd >/dev/null; echo "[dev-up] FATAL: SDK build (tsup) failed." >&2; exit 1; }
    popd >/dev/null
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

prebuild_for_podman() {
    if [ "$DO_REBUILD" -ne 1 ] \
       && podman image exists "localhost/${PROJECT_NAME}_azoa-api:latest" \
       && podman image exists "localhost/${PROJECT_NAME}_azoa-frontend:latest"; then
        echo "[dev-up] --no-build + cached images present: skipping rebuild."
        return 0
    fi
    echo "[dev-up] podman runtime detected -- pre-building images per Dockerfile"
    echo "[dev-up]   (works around podman-compose v1.5.0 'dockerfile:' bug)"
    podman build -f Dockerfile -t "localhost/${PROJECT_NAME}_azoa-api:latest" "$SCRIPT_DIR"
    podman build -f frontend/Dockerfile -t "localhost/${PROJECT_NAME}_azoa-frontend:latest" "$SCRIPT_DIR"
}

case "$COMPOSE" in
    *podman*) prebuild_for_podman ;;
esac

# ── Build + start ────────────────────────────────────────────────────────────

BUILD_FLAG=""
# Don't pass --build for podman runtimes -- we already pre-built above,
# and triggering compose's broken builder would tag azoa-frontend with
# the wrong image content.
case "$COMPOSE" in
    *podman*) BUILD_FLAG="" ;;
    *)        [ "$DO_REBUILD" -eq 1 ] && BUILD_FLAG="--build" ;;
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

# ── SurrealDB schema sync ─────────────────────────────────────────────────────
#
# Default: `surrealforge up` -- idempotent. The CLI tracks applied files
# via the schema_migration table, so re-running is a no-op when nothing
# has changed and applies only pending files when there's drift.
#
# --reset: destructive wipe + full re-apply (delegates to the `reset` verb).
# AZOA_SKIP_RESET=1: skip the schema step entirely (preserves DB state,
#   useful when iterating on UI without touching schema).

if [ "${AZOA_SKIP_RESET:-}" = "1" ]; then
    echo "[dev-up] AZOA_SKIP_RESET=1 set -- skipping schema sync"
else
    : "${SURREALFORGE_NS:=azoa}"
    : "${SURREALFORGE_DB:=azoa}"
    : "${SURREALFORGE_USER:=root}"
    : "${SURREALFORGE_PASS:=root}"
    export SURREALFORGE_NS SURREALFORGE_DB SURREALFORGE_USER SURREALFORGE_PASS

    # Schema CLI runs on the HOST. The SURREALFORGE_URL set earlier for
    # the API container points at host.containers.internal which won't
    # resolve here -- override for the CLI call, restore after.
    SCHEMA_URL_BACKUP="${SURREALFORGE_URL:-}"
    export SURREALFORGE_URL="http://127.0.0.1:${SURREALDB_HOST_PORT}"

    # Wait for SurrealDB to be reachable.
    SURREAL_READY=0
    for _ in $(seq 1 20); do
        if curl -sfo /dev/null --max-time 2 "$SURREALFORGE_URL/health" 2>/dev/null; then
            SURREAL_READY=1
            break
        fi
        sleep 0.5
    done

    if [ "$SURREAL_READY" -ne 1 ]; then
        export SURREALFORGE_URL="$SCHEMA_URL_BACKUP"
        echo "" >&2
        echo "[dev-up] SurrealDB at http://127.0.0.1:${SURREALDB_HOST_PORT} never became reachable." >&2
        echo "[dev-up] Dumping bundled surrealdb container state + logs to diagnose:" >&2
        case "$COMPOSE" in
            *podman*) RUNTIME=podman ;;
            *)        RUNTIME=docker ;;
        esac
        if command -v "$RUNTIME" >/dev/null 2>&1; then
            echo "---- $RUNTIME ps (azoa-dev-surrealdb) ----" >&2
            "$RUNTIME" ps -a --filter 'name=azoa-dev-surrealdb' --format '{{.Status}}  {{.Names}}' 2>&1 >&2 || true
            echo "---- $RUNTIME logs --tail=50 azoa-dev-surrealdb ----" >&2
            "$RUNTIME" logs --tail=50 azoa-dev-surrealdb 2>&1 >&2 || true
            echo "---- end ----" >&2
        else
            echo "[dev-up] (no '$RUNTIME' on PATH to dump logs)" >&2
        fi
        echo "" >&2
        echo "[dev-up] Common causes:" >&2
        echo "  * Storage URI rejected by surrealdb v3.1.4 (look for 'failed to parse' in the log above)." >&2
        echo "  * Rootless podman volume ownership (look for 'permission denied' on /data)." >&2
        echo "  * Port 8000 already bound on host (look for 'address already in use')." >&2
        echo "  * Set AZOA_SKIP_RESET=1 to bypass schema sync while you investigate." >&2
        echo "[dev-up] Aborting -- SurrealDB unreachable. Logs above should name the cause." >&2
        exit 1
    fi

    echo ""
    # `|| SCHEMA_EXIT=$?` captures the failure WITHOUT tripping `set -e`, so
    # the guidance block below can actually print. A bare invocation would
    # abort the script before SCHEMA_EXIT is read.
    # Restore the SurrealForge.Schema CLI (pinned in .config/dotnet-tools.json).
    dotnet tool restore >/dev/null || { echo "[dev-up] dotnet tool restore failed" >&2; exit 1; }
    SCHEMA_EXIT=0
    if [ "$DO_RESET_SCHEMA" -eq 1 ]; then
        echo "[dev-up] --reset: wiping + re-applying SurrealDB schema..."
        dotnet surrealforge reset || SCHEMA_EXIT=$?
    else
        echo "[dev-up] syncing SurrealDB schema (idempotent; use --reset to wipe)..."
        dotnet surrealforge up || SCHEMA_EXIT=$?
    fi
    export SURREALFORGE_URL="$SCHEMA_URL_BACKUP"
    if [ "$SCHEMA_EXIT" -ne 0 ]; then
        if [ "$DO_RESET_SCHEMA" -eq 1 ]; then
            echo "[dev-up] SurrealDB reset failed (exit $SCHEMA_EXIT). Set AZOA_SKIP_RESET=1 to skip, or pass --reset-db for a clean volume wipe." >&2
        else
            echo "[dev-up] SurrealDB up failed (exit $SCHEMA_EXIT). Set AZOA_SKIP_RESET=1 to skip, or pass --reset to force a clean schema apply." >&2
        fi
        exit "$SCHEMA_EXIT"
    fi
fi

echo ""
echo "[dev-up] Endpoints:"
echo "  WebAPI:    http://localhost:5000  (health: /health)"
echo "  Frontend:  http://localhost:3000"
echo "  SurrealDB: http://localhost:8000  (root / root)"
echo ""
echo "[dev-up] Tear down: ./dev-down.sh"
echo "[dev-up] Logs:      $COMPOSE -f $COMPOSE_FILE logs -f <service>"
echo ""
echo "[dev-up] Flags (run ./dev-up.sh <flag>):"
echo "  --no-build   Skip image + SDK rebuild. Fast restart, reuses cached images."
echo "  --reset-db   DESTRUCTIVE. Wipe SurrealDB volume before bringing the stack up."
echo "               (alias: --clean)"
echo "  --reset      Wipe + re-apply SurrealDB schema WITHOUT touching the volume."
echo "               Combine with --reset-db for a total reset."
echo "  --logs       Tail combined container logs after startup (Ctrl-C to stop)."
echo ""
echo "  Default behavior (no flags):"
echo "    * Rebuilds API + Frontend images and host-side SDK dist"
echo "    * PRESERVES the SurrealDB volume across restart"
echo "    * Applies pending schema migrations idempotently"
echo ""

if [ "$TAIL_LOGS" -eq 1 ]; then
    echo ""
    echo "[dev-up] Tailing combined logs (Ctrl-C to stop):"
    exec $COMPOSE -f "$COMPOSE_FILE" logs -f
fi
