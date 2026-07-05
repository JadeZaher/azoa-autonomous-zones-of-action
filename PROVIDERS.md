# PROVIDERS.md — API Surface & Architecture

## Overview

AZOA is a .NET 8 WebAPI with 15 controllers backed by 10 managers. The sole storage engine is SurrealDB (via `SurrealForge.*` packages). Two authentication schemes are supported: JWT (Bearer token) and X-Api-Key (auto-detected by header). Rate limiting is applied globally, with strict "financial" policies on value-moving endpoints.

_Last reconciled: 2026-06-11 (post quest-api phase-F + self-audit-one-fix)._

## Architecture

**Manager → Store → SurrealDB:**
Each business aggregate (Avatar, Wallet, Holon, NFT, etc.) has a manager exposing business logic and a corresponding SurrealDB store (`I*Store` interface + `Surreal*Store` implementation) handling persistence.

**Blockchain Providers:**
Algorand and Solana are plugged via `IBlockchainProvider` factory (indexed by chain ID). Both implement the full interface symmetrically. Adding a new chain requires implementing `IBlockchainProvider` + registering one DI line in `Program.cs`.

**DEX Adapters:**
Swap routing is dispatched by `SwapManager` to chain-specific `IDexAdapter` implementations (Tinyman for Algorand, Jupiter for Solana). Adding a DEX requires implementing `IDexAdapter` + one DI line.

## Controllers (15)

### ApiKeyController
- `POST /api/apikey` — Create an API key
- `GET /api/apikey` — List all API keys for authenticated avatar
- `POST /api/apikey/{id}/revoke` — Revoke an API key
- `DELETE /api/apikey/{id}` — Permanently delete an API key

### AvatarController
- `POST /api/avatar/register` — Register a new avatar (anonymous)
- `POST /api/avatar/login` — Login (anonymous)
- `GET /api/avatar/{id}` — Get avatar by ID
- `GET /api/avatar` — Get all avatars
- `PUT /api/avatar/{id}` — Update avatar
- `DELETE /api/avatar/{id}` — Delete avatar

### AvatarNFTController
- `POST /api/avatarnft` — Mint an avatar NFT
- `GET /api/avatarnft/{id}` — Get avatar NFT by ID
- `GET /api/avatarnft/{chainType}/{contractAddress}/{tokenId}` — Get by token
- `GET /api/avatarnft/avatar/{avatarId}` — List avatar NFTs
- `POST /api/avatarnft/{id}/transfer` — Transfer NFT
- `POST /api/avatarnft/{id}/burn` — Burn NFT

### BlockchainOperationController
- `GET /api/blockchainoperation/{id}` — Get operation by ID
- `GET /api/blockchainoperation/avatar/{avatarId}` — List operations by avatar

### BridgeController
- `GET /api/bridge/routes` — List available bridge routes
- `POST /api/bridge/initiate` — Start a bridge transaction
- `GET /api/bridge/{id}` — Get bridge status
- `POST /api/bridge/{id}/complete` — Complete bridge redemption

### DappCompositionController
- `GET /api/dappcomposition/{id}` — Get dapp composition

### DappSeriesController
- `GET /api/dappseries/{id}` — Get dapp series by ID
- `GET /api/dappseries` — Query dapp series
- `POST /api/dappseries` — Create dapp series
- `PUT /api/dappseries/{id}` — Update dapp series

### HolonController
- `GET /api/holon/{id}` — Get holon by ID
- `GET /api/holon` — Query holons
- `POST /api/holon` — Create holon
- `PUT /api/holon/{id}` — Update holon

### NetworkController
- `GET /api/network` — Get network information

### NftController
- `GET /api/nft/{id}` — Get NFT by ID

### QuestController

30 endpoints total. Per ADR §2.2, `activate`/`complete`/`fail`/`execution-state` are exposed on `QuestRun`, not `Quest`.

**Quest CRUD:**
- `POST /api/quest` — Create quest
- `GET /api/quest/{id}` — Get quest by ID
- `GET /api/quest/avatar/{avatarId}` — List quests by avatar
- `PUT /api/quest/{id}` — Update quest
- `DELETE /api/quest/{id}` — Delete quest
- `POST /api/quest/{id}/validate` — Validate quest DAG

