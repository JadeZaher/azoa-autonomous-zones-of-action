# Node Host Setup — self-hosting an AZOA node

**Audience:** Operators standing up and running a self-hosted AZOA node in a real
environment.
**Scope:** How the .NET 8 WebAPI process and the SurrealDB instance it owns are
provisioned, configured, and booted. Data-engine reality is **SurrealDB, sole
engine** — this guide supersedes the Postgres/EF sections in `GO-TO-PROD.md` and
`RESIDUAL-RISK-RUNBOOK.md`, which predate the SurrealDB cutover.
**Last updated:** 2026-07-05

---

## 0. What a "node" is here

An AZOA node is two cooperating processes:

- **The WebAPI** — the .NET 8 ASP.NET Core host (`AZOA.WebAPI.dll`). It serves the
  AZOA protocol (identity, quests, holons, swaps, STAR, bridge) over HTTP with
  dual auth (JWT + `X-Api-Key`).
- **A SurrealDB instance it owns** — the sole data engine. The node reads and
  writes durable state (quests, idempotency claims, VAA replay ledger, bridge
  rows) here. **Balances are read from the chain, never stored** — the source of
  truth for value is the chain confirmation / settlement callback, not a row in
  SurrealDB.

One WebAPI talks to one SurrealDB. There is no external message broker and no
second database. Everything else (chain RPC endpoints, a Wormhole Guardian
network, fiat settlement partners) is reached over the network per config.

---

## 1. Prerequisites

- **.NET 8 SDK** (build) / .NET 8 runtime (run). Any .NET 8 host works; the
  supported deploy shapes are local `docker-compose` and Railway (see §7).
- **podman or Docker** — to run SurrealDB (and, for the full local stack, the
  WebAPI + frontend images). The `dev-up` scripts auto-detect `docker compose`
  v2, `docker-compose` v1, `podman-compose`, or `podman compose`.
- **SurrealDB** — pinned image. Local/dev stack:
  `surrealdb/surrealdb:v3.1.4` (`docker-compose.dev.yml`). The Railway prod
  service in `RUNBOOK.md` §2 still references `surrealdb/surrealdb:v1.5.4`; the
  1.5.4→3.x cutover is tracked at `surrealdb-major-upgrade`. **Pin the same
  SurrealDB version your schema was generated and tested against** — a 3.x
  instance answers `/health` but enforces stricter namespace/DDL rules than
  1.5.x, so do not mix.
- **Node 20+** — only if you also run the reference frontend.

---

## 2. Provisioning SurrealDB

### 2.1 Local / dev (the fast path)

`./dev-up.sh` (or `./dev-up.ps1` on Windows) brings up SurrealDB as part of the
full stack via `docker-compose.dev.yml`, applies the generated schema, and starts
the WebAPI + frontend. See `RUNBOOK.md` §1 for every flag; the essentials:

```bash
./dev-up.sh              # build images, preserve the DB volume, apply pending schema
./dev-up.sh --reset-db   # DESTRUCTIVE: wipe the SurrealDB volume, then bring up
./dev-down.sh            # stop, preserve volume
./dev-down.sh --wipe     # stop and drop the surrealdb_data volume
```

Local endpoints after boot (`RUNBOOK.md` §1):

| Service   | URL                     | Notes |
|-----------|-------------------------|-------|
| SurrealDB | http://localhost:8000   | `root` / `root`, persistent volume `surrealdb_data` |
| WebAPI    | http://localhost:5000   | health `/health`, swagger `/swagger/v1/swagger.json` |
| Frontend  | http://localhost:3000   | Next.js reference UI |

Remap the SurrealDB host port with `SURREALDB_HOST_PORT` if 8000 is taken.

### 2.2 Standalone SurrealDB (any host)

To run SurrealDB yourself (managed service, bare container, another host), start
it with a **durable RocksDB store on a persistent volume**, matching the Railway
service definition in `RUNBOOK.md` §2:

```bash
surreal start --user root --pass <pass> --bind 0.0.0.0:8000 rocksdb:///data/db
```

