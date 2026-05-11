# Tracks

| Track | Status | Description |
|-------|--------|-------------|
| [core-api](tracks/core-api/spec.md) | `[x]` | Unified provider pattern, base abstractions, OASIS result/response models |
| [avatar-api](tracks/avatar-api/spec.md) | `[x]` | Avatar controller (register, login, CRUD, provider overrides) — OAuth-like identity layer + multi-wallet |
| [holon-api](tracks/holon-api/spec.md) | `[x]` | Holon controller (CRUD, query, cross-provider search, mint, exchange) — NFTs as storage-backed holons |
| [star-api](tracks/star-api/spec.md) | `[x]` | STAR dapp-generator API (scaffold, configure, deploy dapps that operate on holons) |
| [startup-config](tracks/startup-config/spec.md) | `[x]` | Program.cs / Startup.cs wiring, Swagger, JWT, middleware, manager DI |
| [tests](tracks/tests/spec.md) | `[x]` | 256 tests green (215 unit + 41 integration). Stryker mutation score 59.41 % |
| [wallet-api](tracks/wallet-api/spec.md) | `[x]` | First-class Wallet API with CRUD, portfolio analytics, and default-wallet management |
| [nft-api](tracks/nft-api/spec.md) | `[x]` | Semantic NFT layer (mint, transfer, burn, metadata) built on Holon infrastructure |
| [search-api](tracks/search-api/spec.md) | `[x]` | Unified cross-entity search with pagination, filtering, and faceted results |
| [providers-and-cross-chain-bridge](tracks/providers-and-cross-chain-bridge/spec.md) | `[x]` | Algorand + Solana providers via REST/RPC, BlockchainProviderFactory, trusted + Wormhole cross-chain bridge |
| [validation-mapping](tracks/validation-mapping/spec.md) | `[x]` | FluentValidation input pipeline + AutoMapper entity-DTO mapping layer |
| [oasis-wallet-sdk](tracks/oasis-wallet-sdk_20260509/spec.md) | `[x]` | Cross-platform Node SDK (@oasis/wallet-sdk) — client-side tx signing, OASIS API client, DEX adapters |
| [avatar-nft-service](tracks/avatar-nft-service/spec.md) | `[x]` | AvatarNFTService implementation, holon/wallet bindings, composite views, ownership verification |
| [oasis-client](tracks/oasis-client/spec.md) | `[x]` | OasisClient facade — holon querying, avatar OAuth adapter, session management, portfolio aggregation |
| [frontend-demo-harness](tracks/frontend-demo-harness/spec.md) | `[ ]` | shadcn/ui demo harness — full functional test coverage of every OASIS feature |
| [quest-core](tracks/quest-core/spec.md) | `[x]` | Quest DAG domain models — Quest, QuestNode, QuestEdge, QuestDependency, templates, DAG validation |
| [quest-api](tracks/quest-api/spec.md) | `[ ]` | Quest REST API — CRUD, execution orchestration, template management, node dispatch to holon/wallet/STAR managers |
| [dapp-composition](tracks/dapp-composition/spec.md) | `[ ]` | DappSeries — compose quest chains into deployable dApp contracts via STAR generation pipeline |

## Completed This Session

### oasis-wallet-sdk `[x]` Complete

- Package scaffold (tsup, vitest, ESM+CJS+DTS) — 76 tests passing
- ChainProvider interface mirroring .NET IBlockchainProvider
- AlgorandProvider + SolanaProvider with Ed25519 signing (@noble/curves)
- OasisWallet facade (wallet-of-wallets with provider registry)
- OasisApiClient (typed HTTP client matching all .NET controllers — 60+ endpoints)
- Tinyman V2 SDK adapter (dynamic import, atomic tx groups)
- Jupiter Ultra API adapter (MEV-protected, gasless swaps)
- Cross-platform encoding (base64/base58/base32 — pure JS, no btoa/atob/Buffer)
- Platform detection + getRandomBytes for React Native/Lynx
- withRetry utility mirroring .NET ExecuteWithRetryAsync
- API versioning support (api-version.ts with path constants and version routing)
- 3x hot-path code reviews (Opus) — all critical findings fixed
- Bridge DTOs rebuilt to match .NET BridgeTransactionResult exactly (21 fields)

### avatar-nft-service `[x]` Complete

- [x] AvatarNFTService manager (17 methods: CRUD, bindings, composites, verification)
- [x] Registered in Program.cs DI
- [x] [Authorize] added to AvatarNFTController
- [x] Live blockchain balances in WalletManager.GetPortfolioAsync via IBlockchainProviderFactory
- [x] Integration test compilation fixed

### oasis-client `[x]` Complete

- [x] OasisClient facade composing OasisApiClient + OasisWallet
- [x] SessionManager with pluggable storage (localStorage, AsyncStorage, SecureStore)
- [x] HolonQueryBuilder with fluent .where().ownedBy().active().execute() API
- [x] OasisAuthProvider for login/register/getProfile/getAccessToken
- [x] PortfolioAggregator for cross-chain balance views
- [x] Frontend integration: oasis.ts singleton, oasis-auth.tsx context, oasis-hooks.ts
- [x] @oasis/wallet-sdk linked into frontend via local file dependency
- [x] API_SYNC.md regression guide mapping all controllers to SDK methods

### frontend-demo-harness `[ ]` Pending — Long-Run Horizon

**Goal:** shadcn/ui demo harness exercising every capability for functional testing.

**6 phases, ~8-10 days estimated:**
1. Foundation — shadcn/ui init, layout, auth flow
2. Core entities — Avatar, Holon (tree explorer), Wallet (live portfolio)
3. Blockchain ops — NFT lifecycle, DEX swap (Tinyman + Jupiter), Bridge (Wormhole)
4. Search, STAR ODK, Settings
5. Functional test dashboard — automated 38+ test runner with regression detection
6. Polish — responsive, loading states, toasts, keyboard shortcuts

**Test matrix:** 38+ test cases covering every .NET endpoint, SDK method, and chain operation.

See [spec](tracks/frontend-demo-harness/spec.md) and [plan](tracks/frontend-demo-harness/plan.md) for full details.
