# PROVIDERS.md — API Surface & Architecture

## Overview

AZOA is a .NET 10 WebAPI. The sole storage engine is SurrealDB (via `SurrealForge.*` packages). JWT and `X-Api-Key` authentication are supported, and stricter rate limits apply to value-moving endpoints.

_Last reconciled: 2026-09-07 — re-derived from `Controllers/*.cs`, `Providers/Blockchain/`, `Managers/` and `Program.cs` after the Avatar→User / Quest→Action rename and the Phase 5 landing._

## Architecture

**Manager → Store → SurrealDB:**
Each business aggregate (User, Wallet, Holon, NFT, etc.) has a manager exposing business logic and a corresponding SurrealDB store (`I*Store` interface + `Surreal*Store` implementation) handling persistence.

**Value rails and blockchain providers:**
`IValueRailProviderFactory` resolves a bound `IValueRailProvider` for one rail + environment, whether or not that rail is a chain (decision D9). It exists because the older chain-typed factory could not express a fiat rail at all: registration was `Func<IBlockchainProvider>`, so a rail with no contracts, faucet, or wrapped assets had nowhere to go. `IBlockchainProviderFactory` extends it for the chain case. See `Providers/Blockchain/AGENTS.md`.

Four providers are registered in `Program.cs` (~L1373): **Algorand**, **Solana**, **Ethereum**, and **Simulated**. A provider is bound once per canonical `(chainType, network)` pair through a `Lazy` cache and is never shared across networks — `Initialize` mutates the provider's client, so a shared instance would let a Testnet resolution retarget a cached Devnet provider.

Bridge capability is a **module, not a boolean**: there is no `SupportsBridging` flag. A chain that can bridge implements `IBridgeSourceLeg` (`LockAsync`/`ReleaseAsync`) and/or `IBridgeTargetLeg` (`MintWrappedAsync`/`BurnWrappedAsync`), and `TryGetModule` resolves it at runtime. Absence *is* the fail-closed refusal, structurally — there is no method to call and no boolean that can be set true by mistake — and it expresses a direction asymmetry the boolean could not (Algorand is a proven source and deliberately not a target). Adding a chain still requires implementation, registration, and conformance review; registration alone does not make it launchable.

**DEX Adapters:**
Swap routing is dispatched by `SwapManager` to chain-specific `IDexAdapter` implementations (Tinyman for Algorand, Jupiter for Solana). Adding a DEX requires implementing `IDexAdapter` + one DI line.

**KYC Providers:**
Identity verification is selected through `Kyc:Provider` and the
`IKycProviderService` seam. `manual` is Development-only; `veriff` and the
operator-named `hosted` adapter are fail-closed scaffolds until reviewed
integrations land. Availability alone never grants trust: operators must also
set an exact `Kyc:ApprovalPolicy` provider allow-list, policy version, and
assurance label. Attempts persist that profile in a versioned provenance
envelope, so provider or policy rotation requires re-verification. See
`docs/TENANT-CUSTODIAL-ONBOARDING.md`.

## Controllers (35)

Full inventory with route prefix. The sections that follow detail the surfaces the SDK
and frontend consume; operator-, webhook- and admin-only controllers are listed here by
prefix rather than enumerated.

