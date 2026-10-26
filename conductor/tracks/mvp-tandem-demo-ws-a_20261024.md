---
type: report
track: mvp-tandem-demo_20261024
workstream: WS-A
date: 2026-10-24
branch: feat/platform-stub-closeout
---

# WS-A report — AZOA node demo readiness (A1–A5)

The **owning track lives in the ardanova repo**:
`ardanova/conductor/tracks/mvp-tandem-demo_20261024/` (`spec.md`, `plan.md`). This file is the
oasis-sleek-side record of what WS-A changed; the orchestrator folds it into that track. Nothing
is committed — all changes are uncommitted on `feat/platform-stub-closeout`.

## A1 — stale worktrees

- Removed `.worktrees/node-admin-release` and `.worktrees/tenant-economic-workflow-ai` (both
  `status --porcelain` clean), then `git worktree prune`. Their branches
  (`feat/node-admin-release`, `feat/tenant-economic-workflow-ai`) are kept.
- **Left `.worktrees/main-release` — DIRTY:** 3 uncommitted lines appended to
  `conductor/tracks/node-admin-rbac-cli_20261018/owner-live-drill.md` (a NodeAdmin root public key,
  fingerprint, `NodeAdmin__RootPublicKeys=…`, `NodeAdmin__RootKeyRevision=1`). This is the owner's
  material, so it was not discarded.
- Evidence: the architecture-guard suite went from 7 to **5** failures, and all 5 cite only
  `.worktrees/main-release/**` (SurrealQlSourceRules ×3, ReservedIdentityInventory,
  TransactionScopeGate). **Deferred, trigger:** the owner commits or discards that note → `git
  worktree remove .worktrees/main-release` → 0 failures. (Optional hardening: the guards'
  source scanners exclude `bin/obj/.git/.omc` but not `.worktrees`, unlike the .NET SDK, which
  skips dot-folders.)

## A2 — SDK rebuild

`sdk/azoa-wallet` (azoa-sdk 0.5.0): `npm run build` exit 0 (tsup, fresh dist), `npm run
typecheck` exit 0. No version bump, no publish.

## A3 — SurrealDB port

Decision: a host-run node uses **8020**, which `appsettings.Development.json`, CI
(`--publish 8020:8000`) and `SurrealTestDefaults.Endpoint` already use. `dev-node-up.ps1`
exports `SURREALDB_HOST_PORT=8020` to compose and passes `SurrealRuntime__Endpoint`
explicitly. `docker-compose.dev.yml` keeps its 8000 default for `./dev-up.ps1`, whose container API
reaches the DB over the compose network. If `dev-up` already runs the shared
`azoa-dev-surrealdb` container on another port, up refuses and names `-SurrealPort <port>`.
No committed config changed. Rationale: `tools/dev-node/AGENTS.md` §Ports.

## A4 — dev KYC sidecar + Development-only origin allowance