**Execution:**
- `POST /api/quest/{id}/execute` — Execute quest (returns QuestRun)
- `POST /api/quest/{id}/nodes/{nodeId}/execute` — Execute single node
- `POST /api/quest/runs/{runId}/fork` — Fork a run at a node
- `POST /api/quest/runs/{runId}/mark-failed` — Mark run as failed

**Templates:**
- `POST /api/quest/templates` — Create quest template
- `GET /api/quest/templates/{id}` — Get quest template by ID
- `GET /api/quest/templates` — List quest templates
- `POST /api/quest/templates/{id}/instantiate` — Instantiate quest from template

**Node Templates:**
- `POST /api/quest/node-templates` — Create node template
- `GET /api/quest/node-templates` — List node templates

**Nodes sub-resource:**
- `GET /api/quest/{questId}/nodes` — List nodes
- `POST /api/quest/{questId}/nodes` — Add node
- `PUT /api/quest/{questId}/nodes/{nodeId}` — Update node
- `DELETE /api/quest/{questId}/nodes/{nodeId}` — Delete node

**Edges sub-resource:**
- `POST /api/quest/{questId}/edges` — Add edge
- `DELETE /api/quest/{questId}/edges/{edgeId}` — Remove edge
- `GET /api/quest/{questId}/topological-order` — Get topological order

**Dependencies sub-resource:**
- `POST /api/quest/{questId}/dependencies` — Add dependency
- `DELETE /api/quest/{questId}/dependencies/{depId}` — Remove dependency
- `GET /api/quest/{questId}/dependency-status` — Check dependency status

**QuestRun read surface:**
- `GET /api/quest/runs/{runId}` — Get run by ID
- `GET /api/quest/{questId}/runs` — List runs by quest
- `GET /api/quest/runs/{runId}/execution-state` — Get execution state (per ADR §2.2)
- `POST /api/quest/runs/{runId}/complete` — Mark run completed (per ADR §2.2)

### SearchController
- `POST /api/search` — Full-text search

### STARODKController
- `GET /api/starodk/{id}` — Get STARODK by ID
- `GET /api/starodk` — List STARODK
- `POST /api/starodk` — Create STARODK
- `DELETE /api/starodk/{id}` — Delete STARODK

### SwapController
- `GET /api/swap/quote` — Get swap quote (query params: chain, tokenIn, tokenOut, amount, slippage)
- `POST /api/swap/execute` — Build unsigned swap transaction; accepts optional `Idempotency-Key` header; rate-limited ("financial" policy)

### WalletController
- `GET /api/wallet/{id}` — Get wallet by ID
- `GET /api/wallet` — Query wallets
- `POST /api/wallet` — Create wallet
- `PUT /api/wallet/{id}` — Update wallet
- `DELETE /api/wallet/{id}` — Delete wallet
- `POST /api/wallet/{id}/set-default` — Set default wallet
- `GET /api/wallet/{id}/portfolio` — Get wallet portfolio
- `POST /api/wallet/generate` — Generate new wallet on-platform
- `POST /api/wallet/connect` — Connect external wallet
- `POST /api/wallet/{id}/export` — Export wallet private key
- `POST /api/wallet/{id}/topup` — Top-up with test tokens (dev/test only)
- `GET /api/wallet/types` — Get wallets grouped by type

## Managers (10)

| Manager | Public Methods | Role |
|---|---|---|
| **QuestManager** | 16 | Quest DAG orchestration, execution, templates |
| **HolonManager** | 15 | Holon CRUD, asset queries, portfolio aggregation |
| **DappCompositionManager** | 14 | DApp series + composition queries |
| **WalletManager** | 11 | Wallet CRUD, balance federation, portfolio, faucet |
| **AvatarManager** | 6 | Avatar lifecycle (register, login, update, delete) |
| **NftManager** | 6 | NFT queries, avatar NFT bindings |
| **STARManager** | 6 | STARODK CRUD, dapp generation, deployment |
| **SwapManager** | 4 | DEX dispatcher (thin facade to adapters) |
| **BlockchainOperationManager** | 4 | Blockchain operation CRUD + status queries |
| **SearchManager** | 2 | Full-text search dispatch |

