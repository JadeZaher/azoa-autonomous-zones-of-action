#!/usr/bin/env sh
# AZOA WebAPI container entrypoint.
#
# Three responsibilities:
#   1. Wait for SurrealDB to become reachable (the compose `depends_on:
#      condition: service_healthy` covers most cases, but we add a belt
#      check so a hand-run container doesn't crash on first request).
#   2. In Development, run `surrealforge up` so the local namespace + database
#      have every committed `.surql` applied. Production uses a separate job.
#   3. exec the WebAPI host so signals propagate cleanly and the process
#      tree stays flat.
#
# Production requires AZOA_SKIP_MIGRATIONS=1: schema credentials must never
# enter the API process.

set -eu

load_schema_config() {
    SURREAL_URL="${SURREALFORGE_URL:-http://surrealdb:8000}"
    SURREAL_NS="${SURREALFORGE_NS:-azoa}"
    SURREAL_DB="${SURREALFORGE_DB:-azoa}"
    SURREAL_USER="${SURREALFORGE_USER:-root}"
    SURREAL_PASS="${SURREALFORGE_PASS:-root}"
}

require_schema_job_config() {
    if [ -z "${SURREALFORGE_URL:-}" ] \
        || [ -z "${SURREALFORGE_NS:-}" ] \
        || [ -z "${SURREALFORGE_DB:-}" ] \
        || [ -z "${SURREALFORGE_USER:-}" ] \
        || [ -z "${SURREALFORGE_PASS:-}" ]; then
        echo "[entrypoint] Schema mode requires all five explicit SURREALFORGE_URL/NS/DB/USER/PASS values." >&2
        exit 64
    fi

    if ! printf '%s' "$SURREALFORGE_URL" | grep -Eq '^https?://[^[:space:]]+$' \
        || ! printf '%s' "$SURREALFORGE_NS" | grep -Eq '^[A-Za-z][A-Za-z0-9_-]{0,63}$' \
        || ! printf '%s' "$SURREALFORGE_DB" | grep -Eq '^[A-Za-z][A-Za-z0-9_-]{0,63}$'; then
        echo "[entrypoint] Invalid SurrealDB schema-job endpoint, namespace, or database." >&2
        exit 64
    fi

    if ! printf '%s' "$SURREALFORGE_USER" | grep -Eq '^[A-Za-z][A-Za-z0-9_]{2,63}$' \
        || printf '%s' "$SURREALFORGE_USER" | grep -Eiq '^root$' \
        || ! printf '%s' "$SURREALFORGE_PASS" | grep -Eq '^[A-Za-z0-9._~-]{32,}$'; then
        echo "[entrypoint] Schema owner credentials must be explicit, non-root, and use a 32+ character URL-safe password." >&2
        exit 64
    fi

    if ! printf '%s' "${AZOA_RUNTIME_USER:-}" | grep -Eq '^[A-Za-z][A-Za-z0-9_]{2,63}$' \
        || ! printf '%s' "${AZOA_RUNTIME_PASSWORD:-}" | grep -Eq '^[A-Za-z0-9._~-]{32,}$' \
        || [ "$SURREALFORGE_USER" = "$AZOA_RUNTIME_USER" ] \
        || [ "$SURREALFORGE_PASS" = "$AZOA_RUNTIME_PASSWORD" ]; then
        echo "[entrypoint] Schema owner and runtime credentials must both exist and remain distinct." >&2
        exit 64
    fi
}

