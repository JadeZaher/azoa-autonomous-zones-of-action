# API_SYNC.md — Controller ↔ SDK Regression Mapping

## Purpose

This document tracks drift between backend endpoints and SDK method coverage. Use this before shipping a controller change to catch missing SDK methods. The mapping is maintained manually at PR-review time; CI does not validate it.

## Unreleased Contract Change — SDK 0.3.0

Operation reads and operation-producing mutation routes now return a strict
public operation projection. Consumers must not depend on `parameters`, raw
idempotency keys, initiator/API-key metadata, signing context, or
operation-specific implementation fields. `BlockchainOperationResult` models
server-emitted nullable fields as `null` rather than omitted. Allocation,
fungible-token, and bridge responses likewise do not return raw idempotency
keys; use the request header to correlate a caller-owned request and the
allocation receipt endpoint for allocation status.

## SDK 0.3.1 — Waves 3-4 parity (W4.7)

Adds typed coverage for the Wave 3-4 value-rail surfaces that had shipped
backend-first with no SDK method: Hold (retired `HoldController`), fiat on-ramp
checkout (`FiatCheckoutController`), and the two Admin/Operator-scoped read
surfaces (`PlatformTokenController`, `ProjectTreasuryController`). See the
new rows below. `AllocationRequest.kind` gains `"IssueUnits"` plus
`partition`/`purpose`/`expectedFeeScheduleVersion`/`previewOnly` (D47 — the
tenant-facing default allocation path; `Mint` stays retired-for-tenants).

**Breaking, pre-launch (no external consumer depended on it):** the SDK's
`./orchestration` subpath export — a standalone Stripe checkout-and-mint
orchestrator that duplicated `FiatCheckoutController` + `AllocationController`
client-side and held its own Stripe secret key — is removed. `stripe` is no
longer a dependency of this package. A consumer that wants its own
orchestration bridge builds it externally against `azoa.api.startFiatCheckout`
+ `azoa.api.allocate`; the backend's own fiat rail (`FiatCheckoutController` →
`FiatRailProvider` → a registered `IFiatSettlementHandler`) is the
supported path for this node's own on-ramp.

**Hygiene fixes (no behavior change under existing tests):**
- `TinymanAdapter` no longer defaults an unknown ASA's decimals to `6`; it
  resolves them from a configured indexer (`TinymanConfig.indexer`) and fails
  closed (an `SdkError`, never a guess) when neither the caller nor an
  indexer can supply them. Native ALGO (asset id 0) still resolves to 6
  directly — a protocol constant, not a default.
- `AlgorandProvider.buildTransfer`'s ALGO→microAlgo and ASA base-unit
  conversion no longer goes through `Number(...) * 10**decimals` floating
  math; both parse the decimal amount string exactly via `BigInt` and only
  convert to a `number` when the exact result is a safe integer, throwing an
  `INVALID_INPUT` `SdkError` otherwise. Output is unchanged for every amount
  the existing behavior computed correctly (e.g. `"1.5"` ALGO → `1500000`).
- `ChainProvider.supportsBridging` is now documented as a static
  participation declaration, not a live availability check — see
  `AzoaClient.checkBridgeCapability(chainId)`, a new method that asks the
  backend's `GET /api/bridge/routes` registry whether a chain is currently an
  enabled source or target on any route.

## SDK 0.4.0 — final value-rail parity (W10.3)

Adds typed coverage for the remaining consumer-facing value-rail surfaces and
the public transparency reads that had shipped backend-first with no SDK
method: sponsorship `Exchange` (`ExchangeController`), the fiat off-ramp
cash-out flow (`FiatPayoutController` — availability, payout-account status
and setup, start, history), and the anonymous public reads on
`NodeTransparencyController` (current snapshot, the three audit trails, and
the signed history checkpoint). See the new rows below.

`FiatReversalView` and its `FiatReversalKind` / `FiatReversalStatus` /
`FiatReversalRecoveryPath` types are also added, **types only** — mirrors of
`FiatReversalOperatorController`'s wire shape so an operator console can
import a typed shape instead of hand-rolling one locally (the admin
`fiat-reversals` frontend page previously declared its own copy). No SDK
*method* wraps that controller: it is gated by the hardened JWT `Operator`
policy with no API-key path at all, unlike `PlatformTokenController` /
`ProjectTreasuryController` in 0.3.1, which stayed Operator-scoped but kept a
bearer-JWT path an SDK caller can actually reach. The same reasoning holds
`FiatPayoutOperatorController`'s operator board (advance/return/park/restart
a cash-out) out of the SDK; only the tenant-facing `FiatPayoutController` gets
methods here.

**ArdaNova .NET clients — not re-opened.** W4.7 already decided this (see
"Decision: ArdaNova's .NET clients are NOT generated from this SDK" below);
that decision was not deferred and W10.3 does not re-litigate it. The answer
stands: hand-maintain ArdaNova's HTTP clients against this document, or
generate them from AZOA's own OpenAPI document in a future track — never from
this TypeScript SDK's source.