| Controller | Route prefix | Endpoints |
|---|---|---|
| ActionController | `api/action` | 47 |
| AllocationController | `api/allocation` | 1 |
| AllocationReceiptController | `api/allocation/receipt` | 2 |
| AnalyticsController | `api/analytics` | 2 |
| ApiKeyController | `api/apikey` | 7 |
| BackfillController | `api/admin/backfill` | 2 |
| BlockchainOperationController | `api/blockchainoperation` | 2 |
| BridgeController | `api/bridge` | 7 |
| ConsentController | `api/user/consent` | 4 |
| CredentialController | `api/credential` | 1 |
| DappCompositionController | `api/dapp-series/{id}` | 6 |
| DappSeriesController | `api/dapp-series` | 10 |
| ExternalAuthController | `auth/oauth` | 7 |
| HolonController | `api/holon` | 16 |
| HolonTypeRegistryController | `api/holon-types` | 5 |
| KeyRotationController | `api/admin/key-rotation` | 1 |
| KycCallbackController | `api/kyc/callback` | 1 |
| KycController | `api/kyc` | 5 |
| NetworkController | `api/network` | 1 |
| NftController | `api/nft` | 7 |
| NodeGovernanceController | `api/node-governance` | 9 |
| NodeOperatorController | `api/operator` | 9 |
| NodeOperatorSessionController | `api/operator/session` | 2 |
| NodeTransparencyController | `api/node-transparency` | 5 |
| STARODKController | `api/starodk` | 9 |
| SagaOperatorController | `api/admin/sagas` | 3 |
| SearchController | `api/search` | 2 |
| SwapController | `api/swap` | 2 |
| TenantConsentController | `api/tenant/consent` | 2 |
| TenantController | `api/tenant` | 9 |
| TenantKycConfigurationController | `api/tenant/kyc` | 3 |
| UserController | `api/user` | 8 |
| UserNFTController | `api/usernft` | 19 |
| WalletAuthController | `api/user` (wallet challenge/verify) | 5 |
| WalletController | `api/wallet` | 12 |

⚠ `NodeGovernanceController` governs **node parameters** — fee schedule, treasury,
audit trail. It is unrelated to the D20 on-chain-vote action nodes, which are not
enabled; see "Phase 5 primitives that are NOT enabled" below.

### Endpoint detail

### ApiKeyController
- `POST /api/apikey` — Create an API key
- `GET /api/apikey` — List all API keys for authenticated user
- `POST /api/apikey/{id}/revoke` — Revoke an API key
- `DELETE /api/apikey/{id}` — Permanently delete an API key

### UserController
- `POST /api/user/register` — Register a new user (anonymous)
- `POST /api/user/login` — Login (anonymous)
- `GET /api/user/{id}` — Get user by ID
- `GET /api/user` — Get all users
- `PUT /api/user/{id}` — Update user
- `DELETE /api/user/{id}` — Delete user

### UserNFTController
- `POST /api/usernft` — Mint a user NFT
- `GET /api/usernft/{id}` — Get user NFT by ID
- `GET /api/usernft/{chainType}/{contractAddress}/{tokenId}` — Get by token
- `GET /api/usernft/user/{userId}` — List user NFTs
- `POST /api/usernft/{id}/transfer` — Transfer NFT
- `POST /api/usernft/{id}/burn` — Burn NFT

### BlockchainOperationController
- `GET /api/blockchainoperation/{id}` — Get operation by ID
- `GET /api/blockchainoperation/user/{userId}` — List operations by user

### BridgeController
- `GET /api/bridge/routes` — List available bridge routes
- `POST /api/bridge/initiate` — Start a bridge transaction
- `GET /api/bridge/{id}` — Get bridge status

### DappCompositionController
Mounted **under** the series route, not on a `dappcomposition` prefix.
- `POST /api/dapp-series/{id}/compose` — Compose the series
- `GET /api/dapp-series/{id}/validate` — Validate the composition
- `GET /api/dapp-series/{id}/manifest` — Read the manifest
- `POST /api/dapp-series/{id}/generate` — Generate
- `POST /api/dapp-series/{id}/deploy` — Deploy
- `GET /api/dapp-series/{id}/status` — Composition status

### DappSeriesController
- `GET /api/dapp-series/{id}` — Get dapp series by ID
- `GET /api/dapp-series` — List dapp series
- `POST /api/dapp-series` — Create dapp series
- `PUT /api/dapp-series/{id}` — Update dapp series
- `DELETE /api/dapp-series/{id}` — Delete dapp series
- `GET|POST /api/dapp-series/{seriesId}/actions` — List / add series actions
- `DELETE /api/dapp-series/{seriesId}/actions/{actionId}` — Remove a series action
- `PUT /api/dapp-series/{seriesId}/actions/{actionId}/order` — Reorder a series action
- `PUT /api/dapp-series/{seriesId}/actions/{actionId}/mappings` — Update its mappings

