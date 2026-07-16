# AZOA.WebAPI.IntegrationTests — module notes

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

## §database-role-proof — disposable privilege behavior evidence

`Persistence/Surreal/SurrealDatabaseRoleProofTests.cs` creates one
server-generated namespace/database and uses root only for setup and teardown. It
never touches the default `azoa` namespace or any shared user, and skips only when
`/health` is unreachable. The deliberately raw statements cover DDL, principal
management, and a multi-statement transaction beyond typed CRUD. The proof records
the pinned 3.1.4 limitation: database `EDITOR` can mutate table definitions and a
schema-ledger row, so separating runtime from root does not establish DDL tamper
isolation.
