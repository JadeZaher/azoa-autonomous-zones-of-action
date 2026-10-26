# tools/dev-node — host-run dev node for the ArdaNova tandem demo

`dev-node-up.ps1` brings up an AZOA node an external app (ArdaNova's BFF) can drive end to end;
`dev-node-down.ps1` reaps it. PowerShell 5.1-compatible (no `&&`, `||`, `??`, ternary).
Owning track: `mvp-tandem-demo_20261024` in the **ardanova** repo (`conductor/tracks/`); its
`plan.md` fixes the interface contract below.

## What up does (each step reuses a live component, so a re-run is a status-printing no-op)

1. **SurrealDB** — reuses anything healthy on `127.0.0.1:<SurrealPort>`; otherwise starts the
   `surrealdb` service of `docker-compose.dev.yml` (docker compose / docker-compose / podman
   compose / podman-compose, first one whose ENGINE is running) with `SURREALDB_HOST_PORT`.
   No running engine → fails immediately with the reason (`-StartPodmanMachine` opts in to
   `podman machine start`).
2. **Schema** — `surrealforge up --no-reconcile`, the same flags `docker-entrypoint.sh` uses,
   with SurrealForge.Cli pinned to the csproj's `SurrealForge.Schema` version (installed into
   `.omc/tools/dev-node-schema-cli/<ver>`). Runs only when the WebAPI is (re)started.
