# Changelog

`azoa-sdk` follows semver; while the major version is 0, a breaking change bumps the minor version.

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
