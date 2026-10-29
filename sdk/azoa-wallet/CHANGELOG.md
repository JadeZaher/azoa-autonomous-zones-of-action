# Changelog

`azoa-sdk` follows semver; while the major version is 0, a breaking change bumps the minor version.

## 0.6.0

The breaking change below is why this is a minor bump; everything under "Added (money UI)" is additive.

### Added (money UI, SR-05 P0 + P1)
- `azoa-sdk/money` (framework-free): the `AzoaMoneyTransport` seam a host injects (no `userId` anywhere;
  the subject is the host session), `MoneyResult` / `MoneyRefusal` with a closed refusal vocabulary and
  `refusalFromSdkError`, browser-safe views (`CheckoutView` without `admissionKey`, `CustodialAccountView`
  without tenant/subject/user ids) with allowlist narrowing helpers, persisted request keys
  (`createKeyStore`, `claimRequestKey`, `rememberServerId`, `clearSavedRequest`; per-subject, saved before
  the request leaves), the keyed flows (`startCheckout`/`sendCheckout`, `startCashOut`/`sendCashOut`,
  `startSale`/`sendSale`, `resolveCheckoutByState`), amount helpers, the default copy map, and
  `createDirectMoneyTransport` for first-party apps (it refuses a client configured with an `apiKey`).
- `azoa-sdk/money/testing`: `runMoneyTransportContract` / `moneyTransportContractCases`, a
  framework-agnostic contract every transport must pass.
- `azoa-sdk/react` (`"use client"` entry; React `>=18.2 <20` as an optional peer): `AzoaMoneyProvider`,
  the hooks `useCustodialAccount`, `useAddFunds`, `useCheckoutStatus`, `useCashOut`, `useSellUnits`,
  `useBalance`, and unstyled components (`MoneyGate`, `KycStep`, `ConsentStatus`, `CustodialAccountCard`,
  `AddFundsForm`, `CheckoutStatus`, `WithdrawForm`, `PayoutDestinationStep`, `SellUnitsForm`,
  `BalanceSummary`, `PendingRequestNotice`) with `slots` and `className`. The host navigates
  (`onRedirect`); the SDK never sets `location`.
- `AzoaApiClient.hasApiKey()`.

### Security fix round (pre-publish review, 2026-10-27)
- `releasesRequestKey` is an allowlist: only `definite`, `setup_required`, `under_review`, `consent_revoked`,
  `account_holder_required`, `kyb_required` and `payments_not_configured` forget a saved key; any other or
  unknown code keeps it.
- Checkout state is two facts: `isTerminalCheckout` (the key may be forgotten) and `isSettledCheckout` (the
  money outcome is final). `complete` + `payment_received` and `complete` + `not_started` keep polling until
  `funds_applied`/`payment_failed`; `onSettled` fires only then. `isFinishedCheckout` is removed.
  `isOpenCheckout` gates the payment link; "waiting for your payment" is shown only for an open page.
- `refusalFromSdkError` reads the node envelope `code` before status and text: 409 `INTENT_FINISHED`,
  `INTENT_EXPIRED`, `REQUEST_KEY_IN_USE` are `definite`; `INTENT_NEEDS_REVIEW` and any other 409 stay
  `unconfirmed`; message text counts only on a definite status (a 503 saying `KYB_REQUIRED:` keeps the key).
- `AzoaApiClient` refuses an `apiKey` in a browser unless `dangerouslyAllowApiKeyInBrowser: true`, copies its
  config, and `hasApiKey()` uses `Boolean(apiKey)`.
- The provider rebuilds its account store and every hook resets per-person state when the subject or key
  store changes; answers for the previous person are dropped.
- Flows hold a Web Lock around claim-and-send where available, re-read the saved request before a resend
  (`superseded` when another tab changed it), never reject, and treat `{ ok: true, value: null }` as
  `unconfirmed`. A same-key replay of a refused cash-out (200 + `Refused` row) is a `definite` refusal.
