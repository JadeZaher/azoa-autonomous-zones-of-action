# `azoa-sdk/money` — host-transport money state (SR-05 P0)

Framework-free. AZOA owns payments end to end; a host app (ArdaNova first) consumes money UI and state
from here instead of re-implementing it. Safe to import server-side (a host BFF uses `refusalFromSdkError`,
`toCheckoutView`, `toCustodialAccountView` to shape what it returns). `azoa-sdk/react` builds on this.

## §transport

**Hard rule: the SDK never holds a privileged credential in the browser.** Every privileged action goes
through an `AzoaMoneyTransport` the host injects. ArdaNova implements it over tRPC → its BFF, the sole
holder of the tenant API key. Components consume a transport, never a token.

- No `userId` (or any subject) appears anywhere in the interface: the subject is the host session. A
  browser that could name the subject could name somebody else's.
- Every method resolves to `MoneyResult<T>`; a transport never rejects for a refusal. (`azoa-sdk/react`
  still guards against a buggy transport that does.)
- `checkout.begin` / `cashOut.start` take only `{ requestKey, amountMinorUnits }`. Return URLs, purpose and
  pricing are the host's server-side decision, so a browser cannot redirect a payment to another origin.
- `checkout.void(requestKey)` is required (0.6.0): the node's void-or-reveal (§void). A host BFF forwards it to
  `POST /api/fiat/checkout/{userId}/requests/{requestKey}/void` and narrows `revealed.intent` with `toCheckoutView`.
- `sale` is optional and host-priced: `quote` pins quantity and proceeds under a `quoteId`; `sell` sends
  `{ requestKey, quoteId }`. Unit quantities are base-unit decimal strings, never JavaScript numbers.
- Keep the transport object referentially stable; the React provider keys its account store on it.

## §views

View types are node DTOs narrowed for the browser. `CheckoutView` drops `admissionKey` (the node builds it
from subject + tenant ids + request key). `CustodialAccountView` drops `tenantId`, `externalSubject`,
`ardanovaUserId`, `userId`. Capability views drop `missingConfigurationKeys` (configuration key names
belong in the operator console, QA L2). **Every** view has a `to*View` that narrows at runtime with an
**allowlist** (checkout, account, both capability views, KYC session, consent receipt, cash-out quote,
payout destination, cash-out, balance, sellable, sale quote, sale), so a field the node adds later cannot
leak by default; the direct transport runs each answer through one, and host BFFs should too. Readiness
flags (`ready`, `available`, `accountReady`, `readyToReceive`, ...) must be literally `true`: a host
transport that sends `"false"` gets a closed gate, not an open one. `findForbiddenViewKeys` is the deep
check the contract runs (subject/tenant ids plus provider account ids and `missingConfigurationKeys`).
`toRefusalServerResult(result)` narrows a same-key 409's `result` (a projection, or the void's `revealed`
shape) for `MoneyRefusal.serverResult`, and returns null for anything else (§refusals, M1).

## §intake

