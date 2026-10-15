---
type: spec
track: user-sovereign-identity
status: shipped
shipped: 2026-06-22
archived: 2026-07-05
review: done (commit 10e5dad, 2026-06-22)
---

# Track: user-sovereign-identity

## Overview

Let a tenant's (ArdaNova's) **end users own their own users** and authenticate
**independently of the tenant** — so the tenant is no longer a 3rd-party custodian
of its users' identities. Today a tenant-provisioned child user has **no login
path** (`TenantManager.ProvisionChildAsync` sets a random password hash —
[Managers/TenantManager.cs](../../../Managers/TenantManager.cs):84) and can *only*
act through tenant-issued child JWTs. This track gives the user a first-class,
self-sovereign identity.

**Decisions (2026-06-21):**
- Self-owned users + **external-wallet auth** (prove control of your own wallet by
  signing a challenge) is **the ONLY identity model**, alongside manual self-signup
  on ArdaNova (`POST /api/user/register`, already produces `OwnerTenantId = null`).
- **HARD CUTOVER** — drop tenant-provision-and-lock entirely. `ProvisionChildAsync`
  is **tests-only**, no seed/production data (confirmed pre-launch, greenfield —
  consistent with [[greenfield-prelaunch-no-compat]]). There is no dual-model period.
- A **claim** flow exists for the transitional case (a tenant created a user to
  smooth onboarding) → user takes ownership; but the *default and only durable* state
  is self-owned.

Pairs with [[tenant-consent-delegation]] (which strips the tenant's *blanket*
authority); this track gives the user the identity that consent is granted *from*.

## What exists / what's new

| Piece | Today | This track |
|-------|-------|-----------|
| Self-serve register/login | `UserManager.RegisterAsync`/`LoginAsync` (email+password+BCrypt) | reused |
| Tenant-provisioned child | `OwnerTenantId = tenant`, random password, no login | add a **claim** flow |
| External wallets | `WalletType.External` exists; backend returns unsigned tx | wallet-challenge **auth** |
| Wallet-signature login | — | **new**: challenge → sign → JWT |
| User ownership | `OwnerTenantId` (set by tenant, never cleared) | **new**: claim clears it to null |

## Design

### 1. Wallet-challenge authentication (self-sovereign login)
A user proves control of an `External` wallet to authenticate — no password held by
the tenant.
- `POST /api/user/auth/challenge { address, chainType }` → returns a one-time
  nonce to sign. The nonce is **bound to (address, chainType)**, has a short TTL
  (≤5 min), and is **single-use, consumed atomically at verify** (marked/deleted in
  the same transaction as a successful verify — no TOCTOU; two concurrent verifies of
  one nonce yield exactly one success). `challenge` is rate-limited per-IP and
  per-address, and user auto-creation is capped, to prevent nonce-flooding and
  storage-exhaustion spam (H1).
- **Domain-separated signed message (C4).** The bytes the user signs are NOT a bare
  nonce. They are a structured, human-readable message containing, all re-validated
  server-side on verify: a fixed domain prefix constant (e.g. `"AZOA-AUTH-v1"`), the
  AZOA issuer/audience, `chainType`, the `address`, the server nonce, and an expiry
  timestamp. Verify **rejects** the signature if ANY of {domain prefix, audience,
  chainType, address, nonce, expiry} does not match the server-issued challenge. This
  prevents replay across chains, addresses, AZOA instances, and other apps.
- `POST /api/user/auth/verify { address, chainType, signature }` → verify the
  signature against the address (reuse provider signature-verify; Algorand ed25519
  first), then issue the **same login JWT** `UserManager` issues today.
- **Binding is create-or-login ONLY; never auto-link to a pre-existing account (C3).**
  A successful verify either (a) **creates a brand-new self-owned user**
  (`OwnerTenantId = null`) for a never-seen `(address, chainType)`, or (b) **logs into
  the user already wallet-bound to that exact address**. It MUST NOT attach a wallet
  to, or take over, a user created by another auth method (email/password, tenant
  provision) — no matching on email/username/`ExternalUserId`. Linking a wallet to an
  *existing* account is a separate, **authenticated** action
  (`POST /api/user/wallet/link`, authed AS that account), never an unauthenticated
  verify.

### 2. Claim flow (tenant-provisioned → self-owned)
A user claims a user the tenant provisioned for them, severing tenant custody.
- `POST /api/user/claim` (authed by a tenant-issued child JWT OR a claim token)
  with a new credential (password OR a verified wallet challenge).
- On success: set the user's own `PasswordHash`/linked wallet, **set
  `OwnerTenantId = null`** (the IDOR-safe sever), record an audit row. After claim,
  the tenant can no longer mint child JWTs for this user (it owns nothing).
- **Post-claim custody-window cut (H2).** A child JWT minted *before* the claim is
  self-contained and valid up to 15 min. The claim MUST stamp a per-user
  `auth-not-before` watermark (or bump a `tokenVersion`); the signing seam and
  credential checks **reject any tenant-driven token issued before the claim**, so the
  tenant cannot sign on the just-liberated user during the residual window. (This is
  the same watermark the consent revocation re-check uses — see
  [[tenant-consent-delegation]].)
- Tenant-initiated claim invite: tenant generates a **single-use, short-TTL** claim
  link/token for its child (consumed atomically — same anti-TOCTOU rule as the auth
  nonce); the user completes it. The **credential-setting step is strictly user-side**:
  it requires a fresh user-controlled secret/signature (a wallet challenge or a
  user-chosen password) NOT derivable from any tenant-held child JWT. Tenant cannot set
  the credential (M1).
- **No claim-back / shadow re-provision (M1).** After a user is claimed, a tenant
  MUST NOT be able to re-provision a new tenant-locked user for the same
  `ExternalUserId` (the hard cutover below makes provision-and-lock impossible anyway).

