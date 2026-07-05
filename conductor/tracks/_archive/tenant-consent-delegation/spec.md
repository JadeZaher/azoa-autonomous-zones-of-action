# Track: tenant-consent-delegation

## Overview

Strip the tenant (ArdaNova) of **blanket authority** over its users. Today a tenant
can mint a short-lived child JWT for **any** avatar it owns, **with no per-user
consent and no revocation** ([Managers/TenantManager.cs](../../../Managers/TenantManager.cs)
`IssueChildCredentialAsync` — the only check is `child.OwnerTenantId == tenantId`).
This is the 3rd-party custody role the user wants removed.

This track adds a **user-granted, revocable consent model**: a tenant may act for a
user **only** within scopes the user has granted, and the user can revoke at any
time. Combined with [[user-sovereign-identity]] (self-owned avatars + own login),
this makes the tenant a **driver the user authorizes**, not a custodian.

**Decisions (2026-06-21):**
- **Custody stays AZOA-side, user-authorized** — keys remain AZOA-custodied
  (`Platform` wallets); a value/signing action on a user's wallet requires a live
  consent grant. The tenant *triggers*; AZOA *signs* only within granted scope.
- **Consent is a PARTICIPATION-SCOPED STANDING GRANT, obtained with no crypto UX.**
  ArdaNova's brand hides blockchain entirely — users never see wallet signatures or
  a "consent screen." So **joining ArdaNova / a project IS the consent act**: it
  creates a standing AZOA `ConsentGrant` covering the needed scopes for the
  *duration of participation*. Offboarding / leaving revokes it. ArdaNova surfaces it
  in plain domain language ("manage your membership assets"); AZOA owns the record.
- **Revocation logic LIVES IN AZOA** (source of truth + enforcement) but is
  **bridgeable for orchestration**: ArdaNova reads/triggers grant + revoke + status
  via REST AND subscribes to **webhook events** (`consent.granted` / `.revoked` /
  `.expired`) so its no-blockchain orchestration reacts in real time. AZOA *decides*
  validity; ArdaNova *orchestrates around* it — it never owns the decision.

## Design

### 1. Consent grant model (new)
A durable grant record: the user authorizes a tenant for specific scopes, optionally
time-boxed, always revocable.
```csharp
public sealed class ConsentGrant
{
    public Guid Id { get; set; }
    public Guid GrantorAvatarId { get; set; }   // the USER (must own the avatar)
    public Guid TenantId { get; set; }           // the tenant being authorized
    public List<string> Scopes { get; set; }     // e.g. quest:execute, swap:sign, nft:mint
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }      // null = until revoked
    public DateTime? RevokedAt { get; set; }      // set on revoke; grant inert after
    public GrantOrigin Origin { get; set; }       // UserExplicit | Participation
    public string? ParticipationRef { get; set; } // opaque ArdaNova participation id
}

public enum GrantOrigin { UserExplicit, Participation }
```
**Participation-scoped grant lifecycle (no crypto UX):**
- `GrantOrigin` on the record distinguishes `UserExplicit` from `Participation`
  (the join-driven standing grant). `Participation` grants are created when the user
  joins ArdaNova / a project — see the bridge surface below — and are tied to a
  participation reference (`ParticipationRef`, an opaque ArdaNova id) so offboarding
  can revoke precisely.

**A `Participation` grant MUST be bound to a user-authenticated act (H4 — anti-forgery).**
The join flow hides crypto, but the grant cannot be fabricated by the tenant alone. At
join, the **user** authenticates (self-signup login JWT OR wallet-challenge JWT from
[[user-sovereign-identity]]); that user-issued credential is what authorizes the grant.
The tenant API-key principal **CANNOT** create a grant naming a `GrantorAvatarId` it
lacks a user-authenticated assertion for — otherwise "joining = consent" degenerates
into the tenant self-issuing blanket authority under a consent label (the exact thing
this track removes). Practically: the grant call is authed as the USER (the user's
token), even though the surrounding join UX is ArdaNova-branded and crypto-free.

**Scope minimization for the no-UX standing grant (H4).** A `Participation` grant
carries only the **minimum** scopes (e.g. `quest:execute`, read scopes). **Value-moving
/ signing scopes (`swap:sign`, transfer/grant/mint signing) are EXCLUDED from the
standing participation grant** and require a `UserExplicit` grant — a value action gets
a deliberate, user-authenticated authorization, not a side effect of joining.

**Endpoints:**
- `POST /api/avatar/consent` (**authed as the USER** — the user's own token, never the
  tenant API-key alone) — grant a tenant scopes. The ArdaNova join flow surfaces this in
  plain-language terms; the user's token is what signs the request, no wallet prompt.
