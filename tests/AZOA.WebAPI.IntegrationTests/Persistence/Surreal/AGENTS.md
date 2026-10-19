# Real SurrealDB store fixtures

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
transport: SurrealDB reserves `$token`, so the store captures `claimToken` instead.
The gate fixture uses a fresh username/email for its second profile so uniqueness
does not mask the allow path after migration.

## node-admin-commit

`SurrealNodeAdminStoreTests` uses generated directory/role schemas in its own
namespace. A later refused mutation must roll back the earlier role mutation and
directory epoch; concurrent claims at the same epoch produce exactly one winner;
a duplicate role create returns the typed duplicate outcome and cannot overwrite
the winner. These are the named store-family inventory witnesses.
