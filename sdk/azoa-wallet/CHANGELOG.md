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
