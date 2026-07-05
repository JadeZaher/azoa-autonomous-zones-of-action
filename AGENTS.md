# AGENTS.md — azoa repeated-ops context

.NET 8 WebAPI (`AZOA.WebAPI.csproj` at root) + Next.js frontend + `@azoa/wallet-sdk`.
**Sole datastore is SurrealDB** (Postgres + EF Core were removed in
`surrealdb-migration`; no more `azoa-postgres`, no more `start.ps1`).
This file is the operational cheat-sheet for build / test / local stack.
Keep it accurate; it is read on every session.

## Container runtime

This machine has **podman** (no docker). All commands below use `podman`;
substitute `docker` if present. The compose stack defines three
containers: `azoa-dev-surrealdb`, `azoa-dev-api`, `azoa-dev-frontend`.
The bundled SurrealDB image is `surrealdb/surrealdb:v1.5.4`.

## Local stack — `dev-up.ps1` / `dev-up.sh`

Full stack = SurrealDB (`:8000`) + .NET API (`:5000`) + Next.js
frontend (`:3000`), brought up via `docker-compose.dev.yml`.

```powershell
.\dev-up.ps1                 # rebuild images + SDK dist, keep volume, apply pending schema
.\dev-up.ps1 -NoBuild        # fast restart, reuse cached images
.\dev-up.ps1 -ResetDb        # DESTRUCTIVE: wipe SurrealDB volume before bringing up
.\dev-up.ps1 -Reset          # keep volume but wipe + reapply schema
.\dev-up.ps1 -Logs           # tail combined logs after startup
.\dev-down.ps1               # stop containers, keep volume
.\dev-down.ps1 -Wipe         # stop + drop SurrealDB volume (alias: -ResetDb)
```

Bash equivalents (`./dev-up.sh`, `./dev-down.sh`) use the same flag
names with `--kebab-case` (e.g. `--reset-db`, `--no-build`).

- Volume preservation is the **default**. `-ResetDb` is the explicit
  opt-in for a clean slate.
- Rebuild is on by default — `-NoBuild` skips.
- Old `-Rebuild` / `-Clean` / `-Preserve` flags are kept as no-op /
  alias forms so older muscle memory doesn't error.
- `dev-up` also rebuilds `sdk/azoa-wallet/dist` on the host so a
  host-mode frontend (rare) sees the same SDK the container does.

## Database — SurrealDB

- Storage URI: `rocksdb:///data/db` (G1 durability — RocksDB syncs its
  WAL per commit). The original `surrealkv:///data/db?sync=every`
  config crashed because `surrealdb/surrealdb:v1.5.4` ships **without**
  the `surrealkv` feature flag. `surrealkv` default-on starts in 2.x;
  major upgrade tracked separately at
  [`surrealdb-major-upgrade`](conductor/tracks/surrealdb-major-upgrade/spec.md).
- Connection: `SurrealDb:Endpoint=http://surrealdb:8000` (from-API),
  or `http://localhost:8000` (from host). Namespace `azoa`, database
  `azoa`, user `root`, password `root` — single-underscore env-var
  aliases `SURREALFORGE_NS` / `_DB` / `_USER` / `_PASS` get the same
  values via the compose file.
- Schema lives in [Persistence/SurrealDb/Generated/Schemas/](Persistence/SurrealDb/Generated/Schemas/)
  (26 `.surql` files, emitted from decorated POCOs by
  [https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/)).
- Schema sync is **idempotent** — `surrealforge up` runs from the API
  container's entrypoint on every boot AND from the host as the last
  dev-up step. The `schema_migration` ledger skips already-applied
  files. `-Reset` / `--reset` wipes the namespace and re-applies.
- Healthcheck uses `/surreal isready --conn http://localhost:8000`
  (the image is distroless — no curl/wget/nc available inside).
- Host port 8000 collision: if a different SurrealDB is already
  running on `127.0.0.1:8000`, dev-up auto-detects it, skips the
  bundled service, and points the API at the host via
  `host.containers.internal:8000` (podman) / `host.docker.internal`
  (docker).
- `podman exec -it azoa-dev-surrealdb /surreal sql --conn http://localhost:8000 --user root --pass root --ns azoa --db azoa`
  to drop into a REPL.

## Build

```powershell
dotnet build AZOA.WebAPI.csproj -c Debug          # production API only (fast gate)
dotnet build azoa.sln    -c Debug           # whole solution (incl. test projects)
```

- Green = **0 errors**. There are ~18 pre-existing baseline warnings
  (SolanaProvider nullability, SearchManager, a CS1998) — not
  regressions; do not chase them. Adding NEW warnings is a regression.
- Do **not** run the frontend typecheck — it is known pre-existing
  noise. The gates are `dotnet build` + SDK `tsc` only. See memory:
  [`no-frontend-typecheck`](C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/no-frontend-typecheck.md).
