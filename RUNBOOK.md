# AZOA Sleek — Runbook

Operational reference: how to start, stop, reset, deploy, and diagnose the
stack. For developer setup + conventions see [DEVELOPMENT.md](DEVELOPMENT.md);
for live track status see [conductor/tracks.md](conductor/tracks.md).

---

## 1. Local stack

Full stack (SurrealDB + WebAPI + Frontend) via `docker-compose.dev.yml`,
orchestrated by the `dev-up` / `dev-down` scripts. The scripts auto-detect
`docker compose` (v2), `docker-compose` (v1), `podman-compose`, or
`podman compose`.

### Start

```bash
./dev-up.sh          # or ./dev-up.ps1 on Windows
```

Default: rebuilds the API + Frontend images and the host-side SDK dist,
preserves the SurrealDB volume, and applies pending schema migrations
idempotently. Flags:

| Flag (bash / PowerShell) | Effect |
|---|---|
| `--no-build` / `-NoBuild` | Skip the image + SDK rebuild. Fast restart on cached images. |
| `--reset-db` / `-ResetDb` | **DESTRUCTIVE.** Tear down + wipe the SurrealDB volume before bringing the stack up. (alias: `--clean` / `-Clean`) |
| `--reset` / `-Reset` | Wipe + re-apply the SurrealDB schema/namespace WITHOUT touching the volume. Combine with `--reset-db` for a total reset. |
| `--logs` / `-Logs` | Tail combined container logs after startup. |
| `AZOA_SKIP_RESET=1` (env) | Skip the host-side schema sync entirely (the container entrypoint still applies `surrealforge up`). |

After ~30-60s (first run builds images):

| Service | URL | Notes |
|---|---|---|
| Frontend | http://localhost:3000 | Next.js app |
| WebAPI | http://localhost:5000 | health: `/health`, swagger: `/swagger/v1/swagger.json` |
| SurrealDB | http://localhost:8000 | `root` / `root`, persistent volume `surrealdb_data` |

**Custom host port:** set `SURREALDB_HOST_PORT` to remap the SurrealDB
host-side port when 8000 is occupied (the detection probe + host-side
schema sync honor it). The scripts also self-detect an already-running
bundled `azoa-dev-surrealdb` container and take the normal full-stack
path; a non-bundled SurrealDB already answering on the host port is treated
as external and the API is pointed at it via
`host.docker.internal` / `host.containers.internal`.

### Stop

```bash
./dev-down.sh                  # stop containers, preserve volume
./dev-down.sh --wipe           # also drop the surrealdb_data volume (-Wipe / -ResetDb on PowerShell)
```

### Reset (fresh DB)

```bash
./dev-down.sh --wipe           # drop surrealdb_data volume
./dev-up.sh                    # idempotent schema sync re-creates everything
```
Or, preserving the volume but re-applying the namespace:
```bash
./dev-up.sh --reset            # destructively wipes + re-applies the namespace
```

The host-side schema sync needs `dotnet` on the host (the schema CLI runs
from source via `dotnet run`). Without it, set `AZOA_SKIP_RESET=1` — the
WebAPI container's entrypoint applies `surrealforge up` on its own. See
[DEVELOPMENT.md](DEVELOPMENT.md) for host-run and bring-your-own-SurrealDB
variants.

---

## 2. Production deploy (Railway)

The WebAPI ships as a single image (`Dockerfile`) bundling the
`surrealforge` schema CLI alongside `AZOA.WebAPI.dll`. SurrealDB runs as a
separate Railway service.

### WebAPI service — required environment variables

| Variable | Required | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | yes | Set to `Production`. Gates Swagger off and enforces the G1 durability ack below. |
| `Jwt__Key` | yes | JWT signing key, ≥32 chars. No default — boot fails without it. |
| `Jwt__Issuer` / `Jwt__Audience` | optional | Default `AZOA.WebAPI` / `AZOA.Client`. |
| `AZOA__WalletEncryptionKey` | yes | Symmetric key for platform wallet generation. No default — `WalletKeyService` throws without it. |
| `SurrealDb__Endpoint` | yes | URL of the SurrealDB service (e.g. `http://surrealdb.railway.internal:8000`). |
| `SurrealDb__Namespace` | yes | e.g. `azoa`. |
| `SurrealDb__Database` | yes | e.g. `azoa`. |
| `SurrealDb__User` | yes | SurrealDB root user. |
| `SurrealDb__Password` | yes | SurrealDB root password. |
| `SurrealDb__G1DurabilityAcknowledged` | yes | Must be `true`. Outside `IntegrationTest`, `Program.cs` refuses to boot unless this is set — it's the operator's acknowledgement that the SurrealDB storage URI runs with per-commit WAL sync (see §2 durability note). |
| `PORT` | injected | Railway injects `$PORT`; the entrypoint binds `ASPNETCORE_URLS=http://0.0.0.0:$PORT` (falls back to 5000). Do NOT also pin `ASPNETCORE_URLS` to a fixed port — let the entrypoint honor `$PORT`. |

The `SurrealDb__*` family is consumed by both the .NET host AND the
entrypoint's migration pre-step; you only need to wire one family. The
entrypoint also accepts the `SURREALFORGE_*` aliases
(`SURREALFORGE_URL` / `_NS` / `_DB` / `_USER` / `_PASS`) if preferred.

### Entrypoint migration behavior

On every boot, `docker-entrypoint.sh`:
1. Waits up to ~2 min for SurrealDB to answer `/health`, then aborts if
   unreachable.
