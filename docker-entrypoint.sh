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

if [ "$(printf '%s' "${ASPNETCORE_ENVIRONMENT:-Production}" | tr '[:upper:]' '[:lower:]')" = "production" ] \
    && [ "${AZOA_SKIP_MIGRATIONS:-0}" != "1" ]; then
    echo "[entrypoint] Production refuses API-boot migrations. Run the separate schema job and set AZOA_SKIP_MIGRATIONS=1." >&2
    exit 64
fi

if [ "${AZOA_SKIP_MIGRATIONS:-0}" != "1" ]; then
    # Schema credentials are deliberately distinct from SurrealRuntime__*.
    SURREAL_URL="${SURREALFORGE_URL:-http://surrealdb:8000}"
    SURREAL_NS="${SURREALFORGE_NS:-azoa}"
    SURREAL_DB="${SURREALFORGE_DB:-azoa}"
    SURREAL_USER="${SURREALFORGE_USER:-root}"
    SURREAL_PASS="${SURREALFORGE_PASS:-root}"
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

    echo "[entrypoint] Applying schemas + migrations via surrealforge up ..."
    # The SurrealForge.Schema dotnet tool, installed to /app/schema-cli via
    # `dotnet tool install --tool-path` in the Dockerfile build stage.
    # Invoked as `dotnet <dll>` — the SurrealForge.Schema package ships a
    # framework-dependent CLI payload (not an installed dotnet tool); see the
    # Dockerfile schema-cli extraction step.
    dotnet /app/schema-cli/SurrealForge.Schema.dll up \
        --connection "$SURREAL_URL" \
        --user "$SURREAL_USER" \
        --pass "$SURREAL_PASS" \
        --namespace "$SURREAL_NS" \
        --database "$SURREAL_DB" \
        --schemas-dir /app/persistence/SurrealDb/Generated/Schemas \
        --migrations-dir /app/persistence/SurrealDb/Migrations \
        --applied-by "azoa-local/docker-entrypoint"
else
    echo "[entrypoint] AZOA_SKIP_MIGRATIONS=1 -- skipping migration step."
fi

# Honor Railway's injected $PORT (falls back to 5000 for compose). The
# Dockerfile's ENV ASPNETCORE_URLS is overridden here so a platform-provided
# port takes effect without rebuilding the image.
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:${PORT:-5000}}"

echo "[entrypoint] Starting WebAPI host on $ASPNETCORE_URLS ..."
exec dotnet /app/AZOA.WebAPI.dll