### AnalyticsController
- `GET /api/analytics/transactions` — Paged transaction history
- `GET /api/analytics/positions` — Current positions

### CredentialController
- `POST /api/credential/{assetId}/revoke` — Revoke a soulbound credential you issued, by
  clawing the ASA back on chain. Rate-limited ("financial" policy), idempotent on the
  `Idempotency-Key` header. **`200` means revoked, and nothing else does**: a broadcast
  that has not confirmed returns `202 Accepted` with `isError = true` and code
  `PendingConfirmation`, so neither a status-code check nor an `isError` check can
  mistake it for a completed revocation. Do not re-submit a `202` — the transaction is
  already on the network; reconcile, or poll the operation named by `operationId`.

  ⚠ **The rail behind this route is inert.** See "Phase 5 primitives that are NOT
  enabled" below before describing it as a working revocation capability.

### HolonController
- `GET /api/holon/{id}` — Get holon by ID
- `GET /api/holon` — Query holons
- `POST /api/holon` — Create holon
- `PUT /api/holon/{id}` — Update holon

### NetworkController
- `GET /api/network` — Get network information

### NftController
- `GET /api/nft/{id}` — Get NFT by ID

### ActionController

47 endpoints total. Per ADR §2.2, `activate`/`complete`/`fail`/`execution-state` are exposed on `ActionRun`, not `ActionDefinition`.

⚠ **D35 naming.** The C# entity is `ActionDefinition` and its namespaces are plural
(`Models.Actions`), to avoid colliding with `System.Action` under `ImplicitUsings`.
The DB table, the REST route segment, and the SDK type are all plain `action`. The
wire surface is unaffected by the C# suffix — do not add `-definition` to a route.

**Action CRUD:**
- `POST /api/action` — Create action
- `GET /api/action/{id}` — Get action by ID
- `GET /api/action/user/{userId}` — List actions by user
- `PUT /api/action/{id}` — Update action
- `DELETE /api/action/{id}` — Delete action
- `POST /api/action/{id}/validate` — Validate action DAG

**Execution:**
- `POST /api/action/{id}/execute` — Execute action (returns ActionRun)
- `POST /api/action/{id}/nodes/{nodeId}/execute` — Execute single node
- `POST /api/action/runs/{runId}/fork` — Fork a run at a node
- `POST /api/action/runs/{runId}/mark-failed` — Mark run as failed

**Templates:**
- `POST /api/action/templates` — Create action template
- `GET /api/action/templates/{id}` — Get action template by ID
- `GET /api/action/templates` — List action templates
- `POST /api/action/templates/{id}/instantiate` — Instantiate action from template

**Node Templates:**
- `POST /api/action/node-templates` — Create node template
- `GET /api/action/node-templates` — List node templates

**Nodes sub-resource:**
- `GET /api/action/{actionId}/nodes` — List nodes
- `POST /api/action/{actionId}/nodes` — Add node
- `PUT /api/action/{actionId}/nodes/{nodeId}` — Update node
- `DELETE /api/action/{actionId}/nodes/{nodeId}` — Delete node

**Edges sub-resource:**
- `POST /api/action/{actionId}/edges` — Add edge
- `DELETE /api/action/{actionId}/edges/{edgeId}` — Remove edge
- `GET /api/action/{actionId}/topological-order` — Get topological order

**Dependencies sub-resource:**
- `POST /api/action/{actionId}/dependencies` — Add dependency
- `DELETE /api/action/{actionId}/dependencies/{depId}` — Remove dependency
- `GET /api/action/{actionId}/dependency-status` — Check dependency status

**ActionRun read surface:**
- `GET /api/action/runs/{runId}` — Get run by ID
- `GET /api/action/{actionId}/runs` — List runs by action
- `GET /api/action/runs/{runId}/execution-state` — Get execution state (per ADR §2.2)
- `POST /api/action/runs/{runId}/complete` — Mark run completed (per ADR §2.2)

