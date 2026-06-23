# How ArdaNova Becomes an AZOA Tenant

> **Audience:** the operator onboarding the first (and every subsequent) tenant
> application. ArdaNova is used as the running example, but nothing here is
> ArdaNova-specific — the code and config carry no brand string. Any application
> follows the identical steps.

A **tenant** is an ordinary AZOA Avatar that owns an API key carrying the
`tenant:provision` scope. That avatar becomes the *tenant principal*; it can
provision and manage a fleet of *child* avatars (one per ArdaNova end user) and
act on each child's wallets/NFTs — but only its own children, never another
tenant's. There is no separate tenant entity, table, or auth scheme (design
Decision D1 — Option B).

Two ownership facts are stored on each child avatar:

- `OwnerTenantId` — the tenant principal's avatar id (a self-FK). `null` means
  "not tenant-managed" (every self-registered avatar).
- `ExternalUserId` — the tenant's *own* user id for that child, unique **per
  tenant** (two tenants may each have a user `"42"`). This is the
  `ArdaNova userId → AZOA AvatarId` lookup key. `ExternalRef` is a free opaque
  string (e.g. org/realm).

---

## Step 1 — Register the tenant avatar

Register a normal avatar that will represent ArdaNova itself (not an end user):

```http
POST /api/avatar/register
Content-Type: application/json

{
  "username": "ardanova-tenant",
  "email": "ops@ardanova.example",
  "password": "<strong-secret>",
  "firstName": "ArdaNova",
  "lastName": "Tenant"
}
```

Then log in to obtain a JWT for the next step:

```http
POST /api/avatar/login
{ "email": "ops@ardanova.example", "password": "<strong-secret>" }
```

The response `Result` is the bearer token used as `Authorization: Bearer <jwt>`.

## Step 2 — Mint the tenant API key (with the tenant scope)

Using the Step 1 JWT, mint an API key whose `scopes` CSV carries
`tenant:provision` plus whatever the tenant is allowed to delegate to its
children (`wallet:manage`, `nft:mint`):

```http
POST /api/apikey
Authorization: Bearer <jwt-from-step-1>
Content-Type: application/json

{
  "name": "ardanova-tenant-key",
  "scopes": "tenant:provision,wallet:manage,nft:mint"
}
```

The raw key is returned **once** in `Result.key` — store it securely. From here
on, ArdaNova authenticates to AZOA as the tenant using
`X-Api-Key: <raw-key>` (never as an end user).

> A key WITHOUT `tenant:provision` is rejected `403` from every `api/tenant`
> action. "Empty scopes = full access" applies only to legacy non-tenant keys;
> it does NOT silently grant tenant powers.

## Step 3 — Provision a child avatar per ArdaNova user

For each ArdaNova user, provision one child avatar. Pass that user's id as
`externalUserId`. The child's `OwnerTenantId` is set server-side from the
authenticated key — never from the body.

```http
POST /api/tenant/avatars
X-Api-Key: <tenant-raw-key>
Content-Type: application/json

{
  "externalUserId": "ardanova-user-42",
  "externalRef": "ardanova/realm-eu",
  "username": "optional-seed",
  "email": "optional-seed@example"
}
```

`username`/`email` are optional; when omitted they are synthesized
deterministically from `tenant-{tenantId}-{externalUserId}` (both columns are
unique-indexed and required). The response carries the new `avatarId` and the
`externalUserId` echo. Store the `avatarId` against the ArdaNova user if you
want to skip the lookup, or rely on Step 5.

**Idempotent.** Re-posting the same `externalUserId` returns the existing child
(no duplicate) — safe to retry.

## Step 4 — Act for a user (issue a child credential)

To act *as* a user (e.g. create a wallet, mint an NFT), request a short-lived
child credential. The tenant guard asserts the child is owned by this tenant
before issuing; a cross-tenant or unowned target returns **404** (deliberately
not 403, so a prober cannot enumerate other tenants' avatars).

```http
POST /api/tenant/avatars/{avatarId}/credential
X-Api-Key: <tenant-raw-key>
Content-Type: application/json

{ "scopes": ["wallet:manage"] }
```

The response `Result.token` is a short-lived JWT (15-minute TTL) whose subject
is the **child** avatar. The delegated scopes are the intersection of the
requested scopes with the tenant key's own scopes — a tenant can never delegate
a scope it does not itself hold, and `tenant:provision` is never delegated down
(a child cannot provision further avatars). An empty `scopes` array delegates
the tenant's full delegable set.

Use that token as `Authorization: Bearer <child-token>` against the existing
wallet/NFT endpoints — e.g.:

```http
POST /api/wallet
Authorization: Bearer <child-token>
{ "chainType": "Algorand", "walletType": "Platform" }
```

The call runs under the child's identity through the existing per-avatar
authorization. When the token expires, request a new one.

## Step 5 — Resolve `ArdaNova userId → AZOA AvatarId`

To map one of your users back to its AZOA avatar without storing the mapping
client-side:

```http
GET /api/tenant/avatars/ardanova-user-42
X-Api-Key: <tenant-raw-key>
```

Returns the child avatar (scoped to this tenant). A user id that does not belong
to this tenant returns **404**. To list all children (optionally filtered):

```http
GET /api/tenant/avatars?externalUserId=ardanova-user-42
X-Api-Key: <tenant-raw-key>
```

---

## Isolation guarantees (the security crux)

- **Cross-tenant access is impossible.** Every per-child operation asserts
  `child.OwnerTenantId == authenticatedTenantId`; a mismatch (or a `null` owner)
  returns 404 — indistinguishable from "no such avatar".
- **The tenant id is never request-sourced.** It is always the authenticated
  key's owner avatar id (claim), never a body field. The provision/credential
  request models have no tenant id field by construction.
- **No privilege escalation through delegation.** A child credential can only
  carry scopes the tenant key itself holds, minus `tenant:provision`.

## Deploy note

The mechanism shipped here is the tenant-onboarding track (DEPLOY-STEP **B5**,
cross-tenant isolation — enforced via the `OwnerTenantId` guard + 404 response).
Actually registering the first real tenant (ArdaNova) and populating its
user→Avatar mapping is the operational step **P6**, still owed at deploy time.