**Narrowed once, where a transport's answer enters (LOW-2, final gate).** The direct transport narrows every
answer, but a host transport (a BFF over tRPC) is code the SDK does not control: one that forwarded the node's
raw projection, or a 409's raw `result`, used to put `admissionKey` (`{subject}:{tenant}:{key}`) into attempts,
hook state and `onRefusal`, because `flows.ts` only *cast* views and passed refusals through. `intake.ts` is now
the single seam: `narrowMoneyResult(result, narrow)` accepts a well-formed `MoneyResult` only, runs `value`
through `narrow` (an allowlist `to*View`; one that throws means "not the view" → `unconfirmed`), and rebuilds
every refusal with `narrowRefusal` — a known code or `unconfirmed` (which keeps the key), the message, a
non-negative `retryAfterSeconds`, and `serverResult` only through `toRefusalServerResult` and only on `definite`
/ `needs_review`. Nothing else of a host object survives. It has exactly two callers: `settled()` in `flows.ts`
(every flow's transport call) and `settleMoney()` in `src/react/settle.ts` (every hook read), whose `narrow`
parameter is required so a new call site cannot forget it. `checkoutViewFrom` / `cashOutViewFrom` /
`saleViewFrom` keep the old acceptance rule (a record with a string id) and allowlist-copy; `cashOutHistoryFrom`
requires an array. Internal: not exported from `azoa-sdk/money` (the React entry imports it directly).

`deriveAccountGate`: `ready` (the only state that moves value) | `setup` (verify identity / finish the
account; also a `setup_required` refusal, e.g. no account yet) | `review` (`UnderReview`: verified, a
compliance check owes a human decision) | `revoked` (`consent_revoked`) | `unavailable` (the node cannot
onboard right now, or any other refusal).

## §refusals

Closed vocabulary, mapped from node bodies by `refusalFromSdkError` (verified against the controllers on
2026-10-26, and the same-key resend contract of `FiatRailProvider.BeginCheckoutAsync` + `ResumeAttachedAsync`,
oasis-sleek branch feat/fiat-replay-codes, `cee103bd` on 2026-10-27 and `33168340` on 2026-10-28):

| code | node evidence | key |
| :-- | :-- | :-- |
| `consent_revoked` | 403 whose message names consent (`TenantConsentGate.Deny`) | released* |
| `account_holder_required` | 403 "…only be done by the account holder" (F-H8, payout setup) | released* |
| `kyb_required` | `KYB_REQUIRED` code on 400/403/422, or the prefix on a definite status (D-6) | released* |
| `setup_required` | "KYC verification required" (`KycAuthorizationError`) | released* |
| `under_review` | definite refusal mentioning "under review" (AML review) | released* |
| `payments_not_configured` | `PAYMENTS_NOT_CONFIGURED` code on 400/422/503 (QA M3) | released* |
| `definite` | 409 `INTENT_FINISHED` / `INTENT_EXPIRED` / `REQUEST_KEY_IN_USE` / `REQUEST_VOIDED`; any other 400/403/404/422; a client guard that threw before egress | released* |
| `rail_busy` | `RAIL_BUSY`, `RAIL_UNAVAILABLE`, 429; another tab held the send lock past `lockTimeoutMs` | **kept** |
| `unconfirmed` | network loss, timeout (incl. `sendTimeoutMs`), 409 without a final code (`CONFLICT`: Stripe still has the same-key request in flight), 5xx (incl. 503 `DEPENDENCY_UNAVAILABLE`, the node's failed Stripe re-read) | **kept** |
| `needs_review` | 409 `INTENT_NEEDS_REVIEW` (`FiatCreationVerdict.NeedsOperator`) | **kept**, marked |
| `unauthenticated` | 401 / `AUTH_EXPIRED` | **kept** |

\* Directly only for a key's **first** send. **A saved request with a `serverId` is never released by a
refusal code** (§flows "decide by state"), and a **resent** key without one (a lost answer) is released only
by the key's own state: the 409's own projection (`serverResult`, when the node's envelope carries one) if
present, else the node's void answering `voided` (§void and §flows "decide by state").

**Order matters** (each step only runs when the previous did not decide): not an `SdkError` → `unconfirmed`;
401 → `unauthenticated`; no HTTP status → `definite` for a client guard, else `unconfirmed`; the envelope's
machine `code` (a key-releasing code counts only on the statuses the node sends it with, so `INTENT_FINISHED`
on a 500 is still `unconfirmed`); 429 → `rail_busy`; any status that is not 400/403/404/422 → `unconfirmed`
**whatever the message says**; only then message text. That last rule is why a 503 whose text says
`KYB_REQUIRED:` keeps the key (L6): text is evidence only inside a definite status.

The same-key 409 codes (`INTENT_*`, `REQUEST_KEY_IN_USE`, `REQUEST_VOIDED`) follow the node's proof rule, but
only `INTENT_EXPIRED` (after a fresh Stripe read) and `REQUEST_VOIDED` (the node voided the key) are proof that
nothing was or can be paid; `INTENT_FINISHED` is "terminal" (paid, failed or expired) and `REQUEST_KEY_IN_USE`
names another account's row. All four map to `definite`, so the flows never trust the code alone where they
can ask: a key that names its intent is decided by reading it. A resent key that names none is decided
directly from the 409's own `serverResult` (round 4, `33168340`: `INTENT_FINISHED` / `INTENT_EXPIRED` now
carry the GET projection) when the node sent one, else put to the void (§void) -- same decision, no round
trip. That closes round 2's gap (`INTENT_FINISHED` on a lost answer released the key while a card payment's
funds were still landing): the carried or revealed intent decides it and the key follows that state, never
the code (`checkoutProjectionFrom` in `flows.ts`; never releases on `INTENT_FINISHED` unless that projection
is settled -- see `isSettledCheckout`, §presentation).

**`serverResult` is narrowed at the source (M1).** The node's 409 `result` is the GET projection, and the
projection carries `admissionKey` (`{subject}:{tenant}:{key}`), so the raw object must never reach a page.
`refusalFromSdkError` attaches `serverResult` **only** for `INTENT_FINISHED` / `INTENT_EXPIRED` /
`INTENT_NEEDS_REVIEW` (`RESULT_CARRYING_CODES`), and only through `toRefusalServerResult` (§views): a
projection becomes `toCheckoutView(result)`, the void's `{ outcome: "revealed", intent }` becomes
`{ outcome: "revealed", intent: toCheckoutView(intent) }`, anything else is dropped. Narrowing happens once,
here, so a host BFF that builds its refusals with `refusalFromSdkError` is safe by construction; the contract
(§contract) checks refusals for forbidden keys too. A host transport's refusal is rebuilt at the intake
(§intake), so `serverResult` is narrowed whoever built it. Flows only ever *read* it through
`checkoutProjectionFrom`, which trusts it only on a `definite` refusal (after M1 that means `INTENT_FINISHED` /
`INTENT_EXPIRED`; `needs_review` never decides by projection).

`needs_review` (MEDIUM-3) is not "the answer did not arrive": the node answered, and it holds the request
for an operator. The key is kept and `markNeedsReview` flags the saved request, so the UI shows "needs a
manual check — we'll follow up" with no resume prompt, on this visit and later ones. Its actions are a
same-key "check again", which cannot charge twice, and (checkout only, M2b) **Discard**, which is the node's
void like any other (§void): `voided` releases it, `revealed` or a 409 keeps it. A held checkout is never left
without an exit: after the 23 h replay window the node's begin can only answer `INTENT_NEEDS_REVIEW`, so a
disabled Discard was a permanent trap.

`releasesRequestKey` encodes the last column as an **allowlist** of the seven key-releasing codes: host
transports deserialize JSON at runtime, so a typo, a capitalized code, `"server_error"`, `"timeout"`, a
missing refusal or any future code keeps the key (H1). Only a definite answer may forget a key, because
anything else may hide an admission. `moneyRefusal(code, message?)` builds a refusal with the code's fixed
fallback sentence. `refusal.message` is the server's own sentence (wire-code prefixes and
`Missing configuration:` tails stripped) and never SDK request text like `POST /api/... failed`; when the
server said nothing useful it is a fixed fallback. Display copy comes from `describeRefusal`, where the
host-overridable `messages.refusal[code]` wins and `definite`/`under_review` fall back to the server text
(it names the evidence, or the review contact). Known gap: the node's `retryAfterSeconds` body field is not
surfaced by `AzoaApiClient` yet, so the direct transport never sets it; host transports may.