### SearchController
- `POST /api/search` — Full-text search

### STARODKController
- `GET /api/starodk/{id}` — Get STARODK by ID
- `GET /api/starodk` — List STARODK
- `POST /api/starodk` — Create or update STARODK
- `PUT /api/starodk/{id}` — Update STARODK (IDOR-resistant; see Auth & Safety)
- `DELETE /api/starodk/{id}` — Delete STARODK
- `POST /api/starodk/{id}/generate` — Generate the dapp (**not** `/generate-dapp`)
- `POST /api/starodk/{id}/deploy` — Deploy
- `GET /api/starodk/{id}/ecosystem` — Read the ecosystem tree
- `POST /api/starodk/{id}/ecosystem/dapp-series` — Attach a series to the tree

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

## Managers (28)

`Managers/*.cs`, grouped by the surface each one backs. Method counts are no longer
tracked here — they went stale every track; read the interface in `Interfaces/Managers/`.

| Group | Managers |
|---|---|
| Core domain | `ActionManager`, `HolonManager`, `HolonTypeRegistryManager`, `NftManager`, `UserManager`, `WalletManager`, `SearchManager`, `BlockchainOperationManager`, `SwapManager` |
| Dapp / STAR | `DappCompositionManager`, `STARManager` |
| Identity & consent | `ConsentManager`, `WalletAuthManager`, `TenantManager`, `TenantCustodialAccountManager` |
| KYC control plane | `KycManager`, `KycControlPlaneManager` |
| Value rail | `AllocationManager`, `AllocationReceiptManager`, `FungibleTokenManager`, `CredentialManager` |
| Node operation | `NodeOperatorManager`, `NodeGovernanceManager`, `NodeFeeScheduleManager`, `NodeFeeSettlementManager`, `NodeTreasuryManager`, `NodeTransparencyManager`, `NodeTransparencyHistoryService` |

## SDK — AzoaApiClient

`sdk/azoa-wallet/src/api/client.ts` carries ~126 typed methods; holon reads and writes
live on the fluent `HolonQueryBuilder` (`src/client/holon-query.ts`) rather than on the
client. It does **not** wrap all 35 controllers — `API_SYNC.md` is the authoritative
controller ↔ SDK map and lists the gaps. All amounts are strings for arbitrary precision.

⚠ Several method families named below regressed out of the SDK and no longer exist:
`generateWallet`, `connectWallet`, `exportWallet`, `topupWallet`, `getWalletsByType`,
`getNetworkInfo`, `completeBridge`. Their controller endpoints are still live. See
`API_SYNC.md` "Open Drift" rows 9–11.

Notable method families:

**User:** `login`, `getUser`, `getAllUsers`, `deleteUser`

**Wallet:** `getWallet`, `listWallets`, `createWallet`, `updateWallet`, `deleteWallet`, `setDefaultWallet`, `getWalletPortfolio`, `generateWallet`, `connectWallet`, `exportWallet`

**Holon:** Via `HolonQueryBuilder` for fluent filtering + execute pattern

**NFT:** `getNft`, `mintNft`, `transferNft`, `burnNft`, `getNftMetadata`

**Bridge:** `getBridgeRoutes`, `initiateBridge`, `getBridgeStatus`, `completeBridge`, `getBridgeHistory`, `fetchVAA`, `redeemBridge`, `reverseBridge`

**Action:** `createAction`, `getAction`, `listActionsByUser`, `updateAction`, `deleteAction`, `executeAction`, `executeActionNode`, `createActionTemplate`, `getActionTemplate`, `listActionTemplates`, `instantiateActionTemplate`

**STARODK:** `getSTARODK`, `listSTARODK`, `createSTARODK`, `deleteSTARODK`, `generateSTARDapp`, `deploySTARODK`

**UserNFT:** `mintUserNFT`, `getUserNFT`, `getUserNFTByToken`, `listUserNFTs`, `transferUserNFT`, `burnUserNFT`, `bindHolonToNFT`, `listNFTHolonBindings`, `updateHolonBinding`, `removeHolonBinding`, `bindWalletToNFT`, `listNFTWalletBindings`, `updateWalletBinding`, `removeWalletBinding`