Sidecar `tools/dev-kyc-vendor/` (Node ≥ 20, zero dependencies, no `.cs`, so the
`**/*.cs` glob can't pull it in): `server.mjs` ports `IdentomatVendor.cs` onto two listeners. The API host
(:8031) serves `/begin`, KYB `/result` and the four AML paths; the widget host (:8032) serves
`/external-api/result/` and `/external-api/delete/`, plus an applicant page. Company-key 401s
and the `argumentError` shape match the fake. Modes: `approve-on-begin` (default),
`approve-on-poll`, `manual`. Control: `POST /__dev/sessions/{id|token|clientUserId}/verdict`
(Pending/ManualCheck/Approved/Rejected), `POST /__dev/mode`, `GET /__dev/sessions`, `GET
/__dev/health`. It sends keyed callbacks to `/api/kyc/callback/identomat` at 1.5 s, 5 s and
15 s; replays are safe because the node re-fetches the result. Every call is logged as one JSON line.

Allowance:
- `Services/Kyc/KycRuntimeSafety.cs:21-39` — `IsInsecureDevVendorAllowed` = Development AND
  `IsManualSimulationAllowed` (Simulated, no real-value bridge) AND
  `Kyc:Identomat:AllowInsecureDevVendor=true`; `IsLoopbackDevVendorUrl` = loopback-only http(s),
  with no credentials, no fragment, and no query for origins.
- `Providers/Kyc/IdentomatKycProviderService.cs:85,134-136,315,526-537` — vendor origins pass
  `ValidVendorOrigin`. The widget redirect passes `ValidWidgetRedirect`, which also requires the
  configured `ResultBaseUrl` scheme+host+port. `ReturnUrl` is still strict.
- `Providers/Kyb/IdentomatKybProvider.cs:22,126` — the same rule. It's needed because custodial enrolment
  requires the tenant's KYB (D-6), so the KYC leg can't be reached without it.
- `Services/Kyc/KycProviderRegistry.cs:387` — Identomat missing keys now come from the adapter
  instead of a duplicate rule, so the readiness surface can't disagree with it.

Tests:
- `tests/AZOA.WebAPI.Tests/Kyc/IdentomatDevVendorOriginTests.cs`: 25 cases, all pass. They are
  table-driven over (environment, flag) across the KYC adapter, registry readiness and KYB adapter:
  Production+flag, Staging+flag, Production, and Development without the flag all refuse and name
  the URL keys; only Development+flag is Ready.
- Development+flag with `Blockchain:Mode=Live` refuses.
- The allowance still refuses `http://` public hosts, 10/8, 169.254.169.254, userinfo, a query, and
  `ftp`. A widget redirect off the result host is refused.
- `node --test tools/dev-kyc-vendor/server.test.mjs` — 5/5 pass.
- Not added: a full WebApplicationFactory flow test. The existing `IdentityVerificationFlowTests`
  already cover the HTTP flow against the C# fake.

**Review lane:** A4 is security-sensitive. It needs the adversarial review the plan requires
before C1 relies on it (verdict pending).

## A5 — `tools/dev-node/dev-node-up.ps1` / `dev-node-down.ps1`

Each step reuses a live component, so a re-run is a no-op that prints status.

1. SurrealDB via compose on whichever engine is actually running (the reason is printed if none is).
2. `surrealforge up --no-reconcile`, pinned to the csproj version.
3. The sidecar.
4. `dotnet build`, then `dotnet <dll>`. It doesn't use `dotnet run`, which forks a child the
   recorded PID wouldn't cover. Health is `GET /health`.
5. Tenant mint-or-reuse.
6. Best-effort tenant KYB via the sidecar.
7. It writes `.omc/state/dev-node-env.json`.

Down stops only recorded PIDs whose process start time still matches, and the container only if
up started it. A failed up reaps whatever that run started.

**Contract deviation (needs WS-B):** no public route mints one key with all 8 scopes. The operator
route `POST api/apikey/tenant` issues the fixed set `tenant:provision,wallet:manage,kyc:read,kyc:submit`
(`Controllers/ApiKeyController.cs:58-59`), and `tenant:provision` is not self-issuable. So up mints
two keys:
- `tenantApiKey`, the provisioning key (contract field);
- `tenantValueApiKey` (`wallet:manage,kyc:read,kyc:submit,units:issue,nft:mint,token:create:sign,grant:sign`),
  self-issued by the tenant user.

The JSON carries both, plus `tenantId` (the WS-B addition), along with the contract's `baseUrl`, `kyc{…}`
and `surrealEndpoint`. WS-B/C must route calls that need value scopes through the second key, or
the node needs a deliberate scope decision.

Evidence: all three scripts parse clean under Windows PowerShell 5.1.22621.
- Smoke: `dev-node-up.ps1` failed in 3 s with exit 1 and a clear message ("podman is installed but
  its machine is not running …"), leaving no process.
- `dev-node-down.ps1` on empty state exits 0.
- Live harness with the sidecar: it started, `/__dev/health` answered 200, the ownership check
  passed, a forged start time was rejected, `down -Only sidecar` stopped it, and the process was gone.

**Deferred to WS-C, trigger: the podman machine or Docker is running.** The first full live up
(SurrealDB → schema → WebAPI → tenant mint → KYB → JSON) and a second run to prove the no-op
path. `-StartPodmanMachine` opts in to starting the machine.

## Validation sweep (single pass)

- `dotnet build azoa.sln`: succeeded, 0 errors.
- `dotnet test` filtered to Kyc, Kyb, Architecture, BrandLeakGuard and NodeOperatorSecurityContract:
  669 passed, 5 failed (all `.worktrees/main-release`, see A1).
- SDK build and typecheck pass. Sidecar node tests 5/5 pass.