**Publish owed.** This SDK bump (0.3.1 → 0.4.0) was made source-only; running
`tsup` and `npm publish` needs a live build/publish step this track does not
perform (concurrent agents were editing the same tree). Build and publish
`azoa-sdk@0.4.0` to npm before any consumer takes a dependency on the methods
below.

## Method Mapping Table

| Controller | Endpoint | HTTP | SDK Method | SDK File | Status |
|---|---|---|---|---|---|
| **User** | /api/user/register | POST | `register` | client.ts | OK |
| | /api/user/login | POST | `login` | client.ts | OK |
| | /api/user/{id} | GET | `getUser` | client.ts | OK |
| | /api/user | GET | `getAllUsers` | client.ts | OK |
| | /api/user/{id} | PUT | `updateUser` | client.ts | OK |
| | /api/user/{id} | DELETE | `deleteUser` | client.ts | OK |
| | /api/user/logout | POST | `logoutEverywhere` | client.ts | OK |
| | /api/user/{id}/dapp-role | PUT | `assignDappRole` | client.ts | OK |
| **Wallet** | /api/wallet/{id} | GET | `getWallet` | client.ts | OK |
| | /api/wallet | GET | `listWallets` | client.ts | OK |
| | /api/wallet | POST | `createWallet` | client.ts | OK |
| | /api/wallet/{id} | PUT | `updateWallet` | client.ts | OK |
| | /api/wallet/{id} | DELETE | `deleteWallet` | client.ts | OK |
| | /api/wallet/{id}/set-default | POST | `setDefaultWallet` | client.ts | OK |
| | /api/wallet/{id}/portfolio | GET | `getWalletPortfolio` | client.ts | OK |
| | /api/wallet/generate | POST | — | — | **SDK gap** (`generateWallet` no longer in the SDK) |
| | /api/wallet/connect | POST | — | — | **SDK gap** (`connectWallet` no longer in the SDK) |
| | /api/wallet/{id}/export | POST | — | — | **SDK gap** (`exportWallet` no longer in the SDK) |
| | /api/wallet/{id}/topup | POST | — | — | **SDK gap** (`topupWallet` no longer in the SDK) |
| | /api/wallet/types | GET | — | — | **SDK gap** (`getWalletsByType` no longer in the SDK) |
| | (no endpoint) | — | `rotateWalletKeys` | client.ts | see KeyRotation — `POST /api/admin/key-rotation/rotate` |
| **Holon** | /api/holon/{id} | GET | `holons.get` | holon-query.ts | OK |
| | /api/holon | GET | `holons.execute` (fluent builder) | holon-query.ts | OK |
| | /api/holon | POST | `holons.create` | holon-query.ts | OK |
| | /api/holon/{id} | PUT | `holons.update` | holon-query.ts | OK |
| | /api/holon/{id} | DELETE | `holons.delete` | holon-query.ts | OK |
| | /api/holon/{id}/compose | GET | `holons.getComposite` | holon-query.ts | OK |
| | /api/holon/{id}/children | GET | `holons.getChildren` | holon-query.ts | OK |
| | /api/holon/{id}/peers | GET | `holons.getPeers` | holon-query.ts | OK |
| | /api/holon/{id}/ancestors | GET | `holons.getAncestors` | holon-query.ts | OK |
| | /api/holon/{id}/descendants | GET | `holons.getDescendants` | holon-query.ts | OK |
| | /api/holon/{id}/clone | POST | `holons.clone` | holon-query.ts | OK |
| | /api/holon/{id}/interact | POST | — | — | **SDK gap** |
| | /api/holon/{id}/mint | POST | — | — | **SDK gap** |
| | /api/holon/{id}/exchange | POST | — | — | **SDK gap** |
| | /api/holon/{id}/propagate | POST | — | — | **SDK gap** |
| | /api/holon/{id}/move | POST | — | — | **SDK gap** |
| **NFT** | /api/nft/{id} | GET | `getNft` | client.ts | OK |
| | /api/nft | GET | `listNfts` | client.ts | OK |
| | /api/nft/mint | POST | `mintNft` | client.ts | OK (safe operation projection) |
| | /api/nft/{id}/transfer | POST | `transferNft` | client.ts | OK (safe operation projection) |
| | /api/nft/{id}/burn | POST | `burnNft` | client.ts | OK (safe operation projection) |
| | /api/nft/fungible-mint | POST | `fungibleMint` | client.ts | OK (safe response) |
| | /api/nft/{id}/metadata | GET | `getNftMetadata` | client.ts | OK |
| **UserNFT** | /api/usernft/mint | POST | `mintUserNFT` | client.ts | Lifecycle result; required Idempotency-Key |
| | /api/usernft/{id} | GET | `getUserNFT` | client.ts | OK |
| | /api/usernft/{id}/transfer | POST | `transferUserNFT` | client.ts | OK |
| | /api/usernft/{id} | DELETE | `burnUserNFT` | client.ts | Lifecycle result; required Idempotency-Key |
| | /api/usernft/lifecycle/request | GET | `getNftLifecycleByRequest` | client.ts | Actor/tenant-scoped key correlation; absence remains uncertain |
| | /api/usernft/lifecycle/request/cancel | POST | `cancelNftRequest` | client.ts | Atomic cancellation tombstone fences delayed admission |
| | /api/usernft/lifecycle | GET | `listNftLifecycles` | client.ts | Participant-scoped cursor page; continue even when page items are empty |
| | /api/usernft/fee-quote | GET | `getNftFeeQuote` | client.ts | Native fee base units and immutable revision; excludes chain fees |
| | /api/usernft/lifecycle/{operationId}/refund | POST | `refundNftTransfer` | client.ts | Original transfer reference; holder must authorize return |
| | /api/usernft/lifecycle/{operationId} | GET | `getNftLifecycle` | client.ts | Participant-authorized durable status |
| | /api/usernft/lifecycle/{operationId}/refresh | POST | `refreshNftLifecycle` | client.ts | Reconcile chain evidence |
| | /api/usernft/lifecycle/{operationId}/accept | POST | `acceptNftLifecycle` | client.ts | Explicit recipient/creator authorization |
| | /api/usernft/lifecycle/{operationId}/cancel | POST | `cancelNftLifecycle` | client.ts | Actor-authorized unsent cancellation |
| **Bridge** | /api/bridge/routes | GET | `getBridgeRoutes` | client.ts | OK |
| | /api/bridge/initiate | POST | `initiateBridge` | client.ts | OK (safe response) |
| | /api/bridge/{id} | GET | `getBridgeStatus` | client.ts | OK (safe response) |
| | /api/bridge/{id}/fetch-vaa | POST | `fetchVAA` | client.ts | OK (safe response) |
| | /api/bridge/{id}/redeem | POST | `redeemBridge` | client.ts | OK (safe response) |
| | /api/bridge/{id}/reverse | POST | `reverseBridge` | client.ts | OK (safe response) |
| | /api/bridge/history | GET | `getBridgeHistory` | client.ts | OK (safe response) |
| **Exchange** | /api/exchange/{userId} | POST | `exchange` | client.ts | OK — requires an API-key principal carrying the exchange-signing scope, exactly like `allocate`; idempotencyKey is REQUIRED (not optional, unlike allocate) |
| | /api/exchange/receipt/{idempotencyKey} | GET | `getExchangeReceipt` | client.ts | OK |
| **Hold commands** | /api/hold-commands | POST | `startHoldCommand` | client.ts | Keyed admission; quote before any custody |
| | /api/hold-commands | GET | `listHoldCommands` | client.ts | Participant discovery after a lost response |
| | /api/hold-commands/{id} | GET | `getHoldCommand` | client.ts | Participant-safe progress |
| | /api/hold-commands/{id}/refresh | POST | `refreshHoldCommand` | client.ts | Observes/rebroadcasts authorized wire only |
| | /api/hold-commands/{id}/continue | POST | `continueHoldCommand` | client.ts | Explicit quote consent; renews current authority |
| | /api/hold-commands/{id}/accept | POST | `acceptHoldRecipient` | client.ts | Actual recipient acceptance |
| | /api/hold-commands/{id}/replace | POST | `replaceHoldCommand` | client.ts | Proven no-effect generation replacement |
| | /api/hold-commands/{id}/cancel | POST | `cancelHoldCommand` | client.ts | Unsent/no-effect cancellation |
| | /api/hold-commands/{id}/reconcile-request | POST | `reconcileHoldCommand` | client.ts | Saved-request resolution |
| | /api/hold-commands/{id}/accept-opening | POST | `acceptHoldOpening` | client.ts | Workflow opening disclosure acceptance |
| | /api/hold-commands/{id}/reconcile-opening | POST | `reconcileHoldOpening` | client.ts | |
| | /api/hold-commands/{id}/replace-opening | POST | `replaceHoldOpening` | client.ts | |
| | /api/hold-commands/{id}/withdraw-opening | POST | `withdrawHoldOpening` | client.ts | |
| | /api/hold-commands/{id}/reconcile-opening-recovery | POST | `reconcileHoldOpeningRecovery` | client.ts | |
| **Hold release mandates** | /api/hold-release-mandates/offers | POST | `quoteHoldReleaseMandate` | client.ts | Server-authored disclosure |
| | /api/hold-release-mandates/accept | POST | `acceptHoldReleaseMandate` | client.ts | Explicit acceptance |
| | /api/hold-release-mandates | GET | `listHoldReleaseMandates` | client.ts | |
| | /api/hold-release-mandates/{id} | GET | `getHoldReleaseMandate` | client.ts | |
| | /api/hold-release-mandates/{id}/reauthorize | POST | `reauthorizeHoldReleaseMandate` | client.ts | |
| | /api/hold-release-mandates/{id}/revoke | POST | `revokeHoldReleaseMandate` | client.ts | |
| | /api/hold-release-mandates/{id}/reconcile-request | POST | `reconcileHoldReleaseMandate` | client.ts | |
| **Fiat checkout** | /api/fiat/checkout/capabilities | GET | `getFiatCheckoutCapabilities` | client.ts | OK |
| | /api/fiat/checkout/{userId} | POST | `startFiatCheckout` | client.ts | OK |
| | /api/fiat/checkout/{userId}/{intentId} | GET | `getFiatCheckoutIntent` | client.ts | OK |
| | /api/fiat/checkout/{userId}/requests/{requestKey}/void | POST | — | — | **SDK gap** (request-key void; Providers/Fiat/AGENTS.md §request-void) |
| **Fiat payout (off-ramp / cash-out)** | /api/fiat/payouts/{userId}/availability | GET | `getCashOutAvailability` | client.ts | OK |
| | /api/fiat/payouts/{userId}/account | GET | `getPayoutAccountStatus` | client.ts | OK |
| | /api/fiat/payouts/{userId}/account | POST | `beginPayoutAccountSetup` | client.ts | OK |
| | /api/fiat/payouts/{userId} | POST | `startCashOut` | client.ts | OK |
| | /api/fiat/payouts/{userId} | GET | `listCashOutHistory` | client.ts | OK |
| **FiatPayoutOperator** (Admin/Operator, JWT-only) | /api/admin/fiat-payouts | GET | — | — | Deliberate SDK gap — operator board with no API-key path; not part of this SDK's scope |
| | /api/admin/fiat-payouts/{id} | GET | — | — | Deliberate SDK gap — same as above |
| | /api/admin/fiat-payouts/accounts/{userId} | GET | — | — | Deliberate SDK gap — same as above |
| | /api/admin/fiat-payouts/{id}/advance \| /return \| /park \| /restart | POST | — | — | Deliberate SDK gap — same as above |
| **FiatReversalOperator** (Admin/Operator, JWT-only) | /api/admin/fiat-reversals | GET | — | — | Deliberate SDK gap for the METHOD (operator board, no API-key path); `FiatReversalView` TYPE is exported (0.4.0) for a console to type its own `request()` calls |
| | /api/admin/fiat-reversals/{id} | GET | — | — | Deliberate SDK gap — same as above |
| | /api/admin/fiat-reversals/accounts/{userId} | GET | — | — | Deliberate SDK gap — same as above |
| | /api/admin/fiat-reversals/{id}/recover \| /settle \| /restart \| /clear-flag | POST | — | — | Deliberate SDK gap — same as above |
| **NodeTransparency** (public, anonymous) | /api/node-transparency/current | GET | `getNodeTransparencySnapshot` | client.ts | OK |
| | /api/node-transparency/audit/governance | GET | `listNodeGovernanceAudit` | client.ts | OK |
| | /api/node-transparency/audit/fees | GET | `listNodeFeeAudit` | client.ts | OK |
| | /api/node-transparency/audit/treasury | GET | `listNodeTreasuryAudit` | client.ts | OK |
| | /api/node-transparency/audit/checkpoint | GET | `getNodeTransparencyHistoryCheckpoint` | client.ts | OK |
| **PlatformToken** (Admin/Operator) | /api/admin/platform-token | GET | `getPlatformTokenState` | client.ts | OK |
| | /api/admin/platform-token/genesis | POST | — | — | Deliberate SDK gap — the one-time mint additionally requires a fresh `RecentNodeOperatorSession` (non-API-key node-operator login) this SDK's bearer/API-key config cannot express; run genesis through the operator console, not this SDK. |
| **ProjectTreasury** (Admin/Operator) | /api/admin/project-treasuries | GET | `getProjectTreasury` | client.ts | OK |
| | /api/admin/project-treasuries/tenant/{tenantId} | GET | `listProjectTreasuriesByTenant` | client.ts | OK |
| | /api/admin/project-treasuries/resolve | POST | — | — | **SDK gap** — operator-only account provisioning; not part of this track's read-surface scope. |
| | /api/admin/project-treasuries/close | POST | — | — | **SDK gap** — operator-only account retirement; not part of this track's read-surface scope. |
| **Tenant custodial account** | /api/tenant/custodial-accounts/capabilities | GET | `getTenantCustodialCapabilities` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject} | PUT | `ensureTenantCustodialAccount` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject} | GET | `getTenantCustodialAccount` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject}/kyc/session | POST | `beginTenantKyc` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject}/kyc/submissions | POST | `submitTenantKyc` | client.ts | OK |
| **BlockchainOperation** | /api/blockchainoperation/{id} | GET | `getBlockchainOperation` | client.ts | OK (safe projection) |
| | /api/blockchainoperation/user/{userId} | GET | `getBlockchainOperationsByUser` | client.ts | OK (safe projection) |
| **Allocation receipt** | /api/allocation/receipt | GET | — | — | SDK gap (API-key caller surface; .NET SDK track) |
| | /api/allocation/receipt/reconcile | POST | — | — | SDK gap (API-key caller surface; .NET SDK track) |
| **ActionGraphBundle** | /api/actiongraphbundle/{id} | GET | `getActionGraphBundle` | client.ts | OK |
| | /api/actiongraphbundle | GET | `listActionGraphBundle` | client.ts | OK |
| | /api/actiongraphbundle | POST | `createActionGraphBundle` | client.ts | OK |
| | /api/actiongraphbundle/{id} | PUT | `updateActionGraphBundle` | client.ts | OK |
| | /api/actiongraphbundle/{id} | DELETE | `deleteActionGraphBundle` | client.ts | OK |
| | /api/actiongraphbundle/{id}/generate | POST | `generateActionGraphBundleDapp` | client.ts | OK |
| | /api/actiongraphbundle/{id}/ecosystem | GET | — | — | **SDK gap** |
| | /api/actiongraphbundle/{id}/ecosystem/dapp-series | POST | — | — | **SDK gap** |
| | /api/actiongraphbundle/{id}/deploy | POST | `deployActionGraphBundle` | client.ts | OK |
| **Action** | /api/action | POST | `createAction` | client.ts | OK |
| | /api/action/{id} | GET | `getAction` | client.ts | OK |
| | /api/action/user/{userId} | GET | `listActionsByUser` | client.ts | OK |
| | /api/action/{id} | PUT | `updateAction` | client.ts | OK |
| | /api/action/{id} | DELETE | `deleteAction` | client.ts | OK |
| | /api/action/{id}/validate | POST | `validateActionDag` | client.ts | OK |
| | /api/action/{id}/publish | POST | `publishAction` | client.ts | OK |
| | /api/action/{id}/unpublish | POST | `unpublishAction` | client.ts | OK |
| | /api/action/{id}/execute | POST | `executeAction` | client.ts | OK |
| | /api/action/{id}/nodes/{nodeId}/execute | POST | `executeActionNode` | client.ts | OK |
| | /api/action/templates | POST | `createActionTemplate` | client.ts | OK |
| | /api/action/templates/{id} | GET | `getActionTemplate` | client.ts | OK |
| | /api/action/templates | GET | `listActionTemplates` | client.ts | OK |
| | /api/action/templates/{id}/instantiate | POST | `instantiateActionTemplate` | client.ts | OK |
| | /api/action/node-templates | POST | `createActionNodeTemplate` | client.ts | OK |
| | /api/action/node-templates | GET | `listActionNodeTemplates` | client.ts | OK |
| | /api/action/{actionId}/nodes | GET | — | — | **SDK gap** |
| | /api/action/{actionId}/nodes | POST | — | — | **SDK gap** |
| | /api/action/{actionId}/nodes/{nodeId} | PUT | — | — | **SDK gap** |
| | /api/action/{actionId}/nodes/{nodeId} | DELETE | — | — | **SDK gap** |
| | /api/action/{actionId}/edges | POST | — | — | **SDK gap** |
| | /api/action/{actionId}/edges/{edgeId} | DELETE | — | — | **SDK gap** |
| | /api/action/{actionId}/topological-order | GET | — | — | **SDK gap** |
| | /api/action/{actionId}/dependencies | POST | — | — | **SDK gap** |
| | /api/action/{actionId}/dependencies/{depId} | DELETE | — | — | **SDK gap** |
| | /api/action/{actionId}/dependency-status | GET | — | — | **SDK gap** |
| | /api/action/runs/{runId} | GET | — | — | **SDK gap** |
| | /api/action/{actionId}/runs | GET | — | — | **SDK gap** |
| | /api/action/runs/{runId}/execution-state | GET | — | — | **SDK gap** |
| | /api/action/runs/{runId}/complete | POST | — | — | **SDK gap** |
| | /api/action/runs/{runId}/fork | POST | — | — | **SDK gap** |
| | /api/action/runs/{runId}/mark-failed | POST | — | — | **SDK gap** |
| | /api/action/public | GET | `listPublicActions` | client.ts | OK |
| | /api/action/{id}/preview | GET | — | — | **SDK gap** |
| | /api/action/{id}/start-workflow | POST | — | — | **SDK gap** |
| | /api/action/runs/{runId}/advance | POST | — | — | **SDK gap** |
| | /api/action/runs/{runId}/signal | POST | — | — | **SDK gap** |
| | /api/action/runs/{runId}/reconcile | POST | — | — | **SDK gap** |
| | /api/action/runs/reconcile-sweep | POST | — | — | **SDK gap** |
| | /api/action/{id}/run-access | PUT | `setActionRunAccess` | client.ts | OK |
| | /api/action/{id}/invite | POST | `inviteUser` | client.ts | OK |
| | /api/action/{id}/invite/{userId} | DELETE | `revokeInvite` | client.ts | OK |
| | /api/action/{id}/access-requests | POST | `requestActionAccess` | client.ts | OK |
| | /api/action/{id}/access-requests | GET | `listAccessRequests` | client.ts | OK |
| | /api/action/access-requests/{requestId}/decision | POST | `decideAccessRequest` | client.ts | OK |
| | /api/action/access-requests/{requestId}/withdraw | POST | `withdrawAccessRequest` | client.ts | OK |
| | /api/action/access-requests/mine | GET | `listMyAccessRequests` | client.ts | OK |
| **Search** | /api/search | POST | `search` | client.ts | OK |
| | /api/search/facets | GET | `getSearchFacets` | client.ts | OK |
| **ApiKey** | /api/apikey | POST | `createApiKey` | client.ts | OK |
| | /api/apikey | GET | `listApiKeys` | client.ts | OK |
| | /api/apikey/{id}/revoke | POST | `revokeApiKey` | client.ts | OK |
| | /api/apikey/{id} | DELETE | `deleteApiKey` | client.ts | OK |
| | /api/apikey/{id}/rotate | POST | `rotateApiKey` | client.ts | OK |
| | /api/apikey/scopes | GET | `listIssuableScopes`, `listSelfIssuableApiKeyScopes` | client.ts | OK |
| | /api/apikey/tenant | POST | — | — | **SDK gap** (tenant-scoped key issuance) |
| **DappComposition** | /api/dapp-series/{id}/compose | POST | `composeDappSeries` | client.ts | Typed contract; deployment remains pending until confirmed |
| | /api/dapp-series/{id}/validate | GET | `validateDappSeries` | client.ts | Typed contract; deployment remains pending until confirmed |
| | /api/dapp-series/{id}/manifest | GET | `getDappSeriesManifest` | client.ts | Typed contract; deployment remains pending until confirmed |
| | /api/dapp-series/{id}/generate | POST | `generateDappSeries` | client.ts | Typed contract; deployment remains pending until confirmed |
| | /api/dapp-series/{id}/deploy | POST | `deployDappSeries` | client.ts | Typed contract; deployment remains pending until confirmed |
| | /api/dapp-series/{id}/status | GET | `getDappSeriesDeploymentStatus` | client.ts | Typed contract; deployment remains pending until confirmed |
| **DappSeries** | /api/dapp-series/{id} | GET | `getDappSeries` | client.ts | OK |
| | /api/dapp-series | GET | `listDappSeries` | client.ts | OK |
| | /api/dapp-series | POST | `createDappSeries` | client.ts | OK |
| | /api/dapp-series/{id} | PUT | `updateDappSeries` | client.ts | OK |
| | /api/dapp-series/{id} | DELETE | `deleteDappSeries` | client.ts | OK |
| | /api/dapp-series/{seriesId}/actions | GET | `listSeriesActions` | client.ts | OK |
| | /api/dapp-series/{seriesId}/actions | POST | `addSeriesAction` | client.ts | OK |
| | /api/dapp-series/{seriesId}/actions/{actionId} | DELETE | `removeSeriesAction` | client.ts | OK |
| | /api/dapp-series/{seriesId}/actions/{actionId}/order | PUT | `reorderSeriesAction` | client.ts | OK |
| | /api/dapp-series/{seriesId}/actions/{actionId}/mappings | PUT | `updateSeriesMappings` | client.ts | OK |
| **DEX** | /api/dex/capabilities | GET | `getDexCapabilities` | client.ts | OK |
| | /api/dex/quotes | POST | `quoteDex` | client.ts | OK |
| | /api/dex/operations (+ lifecycle subroutes) | POST/GET | `startDexSwap` … `prepareDexSwapCleanup` | client.ts | OK |
| | /api/operator/dex/operations/{id}/settle-review | POST | — | — | Operator-only; no SDK method by design |
| **WTT** | /api/bridge/wtt/quotes | POST | `quoteWtt` | client.ts | OK (source-authored; runtime pending) |
| | /api/bridge/wtt/operations (+ by-request-key, cancel, continue, submit, participant, participant-signature, refresh, native-output, cleanup) | POST/GET | `startWtt` … `startWttCleanup` | client.ts | OK (source-authored; runtime pending) |
| | /api/operator/wtt/operations/{id} (+ reconcile, resume-proof) | GET/POST | — | — | Operator-only; no SDK method by design |
| **Network** | /api/network | GET | — | — | **SDK gap** (`getNetworkInfo` no longer in the SDK) |
| **Credential** | /api/credential/issue/{recipientUserId} | POST | — | — | **SDK gap** — backend two-party mint route (D41) has no typed SDK method yet |
| | /api/credential/{assetId}/revoke | POST | `revokeCredential` | client.ts | OK — see caveat below |
| **Analytics** | /api/analytics/transactions | GET | `getAnalyticsTransactions` | client.ts | OK |
| | /api/analytics/positions | GET | `getAnalyticsPositions` | client.ts | OK |
| **Allocation** | /api/allocation/{userId} | POST | `allocate` | client.ts | OK — `AllocationRequest.kind` covers `Mint` (retired-for-tenants) \| `Transfer` \| `IssueUnits` (D47 default), plus `partition`/`purpose`/`expectedFeeScheduleVersion`/`previewOnly` |
| **HolonTypeRegistry** | /api/holon-types | GET/POST | `listHolonTypes`, `registerHolonType` | client.ts | OK |
| | /api/holon-types/{assetType} | GET/DELETE | `getHolonType`, `deleteHolonType` | client.ts | OK |
| | /api/holon-types/{assetType}/deactivate | POST | `deactivateHolonType` | client.ts | OK |
| **SagaOperator** | /api/admin/sagas/dead-letters | GET | `listSagaDeadLetters` | client.ts | OK |
| | /api/admin/sagas/{id}/requeue | POST | `requeueSagaStep` | client.ts | OK |
| | /api/admin/sagas/{id}/cancel | POST | `cancelSagaStep` | client.ts | OK |
| **KeyRotation** | /api/admin/key-rotation/rotate | POST | `rotateWalletKeys` | client.ts | OK |