## §request-keys

Lifted from the AZOA frontend's `lib/persisted-request-key.ts` (rules: `frontend/src/app/(dashboard)/AGENTS.md`
§first-party-money), with storage injected:

- **Keys before egress.** `claimRequestKey` writes the key *and reads it back* before returning, so a store
  that silently dropped the write cannot let a request leave unkeyed (it throws; `flows` turns that into a
  `definite` refusal: nothing was sent).
- **An unresolved key is never replaced silently.** A saved key for a different amount (or a different
  `reference`, e.g. another sale quote) returns `pending`: resume that exact request or explicitly discard it.
- **The key is the lock against a second payment.** `rememberServerId` stores the intent id beside the key;
  `resolveCheckoutByState` forgets the key only when that intent is **settled** (`isSettledCheckout`: funds
  applied, or provably unpaid and closed). A terminal intent whose money is still landing keeps it, so a
  reload cannot offer a second payment while the first is in flight. `clearSavedRequest(store, scope,
  requestKey)` clears only while that key is still the saved one (another tab may have replaced it).
- **Held for review.** `markNeedsReview` sets `needsReview: true` on the saved request (a `needs_review`
  answer); `rememberServerId` drops it once an answer names the server's record.
- **Per subject.** `createKeyStore({ subject, storage? })` hashes the subject (cyrb53, namespacing only)
  into every storage key, so two people on one device never see each other's pending payments. It throws on
  an empty subject rather than share a slot. Default storage is `localStorage`; with none (SSR, sandboxed
  frames) it is per-instance memory, never module-level (a server must not share keys across requests).
