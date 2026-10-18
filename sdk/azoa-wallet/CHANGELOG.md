# Changelog

`azoa-sdk` follows semver; while the major version is 0, a breaking change bumps the minor version.

## Unreleased

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
