---
type: Module Notes
title: Providers/Fiat — Stripe rail adapters
tags: [fiat, stripe, hosted-session, creation-fence, D1]
---

# Providers/Fiat

Stripe checkout (`FiatRailProvider`, `StripeGateway`) and Stripe Connect (`StripeConnectGateway`)
adapters. Domain rules for money in/out live in `Services/Fiat/**/AGENTS.md`; this file covers the
adapter-level invariants.

## §creation-fence — one Stripe object per hosted claim (D1, round 4)

Both Stripe-backed hosted flows — checkout (`FiatRailProvider.BeginCheckoutAsync`) and the
payout account (`PayoutAccountService.BeginSetupAsync`) — ride `hosted_session` and share
`FiatHostedCreation`:

1. **Claim** with a fresh `CreationToken`. `ClaimAdmissionAsync` is create-if-absent on
   `(flow, provider, admission key)`, so every concurrent request ends up holding the *same* row.
2. **Adopt the row's token**, never the request's. The token is a durable property of the claim
   incarnation, not a secret of the request that wrote it (the same rule `HostedIdentityRecovery`
   uses). Exclusivity comes from step 3, not from who knows the token.
3. **Fence before the external call.** `TryBeginCreationAsync` is a version CAS that sets
   `creation_started_at` only while it is `NONE`; exactly one caller wins. A loser re-reads.
4. **Send with a claim-bound Stripe idempotency key** `<prefix><rowId>-<creationToken>`
   (`FiatHostedCreation.IdempotencyKey`). Any request that finds the fence already begun —
   a concurrent loser or a retry after a crash/lost response — *replays the same key*, so Stripe
   returns the object it already created (or answers a concurrent duplicate with a 409, which is
   surfaced as busy). Two different keys for one claim are impossible.
5. **Attach with the token** (`TryAttachProviderSessionAsync`: version CAS + `CLAIMED` + token +
   begun creation + no provider id). The loser of the attach race re-reads and accepts the row only
   if it carries the very Stripe id its own create returned.

**Why it is crash-safe.** Every crash point leaves a row the next request resolves without a second
Stripe object: before step 3 → the next caller begins the fence; between 3 and 5 → the next caller
replays the same key and attaches; after 5 → the row is attached and is reused. No DB transaction
spans the Stripe call (transaction-scope rule 5).

**The replay window.** Stripe prunes idempotency keys after at least 24 hours, so a replay later
than that could open a second object. `FiatHostedCreation.ReplayWindow` is 23 hours measured from
`creation_started_at` (which precedes the first send, so the window is conservative). Past it, or on
a quarantined row, the verdict is `NeedsOperator`. The generic operator route
(`POST /api/operator/hosted-sessions/{id}/reconcile`) cannot resolve Stripe rows (it needs a provider-attempt
record the fiat path never writes), so payout-account setups have their own Stripe-specific exit:
`POST /api/admin/fiat-payouts/accounts/{userId}/setup/resolve` (`PayoutAccountService.ResolveStuckSetupAsync`,
see `Services/Fiat/OffRamp/AGENTS.md` §account-recovery). Connected accounts carry
`metadata[azoa_payout_claim]=<rowId>` and `metadata[azoa_user_id]` so that exit can find the one Stripe opened.
Checkout claims have no such exit: a new request key opens a new claim, and a Stripe session a lost response
created expires unpaid (RV-L1-01; revisit if a stuck checkout claim ever blocks a person).

## §expiry-pin — checkout `expires_at` is fixed per claim (D8, round 4)

Stripe answers `idempotency_error` when a key is replayed with different parameters, so a replay
after a lost create response must send the exact bytes of the first attempt. `expires_at` is
therefore `creation_started_at + clamp(SessionExpiryMinutes, 35, 1440)`
(`FiatRailProvider.PinnedCheckoutExpiry`): `creation_started_at` is written once by the creation
fence, so every replay of one claim computes the same instant. The 35-minute floor is Stripe's
30-minute minimum plus a replay margin. Once the pinned expiry is closer than 31 minutes, the
claim can no longer be (re)sent validly — if the first create never reached Stripe, Stripe would
reject the short lifetime — so the node answers "This payment page has expired. Start a new
payment." (Conflict) instead of looping on a create Stripe refuses. A new request key opens a new
claim; a Stripe session the lost response did create simply expires unpaid (nobody holds its URL).

## §webhook-secrets — platform and Connect endpoints (F-M2, round 4)

Stripe signs connected-account events (`payout.*`, `account.*` with an `account` field) on a
**Connect** webhook endpoint whose signing secret differs from the platform endpoint's. Configure
`Fiat:<Env>:WebhookSecret` (platform endpoint, required) and `Fiat:<Env>:ConnectWebhookSecret`
(Connect endpoint, optional). `StripeWebhookController` verifies with the platform secret first,
then the Connect secret. With a **distinct** Connect secret configured, each secret is accepted
only for its own endpoint's events — a Connect-signed event must carry `account`, a
platform-signed one must not — so a leaked secret of one endpoint cannot forge the other's events.
With no Connect secret (or the same value, as `stripe listen --forward-connect-to` uses in dev) the
single platform secret verifies both. Register both endpoints in the Stripe dashboard: the platform
endpoint for `checkout.session.*`, `charge.*`, `transfer.*`; the Connect endpoint for `payout.*` and
`account.updated`. Both URLs are `POST /api/fiat/webhook/stripe`.

## §same-key-replay-codes — telling one 409 from another

A persisted idempotency key gets resent after a lost answer, and `BeginCheckoutAsync` (and the
cash-out claim in `Services/Fiat/OffRamp/OffRampService.cs`) answers several different situations
with the same HTTP 409 — a caller that only reads the status cannot tell "finished, start fresh"
from "still in flight, keep waiting" from "wrong account". Each situation now carries a stable
`code` on the existing `AZOAResult` envelope (`FiatConsumerErrors`), with the HTTP status and human
message unchanged so this is additive for any caller that still only reads those:

- `REQUEST_KEY_IN_USE` — a different account's `OwnedBy`/`DrivenBy` check failed on this key.
- `INTENT_FINISHED` — `HostedSessionStates.IsTerminal` on the claimed row; nothing left to resume.
- `INTENT_EXPIRED` — `FiatHostedCreation` reports the claim `Attached` with no resumable page, or
  the pinned expiry (§expiry-pin) is inside the 31-minute floor.
- `INTENT_NEEDS_REVIEW` — `FiatCreationVerdict.NeedsOperator`: the 23-hour replay window elapsed, or
  the row is quarantined, with no provider session ever attached.

These four strings are a wire contract with the SDK (`sdk/azoa-wallet`) and the ArdaNova BFF; never
rename or repurpose them once shipped.

## §settlement-read-failure — a read that cannot confirm state must fail, not guess

`FiatRailProvider.ProjectAsync` re-reads Stripe to report `SettlementState`. When that re-read
errors, the claim may already be a paid, closed intent — reporting the `NotStarted` default would
tell a payer who already paid "waiting for your payment", inviting a second charge. `ProjectAsync`
instead fails the whole projection with `AzoaErrorCodes.DependencyUnavailable` (503), so
`BeginCheckoutAsync` and `GetIntentAsync` (and therefore `FiatCheckoutController.Start`/`Get`) answer
with a retryable failure rather than a lying 200. The oasis-sleek frontend's
`isDefiniteRefusal` (`frontend/src/lib/persisted-request-key.ts`) already treats every 5xx as "keep
the saved key and retry", so this needs no frontend change to stop being unsafe.
