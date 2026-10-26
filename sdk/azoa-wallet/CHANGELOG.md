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