- No `dotnet ef` migrations on new work — EF was removed in
  surrealdb-migration. Schema changes are now decorated-POCO edits +
  re-emitting `Persistence/SurrealDb/Generated/Schemas/*.surql` via
  [SurrealForge.Schema](https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Schema/).

## Test

```powershell
# Unit suite (fast gate, no external deps): ~500 tests, ~10s
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug

# One class / filter
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj -c Debug `
  --filter "FullyQualifiedName~CrossChainBridgeServiceTests"

# Integration tests (need SurrealDB up)
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj -c Debug

# Schema package
dotnet test tests/SurrealForge.Schema.Tests/SurrealForge.Schema.Tests.csproj -c Debug
```

- Integration tests use the persistent SurrealDB instance the
  `dev-up` stack already brings up. Bring it up first, then run.
- Unit suite is the authoritative regression gate; integration suites
  are higher-fidelity but slower.
- **SurrealDB namespace isolation: RESOLVED** (2026-06-27). The factory
  (`tests/AZOA.WebAPI.IntegrationTests/Factories/AZOATestWebApplicationFactory.cs`)
  pins `SurrealDb:Namespace/Database` to a per-CLASS namespace so the app
  and the schema-applied namespace match (the app was connecting to `azoa`
  while the harness seeded into `test{guid}`). Integration suite is now
  **216 passing / 0 skipped**; the residual ~37 are pre-existing and triaged
  in `tests/AZOA.WebAPI.IntegrationTests/INTEGRATION-TEST-PASSOFF.md`
  (test-design IDOR conflicts, env/repo-layout drift, socket races) — do not
  paper these over.

## Conventions for agents

- **Config-driven over hardcoded.** Tests load real
  `appsettings.json`. See memory: [`config-driven-calls`](C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/config-driven-calls.md).
- **SurrealDB sole storage engine, chain = source of truth for balances.**
  See memory: [`data-engine-decision`](C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/data-engine-decision.md).
- **Greenfield, pre-launch.** No customers, no production data. Prefer
  clean re-architecture over compat/migration shims. Memory:
  [`greenfield-prelaunch-no-compat`](C:/Users/atooz/.claude/projects/c--Users-atooz-Programming-Projects-azoa/memory/greenfield-prelaunch-no-compat.md).
- **SurrealDB record lookups** use `SELECT * FROM type::record($_t, $_id)`,
  NOT `WHERE id = $id` (Thing vs string equality fails silently in
  1.5.x). See [SurrealApiKeyStore](Providers/Stores/Surreal/SurrealApiKeyStore.cs)
  for the canonical pattern.
- **SDK and .NET providers stay mirrored;** new chains via the plugin
  interfaces (`ChainProvider` / `IBlockchainProvider`, `DexAdapter`).
  See [PROVIDERS.md](PROVIDERS.md).
- **Self-documenting code over verbose comments.** Test helpers over
  narrative.
- **Role/pattern-first layout (2026-06-27).** Place new code by ROLE, domain
  second: `Managers/` = orchestrators only; `Services/<domain>/` = services /
  gates / emitters / adapters / workers; `Helpers/` = reusable static utilities
  (`AZOA.WebAPI.Helpers`, global-using'd); `Core/` = primitives ONLY (enums,
  value-types, constants, `Idempotency/`, base classes — no behavior classes);
  `Providers/`, `Middleware/`, `Interfaces/` as named. Folder↔namespace is
  strictly mirrored — moving a file means updating its `namespace`. Gotchas:
  `Services/Sagas/` deliberately keeps ns `AZOA.WebAPI.Sagas`; `Program.cs`
  scans Quest handlers by namespace STRING (don't move that folder blindly);
  the `Providers/Blockchain/Algorand` ns collides with the Algorand SDK root
  (use `global::Algorand.*`).
- **Code style is enforced** via the root `.editorconfig` +
  `EnforceCodeStyleInBuild` (Directory.Build.props). The high-value rules are
  `warning` (unused usings IDE0005, unused private members IDE0051/0052,
  interface `I`-prefix); stylistic rules (`var`, expression bodies, file-scoped
  namespaces) are `suggestion` — don't churn the tree converting them. The app
  project (`AZOA.WebAPI.csproj`) promotes **IDE0005 to a build ERROR** (it is
  clean), so do NOT commit unused usings in app code. Don't run a blanket
  `dotnet format` — it rewrites whitespace/EOL across dozens of files; use
  `dotnet format analyzers --diagnostics IDE0005` or remove the flagged line.
- **Bridge moves real value** — never weaken an exactly-once / replay
  assertion to make a test pass; fix the cause. Pre-launch safety
  surface + ops runbook: `docs/RESIDUAL-RISK-RUNBOOK.md`.
- **DEX env-conditions are 200 OK with `Unavailable: true`** — no-pool
  on Tinyman, upstream unreachable on Jupiter. Frontend tests
  render these as expected, not as red failures.
