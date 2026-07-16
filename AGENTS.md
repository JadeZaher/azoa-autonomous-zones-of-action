# AGENTS.md — azoa repeated-ops context

.NET 10 WebAPI (`AZOA.WebAPI.csproj` at root) + Next.js frontend + `@azoa/wallet-sdk`.
**Sole datastore is SurrealDB** (Postgres + EF Core were removed; no more `azoa-postgres`, no more `start.ps1`).
This file is the operational cheat-sheet for build / test / local stack. Keep it accurate.

## Container runtime

The reference dev setup uses **podman** (docker works identically). All commands below use `podman`; substitute `docker` if present.
Containers: `azoa-dev-surrealdb`, `azoa-dev-api`, `azoa-dev-frontend`.
SurrealDB image: `surrealdb/surrealdb:v3.1.4`.

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
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug --filter "FullyQualifiedName~AttributePocoByteEquivalenceTests|FullyQualifiedName~FlowchartRegenerationTests" # Schema/flowchart goldens
```

- Integration tests require dev-up SurrealDB instance.
- **SurrealDB namespace isolation**: Resolved. `AZOATestWebApplicationFactory.cs` isolates namespaces per-class (216 passing). Do not paper over the residual pre-existing failures documented in `INTEGRATION-TEST-PASSOFF.md`.

## Conventions for agents

- **Config-driven**: Load real `appsettings.json` in tests.
- **Source of truth**: SurrealDB sole storage engine; blockchain is the source of truth for balances.
- **Greenfield, pre-launch**: No customers/prod data; prefer clean re-architecture over shims.
- **SurrealDB lookups**: Use `SurrealQuery<T>.Key(...)` / `SelectById`, never a
  scan-style `WHERE id = $id`; a temporary raw record lookup needs the same
  expiring waiver as any other unsupported single statement.
- **Typed Surreal access first**: Use `SurrealQuery<T>`/LINQ for ordinary reads,
  `SurrealWriter` for ordinary creates/upserts, and typed conditional-mutation
  builders for single-record updates/deletes, and the typed relation builder for
  graph edges. DDL belongs in generated schema tooling. Raw `SurrealQuery.Of(...)`
  is reserved for a genuinely multi-table/multi-statement atomic transaction.
  A temporarily unsupported single-statement construct requires a linked,
  expiring SurrealForge issue/track waiver; it is not a permanent exception.
  Every retained raw call needs a terse `// raw: <atomic invariant or waiver>`
  pointer and a directory-level design note. Never hand-write basic CRUD merely
  because it is shorter.
- **SDK & Provider parity**: Keep SDK and .NET providers mirrored; add chains via plugin interfaces (`ChainProvider` / `IBlockchainProvider`, `DexAdapter`).
- **Self-documenting**: Prefer clean code and test helpers over verbose comments.
- **Role-first layout**: Place code by role: `Managers/`, `Services/<domain>/`, `Helpers/`, `Core/` (primitives only), `Providers/`, `Middleware/`, `Interfaces/`. Folder↔namespace strictly mirrored.
- **Exception boundaries**: Expected validation/conflict/not-found outcomes use
  typed results. Unexpected infrastructure/programming exceptions bubble to the
  centralized HTTP/worker boundary so structured logging and OpenTelemetry see
  the original exception once. Catch only to translate a known exception, add
  recovery/retry semantics, or keep a long-running worker alive; rethrow
  cancellation and avoid catch-log-rethrow duplication.
- **Contract docs and helpers**: Interfaces own the XML contract. Implementations
  use `/// <inheritdoc/>` (or `cref` when implementing a differently named
  contract) instead of copying prose. Reusable pure helpers live in `Helpers/`
  or the owning domain package; keep a private static helper beside a class only
  when it is tiny, single-purpose, and not duplicated.
- **Code style**: Unused usings (`IDE0005`) are **build errors** in the app project. Do not commit unused usings.
- **Changed-file pruning gate**: Before handing off any turn, inspect every
  touched file for avoidable raw SurrealQL, catch-all exception swallowing,
  duplicated/private helpers, missing `inheritdoc`, stale comments, and unused
  imports. Remove what the current typed/shared surface can express; record any
  justified escape hatch in the nearest `AGENTS.md` and active conductor track.
- **Bridge safety**: Never weaken exactly-once assertions. See `docs/RESIDUAL-RISK-RUNBOOK.md` for residual risks.
- **DEX env-conditions**: Tinyman/Jupiter pools/unreachability are `Unavailable: true` (200 OK), not red errors.
