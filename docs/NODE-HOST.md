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
- **SurrealDB** — local compose pins **`surrealdb/surrealdb:v3.1.4`**;
  Railway pins that release's multi-arch OCI index as
  **`docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60`**
  (the 1.5.4→3.x cutover, tracked at `surrealdb-major-upgrade`, is closed). **Pin
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
files are skipped). Development and production deliberately use different paths:

- **Development** — the API container entrypoint applies schema automatically.
  Explicit `-Reset` / `--reset` asks the launcher to invoke that same packaged
  CLI; the host does not restore or run a separate .NET tool.
- **Production** — a one-shot `azoa-schema` service runs
  `/usr/local/bin/docker-entrypoint.sh schema` from the exact digest promoted
  for `azoa-api`. It applies schema with owner credentials and provisions the
  database-scoped runtime user. The API requires `AZOA_SKIP_MIGRATIONS=1` and
  refuses owner/schema credentials.

The entrypoint overlays immutable shipped files from
`Persistence/SurrealDb/CompatibilityBaselines/` before running timestamped
forward migrations. This prevents a generated-schema edit from bypassing or
tripping SurrealForge's filename-and-checksum ledger. Never use `--force` for a
normal upgrade.

A fresh SurrealDB needs no out-of-band setup: the runner bootstraps
`DEFINE NAMESPACE/DATABASE IF NOT EXISTS`. Check applied migrations with
`surrealforge migrate status`.

---

## 3. Required environment / config

Production binds the WebAPI from the isolated `SurrealRuntime` config section.
Development may fall back to `SurrealDb`; Production rejects that credential
family so schema-owner authority cannot enter the API process.

| Variable | Required | Notes |
|---|---|---|
| `SurrealRuntime__Endpoint` | yes (prod) | SurrealDB URL, e.g. `http://surrealdb.railway.internal:8000`. |
| `SurrealRuntime__Namespace` | yes | e.g. `azoa`. |
| `SurrealRuntime__Database` | yes | e.g. `azoa`. |
| `SurrealRuntime__User` / `SurrealRuntime__Password` | yes (prod) | Database-scoped runtime `EDITOR` credentials provisioned by `azoa-schema`; never owner/root credentials. |
| `SurrealRuntime__AuthenticationScope` | yes (prod) | Must be `Database`. The named client adds `Surreal-Auth-NS` and `Surreal-Auth-DB` for SurrealDB 3 database-user Basic authentication. Omission remains a Development-only root compatibility path. |
| `SurrealRuntime__G1DurabilityAcknowledged` | yes | Must be `true` outside `IntegrationTest` or the node **refuses to boot** (§6). Operator's ack that the store fsyncs per commit. |
| `AZOA_SKIP_MIGRATIONS` | yes (prod) | Must be `1`; Production API boot never applies schema. |
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` gates Swagger off and enforces the secret guards + G1 ack. |
| `ForwardedHeaders__Enabled` | proxy deploys | Disabled by default. Enable only with explicit `KnownProxies`/`KnownNetworks`, or with both trust-all and edge-only acknowledgement on a platform whose edge is the sole ingress. |
| `ForwardedHeaders__TrustAll` / `ForwardedHeaders__EdgeOnlyDeploymentAcknowledged` | Railway template | Both are `true` in the Railway edge-only template. They are unsafe on a directly reachable self-hosted port. |
| `Jwt__Key` | yes (**SECRET**) | JWT signing key, ≥32 chars. No default — boot fails without it outside Development/IntegrationTest. |
| `Jwt__Issuer` / `Jwt__Audience` | optional | Default `AZOA.WebAPI` / `AZOA.Client`. |
| `NodeOperator__Username` | yes on first boot | Static reserved-operator username, 3–64 canonical lowercase characters. |
| `NodeOperator__Password` | yes on first boot (**SECRET**) | Generated 24–72 byte secret; only its bcrypt hash is persisted. |
| `NodeOperator__CredentialRevision` | yes on first boot | Positive monotonic integer. Increment for every username/password rotation; rollback is refused. |
| `NodeOperator__SessionMinutes` | optional | Dedicated operator-session lifetime, 5–30 minutes (default 20). |
| `AZOA__WalletEncryptionKey` | yes (**SECRET**) | At-rest key for platform wallet generation. No default — `WalletKeyService` throws without it. |
| `AUTOMAPPER_LICENSE_KEY` | yes (hosted prod, **SECRET**) | Registered AutoMapper 15+ commercial or Community key. `LUCKYPENNY_LICENSE_KEY` is accepted as the vendor alias; `/health` is unhealthy in Production when both are absent. |
| `PORT` | injected | On Railway the entrypoint binds `ASPNETCORE_URLS=http://0.0.0.0:$PORT` (falls back to 5000). Do not also pin `ASPNETCORE_URLS`. |

