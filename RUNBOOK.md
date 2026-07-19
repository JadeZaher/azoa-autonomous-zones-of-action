# AZOA — Runbook

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

Ordinary schema sync runs inside the API container from the exact
`SurrealForge.Schema` package restored into that image. `--reset` is the only
launcher path that suppresses ordinary `up` while it performs the explicit
destructive reset. See [DEVELOPMENT.md](DEVELOPMENT.md) for host-run and
bring-your-own-SurrealDB variants.

---

## 2. Production deploy (Railway)

Production uses digest-pinned API and frontend images plus a separate SurrealDB
service. A one-shot `azoa-schema` service runs from the same attested image
digest as the API before every API rollout.

### WebAPI service — required environment variables

| Variable | Required | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | yes | Set to `Production`. Gates Swagger off and enforces the G1 durability ack below. |
| `ForwardedHeaders__Enabled` | Railway | `true` only when an edge proxy is present; direct/self-hosted nodes leave it false or configure explicit trusted proxy IPs/networks. |
| `ForwardedHeaders__TrustAll` / `ForwardedHeaders__EdgeOnlyDeploymentAcknowledged` | Railway | Both `true` for the Railway edge-only template. Never use this pair when the application port is directly reachable; configure `KnownProxies`/`KnownNetworks` instead. |
| `Jwt__Key` | yes | JWT signing key, ≥32 chars. No default — boot fails without it. |
| `Jwt__Issuer` / `Jwt__Audience` | optional | Default `AZOA.WebAPI` / `AZOA.Client`. |
| `NodeOperator__Username` | yes on first boot | Static node-operator username, 3–64 lowercase letters/digits plus `.`, `_`, or `-`. The Railway template uses `node-operator`. |
| `NodeOperator__Password` | yes on first boot (**SECRET**) | Generated 24–72 byte password. Store it in the host secret manager; the API persists only a bcrypt hash. |
| `NodeOperator__CredentialRevision` | yes on first boot | Positive monotonic integer. Start at `1` and increment whenever username or password changes; rollback and same-revision drift refuse startup. |
| `NodeOperator__SessionMinutes` | optional | Dedicated operator JWT lifetime, 5–30 minutes; default/template value is `20`. |
| `AZOA__WalletEncryptionKey` | yes | Symmetric key for platform wallet generation. No default — `WalletKeyService` throws without it. |
| `AUTOMAPPER_LICENSE_KEY` | yes (hosted prod) | Registered AutoMapper 15+ key. The vendor alias `LUCKYPENNY_LICENSE_KEY` is also accepted; `/health` is unhealthy in Production when neither is set. |
| `SurrealRuntime__Endpoint` | yes | URL of the SurrealDB service (e.g. `http://surrealdb.railway.internal:8000`). |
| `SurrealRuntime__Namespace` | yes | e.g. `azoa`. |
| `SurrealRuntime__Database` | yes | e.g. `azoa`. |
| `SurrealRuntime__User` | yes | Database-scoped non-root user provisioned by `azoa-schema`. |
| `SurrealRuntime__Password` | yes | Runtime password shared by reference from `azoa-schema`; never the owner password. |
| `SurrealRuntime__G1DurabilityAcknowledged` | yes | Must be `true`. Outside `IntegrationTest`, `Program.cs` refuses to boot unless this is set — it is the operator's acknowledgement that the SurrealDB storage URI runs with per-commit WAL sync (see §2 durability note). |
| `AZOA_SKIP_MIGRATIONS` | yes | Must be `1`; Production refuses API-boot migrations. |
| `Kyc__Provider` | yes | Keep `unavailable` until a reviewed provider is configured. `manual` is Development-only. |
| `Kyc__VeriffApiKey` / `Kyc__VeriffBaseUrl` | provider-specific | Host-managed Veriff slots. The current adapter remains fail-closed until its real lifecycle integration is implemented. |
| `Kyc__Hosted__ProviderName` / `BaseUrl` / `ApiKey` / `WebhookSecret` | provider-specific | Generic hosted-adapter metadata and secrets. HTTPS origin and both secrets are required; the current adapter remains a fail-closed scaffold. |
| `Kyc__ApprovalPolicy__PolicyVersion` / `AssuranceLevel` | provider-specific | Exact versioned trust labels. Policy changes make older approval provenance stale. |
| `PORT` | injected | Railway injects `$PORT`; the entrypoint binds `ASPNETCORE_URLS=http://0.0.0.0:$PORT` (falls back to 5000). Do NOT also pin `ASPNETCORE_URLS` to a fixed port — let the entrypoint honor `$PORT`. |

