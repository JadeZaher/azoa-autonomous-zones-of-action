# Tenant Onboarding — Plan

Source spec: [spec.md](spec.md)
Onboarding runbook (authored in Phase 5): [ONBOARDING.md](ONBOARDING.md)

## Decisions to record before starting

### D1 — Tenant representation: parallel entity vs. scope+FK

The genuinely new design choice. Two valid approaches; **recommendation: Option B**.

| | **Option A — `Tenant` entity** | **Option B — scope + `OwnerTenantId` FK (RECOMMENDED)** |
|---|---|---|
| **Shape** | New `tenant` table + `Tenant` aggregate; tenant has its own API keys via a `Tenant→ApiKey` edge; Avatar gains `TenantId` FK to the tenant table. | No new table. A tenant *is* an Avatar that owns an API key carrying the `tenant:provision` scope. Avatar gains a nullable `OwnerTenantId` self-FK (`record<avatar>`). |
| **New persistence** | New `tenant` SCHEMAFULL table + POCO + store + DI; second FK target. | One nullable column + one composite index on the existing `avatar` table. Reuses `IApiKeyStore` / `IAvatarStore` as-is. |
| **Auth changes** | New principal type; `ApiKeyAuthenticationHandler.cs:72-79` must learn to emit a `TenantId` claim distinct from `AvatarId`. | **Zero** auth-handler change — `AvatarId` claim already identifies the tenant principal; `scope` claims already flow (`ApiKeyAuthenticationHandler.cs:81-87`). |
| **IDOR reuse** | New guard shape (tenant-vs-tenant) to author from scratch. | Direct extension of the existing route-id-vs-identity guard (`AvatarManager.cs:76,96`) — assert `child.OwnerTenantId == claimTenantId`. |
| **Cost** | Higher: a whole aggregate + store + tests, plus a migration to backfill `TenantId` if any avatar ever becomes a tenant. | Minimal: additive nullable column; every existing avatar is a valid `OwnerTenantId == null` row by construction (greenfield, no backfill). |
| **Ceiling** | Natural home for per-tenant config (billing plan, quotas, branding). | Per-tenant config has no home yet — would need a follow-up `tenant_profile` holon when fiat/quotas arrive. |

**Decision: Option B.** It reuses the strong, already-shipped ApiKey infra
(`ApiKeyAuthenticationHandler.cs`, `ApiKeyController.cs`) instead of standing up a
parallel principal type and a second auth claim. The tenant principal is just an
Avatar with a `tenant:provision` key; ownership is one nullable self-FK; isolation
is the existing IDOR guard extended by one equality check. Greenfield + no
customers (project memory `greenfield-prelaunch-no-compat`) means the additive
column needs no migration. **Trade-off accepted:** when `fiat-stripe-bridge` needs
per-tenant config (billing plan, quotas), that lands as a separate
`tenant_profile` record keyed by the tenant's avatar id — Option B does not block
it, it just defers it. Re-open D1 → Option A only if per-tenant config proliferates
beyond what a keyed side-record cleanly holds.

### D2 — Acting "as a child": credential shape

| Decision required |
|-------------------|
| **Issue a short-lived child JWT, reusing `AvatarManager`'s existing symmetric signer** (`AvatarManager.cs:102-124`), with `sub = childAvatarId` and the delegated scopes as `scope` claims. **Rejected alt:** minting a per-child ephemeral *ApiKey* row — that pollutes the `api_key` table with throwaway rows and needs revocation lifecycle. A JWT is stateless, already understood by the auth pipeline (JWT or ApiKey, `Program.cs` MultiScheme), expires on its own, and the subject swap is exactly what `GenerateJwt` already does. v1 keeps the existing 24h symmetric signing (shorten TTL for child tokens). Re-open if/when child credentials need independent revocation before expiry. |

### D3 — Scope-enforcement mechanism