- Host callbacks run through a guard and every form resets `busy` in a `finally`.
- `safeRedirectUrl(url, policy?)` returns the parsed `href`, refuses userinfo, admits loopback `http:` only
  with `allowLoopbackHttp`, and honors `allowedOrigins`; the provider takes `allowedRedirectOrigins` and
  `allowLoopbackRedirects`.
- Amounts use the currency's ISO 4217 minor units (`currencyMinorDigits`); `toMinorUnits(text, currency)`
  and `formatMinorUnits` return null for an unknown currency. `AddFundsForm` now requires `currencyCode`.
- Every view has an allowlist `to*View`; the direct transport narrows cash-out, payout and capability
  answers, and the contract's forbidden-key case covers every call, mutations included.

### Sixth round: final-gate LOW findings (pre-publish, 2026-10-28)
- **LOW-2** Every transport answer is narrowed once, at the intake (`src/money/intake.ts`): the flows' `settled()`
  and the hooks' `settleMoney()` run each value through its allowlist view (`toCheckoutView`, `toCheckoutVoidView`,
  `toCashOutView`, ...) and rebuild each refusal from allowlisted fields (unknown code → `unconfirmed`;
  `serverResult` only via `toRefusalServerResult`). A host transport that forwards the node's raw objects can no
  longer put `admissionKey` or subject/tenant/provider ids into attempts, hook state or `onRefusal`.
- The contract's forbidden-key and amount sweeps also read back the checkout `begin` opened (`checkout.get(<id>)`)
  and, with fixture controls, resend a paid checkout's key after `markPaid`.
- `SdkError.toJSON()` no longer includes the unnarrowed `serverResult` (it stays on the error object).
- **LOW-4** `useAddFunds().acknowledge(view)` shows `checkout.acknowledged` only when `acknowledgeCheckout` actually
  forgot this device's key.
- `tests/money/node-emulator.ts` mirrors the node's `rowRecordedTheEnd` projection rule (**LOW-3**: a fresh Stripe
  `complete` reads `processing` unless the row recorded `complete`, or `failed` on an unpaid page) and the node's
  lapsed-claim abandon before `INTENT_EXPIRED` (oasis-sleek `b97edf60`).

### Fifth round: release-gate fixes (pre-publish, 2026-10-28)
- **M1** `MoneyRefusal.serverResult` is now `RefusalServerResult` (was `unknown`) and is attached only for
  `INTENT_FINISHED` / `INTENT_EXPIRED` / `INTENT_NEEDS_REVIEW`, narrowed by the new `toRefusalServerResult`; the
  node's raw `result` (whose `admissionKey` embeds subject and tenant ids) no longer reaches attempts, hook state
  or `onRefusal`. The contract's forbidden-key case now checks refusals too. Decide-by-projection trusts only a
  `definite` refusal's projection (LOW-2).
- **M2** A claim whose first Stripe create failed no longer traps the person. A resend answered `INTENT_EXPIRED`
  on a `starting` claim (no page ever existed) releases the key as the new `CheckoutAttempt` kind `neverStarted`
  (copy `pending.neverStarted`). A request held for review keeps **Discard** for checkouts: the node's void
  decides (`voided` releases; `revealed` / 409 keeps), so `useAddFunds().discard()` no longer refuses it.
- **M3** A paid intent whose funds never land can be acknowledged: `acknowledgeCheckout(keyStore, view)` and
  `isAwaitingFundsCheckout` (`complete` + `payment_received`), `useAddFunds().acknowledge(view)`, and a "Got it"
  button in `<AddFundsForm>`'s status card (copy `checkout.acknowledge` / `checkout.acknowledged`). It releases the
  key and starts nothing; non-terminal states keep polling as before.
- `tests/money/node-emulator.ts` models unattached claims (`starting` projections, the creation fence, the
  pinned-expiry floor, the 23 h replay window, `failNextCreate`), and the announced void change behind
  `unreplayableClaimVoid`; the `expiryBeforeStripeRead` first-cut mode is gone.