2. Runs `surrealforge up` — applies `Generated/Schemas/` then
   `Migrations/` against the configured namespace/database. **Idempotent**:
   the `schema_migration` ledger skips already-applied files. A fresh
   SurrealDB needs no out-of-band setup (the runner bootstraps
   `DEFINE NAMESPACE/DATABASE IF NOT EXISTS`).
3. Execs the WebAPI host.

Skip the migration step with `AZOA_SKIP_MIGRATIONS=1` (e.g. when an earlier
deploy step already applied them).

### SurrealDB service on Railway

| Aspect | Value |
|---|---|
| Image | `surrealdb/surrealdb:v3.1.4` (pin matches the rest of the stack; the 1.5.4→3.x cutover tracked at `surrealdb-major-upgrade` is closed). |
| Start command | `start --user root --pass <pass> --bind 0.0.0.0:8000 rocksdb:///data/db` |
| Volume | Mount a persistent volume at `/data` so the RocksDB store survives restarts. |
| Durability | RocksDB syncs its WAL per commit (`SURREAL_SYNC_DATA: "true"`), which satisfies the G1 durability gate — this is what `SurrealDb__G1DurabilityAcknowledged=true` on the API acknowledges. **G1 is live and green on SurrealDB 3.1.4** (re-verified during final-hardening-cutover). RocksDB remains the durable path; review the storage URI + sync flags at deploy time, since the fsync mode is not introspectable at runtime. |

---

## 3. Common diagnostics

**`dev-up.sh` says "no compose runtime found"**
Install one of: Docker Desktop, Docker Engine (Linux), or Podman 4.x+. The
script picks the first one it finds.

**SurrealDB never became reachable at boot**
1. Container running? `docker compose -f docker-compose.dev.yml ps`
2. Host port (default 8000) free? Set `SURREALDB_HOST_PORT` to remap if not.
3. On `--reset` failure the script dumps `azoa-dev-surrealdb` state + the
   last 50 log lines — common causes are a rejected storage URI, rootless
   podman volume-ownership (`permission denied` on `/data`), or the port
   already bound.

**"checksum mismatch detected" re-running `surrealforge up`**
A migration file drifted from its recorded `schema_migration` hash. Revert
the edit, or rerun with `--force` (see the migrations README "Drift
detection").

**WebAPI boots but `/health` returns Unhealthy**
The `storage-db` check failed. Confirm SurrealDB is reachable from the API's
perspective:
- compose: `docker exec azoa-dev-api curl -s http://surrealdb:8000/health`
- host-run: `curl http://127.0.0.1:8000/health`

**WebAPI refuses to boot citing G1 durability**
`SurrealDb:G1DurabilityAcknowledged` is unset/false in a non-`IntegrationTest`
environment. Confirm the SurrealDB storage URI runs with per-commit sync,
then set `SurrealDb__G1DurabilityAcknowledged=true`.

**Frontend hits CORS / wrong-API-URL errors**
`NEXT_PUBLIC_API_URL` is **baked in at Next.js build time** — editing it in
compose alone does nothing. Update it AND rebuild the frontend image
(`./dev-up.sh` rebuilds by default, or
`docker compose -f docker-compose.dev.yml build azoa-frontend`). See the
DEVELOPMENT.md troubleshooting note.

**Which migrations are applied?**
```bash
surrealforge migrate status
```
Reads the `schema_migration` ledger. `dev-up` does this implicitly on every
run; repeat invocations are no-ops when the ledger matches the on-disk files.

---

## 4. Where to look for what

| Question | Document |
|---|---|
| "How do I clone + run locally?" | [DEVELOPMENT.md](DEVELOPMENT.md) |
| "What's the right C# pattern for a new SurrealDB entity?" | [Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md) |
| "What does the API surface look like?" | [PROVIDERS.md](PROVIDERS.md) |
| "What invariants does the bridge enforce?" | [docs/RESIDUAL-RISK-RUNBOOK.md](docs/RESIDUAL-RISK-RUNBOOK.md) |
| "Which track is which?" | [conductor/tracks.md](conductor/tracks.md) |
| "Historical RUNBOOK status snapshots" | [conductor/retros/runbook-status-2026-06-12.md](conductor/retros/runbook-status-2026-06-12.md) |

---

## 5. Doc-drift audit corrections (final-hardening-cutover Phase H, H8)

Two independent fresh-eyes audits (2026-07-05) were misled by stale operator
docs into flagging items that were, in fact, already resolved. Recorded here so
neither claim resurfaces:

1. **Sagas default is `Enabled=true`, not `false`.** `docs/GO-TO-PROD.md` (dated
   2026-05-18) and its "no consumer until durable-saga Phase 2" premise are
   superseded — the durable-quest engine IS a saga consumer
   (`QuestManager`/`SagaProcessorHostedService`), so `Sagas:Enabled=true` ships
   as the deliberate default with a boot guard. See
   [[durable-quests-inert-sagas-disabled]] for the fix history.
2. **The G1 durability gate is live and green on SurrealDB 3.1.4.** RocksDB WAL
   sync (`SURREAL_SYNC_DATA: "true"`) + `SurrealDb__G1DurabilityAcknowledged=true`
   were re-verified against the 3.1.4 pin during `final-hardening-cutover`; the
   gate is not stale or unverified.

`docs/GO-TO-PROD.md` and `docs/RESIDUAL-RISK-RUNBOOK.md` now carry deprecation
banners pointing to `docs/NODE-HOST.md` as the authoritative operator guide;
this section is the durable record of why.
