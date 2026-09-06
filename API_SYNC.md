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
| **Wallet** | /api/wallet/{id} | GET | `getWallet` | client.ts | OK |
| | /api/wallet | GET | `listWallets` | client.ts | OK |
| | /api/wallet | POST | `createWallet` | client.ts | OK |
| | /api/wallet/{id} | PUT | `updateWallet` | client.ts | OK |
| | /api/wallet/{id} | DELETE | `deleteWallet` | client.ts | OK |
| | /api/wallet/{id}/set-default | POST | `setDefaultWallet` | client.ts | OK |
| | /api/wallet/{id}/portfolio | GET | `getWalletPortfolio` | client.ts | OK |
| | /api/wallet/generate | POST | `generateWallet` | client.ts | OK |
| | /api/wallet/connect | POST | `connectWallet` | client.ts | OK |
| | /api/wallet/{id}/export | POST | `exportWallet` | client.ts | OK |
| | /api/wallet/{id}/topup | POST | `topupWallet` | client.ts | OK |
| | /api/wallet/types | GET | `getWalletsByType` | client.ts | OK |
| **Holon** | /api/holon/{id} | GET | `getHolon` | holon-query.ts | OK |
| | /api/holon | GET | `queryHolons` | holon-query.ts | OK |
| | /api/holon | POST | `createHolon` | client.ts | OK |
| | /api/holon/{id} | PUT | `updateHolon` | client.ts | OK |
| | /api/holon/{id}/compose | GET | `getComposite` | holon-query.ts | OK |
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
| | /api/starodk/{id}/generate-dapp | POST | `generateSTARDapp` | client.ts | OK |
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
| **Search** | /api/search | POST | `search` | client.ts | OK |
| | /api/search/facets | GET | `getSearchFacets` | client.ts | OK |
| **ApiKey** | /api/apikey | POST | `createApiKey` | client.ts | OK |
| | /api/apikey | GET | `listApiKeys` | client.ts | OK |
| | /api/apikey/{id}/revoke | POST | `revokeApiKey` | client.ts | OK |
| | /api/apikey/{id} | DELETE | `deleteApiKey` | client.ts | OK |
| **DappComposition** | /api/dappcomposition/{id} | GET | `getDappComposition` | client.ts | OK |
| **DappSeries** | /api/dappseries/{id} | GET | `getDappSeries` | client.ts | OK |
| | /api/dappseries | GET | `queryDappSeries` | client.ts | OK |
| | /api/dappseries | POST | `createDappSeries` | client.ts | OK |
| | /api/dappseries/{id} | PUT | `updateDappSeries` | client.ts | OK |
| **Swap** | /api/swap/quote | GET | `getSwapQuote` | client.ts | OK |
| | /api/swap/execute | POST | `executeSwap` | client.ts | OK |
| **Network** | /api/network | GET | `getNetworkInfo` | client.ts | OK |

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

8. **Action sub-resource endpoints (nodes, edges, dependencies, runs) — 16 new controller routes** — Shipped in `action-api` phase-F. No SDK methods exist yet. See the SDK gap rows in the Action section above.

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

**Last reconciled:** 2026-07-18 (tenant custody/KYC SDK contract and bridge route corrections)

**Note:** The actions page (`frontend/src/app/(dashboard)/actions/page.tsx`) uses `azoa.api.request(...)` directly rather than the typed SDK action methods. The publish/unpublish calls on the page are therefore direct HTTP calls, not `publishAction`/`unpublishAction`. The typed SDK methods exist for external SDK consumers. This gap is tracked as part of the existing action sub-resource SDK gap (row 8 in "Open Drift" above).

## Next integration track

The next named track is **Arda Nova financial workflow conformance**. It should
consume the public Azoa API/SDK contract as a partner dependency, add end-to-end
coverage for Arda Nova onboarding and financial workflows, and keep a clean
versioned seam so future agents and external partners do not depend on Azoa
internals or a shared repository checkout.
