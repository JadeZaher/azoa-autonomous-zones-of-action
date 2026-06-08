#!/usr/bin/env sh
# OASIS WebAPI container entrypoint.
#
# Three responsibilities:
#   1. Wait for SurrealDB to become reachable (the compose `depends_on:
#      condition: service_healthy` covers most cases, but we add a belt
#      check so a hand-run container doesn't crash on first request).
#   2. Run `oasis-surreal up` so the deployed namespace + database have
#      every committed `.surql` applied. Idempotent on re-runs.
#   3. exec the WebAPI host so signals propagate cleanly and the process
#      tree stays flat.
#
# Skip the migration step with OASIS_SKIP_MIGRATIONS=1 -- useful in CI
# when migrations are already applied by an earlier step.

set -eu

# Resolve the SurrealDB endpoint from the same env-var aliases the
# oasis-surreal CLI recognises. Fall back to the compose service name
# `surrealdb` so the docker-compose.dev.yml works without extra wiring.
SURREAL_URL="${OASIS_SURREAL_URL:-http://surrealdb:8000}"
SURREAL_NS="${OASIS_SURREAL_NS:-oasis}"
SURREAL_DB="${OASIS_SURREAL_DB:-oasis}"
SURREAL_USER="${OASIS_SURREAL_USER:-root}"
SURREAL_PASS="${OASIS_SURREAL_PASS:-root}"

if [ "${OASIS_SKIP_MIGRATIONS:-0}" != "1" ]; then
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

    echo "[entrypoint] Applying schemas + migrations via oasis-surreal up ..."
    # Explicit `dotnet <dll>` form -- works regardless of whether the
    # Schema package was published with the native launcher shim.
    dotnet /app/schema-cli/Oasis.SurrealDb.Schema.dll up \
        --connection "$SURREAL_URL" \
        --user "$SURREAL_USER" \
        --pass "$SURREAL_PASS" \
        --namespace "$SURREAL_NS" \
        --database "$SURREAL_DB" \
        --schemas-dir /app/persistence/SurrealDb/Generated/Schemas \
        --migrations-dir /app/persistence/SurrealDb/Migrations \
        --applied-by "oasis-api/docker-entrypoint"
else
    echo "[entrypoint] OASIS_SKIP_MIGRATIONS=1 -- skipping migration step."
fi

echo "[entrypoint] Starting WebAPI host ..."
exec dotnet /app/OASIS.WebAPI.dll