## Known Drift (Snapshot 2026-06-11)

### Resolved 2026-06-11 (shipped in `self-audit-one-fix`)

~~1. **`GET /api/nft` (list all NFTs)**~~ — `listNfts()` added to SDK. Resolved.

~~2. **`PUT /api/actiongraphbundle/{id}` (update the bundle)**~~ — `updateActionGraphBundle()` added to SDK. Resolved.

~~3. **`GET /api/swap/quote`**~~ — `getSwapQuote()` added to SDK; backend endpoint is `GET /api/swap/quote`. Resolved.

~~4. **`POST /api/swap/build`**~~ — Replaced by `POST /api/swap/execute` with `executeSwap()` in SDK. Resolved.

### Open Drift

5. **`GET /api/blockchainoperation` (list all)** — Only `getByUser` and `getById` exist; no list-all method.

6. **`DELETE /api/action/templates/{id}`** — Create and list exist; delete is missing from both controller and SDK.

7. **`DELETE /api/action/node-templates/{id}`** — Create and list exist; delete is missing from both controller and SDK.

8. **Action sub-resource endpoints (nodes, edges, dependencies, runs) — 16 controller routes** — Shipped in `action-api` phase-F. No SDK methods exist yet. See the SDK gap rows in the Action section above.

9. **Wallet lifecycle methods regressed out of the SDK** — `generateWallet`, `connectWallet`, `exportWallet`, `topupWallet` and `getWalletsByType` are documented history but no longer exist in `sdk/azoa-wallet/src`. The five controller endpoints are still live, so this is an SDK gap, not a removed surface. Decide whether to restore the methods or retire the routes.