| Decision required |
|-------------------|
| **ASP.NET authorization policy `TenantScope` + a `ClaimsPrincipal.HasScope(string)` extension.** The handler already emits one `scope` claim per CSV entry (`ApiKeyAuthenticationHandler.cs:81-87`); the policy checks for `tenant:provision`. `HasScope` is the single reusable consuming helper (used by the policy and by the credential-issuer that decides delegated scopes). **Rejected alt:** ad-hoc `User.FindAll("scope")` checks scattered per-action — duplicates logic and drifts. One helper, one policy. |

---

## Phase 1 — Model + persistence (additive)

- [ ] **Avatar aggregate** — `Models/Avatar.cs:5-22`: add `Guid? OwnerTenantId`,
      `string? ExternalUserId`, `string? ExternalRef`.
- [ ] **Avatar POCO** — `Persistence/SurrealDb/Models/Avatar.cs`: add three columns
      after `Level` (Order 14-16): `owner_tenant_id` (`option<record<avatar>>`,
      `[References(typeof(Avatar))]`), `external_user_id` (`option<string>`),
      `external_ref` (`option<string>`). Add an `[Index("avatar_owner_tenant",
      Fields = new[] { "owner_tenant_id" })]` and a **composite unique**
      `[Index("avatar_tenant_extuser", Fields = new[] { "owner_tenant_id",
      "external_user_id" }, Unique = true)]` (mirrors the index style at
      `Persistence/SurrealDb/Models/ApiKey.cs:20-21`).
      Note the SurrealDB NULL-collision caveat the ApiKey POCO documents
      (`ApiKey.cs:17`): the composite unique is only meaningful for rows where
      both fields are non-NONE — verify a tenant-managed row always sets both.
- [ ] **Store mapping** — extend the avatar SurrealDB store's `FromDomain`/`ToDomain`
      to round-trip the three new fields (`owner_tenant_id` via
      `SurrealLink.ToLink("avatar", …)` like `SurrealApiKeyStore.cs:179`).
- [ ] **`IAvatarStore`** — add `Task<AZOAResult<IEnumerable<IAvatar>>>
      ListByOwnerTenantAsync(Guid tenantId, CancellationToken ct)` and
      `Task<AZOAResult<IAvatar>> GetByTenantAndExternalUserAsync(Guid tenantId,
      string externalUserId, CancellationToken ct)` to
      `Interfaces/Stores/IAvatarStore.cs:6-19`, scoped queries that return the
      owner's rows only (mirror `SurrealApiKeyStore.ListByAvatarAsync:59-69`).

## Phase 2 — Scope vocabulary + enforcement

- [ ] **`HasScope` helper** — add `ClaimsPrincipal.HasScope(string scope)` extension
      (new `Core/ClaimsPrincipalExtensions.cs`) reading the `scope` claims emitted
      at `ApiKeyAuthenticationHandler.cs:81-87`.
- [ ] **`TenantScope` policy** — register an authorization policy in `Program.cs`
      requiring `tenant:provision`; apply `[Authorize(Policy="TenantScope")]` on
      `TenantController`.
- [ ] **Scope constants** — central `static class AzoaScopes` with
      `TenantProvision`, `WalletManage`, `NftMint` (no magic strings).
- [ ] **Delegation rule** — child-credential issuance intersects requested scopes
      with the tenant key's own scopes (no escalation); covered by unit test.

## Phase 3 — TenantManager + TenantController

- [ ] **`ITenantManager` / `TenantManager`** — `Managers/TenantManager.cs`:
  - `ProvisionChildAsync(Guid tenantId, ProvisionChildModel model, ct)` — sets
    `OwnerTenantId = tenantId` **from the parameter, never the model** (IDOR rule,
    mirrors `AvatarManager.UpdateAsync:72,76`); idempotent on
    `(tenantId, externalUserId)` via `GetByTenantAndExternalUserAsync`.
  - `ListChildrenAsync(Guid tenantId, string? externalUserId, ct)`.
  - `ResolveChildAsync(Guid tenantId, string externalUserId, ct)`.
  - `IssueChildCredentialAsync(Guid tenantId, Guid childId, scopes, ct)` — loads
    child, asserts `child.OwnerTenantId == tenantId` else returns a not-found
    result (the §3 crux); reuses the JWT generator pattern from
    `AvatarManager.cs:102-124` (refactor `GenerateJwt` into a shared signer or
    duplicate the minimal primitive — record which in the commit).
