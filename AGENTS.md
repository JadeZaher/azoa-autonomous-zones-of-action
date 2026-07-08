# AGENTS.md — azoa repeated-ops context

.NET 10 WebAPI (`AZOA.WebAPI.csproj` at root) + Next.js frontend + `@azoa/wallet-sdk`.
**Sole datastore is SurrealDB** (Postgres + EF Core were removed; no more `azoa-postgres`, no more `start.ps1`).
This file is the operational cheat-sheet for build / test / local stack. Keep it accurate.

## Container runtime

The reference dev setup uses **podman** (docker works identically). All commands below use `podman`; substitute `docker` if present.
Containers: `azoa-dev-surrealdb`, `azoa-dev-api`, `azoa-dev-frontend`.
SurrealDB image: `surrealdb/surrealdb:v1.5.4`.

## Local stack — `dev-up.ps1` / `dev-up.sh`

Full stack = SurrealDB (`:8000`) + .NET API (`:5000`) + Next.js frontend (`:3000`) via `docker-compose.dev.yml`.

```powershell
.\dev-up.ps1                 # rebuild images + SDK dist, keep volume, apply pending schema
.\dev-up.ps1 -NoBuild        # fast restart, reuse cached images
.\dev-up.ps1 -ResetDb        # DESTRUCTIVE: wipe SurrealDB volume before bringing up
.\dev-up.ps1 -Reset          # keep volume but wipe + reapply schema
.\dev-up.ps1 -Logs           # tail combined logs after startup
.\dev-down.ps1               # stop containers, keep volume
.\dev-down.ps1 -Wipe         # stop + drop SurrealDB volume (alias: -ResetDb)
```

Bash equivalents (`./dev-up.sh`, `./dev-down.sh`) use same flags in `--kebab-case` (e.g. `--reset-db`, `--no-build`).

- Volume preservation is the default; `-ResetDb` is explicit opt-in.
- Rebuild is on by default; `-NoBuild` skips it.
- `dev-up` also rebuilds `sdk/azoa-wallet/dist` on the host.

## Database — SurrealDB

- Storage URI: `rocksdb:///data/db` (G1 durability).
- Connection: `SurrealDb:Endpoint=http://surrealdb:8000` (from-API), namespace `azoa`, database `azoa`, user/pass `root`/`root`.
- Schema: lives in `Persistence/SurrealDb/Generated/Schemas/` (26 `.surql` files emitted from C# POCOs).
- Schema sync: idempotent; runs from API entrypoint on boot and from host via `surrealforge up`.
- Healthcheck: `/surreal isready --conn http://localhost:8000`.
- REPL: `podman exec -it azoa-dev-surrealdb /surreal sql --conn http://localhost:8000 --user root --pass root --ns azoa --db azoa`

## Build

```powershell
dotnet build AZOA.WebAPI.csproj -c Debug          # production API only (fast gate)
dotnet build azoa.sln    -c Debug           # whole solution (incl. test projects)
```

- Green = 0 errors. Baseline warnings = 25 (reserved crypto/value files and MSBuild notes). Do not chase them; adding new warnings is a regression.
- Do not run frontend typecheck (known pre-existing noise). Gates are `dotnet build` + SDK `tsc` only.
- No EF Core migrations. Schema changes are POCO edits + re-emitting `.surql` via `SurrealForge.Schema`.

## Test

```powershell
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug # Unit suite
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug --filter "FullyQualifiedName~CrossChainBridgeServiceTests"
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj -c Debug # Integration
dotnet test tests/SurrealForge.Schema.Tests/SurrealForge.Schema.Tests.csproj -c Debug # Schema
```

- Integration tests require dev-up SurrealDB instance.
- **SurrealDB namespace isolation**: Resolved. `AZOATestWebApplicationFactory.cs` isolates namespaces per-class (216 passing). Do not paper over the residual pre-existing failures documented in `INTEGRATION-TEST-PASSOFF.md`.

## Conventions for agents

- **Config-driven**: Load real `appsettings.json` in tests.
- **Source of truth**: SurrealDB sole storage engine; blockchain is the source of truth for balances.
- **Greenfield, pre-launch**: No customers/prod data; prefer clean re-architecture over shims.
- **SurrealDB lookups**: Use `SELECT * FROM type::record($_t, $_id)` (or `SelectById`), NOT `WHERE id = $id`.
- **SDK & Provider parity**: Keep SDK and .NET providers mirrored; add chains via plugin interfaces (`ChainProvider` / `IBlockchainProvider`, `DexAdapter`).
- **Self-documenting**: Prefer clean code and test helpers over verbose comments.
- **Role-first layout**: Place code by role: `Managers/`, `Services/<domain>/`, `Helpers/`, `Core/` (primitives only), `Providers/`, `Middleware/`, `Interfaces/`. Folder↔namespace strictly mirrored.
- **Code style**: Unused usings (`IDE0005`) are **build errors** in the app project. Do not commit unused usings.
- **Bridge safety**: Never weaken exactly-once assertions. See `docs/RESIDUAL-RISK-RUNBOOK.md` for residual risks.
- **DEX env-conditions**: Tinyman/Jupiter pools/unreachability are `Unavailable: true` (200 OK), not red errors.
