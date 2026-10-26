# Real SurrealDB store fixtures

## retained-history-and-terminal-races

The recovery ancestry audit seeds inert historical predecessors with typed
writers. Live `ClaimAsync` admission checks the parent's current child and must
reject a retained predecessor; the fixture also proves it cannot authorize a
submission. Pagination, immutable digests and stale-version checks use the real
audit store. In the consent expiry/revoke race, revoke may win without an expiry
event. Preserve the at-most-one expiry count and validate the original timestamp
of every expiry event that exists, alongside exactly one revocation event.

## user-store-fixtures

`SurrealUserStoreTests` creates both the user schema and the wallet identity migration
gate, then runs the real migration before testing deletion. Only an unreachable
health endpoint makes the class unavailable; schema, migration and store errors
fail setup instead of silently converting product regressions into skips. Assertions
cover returned identities, stored fields and deletion booleans rather than mutable
success messages. Profile updates retain the original password credential.

## authority-and-refund-seeds

Deployment run/execution and refund lot fixtures populate `[References]` properties
with `SurrealLink.ToLink` so the typed writer emits record values. Deployment
operation authority carries bare actor/run/node identifiers because the production
fence constructs those records itself. Keep those representations distinct. Wallet
proof removal and swap grant changes use predicates matching the exact seeded
owner/address or explicit live grant, retaining the production CAS assertions.

## wallet-claim-binding

`SurrealWalletAuthenticationTests` reads two real claim invites by exact token and
checks an absent token. The regression exercises the typed query over the real CBOR
transport: SurrealDB reserves `$token`, so the shared statement helper scopes the
column-derived binding to `$claim_lookup_token`. A local-variable rename cannot
change SurrealForge 1.3's generated parameter name.
The gate fixture uses a fresh username/email for its second profile so uniqueness
does not mask the allow path after migration.

## node-admin-commit

`SurrealNodeAdminStoreTests` uses generated directory/role schemas in its own
namespace. A later refused mutation must roll back the earlier role mutation and
directory epoch; concurrent claims at the same epoch produce exactly one winner;
a duplicate role create returns the typed duplicate outcome and cannot overwrite
the winner. These are the named store-family inventory witnesses.

`SurrealNodeAdminAuditLogTests` forwards every read and transaction to the real
database. An asynchronous read barrier makes two appenders capture the same
head; a witnessed head-CAS refusal must retry and produce a contiguous verified
chain. A preoccupied next row makes insertion fail after the head mutation;
every retry must roll back the head and preserve the existing row. These cases
inventory `NodeAdminAuditLog` separately from directory/role commits.

## hosted-identity-provider-binding

`SurrealKycIdentityStoreTests` binds a pending attempt under its exact live hosted
lease, through the real CBOR transport and generated schemas. Both direct and
tenant-scoped cases retain a lowercase provider key and uppercase enum, with null
optional personal fields and retention. Separate negative cases change the lease
token, provider key, selection version or trust revision and require a typed
conflict with no identity or evidence-guard partial write. This guards combined
query parameter collisions without removing any authority predicate.