- `newRequestKey` falls back to `getRandomValues` where `crypto.randomUUID` is missing (non-secure origins).

## §flows

`startCheckout` / `sendCheckout` / `startCashOut` / `sendCashOut` / `startSale` / `sendSale` are the
state machine both the React hooks and any non-React host run: lock → claim → persist → send → resolve the
key by the answer. They **never reject**: a transport that rejects, resolves to anything but a well-formed
`MoneyResult` whose `value` is a view (e.g. `{ ok: true, value: null }`), or takes longer than
`sendTimeoutMs` (default 45 s), is `unconfirmed` and the key survives (L4, LOW-2). `sendCheckout` saves the
intent id **before** returning `opened`, so a host that navigates to `paymentUrl` immediately cannot lose it;
`paymentUrl` is set only while the intent is open (`isOpenCheckout`) and admitted by the caller's
`redirectPolicy`. Every checkout view goes through one rule: settled → the key is cleared, `finished`;
terminal but unsettled → the key is kept, `finished` (nothing new can be charged; poll it); otherwise the key
is kept, `opened` (with `paymentUrl: null` when there is nothing to pay, e.g. `processing`).

- **Decide by state (HIGH-1, LOW-1).** When the saved request already has a `serverId`, **no refusal from
  `begin` releases its key**: not `INTENT_EXPIRED`/`INTENT_FINISHED`, not `PAYMENTS_NOT_CONFIGURED`, not
  anything. `decideByState` reads the intent and applies the rule above to what it sees, and a refused send
  never hands out a payment page (an open one is shown, not navigated to). If the read fails too, the
  attempt is `refused` with the read's refusal and `kept` = the request: the key stays and the intent stays
  watched. This is why the node lane's first cut (`769494f1`, `INTENT_EXPIRED` on any lapsed page) cannot
  release the key of an ACH payment that is still clearing.

- **A resent key's final-looking refusal is decided by state, not the code (round 3, round 4).** When
  `beginCheckout` resends a key (`sendCheckout`, or `startCheckout` reusing a saved key: `claimKey` reports
  `resent`) that names no intent, a refusal `releasesRequestKey` would accept (`INTENT_FINISHED`,
  `REQUEST_KEY_IN_USE`, a gate that appeared since the first send, ...) proves nothing about the **earlier**
  send by itself. `checkoutProjectionFrom` first checks whether the 409's own envelope already carries that
  intent's projection (`serverResult`, allowlist-narrowed at the intake, §intake): when it does (round 4,
  `33168340`'s `INTENT_FINISHED` / `INTENT_EXPIRED`), `applyCheckoutView` decides it directly -- no round
  trip, and still never releases the key unless that state is **settled** (an unlanded card payment keeps
  it). Only when no projection is present does `releaseOnVoid` ask the node: `voided` → the key goes and the
  attempt is the original refusal; `revealed` → the key follows that intent (`applyCheckoutView`, no
  redirect); anything else → kept. A key's first send still releases on such a refusal directly: nothing
  else was ever sent under it.

- **Never started (M2a, B9).** A claim whose first Stripe create failed has no page (status `starting`). From
  4 min to 23 h after its creation fence began, the node's resend answers 409 `INTENT_EXPIRED` with that
  `starting` projection: the pinned expiry is inside Stripe's 31-minute floor, so no page can ever be created,
  and none ever left the node (since node `b97edf60` it closes the claim with the void's abandon CAS before
  answering, so a stalled earlier attach cannot hand one out and a later resend gets `REQUEST_VOIDED`). Applying it as an ordinary view showed "Waiting for your payment" with the form
  locked and no Discard. So `beginCheckout` checks `isNeverStartedClaim` (`starting`, `not_started`, no
  `paymentUrl`, nothing paid) on the definite projection first: the key is cleared and the attempt is
  `neverStarted` (copy `pending.neverStarted`: "That payment never started. Nothing was charged."; the form is
  free for a fresh payment, none is started). `starting` proves this **only** inside a definite 409: a GET or a
  `needs_review` answer with the same projection keeps the key (a create may still attach).

- **Acknowledge (M3).** `complete` + `payment_received` is terminal (a same-key resend can never charge it
  again) but not settled, so on a node whose funds never land (no issuance handler, crediting dead-lettered)
  the key used to be kept forever: resume → `finished`, kept; discard → `revealed`, kept. `acknowledgeCheckout(keyStore,
  view)` is the person's exit: it forgets the saved key only when `isAwaitingFundsCheckout(view)` and the key
  names `view.intentId`, and starts nothing. The UI says the payment was received and the funds are still
  being added (`checkout.acknowledged`), never that they landed. Anything non-terminal (`processing`, paid at
  Stripe but not closed) is not acknowledgeable and keeps polling.