**Search:** `search`, `getSearchFacets`

**BlockchainOperation:** `getBlockchainOperation`, `getBlockchainOperationsByUser`

**ApiKey:** `createApiKey`, `listApiKeys`, `revokeApiKey`, `deleteApiKey`

**Verification:** `verifyNFTOwnership`, `verifyHolonAccess`, `verifyWalletAccess`

**Other:** `validateActionDag`

## Blockchain Providers

Amounts crossing `IBlockchainProvider` are `RailAmount` (`Core/RailAmount.cs`) —
arbitrary-precision `BigInteger` base units plus the `ValueDimension` they are counted
in. This replaced a `ulong` surface whose ceiling of ~1.8447e19 wei is about **18.45
ETH**, which the shared interface could not express at all. Algorand and Solana narrow
to 64-bit chain base units via `NarrowToChainBaseUnits`; Ethereum deliberately never
narrows. `RejectUnsettleableAmount` (on `BaseBlockchainProvider`) refuses a
fiat-denominated amount rather than reinterpreting it as base units.

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
  `custody-key-management` track (retired deploy registry item B3/P1, now `docs/NODE-HOST.md` §8). Mainnet stays gated (B6).
- Soulbound clawback-revoke **shipped as a route but cannot revoke anything.** The mint
  path sets the platform as manager/freeze/clawback, and `POST /api/credential/{assetId}/revoke`
  exists, but the rail is inert — see "Phase 5 primitives that are NOT enabled" below.
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

### Ethereum Provider

Registered in `Program.cs` alongside Algorand and Solana. Implementation notes live in
`Providers/Blockchain/Ethereum/AGENTS.md`; the load-bearing contracts are:

- **Every internal is `BigInteger`** — wei amounts, balances, gas and fee math, nonces,
  chain ids, ABI values. Wei never transits a `ulong`. `IEthereumValueModule` is the
  direct value path, not an escape hatch.
- **Signing is EIP-2718 type-2 only.** The provider produces the signing payload
  `0x02 ‖ rlp([chainId, nonce, maxPriorityFeePerGas, maxFeePerGas, gasLimit, to, value,
  data, accessList])`; `EthereumTransactionSigner` returns the fully framed envelope
  `0x02 ‖ rlp([…, yParity, r, s])`, which the provider hex-encodes and submits
  **unmodified** — re-encoding after signing would invalidate the signature. The
  signature is secp256k1 over `keccak256(payload)` with y-parity (0/1), not legacy V;
  the signer refuses any payload not starting `0x02` rather than guessing, and
  re-derives the sender from the assembled envelope before returning.

**Caveat:** like every other chain, Ethereum real-value routes stay behind
`RealValueEnabled=false` until a provider passes the full lock/mint/burn/release plus
production-custody review.

## DEX Adapters

### TinymanAdapter (Algorand)
- `getQuote` — Tinyman API quote (requires `@tinymanorg/tinyman-js-sdk` peer dep)
- `buildSwapTransaction` — Native Algorand transaction with atomic group

**Note:** Decimals are caller-provided (`assetInDecimals` / `assetOutDecimals` params); fallback is 6 when caller omits them. Full indexer ASA lookup for the no-caller-supplied case is tracked as a TODO in the implementation — quotes for non-6-decimal ASAs will be inaccurate if the caller does not supply decimals. Requires `@tinymanorg/tinyman-js-sdk` peer dep.

### JupiterAdapter (Solana)
- `getQuote` — Jupiter v2 REST API
- `buildSwapTransaction` — Cross-platform; uses `base64Decode` from `core/encoding.ts` (no `Buffer.from()` — safe in React Native and browser)

## MCP Surface

