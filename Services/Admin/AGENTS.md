# Services/Admin — operator:admin bootstrap (H2)

## Why

`AvatarManager.GenerateJwt` emitted no role/scope claim, so there was no way to
mint the first `operator:admin` principal (`Core/AzoaScopes.cs:33`) — the
`Operator` authorization policy in `Program.cs` requires it (or the legacy
`role=Admin`/`is_admin` claim), but nothing could ever produce it for a fresh
deploy. NODE-HOST §8.9 documented "configure your JWT issuer" with no concrete
mechanism.

## Mechanism (stateless, no schema change)

`AdminBootstrapOptions` (config section `AdminBootstrap`) holds two values, both
env-driven and both required together:

- `AdminBootstrap:SeedEmail` — the email of the avatar to promote.
- `AdminBootstrap:SeedSecret` — a shared secret proving operator intent.

`AvatarManager.StampOperatorAdminIfSeeded` runs on every JWT mint
(`GenerateJwt`). If **both** values are set and the minting avatar's email
(case-insensitive) matches `SeedEmail`, the token gets `scope=operator:admin`
plus the interim `role=Admin` / `ClaimTypes.Role=Admin` claims (the legacy path
keeps working unconditionally — this is additive).

**Fail-closed by construction:** either value absent ⇒ the seam is a no-op for
every avatar, forever. There is no code path that stamps a scope without both
values present. The secret is a config value, never checked against a request
input (no endpoint takes a "prove you know the secret" body) — proving intent
means an operator with deploy/config access set it, which is the same trust
level as any other launch secret (Jwt:Key, WalletEncryptionKey, etc).

`SeedAdminHostedService` adds nothing to the stamping decision — it only makes
a **partial** config (one of the two set, not both) loud: a warning in
Dev/IntegrationTest, a startup **throw** in Production. This catches an
operator who sets `SeedEmail` and forgets `SeedSecret` (or a typo'd env var
name) before it becomes a silent, permanent inert-bootstrap surprise.

## Why this shape, not a persisted "is admin" column

Adding a persisted admin flag to `Avatar`/the SurrealDB schema means a schema
migration + regenerated `.surql` goldens for a capability that is meant to be
used exactly once (mint the first operator, then rely on `operator:admin`
delegation for subsequent admins if ever needed). The config-driven seed avoids
touching `Persistence/SurrealDb/Models/Avatar.cs` or its generated schema at
all — the seam disappears the moment an operator unsets the env vars post-
bootstrap, with zero data left behind.

## Operator procedure

See `docs/NODE-HOST.md` §8.9 for the exact env vars, first-login flow, and
verify step.
