# API_SYNC.md — Controller ↔ SDK Regression Mapping

## Purpose

This document tracks drift between backend endpoints and SDK method coverage. Use this before shipping a controller change to catch missing SDK methods. The mapping is maintained manually at PR-review time; CI does not validate it.

## Method Mapping Table

| Controller | Endpoint | HTTP | SDK Method | SDK File | Status |
|---|---|---|---|---|---|
| **Avatar** | /api/avatar/register | POST | `register` | client.ts | OK |
| | /api/avatar/login | POST | `login` | client.ts | OK |
| | /api/avatar/{id} | GET | `getAvatar` | client.ts | OK |
| | /api/avatar | GET | `getAllAvatars` | client.ts | OK |
| | /api/avatar/{id} | PUT | `updateAvatar` | client.ts | OK |
| | /api/avatar/{id} | DELETE | `deleteAvatar` | client.ts | OK |
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
| | /api/nft | POST | `mintNft` | client.ts | OK |
| | /api/nft/{id} | POST (transfer) | `transferNft` | client.ts | OK |
| | /api/nft/{id} | POST (burn) | `burnNft` | client.ts | OK |
| | /api/nft/{id}/metadata | GET | `getNftMetadata` | client.ts | OK |
| **AvatarNFT** | /api/avatarnft | POST | `mintAvatarNFT` | client.ts | OK |
| | /api/avatarnft/{id} | GET | `getAvatarNFT` | client.ts | OK |
| | /api/avatarnft/{id}/transfer | POST | `transferAvatarNFT` | client.ts | OK |
| | /api/avatarnft/{id}/burn | POST | `burnAvatarNFT` | client.ts | OK |
| **Bridge** | /api/bridge/routes | GET | `getBridgeRoutes` | client.ts | OK |
| | /api/bridge | POST | `initiateBridge` | client.ts | OK |
| | /api/bridge/{id} | GET | `getBridgeStatus` | client.ts | OK |
| | /api/bridge/{id}/complete | POST | `completeBridge` | client.ts | OK |
| | /api/bridge/{id}/vaa | POST | `fetchVAA` | client.ts | OK |
| | /api/bridge/{id}/redeem | POST | `redeemBridge` | client.ts | OK |
| | /api/bridge/{id}/reverse | POST | `reverseBridge` | client.ts | OK |
| | /api/bridge/history | GET | `getBridgeHistory` | client.ts | OK |
| **BlockchainOperation** | /api/blockchainoperation/{id} | GET | `getBlockchainOperation` | client.ts | OK |
| | /api/blockchainoperation/avatar/{avatarId} | GET | `getBlockchainOperationsByAvatar` | client.ts | OK |
| **STARODK** | /api/starodk/{id} | GET | `getSTARODK` | client.ts | OK |
| | /api/starodk | GET | `listSTARODK` | client.ts | OK |
| | /api/starodk | POST | `createSTARODK` | client.ts | OK |
| | /api/starodk/{id} | PUT | `updateSTARODK` | client.ts | OK |
| | /api/starodk/{id} | DELETE | `deleteSTARODK` | client.ts | OK |
| | /api/starodk/{id}/generate-dapp | POST | `generateSTARDapp` | client.ts | OK |
| | /api/starodk/{id}/deploy | POST | `deploySTARODK` | client.ts | OK |
| **Quest** | /api/quest | POST | `createQuest` | client.ts | OK |
| | /api/quest/{id} | GET | `getQuest` | client.ts | OK |
| | /api/quest/avatar/{avatarId} | GET | `listQuestsByAvatar` | client.ts | OK |
| | /api/quest/{id} | PUT | `updateQuest` | client.ts | OK |
| | /api/quest/{id} | DELETE | `deleteQuest` | client.ts | OK |
| | /api/quest/{id}/validate | POST | `validateQuestDag` | client.ts | OK |
| | /api/quest/{id}/publish | POST | `publishQuest` | client.ts | OK |
| | /api/quest/{id}/unpublish | POST | `unpublishQuest` | client.ts | OK |
| | /api/quest/{id}/execute | POST | `executeQuest` | client.ts | OK |
| | /api/quest/{id}/nodes/{nodeId}/execute | POST | `executeQuestNode` | client.ts | OK |
| | /api/quest/templates | POST | `createQuestTemplate` | client.ts | OK |
| | /api/quest/templates/{id} | GET | `getQuestTemplate` | client.ts | OK |
| | /api/quest/templates | GET | `listQuestTemplates` | client.ts | OK |
| | /api/quest/templates/{id}/instantiate | POST | `instantiateQuestTemplate` | client.ts | OK |
| | /api/quest/node-templates | POST | `createQuestNodeTemplate` | client.ts | OK |
| | /api/quest/node-templates | GET | `listQuestNodeTemplates` | client.ts | OK |
| | /api/quest/{questId}/nodes | GET | — | — | **SDK gap** |
| | /api/quest/{questId}/nodes | POST | — | — | **SDK gap** |
| | /api/quest/{questId}/nodes/{nodeId} | PUT | — | — | **SDK gap** |
| | /api/quest/{questId}/nodes/{nodeId} | DELETE | — | — | **SDK gap** |
| | /api/quest/{questId}/edges | POST | — | — | **SDK gap** |
| | /api/quest/{questId}/edges/{edgeId} | DELETE | — | — | **SDK gap** |
| | /api/quest/{questId}/topological-order | GET | — | — | **SDK gap** |
| | /api/quest/{questId}/dependencies | POST | — | — | **SDK gap** |
| | /api/quest/{questId}/dependencies/{depId} | DELETE | — | — | **SDK gap** |
| | /api/quest/{questId}/dependency-status | GET | — | — | **SDK gap** |
| | /api/quest/runs/{runId} | GET | — | — | **SDK gap** |
| | /api/quest/{questId}/runs | GET | — | — | **SDK gap** |
| | /api/quest/runs/{runId}/execution-state | GET | — | — | **SDK gap** |
| | /api/quest/runs/{runId}/complete | POST | — | — | **SDK gap** |
| | /api/quest/runs/{runId}/fork | POST | — | — | **SDK gap** |
| | /api/quest/runs/{runId}/mark-failed | POST | — | — | **SDK gap** |
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

5. **`GET /api/blockchainoperation` (list all)** — Only `getByAvatar` and `getById` exist; no list-all method.

6. **`DELETE /api/quest/templates/{id}`** — Create and list exist; delete is missing from both controller and SDK.

7. **`DELETE /api/quest/node-templates/{id}`** — Create and list exist; delete is missing from both controller and SDK.

8. **Quest sub-resource endpoints (nodes, edges, dependencies, runs) — 16 new controller routes** — Shipped in `quest-api` phase-F. No SDK methods exist yet. See the SDK gap rows in the Quest section above.

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

**Last reconciled:** 2026-07-02 (quest-dag-semantic-hardening Phase G — publish/unpublish SDK methods added)

**Note:** The quests page (`frontend/src/app/(dashboard)/quests/page.tsx`) uses `azoa.api.request(...)` directly rather than the typed SDK quest methods. The publish/unpublish calls on the page are therefore direct HTTP calls, not `publishQuest`/`unpublishQuest`. The typed SDK methods exist for external SDK consumers. This gap is tracked as part of the existing quest sub-resource SDK gap (row 8 in "Open Drift" above).