Never put `SurrealDb__User`, `SurrealDb__Password`, or `SURREALFORGE_*` on the
Production API. The startup guard rejects legacy owner credentials.

Mount a persistent volume at `/app/data` for the Data Protection cursor key
ring. A multi-replica deployment must use one shared key ring and the same
`DataProtection__ApplicationName`; otherwise opaque public cursors fail across
restarts or replicas.

### Node-operator and KYC control-plane bring-up

1. Before the first API boot, set the complete `NodeOperator__Username`,
   `NodeOperator__Password`, and `NodeOperator__CredentialRevision=1` triplet.
   The Railway blueprint creates a strong password slot; place its usable value
   in the node owner's password manager before relying on the console.
2. Complete the serial rollout and wait for the schema job, API `/health`, and
   frontend `/api/health` gates. Open the frontend's `/operator/login`; do not
   use ordinary avatar login for node authority.
3. Confirm the overview reports durable persistence ready. Provider credentials
   stay in the API service's secret store. The Providers page displays only
   `requiredConfigurationKeys`, `missingConfigurationKeys`, configured flags,
   and the profile/trust revisions—never secret values.
4. Add the exact provider variables to the host and redeploy the API. Then use
   `/operator/providers` to enable and version only a profile whose readiness is
   `READY`. A configured-but-unimplemented adapter remains unavailable by
   design; do not override that signal.
5. Each tenant chooses from the backend-filtered ready catalog at `/kyc` (or the
   corresponding authenticated tenant API). The operator may assign policy at
   `/operator/tenants`, but cannot make missing host configuration appear ready.
6. Work only submissions marked `humanReviewAllowed`. Manual review is a
   Development simulation; an external-provider result is not manually
   approvable through this console.

The header's **End operator session** action clears only this browser's
HttpOnly operator cookie. The separately confirmed **Revoke all operator
sessions** control is an incident/handoff action that advances the server-side
authentication cutoff and signs out every operator browser.

To rotate credentials, change the username and/or password, increment
`NodeOperator__CredentialRevision`, and redeploy once. Never decrement or reuse
the revision for different credentials. Keep the previous deployment variables
available for rollback analysis, but a credential-revision rollback itself is
intentionally refused.

### One-shot schema job

Configure `azoa-schema` with the exact API image digest and the custom start
command `/usr/local/bin/docker-entrypoint.sh schema`. Give that service only:

- `SURREALFORGE_URL`, `_NS`, `_DB`, `_USER`, and `_PASS` using the SurrealDB
  owner references;
- `AZOA_RUNTIME_USER=azoa_runtime`; and
- `AZOA_RUNTIME_PASSWORD` as a generated 48-character secret.

The job waits for SurrealDB, runs `surrealforge up` without checksum force, then
creates or rotates a database-level `EDITOR` user. Require terminal `SUCCESS`
before promoting the API. The built-in role removes owner/IAM authority but is
not DDL-proof; track stricter data-only isolation separately.

The schema owner's username accepts a 3–64 character token-safe value, including
Railway-generated values that begin with a digit. Its `SURREALFORGE_PASS` accepts
any 32+ character printable secret without control characters; both are passed
only as quoted CLI/basic-auth data. Keep the runtime username letter-prefixed and
`AZOA_RUNTIME_PASSWORD` URL-safe (`A-Z`, `a-z`, `0-9`, `.`, `_`, `~`, `-`)
because the schema job interpolates them into the `DEFINE USER` query.

