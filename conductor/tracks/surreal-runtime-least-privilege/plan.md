---
type: plan
track: surreal-runtime-least-privilege
created: 2026-07-13
status: in_progress
---

# Plan: SurrealDB runtime least privilege

1. [x] Split production API runtime configuration from schema credentials and
   fail closed on root, legacy credentials, or API-boot migrations.
2. [x] Remove checksum-forcing behavior from the API entrypoint.
3. [x] Prove the built-in role behavior in a disposable local SurrealDB 3.1.4
   namespace: `EDITOR` can transact, mutate the schema ledger, and run DDL but
   cannot create users/databases/namespaces; `OWNER` can run schema DDL and
   create database users. Database Basic auth additionally requires explicit
   `Surreal-Auth-NS`/`Surreal-Auth-DB` headers.
4. [ ] Add a dedicated deployment schema job after the live role proof shows
   which database/namespace authority it needs; keep its credentials out of
   the API service and rotate the prior root secret.
5. [ ] Publish/review and consume the SurrealForge client authentication-scope
   option, then prove the actual API connection authenticates as a database
   user without root credentials.
6. [ ] Replace or constrain database `EDITOR` if the live proof confirms it
   retains unwanted DDL authority; do not mark this track complete before that
   limitation has a verified mitigation.
7. [ ] Exercise the exact SurrealForge schema CLI bootstrap path with its
   isolated schema identity before authorizing a production schema job.