- **Resend re-reads storage (L1).** `send*` re-reads the saved request inside the lock and sends only when
  its key is still the saved one; otherwise it returns `superseded` with what is saved now and sends
  nothing (another tab resolved, discarded or replaced it).
- **Cross-tab lock (L8).** `localStorage` is not atomic across tabs: two tabs can both read an empty slot and
  both mint a key. Every `start*`/`send*` runs claim-and-send inside `navigator.locks.request("azoa-sdk:money:<scope>")`
  when the runtime has Web Locks, so the second tab claims only after the first persisted (it then reuses
  that key, or sees `pending`). The lock is held across the send so the first tab's write has propagated.
  Without Web Locks (old browsers, SSR, jsdom) it runs unlocked; a runtime that throws on `request` before
  running the task falls back to running it once. The lock is requested with a timeout signal
  (`AbortSignal.timeout(lockTimeoutMs)`, default 60 s, longer than one send): a tab that cannot get it in time
  answers `rail_busy` ("another window is still sending; nothing was sent from here") without claiming or
  sending. Together with `sendTimeoutMs` this bounds how long a hung host transport can hold every tab (LOW-2).
- **A refused cash-out replay is a refusal (L2).** `OffRampService` claims the row first and refuses it
  durably; a same-key retry then answers **200 with the `Refused` row**. `sendCashOut` (and `sendSale`)
  treat a view whose `status` is `refused` (any case) as a `definite` refusal carrying its
  `statusMessage`, so no host shows it as success or fires `onSettled`.

## §void

**Rule** (the money-key doctrine): a saved request key is the lock against a second payment, and it may be
released only on a server-set value whose emitter proves nothing was or can be paid under it. A client-side
discard cannot prove that: only the node knows what was admitted, and a begin still on the wire can be admitted
after the client forgot the key. The node's answer is void-or-reveal by request key (oasis-sleek
`FiatRailProvider.Void.cs`, `Providers/Fiat/AGENTS.md` §request-void, f2cb4fe8):