The entrypoint first overlays exact shipped files from
`Persistence/SurrealDb/CompatibilityBaselines/`, then applies timestamped
forward migrations. This preserves the checksum ledger while allowing the
generated desired-schema golden to advance.

### SurrealDB service on Railway

| Aspect | Value |
|---|---|
| Image | v3.1.4 multi-arch OCI index pinned as `docker.io/surrealdb/surrealdb@sha256:5757ed157c13b539bdc23a798ba2db1ffba6026deb3d15513058bffc77754a60` (the 1.5.4→3.x cutover tracked at `surrealdb-major-upgrade` is closed). |
| Start command | `/surreal start`; the shell-less image reads `SURREAL_USER`, `SURREAL_PASS`, `SURREAL_BIND=0.0.0.0:8000`, `SURREAL_LOG=info`, and `SURREAL_PATH=rocksdb:///data/db` from the environment. Do not put `$VAR` expansion in Railway's exec-form override. |
| Volume | Mount a persistent volume at `/data` and set `RAILWAY_RUN_UID=0`; Railway volumes mount root-owned and the shell-less Surreal image cannot perform a chown-then-drop bootstrap. The database process therefore runs as root inside its container, while the API uses root only to repair `/app/data` and immediately drops to `APP_UID` with `setpriv`. |
| Durability | RocksDB syncs its WAL per commit (`SURREAL_SYNC_DATA: "true"`), which satisfies the G1 durability gate — this is what `SurrealRuntime__G1DurabilityAcknowledged=true` on the Production API acknowledges. **G1 is live and green on SurrealDB 3.1.4** (re-verified during final-hardening-cutover). RocksDB remains the durable path; review the storage URI + sync flags at deploy time, since the fsync mode is not introspectable at runtime. |

---

## 3. Common diagnostics

### Exception logs and telemetry

`Logging__LogLevel__Default` is the single severity control used by every
configured sink: the base default is `Information`, Development uses `Debug`,
and Production uses `Critical`. The optional JSONL sink is enabled with
`Diagnostics__JsonlExceptionLogger__Enabled=true`; set
`Diagnostics__JsonlExceptionLogger__Directory` to an absolute path or a path
relative to the API binary directory (default `logs/exceptions`). Mount that
directory persistently if local exception records must survive a container
replacement. Configure OpenTelemetry export with
`OpenTelemetry__Otlp__Endpoint` and optional
`OpenTelemetry__Otlp__Protocol=grpc|http/protobuf`. Spans export exception type,
not exception message.

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
the edit and add a new forward migration. Production and ordinary development
flows never use `--force`; test a deliberate clean reset only on an isolated,
disposable volume.

**WebAPI boots but `/health` returns Unhealthy**
The `storage-db` check failed. Confirm SurrealDB is reachable from the API's
perspective:
- compose: `docker exec azoa-dev-api curl -s http://surrealdb:8000/health`
- host-run: `curl http://127.0.0.1:8000/health`

**WebAPI refuses to boot citing G1 durability**
`SurrealRuntime:G1DurabilityAcknowledged` is unset/false in Production (or the
development `SurrealDb` equivalent is unset). Confirm the SurrealDB storage URI
runs with per-commit sync, then set
`SurrealRuntime__G1DurabilityAcknowledged=true` for Production.

**Frontend hits CORS / wrong-API-URL errors**
`API_URL` is injected into the browser-facing runtime config by the Next.js
server. Update the service variable and restart the frontend; an image rebuild
is not required.

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
   sync (`SURREAL_SYNC_DATA: "true"`) + `SurrealRuntime__G1DurabilityAcknowledged=true`
   were re-verified against the 3.1.4 pin during `final-hardening-cutover`; the
   gate is not stale or unverified.

`docs/GO-TO-PROD.md` and `docs/RESIDUAL-RISK-RUNBOOK.md` now carry deprecation
banners pointing to `docs/NODE-HOST.md` as the authoritative operator guide;
this section is the durable record of why.
