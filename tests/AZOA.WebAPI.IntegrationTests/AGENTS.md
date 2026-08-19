# AZOA.WebAPI.IntegrationTests — module notes

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

- **`record<T>` link fields** (e.g. `wallet.avatar_id`, `api_key.avatar_id`,
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
fields — e.g. `avatar` has no `karma`/`level`). `record<T>` links do NOT require the
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