## SDK — AzoaApiClient (84 Typed Methods)

The SDK provides a typed `AzoaApiClient` wrapping all 15 controllers. All amounts are strings for arbitrary precision. Notable method families:

**Avatar:** `login`, `getAvatar`, `getAllAvatars`, `deleteAvatar`

**Wallet:** `getWallet`, `listWallets`, `createWallet`, `updateWallet`, `deleteWallet`, `setDefaultWallet`, `getWalletPortfolio`, `generateWallet`, `connectWallet`, `exportWallet`

**Holon:** Via `HolonQueryBuilder` for fluent filtering + execute pattern

**NFT:** `getNft`, `mintNft`, `transferNft`, `burnNft`, `getNftMetadata`

**Bridge:** `getBridgeRoutes`, `initiateBridge`, `getBridgeStatus`, `completeBridge`, `getBridgeHistory`, `fetchVAA`, `redeemBridge`, `reverseBridge`

**Quest:** `createQuest`, `getQuest`, `listQuestsByAvatar`, `updateQuest`, `deleteQuest`, `executeQuest`, `executeQuestNode`, `createQuestTemplate`, `getQuestTemplate`, `listQuestTemplates`, `instantiateQuestTemplate`

**STARODK:** `getSTARODK`, `listSTARODK`, `createSTARODK`, `deleteSTARODK`, `generateSTARDapp`, `deploySTARODK`

**AvatarNFT:** `mintAvatarNFT`, `getAvatarNFT`, `getAvatarNFTByToken`, `listAvatarNFTs`, `transferAvatarNFT`, `burnAvatarNFT`, `bindHolonToNFT`, `listNFTHolonBindings`, `updateHolonBinding`, `removeHolonBinding`, `bindWalletToNFT`, `listNFTWalletBindings`, `updateWalletBinding`, `removeWalletBinding`

**Search:** `search`, `getSearchFacets`

**BlockchainOperation:** `getBlockchainOperation`, `getBlockchainOperationsByAvatar`

**ApiKey:** `createApiKey`, `listApiKeys`, `revokeApiKey`, `deleteApiKey`

**Verification:** `verifyNFTOwnership`, `verifyHolonAccess`, `verifyWalletAccess`

**Other:** `validateQuestDag`

## Blockchain Providers

### Algorand Provider
Implements `IBlockchainProvider` fully:
- `GetBalance` — Native ALGO balance
- `GetTokenBalance` — ASA balance (via indexer)
- `GetNFTMetadata` — ASA params
- `Transfer` / `Burn` / `Mint` / `CreateASA` / `OptIn` — **real server-side signing**
  (params → build → sign → submit → confirm), plus a soulbound-ASA mint primitive
- `SendTransaction` — POST to algod
- `GetTransactionStatus` — Query algod pending tx / indexer
- `GetFeeEstimate` — algod /v2/status
- `Health` — Connectivity check

**Server-side signing (signing-core-keystone):** the .NET `AlgorandProvider` now
performs real Ed25519 signing through a chain-agnostic seam — `ITransactionSigner`
(+ `SigningKeyMaterial(byte[])`) selected by ChainType via `ITransactionSignerFactory`,
implemented for Algorand by `AlgorandTransactionSigner`. Canonical msgpack
build/encode/sign comes entirely from the `Algorand2` package (no homebake msgpack
or curve math). Value-moving calls honor `RetrySafety.Broadcast` (no post-broadcast
retry). Keygen (`WalletKeyService.GenerateAlgorandKeypair`) produces real,
spendable Ed25519 keypairs with restorable 25-word mnemonics. Address validation
uses the real SHA-512/256 checksum (`Algorand.Address.IsValid`).