3. **KYC sidecar** — `node tools/dev-kyc-vendor/server.mjs` (see that directory's AGENTS.md).
4. **WebAPI** — `dotnet build` then `dotnet bin/Debug/net10.0/AZOA.WebAPI.dll`, NOT `dotnet run`:
   `dotnet run` forks the app as a child, so the recorded PID would be the launcher and `down`
   would orphan the real server. Env: `Development`, `Blockchain__Mode=Simulated`,
   `CustodialAccounts__CustodyMode=DevelopmentOnly`, `Kyc__Identomat__*` → sidecar,
   `Kyc__Identomat__AllowInsecureDevVendor=true`, `Kyc__Identity__PersonKeySecret`, `Kyb__ConfigId`.
   Health: `GET /health` (`Observability/HealthCheckExtensions.cs`).
5. **Tenant** — registers a tenant user (`POST api/user/register`, Development allows
   self-registration), then mints TWO keys, because no public route issues the full scope set
   on one key: the operator route `POST api/apikey/tenant` issues the FIXED set
   `tenant:provision,wallet:manage,kyc:read,kyc:submit` (`ApiKeyController.CreateTenantKey`), and
   `tenant:provision` is not self-issuable (`AzoaScopes.IsIssuableByUser`). The value scopes
   `wallet:manage,kyc:read,kyc:submit,units:issue,nft:mint,token:create:sign,grant:sign` are
   self-issued by the tenant user (`POST api/apikey` with its login JWT). Then the operator enables
   the `identomat` profile and selects it for the tenant (same calls as `HttpFlowTestBase`).
   Reuse: stored keys are validated with `GET api/user/{tenantId}`; a tenant whose login fails
   (DB wiped) is replaced by a fresh one.
6. **Tenant KYB** (custodial enrolment requires it, D-6) — begins business verification and
   waits for the sidecar's approval (best effort; `-SkipKybBootstrap` skips).
7. **ARDA genesis** (`Ensure-ArdaGenesis`, §arda-genesis) — reads `GET api/admin/platform-token`;
   when absent, runs the one-time platform-unit genesis on the Simulated chain and drives its
   durable creation operation to `Completed`; then `Ensure-ArdaOnRampPartition` declares the `on-ramp`
   reserve partition at 1,000,000,000 base units (half the supply) through the operator route
   `POST api/admin/platform-token/partitions` (re-declare SETS, so every run is idempotent).
8. Writes `.omc/state/dev-node-env.json`.

## Interface contract (`.omc/state/dev-node-env.json`)

Required by the track: `baseUrl`, `tenantApiKey` (the provisioning key), `tenantId` (the tenant
GUID owning the keys → ArdaNova `Azoa:TenantId`), `kyc { beginBaseUrl, resultBaseUrl, companyKey,
configId, callbackApiKey }`, `surrealEndpoint`. Additive: `tenantApiKeyScopes`, `tenantValueApiKey`,
`tenantValueApiKeyScopes`, `kybStatus`, `sidecarControlUrl`, `writtenAt`, `ardaAssetId` /
`ardaChainType` / `ardaDecimals` / `ardaOnRampPartition {name, declared, available}` (the platform unit; ArdaNova maps them to `Azoa:OnRamp:AssetId` /
`Azoa:OnRamp:ChainType`). Written UTF-8 without BOM.

## §arda-genesis

ARDA is the node's platform unit (`Services/Reserve/PlatformTokenGenesis.cs`). There is no genesis
config section and no boot hook: genesis is the operator route `POST api/admin/platform-token/genesis`
(`Operator` + `RecentNodeOperatorSession`), and an empty definition takes `PlatformTokenGenesisDefaults`
— `Arda`/`ARDA`, **2 decimals, 2_000_000_000 base units (20,000,000.00 ARDA)**. The dev node keeps
those defaults on purpose: ArdaNova's on-ramp seed price (`UnitsPerUsdMinorUnit = "100"`, 1 ARDA =
$0.01) assumes 2 decimals, and the doc's "promote the identical definition" rule
(`docs/PLATFORM-TOKEN-GENESIS.md` §1) applies. Only `chainType = "Simulated"` and
`expectedNetwork = "Devnet"` are sent: `Blockchain:Mode=Simulated` routes every chain to the simulated
provider, whose fungible-creation intent must name `Simulated`, and `appsettings.Development.json`
lists `Simulated` under `Reserve:Chains` with its pinned `ExpectedAddresses:Simulated` reserve.
The response is `202` with a durable creation operation in `AwaitingConsent`; the script walks
`api/admin/platform-token/creation/{id}` `quote` (if no quote) -> `accept-quote {version, quoteId}` ->
`continue {requestId, version}` -> `refresh` until `Completed` (simulated costs are zero). The asset id
is the simulated provider's deterministic ASA-shaped integer, so it is path-safe for the price-schedule
route. Idempotent: an existing active definition short-circuits; a re-run with the same definition
resumes the same `genesis_operation_key` rather than minting a second unit. A SurrealDB volume wipe
means a new asset id — re-run up and restart ArdaNova so it re-reads `ardaAssetId`.

## Ports (A3)

Host-run node ⇒ SurrealDB on **8020**: the committed `appsettings.Development.json` pin, CI's
`--publish 8020:8000`, and the integration tests' `SurrealTestDefaults.Endpoint` all use it, so up
exports `SURREALDB_HOST_PORT=8020` and also passes `SurrealRuntime__Endpoint` explicitly.
`docker-compose.dev.yml` keeps its `8000` default for `./dev-up.ps1`, whose containerised API
reaches SurrealDB over the compose network and overrides the endpoint itself. Both launchers
share the container name `azoa-dev-surrealdb`; if `dev-up` already runs it on another host port,
up refuses and tells you to pass `-SurrealPort <that port>`.
Defaults: node 5000, sidecar 8031 (API host) / 8032 (widget host).

## State and ownership

`.omc/state/dev-node/`: `pids.json` (pid + process start time per component, plus the container
it started), `dev-secrets.json` (vendor keys + person-key secret, generated once — the person-key
secret must stay stable), `tenant.json` (tenant credentials + keys), `logs/`.
`down` stops ONLY recorded PIDs whose start time still matches (guards PID reuse) and the
SurrealDB container only if up started it; it never matches by process name. A failed up reaps
what that run started. `down` deletes `dev-node-env.json` (it would describe a dead node) but keeps
`tenant.json`, so the next up reuses the same tenant id.