- [ ] **`TenantController`** — `Controllers/TenantController.cs`, route `api/tenant`,
      `[Authorize(Policy="TenantScope")]`. Tenant id from
      `User.FindFirst("AvatarId")` (pattern at `AvatarController.cs:81-86`):
  - `POST /avatars` → `ProvisionChildAsync`
  - `GET /avatars` (+ `?externalUserId=`) → `ListChildrenAsync`
  - `GET /avatars/{externalUserId}` → `ResolveChildAsync`
  - `POST /avatars/{id}/credential` → `IssueChildCredentialAsync`
  - Cross-tenant / unowned target → **404** (not 403), per
    `SurrealApiKeyStore.cs:84-89` precedent.
- [ ] **DI** — register `ITenantManager → TenantManager` in `Program.cs` alongside
      the other managers.

## Phase 4 — Tests

- [ ] **Unit** (`tests/AZOA.WebAPI.Tests/Managers/TenantManagerTests.cs`):
      ownership guard (own vs. other vs. null `OwnerTenantId`), claim-sourced tenant
      id ignores body, scope-delegation intersection, provision idempotency.
- [ ] **Integration** (`tests/AZOA.WebAPI.IntegrationTests/...`): acceptance (a)
      provision, (b) child wallet manage, (c) **cross-tenant rejection → 404**,
      (d) missing-scope → 403 + child-scope ceiling, (e) external-user resolve +
      duplicate-provision idempotency.
      Note the open harness dependency: per-test SurrealDB namespace isolation
      (project memory `integration-test-namespace-isolation` /
      `open-decisions-2026-06-12`) — if the integration host still can't see the
      per-test namespace, mark these `[Fact(Skip=...)]` referencing that follow-up
      rather than shipping red, and prove (c) at the manager/unit layer too.

## Phase 5 — Onboarding doc

- [ ] Author `conductor/tracks/tenant-onboarding/ONBOARDING.md` per spec §5
      (register → mint tenant key → provision children → issue child credential →
      resolve by external id). Verify each step against the actually-shipped route
      names before sign-off.

## Phase 6 — Verification gates

- [ ] `dotnet build` from repo root — **0 warnings** (nullable enabled),
      `conductor/workflow.md` Quality Gate 1.
- [ ] `dotnet test` — green (new unit + integration pass, or integration skipped
      with the namespace-isolation reference and the guard proven at unit level).
- [ ] Swagger UI lists the four `api/tenant` endpoints (Quality Gate 3).
- [ ] Confirm no tenant id is read from any request body anywhere in
      `TenantController` / `TenantManager` (grep for body-sourced tenant id).
- [ ] Confirm cross-tenant access returns 404, never 403 or a leaked row.
- [ ] `ONBOARDING.md` steps verified against shipped routes.
- [ ] Move `conductor/tracks.md` row for `tenant-onboarding` from `[ ]` to `[x]`
      in the Shipped section.

## Commit strategy

One commit per phase, `[tenant-onboarding] <verb> <subject>`:

- `[tenant-onboarding] add OwnerTenantId + ExternalUserId to avatar model`
- `[tenant-onboarding] add tenant scope vocabulary and HasScope policy`
- `[tenant-onboarding] add TenantManager + TenantController provisioning surface`
- `[tenant-onboarding] cover provisioning, cross-tenant isolation, scope ceiling`
- `[tenant-onboarding] document ArdaNova tenant onboarding runbook`

## Success criteria

The five acceptance tests (a)-(e) in [spec.md](spec.md) pass, `dotnet build` is
zero-warning, and a tenant can provision and act for its own users while being
provably unable to touch another tenant's avatars or wallets.
