---
type: spec
track: surreal-runtime-least-privilege
created: 2026-07-13
status: in_progress
horizon: pre-production
---

# Track: SurrealDB runtime least privilege

## Goal

Run the production AZOA API with a dedicated, database-scoped runtime identity
and keep schema/bootstrap authority outside the API process.

## Implemented boundary

- `SurrealRuntime` is the only production API connection section.
- Production rejects root, empty runtime credentials, legacy `SurrealDb`
  credentials, and an unset `AZOA_SKIP_MIGRATIONS=1`.
- The entrypoint refuses production API-boot migrations and no longer applies
  schema-ledger checksum overrides with `--force`.
- Development retains an explicit legacy fallback only to avoid breaking local
  and integration harnesses while their fixtures are migrated.

## SurrealDB 3.1.4 live evidence and limit

`SurrealDatabaseRoleProofTests` ran against the local pinned 3.1.4 container
on 2026-07-13 in a generated namespace that was removed in `finally`. A
database `EDITOR` completed a CRUD transaction, changed a schema-ledger row,
and ran `DEFINE FIELD`; it was denied `DEFINE USER`, `DEFINE DATABASE`, and
`DEFINE NAMESPACE`. A database `OWNER` could define a table and database user.
The proof therefore confirms principal/hierarchy separation but disproves any
claim that built-in `EDITOR` establishes write-without-DDL or ledger-tamper
isolation.

SurrealDB v3 requires `Surreal-Auth-NS` and `Surreal-Auth-DB` in addition to
the query `Surreal-NS`/`Surreal-DB` headers when Basic credentials name a
database user. Installed `SurrealForge.Client` 0.2.0 cannot express that
authentication scope, so the production runtime split remains fail-closed.
The local SurrealForge source now has a backwards-compatible explicit
`AuthenticationScope=Database` transport option with unit and live proof, but
AZOA must consume a published/reviewed version before deployment is permitted.

## Acceptance criteria

1. Production API startup fails unless `SurrealRuntime` contains endpoint,
   namespace, database, a non-root user, and a password.
2. Production API startup fails if it receives legacy `SurrealDb` credentials
   or can run migrations at boot.
3. Production deployment has a distinct schema job with schema credentials
   absent from the API process; it does not pass `--force`.
4. Before runtime activation, the consumed SurrealForge client proves a
   database-scoped runtime user can authenticate and perform required API
   reads/writes without using root credentials.
5. A separate, independently enforced mitigation prevents the runtime identity
   from altering schema or the migration ledger; built-in database `EDITOR`
   alone is explicitly insufficient.
6. The role proof covers the exact SurrealForge CLI bootstrap behavior before a
   production schema job is authorized.

## Non-goals

This track does not rotate credentials, modify a live database, create Railway
services, or claim that a database `EDITOR` cannot execute DDL.
