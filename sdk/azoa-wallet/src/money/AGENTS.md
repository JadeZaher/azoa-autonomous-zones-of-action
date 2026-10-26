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
belong in the operator console, QA L2). `toCheckoutView` / `toCustodialAccountView` narrow at runtime with
an **allowlist**, so a field the node adds later cannot leak by default. `findForbiddenViewKeys` is the
deep check the contract runs.

`deriveAccountGate`: `ready` (the only state that moves value) | `setup` (verify identity / finish the
account; also a `setup_required` refusal, e.g. no account yet) | `review` (`UnderReview`: verified, a
compliance check owes a human decision) | `revoked` (`consent_revoked`) | `unavailable` (the node cannot
onboard right now, or any other refusal).

## §refusals

Closed vocabulary, mapped from node bodies by `refusalFromSdkError` (verified against the controllers on
2026-10-26):

| code | node evidence | key |
| :-- | :-- | :-- |
| `consent_revoked` | 403 whose message names consent (`TenantConsentGate.Deny`) | released |
| `account_holder_required` | 403 "…only be done by the account holder" (F-H8, payout setup) | released |
| `kyb_required` | `isKybRequired` (D-6, `KYB_REQUIRED` code or prefix) | released |
| `setup_required` | "KYC verification required" (`KycAuthorizationError`) | released |
| `under_review` | definite refusal mentioning "under review" (AML review) | released |
| `payments_not_configured` | `PAYMENTS_NOT_CONFIGURED` (QA M3) | released |
| `definite` | any other 400/403/404/422, or a client guard that threw before egress | released |
| `rail_busy` | `RAIL_BUSY`, `RAIL_UNAVAILABLE`, 429 | **kept** |
| `unconfirmed` | network loss, timeout, 409, 5xx | **kept** |
| `unauthenticated` | 401 / `AUTH_EXPIRED` | **kept** |

`releasesRequestKey` encodes the last column: only a definite answer may forget a key, because anything
else may hide an admission. `refusal.message` is the server's own sentence (wire-code prefixes and
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
  `resolveCheckoutByState` forgets the key only when that intent is finished. `clearSavedRequest(store,
  scope, requestKey)` clears only while that key is still the saved one (another tab may have replaced it).
- **Per subject.** `createKeyStore({ subject, storage? })` hashes the subject (cyrb53, namespacing only)
  into every storage key, so two people on one device never see each other's pending payments. It throws on
  an empty subject rather than share a slot. Default storage is `localStorage`; with none (SSR, sandboxed
  frames) it is per-instance memory, never module-level (a server must not share keys across requests).
- `newRequestKey` falls back to `getRandomValues` where `crypto.randomUUID` is missing (non-secure origins).

## §flows

`startCheckout` / `sendCheckout` / `startCashOut` / `sendCashOut` / `startSale` / `sendSale` are the
state machine both the React hooks and any non-React host run: claim → persist → send → resolve the key by
the answer. `sendCheckout` saves the intent id **before** returning `opened`, so a host that navigates to
`paymentUrl` immediately cannot lose it. A same-key answer that is already finished returns `finished`
(the key is cleared; a resumed finished intent means nothing new was charged).

## §presentation

`isFinishedCheckout`, `isDefiniteRefusal`, `toMinorUnits`, `formatMinorUnits` are lifted unchanged.
`toMinorUnits` assumes two-decimal currencies (as the node's checkout does today). `toBaseUnits` /
`formatBaseUnits` use BigInt only. `safeRedirectUrl` admits `https:` (and `http:` on loopback) so a
transport answer can never hand a `javascript:` URL to a link or to the host's navigation.
`defaultMoneyMessages` is the full copy map; `mergeMoneyMessages` merges a host override section by section.
`describeSettlement`: only `funds_applied` says the balance moved.

## §direct-transport

`createDirectMoneyTransport(client, userId, options?)` is for AZOA's own first-party apps. It **throws**
when `client.hasApiKey()`: a tenant key in a browser bundle is the exact leak this package exists to
prevent. First-party differences: `account.ensure` is a status read (a login is its own account);
`account.status` maps `/api/kyc/status` (404 = not started) and reports the wallet best-effort, with
`ready` keyed on KYC approval alone because that is what checkout gates on; `revokeConsent` is a
`definite` refusal (a first-party session has no app permission to withdraw); there is no `sale`;
`balance` mirrors cash-out availability. Every call is wrapped so the client's own guards (which throw
inside async methods) become refusals.

## §contract

`azoa-sdk/money/testing`: `moneyTransportContractCases(factory)` (one case per test in any runner) and
`runMoneyTransportContract(factory, { requireAll })` (a report). Cases: every read resolves to a
well-formed `MoneyResult`; no view carries subject/tenant keys; same key → same checkout (`resumed: true`)
and readable via `get`; new key → new checkout; unknown/malformed intent → a key-releasing refusal (or a
poller never stops); same key → one cash-out, listed once; money is integer minor units and quantities are
base-unit strings. Cases that need fixture state (an admitted checkout, something cashable) skip with a
reason; `requireAll` turns skips into failures. The SDK's own test proves the contract fails a transport
that mints a new key per call and leaks `admissionKey`.
