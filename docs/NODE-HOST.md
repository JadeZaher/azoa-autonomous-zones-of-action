# Node Host Setup — self-hosting an AZOA node

**Audience:** Operators standing up and running a self-hosted AZOA node in a real
environment.
**Scope:** How the .NET 10 WebAPI process and the SurrealDB instance it owns are
provisioned, configured, and booted. Data-engine reality is **SurrealDB, sole
engine** — this guide supersedes the Postgres/EF sections in `GO-TO-PROD.md` and
`RESIDUAL-RISK-RUNBOOK.md`, which predate the SurrealDB cutover.
**Last updated:** 2026-07-05

---

## 0. What a "node" is here

An AZOA node is two cooperating processes:

- **The WebAPI** — the .NET 10 ASP.NET Core host (`AZOA.WebAPI.dll`). It serves the
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

- **.NET 10 SDK** (build) / .NET 10 runtime (run). Any .NET 10 host works; the
  supported deploy shapes are local `docker-compose` and Railway (see §8.7).
- **podman or Docker** — to run SurrealDB (and, for the full local stack, the
  WebAPI + frontend images). The `dev-up` scripts auto-detect `docker compose`
  v2, `docker-compose` v1, `podman-compose`, or `podman compose`.
- **SurrealDB** — pinned image, **`surrealdb/surrealdb:v3.1.4`** everywhere
  (local `docker-compose.dev.yml` and the Railway prod service alike — the
  1.5.4→3.x cutover, tracked at `surrealdb-major-upgrade`, is closed). **Pin
  the same SurrealDB version your schema was generated and tested against.**
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
| `ForwardedHeaders__Enabled` | proxy deploys | Disabled by default. Enable only with explicit `KnownProxies`/`KnownNetworks`, or with both trust-all and edge-only acknowledgement on a platform whose edge is the sole ingress. |
| `ForwardedHeaders__TrustAll` / `ForwardedHeaders__EdgeOnlyDeploymentAcknowledged` | Railway template | Both are `true` in the Railway edge-only template. They are unsafe on a directly reachable self-hosted port. |
| `Jwt__Key` | yes (**SECRET**) | JWT signing key, ≥32 chars. No default — boot fails without it outside Development/IntegrationTest. |
| `Jwt__Issuer` / `Jwt__Audience` | optional | Default `AZOA.WebAPI` / `AZOA.Client`. |
| `AZOA__WalletEncryptionKey` | yes (**SECRET**) | At-rest key for platform wallet generation. No default — `WalletKeyService` throws without it. |
| `PORT` | injected | On Railway the entrypoint binds `ASPNETCORE_URLS=http://0.0.0.0:$PORT` (falls back to 5000). Do not also pin `ASPNETCORE_URLS`. |

The `SurrealDb__*` family is consumed by **both** the .NET host and the
entrypoint's `surrealforge` pre-step — wire one family. The entrypoint also
accepts `SURREALFORGE_*` aliases (`_URL` / `_NS` / `_DB` / `_USER` / `_PASS`).

Production also requires a persistent Data Protection key ring at
`DataProtection__KeyRingPath` (the Railway template mounts `/app/data`). All
replicas that accept the same public cursor must share that ring and application
name.

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

### 4.1 Which value routes are real (read before enabling any value flow)

Not every chain's value path is production-real yet. The node is built so the
unfinished ones are **fail-closed** — they refuse rather than move value badly —
but an operator MUST know which is which before flipping anything on:

- **Algorand — real.** Real Ed25519 keygen, real server-side signing, and real
  on-chain lock/burn/transfer/mint run end-to-end through the custodial signer.
  This is the supported real-value chain.
- **Solana / Wormhole / Ethereum — fail-closed, keep disabled.** These value
  routes are **not production-complete** and MUST stay off
  (`Blockchain__Bridge__RealValueEnabled=false`) until their follow-ups land:
  a real Solana SPL transfer pipeline, real Wormhole VAA **sequence parsing**,
  and real Ethereum **secp256k1** keygen/signing. Until then these paths
  deliberately refuse to move value; do **not** set `RealValueEnabled=true` or
  enable a non-Algorand mainnet chain expecting cross-chain value to flow.