Only the separate schema job receives `SURREALFORGE_*` (`_URL`, `_NS`, `_DB`,
`_USER`, `_PASS`) plus `AZOA_RUNTIME_USER` / `AZOA_RUNTIME_PASSWORD`. Its owner
credentials must not be copied to `azoa-api`.

Production also requires a persistent Data Protection key ring at
`DataProtection__KeyRingPath` (the Railway template mounts `/app/data`). All
replicas that accept the same public cursor must share that ring and application
name.

### Secrets

`Jwt__Key`, `NodeOperator__Password`, `AZOA__WalletEncryptionKey`, `AUTOMAPPER_LICENSE_KEY`,
`SurrealRuntime__Password`, the schema job's `SURREALFORGE_PASS`, and any faucet
mnemonic **must come from a secret store / deploy env, never committed
appsettings**. The base `appsettings.json` ships these as empty placeholders on
purpose — a real deploy MUST replace them before any wallet or seed is created.
For the full per-key list and the secret audit gate, see **`GO-TO-PROD.md` §2**
(read its storage reality through this guide, not its Postgres DSN row).

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

- **Algorand — partial primitives, no launchable bridge.** Ed25519 keygen,
  signing, and selected on-chain operations exist, but the complete reviewed
  lock/mint/burn/release lifecycle and production custody do not. Its bridge
  capability therefore remains fail-closed.
- **Solana / Wormhole / Ethereum — fail-closed, keep disabled.** These value
  routes are also **not production-complete**. Missing work includes a real
  Solana SPL transfer pipeline, Wormhole VAA **sequence parsing**, Ethereum
  **secp256k1** keygen/signing, and reviewed production custody.

**Verify:** with `RealValueEnabled=false`, every real-chain bridge value attempt,
including Algorand, is rejected rather than silently no-op'd. Wormhole requests
remain hard-blocked even if the global switch is accidentally enabled.

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
- `SurrealRuntime__Password` — database-scoped API runtime password, shared by
  reference from the schema job.
- `SURREALFORGE_PASS` — SurrealDB owner password, present only on the one-shot
  schema service.

Required only if you enable the matching feature:

- The selected external **KYC provider metadata and secrets**
  (`Kyc__VeriffApiKey` + `Kyc__VeriffBaseUrl`, or
  `Kyc__Hosted__ProviderName` + `Kyc__Hosted__BaseUrl` +
  `Kyc__Hosted__ApiKey` + `Kyc__Hosted__WebhookSecret`) — keep values in the
  host store and never set an external profile ready until §8.5 is satisfied.
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

The tenant custodial-account capability treats the current AES-GCM key derived
from `AZOA__WalletEncryptionKey` as **development-only**. It reports ready only
with `CustodialAccounts__CustodyMode=DevelopmentOnly`, a `Development` host, and
`Blockchain__Mode=Simulated`. It stores no recovery seed phrase for these
bootstrap wallets. A live/non-development node fails wallet provisioning
closed, while deterministic identity creation and an available KYC provider
remain usable as independent readiness dimensions.

For production custody, implement and provision a **KMS/HSM-backed key store**.
The code exposes a custody seam
(`IKeyCustodyService`) that is the single audited decrypt→sign→zero choke point,
but no production KMS/HSM implementation is registered in this release. Setting
`CustodialAccounts__CustodyMode=KmsHsm` is therefore an explicit unavailable
state, not a feature toggle.