**Caveats:**
- Custody is INTERIM: the signing key is resolved by decrypting
  `AZOA:Algorand:PlatformMnemonic` from config (no per-user ownership check). The
  production ownership-checked, KMS-backed, byte[]-zeroing resolver is the
  `custody-key-management` track (DEPLOY-STEPS-TODO B3/P1). Mainnet stays gated (B6).
- Soulbound clawback-revoke is deferred (D4 → H2); the mint path sets the platform
  as manager/freeze/clawback so revoke is a pure follow-up.
- The TypeScript SDK provider still builds `json-descriptor`/wallet-adapter signing
  envelopes client-side; the real server-side signer above is the .NET backend path.

### Solana Provider
Implements `IBlockchainProvider` fully:
- `GetBalance` — SOL balance
- `GetTokenBalance` — SPL token balance (via RPC)
- `GetNFTMetadata` — Token supply only (name, symbol, URI not resolved)
- `SendTransaction` — Base64 transaction via RPC
- `GetTransactionStatus` — getTransaction RPC
- `GetFeeEstimate` — getRecentPrioritizationFees
- `Health` — Slot + supply fetch

**Caveats:**
- `validateAddress` is regex-only (base58 length check)
- `getTokenMetadata` returns supply stats only; full NFT metadata (name, image, URI) not fetched
- `buildMint` and `buildBurn` produce `json-descriptor` format (intent description, not executable transaction bytes)

## DEX Adapters

### TinymanAdapter (Algorand)
- `getQuote` — Tinyman API quote (requires `@tinymanorg/tinyman-js-sdk` peer dep)
- `buildSwapTransaction` — Native Algorand transaction with atomic group

**Note:** Decimals are caller-provided (`assetInDecimals` / `assetOutDecimals` params); fallback is 6 when caller omits them. Full indexer ASA lookup for the no-caller-supplied case is tracked as a TODO in the implementation — quotes for non-6-decimal ASAs will be inaccurate if the caller does not supply decimals. Requires `@tinymanorg/tinyman-js-sdk` peer dep.

### JupiterAdapter (Solana)
- `getQuote` — Jupiter v2 REST API
- `buildSwapTransaction` — Cross-platform; uses `base64Decode` from `core/encoding.ts` (no `Buffer.from()` — safe in React Native and browser)

## MCP Surface

The `/mcp` endpoint exposes 5 read-only tools + HNSW vector search via `ModelContextProtocol.AspNetCore`. Avatar-scoped auth required. (See `mcp-surface` conductor track.)

## Auth & Safety

**Dual Auth:**
- Bearer token (JWT) — standard flow
- X-Api-Key header — auto-detected; no scope required

**Rate Limiting:**
- Global policy: modest limits to prevent abuse
- "financial" policy: strict limits on value-moving endpoints (topup, bridge, swap)

**Idempotency:**
Spine for replay safety on key endpoints. Client can supply `Idempotency-Key` header; server derives deterministic key when absent.

**Security Registrations:**
- `Secp256k1VaaSignatureVerifier` — Wormhole VAA verification
- `ApiKeyAuthenticationHandler` — SHA-256 hashed key storage + per-request validation
- `assertUuid` guards in SDK — prevents path traversal on all URL-interpolated IDs
- STARODK upsert IDOR-resistance — STARODK upsert routes scope the lookup by route id + authenticated avatar identity; caller-supplied `model.AvatarId` on the body is ignored (closes IDOR surface caught by self-audit-one-fix)

See `conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md` for full pre-launch gates.

## Removed Surfaces

**EOL as of SurrealDB migration (2026-05-27):**
- `IAZOAStorageProvider` — EF + PostgreSQL + InMemory paths are deleted
- No controllers or managers use EF code paths

Old documentation mentioning "9 controllers / 7 managers / EF+InMemory" is obsolete.

## Storage Duality Status

Fully migrated to SurrealDB. All operational stores (`Avatar`, `Wallet`, `Holon`, `NFT`, `Quest`, `Bridge`, `BlockchainOperation`, `STAR`, `ApiKey`, `Idempotency`, `Saga`) use Surreal. Only `DappSeriesStore` remains InMemory (intentional, pending `dapp-composition` track close-out).