- Mount a persistent volume at `/data` so the store survives restarts.
- RocksDB syncs its WAL per commit (`SURREAL_SYNC_DATA: "true"` in the compose
  env) — this is the durable path that satisfies the **G1 durability gate**
  (see §4). The fsync mode is not introspectable at runtime, so it is a
  deploy-time review item, not a runtime probe (see the boot self-check in §6).

### 2.3 Applying the schema

The node's schema lives generated in `Persistence/SurrealDb/Generated/Schemas/`
plus `Persistence/SurrealDb/Migrations/`. It is applied by the **`surrealforge`
schema CLI**, idempotently, via a `schema_migration` ledger (already-applied
files are skipped). Two paths, both from `RUNBOOK.md`:

- **`dev-up`** runs the host-side schema sync automatically on every run (needs
  `dotnet` on the host; the CLI runs from source). Set `AZOA_SKIP_RESET=1` to
  skip the host-side sync and let the WebAPI container's entrypoint apply it.
- **Container entrypoint** — the production WebAPI image bundles `surrealforge`
  and runs `surrealforge up` on every boot (waits for SurrealDB `/health`,
  applies `Generated/Schemas/` then `Migrations/`, then execs the host). Skip
  with `AZOA_SKIP_MIGRATIONS=1` if a prior deploy step already applied them.

A fresh SurrealDB needs no out-of-band setup: the runner bootstraps
`DEFINE NAMESPACE/DATABASE IF NOT EXISTS`. Check applied migrations with
`surrealforge migrate status`.

---

## 3. Required environment / config

The WebAPI binds its SurrealDB connection from the `SurrealDb` config section
(`Extensions/SurrealDbServiceCollectionExtensions.cs` → `SurrealConnectionOptions`).
Double-underscore env-var form (`SurrealDb__Endpoint`) overrides appsettings.

| Variable | Required | Notes |
|---|---|---|
| `SurrealDb__Endpoint` | yes (prod) | SurrealDB URL, e.g. `http://surrealdb.railway.internal:8000`. Dev default `http://127.0.0.1:8000`. |
| `SurrealDb__Namespace` | yes | e.g. `azoa`. |
| `SurrealDb__Database` | yes | e.g. `azoa`. |
| `SurrealDb__User` / `SurrealDb__Password` | yes (prod) | SurrealDB root credentials. |
| `SurrealDb__G1DurabilityAcknowledged` | yes | Must be `true` outside `IntegrationTest` or the node **refuses to boot** (§6). Operator's ack that the store fsyncs per commit. |
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` gates Swagger off and enforces the secret guards + G1 ack. |
| `Jwt__Key` | yes (**SECRET**) | JWT signing key, ≥32 chars. No default — boot fails without it outside Development/IntegrationTest. |
| `Jwt__Issuer` / `Jwt__Audience` | optional | Default `AZOA.WebAPI` / `AZOA.Client`. |
| `AZOA__WalletEncryptionKey` | yes (**SECRET**) | At-rest key for platform wallet generation. No default — `WalletKeyService` throws without it. |
| `PORT` | injected | On Railway the entrypoint binds `ASPNETCORE_URLS=http://0.0.0.0:$PORT` (falls back to 5000). Do not also pin `ASPNETCORE_URLS`. |

The `SurrealDb__*` family is consumed by **both** the .NET host and the
entrypoint's `surrealforge` pre-step — wire one family. The entrypoint also
accepts `SURREALFORGE_*` aliases (`_URL` / `_NS` / `_DB` / `_USER` / `_PASS`).

### Secrets

`Jwt__Key`, `AZOA__WalletEncryptionKey`, `SurrealDb__Password`, and any faucet
mnemonic **must come from a secret store / deploy env, never committed
appsettings**. The base `appsettings.json` ships these as empty placeholders on
purpose — a real deploy MUST replace them before any wallet or seed is created.
For the full per-key list and the secret audit gate, see **`GO-TO-PROD.md` §2**
(read its `SurrealDb__*` reality through this guide, not its Postgres DSN row).

---

## 4. Choosing a network mode

Blockchain network selection is config-driven (`appsettings.json` →
`Blockchain`):