### Fourth round: expire-first resend, decide by projection (pre-publish, 2026-10-28)
Synced to the node's fiat branch at `33168340`: a same-key resend on a page past its clock expiry no longer
trusts the clock alone (the node's lane-one bug this SDK already guarded against) -- the node expires the
page AT STRIPE first, same idempotency key as the void, then decides from a fresh read.
- Final same-key 409s (`INTENT_FINISHED`, `INTENT_EXPIRED`, `INTENT_NEEDS_REVIEW`) now carry the GET
  projection in the envelope's `result`; `SdkError.serverResult` (and `MoneyRefusal.serverResult`) surface it,
  unnarrowed, additive.
- `beginCheckout`'s resent-key branch decides by that projection directly (`checkoutProjectionFrom`, narrowed
  with `toCheckoutView`) instead of a void round trip when the 409 carries one; falls back to void-or-reveal
  when it does not (`REQUEST_KEY_IN_USE`, `REQUEST_VOIDED` still carry none). Never releases the key on
  `INTENT_FINISHED` unless that projection is settled -- the same rule `applyCheckoutView` already enforced.
- A terminal intent whose Stripe re-read fails now answers 503 `DEPENDENCY_UNAVAILABLE` (previously a bodiless
  409); `tests/money/node-emulator.ts` rebuilt to mirror both.

### Third round: void-or-reveal (pre-publish, 2026-10-28)
Discarding a saved checkout key is now the node's decision (oasis-sleek f2cb4fe8, `Providers/Fiat/AGENTS.md`
§request-void): a key is released only when the node answers `voided`.
- `AzoaApiClient.voidFiatCheckoutRequest(userId, requestKey)` (`FiatCheckoutVoidResult`), additive.
- `CheckoutTransport.void(requestKey)` → `MoneyResult<CheckoutVoidView>` (`{ outcome: "voided", intentId }` |
  `{ outcome: "revealed", intent }`), **required**: host transports must forward it (narrow `intent` with
  `toCheckoutView`). The direct transport implements it; `toCheckoutVoidView` narrows the node's answer.
- `discardCheckout(transport, keyStore, request)` → `CheckoutDiscardAttempt` (`discarded` | `revealed` |
  `refused` | `superseded`). Only `voided` clears the key; a void refusal keeps it whatever its code.
- A resent key that names no intent (a lost answer) and meets a key-releasing refusal (`INTENT_FINISHED`,
  `REQUEST_KEY_IN_USE`, a gate) is put to the void first; a revealed payment is followed by state. Closes round
  2's open item (`INTENT_FINISHED` with no intent id released the key).
- 409 `REQUEST_VOIDED` → `definite`.
- `useAddFunds().discard()` is async and runs the void; new copy `pending.voided`, `pending.revealed`,
  `pending.discardUnconfirmed`; `pending.detail.checkout` reworded.
- `azoa-sdk/money/testing`: four void cases; a factory may return `{ transport, checkout: { markPaid,
  makeUnreadable } }` for the two that need fixture control.

### Second fix round (pre-publish re-review, 2026-10-27)
Guiding rule: the saved request key is the lock against a second payment; only proof that nothing was or can
be paid under it may release it.
- A saved checkout that already names its intent (`serverId`) is **decided by state**: no `begin` refusal
  (`INTENT_EXPIRED`, `INTENT_FINISHED`, `PAYMENTS_NOT_CONFIGURED`, anything) releases its key; the flow reads
  the intent and releases only on a settled view. A refused send never hands out a payment page.
- `resolveCheckoutByState` and every checkout answer release the key only when the intent is **settled**
  (`isSettledCheckout`); a terminal intent whose funds are still landing keeps it (`finished`, key kept).
- `<AddFundsForm>` locks new payments while it watches any intent without a settled read (open, `processing`,
  `payment_received`, unreadable); `discard()` also stops watching the discarded intent.
