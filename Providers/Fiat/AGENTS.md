---
type: Module Notes
title: Providers/Fiat — Stripe rail adapters
tags: [fiat, stripe, hosted-session, creation-fence, D1, request-void, stripe-session-verdict]
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
Checkout claims have no operator exit: a new request key opens a new claim, and a Stripe session a lost response
created expires unpaid (RV-L1-01). The key holder's own exit is the request-key void (§request-void).

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
- `INTENT_EXPIRED` — see the proof rule below; nothing else may emit it.
- `INTENT_NEEDS_REVIEW` — `FiatCreationVerdict.NeedsOperator`: the 23-hour replay window elapsed, or
  the row is quarantined, with no provider session ever attached.
- `REQUEST_VOIDED` — the key was voided (§request-void); nothing was or can be paid under it.

These five strings are a wire contract with the SDK (`sdk/azoa-wallet`) and the ArdaNova BFF; never
rename or repurpose them once shipped. Every controller switch that can see one maps it to 409
(`FiatCheckoutController.Start`, `TenantPayoutBalanceController.TopUp`), never to the 400 default.

**The proof rule.** Four codes are KEY-RELEASING — `REQUEST_KEY_IN_USE`, `INTENT_FINISHED`,
`INTENT_EXPIRED`, `REQUEST_VOIDED`: clients release the request key and unlock the form. So one of
them may be sent only when the code path that emits it can PROVE the key's outcome is settled —
nothing was or can be paid under it, or (`INTENT_FINISHED`) the intent is terminal and its `result`
says exactly what was paid. `INTENT_NEEDS_REVIEW` is final for the request (stop resending) but NOT
key-releasing: the unattached claim may still have a Stripe session behind it (a lost create
response) that reconciliation can attach, so nothing proves the key dead; the client keeps the key
and shows the intent named in `result`. Anything the node cannot prove gets a non-final answer — a
200 projection, a 503, or the generic 409 `CONFLICT` — because a key-releasing code on a payment that
is clearing is a double charge (the person pays again on a fresh key). The full same-key resend
contract for checkout begin (`FiatRailProvider.BeginCheckoutAsync`; the SDK emulator mirrors this table):