**This is mandatory for real/live value.** Development-only simulated custody
exists solely for local E2E and must not be promoted to a live network.

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
- The chain you are enabling has a complete, independently reviewed real-value
  route. **No chain meets that gate today**; keep `RealValueEnabled=false` until
  the missing provider and custody work in §4.1 lands and passes review.
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

### 8.5 Select KYC deliberately

Base/production configuration uses `Kyc__Provider=unavailable` and a bounded
`Kyc__SubmissionExpiryDays=30`. Development explicitly selects `manual`, the
only implemented provider, with a 30-minute session TTL. It accepts validated
credential-free HTTPS document references and requires an Azoa operator to
approve/reject them. An explicit production `manual` override still fails
closed until private upload, scanning, retention, and review operations exist.

The node control plane stores multiple secret-free provider profiles and a
versioned selection per tenant. A profile key is bound to its installed adapter
key and cannot be repointed after creation. The operator edits display name,
enabled state, policy version, and assurance level at `/operator/providers`;
the API evaluates host configuration separately. Responses expose only
`requiredConfigurationKeys`, `missingConfigurationKeys`, configured booleans,
profile `version`, and `trustRevision`. They never expose API keys or webhook
secrets. A tenant sees and selects only profiles whose readiness is exactly
`READY`.

Profile changes use compare-and-swap (`expectedVersion`) so two operator
browsers cannot silently overwrite one another. Trust-bearing changes advance
`trustRevision`; submissions and approvals remain bound to the tenant
selection, provider, policy, and assurance provenance that produced them.

Provider selection is not sufficient to authorize KYC. Configure the reviewed
trust profile explicitly:

```text
Kyc__ApprovalPolicy__PolicyVersion=<operator-controlled-version>
Kyc__ApprovalPolicy__AssuranceLevel=<reviewed-assurance-label>
Kyc__ApprovalPolicy__TrustedProviderKeys__0=<active-provider-key>
Kyc__ApprovalPolicy__AllowManualInDevelopment=false
```

All four fields fail closed by default. Policy/assurance labels are exact-match;
changing either requires re-verification. `AllowManualInDevelopment=true` is
valid only for a Development host with `Blockchain__Mode=Simulated` and
`Blockchain__Bridge__RealValueEnabled=false`. Manual approval is explicitly
non-authoritative and cannot satisfy a value-operation KYC gate. Approvals
without a future expiry or the current versioned provenance envelope are denied.

External options are explicit but unavailable until reviewed integrations land:

- `Kyc__Provider=veriff` selects the fail-closed Veriff adapter stub.
- `Kyc__Provider=hosted` selects the generic hosted-provider scaffold. Its keys
  are `Kyc__Hosted__ProviderName`, `Kyc__Hosted__BaseUrl`,
  `Kyc__Hosted__ApiKey`, `Kyc__Hosted__WebhookSecret`,
  `Kyc__Hosted__SessionPath`, and `Kyc__Hosted__StatusPath`.
- Any other value is unknown and fails closed; it never falls back to manual.

Both external choices report unavailable even when their config is complete.
They are extension contracts, not working integrations. Do not enable one until
it has durable idempotent attempts, persisted hosted sessions, raw-body webhook
signature verification, event replay protection, attempt/avatar mapping, and
CAS terminal status. Full contract: `TENANT-CUSTODIAL-ONBOARDING.md`.

**Note:** the **mint path is KYC-gated** — an unverified avatar is rejected at
the single mint choke point with no asset created, whether it arrives via the
allocation door or a raw mint call. Whether wallet-generation should also be
gated pre-KYC (a zero-balance wallet before verification) is a deployment policy
decision you make; by default wallet provisioning is allowed pre-KYC.

**Verify:** an unverified, expired, indefinite, or stale-policy avatar is denied
at mint (403); `manual` reports available only in Development; production
manual, `veriff`, `hosted`, and unknown values report unavailable and do not
create a submission.

### 8.6 Onboard the first tenant

The provisioning surface (`api/tenant`) and the step-by-step onboarding runbook
already exist. As the operator you actually execute them for your first tenant:

- Register the first tenant avatar.
- Mint its **tenant-scoped API key** with `tenant:provision`, `wallet:manage`,
  `kyc:read`, and `kyc:submit` by calling JWT-Operator-only
  `POST /api/apikey/tenant` with the existing tenant avatar id, a name, and a
  1–365 day expiry. The raw key is returned once with no-store headers; store it
  as a tenant-side secret. The endpoint accepts no arbitrary scopes or origins.