10. **`getNetworkInfo` regressed out of the SDK** — `GET /api/network` is live with no typed method.

11. **Durable-run control surface has no SDK** — `start-workflow`, `advance`, `signal`, `reconcile` and `reconcile-sweep` on `ActionRun` are reachable only through `azoa.api.request(...)`.

12. **Whole controllers with no mapping rows** — `Backfill` (`/api/admin/backfill`), `Consent` (`/api/user/consent`), `ExternalAuth` (`/auth/oauth`), `Kyc` (`/api/kyc`), `KycCallback` (`/api/kyc/callback`), `NodeGovernance` (`/api/node-governance`), `NodeOperator` (`/api/operator`), `NodeOperatorSession` (`/api/operator/session`), `TenantConsent` (`/api/tenant/consent`), `TenantKycConfiguration` (`/api/tenant/kyc`), `WalletAuth` (`/api/user`, wallet-challenge login) and the non-custodial half of `Tenant` (`/api/tenant`). Several are deliberately operator- or webhook-only surfaces that no browser SDK should call; the rest are genuine gaps. Classify each before the next SDK minor. (`NodeTransparency` was resolved in 0.4.0 — see the Method Mapping Table; `FiatPayoutOperator` and `FiatReversalOperator` are now explicitly rows above, deliberately without methods, rather than missing controllers.)

