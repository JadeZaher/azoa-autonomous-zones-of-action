# OASIS Sleek — developer setup

Clone, run, hit `localhost:3000` and `localhost:5000`. That's the goal.

## TL;DR

```bash
git clone <repo> oasis-sleek
cd oasis-sleek
./dev-up.sh          # or ./dev-up.ps1 on Windows
```

After ~30-60 seconds (first run: image build):

| Service | URL | Notes |
|---|---|---|
| Frontend | http://localhost:3000 | Next.js app |
| WebAPI | http://localhost:5000 | health: `/health`, swagger: `/swagger/v1/swagger.json` |
| SurrealDB | http://localhost:8000 | `root` / `root`, persistent volume `surrealdb_data` |

Tear down: `./dev-down.sh` (or `.ps1`). Wipe the DB volume too:
`./dev-down.sh --wipe` (or `-Wipe`).

## What `dev-up` actually does

1. **Detect compose runtime** — checks for, in order: `docker compose`
   (v2 plugin), `docker-compose` (v1 standalone), `podman-compose`,
   `podman compose` (4.x+ subcommand). First match wins.
2. **Bring up SurrealDB** — `surrealdb/surrealdb:v1.5.4` container with
   surrealkv backing at `surrealkv:///data/db?sync=every` (G1 durability
   contract). Healthcheck polls `/health` until ready.
3. **Bring up the WebAPI** — `Dockerfile` builds the .NET 8 image plus
   the `oasis-surreal` CLI. The container's entrypoint
   ([`docker-entrypoint.sh`](docker-entrypoint.sh)):
   1. Waits for SurrealDB to be reachable
   2. Runs `oasis-surreal up` (applies `Persistence/SurrealDb/Generated/Schemas/`
      then `Persistence/SurrealDb/Migrations/` via the runner, idempotent)
   3. Execs `dotnet OASIS.WebAPI.dll`
4. **Bring up the frontend** — Next.js dev image talking to the WebAPI
   at the host-mapped port. Depends on the WebAPI's `/health` being
   green.

## Variants

### Option A: Pure docker-compose (no host .NET / Node needed)
```
./dev-up.sh                # build + up all three services
./dev-up.sh --rebuild      # force rebuild after a code change
./dev-up.sh --logs         # tail combined logs after startup
./dev-up.sh --clean        # wipe DB volume first, then start
```

### Option B: Host-run WebAPI against a containerised SurrealDB
Useful when iterating on the C# side and you want the debugger attached:

```bash
# 1. Spin up just the DB
docker compose -f docker-compose.dev.yml up -d surrealdb

# 2. Apply schemas
packages/Oasis.SurrealDb.Schema/bin/Debug/net8.0/Oasis.SurrealDb.Schema up \
    --connection http://127.0.0.1:8000 \
    --user root --pass root \
    --namespace oasis --database oasis

# 3. Run the WebAPI (uses appsettings.Development.json values)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project OASIS.WebAPI.csproj
```

### Option C: Everything host-run (you bring your own SurrealDB)
Already covered by your `localhost:8000` instance + the steps above
starting at step 2.

## Configuration

The `SurrealDb` section in `appsettings.json` / `appsettings.Development.json`
binds to [`SurrealConnectionOptions`](packages/Oasis.SurrealDb.Client/SurrealConnectionOptions.cs):

```json
"SurrealDb": {
  "Endpoint":   "http://127.0.0.1:8000",
  "Namespace":  "oasis",
  "Database":   "oasis",
  "User":       "root",
  "Password":   "root",
  "G1DurabilityAcknowledged": true
}
```

Inside the docker-compose, the same values are injected via
`SurrealDb__Endpoint` etc. environment variables and the endpoint
becomes `http://surrealdb:8000` (service-name DNS inside the compose
network).

CLI invocations + the migration container use the same values via the
`OASIS_SURREAL_URL` / `_NS` / `_DB` / `_USER` / `_PASS` env-var aliases.

## Migrations

See [`Persistence/SurrealDb/Migrations/README.md`](Persistence/SurrealDb/Migrations/README.md)
for the data-migration authoring guide.

The schema (auto-generated from `[SurrealTable]` POCOs in
[`Persistence/SurrealDb/Models/`](Persistence/SurrealDb/Models/)) lands
at [`Persistence/SurrealDb/Generated/Schemas/`](Persistence/SurrealDb/Generated/Schemas/)
— never hand-edit those files. Edit the POCO and the build + the live
integration test regenerate them.

## Troubleshooting

**`dev-up.sh` says "no compose runtime found"**
Install one of: Docker Desktop (Windows / macOS), Docker Engine
(Linux), or Podman 4.x+. The script picks the first one it finds.

**"SurrealDB server unreachable at boot"**
Two things to check:
1. The container is running: `docker compose -f docker-compose.dev.yml ps`
2. Port 8000 isn't already occupied by something else on the host.

**"checksum mismatch detected" when re-running `oasis-surreal up`**
A migration file's content drifted from what's recorded in the
`schema_migration` ledger. Either revert the edit OR rerun with
`--force` (see migrations README §"Drift detection").

**WebAPI starts but `/health` returns Unhealthy**
The `storage-db` check failed. Confirm SurrealDB is reachable from the
WebAPI's perspective (`docker exec oasis-dev-api curl -s http://surrealdb:8000/health`
inside docker-compose, or `curl http://127.0.0.1:8000/health` on the
host for option B / C).

**Frontend hits CORS errors against the WebAPI**
The frontend's `NEXT_PUBLIC_API_URL` defaults to `http://localhost:5000`
which the API CORS allowlist permits. If you've changed the WebAPI's
listen port, update `NEXT_PUBLIC_API_URL` in
`docker-compose.dev.yml`.

**Want a totally fresh DB?**
```
./dev-down.sh --wipe        # drops surrealdb_data volume
./dev-up.sh
```

## Related docs

- [RUNBOOK.md](RUNBOOK.md) — day-to-day operations + recently-shipped log
- [Persistence/SurrealDb/CONVENTION.md](Persistence/SurrealDb/CONVENTION.md) — POCO-as-schema convention
- [packages/Oasis.SurrealDb.Client/Schema/ANNOTATIONS.md](packages/Oasis.SurrealDb.Client/Schema/ANNOTATIONS.md) — attribute reference
- [Persistence/SurrealDb/Migrations/README.md](Persistence/SurrealDb/Migrations/README.md) — data-migration authoring
