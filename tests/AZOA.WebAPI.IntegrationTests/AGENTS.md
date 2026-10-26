# AZOA.WebAPI.IntegrationTests — module notes

All four independent `WebApplicationFactory<Program>` roots explicitly set
`OpenTelemetry:ExportEnabled=false` before host registration. Their derived HTTP
flow and Development composition factories inherit it. Trace and metric providers,
instrumentation and correlation remain active; only remote OTLP processors/readers
are absent, so fixture disposal does not wait on an external collector. See
`Observability/AGENTS.md` for the pinned SDK contract and default-enabled behavior.

## §factory-bootstrap-order — create isolation before host startup

`WebApplicationFactory.CreateClient()` starts application hosted services. Test
class constructors run before xUnit calls `IAsyncLifetime.InitializeAsync`, so a
hosted service that reads SurrealDB would otherwise reach the per-class namespace
before `IntegrationTestBase` created it. `AZOATestWebApplicationFactory.CreateHost`
therefore creates the server-generated namespace, `test` database, and the
startup-required `admin_bootstrap_state` and `saga_steps` goldens first. `IntegrationTestBase.InitializeAsync`
then idempotently reapplies that scope and the complete generated schema before
test methods run. Keep bootstrap identifiers server-generated Guid hex and never
move user-controlled input into root-scope DDL.

## §test-principal-capabilities — mirror production policy conjunctions

`NodeGovern` requires both a JWT node-operator identity and the `node:govern`
scope. Operator test principals carry the production `token_use=node_operator`
credential class, and `CreateNodeGovernClient` stamps that identity plus the
governance capability. `CreateOperatorOnlyClient` deliberately suppresses the
governance capability so negative policy tests remain meaningful.

## §signing-principal — the default test principal is a real, signable login

Every signing seam (`ActionController` execute/start-workflow, deploy, NFT, hold,
consent, ...) first calls `SigningAdmissionAuthorityFactory.CaptureCredentialAsync`,
which refuses unless (a) the subject is an **active row** in `IUserStore`
("The signing user is unavailable.") and (b) the principal carries the validated
login-token time evidence `ValidatedTokenTime.TryRead` reads: `auth_time` **and**
`exp` (optional `iat` must sit between them). A production login JWT always has
both, because `UserLoginTokenIssuer` only mints tokens for registered users.

`TestAuthHandler` therefore stamps `auth_time`/`iat`/`exp` exactly like
`UserLoginTokenIssuer.BuildLoginToken`. The user row is **opt-in** per class via
`protected override bool SeedsDefaultPrincipalUser => true;` on
`IntegrationTestBase`, which writes `TestAuthHandler.DefaultUserId` through the
app's own `IUserStore.CreateIfAbsentAsync` (the seam production uses for fixed-id
identities, e.g. `SeedAdminHostedService`). It is not global because suites that
assert exact user counts (`UserControllerIntegrationTests.GetAll_AsOperator_*`
expects 2) would see the extra row. Opt in whenever a class drives a signing seam
as the default principal; principals minted with `X-Test-User-Id` for a random
Guid are intentionally *not* signable and remain fine for IDOR/denial tests.

Run admission is keyed: `ActionManager.StartWorkflowRunAsync` derives the run id from
the `Idempotency-Key` header (`ActionWorkflowIdentity.RunId(action, user, tenant, key)`)
and refuses a request without one. Success-path `execute`/`start-workflow` calls go
through `IntegrationTestBase.PostWithRequestKeyAsync`, which mints a fresh key per
call; two calls are two runs, and reusing a key replays the first run.

## §durable-execute — `execute` admits a durable run; it does not complete it