## How to Keep This Current

**When adding/changing a controller endpoint:**
1. Implement the backend endpoint
2. Add or update the corresponding SDK method in `sdk/azoa-wallet/src/api/client.ts`
3. Update the row in the mapping table above (add row if new endpoint, update Status column if changing)
4. Mark the status: `OK` (both exist), `SDK gap` (endpoint exists, no SDK method), `Backend gap` (SDK method exists, no endpoint), or `Path mismatch` (different routes)

**When adding an SDK method:**
1. Ensure it maps to an existing controller endpoint
2. Add a row to the table if it's a new endpoint
3. Update the Status column

**CI does not validate this document.** It is a manual gate at PR-review. The author of a PR touching controllers or the SDK is responsible for syncing this table before merge.

**Last reconciled:** 2026-09-07 — re-derived from `Controllers/*.cs` route attributes and the
`sdk/azoa-wallet/src` method surface after the Avatar→User / Quest→Action rename and the Phase 5
landing. Route-level corrections made in this pass: the dapp surface is `/api/dapp-series`, never
`/api/dappseries` or `/api/dappcomposition`; holon CRUD lives on `HolonQueryBuilder`
(`holons.get` / `.execute` / `.create` / `.update` / `.delete`), not on `client.ts`; holon types key
on `{assetType}`, not `{id}`; key rotation is `POST /api/admin/key-rotation/rotate`.

