# Tenant Onboarding — Specification

## Goal

Make AZOA multi-tenant so that an external application — **ArdaNova** first, any
future app after — can act as a **tenant** that provisions and manages a fleet of
AZOA Avatars on behalf of *its own* users. Each ArdaNova user maps 1:1 to an
AZOA Avatar; ArdaNova authenticates to AZOA as a tenant principal via an API
key, never as the end user. The tenant can create child avatars, look them up by
its own user id, and act on each child's wallets/NFTs — but **only** its own
children, never another tenant's.

This is a design-and-enable track: it adds the tenant principal concept, the
tenant-scoped provisioning surface, and the cross-tenant isolation guarantee. It
does **not** add fiat, billing, or app-specific business logic — those sit on top
of this.

## Background

API-key authentication already exists and is strong, but it is **single-avatar**:
an API key is owned by exactly one Avatar and every request it authenticates is
scoped to *that* Avatar.

- `Core/ApiKeyAuthenticationHandler.cs:46` looks the key up by SHA-256 hash;
  `:52-60` enforce active/revoked/expiry; `:72-79` emit the identity claims
  (`NameIdentifier`/`sub`/`AvatarId` all set to `apiKey.AvatarId`, plus `ApiKeyId`
  and `AuthMethod=ApiKey`); `:81-87` split `apiKey.Scopes` (CSV) into one `scope`
  claim per entry. **The authenticated identity is hardwired to the key's single
  owner avatar.**
- `Models/ApiKey.cs:6` is the `AvatarId` FK; `:29` is the `Scopes` CSV field
  ("Empty means full access"). The SurrealDB POCO mirror is
  `Persistence/SurrealDb/Models/ApiKey.cs:36-37` (`avatar_id`, `[References(typeof(Avatar))]`)
  and `:81-84` (`scopes` `option<string>`).
- `Controllers/ApiKeyController.cs:36` pins every created key to the caller's own
  `GetAvatarId()` (`:22-28`); list/revoke/delete (`:84`, `:112`, `:129`) are all
  scoped to that same avatar.
- `Persistence/SurrealDb/Models/Avatar.cs:20-83` is the avatar table — there is
  **no** field linking an avatar to an owning tenant, and **no** field storing an
  external application's user id.

There is no concept of a **tenant** avatar that owns *many* downstream user
avatars, and no authorization path by which one principal may act on another
avatar. That is exactly the gap this track closes.

### Existing patterns this track reuses (not reinvents)

- **IDOR guard (route-id-vs-identity).** `Managers/AvatarManager.cs:76` and `:96`
  reject any op where `id != avatarId`; `Controllers/AvatarController.cs:61-64`
  passes the claims-derived avatar id into the manager rather than trusting the
  body. The tenant authorization check extends this exact shape (see §3).
- **Owner-scoped store lookups that return "not found", not "forbidden".**
  `Interfaces/Stores/IApiKeyStore.cs:39` (`GetByIdForAvatarAsync`) and
  `Providers/Stores/Surreal/SurrealApiKeyStore.cs:84-89` deliberately surface a
  cross-owner row as `null` so a prober cannot distinguish "no such record" from
  "exists but not yours". The tenant store queries mirror this.
- **Scope claims already flow.** The handler already emits `scope` claims from the
  CSV (`ApiKeyAuthenticationHandler.cs:81-87`); this track defines the *vocabulary*
  and the *enforcement point*, not the plumbing.
- **Context-sourced identity, never request-sourced.** `Mcp/Tools/AvatarScopedQueryTool.cs:133-143`
  sources `avatar_id` exclusively from `ToolCallContext.AvatarId` and never from a
  tool argument — the same rule governs the tenant id (always from the
  authenticated key, never from the request body).

## Design overview

A **tenant** is a principal that owns a fleet of child Avatars. The chosen
representation (see `plan.md` decisions table — recommendation is **Option B**) is
the *minimal* one that reuses the existing ApiKey + Avatar infrastructure:

1. **Tenant designation = a scope on an API key.** A key whose `Scopes` CSV
   contains `tenant:provision` is a *tenant key*. The avatar that owns the key is
   the *tenant principal*. No parallel `Tenant` entity, no new auth scheme.
2. **Ownership edge = `OwnerTenantId` on Avatar.** A nullable FK on the avatar
   record. When a tenant provisions a child, the child's `OwnerTenantId` is set to
   the tenant principal's avatar id. A `null` `OwnerTenantId` means "not a
   tenant-managed avatar" (every avatar that exists today).
3. **External mapping = `ExternalUserId` (+ `ExternalRef`) on Avatar.** The
   tenant's *own* user id for this child, stored so ArdaNova can resolve
   `ArdaNova userId → AZOA AvatarId` by querying its own identifier. Unique
   **per tenant**, not globally (two tenants may each have a user `"42"`).