- `Blockchain:Mode` — `Live` (default). Chain calls hit real RPC endpoints.
- `Blockchain:DefaultChain` — e.g. `Algorand`.
- `Blockchain:DefaultNetwork` — `Devnet` | `Testnet` | `Mainnet`. Each chain has
  a per-network `NodeUrl` / `IndexerUrl` block with an `IsEnabled` flag
  (mainnet ships `IsEnabled: false` until you deliberately enable it).

What each network implies for the **Wormhole bridge trust root**:

- **Devnet** — the "tilt" Guardian set (index `0`, single guardian) is shipped
  and test-verified in `appsettings.Development.json`. Works out of the box; no
  operator action.
- **Testnet / Mainnet** — the Guardian set is **NOT shipped** (absent ⇒
  fail-closed: every VAA rejected). You MUST populate and independently verify
  the real Guardian set per **`GUARDIAN-SET-SETUP.md`** before Wormhole value can
  flow. `RealValueEnabled` / `RequireFullSignatureVerification` and the launch
  gates in `GO-TO-PROD.md` §1 govern when real cross-chain value is allowed.

The faucet (`Blockchain:Faucet:Algorand:Mnemonic`) is a **secret**, testnet-only.

---

## 5. Running the node — single instance vs scale-out

- **Single instance is the supported posture pre-launch.** One WebAPI + one
  SurrealDB is fully correct: the exactly-once guarantees (idempotency claims,
  atomic state transitions, VAA replay ledger) live in SurrealDB, shared by every
  request the instance handles.
- **Rate limiting is in-memory, per instance.** ASP.NET's built-in fixed-window
  limiter keeps its counters in process memory. Two replicas do **not** share a
  quota — a client can spend the limit against each independently. Per
  `GO-TO-PROD.md` §3 / `RESIDUAL-RISK-RUNBOOK.md` §2, horizontal scale-out needs
  a distributed rate-limit store (Redis fixed-window) first. Until then, run one
  instance.
- The **reconciliation background sweep** (`Reconciliation:Enabled=true`) and any
  hosted loops assume a single writer per node. Do not run two nodes against the
  same SurrealDB namespace/database expecting coordinated sweeps.

---

## 6. Health / readiness — what to check post-boot

- **`/health`** — mapped by `Observability/HealthCheckExtensions.cs`
  (`MapHealthChecks("/health", …)`), returns JSON listing each check
  (`storage-db` via `StorageHealthCheck.CanConnectAsync`, plus the provider
  monitor). A healthy node returns `200`. If `storage-db` is Unhealthy, SurrealDB
  is unreachable from the API's perspective (see `RUNBOOK.md` §3).
- **Boot self-check (Program.cs, non-`IntegrationTest`):**
  1. Refuses to boot unless `SurrealDb:G1DurabilityAcknowledged=true` — the
     durability ack described in §2.2 / §3.
  2. Probes SurrealDB through the same `ISurrealExecutor` the app uses
     (`RETURN 1`), proving DI + connection + auth line up. A failure here aborts
     boot with the configured endpoint in the message.
- **Post-boot checklist:**
  - `curl http://<node>/health` → 200, `storage-db` Healthy.
  - `surrealforge migrate status` → schema ledger matches on-disk files.
  - Swagger reachable only in Development / IntegrationTest (gated off in
    Production, by design).
  - Confirm the intended `Blockchain:DefaultNetwork` and, for testnet/mainnet,
    that the Guardian set is present and verified.

---

## 7. Cross-links

- **`RUNBOOK.md`** — local stack control (`dev-up`/`dev-down`), the Railway
  production deploy (WebAPI image + separate SurrealDB service, entrypoint
  migration behavior), and diagnostics.
- **`GO-TO-PROD.md`** — the production-readiness gate checklist and full config /
  secret list (note: its DB sections predate the SurrealDB cutover — use this
  guide for data-engine setup).
- **`GUARDIAN-SET-SETUP.md`** — Wormhole Guardian-set trust-root setup +
  two-source verification, required for testnet/mainnet bridge value flow.
- **`RESIDUAL-RISK-RUNBOOK.md`** — operator runbook: exactly-once guarantees,
  reconciliation, stuck-bridge ops, manual-intervention gates.
- **`DEVELOPMENT.md`** — developer setup, `dev-up` variants,
  bring-your-own-SurrealDB / host-run variants, troubleshooting.
