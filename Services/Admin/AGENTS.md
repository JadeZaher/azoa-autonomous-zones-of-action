# Services/Admin — operator:admin bootstrap (H2)

## Why

`AvatarManager.GenerateJwt` emitted no role/scope claim, so there was no way to
mint the first `operator:admin` principal (`Core/AzoaScopes.cs:33`) — the
`Operator` authorization policy in `Program.cs` requires it (or the legacy
`role=Admin`/`is_admin` claim), but nothing could ever produce it for a fresh
deploy. NODE-HOST §8.9 documented "configure your JWT issuer" with no concrete
mechanism.

## Mechanism

`AdminBootstrapOptions` (config section `AdminBootstrap`) holds two values, both
env-driven and both required together:

- `AdminBootstrap:SeedEmail` — the email of the avatar to promote.
- `AdminBootstrap:SeedSecret` — a shared secret proving operator intent.

`AvatarManager.ResolveBootstrapAuthorityAsync` runs before every JWT mint. An
unbound seed avatar must match `SeedEmail` and present the exact secret; the
first successful proof creates a record-id binding. Only that bound avatar gets
`scope=operator:admin`, `scope=node:govern`, and the Admin role on later JWTs.

**Fail-closed by construction:** either config value absent leaves every login
ordinary; missing or wrong proof leaves an unbound seed avatar ordinary; a
binding owned by another avatar leaves the caller ordinary; and state-store
failure prevents scope stamping. The bootstrap secret is never persisted.

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

## One-time identity binding

The bootstrap is no longer stateless: first login for `SeedEmail` must present
the configured `BootstrapSecret`, which is compared in constant time and never
persisted. `admin_bootstrap_state:local` then binds the bootstrap to that avatar
record id. Only the bound id receives `operator:admin` and `node:govern` on
later logins; profile edits cannot claim the configured seed email.
