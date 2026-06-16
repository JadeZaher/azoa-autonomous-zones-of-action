# Tenant → OASIS Allocation — Integration Contract

This document defines the single cross-system call surface of the
`fiat-stripe-bridge` track: the ordered OASIS API calls **the fiat-settlement
tenant** makes *after* money has cleared and *after* it has written its own
investment record. OASIS contributes an idempotent, KYC-gated, tenant-callable
**wallet-provision + asset-allocation** primitive and nothing else.

> **Naming.** The caller is referred to here only as **"the fiat-settlement
> tenant."** Stripe is named in this document *solely* as the upstream trigger
> source (the payment provider that tells the tenant money cleared). No Stripe
> SDK, no Stripe secret, and no Stripe webhook handler exists in OASIS. The
> token economics, treasury split, and any funding-gate evaluation stay
> entirely in the tenant.

---

## Division of responsibility

| Concern | Owner |
|---|---|
| Checkout, PaymentIntent retrieval, webhook signature verification, metadata parsing | **Tenant** |
| Token-allocation math, balance credit, treasury split, funding-gate evaluation | **Tenant** |
| The investment record (the tenant's source of truth) | **Tenant** |
| Deciding *how much* of *which* asset to allocate | **Tenant** |
| Provisioning a custodial wallet for an avatar (if absent) | **OASIS** |
| Moving the on-chain / custodial asset (mint or transfer) | **OASIS** |
| Deduping a redelivered trigger so the asset moves exactly once | **OASIS** |
| KYC gate (fail-closed) on the value-bearing move | **OASIS** |

OASIS receives an **already-decided amount**. It never computes economics.

---

## Trust & credentials

- The tenant authenticates to OASIS with an **`X-Api-Key`** header carrying the
  tenant's OASIS API key. OASIS validates it via the existing API-key
  infrastructure (SHA-256 hashed at rest) and admits the request as an
  avatar-scoped principal — controllers cannot distinguish it from a JWT.
- The API key MUST carry the `nft:mint` (or `wallet:manage`) **scope** — this is
  what authorises allocation. A key without it receives `403`.
- The tenant's OASIS API key is a **deploy-time secret**, recorded as the
  `OASIS_TENANT_API_KEY` deploy-stub in `conductor/DEPLOY-STEPS-TODO.md`. It is
  **never committed**.
- OASIS holds **no Stripe secret** (`Stripe:SecretKey` / `Stripe:WebhookSecret`
  are absent). Webhook signature verification is the tenant's job; OASIS trusts
  the tenant via the API key, not via a payment-provider signature.

---

## The call the tenant makes

After the tenant's post-settlement handler has (1) verified the upstream
webhook signature, (2) decided the allocation amount, and (3) written its own
investment record, it makes **one** call into OASIS:

### `POST /api/allocation/{avatarId}`

| | |
|---|---|
| **Method / path** | `POST /api/allocation/{avatarId}` |
| **`{avatarId}`** | The OASIS avatar that receives the asset. **This is the only place the target is named** — no body field can redirect it (IDOR-resistant). |
| **`X-Api-Key`** | `<tenant OASIS API key>` |
| **`Idempotency-Key`** | A **stable per-payment key** — e.g. the upstream PaymentIntent id. A redelivered trigger MUST reuse the same value. |
| **`Content-Type`** | `application/json` |

#### Request body

The body is the asset descriptor + the already-decided amount. It deliberately
carries **no owner / avatar id** — the target avatar comes from the route.

Mint (create a new asset into the avatar's custodial wallet):

```json
{
  "kind": "Mint",
  "chainType": "Algorand",
  "amount": "100.00",
  "name": "Project Alpha Allocation",
  "description": "Fiat-settled allocation",
  "assetId": "PRJALPHA",
  "metadata": { "investmentRef": "inv_abc123" }
}
```

Transfer (move an existing OASIS asset into the avatar's custodial wallet):

```json
{
  "kind": "Transfer",
  "chainType": "Algorand",
  "amount": "100.00",
  "assetRecordId": "3f0c2b6e-1a2b-4c3d-9e8f-7a6b5c4d3e2f",
  "memo": "inv_abc123"
}
```

| Field | Meaning |
|---|---|
| `kind` | Discriminator: `Mint` or `Transfer`. |
| `chainType` | Target chain — decides wallet reuse vs. provision. |
| `amount` | Already-decided amount, **string** for arbitrary precision. Opaque to OASIS. |
| `name` / `description` | Asset name / description (mint path). |
| `assetId` | Chain-native asset id (required on transfer, optional on mint). |
| `assetRecordId` | The existing OASIS asset id to transfer (transfer path only). |
| `metadata` | Optional extra asset metadata (mint path). The amount is also folded in here. |
| `memo` | Optional free-text memo (transfer path). |

#### Response body — `200 OK`

```json
{
  "isError": false,
  "message": "Allocation completed.",
  "result": {
    "avatarId": "9c1e...",
    "walletId": "a4d2...",
    "walletAddress": "ALGO...ADDRESS",
    "walletProvisioned": true,
    "operationId": "b7f3...",
    "replayed": false,
    "idempotencyKey": "alloc:<apiKeyId>:pi_3Q..."
  }
}
```

The tenant records `walletId`, `walletAddress`, and `operationId` against its
own investment row as the OASIS reference.

#### Replay — `200 OK` with `replayed: true`

A second call with the **same** `Idempotency-Key` under the same API key
returns the **original** result and performs **no** second mint/transfer:

```json
{
  "isError": false,
  "message": "Duplicate request: returning the result of the original allocation (not re-executed).",
  "result": { "replayed": true, "operationId": "b7f3...", "...": "..." }
}
```

#### Failure modes

| Status | When |
|---|---|
| `401 Unauthorized` | Missing / invalid API key, or missing `ApiKeyId` claim. |
| `403 Forbidden` | API key lacks the `nft:mint` / `wallet:manage` scope, **or** the target avatar's KYC is not `APPROVED` (message prefixed `KYC_FORBIDDEN:`). Fail-closed — no asset moved. |
| `400 Bad Request` | Malformed request, missing `chainType`, transfer without `assetRecordId`, or a provisioning / allocation error. |
| `429 Too Many Requests` | The `financial` rate-limit policy tripped (value-bearing endpoint). |

---

## Idempotency semantics

- The idempotency key is **partitioned by the caller's API key**: the persisted
  key is `alloc:<apiKeyId>:<tail>`, so two tenants reusing the same
  human-friendly key (e.g. the same integer) never collide.
- The **client `Idempotency-Key` wins**. When absent, OASIS derives a
  **deterministic content key** over `(avatarId, kind, chainType, amount,
  assetId, assetRecordId)`. A random per-request key is **never** generated —
  absence is still dedup-safe.
- The first caller to claim the key performs the effect exactly once; concurrent
  / redelivered callers observe the original result. This is the same
  exactly-once ledger (`IIdempotencyStore`) the bridge and faucet use — we do
  not repeat the bridge's pre-launch "no idempotency" mistake.

**Tenant obligation:** the tenant MUST send a stable `Idempotency-Key` (e.g. the
PaymentIntent id) so its own at-least-once webhook redelivery maps to one OASIS
allocation.

---

## Prose sequence diagram

```
 Payment provider          The fiat-settlement tenant                     OASIS
 ───────────────           ──────────────────────────                     ─────
   money clears ─────────────►  webhook received
                                 verify signature (tenant secret)
                                 parse metadata, DECIDE amount
                                 run token economics / treasury / gate
                                 write ProjectInvestment (tenant's truth)
                                       │
                                       │  POST /api/allocation/{avatarId}
                                       │  X-Api-Key: <tenant key>
                                       │  Idempotency-Key: <PaymentIntent id>
                                       │  { kind, chainType, amount, ... }
                                       └──────────────────────────────────►  TryClaim(idempotencyKey)
                                                                              ├─ lost claim ─► replay original result ──┐
                                                                              │                                          │
                                                                              ├─ won claim ─► RequireVerifiedAsync(avatar)
                                                                              │                ├─ not APPROVED ─► 403, no effect ─┐
                                                                              │                └─ APPROVED                         │
                                                                              │                    provision wallet if absent     │
                                                                              │                    mint / transfer (exactly once)  │
                                                                              │                    persist result for replay       │
                                       ◄──────────────────────────────────────────────────────  200 { walletId, operationId } ───┘
                                 record OASIS reference on the investment row
```

Webhook redelivery (the provider sends `payment_intent.succeeded` again) re-runs
the tenant handler with the **same** `Idempotency-Key`; OASIS replays the
original result and moves no asset a second time.

---

## What this track does NOT add to OASIS

- No Stripe SDK dependency.
- No `Stripe:SecretKey`, no `Stripe:WebhookSecret`.
- No webhook controller / signature verification.
- No checkout, no PaymentIntent retrieval, no Connect / payout.
- No token-economics, treasury, or gate logic.

The only credential introduced is the tenant's OASIS API key, recorded as a
deploy-stub (`OASIS_TENANT_API_KEY`), never committed.
