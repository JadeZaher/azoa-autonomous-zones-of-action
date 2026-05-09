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
| [oasis-wallet-sdk](tracks/oasis-wallet-sdk_20260509/spec.md) | `[~]` | Cross-platform Node SDK (@oasis/wallet-sdk) — client-side tx signing, OASIS API client, DEX adapters |
| [avatar-nft-service](tracks/avatar-nft-service/spec.md) | `[~]` | AvatarNFTService implementation, holon/wallet bindings, composite views, ownership verification |
| [oasis-client](tracks/oasis-client/spec.md) | `[ ]` | Generic OasisClient for SDK — holon querying, avatar OAuth integration, app account systems |

## Track Details

### oasis-wallet-sdk `[~]` In Progress

**Completed:**
- Package scaffold (tsup, vitest, ESM+CJS+DTS) — 76 tests passing
- ChainProvider interface mirroring .NET IBlockchainProvider
- AlgorandProvider + SolanaProvider (balance, assets, buildTransfer/Mint/Burn, sign, submit)
- OasisWallet facade (wallet-of-wallets with provider registry)
- OasisApiClient (typed HTTP client matching all .NET controllers)
- Tinyman V2 SDK adapter (dynamic import, atomic tx groups)
- Jupiter Ultra API adapter (MEV-protected, gasless swaps)
- Cross-platform encoding (base64/base58/base32 — no btoa/atob/Buffer)
- Platform detection + getRandomBytes for React Native/Lynx
- @noble/curves Ed25519 integration (lazy-loaded, optional peer dep)
- withRetry utility mirroring .NET ExecuteWithRetryAsync
- 3x hot-path code reviews (Opus) — all critical findings fixed

**Remaining:**
- Real msgpack encoding for Algorand native transactions
- Solana native transaction construction (instruction serialization)
- Integration tests against devnet endpoints
- React Native compatibility test suite

### avatar-nft-service `[~]` In Progress

**Tasks:**
- [x] Define IAvatarNFTService interface (19 methods)
- [x] Define IOASISStorageProviderNFTExtensions (12 methods)
- [x] Implement in EfStorageProvider
- [ ] Implement AvatarNFTService manager class
- [ ] Register IAvatarNFTService in Program.cs DI
- [ ] Add [Authorize] to AvatarNFTController
- [ ] Wire live blockchain balances into WalletManager.GetPortfolioAsync
- [ ] Complete InMemoryStorageProvider NFT stub implementations

### oasis-client `[ ]` Pending

**Vision:** A higher-level client built on top of OasisApiClient that provides:
1. **Holon query builder** — fluent API for querying/filtering holons across providers
2. **Avatar OAuth adapter** — use OASIS avatars as an identity provider for external apps
3. **Session management** — JWT handling, auto-refresh, token storage hooks
4. **Entity watchers** — subscribe to holon/wallet/NFT state changes
5. **Cross-chain portfolio** — aggregate balances across all registered wallets/chains

**Tasks:**
- [ ] Design OasisClient class extending OasisApiClient
- [ ] Implement HolonQueryBuilder (fluent filter/sort/paginate)
- [ ] Implement AvatarAuthProvider (OAuth 2.0 compatible adapter)
- [ ] Implement SessionManager (token lifecycle, storage adapters)
- [ ] Implement PortfolioAggregator (cross-chain balance views)
- [ ] Add to SDK exports and tests