### 3. Self-owned users are the only model (hard cutover)
- `OwnerTenantId = null` is the durable state for ALL users. Users authenticate
  directly (self-signup password OR wallet challenge) and authorize tenants
  explicitly via [[tenant-consent-delegation]].
- **Remove tenant-provision-and-lock.** Either drop `ProvisionChildAsync` or
  redefine it to mint a self-owned user with a pending single-use claim (never a
  locked child). Update/retire the tests-only call sites
  ([Managers/TenantManager.cs](../../../Managers/TenantManager.cs),
  `TenantManagerTests.cs`). Safe: no production data, greenfield.
- `forActor` no longer rides on tenant *ownership*; it rides on a consent grant
  ([[tenant-consent-delegation]]). A tenant with no grant for a self-owned user
  cannot mint a child credential for them.

## Acceptance Criteria

- [x] AC1 — Wallet-challenge auth. → `WalletAuthController` (`api/user/auth/{challenge,
      verify}`) + `WalletAuthManager` + `SurrealWalletAuthChallengeStore.TryConsumeAsync`
      (atomic conditional UPDATE, AffectedCount==1) + `Ed25519SignatureVerifier` (BC 2.x +
      Algorand2 address decode); `[EnableRateLimiting("financial")]` on challenge; ≤5-min
      TTL; per-address live-challenge cap. Tests: `WalletAuthManagerTests`,
      `Ed25519SignatureVerifierTests`.
- [x] AC1b — Domain separation. → `ConsumeAndVerifyAsync` re-validates the stored
      `DomainMessage` (prefix `AZOA-AUTH-v1` + embedded chain/address/nonce) and a client
      echo must match byte-for-byte. Test: `Verify_ClientMessageMismatch_Rejected_AC1b`.
- [x] AC2 — Unknown `(address,chainType)` → new self-owned user; known → login. →
      `VerifyAsync` + `IUserStore.GetByAuthWalletAsync` (binding-only lookup). Tests:
      `Verify_UnknownWallet_CreatesSelfOwnedUser`, `Verify_KnownWallet_LogsIntoThatUser`.
- [x] AC2b — No takeover. → lookup keys on `auth_wallet_address`/`_chain_type` ONLY (never
      email/username/extuser); `LinkWalletAsync(authedUserId,…)` is the authed link path.
- [x] AC3 — Claim flow, user-side credential, `OwnerTenantId`→null, single-use+TTL token,
      audited. → `WalletAuthManager.ClaimAsync` + `SurrealWalletAuthClaimTokenStore`.
- [x] AC3b — Post-claim cut. → `User.AuthNotBefore` stamped to now on claim;
      `IssueChildCredentialAsync` sets the child-JWT `nbf` at/after it. Test:
      `IssueChildCredential_RespectsAuthNotBeforeWatermark_AC3b`.
- [x] AC4 — Single-use claim invite (tenant cannot set credential); claimed user not
      tenant-driven without a grant. → `CreateClaimInviteAsync` (ownership-asserted);
      `IssueChildCredentialAsync` requires a live grant (tenant-consent-delegation AC2).
- [x] AC5 — IDOR sever is param/identity-scoped; cross-tenant → NotFound.
- [x] AC6 — Hard cutover. → `ProvisionChildAsync` mints SELF-OWNED (`OwnerTenantId=null`);
      no tenant-lock path remains. Test: `ProvisionChild_MintsSelfOwnedUser_NeverTenantLocked`.
      (`TenantManagerTests` rewritten to the new contract; the dead `tests/OASIS.WebAPI.Tests/`
      copy is not in the build — see Deviations.)
- [~] AC7 — Security review of the auth + custody surface is **PENDING a separate
      adversarial pass — NOT self-approved** (see the SECURITY-REVIEW note below).
      `dotnet build`: 0 errors, 28 warnings = baseline, 0 new. Unit tests: 914 pass
      (2 pre-existing unrelated failures — see Deviations).

## SECURITY-REVIEW REQUIRED (track-1 AC7 / track-2 AC11) — ✅ DONE (commit `10e5dad`, 2026-06-22)

This change rewires auth, custody, and value-signing, so it **warranted a separate
security-review pass before being considered done** — the custody seam was implemented
and unit-tested by the same lane, so it could not be self-approved. That pass was
**performed and its findings remediated in commit `10e5dad` (2026-06-22)**: the
`KeyCustodyService` consent chokepoint + `TenantConsentGate` fail-closed paths, the
AC4b sign-path enumeration (no path bypasses the gate), the wallet-challenge domain
separation + atomic nonce consume, the claim watermark cut, and the webhook
SSRF/HMAC/per-tenant isolation were all reviewed and hardened. See
[[consent-gate-architecture]]. AC7 (this track) is therefore satisfied.

## Out of scope / follow-ups

- Client-side (non-custodial) transaction signing in workflows — deferred; custody
  decision is "AZOA-custodied, user-authorized" (see [[tenant-consent-delegation]]).
  A future `signing-gate` action node would park a run for a user-signed tx.
- Social / OAuth login.
- Multi-wallet-per-user auth precedence rules beyond a primary auth wallet.
- EVM/Solana signature verification (Algorand ed25519 first; others follow the
  provider signature-verify pattern).

## Adversarial review applied (2026-06-21)

A hostile security pass hardened this spec before handoff. Folded in: domain-separated
signed auth message + no-replay (C4); create-or-login only, never auto-link to a
pre-existing account (C3); atomic single-use nonce + rate limit (H1); post-claim
`auth-not-before` watermark closing the 15-min child-JWT window (H2); user-side claim
credential + no shadow re-provision (M1). Paired enforcement lives in
[[tenant-consent-delegation]].