prepare_schema_directory() {
    RUNTIME_SCHEMA_DIR="${TMPDIR:-/tmp}/azoa-runtime-schemas-$$"
    mkdir -p "$RUNTIME_SCHEMA_DIR"
    cp /app/persistence/SurrealDb/Generated/Schemas/*.surql "$RUNTIME_SCHEMA_DIR/"
    if [ -d /app/persistence/SurrealDb/CompatibilityBaselines ]; then
        cp /app/persistence/SurrealDb/CompatibilityBaselines/*.surql "$RUNTIME_SCHEMA_DIR/"
    fi
}

wait_for_surreal() {
    echo "[entrypoint] Waiting for SurrealDB at $SURREAL_URL ..."
    attempts=0
    until curl -sf "$SURREAL_URL/health" >/dev/null 2>&1; do
        attempts=$((attempts + 1))
        if [ "$attempts" -ge 60 ]; then
            echo "[entrypoint] SurrealDB did not become reachable after 60 attempts (~2 min). Aborting." >&2
            exit 1
        fi
        sleep 2
    done
    echo "[entrypoint] SurrealDB is reachable."
}

apply_schema() {
    prepare_schema_directory
    echo "[entrypoint] Applying schemas + migrations via surrealforge up ..."
    # The SurrealForge.Schema CLI is extracted to /app/schema-cli by the
    # Dockerfile build stage; it is not installed through a dotnet tool manifest.
    # Invoked as `dotnet <dll>` — the SurrealForge.Schema package ships a
    # framework-dependent CLI payload (not an installed dotnet tool); see the
    # Dockerfile schema-cli extraction step.
    dotnet /app/schema-cli/SurrealForge.Schema.dll up \
        --connection "$SURREAL_URL" \
        --user "$SURREAL_USER" \
        --pass "$SURREAL_PASS" \
        --namespace "$SURREAL_NS" \
        --database "$SURREAL_DB" \
        --schemas-dir "$RUNTIME_SCHEMA_DIR" \
        --migrations-dir /app/persistence/SurrealDb/Migrations \
        --applied-by "azoa-local/docker-entrypoint"
}

provision_runtime_user() {
    runtime_user="${AZOA_RUNTIME_USER:-}"
    runtime_pass="${AZOA_RUNTIME_PASSWORD:-}"

    if ! printf '%s' "$SURREAL_URL" | grep -Eq '^https?://[^[:space:]]+$' \
        || ! printf '%s' "$SURREAL_NS" | grep -Eq '^[A-Za-z][A-Za-z0-9_-]{0,63}$' \
        || ! printf '%s' "$SURREAL_DB" | grep -Eq '^[A-Za-z][A-Za-z0-9_-]{0,63}$'; then
        echo "[entrypoint] Invalid SurrealDB schema-job endpoint, namespace, or database." >&2
        exit 64
    fi

    if ! printf '%s' "$runtime_user" | grep -Eq '^[A-Za-z][A-Za-z0-9_]{2,63}$' \
        || ! printf '%s' "$runtime_pass" | grep -Eq '^[A-Za-z0-9._~-]{32,}$'; then
        echo "[entrypoint] AZOA_RUNTIME_USER must be a safe database-user identifier and AZOA_RUNTIME_PASSWORD must be at least 32 URL-safe characters." >&2
        exit 64
    fi

    query="DEFINE USER OVERWRITE $runtime_user ON DATABASE PASSWORD '$runtime_pass' ROLES EDITOR;"
    response="$(printf '%s' "$query" | curl -fsS \
        --user "$SURREAL_USER:$SURREAL_PASS" \
        --header "Surreal-NS: $SURREAL_NS" \
        --header "Surreal-DB: $SURREAL_DB" \
        --header "Accept: application/json" \
        --header "Content-Type: application/surrealql" \
        --data-binary @- \
        "$SURREAL_URL/sql")"

    if ! printf '%s' "$response" | grep -Eq '"status"[[:space:]]*:[[:space:]]*"OK"' \
        || printf '%s' "$response" | grep -Eq '"status"[[:space:]]*:[[:space:]]*"ERR"'; then
        echo "[entrypoint] Failed to provision the database-scoped runtime user." >&2
        exit 1
    fi

    echo "[entrypoint] Database-scoped EDITOR runtime user provisioned."
}

if [ "${1:-}" = "schema" ]; then
    require_schema_job_config
    load_schema_config
    wait_for_surreal
    apply_schema
    provision_runtime_user
    echo "[entrypoint] Schema job completed successfully."
    exit 0
fi

if [ "$(printf '%s' "${ASPNETCORE_ENVIRONMENT:-Production}" | tr '[:upper:]' '[:lower:]')" = "production" ] \
    && [ "${AZOA_SKIP_MIGRATIONS:-0}" != "1" ]; then
    echo "[entrypoint] Production refuses API-boot migrations. Run the separate schema job and set AZOA_SKIP_MIGRATIONS=1." >&2
    exit 64
fi

if [ "${AZOA_SKIP_MIGRATIONS:-0}" != "1" ]; then
    # Schema credentials are deliberately distinct from SurrealRuntime__*.
    load_schema_config
    wait_for_surreal
    apply_schema
else
    echo "[entrypoint] AZOA_SKIP_MIGRATIONS=1 -- skipping migration step."
fi

# Honor Railway's injected $PORT (falls back to 5000 for compose). The
# Dockerfile's ENV ASPNETCORE_URLS is overridden here so a platform-provided
# port takes effect without rebuilding the image.
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${PORT:-5000}}"

echo "[entrypoint] Starting WebAPI host on $ASPNETCORE_URLS ..."
if [ "$(id -u)" = "0" ]; then
    app_uid="${APP_UID:-1654}"
    key_ring_path="${DataProtection__KeyRingPath:-/app/data/data-protection-keys}"
    case "$key_ring_path" in
        /app/data/*) ;;
        *)
            echo "[entrypoint] Data Protection key path must stay below /app/data when repairing a Railway volume." >&2
            exit 64
            ;;
    esac
    mkdir -p "$key_ring_path"
    chown -R "$app_uid:$app_uid" /app/data
    echo "[entrypoint] Repaired Railway volume ownership; dropping to uid $app_uid."
    exec setpriv --reuid="$app_uid" --regid="$app_uid" --init-groups \
        dotnet /app/AZOA.WebAPI.dll
fi
exec dotnet /app/AZOA.WebAPI.dll