4. **Tenant-scoped surface = a new `TenantController`** under `api/tenant`,
   authorized by the `tenant:provision` scope, exposing provision / list /
   issue-child-credential. Every operation derives the tenant id from the
   authenticated key's `AvatarId` claim — never from the request body.
5. **Acting for a child** = the tenant requests a short-lived, child-scoped
   credential (a child JWT or a child-scoped ephemeral key) whose subject is the
   *child* avatar. Downstream wallet/NFT calls then run under the child's identity
   through the existing per-avatar authorization, with the tenant guard asserting
   `child.OwnerTenantId == tenant` at issuance time.

## Scope

### 1. Tenant principal model

- [ ] Add `OwnerTenantId` (`Guid?`) to `Models/Avatar.cs:5-22` and the SurrealDB
      POCO `Persistence/SurrealDb/Models/Avatar.cs` as a new
      `option<record<avatar>>` column with a `[References(typeof(Avatar))]` self-FK
      and an index for `owner_tenant_id` lookups (mirrors the `api_key_by_avatar`
      index at `Persistence/SurrealDb/Models/ApiKey.cs:21`).
- [ ] Add `ExternalUserId` (`string?`) and `ExternalRef` (`string?`) to both
      `Avatar` models. `ExternalUserId` is the tenant's own user id; `ExternalRef`
      is a free opaque string (e.g. ArdaNova org/realm). Add a **composite unique
      index** on `(owner_tenant_id, external_user_id)` so a given tenant cannot
      provision two avatars for the same external user, while two different tenants
      may share an `external_user_id` value.
- [ ] Define a tenant principal as "an avatar that owns an active API key carrying
      the `tenant:provision` scope". No new entity, no new table.

### 2. Tenant-scoped provisioning surface (`TenantController`, route `api/tenant`)

- [ ] `POST /api/tenant/avatars` — provision a new child avatar under the
      authenticated tenant. Request carries `externalUserId` (+ optional
      `externalRef`, username/email seed). The handler sets the new avatar's
      `OwnerTenantId` to the tenant principal's avatar id **from the claim**,
      ignoring any tenant id in the body (IDOR rule). Returns the new `AvatarId`
      and the `externalUserId` echo. Idempotent on `(tenant, externalUserId)`:
      a repeat provision for the same external user returns the existing child,
      not a duplicate.
- [ ] `GET /api/tenant/avatars` — list the tenant's child avatars (filterable by
      `externalUserId` for the `ArdaNova userId → AvatarId` lookup). Scoped to the
      tenant; never returns another tenant's children.
- [ ] `GET /api/tenant/avatars/{externalUserId}` — resolve one child by the
      tenant's own user id (the primary ArdaNova lookup path).
- [ ] `POST /api/tenant/avatars/{id}/credential` — issue a short-lived
      child-scoped credential the tenant can use to act *as* that child for
      wallet/NFT operations. Asserts `child.OwnerTenantId == tenant` before
      issuing (the security crux). The issued credential's subject is the child
      avatar id; it carries only the scopes the tenant is permitted to delegate
      (see §4). Reuse `AvatarManager`'s JWT generator
      (`Managers/AvatarManager.cs:102-124`) as the issuance primitive rather than
      minting a fresh signing path.

### 3. Authorization model (the security crux)

Threat model: **a tenant must never reach another tenant's avatars or wallets**,
and a non-tenant key must never reach the tenant surface at all.