| node answer | `checkout.void` result | key |
| :-- | :-- | :-- |
| 200 `voided`, `intentId: null` (nothing admitted: a tombstone is written; a late begin then gets 409 `REQUEST_VOIDED`) | `{ outcome: "voided", intentId: null }` | **released** |
| 200 `voided`, `intentId` (unattached claim abandoned, or an open page expired at Stripe, then abandoned) | `{ outcome: "voided", intentId }` | **released** |
| 200 `revealed` + `intent` (Stripe paid/complete, clearing, or terminal) | `{ outcome: "revealed", intent }` (narrowed: no `admissionKey`) | follows the intent: kept unless settled |
| 503 `DEPENDENCY_UNAVAILABLE` / `RAIL_BUSY` / `PAYMENTS_NOT_CONFIGURED` / `RAIL_UNAVAILABLE`, 409 `REQUEST_KEY_IN_USE`, 400, 403, lost | a refusal | **kept** (whatever its code) |
| 409 `INTENT_NEEDS_REVIEW` (33168340: an unattached claim past its replay window or quarantined) | `needs_review` | **kept**, marked |
| 200 `voided` for that same claim (the node lane's announced change: any unattached `CLAIMED` row is abandoned) | `{ outcome: "voided", intentId }` | **released** |

Voiding twice gives the same answer; a voided intent reads `status: "expired"` (settled).

- `discardCheckout(transport, keyStore, request)` runs under the request lock, re-reads storage
  (`superseded` if another tab changed it) and calls `voidOrReveal`. Results: `discarded` (`intentId` of the
  voided intent, or null), `revealed` (the key now names that intent and follows its state; no page is handed
  out), `refused` (kept; `needs_review` marks it).
- **Only `outcome === "voided"` releases.** The intake narrows a void answer with `toCheckoutVoidView` (§intake), which accepts the literal outcomes only (a host's
  `"Voided"`, `null` value or missing intent keeps the key), and a void refusal whose code `releasesRequestKey`
  would accept (`payments_not_configured` from the 503, `definite` from a BFF's 400 or `REQUEST_KEY_IN_USE`)
  becomes `unconfirmed`: a void that could not decide proves nothing.
- The direct transport calls `AzoaApiClient.voidFiatCheckoutRequest(userId, requestKey)` (the raw key as one
  `encodeURIComponent` path segment, no body) and narrows with `toCheckoutVoidView`, which throws (→
  `unconfirmed`) on anything but the two literal outcomes.
- Known gap: a key the node binds to another account (`REQUEST_KEY_IN_USE` on both begin and void) is kept for
  good. `BuildAdmissionKey` embeds the subject, so the node cannot reach it today; if it ever can, the person
  needs an operator, not a second payment.

## §presentation

**Checkout states (H2, HIGH-1).** "Nothing new can be charged" and "the money is settled" are different
facts, so there are two predicates. The real node (`FiatRailProvider.ProjectAsync`) sends `complete` +
`payment_received` once the webhook marks the session complete, and `funds_applied` only later, from a
separate applied inbox row; `processing` while an asynchronous method (ACH) clears; `expired` +
`payment_failed` when Stripe expired the page. When its Stripe re-read fails it now answers 503
`DEPENDENCY_UNAVAILABLE` (never a 200 `not_started`); older nodes sent `complete` + `not_started`.

| predicate | true for | used for |
| :-- | :-- | :-- |
| `isTerminalCheckout` | status `complete`/`failed`/`expired`, or settled | `finished` (a same-key resend cannot charge again) |
| `isSettledCheckout` | settlement `funds_applied`/`payment_failed`, or `failed`/`expired` with `not_started` (funds applied, or provably unpaid and closed) | forgetting the saved key, stopping the poll, `onSettled`, unlocking the add-funds form |
| `isAwaitingFundsCheckout` | `complete` + `payment_received` (paid and closed, funds not landed) | offering the person's acknowledgement (`acknowledgeCheckout`, §flows) |
| `isOpenCheckout` | `awaiting_payment` + `not_started` | offering the payment page |

An unrecognized status or settlement value is never terminal, settled or open (allowlists again).
`describeSettlement` never says "waiting for your payment" unless the page is still open: `processing` says
`checkout.clearing` ("still clearing"), `complete` with nothing cleared says `checkout.confirming`; only
`funds_applied` says the balance moved. `checkout.cannotConfirm` is for a read that failed (see
`src/react/AGENTS.md` §checkout-status).

**Amounts (L5).** `currencyMinorDigits(code)` is the ISO 4217 minor unit (an embedded table: List One,
2025; 0 for JPY/KRW/..., 3 for KWD/BHD/..., 4 for CLF/UYW, 2 for the rest), or null for a code the SDK does
not know. `toMinorUnits(text, currency)` parses with those digits and returns null for an unknown currency
(no amount is ever guessed with two decimals: `"2500"` JPY is 2500, not 250000). `formatMinorUnits` returns
null for an unknown currency; `minorUnitsToText` is the plain text the field parses back. Grouping is
`en-US` so it never mixes with the `.` separator. `toBaseUnits` / `formatBaseUnits` use BigInt only.

**Redirects (L3, LOW-6).** `safeRedirectUrl(url, policy?)` returns the parsed `href` (never the raw string)
only for an `https:` URL without userinfo (`https://trusted@elsewhere` reads as trusted and goes elsewhere)
whose exact origin is in `DEFAULT_REDIRECT_ORIGINS` or `policy.allowedOrigins`. **The default is safe**: with
no policy, only the hosted pages the AZOA node actually hands out pass. `allowedOrigins` **extends** the
list (a Stripe Checkout custom domain, a Veriff or generic hosted-KYC origin, the host's own account page);
it never replaces it. `http:` on loopback needs `policy.allowLoopbackHttp` (a development flag).

| built-in origin | what the node returns there | evidence (oasis-sleek, read-only) |
| :-- | :-- | :-- |
| `https://checkout.stripe.com` | checkout `paymentUrl` (Stripe Checkout Session `url`) | `Providers/Fiat/StripeGateway.cs` reads the session `url` → `FiatRailProvider` `PaymentUrl`; Stripe API docs, Checkout Session object (`url: https://checkout.stripe.com/c/pay/cs_test_…`) |
| `https://connect.stripe.com` | payout `setupUrl` (Stripe Connect Account Link `url`) | `Providers/Fiat/StripeConnectGateway.cs` `CreateAccountLinkAsync` (`/v1/account_links`) → `PayoutAccountService` `SetupUrl`; Stripe API docs, Account Link object (`url: https://connect.stripe.com/setup/c/…`). Dormant once Option C (platform-collected setup, `setupUrl` always null) lands |
| `https://widget.identomat.com` | KYC `verificationUrl` (Identomat `/begin` `url`) | `Providers/Kyc/IdentomatKycProviderService.cs` (`RedirectUrl` → `VerificationUrl`); `appsettings.json` `Kyc:Provider: identomat`, `ResultBaseUrl`; `.omc/research/identomat-api-2026-10-15.md` (`"url": "https://widget.identomat.com/?session_token=…"`) |

Not built in, because the node pins them per deployment: Veriff (`Kyc:VeriffRedirectOrigins`) and the
generic hosted adapter (`Kyc:Hosted:RedirectOrigins`). A host using either lists that origin.

`defaultMoneyMessages` is the full copy map; `mergeMoneyMessages` merges a host override section by section.
`pending.summary[kind](amount)` gets `null` when the currency is unknown, and the default copy then names no
amount.

## §direct-transport

`createDirectMoneyTransport(client, userId, options?)` is for AZOA's own first-party apps. It **throws**
when `client.hasApiKey()`: a tenant key in a browser bundle is the exact leak this package exists to
prevent. That check is defense in depth; the primary guard is `AzoaApiClient`'s constructor, which refuses
an `apiKey` in any browser runtime (see `src/api/AGENTS.md` §api-key-in-browser). Every answer is narrowed
through its `to*View`, and a narrowing that throws on an unreadable body is `unconfirmed`, never a
rejection. First-party differences: `account.ensure` is a status read (a login is its own account);
`account.status` maps `/api/kyc/status` (404 = not started) and reports the wallet best-effort, with
`ready` keyed on KYC approval alone because that is what checkout gates on; `revokeConsent` is a
`definite` refusal (a first-party session has no app permission to withdraw); there is no `sale`;
`balance` mirrors cash-out availability; `checkout.void` is the node's void-or-reveal (§void). Every call is
wrapped so the client's own guards (which throw inside async methods) become refusals.

## §contract

`azoa-sdk/money/testing`: `moneyTransportContractCases(factory)` (one case per test in any runner) and
`runMoneyTransportContract(factory, { requireAll })` (a report). Cases: every read resolves to a
well-formed `MoneyResult` whose refusal code is in the closed vocabulary (now including `needs_review`; a host
BFF must map the node's `INTENT_NEEDS_REVIEW` to it, never to `unconfirmed` or `definite`); no view **or
refusal** carries subject/tenant keys (a refusal's `serverResult` reaches the page like a view, M1) — checked over **every** call, mutations
included (`checkout.begin`, `checkout.get(<the id begin returned>)` — a read of an unknown intent is a refusal
and cannot leak, so only this one catches a leaky `get` — `checkout.void`, and with fixture controls a second
checkout's same-key resend after `markPaid`, whose answer is the 200 projection on the node today and a final
409 whose `serverResult` is the intent on a fixture whose `markPaid` also closes it; `account.ensure`,
`account.beginKyc`, `cashOut.beginDestinationSetup`, `cashOut.start`, `sale.quote`, `sale.sell`, and
`account.revokeConsent` last so it cannot gate the others; each case builds its own fixture); same key → same checkout (`resumed: true`)
and readable via `get`; new key → new checkout; unknown/malformed intent → a key-releasing refusal (or a
poller never stops); same key → one cash-out, listed once; money is integer minor units and quantities are
base-unit strings. Void cases (§void): an unused key → `voided` with no intent, twice, and a later begin is
refused with a key-releasing code (`REQUEST_VOIDED`); an open unpaid checkout → `voided` naming it, twice, then
reads settled and never opens again; a paid checkout → `revealed` (twice, narrowed), never voided, still
resumable; an unreadable provider → a refusal whose code keeps the key. `checkout.void` is also in the
forbidden-key and amount sweeps. The paid and unreadable cases need fixture controls: the factory may return
`{ transport, checkout: { markPaid(intentId), makeUnreadable(intentId) } }` instead of a bare transport
(still accepted); without controls those two skip. Cases that need fixture state (an admitted checkout,
something cashable) skip with a reason; `requireAll` turns skips into failures. The SDK's own test proves the
contract fails a transport that mints a new key per call and leaks `admissionKey`.
