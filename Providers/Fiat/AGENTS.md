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
a quarantined row, the verdict is `NeedsOperator`: the operator reconciliation path
(`ReconcileAdmissionAsync`) attaches the verified object. Connected accounts carry
`metadata[azoa_payout_claim]=<rowId>` so the operator can find the one Stripe opened.

**Known adjacent gap (L3, D8).** Checkout recomputes `expires_at` on every attempt, so a replay
after a lost response gets Stripe's `idempotency_error` and reports busy (fail-closed: no second
page). Pinning `expires_at` at the first claim is L3's D8 fix.