`ActionManager.ExecuteAsync` is an alias for `StartWorkflowRunAsync`: the HTTP
response is the admitted run (`Running`) and node work happens on the saga outbox.
A test that asserts node states (gate failure, cascade skip, Grant output) must call
`IntegrationTestBase.SettleRunAsync(client, runId, host?)`, which drains
`ISagaProcessor` in a fresh scope and re-reads `execution-state` until the run leaves
Pending/Running (30s bound, then a `TimeoutException` naming the stuck status).
Draining and polling together is deliberate: the hosted processor is always on
(`Sagas:Enabled=false` with a registered consumer trips a boot guard), so either
processor may run a step, and the test only cares about the settled outcome. Pass the
owning host's `Services` when a second factory shares the namespace (the Ardanova
simulated host owns the Simulated Grant handler and `ReadyValueAccessService`).
Residual risk: the base host's hosted processor can still claim a simulated-host
step on its poll. Known gap: a Grant node is now an NFT *mint* (`metadataUri`
required, no imported `tokenId`), and `SimulatedBlockchainProvider` exposes no
`INftTransactionModule`, so `NftLifecycleService` refuses it ("This chain does not
offer a verified NFT lifecycle.") and the run parks in `AwaitingReconciliation`.
`ArdanovaFlow_Tier2Grant_*` stays red until the simulated provider gains that module.

## §bundle-deploy-contract — ActionGraphBundle generate/deploy are versioned

`POST api/actiongraphbundle/{id}/generate` requires `Generation`
(`DeploymentGenerateRequest`: explicit `DeploymentTarget(Kind, ChainType, Network)` +
ordered published `ActionIds`); the legacy `TargetChain`/`Config` body is refused.
A `PlatformRuntime` artifact needs 1–64 published sources
(`DeploymentArtifactCompiler.Compose`), so tests publish a one-node Emit action first.
Generation writes `DeploymentConfig = {formatVersion:2, artifactId, artifactHash,
state:"Generated", target}` onto the bundle. `deploy` takes a `DeploymentStartRequest`
body naming that exact artifact plus an `Idempotency-Key` header and returns a
`DeploymentReceipt`; platform-runtime activation confirms synchronously (200,
`State == "Confirmed"`, `InvokeUrl` set). An unknown bundle id on generate is 404
(`ActionGraphBundleAuthorizationError.NotFound` prefix), never 400.

## §param-binding — SurrealDB 3.x HTTP `/sql` parameter binding

`ExecuteSurrealSqlAsync(sql, params)` seeds data over the raw HTTP `/sql`
endpoint. The parameter-passing contract **changed** between the pre-1.0
`SurrealDb.Net` era and the current SurrealDB **3.1.4** cutover, and the old
form fails **silently**:

- **Broken (pre-3.x):** POST a JSON envelope `{"query": "...", "params": {...}}`
  as `application/json`. On 3.1.4 the server treats the whole envelope as a single
  literal string expression — it returns `status: OK` while echoing the envelope
  back as a value (`{"result": {"params": ..., "query": ...}}`) and **never runs
  the statement**. Every bound-param seed became a no-op, so tests that seeded this
  way saw an empty table with no error. This was the root cause behind several of
  the documented integration-tail failures (G5 restore-drill, MCP catalog/vector
  seeds).
- **Correct (3.x):** the SurrealQL goes in a **`text/plain` body**, and each
  parameter is prefixed as a `LET $name = <surql-literal>;` statement ahead of the
  query in that same body. Query-string binding (`/sql?id=foo`) also works but only
  for **scalars** — it arrives untyped-as-string and **cannot carry an object**, so
  `CONTENT $body` with a structured `$body` fails `InvalidContent`. The `LET`-prelude
  form binds scalars *and* objects, type-preserved (JSON is a valid SurrealQL object
  literal; `null` → `NONE`), which is what the G5 restore-drill and MCP seeds need.

`BuildParamLets` + `ToSurqlLiteral` reflect the anonymous params object into those
`LET` statements.

`ExecuteSurrealSqlAsync` now also inspects the JSON body for `"status":"ERR"` and
throws — the endpoint returns HTTP 200 even when an individual statement errors, so
without this a failed seed could still masquerade as success.

Related: `SurrealClient` must send the **`Surreal-NS`/`Surreal-DB`** headers (the
legacy `NS`/`DB` names are ignored on 3.x, silently routing to the default
namespace). `ExecuteSurrealSqlRawAsync` was already on the correct text/plain path.

## §g5-seed-shapes — matching seed values to SCHEMAFULL column types

`ExecuteSurrealSqlAsync`'s `LET`-prelude renders scalars/objects as JSON literals.
That is correct for plain columns but **wrong for three shapes**, so G5 seeds inject
those as SurrealQL literals via `object::extend($body, { ... })` (scalars stay in `$body`):

- **`record<T>` link fields** (e.g. `wallet.user_id`, `api_key.user_id`,
  `consumed_vaa_ledger.bridge_transaction_id`): a bare string won't coerce. Use
  `field: type::record('T', $idParam)`. Note SurrealDB 3.x is `type::record`, NOT
  `type::thing` (which was removed).
- **`datetime` fields**: a JSON string fails coercion. Use `field: type::datetime($isoParam)`.
- **`option<T>` fields set to NONE**: OMIT them. A JSON `null` inside `$body` becomes
  SurrealDB `NULL` (rejected: `Expected 'none | string' but found 'NULL'`); an absent
  SCHEMAFULL field defaults to NONE. Do not seed `field = (string?)null`.
- **`id` in a `CREATE <table> CONTENT { id: $hid, ... }`**: pass BARE hex. `CREATE`
  prefixes the table, so the record id becomes `<table>:hex`. Seeding `id: "holon:hex"`
  double-prefixes to `holon:⟨holon:hex⟩`, which no query then matches. The MCP tools
  query records by their `<table>:hex` link form (see `Mcp/AGENTS.md §record-id-binding`).

Also: only seed fields that EXIST in the generated schema (SCHEMAFULL rejects unknown
fields — e.g. `user` has no `karma`/`level`). `record<T>` links do NOT require the
target row to pre-exist, so seed order is unconstrained.

`RunPwsh` resolves `pwsh` (7+) first, falling back to `powershell.exe` (5.1) when pwsh
is absent (this dev box + some CI images ship only 5.1); the backup/restore scripts are
5.1-compatible.

## §g5-container-selection — CI-safe restore container targeting

`G5_RestoreDrillTest` defaults its script invocation to the local
`azoa-dev-surrealdb` container. CI sets `AZOA_SURREALDB_CONTAINER_NAME` to the
container it starts (`azoa-ci-surrealdb`), and the test passes that value explicitly
to both backup and restore scripts. This keeps the restore drill enabled in the
routine non-chaos/non-performance correctness gate without making local runs depend
on the CI container name.

## §database-role-proof — disposable privilege behavior evidence

`Persistence/Surreal/SurrealDatabaseRoleProofTests.cs` creates one
server-generated namespace/database and uses root only for setup and teardown. It
never touches the default `azoa` namespace or any shared user, and skips only when
`/health` is unreachable. The deliberately raw statements cover DDL, principal
management, and a multi-statement transaction beyond typed CRUD. The proof records
the pinned 3.1.4 limitation: database `EDITOR` can mutate table definitions and a
schema-ledger row, so separating runtime from root does not establish DDL tamper
isolation.

## §treasury-provider-config — live-mode simulated provider registration

`AZOATestWebApplicationFactory` remains in `Blockchain:Mode=Live`, matching the
ordinary integration host. It registers an enabled `Simulated/Devnet` chain entry
only so node treasury routing can validate its explicitly requested provider without
network I/O. Tests that need globally simulated settlement use their dedicated
factory and set `Blockchain:Mode=Simulated`; do not change the shared host mode to
make a treasury test pass.

## §cbor-transport — the harness runs on the SDK, not the JSON transport

Every `ISurrealConnection` this harness builds is `SurrealDbNetConnection`
(SurrealForge 1.0.0's `SurrealDb.Net`/CBOR transport). `HttpSurrealConnection`
is gone: leaving it here would have kept the store tests on the legacy JSON
wire, where SurrealDB coerces text into typed columns, and the suite would have
proved nothing about the transport the app actually uses. See
`Core/Surreal/AGENTS.md §cbor-transport` for what changes because of that.

- **Endpoint is `http://127.0.0.1:8020`** (`SurrealTestDefaults`). Port 8000 is a
  different service on this machine.
- **Surreal settings reach the app through `UseSetting`, not
  `ConfigureAppConfiguration`.** `Program.cs` binds the section eagerly, before
  any `ConfigureAppConfiguration` delegate registered by a
  `WebApplicationFactory` has run; values supplied only there arrive too late and
  the host silently falls back to `SurrealConnectionOptions`' default endpoint
  (`http://localhost:8442`), where nothing listens. Both the shared factory and
  the two per-test factories (`ParameterizedAuthFactory`,
  `ArdanovaSimulatedFactory`) do this.
- **`SurrealCborTransportProofTests` is the guard.** It asserts the app host
  resolves `SurrealDbNetConnection`, and that `type::of()` on real FK columns
  reads back `record` — checked over a raw HTTP/JSON fixture, outside
  SurrealForge, so the library cannot grade its own output. A green suite that
  quietly fell back to the JSON transport is exactly what it exists to catch.

The shared factory pins **both** `SurrealDb` and `SurrealRuntime` to its generated
namespace and `test` database, using the same local endpoint and root fixture
credentials. `DevelopmentCompositionFactory` selects the Development config,
which already contains `SurrealRuntime`; pinning only the legacy section lets the
eager SDK binding escape into the shared `azoa` namespace. Apply identical values
with `UseSetting` before registration and the late in-memory provider.

## §node-admin-real-auth — `NodeAdmin/NodeAdminWebApplicationFactory`

Node-admin flows need the production JWT pipeline (the `OnTokenValidated` node-admin branch, the policy
provider, the denial handler), so this factory does **not** install `TestAuthHandler`. It creates its own
namespace with the `node_admin*` goldens plus the startup prerequisites before host start, configures a
generated root key, a fixed audience and a raised `operator-login` limit, and drops the namespace on dispose.
Clients sign with real Ed25519 (`NodeAdminTestKey`, BouncyCastle aliased `BCCrypto2` exactly like the API
project) and compute real TOTP codes; `AwaitStepHeadroomAsync` keeps a ±1-step sequence inside one 30 s
window so step boundaries cannot flake the run.

`NodeAdminOperatorRoutesFactory` (lane A2) derives from it through `ExtraTables`/`ExtraSettings`: it adds the
governance and KYC tables and seeds the legacy reserved operator (`NodeOperator:*`), so one host exercises named
admins, root and the legacy operator token against the same operator routes (`NodeAdminOperatorRouteFlowTests`,
`NodeAdminOperatorFenceTests`). Step-up is driven by rewinding the session row's `auth_time`, never by a fake clock.

`TestAuthHandler`'s operator principal (`X-Test-Operator`) defaults its subject to the reserved operator id, the only
subject a real `node_operator` token can carry: operator controllers resolve their actor with
`HttpContext.TryGetOperatorActor`, which refuses any other subject. Operator tests that assert `actor_user_id`
therefore expect `NodeOperatorIdentity.UserId`.

## §wallet-identity-migration-race

Every host runs `WalletAuthIdentityMigrationWorker`, which calls `WalletAuthIdentityMigrationService.SweepAsync` every
5 s under a lease. A test that sweeps by hand can land while the worker holds that lease; its sweep is then a no-op and
the status reads `Migrating`. Tests that need `Ready` (e.g. `UserControllerIntegrationTests.Delete_ShouldRemoveUser`)
sweep in a loop until `Ready`, bounded by the 2-minute lease a cancelled sweep can leave behind.

## §caller-built-holon-operations

`api/holon/{id}/mint` and `/exchange` are refused by design (Managers/AGENTS.md §caller-built operations, M9): 400 with
`OPERATION_NOT_ALLOWED` and a pointer to `/api/nft/mint` or the reserve exchange. The Holon tests assert that refusal.