- [ ] **Tenant-surface gate.** Every `TenantController` action requires the
      `tenant:provision` scope (enforced via an `[Authorize(Policy="TenantScope")]`
      policy or an explicit scope check mirroring
      `ApiKeyAuthenticationHandler.cs:81-87`'s claim emission). A key without the
      scope is rejected `403`, not `401`.
- [ ] **Child-ownership guard.** Any operation targeting a specific child
      (`/credential`, future per-child mutations) loads the child and asserts
      `child.OwnerTenantId == authenticatedTenantId`. On mismatch (or
      `OwnerTenantId == null`), return **404** — surface as "not found", not
      "forbidden", per the `GetByIdForAvatarAsync` precedent
      (`SurrealApiKeyStore.cs:84-89`) so a prober cannot enumerate other tenants'
      avatars.
- [ ] **Tenant id is claim-sourced only.** The tenant id is always
      `User.FindFirst("AvatarId")` from the authenticated key (the pattern at
      `AvatarController.cs:81-86` / `ApiKeyController.cs:22-28`). A tenant id in any
      request body is ignored. New manager methods take the tenant id as an
      explicit parameter the controller fills from the claim — never from the
      model — exactly as `AvatarManager.UpdateAsync(id, model, avatarId)` does
      (`AvatarManager.cs:72`).
- [ ] **Cross-tenant rejection is a first-class test** (see Acceptance c).

### 4. Scope vocabulary

Layered on the existing CSV `ApiKey.Scopes` (`Models/ApiKey.cs:29`) — no schema
change to the scope storage, only a defined vocabulary and an enforcement point.

- [ ] Define the v1 scope vocabulary:
  - `tenant:provision` — may create/list child avatars and issue child credentials.
  - `wallet:manage` — child credential may create/manage wallets for its avatar.
  - `nft:mint` — child credential may mint/transfer NFTs for its avatar.
  - (Absent scopes CSV still means "full access" for *non-tenant* legacy keys, per
    `Models/ApiKey.cs:26-28` — but a tenant key MUST carry `tenant:provision`
    explicitly; "empty = full" does **not** silently grant tenant powers.)
- [ ] Define the enforcement point: a single reusable scope-check helper (e.g.
      `ClaimsPrincipal.HasScope(string)`) used by the authorization policy and by
      the credential-issuance code that decides which scopes to delegate onto a
      child credential. The handler already emits `scope` claims
      (`ApiKeyAuthenticationHandler.cs:81-87`); this is the consuming side.
- [ ] A tenant may only delegate onto a child credential scopes that the tenant
      key itself holds (no privilege escalation through delegation).

### 5. Onboarding runbook (prose doc)

- [ ] Author `conductor/tracks/tenant-onboarding/ONBOARDING.md` — "How ArdaNova
      becomes an AZOA tenant", a step-by-step prose runbook:
  1. Register the tenant avatar (`POST /api/avatar/register`).
  2. Log in, mint a tenant API key with `Scopes="tenant:provision,wallet:manage,nft:mint"`
     via `POST /api/apikey` (`ApiKeyController.cs:33`).
  3. For each ArdaNova user: `POST /api/tenant/avatars` with that user's id as
     `externalUserId`; store the returned `AvatarId` against the ArdaNova user.
  4. To act for a user: `POST /api/tenant/avatars/{id}/credential`, then call
     wallet/NFT endpoints with the child credential.
  5. Lookup path: `GET /api/tenant/avatars/{externalUserId}` resolves
     `ArdaNova userId → AZOA AvatarId` without storing the mapping client-side.

## Acceptance criteria

- [ ] **(a) Provision** — A tenant key (`tenant:provision`) can
      `POST /api/tenant/avatars` and a child avatar is created with
      `OwnerTenantId == tenant` and the supplied `ExternalUserId`. Integration test.
- [ ] **(b) Manage child wallet** — Using a child credential issued via
      `/credential`, the tenant can create/list a wallet for that child avatar
      through the existing wallet surface. Integration test.
- [ ] **(c) Cross-tenant rejection** — Tenant T1 cannot provision-for, read, or
      issue a credential for an avatar owned by tenant T2 (or an unowned avatar);
      every such attempt returns 404. **This is the load-bearing security test.**
- [ ] **(d) Scope enforcement** — A key *without* `tenant:provision` is rejected
      `403` from every `TenantController` action; a child credential cannot exceed
      the scopes the tenant delegated. Tests for both.
- [ ] **(e) External mapping** — `GET /api/tenant/avatars/{externalUserId}`
      resolves to the correct child avatar; the composite unique index rejects a
      duplicate `(tenant, externalUserId)` provision and instead returns the
      existing child (idempotency). Test.
- [ ] Unit tests on the new manager method(s) covering the ownership guard,
      claim-sourced tenant id, and scope-delegation rules (≥70% per
      `conductor/workflow.md` coverage target).
- [ ] `dotnet build` — **zero warnings** (nullable enabled), per
      `conductor/workflow.md` Quality Gate 1.
- [ ] `dotnet test` green.
- [ ] Swagger lists the new `api/tenant` endpoints (Quality Gate 3).
- [ ] `ONBOARDING.md` runbook authored and accurate against the shipped routes.
- [ ] `conductor/tracks.md` row for this track moves to `[x]` Shipped.

## Out of scope

- Fiat / Stripe / billing logic — **`fiat-stripe-bridge` depends on this track**
  but is its own unit.
- A parallel `Tenant` entity / dedicated tenant table (Option A) — evaluated and
  rejected in `plan.md`; revisit only if per-tenant config outgrows a scope+FK.
- Tenant self-service UI / dashboard pages in `frontend/`.
- Per-child rate limiting, quotas, or usage metering.
- Rotating/JWKS-style signing for child credentials — v1 reuses the existing
  symmetric JWT primitive (`AvatarManager.cs:102-124`); harden later if needed.
- Frontend typecheck (per project memory `no-frontend-typecheck`).

## Tier

**Tier 0.5 — enabling.** Gates fiat and multi-app use. Independent to land but
unblocks downstream value flows.

## Dependencies

- **Hard:** none. Builds entirely on the existing ApiKey + Avatar infrastructure
  (`ApiKeyAuthenticationHandler.cs`, `ApiKeyController.cs`, `AvatarManager.cs`).
- **Downstream:** `fiat-stripe-bridge` depends on this track (a tenant principal
  is the natural owner of a fiat on-ramp acting for its users).
