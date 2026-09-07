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
| **UserNFT** | /api/usernft | POST | `mintUserNFT` | client.ts | OK |
| | /api/usernft/{id} | GET | `getUserNFT` | client.ts | OK |
| | /api/usernft/{id}/transfer | POST | `transferUserNFT` | client.ts | OK |
| | /api/usernft/{id}/burn | POST | `burnUserNFT` | client.ts | OK |
| **Bridge** | /api/bridge/routes | GET | `getBridgeRoutes` | client.ts | OK |
| | /api/bridge/initiate | POST | `initiateBridge` | client.ts | OK (safe response) |
| | /api/bridge/{id} | GET | `getBridgeStatus` | client.ts | OK (safe response) |
| | /api/bridge/{id}/fetch-vaa | POST | `fetchVAA` | client.ts | OK (safe response) |
| | /api/bridge/{id}/redeem | POST | `redeemBridge` | client.ts | OK (safe response) |
| | /api/bridge/{id}/reverse | POST | `reverseBridge` | client.ts | OK (safe response) |
| | /api/bridge/history | GET | `getBridgeHistory` | client.ts | OK (safe response) |
| **Tenant custodial account** | /api/tenant/custodial-accounts/capabilities | GET | `getTenantCustodialCapabilities` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject} | PUT | `ensureTenantCustodialAccount` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject} | GET | `getTenantCustodialAccount` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject}/kyc/session | POST | `beginTenantKyc` | client.ts | OK |
| | /api/tenant/custodial-accounts/{externalSubject}/kyc/submissions | POST | `submitTenantKyc` | client.ts | OK |
| **BlockchainOperation** | /api/blockchainoperation/{id} | GET | `getBlockchainOperation` | client.ts | OK (safe projection) |
| | /api/blockchainoperation/user/{userId} | GET | `getBlockchainOperationsByUser` | client.ts | OK (safe projection) |
| **Allocation receipt** | /api/allocation/receipt | GET | — | — | SDK gap (API-key caller surface; .NET SDK track) |
| | /api/allocation/receipt/reconcile | POST | — | — | SDK gap (API-key caller surface; .NET SDK track) |
| **STARODK** | /api/starodk/{id} | GET | `getSTARODK` | client.ts | OK |
| | /api/starodk | GET | `listSTARODK` | client.ts | OK |
| | /api/starodk | POST | `createSTARODK` | client.ts | OK |
| | /api/starodk/{id} | PUT | `updateSTARODK` | client.ts | OK |
| | /api/starodk/{id} | DELETE | `deleteSTARODK` | client.ts | OK |
| | /api/starodk/{id}/generate | POST | `generateSTARDapp` | client.ts | OK |
| | /api/starodk/{id}/ecosystem | GET | — | — | **SDK gap** |
| | /api/starodk/{id}/ecosystem/dapp-series | POST | — | — | **SDK gap** |
| | /api/starodk/{id}/deploy | POST | `deploySTARODK` | client.ts | OK |
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
| **DappComposition** | /api/dapp-series/{id}/compose | POST | — | — | **SDK gap** |
| | /api/dapp-series/{id}/validate | GET | — | — | **SDK gap** |
| | /api/dapp-series/{id}/manifest | GET | — | — | **SDK gap** |
| | /api/dapp-series/{id}/generate | POST | — | — | **SDK gap** |
| | /api/dapp-series/{id}/deploy | POST | — | — | **SDK gap** |
| | /api/dapp-series/{id}/status | GET | — | — | **SDK gap** |
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
| **Swap** | /api/swap/quote | GET | `getSwapQuote` | client.ts | OK |
| | /api/swap/execute | POST | `executeSwap` | client.ts | OK |
| **Network** | /api/network | GET | — | — | **SDK gap** (`getNetworkInfo` no longer in the SDK) |
| **Credential** | /api/credential/{assetId}/revoke | POST | `revokeCredential` | client.ts | OK — see caveat below |
| **Analytics** | /api/analytics/transactions | GET | `getAnalyticsTransactions` | client.ts | OK |
| | /api/analytics/positions | GET | `getAnalyticsPositions` | client.ts | OK |
| **Allocation** | /api/allocation/{userId} | POST | `allocate` | client.ts | OK |
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

~~2. **`PUT /api/starodk/{id}` (update STARODK)**~~ — `updateSTARODK()` added to SDK. Resolved.

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

12. **Whole controllers with no mapping rows** — `Backfill` (`/api/admin/backfill`), `Consent` (`/api/user/consent`), `ExternalAuth` (`/auth/oauth`), `Kyc` (`/api/kyc`), `KycCallback` (`/api/kyc/callback`), `NodeGovernance` (`/api/node-governance`), `NodeOperator` (`/api/operator`), `NodeOperatorSession` (`/api/operator/session`), `NodeTransparency` (`/api/node-transparency`), `TenantConsent` (`/api/tenant/consent`), `TenantKycConfiguration` (`/api/tenant/kyc`), `WalletAuth` (`/api/user`, wallet-challenge login) and the non-custodial half of `Tenant` (`/api/tenant`). Several are deliberately operator- or webhook-only surfaces that no browser SDK should call; the rest are genuine gaps. Classify each before the next SDK minor.

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

⚠ **Credential caveat.** `POST /api/credential/{assetId}/revoke` and its `revokeCredential` SDK
method are wired end to end, but the rail behind them **cannot currently revoke anything**: the
only production mint path stamps the issuer as the holder, so D19's issuer-authorization check is
vacuous and every real revoke fails its holder-address precondition first. Treat the route as
present-but-inert until `conductor/tracks/credential-third-party-issuance_20260906` closes. Do not
document it as a working revocation capability.

**Note:** The actions page (`frontend/src/app/(dashboard)/actions/page.tsx`) uses `azoa.api.request(...)` directly rather than the typed SDK action methods. The publish/unpublish calls on the page are therefore direct HTTP calls, not `publishAction`/`unpublishAction`. The typed SDK methods exist for external SDK consumers. This gap is tracked as part of the existing action sub-resource SDK gap (row 8 in "Open Drift" above).

## Next integration track

The next named track is **Arda Nova financial workflow conformance**. It should
consume the public Azoa API/SDK contract as a partner dependency, add end-to-end
coverage for Arda Nova onboarding and financial workflows, and keep a clean
versioned seam so future agents and external partners do not depend on Azoa
internals or a shared repository checkout.
