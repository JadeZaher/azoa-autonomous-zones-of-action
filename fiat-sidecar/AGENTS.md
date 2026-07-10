# fiat-sidecar — reference Stripe→AZOA bridge

A thin, Docker-ready Express host around `AzoaStripeOrchestrator` from
`@azoa/wallet-sdk/orchestration`. It holds the Stripe secret + webhook secret and
an AZOA API key; it maps confirmed fiat payments to idempotent AZOA allocations.
The orchestrator itself is stateless (no DB) — dedup is the AZOA allocation
idempotency key.

## §pricing — mint quantity is server-side, never client-supplied

The minted token quantity is derived at webhook time from the **Stripe-confirmed**
`session.amount_total` (cents) times the operator's price schedule
(`TOKENS_PER_CENT`), floored to an integer. Client checkout metadata carries only
identity/routing (`avatarId`/`assetId`/`chainType`/`kind`) — it CANNOT carry the
mint quantity. This closes the CRITICAL-1 money-printer where a client could mint
an arbitrary amount for a trivial payment.

Two distinct units, never conflated:
- **fiat cents** (`amount_total`, Stripe minor units) — the money that cleared.
- **AZOA base units** (integer string, `NumberStyles.None` on the backend) — the
  token quantity. The AZOA `amount` is ALWAYS an integer base-unit string; a
  decimal dollar/cents string is never passed as the AZOA amount.

## §auth — checkout is authenticated, routing values are allowlisted

`POST /api/checkout` requires `Authorization: Bearer <CHECKOUT_AUTH_TOKEN>`
(constant-time compare, fail-closed when the secret is unset) → 401 otherwise.
`assetId`/`chainType`/`kind` are validated against server-side allowlists
(`ALLOWED_ASSET_IDS` / `ALLOWED_CHAIN_TYPES` / `ALLOWED_KINDS`; an empty list means
that dimension is intentionally unrestricted). CORS uses an origin allowlist
(`ALLOWED_ORIGINS`), not allow-any-origin.

## §webhook-mapping — transient vs terminal (HIGH-5)

The AZOA allocation seam returns a RETRYABLE in-progress "Fail" before the chain
TxHash is recorded (`Managers/AllocationManager.cs` ~L155-158). If we returned a
plain error there, Stripe would retry forever, eventually give up, and the
customer would be charged with no tokens. Mapping:

| Orchestrator outcome | HTTP | Rationale |
| --- | --- | --- |
| `WebhookValidationError` (bad signature, missing metadata, bad `session.id`) | **400** | Stripe does not retry a 4xx; the request is genuinely malformed. |
| `ignored` (not `checkout.session.completed`) | **2xx** | Nothing to do. |
| `settled` (AZOA confirmed) | **2xx** | Done. |
| `accepted_pending` (AZOA in-progress, no TxHash yet) | **2xx** | Backend reconciliation settles from chain truth; stop Stripe retries. |
| `failed_terminal` (genuine AZOA failure) | **500** | Stripe retries with bounded backoff. |

Transient detection is by message substring (`in progress` / `retry once` /
`has not yet`) because the backend surfaces the in-progress state as a plain 400
`AZOAResult` with no distinct status — see `isTransientAllocationError` in
`sdk/azoa-wallet/src/orchestration/stripe.ts`.

## §idempotency

`Idempotency-Key = session.id` (always present, unique per checkout — HIGH-4).
`session.payment_intent` was previously used but can be null/non-string, silently
falling through to backend content-key dedup and colliding distinct payments.
The orchestrator rejects a missing/empty `session.id` rather than proceeding.

## §errors

Handlers never echo raw exception text (Stripe internals / AZOA method+path) to
the caller. Full detail is logged server-side with a `correlationId`; the client
gets a generic message plus that opaque id (MEDIUM-6).

## Config knobs (env)

`STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `AZOA_API_URL`, `AZOA_API_KEY`,
`SUCCESS_URL`, `CANCEL_URL`, `CHECKOUT_AUTH_TOKEN`, `ALLOWED_ORIGINS` (csv),
`ALLOWED_ASSET_IDS` (csv), `ALLOWED_CHAIN_TYPES` (csv), `ALLOWED_KINDS` (csv,
default `Mint,Transfer`), `TOKENS_PER_CENT` (positive float, default 1).