| situation (key already admitted) | answer |
|---|---|
| page attached, open and inside its lifetime | 200, resumed, `paymentUrl` set |
| Stripe says the session is `complete` (paid OR unpaid), or the intent is `AwaitingOutcome` / `Resuming` | 200, resumed, the projection, `paymentUrl: null` |
| the Stripe re-read fails | 503 `DEPENDENCY_UNAVAILABLE` (§settlement-read-failure) |
| intent terminal | 409 `INTENT_FINISHED`, `result` = the projection (503 if that projection's Stripe read fails) |
| page attached + past expiry, intent `AwaitingSubject`, fresh read `expired` unpaid | 409 `INTENT_EXPIRED`, `result` = the projection |
| page attached + past expiry, intent `AwaitingSubject`, fresh read still `open` | expire it AT STRIPE first (§stripe-session-verdict), then: `expired` → 409 `INTENT_EXPIRED`; `complete`/`paid` → 200 projection, `paymentUrl: null`; expire failed and the re-read still `open`, unknown status, or unreadable → 503 `DEPENDENCY_UNAVAILABLE` |
| no page ever created and the pinned expiry is inside the 31-minute floor (§expiry-pin) | 409 `INTENT_EXPIRED` (nothing was ever payable), `result` = the projection |
| needs operator | 409 `INTENT_NEEDS_REVIEW` (not key-releasing), `result` = the projection |
| key voided (tombstone or voided intent), including a void that won the race against this begin | 409 `REQUEST_VOIDED`, `result: null` |
| key bound to another account | 409 `REQUEST_KEY_IN_USE`, `result: null` (never the other account's intent) |
| Stripe answers 409 / `idempotency_key_in_use` (a same-key request still in flight) | 409 `CONFLICT` (`FiatConsumerErrors.RequestInFlight`) — keep the key, retry |

**409 `result`.** `INTENT_FINISHED`, `INTENT_EXPIRED` and `INTENT_NEEDS_REVIEW` carry, in the
envelope's `result`, exactly the `FiatCheckoutIntent` projection `GET /api/fiat/checkout/{userId}/{intentId}`
returns (`intentId`, `status`, `settlementState`, `paymentUrl: null`, `amountPaidMinorUnits`, ...), so a
client that lost its first answer can tell a `complete`/`payment_received` intent from a failed one.
`code`, HTTP status and `message` are unchanged; the field is additive (`FiatRailProvider.FinalConflict`).
The void route's 409 `INTENT_NEEDS_REVIEW` carries `{outcome:"revealed", intentId, intent}` — never
`voided`. `REQUEST_VOIDED` and `REQUEST_KEY_IN_USE` stay `result: null`.

Where it lives: the `FiatCreationVerdict.Attached` branch calls `ResumeAttachedAsync`, which re-reads
Stripe BEFORE deciding and, for a past-expiry `AwaitingSubject` page, settles it with
`SettleCheckoutSessionAsync` — the same expire-first classifier the void uses. The fence's verdict
alone is not proof — it only says a Stripe session exists, which is exactly when something may have
been paid. `ProjectAsync` hands back `paymentUrl` only while the intent is `awaiting_payment` and
Stripe reads `open`, and reports `processing` once Stripe says `complete` even if the webhook has not
moved the claim yet. `StripeGateway.SendAsync` maps every Stripe 409 (and the `idempotency_key_in_use`
code) to the non-final `CONFLICT`, never to `RAIL_REFUSED`/400, which clients treat as final.

## §stripe-session-verdict — one classifier for "can anything still be paid on this page?"

`FiatRailProvider.SessionVerdict.cs` holds the only code that turns a Stripe Checkout read into a
key decision: `JudgeCheckoutSession` (`paid` or `complete` → `PaidOrClearing`; `expired` → `Dead`;
`open` → `Payable`; else `Unknown`) and `SettleCheckoutSessionAsync`, which never returns `Payable`:
an `open` page is expired at Stripe (`POST /v1/checkout/sessions/{id}/expire`, Idempotency-Key
`FiatCheckoutVoid.ExpireIdempotencyKey(intent, session)`), and a refused, lost or inconclusive expire
is settled by a fresh read — `expired` → `Dead`, `complete`/`paid` → `PaidOrClearing`, anything else
(still `open`, unknown, unreadable) → `Unknown`. Both the void (§request-void) and the same-key resume
call it, so "voided" and "INTENT_EXPIRED" cannot mean different things again.

**Why the resume may not trust a clock.** The row's `ExpiresAt` IS Stripe's `expires_at`, stored at
attach, so "our page is past expiry and Stripe's own `expires_at` has passed too" was one fact read
twice. Stripe keeps a session `open` past `expires_at` while its PaymentIntent is still confirming
(a payer in 3DS or a slow confirm), and the node's clock can run ahead of Stripe's. An earlier version
answered `INTENT_EXPIRED` on `open` + clock: the client released the key and Stripe completed the
payment seconds later. Expiring at Stripe turns the clock into a question only Stripe can answer.
Because the void and the resume share the expire key, a void after a resume's expire replays the same
request (and a resume after a void's expire reads `expired`), never a second expire.

## §request-void — the one authoritative way to discard a request key

**Why it exists.** Clients (the SDK, the ArdaNova BFF) persist the request key and resend it after a
lost answer; the key is the lock against a second payment. A client cannot safely discard a key on
its own ("start a new payment", "cancel", a first answer lost before any intent id came back):
only the node knows whether anything was admitted under it, and a late in-flight begin could still
be admitted after a client-side discard. `POST /api/fiat/checkout/{userId}/requests/{requestKey}/void`
(`FiatCheckoutController.VoidRequest` → `FiatRailProvider.VoidRequestAsync`, in
`FiatRailProvider.Void.cs`) is that server call. The proof rule above governs it: it answers
`voided` only when it can prove nothing was or can be paid under the key.

**Same gates and key as begin.** `AuthoriseSubjectAsync` (first-party self, or an API-key tenant with
`tenant:provision` and live consent) and `BuildAdmissionKey` (`{subject}:{tenant|self}:{key}`) are the
very methods `Start` uses. No KYC/KYB gate: voiding moves no value, and a lapsed approval must not
stop a person from cancelling. A route value containing `%2F` is refused 400: routing leaves an
encoded `/` undecoded, so it would name a different key than begin stored and a "voided" would lie.

**How it serializes with begin.** There is no lock; the fence is the admission row plus its
state-version CAS — the same one begin already relies on.
1. The void's admission candidate IS a tombstone (`ABANDONED`, `authoritative_outcome=VOIDED`,
   `outcome_detail=VOIDED_BEFORE_ADMISSION`). `ClaimAdmissionAsync` is create-if-absent on the unique
   `(flow, provider, admission key)`, so a void and a begin racing on an unclaimed key resolve to one
   row: if the void created it, every later begin finds a voided row.
2. Every begin step that can lead to a page URL leaving the node — the creation fence
   (`TryBeginCreationAsync`) and the attach (`TryAttachProviderSessionAsync`) — is a CAS requiring
   `CLAIMED` at the observed version. The void's only write on an admitted row is
   `TryTransitionAsync(→ ABANDONED, VOIDED, VOIDED_AFTER_ADMISSION)` at the version it read. Exactly
   one lands; the loser re-reads. A void that loses re-decides on the new row (bounded attempts, then
   503 `RAIL_BUSY`).
3. Begin checks `FiatCheckoutVoid.IsVoided` after the claim, after the fence verdict, and after a lost
   attach. In the last case its Stripe create already ran; the page was never handed out (a URL leaves
   only after a won attach), and begin expires it at Stripe best-effort.

**Decision table** (`TryVoidAdmittedAsync`; answers are the wire contract the SDK and BFF build on):

| state under the admission key | effect | answer |
|---|---|---|
| nothing admitted | tombstone written | 200 `{outcome:"voided", intentId:null}` |
| already voided (tombstone / voided intent) | none | the same 200 as the first void (idempotent) |
| `CLAIMED`, no page attached, replayable | CAS → `ABANDONED`/`VOIDED` | 200 `{outcome:"voided", intentId}` |
| page attached, fresh Stripe read `expired` | CAS → `ABANDONED`/`VOIDED` | 200 voided |
| page attached, fresh Stripe read `open` | expire at Stripe, then CAS → `ABANDONED`/`VOIDED` | 200 voided |
| expire refused or lost, and a fresh read then says `complete`/`paid` | none | 200 `{outcome:"revealed", intentId, intent}` |
| Stripe says `complete` (paid OR unpaid) or `paid` | none | 200 revealed |
| intent `AWAITING_OUTCOME`, `RESUMING`, or terminal (not voided) | none | 200 revealed |
| Stripe unreadable; expire failed and the re-read still says `open`; unknown status | none | 503 `DEPENDENCY_UNAVAILABLE` |
| unattached claim quarantined, tokenless or past the replay window (`FiatHostedCreation.IsUnreplayable`) | none | 409 `INTENT_NEEDS_REVIEW`, `result: {outcome:"revealed", intentId, intent}` |
| key bound to another account | none | 409 `REQUEST_KEY_IN_USE` |

`intent` on `revealed` is exactly the projection `GET /api/fiat/checkout/{userId}/{intentId}` returns.
An unattached `CLAIMED` row is voidable even with its creation fence begun: the abandon CAS makes the
attach impossible, so the page Stripe may hold for it is one nobody was or can be handed, and it
lapses unpaid at its pinned `expires_at` (§expiry-pin).

**Stripe expire.** `POST /v1/checkout/sessions/{id}/expire` with Idempotency-Key
`azoa-fiat-checkout-expire-<intentId>-<stripeSessionId>` (`FiatCheckoutVoid.ExpireIdempotencyKey`),
fixed per intent so a retried void replays the same request rather than issuing a second one, and a
void that finds the row already voided never calls Stripe. Any failed or inconclusive expire is
settled by a fresh read, never assumed: `expired` → void, `complete`/`paid` → reveal, anything else
→ 503. This is `SettleCheckoutSessionAsync` (§stripe-session-verdict), shared with the same-key resume. Liveness caveat: Stripe replays a stored answer for a reused key, so if the first expire
drew a Stripe 500 the void keeps answering 503 (key kept, safe) until the page lapses at its pinned
expiry, after which the read says `expired` and the void succeeds.

## §settlement-read-failure — a read that cannot confirm state must fail, not guess

`FiatRailProvider.ProjectAsync` re-reads Stripe to report `SettlementState`. When that re-read
errors, the claim may already be a paid, closed intent — reporting the `NotStarted` default would
tell a payer who already paid "waiting for your payment", inviting a second charge. `ProjectAsync`
instead fails the whole projection with `AzoaErrorCodes.DependencyUnavailable` (503), so
`BeginCheckoutAsync` and `GetIntentAsync` (and therefore `FiatCheckoutController.Start`/`Get`) answer
with a retryable failure rather than a lying 200. The one projection that skips the re-read is a
begin's own first answer: it is built from the create's response (`created.Result`), because that
request just won the attach, so the URL never left the node and nothing can have been paid on it;
a second read there only added a Stripe call per checkout and a spurious 503. The oasis-sleek frontend's
`isDefiniteRefusal` (`frontend/src/lib/persisted-request-key.ts`) already treats every 5xx as "keep
the saved key and retry", so this needs no frontend change to stop being unsafe.