- Call the create-only `PUT /api/tenant/custodial-accounts/{externalSubject}`
  with a stable `Idempotency-Key`, then persist only the returned public ids and
  readiness fields. A claimed avatar is resolved, never overwritten.

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

**The deploy.** Production uses digest-pinned API and frontend images, a
**separate SurrealDB service**, and an `azoa-schema` one-shot job built from the
same digest as the API. Require the schema job to finish successfully before
promoting the API. It alone receives database-owner credentials; the API runs
with the database-scoped runtime account and refuses boot migrations. The full
Railway procedure is in **`deploy/railway/DEPLOY.md`** and **`RUNBOOK.md` §2**.

**SurrealDB version note.** Match the SurrealDB version in production to the one
your schema was generated and tested against. Both dev/local
(`docker-compose.dev.yml`) uses `surrealdb/surrealdb:v3.1.4`; the Railway prod
service (`RUNBOOK.md` §2) pins the reviewed v3.1.4 OCI index digest. **Verify the
digest still matches the release artifact** before going live; a version
mismatch across environments enforces different namespace/DDL strictness and
is the failure mode to avoid.

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

### 8.9 Seed and operate the reserved node authority

Node authority no longer promotes an ordinary avatar. The API owns one reserved
identity (`a20a0000-0000-4000-8000-000000000001`,
`node-operator@azoa.invalid`) and binds it durably on first launch. Its token is
dedicated to node operations, API-key-forbidden, and never enters ordinary
avatar or tenant login.

Set this complete triplet on the API before the first Production boot:

```text
NodeOperator__Username=node-operator
NodeOperator__Password=<generated 24-72 byte secret>
NodeOperator__CredentialRevision=1
NodeOperator__SessionMinutes=20
```

`SessionMinutes` may be 5–30. The password belongs in the host secret store; the
database receives only a bcrypt hash. A partial seed, invalid credential shape,
or absent first-boot seed refuses Production startup. The Railway template
creates the username, revision, session lifetime, and a generated password slot.
Capture the usable password in the node owner's password manager.

After the serial deployment is healthy, open the frontend at
`/operator/login`. The operator session lasts no longer than 30 minutes and
carries `token_use=node_operator`, the current credential revision,
`operator:admin`, and `node:govern`. It is held only by the frontend's
SameSite=Strict, HttpOnly operator cookie. Do not copy it into browser storage or
use it as an ordinary Bearer/API-key credential.

Use the console in this order:

1. Verify persistence and service readiness on `/operator`.
2. Configure provider credentials in Railway/the host secret store, redeploy,
   and use `/operator/providers` to inspect the exact required/missing variable
   names. The console never reads secret values.
3. Enable and version only a `READY` provider profile. `trustRevision` advances
   when trust-bearing policy changes, making older approval provenance stale.
4. Let each authenticated tenant choose from the ready catalog at `/kyc`, or
   manage assignments from `/operator/tenants`.
5. Work `/operator/reviews` only when the API reports
   `humanReviewAllowed=true`. Manual review is Development simulation, not an
   external-provider override.

**Rotation:** change the static username and/or password, increment
`NodeOperator__CredentialRevision`, and redeploy. The same revision with
different credentials and every revision rollback are refused. A restart may
omit the seed only after the durable binding exists, but keeping the current
secret-backed seed makes configuration drift detectable.

**Session response:** **End operator session** clears only this browser. The
separately confirmed **Revoke all operator sessions** action is for credential
exposure, handoff, or incident response and invalidates every operator session
server-side.

`AdminBootstrap__SeedEmail` / `AdminBootstrap__SeedSecret` remain a legacy
compatibility check only. Do not configure them for a new node. Production
still refuses a half-configured legacy pair so an old deployment error cannot
remain silent.

**Verify:** the reserved credentials reach `/operator`; an ordinary avatar JWT
and an `X-Api-Key` do not. Rotation succeeds only with a higher revision, global
revocation signs out all browsers, and ordinary sign-out leaves other browsers
active.

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