**Updated 2026-09-08 (W4.7, `azoa-sdk` 0.3.1):** added the Hold, Fiat checkout, PlatformToken and
ProjectTreasury rows above (all previously undocumented SDK gaps); the SDK's `./orchestration`
subpath export and its `stripe` dependency are removed — see the "SDK 0.3.1" section above.

**Updated 2026-09-08 (W10.3, `azoa-sdk` 0.4.0):** added the Exchange, Fiat payout (off-ramp),
NodeTransparency rows and the FiatPayoutOperator/FiatReversalOperator "deliberate gap" rows above
— see the "SDK 0.4.0" section above. `FiatReversalView` ships as a type only, no SDK method.

⚠ **Credential caveat — corrected 2026-09-07.** The vacuous issuer-equals-holder mint path this
caveat used to describe is **fixed**: `POST /api/credential/issue/{recipientUserId}` (added by
`credential-third-party-issuance_20260906`, D41) mints two-party, with the issuer of record
stamped from the authenticated caller and `current_owner` written at mint, so `POST
/api/credential/{assetId}/revoke` and `revokeCredential` are now backed by a real, reachable
authorization check. **The route is still present-but-inert for a different reason:**
`ActionNodeType.CredentialRevoke` remains in `Services/Actions/NotEnabledActionNodeTypes.cs`
until the track's non-negotiable `/security-review` closes (a value + key-authority path). Do not
document either route as a working end-to-end capability until that gate lifts. The new `issue`
route also has no typed SDK method yet (see the SDK-gap row above) — add one before any
frontend/consumer surface tries to use it.

