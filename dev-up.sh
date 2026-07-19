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

SOURCE_REVISION="$(git -C "$SCRIPT_DIR" rev-parse HEAD 2>/dev/null || true)"
if ! printf '%s' "$SOURCE_REVISION" | grep -Eq '^[0-9a-f]{40}$'; then
    echo "[dev-up] FATAL: unable to resolve a 40-character Git SOURCE_REVISION." >&2
    exit 1
fi
export SOURCE_REVISION

if [ ! -f "$COMPOSE_FILE" ]; then
    echo "[dev-up] FATAL: $COMPOSE_FILE not found." >&2
    exit 1
fi

COMPOSE_OVERRIDE_FILE=""
if [ -n "${AZOA_SURREAL_VOLUME_NAME:-}" ]; then
    if ! printf '%s' "$AZOA_SURREAL_VOLUME_NAME" | grep -Eq '^[A-Za-z0-9][A-Za-z0-9_.-]*$'; then
        echo "[dev-up] FATAL: AZOA_SURREAL_VOLUME_NAME must be a Docker/Podman-safe volume name." >&2
        exit 1
    fi
    COMPOSE_OVERRIDE_FILE="$SCRIPT_DIR/docker-compose.qa-volume.yml"
    if [ ! -f "$COMPOSE_OVERRIDE_FILE" ]; then
        echo "[dev-up] FATAL: $COMPOSE_OVERRIDE_FILE not found." >&2
        exit 1
    fi
    echo "[dev-up] Using isolated SurrealDB volume: $AZOA_SURREAL_VOLUME_NAME"
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

compose_run() {
    if [ -n "$COMPOSE_OVERRIDE_FILE" ]; then
        # COMPOSE intentionally word-splits `docker compose` / `podman compose`.
        # shellcheck disable=SC2086
        $COMPOSE -f "$COMPOSE_FILE" -f "$COMPOSE_OVERRIDE_FILE" "$@"
    else
        # shellcheck disable=SC2086
        $COMPOSE -f "$COMPOSE_FILE" "$@"
    fi
}

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

# The API container entrypoint owns ordinary schema application. For an
# explicit reset, start it without `up`; reset is
# invoked below through the packaged CLI inside the running API container.
if [ "$DO_RESET_SCHEMA" -eq 1 ]; then
    export AZOA_SKIP_MIGRATIONS=1
fi

PROJECT_NAME="$(basename "$SCRIPT_DIR")"

# ── Teardown (default: preserve volume; --reset-db to wipe) ──────────────────

if [ "$DO_WIPE" -eq 1 ]; then
    echo "[dev-up] --reset-db: tearing down stack + wiping database and cursor-key volumes..."
    compose_run down -v --remove-orphans || true
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
    podman build --build-arg "SOURCE_REVISION=$SOURCE_REVISION" -f Dockerfile -t "localhost/${PROJECT_NAME}_azoa-api:latest" "$SCRIPT_DIR"
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
    # Force Compose to reuse the explicitly-tagged images above. Some
    # podman-compose versions rebuild services with a build section even when
    # the expected tag exists, re-entering the broken Dockerfile selection path.
    *podman*) BUILD_FLAG="--no-build" ;;
    *)        [ "$DO_REBUILD" -eq 1 ] && BUILD_FLAG="--build" ;;
esac

NO_DEPS_FLAG=""
# --no-deps tells compose to ignore depends_on when an external SurrealDB
# is in play, so it doesn't try to start the bundled surrealdb service
# (which would collide on port 8000).
[ -n "$COMPOSE_UP_SERVICES" ] && NO_DEPS_FLAG="--no-deps"

echo "[dev-up] Starting stack ..."
# shellcheck disable=SC2086
compose_run up -d --remove-orphans $BUILD_FLAG $NO_DEPS_FLAG $COMPOSE_UP_SERVICES

echo "[dev-up] Stack started. Service status:"
compose_run ps

# ── SurrealDB schema sync ─────────────────────────────────────────────────────
#
# Ordinary bundled and external-DB launches use the API container entrypoint,
# which carries the exact schema CLI payload extracted by Dockerfile. The host
# never restores the NuGet package as a dotnet tool (it is not tool-packaged).
# --reset starts the API with migrations skipped, then invokes that packaged
# CLI inside the container for an explicit destructive reset.

if [ "$DO_RESET_SCHEMA" -eq 1 ]; then
    case "$COMPOSE" in
        *podman*) SCHEMA_RUNTIME=podman ;;
        *)        SCHEMA_RUNTIME=docker ;;
    esac
    echo "[dev-up] --reset: running packaged schema reset inside azoa-dev-api..."
    "$SCHEMA_RUNTIME" exec azoa-dev-api /bin/sh -c \
        'exec dotnet /app/schema-cli/SurrealForge.Schema.dll reset --connection "$SURREALFORGE_URL" --user "$SURREALFORGE_USER" --pass "$SURREALFORGE_PASS" --namespace "$SURREALFORGE_NS" --database "$SURREALFORGE_DB" --schemas-dir /app/persistence/SurrealDb/Generated/Schemas --migrations-dir /app/persistence/SurrealDb/Migrations --applied-by "azoa-local/dev-up-reset"'
elif [ "${AZOA_SKIP_MIGRATIONS:-}" = "1" ]; then
    echo "[dev-up] AZOA_SKIP_MIGRATIONS=1 -- API entrypoint skipped schema sync."
else
    echo "[dev-up] Schema sync is owned by the API container entrypoint (bundled or external SurrealDB)."
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
    compose_run logs -f
fi