The `/mcp` endpoint exposes 6 read-only tools (`[McpServerTool]`-decorated) + HNSW vector search via `ModelContextProtocol.AspNetCore`. User-scoped auth required. (See `mcp-surface` conductor track.)

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
- STARODK upsert IDOR-resistance — STARODK upsert routes scope the lookup by route id + authenticated user identity; caller-supplied `model.UserId` on the body is ignored (closes IDOR surface caught by self-audit-one-fix)

See `docs/RESIDUAL-RISK-RUNBOOK.md` for full pre-launch gates.

## Phase 5 primitives that are NOT enabled

Two primitives landed in commit `a59bd85` and **must not be described as done.** Both
failed the same way: a control was wired on one path and the thing that consumes its
result was wired to a different, unprotected one. Each has a design track dated
2026-09-06 that has not been implemented.

**"Not enabled" is now a control, not a claim.** `Services/Actions/NotEnabledActionNodeTypes.cs`
holds the blocked set — `GovernanceProposalCreate`, `GovernanceVote`, `GovernanceTally`,
`CredentialRevoke` — and refuses at **publish plus both dispatch seams**, with one shared
refusal string. Before `7b28a16` this was frontend copy and prose only: `Program.cs`
assembly-scans and registers every `IActionNodeHandler`, so a published action reaching
`GovernanceVote` *would* have cast a ballot and `CredentialRevoke` *would* have attempted
an Algorand clawback. Remove a member from that set only when its track closes.

⚠ The blocked set covers **action node types**, not the REST surface.
`POST /api/credential/{assetId}/revoke` is still reachable — it is inert for the reasons
below, not gated by this control.

### Governance vote / tally (D20) — live Sybil hole, do not enable

`GovernanceVoteNodeHandler` claims correctly through `IPersonUniquenessGate`.
`GovernanceTallyNodeHandler` then **counts raw child holons and never reads the claim
ledger.** One verified user can `POST /api/holon` 500 times with
`{"assetType":"GovernanceVote","parentHolonId":"<proposal>","metadata":{"option":"yes"}}`;
none pass through the handler, none take a claim, and the tally counts all 500. Two
compounding defects: the ballot ordering key is read from attacker-controlled holon
metadata, and `HolonManager.UpdateAsync` blind-merges caller metadata so a voter can
rewrite a cast ballot's option.

Corrective: `conductor/tracks/governance-vote-of-record_20260906` (D39 — the uniqueness
claim *is* the vote of record; the tally reads `person_operation_claim` and never reads
holons).

### Credential revocation (D19) — inert

`POST /api/credential/{assetId}/revoke` authorizes the credential's **issuer, not its
holder** — but the only production mint path stamps `issuer_user_id` = the minting
caller, who is also the holder, so the authorization check is **vacuous**. No production
path writes `current_owner` on a soulbound row, so a real revoke dies at the
holder-address precondition before authorization is reached; a revoke confirmed by
reconciliation never re-points `nft_ownership`; and the Algorand clawback precondition
uses `RequireOptedInAsync`, which refuses a frozen holding — while soulbound credentials
are minted `defaultFrozen: true`, so the guard refuses exactly the case it exists to
serve.

Corrective: `conductor/tracks/credential-third-party-issuance_20260906` (D41 — soulbound
mint takes a recipient distinct from the caller). This is an API change to the *mint*
path; patching revoke alone cannot work, because the data it authorizes against is never
written.

## Removed Surfaces

**EOL as of SurrealDB migration (2026-05-27):**
- `IAZOAStorageProvider` — EF + PostgreSQL + InMemory paths are deleted
- No controllers or managers use EF code paths

Old documentation mentioning "9 controllers / 7 managers / EF+InMemory" is obsolete, and so is the "15 controllers / 10 managers" count this file itself carried until 2026-09-07.

## Storage Duality Status

Fully migrated to SurrealDB. All operational stores (`User`, `Wallet`, `Holon`, `NFT`, `ActionDefinition`, `Bridge`, `BlockchainOperation`, `STAR`, `ApiKey`, `Idempotency`, `Saga`) use Surreal. Only `DappSeriesStore` remains InMemory (intentional, pending `dapp-composition` track close-out).