**Note:** The actions page (`frontend/src/app/(dashboard)/actions/page.tsx`) uses `azoa.api.request(...)` directly rather than the typed SDK action methods. The publish/unpublish calls on the page are therefore direct HTTP calls, not `publishAction`/`unpublishAction`. The typed SDK methods exist for external SDK consumers. This gap is tracked as part of the existing action sub-resource SDK gap (row 8 in "Open Drift" above).

## Decision: ArdaNova's .NET clients are NOT generated from this SDK

Asked by W4.7 (`ardanova-production-readiness-waves_20260907`), reaffirmed by
W10.3 (same track, later wave) — this decision was made, not deferred, so
W10.3 did not re-open it. **No.** This
SDK (`azoa-sdk`) is a TypeScript package built for browser/Node/React Native
consumers with client-side signing; ArdaNova is a separate .NET repository
(`c:/Users/atooz/Documents/Escherbridge/ardanova`) consuming Azoa over plain
HTTP. There is no TypeScript→.NET code-generation step in this toolchain, and
building one only for a single internal partner would be new infrastructure
this track's scope does not cover.

ArdaNova's typed HTTP clients should instead be either hand-maintained against
this document (the pattern already in effect — see
`conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`) or, if drift between the
two repos keeps recurring, generated from an OpenAPI/Swagger document emitted
by the AZOA WebAPI itself (ASP.NET already produces one at the controller
level) via a standard `.NET` OpenAPI client generator — never from this TS
SDK's source. That is future work, not part of this track; the near-term
mitigation is keeping this table current at PR-review time as the existing
"How to Keep This Current" section already asks.