**Verify:** with `RealValueEnabled=false`, a Solana/Wormhole/ETH value attempt is
rejected (fail-closed) rather than silently no-op'd; only Algorand real value
transacts.

**Forward-compat residual (record before you make quests shareable).** Value-node
actor derivation currently reads the **quest definition** owner (`quest.AvatarId`).
That is correct only while a run's actor equals the quest owner. **If quests ever
become shareable/public** (a run driven by someone other than the quest author),
the value-node actor MUST switch from `quest.AvatarId` to the **run** owner
(`run.AvatarId`) so value acts as the run's driver, not the template's author.
This is a launch-safe residual today (quests are not shareable) — flag it for the
change that makes them so.

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

## 8. Going to production — operator checklist

AZOA is built so the **safe path is the automatic one**: fail-closed defaults,
boot guards that refuse to start on a missing secret, and a trust root that
rejects everything until you supply and verify it. What is left for launch is
therefore not code — it is the small set of **explicit config, secret, and
trust-root decisions that only the operator can own**. The code track can make
the safe path automatic; it cannot decide *your* KMS provider, *your* Guardian
addresses, or *when* real value is allowed to flow. Those are yours. This
section is the checklist for making them — each item is what you DO, why it
matters, and how you confirm it took.

The full production-readiness gate lives in **`GO-TO-PROD.md`**; this section is
the node-operator view of the same finish line and does not duplicate its
sign-off table.

### 8.1 Provision the secrets (the load-bearing step)

Supply every secret from your secret store / deploy env — **never** committed
appsettings. The base `appsettings.json` ships these as empty placeholders on
purpose. Always required:

- `Jwt__Key` — JWT signing key, ≥32 chars, random and rotated.
- `AZOA__WalletEncryptionKey` — at-rest key material for platform wallet
  generation.
- `SurrealDb__Password` — SurrealDB root password (must match the value the
  SurrealDB service starts with).

Required only if you enable the matching feature:

- The **KYC provider secret** (`Kyc__VeriffApiKey` + base URL / webhook secret)
  — only when automated KYC is turned on (§8.5).
- The **platform signing mnemonic** (`Blockchain__Faucet__Algorand__Mnemonic`,
  and any custodial platform-account seed) — testnet-only for the faucet; do not
  fund on mainnet unless deliberate.

**Why it's fail-safe:** `Program.cs` guards these. Outside
Development/IntegrationTest the node **refuses to boot** without `Jwt__Key`, and
`WalletKeyService` throws without `AZOA__WalletEncryptionKey` — a deploy that
forgot a secret fails loudly at startup, never silently with a weak default.

**Verify:** the node boots clean with the real secrets set, and a config audit
confirms none of these values live in a committed file. For the complete
per-key list and the audit gate, see **`GO-TO-PROD.md` §2** (read its DB row as
the `SurrealDb__*` reality of this guide, not its legacy Postgres DSN).

### 8.2 Choose your key custody (recommended for real value)

By default, wallet keys are AES-GCM encrypted under a data key **derived from a
config secret** (`AZOA__WalletEncryptionKey`). That is fine for a beta / internal
cut, but a config-derived secret is **not production-grade custody for
value-bearing keys**.

For production custody, provision a **KMS/HSM-backed key store** and wire your
KMS provider in at deploy time. The code exposes a custody seam
(`IKeyCustodyService`) that is the single audited decrypt→sign→zero choke point,
so a KMS-backed implementation drops in **without touching the signing path** —
your KMS provider slots in behind the same interface.

**This is a recommended-for-real-value step, not mandatory for a beta/internal
cut.** If you are moving real value, do it before flipping mainnet (§8.3).

**Verify:** with the KMS store wired, no value-bearing private key is
recoverable from app config alone.

**Key-rotation pending-key marker (persist it on a volume).** Live key rotation
writes a **pending-key marker** (the re-wrap recovery marker) via a file store
at `AZOA:Rotation:PendingKeyFilePath`. On an ephemeral container (Railway and
most PaaS), point that path at a **mounted persistent volume** — otherwise a
crash mid-rotation loses the recovery marker and the rotation cannot resume
cleanly. Give the file **restrictive permissions**: it is a key-confirmation
oracle (though AES-GCM makes offline guessing infeasible, treat it as sensitive).