- `DELETE /api/avatar/consent/{id}` (authed as the USER) — revoke. Offboarding revokes
  by `(tenantId, ParticipationRef)` **exact-match within the tenant's own grants only**
  (L3 — no cross-tenant ref collision, no loose match).
- `GET /api/avatar/consent` — user-scoped list of the user's own grants.
- `GET /api/tenant/consent` — **separate** tenant-scoped endpoint (L2): a tenant lists
  ONLY grants made to itself; a principal-type assertion guarantees a tenant can never
  receive another tenant's or a non-grantee's grants.

### 2. Enforce consent at credential issuance AND at the single signing chokepoint

**(a) Credential issuance.** Every avatar is self-owned after the
[[user-sovereign-identity]] hard cutover, so `IssueChildCredentialAsync` ALWAYS
requires a **live ConsentGrant** (grantor=user, tenant=caller, scope ⊇ requested, not
expired/revoked) — else NotFound (isolation crux). **There is NO `OwnerTenantId ==
tenant` ownership-only issuance path (M2):** the legacy provision-and-lock path is
removed by the cutover, so no credential is ever issued on ownership alone. Delete the
old "legacy children keep working" notion — it reopened consent-free custody.

**(b) Scope ceiling from server-trusted inputs (M3).** The issued child JWT's scopes =
**(tenant's authenticated scopes ∩ granted scopes ∩ requested)**. The tenant scopes
and tenant identity MUST be derived from the authenticated tenant principal
(API-key/JWT claims) **server-side**, never from a request body field. Granted scopes
are a hard ceiling no request field can widen.

**(c) The signing seam is structurally consent-blind today — this MUST be fixed (C1).**
The child JWT sets `Sub`/`AvatarId = childAvatarId` (the USER's avatar id —
[Managers/TenantManager.cs](../../../Managers/TenantManager.cs) `GenerateChildJwt`),
so at `KeyCustodyService.WithSigningKeyAsync(walletId, avatarId, sign)` a tenant-driven
action is **indistinguishable** from a user-driven one — its only check is
`wallet.AvatarId == avatarId`, which passes, and the consent grant is never consulted.
`SigningContext` carries only `(AvatarId, WalletId, IsPlatform)` — no tenant, no scope.
**Required rearchitecture:**
  - The child JWT MUST carry a distinguishing `act_as_tenant` claim (the `TenantId`).
  - `SigningContext` / `WithSigningKeyAsync` MUST be extended to carry that tenant
    identity + the requested signing scope.
  - When a `tenantId` is present, the seam does a **live** `ConsentGrant` lookup
    (grantor=avatarId, tenant=tenantId, scope ⊇ op, `RevokedAt == null` AND
    (`ExpiresAt == null` OR `now < ExpiresAt`)) **inside the call, before key decrypt**,
    failing closed. An avatar-id-only seam cannot satisfy revocation or consent.

**(d) Single chokepoint — enumerate EVERY sign path (C2).** The grant check lives at
ONE custody chokepoint inside `KeyCustodyService` (covering both `WithSigningKeyAsync`
AND every tenant-reachable caller of `WithPlatformSigningKeyAsync`), NOT scattered
"in some managers." Before merge, **exhaustively enumerate** every path that reaches a
provider sign and prove each fails closed without a grant: `AllocationManager`,
`FungibleTokenManager`, the bridge, swap, and each Tier-2 economic quest node
(`Swap`/`Grant`/`Transfer`/`Refund`/`FungibleTokenCreate`). No "or" — all of them.

### 3. Revocation is immediate — live re-check is MANDATORY (H3, M4)
- A revoked/expired grant fails the next credential issuance AND the next signing
  check. Already-issued child JWTs are short-lived (15 min) but **the TTL is NOT the
  cutoff** — the seam does a **live** grant validity check on **every** tenant-driven
  sign: `RevokedAt == null && (ExpiresAt == null || now < ExpiresAt) && scope ⊇ op`.
  An already-issued child JWT confers **no standing signing authority**; revocation and
  expiry take effect on the very next sign attempt, independent of webhook delivery and
  independent of any background job. (This is NOT optional.)
- Expiry is enforced by this live `now`-comparison at the seam, NOT by the
  `consent.expired` emitter — the webhook is notification only.

### 4. Bridge surface — AZOA owns it, ArdaNova orchestrates around it (NEW infra)
AZOA is the source of truth + enforcement for consent/revocation; ArdaNova's
no-blockchain orchestration must be able to *read* and *react* without owning the
decision.
- **REST (read/trigger):** the grant/revoke/list endpoints above + a status query
  `GET /api/avatar/consent/{id}` and a participation-scoped lookup
  `GET /api/tenant/consent?participationRef=…` (tenant-scoped). ArdaNova grants at
  join, revokes at offboard, queries status anytime.
- **Webhook events (react):** AZOA pushes `consent.granted` / `consent.revoked` /
  `consent.expired` (+ payload: grantId, avatarId, tenantId, scopes, participationRef,
  occurredAt) to a tenant-registered endpoint. This is **new outbound infra** — there
  is no webhook mechanism today (the saga **transactional outbox**,
  `Services/Sagas/SagaProcessor.cs` `EnqueueNextStepAsync`, is internal-only). Build
  the emitter ON that outbox pattern: write the event to an outbox table in the same
  transaction as the consent state change (no dual-write), then a delivery worker
  POSTs with retry + idempotency id. Hardening (H5):
  - **Replay-resistant signature:** HMAC over the body **including a signed
    `occurredAt`/timestamp**, with a receiver freshness window — a captured event
    cannot be replayed later to desync ArdaNova's view (e.g. resurrect a revoked grant).
  - **Per-tenant secret + rotation:** each tenant has its own webhook secret, rotatable;
    no shared secret.
  - **SSRF guard on the registered URL:** https-only, public-IP allowlist; block
    link-local / RFC1918 / cloud-metadata ranges so a registered callback can't reach
    AZOA-internal services.
  - **Strict tenant isolation:** a tenant receives ONLY its own consent events; webhook
    registration is scoped to the authenticated API-key principal.
- **Boundary:** AZOA *decides* validity (a revoked grant is dead the instant it's
  written, enforced at the signing seam regardless of event delivery); the webhook is
  an orchestration convenience, NOT the enforcement path. ArdaNova must never be able
  to override an AZOA revocation — it can only observe and react.

## Acceptance Criteria

- [x] AC1 — `ConsentGrant` model + SCHEMAFULL POCO (`consent_grant`); separate
      user-scoped (`ConsentController`, `GET /api/avatar/consent`) + tenant-scoped
      (`TenantConsentController`, `GET /api/tenant/consent`, `[Authorize(Policy="TenantScope")]`)
      lists; store queries are grantor/tenant-scoped (L2). Tests in `ConsentManagerTests`.
- [x] AC2 — `IssueChildCredentialAsync` requires a **live** grant; **no ownership-only
      path** (the `OwnerTenantId == tenant` check is GONE). Tests:
      `IssueChildCredential_NoGrant_ReturnsNotFound`, `…_OwnershipAlone_IsNotEnough_M2`.
- [x] AC3 — Scope ceiling = (server-trusted tenant scopes ∩ live grant scopes ∩
      requested); no body field can widen it. Test:
      `IssueChildCredential_ScopeCeiling_IsTenantIntersectGrantIntersectRequested_M3`.
- [x] **AC4 (CRITICAL)** — Signing seam carries tenant identity + scope and does a LIVE
      grant check before key decrypt. → `SigningContext` extended with
      `(ActingTenantId, GrantorAvatarId, Scope)`; `KeyCustodyService.WithSigningKeyAsync(ctx)`
      + `WithPlatformSigningKeyAsync(bool, ctx)` consult `ITenantConsentGate` BEFORE
      decrypt; `act_as_tenant` JWT claim from `IssueChildCredentialAsync`. Test:
      `WithSigningKey_TenantDriven_NoGrant_Rejected_EvenThoughOwnershipMatches` (the C1 proof).
- [x] **AC4b (CRITICAL)** — Single chokepoint, every path gated. → ALL signing converges
      on `KeyCustodyService` (the only callers of both methods are in
      `AlgorandProvider.SignWithCustodyAsync`, fed by `SigningContext`). Enumeration with
      fail-closed coverage:
      | Path | Custody method | Gated via |
      |---|---|---|
      | AllocationManager Mint | platform | `BuildSigningContext(platformOp)` + acting-tenant |
      | AllocationManager Transfer | user | `BuildSigningContext` + acting-tenant |
      | FungibleTokenManager Create (ASA) | platform | `CreateASAAsync(…, SigningContext)` overload |
      | Quest Grant (mint) | platform | `QuestRun.ActingTenantId`→`NftManager.MintAsync` |
      | Quest Transfer / Refund | user | `QuestRun.ActingTenantId`→`NftManager.TransferAsync` |
      | Quest FungibleTokenCreate | platform | `QuestRun.ActingTenantId`→`FungibleTokenManager` |
      | Bridge | per-op | `BlockchainOperation.ActingTenantId`→`BuildSigningContext` |
      | Swap | **UNSIGNED** (client-side) | no server sign path to gate (documented) |
      Tests: `KeyCustodyConsentSeamTests` (both seam methods, tenant-driven deny + user/
      platform allow), `TenantConsentGateTests` (fail-closed: no-grant/revoked/expired/
      lookup-error/lookup-throw → deny).
- [x] AC5 — Revocation/expiry is the LIVE per-sign re-check (NOT TTL). → `ConsentGrant.
      IsLiveAt(now)`/`Covers(scope,now)` evaluated at the seam on every tenant-driven sign;
      store `FindCoveringGrantAsync` filters `revoked_at = NONE AND (expires_at = NONE OR
      expires_at > now)`. Tests: `GrantActLiveRevoke_ImmediatelyDeniesNextCheck_AC5`,
      `IssueChildCredential_RevokedGrant_Denied_AC5`.
- [x] AC6 — Participation grant authed as the USER's token; value-signing scopes EXCLUDED
      (require `UserExplicit`); offboard revokes by `(tenantId, ParticipationRef)`
      exact-match within the tenant's grants only. → `ConsentManager.GrantParticipationAsync`
      (H4 exclusion via `AzoaScopes.ValueSigningScopes`), `RevokeByParticipationAsync` (L3),
      `ConsentController` authes as USER; `TenantConsentController` has NO create path.
      Tests: `Grant_Participation_WithValueScope_Rejected_H4`, `RevokeByParticipation_…_L3`.
- [x] AC7 — Webhook bridge (new outbound infra): outbox event enqueued in the same logical
      flow as the grant/revoke (`ConsentManager` → `IConsentWebhookEmitter.EmitAsync`);
      `ConsentWebhookDeliveryWorker` (hosted) POSTs with retry + idempotency id +
      timestamped HMAC (`WebhookHmacSigner`); per-tenant rotatable secret
      (`WebhookRegistration`); SSRF guard (`WebhookSsrfGuard`, https-only + public-IP
      allowlist + DNS-rebinding defence); strict per-tenant isolation. Tests:
      `ConsentWebhookSecurityTests` (SSRF block-list, DNS-rebind, timestamped/per-tenant HMAC).
- [x] AC8 — Boundary: webhook is observe-only; it never writes ConsentGrant. The signing
      seam is the enforcement path (a revoked grant is dead the instant `RevokedAt` is
      written, independent of delivery).
- [x] AC9 — IDOR: grantor is always the authenticated user; tenant-in-body ignored;
      cross-user/cross-tenant probes → NotFound. Test:
      `Revoke_CrossUser_ReturnsNotFound_NotForbidden_AC9`.
- [x] AC10 — Audit (L1): every grant, revoke, and tenant-driven sign writes an immutable
      `consent_audit` row (`ConsentManager` + `TenantConsentGate`), independent of the
      best-effort webhook. Tests assert `Granted`/`Revoked`/`TenantSignAllowed`/
      `TenantSignDenied` rows.
- [~] AC11 — Security review: **PENDING a separate adversarial pass — the custody seam
      MUST NOT be self-approved** (see track-1 SECURITY-REVIEW note). `dotnet build`:
      0 errors, 28 warnings = baseline, 0 new. Unit tests: 914 pass (2 pre-existing
      unrelated failures).

## Dependencies

- **[[user-sovereign-identity]]** — consent is granted BY a self-owned user; needs
  the claim flow + user auth to exist first (a tenant-locked child can't consent).
- Touches the same custody/auth surface as [[bridge-unsafe-pre-launch]] /
  custody-key-management — coordinate the signing-seam change with those.

## Out of scope

- Per-action (vs per-scope) interactive consent prompts — participation + scope
  standing grants v1.
- Client-side non-custodial signing (custody stays AZOA-side per decision).
- Delegation chains (tenant sub-delegating to another tenant).
- A general-purpose webhook framework for all AZOA events — this track ships ONLY
  the consent events; generalizing the outbound emitter is a follow-up (it may later
  serve the `Emit` quest node, bridge status, etc.).

## Adversarial review applied (2026-06-21)

A hostile security pass found that the signing seam is structurally consent-blind
(the child JWT 'Sub' = the user's avatar id, so a tenant action is indistinguishable
from a user action) — without the C1 rearchitecture the whole consent model is theater.
Folded in: tenant identity + scope on `SigningContext` + a LIVE per-sign grant check
before decrypt (C1); single chokepoint enumerating every sign path incl.
`WithPlatformSigningKeyAsync` (C2); mandatory (not optional) revocation re-check (H3);
live expiry at the seam (M4); participation grant bound to a user-authenticated
assertion + value scopes excluded (H4); webhook SSRF/timestamped-HMAC/per-tenant
isolation (H5); removal of the legacy ownership-only issuance path (M2); server-trusted
scope ceiling (M3); audit rows (L1); split user/tenant list endpoints (L2); exact-match
revoke-by-ref (L3).