## Next integration track

The next named track is **Arda Nova financial workflow conformance**. It should
consume the public Azoa API/SDK contract as a partner dependency, add end-to-end
coverage for Arda Nova onboarding and financial workflows, and keep a clean
versioned seam so future agents and external partners do not depend on Azoa
internals or a shared repository checkout.

## Hosted identity recovery and renewal (Round 1)

KYC begin now persists a durable attempt before contacting a hosted provider.
Retries reuse the attempt; rejected/expired attempts and real approvals missing
required verification evidence can renew. Missing-evidence real approvals project
EXPIRED through the existing KYC status contract, and tenant custodial projection
uses its existing rejected/reverification state. SDK begin/status signatures are
unchanged; clients should start a new verification after that status, and resume
pending attempts using begin. No AML details or lineage hashes are public.

Operator hosted reconciliation returns Recovered, ResolvedUnsent, or Quarantined.
Confirmed absence releases only provably unsent creation. Uncertain sent requests
retain admission; late original responses or verified canonical attachment resolve
them. Unverified callback aliases are rejected. Fresh creation remains pending;
aged unresolved/quarantined admissions require operator reconciliation.

### Durable deployment API (source authored; integrated verification pending)

| Route | Method | SDK method | Contract |
| --- | --- | --- | --- |
| `/api/deployments/bundles/{id}/generate` | POST | `generateDeployment` | Explicit runtime/native target and immutable artifact |
| `/api/deployments/artifacts/{id}` | GET | `getDeploymentArtifact` | Owned normalized artifact |
| `/api/deployments/quote` | POST | `quoteDeployment` | Native intent with exact fee/rent/value quote |
| `/api/deployments/contract-query` | POST | `queryNativeContract` | Read-only EVM simulation; no signing |
| `/api/deployments` | POST | `startDeployment` | Idempotency key and typed request; pending receipt |
| `/api/deployments` | GET | `listDeployments` | Actor/tenant-scoped cursor discovery |
| `/api/deployments/{id}` | GET | `getDeployment` | Read-only current receipt |
| `/api/deployments/request` | GET | `getDeploymentByRequest` | Recover a lost first response by exact key |
| `/api/deployments/request/cancel` | POST | `cancelDeploymentRequest` | Exact-body cancellation/tombstone |
| `/api/deployments/{id}/continue` | POST | `continueDeployment` | Current authority for the next stage |
| `/api/deployments/{id}/cancel` | POST | `cancelDeployment` | Only authoritative cancellable state |
| `/api/deployments/{id}/invoke` | POST | `invokeDeployment` | Actual caller, immutable input and invocation key; returns durable ActionRun |
| `/api/deployments/{id}/artifact.mjs` | GET | Console authenticated download | Hash-pinned runnable loader |

Solana partial-stage cleanup and recovery after fee-budget exhaustion, plus AVM
large-ID wire precision and verified remote compiler identity, remain REQUIRED
Round3 implementation packages. These are not external deferrals or accepted
production-complete paths.