**Verify:** `AZOA:Rotation:PendingKeyFilePath` resolves to a mounted volume path
(not container-local scratch) and the file is not world-readable.

### 8.3 Sign the mainnet enablement gate before flipping any chain to real value

Before you set a chain's `Mainnet.IsEnabled=true` or `Blockchain__Bridge__RealValueEnabled=true`,
sign off a documented checklist. Do **not** flip these until every box is true:

- Real on-chain signing has been verified on testnet (a real transaction signs,
  broadcasts, and confirms).
- The chain you are enabling has a **real** value route. Today only **Algorand**
  does; **Solana / Wormhole / Ethereum value routes are fail-closed and must stay
  disabled** (`RealValueEnabled=false`) until their follow-ups land (§4.1). Do not
  flip `RealValueEnabled=true` for a non-Algorand chain.
- Production key custody is in place (§8.2).
- Guardian sets for the target network are provisioned **and** independently
  verified (§8.7 / `GUARDIAN-SET-SETUP.md`).
- A security review of the value path has been signed off.

**Why:** flipping to mainnet early moves real value over paths that may not yet
be fully signed, custodied, or trust-rooted. The gate is the deliberate stop.
The hard launch gates and the sign-off table are in **`GO-TO-PROD.md` §1**;
Guardian-set verification is **`GUARDIAN-SET-SETUP.md`**.

### 8.4 Fund the platform account and alert on low balance

A custodial signer needs native gas (**ALGO** on Algorand) to pay transaction
fees. This is pure ops:

- Provision the platform account and **fund it** with enough native token to
  cover expected fee volume.
- Set up **low-balance alerting** so the account is topped up before it runs
  dry — a drained fee account stalls every custodial write.

**Verify:** the account holds a working balance and an alert fires on a test
threshold.

### 8.5 Enable KYC (only if you need automated verification)

The default is **manual admin-review KYC**, which needs no secrets — leave
`Kyc:Provider=manual` for a beta cut. To enable **automated KYC (Veriff)**:

- Set `Kyc__Provider=veriff`.
- Supply `Kyc__VeriffApiKey`, the provider base URL, and the webhook signing
  secret from the secret store (empty placeholders in the `Kyc` section of
  `appsettings.json` — never commit real values).

**Note:** the **mint path is KYC-gated** — an unverified avatar is rejected at
the single mint choke point with no asset created, whether it arrives via the
allocation door or a raw mint call. Whether wallet-generation should also be
gated pre-KYC (a zero-balance wallet before verification) is a deployment policy
decision you make; by default wallet provisioning is allowed pre-KYC.

**Verify:** an unverified avatar is denied at mint (403); with `veriff`
configured, a verification session round-trips against the provider.

### 8.6 Onboard the first tenant

The provisioning surface (`api/tenant`) and the step-by-step onboarding runbook
already exist. As the operator you actually execute them for your first tenant:

- Register the first tenant avatar.
- Mint its **tenant-scoped API key** and provision that key as an env secret for
  the tenant (e.g. the tenant authenticates its allocation calls with this key —
  it carries the tenant's mint/manage scope; never commit it).
- Populate the tenant's **user→avatar mapping** so external user ids resolve to
  AZOA avatars.

Follow the generic onboarding steps: register tenant → mint tenant-scoped key →
provision children → issue child credential → resolve by external id.

**Verify:** the tenant's key authenticates, and an external user id resolves to
the expected avatar.

### 8.7 Provision Guardian sets and run the Railway deploy

**Guardian sets.** For testnet/mainnet, the Wormhole Guardian set is the bridge
**trust root** and is **not shipped** — absent ⇒ every VAA is rejected
(fail-closed). Retrieve the ordered Guardian address list, verify it byte-for-byte
across at least two independent authoritative sources, drop it into the
per-environment appsettings under `Blockchain:Wormhole:GuardianSets`, and sign
the verification checklist. Full procedure: **`GUARDIAN-SET-SETUP.md`**.

**The deploy.** Production runs as a WebAPI image (bundling the `surrealforge`
schema CLI) plus a **separate SurrealDB service**; the container entrypoint waits
for SurrealDB `/health`, applies schema + migrations idempotently, then execs the
host. The full Railway procedure — required env vars, entrypoint behavior, and
the SurrealDB service definition — is in **`RUNBOOK.md` §2**.

**SurrealDB version note.** Match the SurrealDB version in production to the one
your schema was generated and tested against. Both dev/local
(`docker-compose.dev.yml`) and the Railway prod service (`RUNBOOK.md` §2) are
pinned to `surrealdb/surrealdb:v3.1.4` — **verify your deploy's Railway service
config still matches** before going live; a version mismatch across environments
enforces different namespace/DDL strictness and is the failure mode to avoid.

**Verify:** `/health` returns 200 with `storage-db` Healthy, `surrealforge
migrate status` matches the on-disk files, and for testnet/mainnet the Guardian
set is present and its checklist signed.

### 8.8 Set up the brand-boundary CI guard

AZOA must not leak third-party-tenant brand strings into its own surface. A CI
check enforces this boundary (a "no third-party brand strings" grep, promoted to
a required pipeline stage). As the CI owner, wire this check into the pipeline so
the brand boundary cannot silently regress.

**Verify:** the check runs as a required stage and fails the build on a
deliberately introduced brand string.

### 8.9 Mint your first operator-admin principal

Operator-only endpoints are guarded by an **Operator policy** that accepts an
`operator:admin` scope (or the legacy `role=Admin`/`is_admin` claim — see below).
That scope is **API-key-forbidden by design** — an `X-Api-Key` principal can
never carry it, so a leaked tenant/API key cannot reach operator surface. On a
fresh deploy there is no admin yet, so it must be **bootstrapped once** via the
seed mechanism below (`Services/Admin/AGENTS.md` has the full design rationale).

**How it works.** `AvatarManager` stamps `operator:admin` (+ the legacy
`role=Admin` claim) onto a JWT at login time, but ONLY for the one avatar named
by two env vars — both required together, fail-closed if only one is set:

- `AdminBootstrap__SeedEmail` — the email of the avatar to promote.
- `AdminBootstrap__SeedSecret` — a shared secret proving you (the operator, with
  deploy/config access) intend to arm the bootstrap. Not sent in any request —
  it only needs to be present in the running process's config.

**Step-by-step (executable as written):**

1. **Register a normal account first** (`POST /api/avatar/register`) with the
   email you want to promote — e.g. `ops@yourorg.example`. This step can happen
   before or after step 2.
2. **Set both env vars** on the host, then (re)start the API:
   ```
   AdminBootstrap__SeedEmail=ops@yourorg.example
   AdminBootstrap__SeedSecret=<any random string you generate — e.g. `openssl rand -hex 24`>
   ```
   At boot, `SeedAdminHostedService` logs `Admin bootstrap is ARMED for seed
   email ...` if both vars are set consistently. If only one is set, it logs a
   warning in Dev/IntegrationTest — **and throws at startup in Production**
   (fail-closed: a half-configured bootstrap must not boot silently).
3. **Log in as that avatar** (`POST /api/avatar/login`). The returned JWT now
   carries `scope=operator:admin` (and `role=Admin`) — use it as your Bearer
   token against the operator endpoints.
4. **Unset both env vars and restart** once you've minted your first admin (or
   any subsequent admins you need). The bootstrap seam persists nothing to the
   database — with the env vars gone, no JWT is ever stamped again until you
   re-arm it, which is the intended one-shot shape.

**Interim (unchanged, still safe):** the legacy `role=Admin` / `is_admin` claim
path keeps working independently of the above — it was never removed. It is
safe because only a **validly-signed admin JWT** reaches it — there is no
API-key or unauthenticated bypass.

**Verify:** a token minted via the seed path (or carrying `operator:admin`/
`role=Admin` some other way) reaches the operator endpoints; the same request
bearing an `X-Api-Key` (no JWT) is rejected; booting with only one of the two
env vars set in a Production environment refuses to start.

---

## 9. Cross-links

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
