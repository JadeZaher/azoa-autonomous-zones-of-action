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

`deriveAccountGate`: `ready` (the only state that moves value) | `setup` (verify identity / finish the
account; also a `setup_required` refusal, e.g. no account yet) | `review` (`UnderReview`: verified, a
compliance check owes a human decision) | `revoked` (`consent_revoked`) | `unavailable` (the node cannot
onboard right now, or any other refusal).

## §refusals

Closed vocabulary, mapped from node bodies by `refusalFromSdkError` (verified against the controllers on
2026-10-26, and `FiatRailProvider.BeginCheckoutAsync` on 2026-10-27):

| code | node evidence | key |
| :-- | :-- | :-- |
| `consent_revoked` | 403 whose message names consent (`TenantConsentGate.Deny`) | released |
| `account_holder_required` | 403 "…only be done by the account holder" (F-H8, payout setup) | released |
| `kyb_required` | `KYB_REQUIRED` code on 400/403/422, or the prefix on a definite status (D-6) | released |
| `setup_required` | "KYC verification required" (`KycAuthorizationError`) | released |
| `under_review` | definite refusal mentioning "under review" (AML review) | released |
| `payments_not_configured` | `PAYMENTS_NOT_CONFIGURED` code on 400/422/503 (QA M3) | released |
| `definite` | 409 `INTENT_FINISHED` / `INTENT_EXPIRED` / `REQUEST_KEY_IN_USE`; any other 400/403/404/422; a client guard that threw before egress | released |
| `rail_busy` | `RAIL_BUSY`, `RAIL_UNAVAILABLE`, 429 | **kept** |
| `unconfirmed` | network loss, timeout, 409 without a final code (incl. today's `CONFLICT`, `INTENT_NEEDS_REVIEW`), 5xx | **kept** |
| `unauthenticated` | 401 / `AUTH_EXPIRED` | **kept** |

**Order matters** (each step only runs when the previous did not decide): not an `SdkError` → `unconfirmed`;
401 → `unauthenticated`; no HTTP status → `definite` for a client guard, else `unconfirmed`; the envelope's
machine `code` (a key-releasing code counts only on the statuses the node sends it with, so `INTENT_FINISHED`
on a 500 is still `unconfirmed`); 429 → `rail_busy`; any status that is not 400/403/404/422 → `unconfirmed`
**whatever the message says**; only then message text. That last rule is why a 503 whose text says
`KYB_REQUIRED:` keeps the key (L6): text is evidence only inside a definite status.

The same-key 409 codes (`INTENT_*`, `REQUEST_KEY_IN_USE`) are the parallel node lane's; until it lands, the
node sends `CONFLICT` for every same-key 409 and those stay `unconfirmed` (the key survives; the person can
discard it explicitly). That is the safe side: a lapsed page then needs one "discard" click instead of
risking a second charge.

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
- **Resolved by state, not by landing page.** `rememberServerId` stores the intent id beside the key;
  `resolveCheckoutByState` forgets the key only when that intent is terminal (`isTerminalCheckout`; its money
  may still be settling, see §presentation). `clearSavedRequest(store,
  scope, requestKey)` clears only while that key is still the saved one (another tab may have replaced it).
- **Per subject.** `createKeyStore({ subject, storage? })` hashes the subject (cyrb53, namespacing only)
  into every storage key, so two people on one device never see each other's pending payments. It throws on
  an empty subject rather than share a slot. Default storage is `localStorage`; with none (SSR, sandboxed
  frames) it is per-instance memory, never module-level (a server must not share keys across requests).
- `newRequestKey` falls back to `getRandomValues` where `crypto.randomUUID` is missing (non-secure origins).

## §flows

`startCheckout` / `sendCheckout` / `startCashOut` / `sendCashOut` / `startSale` / `sendSale` are the
state machine both the React hooks and any non-React host run: lock → claim → persist → send → resolve the
key by the answer. They **never reject**: a transport that rejects, or resolves to anything but a
well-formed `MoneyResult` whose `value` is a view (e.g. `{ ok: true, value: null }`), is `unconfirmed` and
the key survives (L4). `sendCheckout` saves the intent id **before** returning `opened`, so a host that
navigates to `paymentUrl` immediately cannot lose it; `paymentUrl` is set only while the intent is open
(`isOpenCheckout`) and admitted by the caller's `redirectPolicy`. A same-key answer that is already
terminal returns `finished` (the key is cleared; its money may still be settling, so poll it).

- **Resend re-reads storage (L1).** `send*` re-reads the saved request inside the lock and sends only when
  its key is still the saved one; otherwise it returns `superseded` with what is saved now and sends
  nothing (another tab resolved, discarded or replaced it).
- **Cross-tab lock (L8).** `localStorage` is not atomic across tabs: two tabs can both read an empty slot and
  both mint a key. Every `start*`/`send*` runs claim-and-send inside `navigator.locks.request("azoa-sdk:money:<scope>")`
  when the runtime has Web Locks, so the second tab claims only after the first persisted (it then reuses
  that key, or sees `pending`). The lock is held across the send so the first tab's write has propagated.
  Without Web Locks (old browsers, SSR, jsdom) it runs unlocked; a runtime that throws on `request` before
  running the task falls back to running it once. Watch-out: a host transport that never settles holds the
  lock, so other tabs' money requests wait; transports must time out (the direct transport does, 30 s).
- **A refused cash-out replay is a refusal (L2).** `OffRampService` claims the row first and refuses it
  durably; a same-key retry then answers **200 with the `Refused` row**. `sendCashOut` (and `sendSale`)
  treat a view whose `status` is `refused` (any case) as a `definite` refusal carrying its
  `statusMessage`, so no host shows it as success or fires `onSettled`.

## §presentation

**Checkout states (H2).** "The key may be forgotten" and "the money is settled" are different facts, so
there are two predicates. The real node (`FiatRailProvider.ProjectAsync`) sends `complete` +
`payment_received` once the webhook marks the session complete, and `funds_applied` only later, from a
separate applied inbox row; it sends `complete` + `not_started` when its Stripe re-read fails, and
`expired` + `payment_failed` when Stripe expired the page.

| predicate | true for | used for |
| :-- | :-- | :-- |
| `isTerminalCheckout` | status `complete`/`failed`/`expired`, or settled | forgetting the saved key (a same-key resend is refused now) |
| `isSettledCheckout` | settlement `funds_applied`/`payment_failed`, or `failed`/`expired` with `not_started` | stopping the poll, `onSettled` |
| `isOpenCheckout` | `awaiting_payment` + `not_started` | offering the payment page |

An unrecognized status or settlement value is never terminal, settled or open (allowlists again).
`describeSettlement` never says "waiting for your payment" unless the page is still open: `complete` (or
`processing`) with nothing cleared says `checkout.confirming`; only `funds_applied` says the balance moved.

**Amounts (L5).** `currencyMinorDigits(code)` is the ISO 4217 minor unit (an embedded table: List One,
2025; 0 for JPY/KRW/..., 3 for KWD/BHD/..., 4 for CLF/UYW, 2 for the rest), or null for a code the SDK does
not know. `toMinorUnits(text, currency)` parses with those digits and returns null for an unknown currency
(no amount is ever guessed with two decimals: `"2500"` JPY is 2500, not 250000). `formatMinorUnits` returns
null for an unknown currency; `minorUnitsToText` is the plain text the field parses back. Grouping is
`en-US` so it never mixes with the `.` separator. `toBaseUnits` / `formatBaseUnits` use BigInt only.

**Redirects (L3).** `safeRedirectUrl(url, policy?)` returns the parsed `href` (never the raw string) when the
URL is `https:` without userinfo (`https://trusted@elsewhere` reads as trusted and goes elsewhere), and, with
`policy.allowedOrigins`, only for an exact listed origin (an empty list admits nothing). `http:` on loopback
needs `policy.allowLoopbackHttp` (a development flag); it is never admitted by default.

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
`balance` mirrors cash-out availability. Every call is wrapped so the client's own guards (which throw
inside async methods) become refusals.

## §contract

`azoa-sdk/money/testing`: `moneyTransportContractCases(factory)` (one case per test in any runner) and
`runMoneyTransportContract(factory, { requireAll })` (a report). Cases: every read resolves to a
well-formed `MoneyResult`; no view carries subject/tenant keys — checked over **every** call, mutations
included (`checkout.begin`, `account.ensure`, `account.beginKyc`, `cashOut.beginDestinationSetup`,
`cashOut.start`, `sale.quote`, `sale.sell`, and `account.revokeConsent` last so it cannot gate the
others; each case builds its own fixture); same key → same checkout (`resumed: true`)
and readable via `get`; new key → new checkout; unknown/malformed intent → a key-releasing refusal (or a
poller never stops); same key → one cash-out, listed once; money is integer minor units and quantities are
base-unit strings. Cases that need fixture state (an admitted checkout, something cashable) skip with a
reason; `requireAll` turns skips into failures. The SDK's own test proves the contract fails a transport
that mints a new key per call and leaks `admissionKey`.