- New refusal code `needs_review` (409 `INTENT_NEEDS_REVIEW`): keeps the key, marks the saved request
  `needsReview` (`markNeedsReview`), and the UI shows "needs a manual check — we'll follow up" with no resume
  prompt and no fresh payment, only a same-key "Check again".
- A failed status read after a good one shows `checkout.cannotConfirm` and no payment page instead of the
  stale "Waiting for your payment"; `processing` says `checkout.clearing`.
- Flows take `MoneyFlowOptions` (`sendTimeoutMs`, default 45 s; `lockTimeoutMs`, default 60 s): a hung host
  transport is `unconfirmed` with the key kept, and a tab that cannot get the Web Lock in time answers
  `rail_busy` without sending. `startCashOut`/`sendCashOut`/`startSale`/`sendSale` gained the optional
  options argument.
- The provider exposes an `identity` token (transport + key store + subject); hooks reset during render, so
  a new person's first paint has none of the previous person's state, even with a stable `keyStore` prop, and
  late answers for the previous person never apply or redirect.
- `pending.superseded` no longer claims the request settled; `pending.replaced` covers a request another
  window replaced.
- `AzoaApiClient` also refuses an `apiKey` in React Native (`navigator.product === "ReactNative"`).
- **Redirect default is now safe** (behavior change, not breaking in practice: no host has shipped on 0.6.0
  yet). Without `allowedRedirectOrigins` / `policy.allowedOrigins`, only `DEFAULT_REDIRECT_ORIGINS` pass
  (`https://checkout.stripe.com`, `https://connect.stripe.com`, `https://widget.identomat.com`, the hosted
  pages the node returns); the option now **extends** that list instead of replacing "any https origin". Hosts
  on a Stripe custom domain, Veriff or a generic hosted-KYC provider must list that origin, and
  `PayoutDestinationStep`'s `accountHolderSetupUrl` must be on a listed origin.

### Breaking
- `ApiKeyInfo.scopes` and `ApiKeyCreateResult.scopes` are now `string[]`, normalized from the
  comma-separated string the API stores (`normalizeApiKeyScopes`). An empty list means legacy full
  access. `createApiKey({ scopes })` accepts a list or the CSV and sends the CSV.

### Fixed
- `login()` / `register()` and the wallet challenge/verify/claim calls never run the token refresh:
  a 401 there is the server's refusal ("Invalid credentials."), not an expired session. Before this
  fix a wrong password surfaced as `NETWORK_ERROR` "network request failed".
- `listFungibleCreations()` reads the bare array `GET /api/fungible-creation` returns; it resolved to
  `undefined` before. A body that is not a list is an error, never an empty list.
- Error bodies in ASP.NET `ValidationProblemDetails` shape (`errors: { Field: [..] }`) now reach
  `SdkError.message` instead of "failed with HTTP 400".

### Added
- `SdkError.serverMessage` (the server's message without the `METHOD /path:` prefix) and
  `userFacingMessage(error)` for copy shown to people.

## 0.5.0

### Breaking
- Removed the `azoa-sdk/dex` subpath export, the `DexAdapter`/Jupiter/Tinyman adapters and the Tinyman
  peer dependency. Swaps go only through the backend's durable swap lifecycle.
- Removed `wallet.getSwapQuote()` and `wallet.buildSwap()`.
- `clearWalletIdentityConflict()` now resolves to `boolean`, which is what the server returns
  (`AZOAResult<bool>`). Call `getWalletIdentityMigration()` again to read the updated view.

### Fixed
- `listEthereumPreparations()` (user and operator) and `getWalletIdentityMigration()` read the bare
  bodies those controllers return. Before this fix they resolved to `undefined`.

### Added
- Round-4 contracts: KYC session/refresh, wallet ownership proof, signing plan and submit-signed,
  stuck client-signed operations, deployment/payout/reversal/retired-row resolutions, hold
  liabilities, the tenant payout float and top-up, WTT/DEX/wallet-identity operator routes, and the
  reconcile-sweep shape. Shared `assertHex32`/`assertHex64` guards.
