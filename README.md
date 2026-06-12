# OASIS Sleek

A .NET 8 WebAPI for the OASIS protocol with a TypeScript SDK and Next.js 14 frontend. Single storage engine: SurrealDB.

## Status

**Branch:** `api-safety-hardening`

**Tests:** 567 unit tests green (2026-06-05); 934 .NET tests discoverable (146 are integration tests currently skipped waiting for E1 image pin).

**Tracks:** 20 conductor tracks complete; 2 in-flight; several pending. See `conductor/tracks.md` for full status.

## Stack

- **.NET 8 WebAPI** — 15 controllers, 10 managers, dual auth (JWT + X-Api-Key)
- **SurrealDB** — sole data engine via `Oasis.SurrealDb.*` packages; no EF/InMemory in production paths
- **@oasis/wallet-sdk** — TypeScript SDK (vitest, ESM+CJS+DTS via tsup)
- **Next.js 14 frontend** — linked to SDK via `file:../sdk/oasis-wallet`
- **xUnit + FluentAssertions + Moq** — test stack; integration tests via persistent podman SurrealDB

## Repo Layout

- `Controllers/` — 15 HTTP endpoints (ApiKey, Avatar, AvatarNFT, BlockchainOperation, Bridge, DappComposition, DappSeries, Holon, Network, NFT, Quest, Search, STARODK, Swap, Wallet)
- `Managers/` — 10 business logic layers (QuestManager, HolonManager, DappCompositionManager, WalletManager, AvatarManager, NftManager, STARManager, SwapManager, BlockchainOperationManager, SearchManager)
- `Core/` — blockchain providers, base classes, error handling, auth handlers
- `Persistence/SurrealDb/` — SurrealDB store implementations; schema conventions in `CONVENTION.md`
- `Providers/Stores/Surreal/` — per-aggregate store interfaces (`I*Store`) and implementations
- `packages/Oasis.SurrealDb.{Client,Schema,Analyzer}/` — SurrealDB toolkit (C#-first schema authoring, Roslyn analyzer for injection guards)
- `sdk/oasis-wallet/` — TypeScript SDK with OasisClient facade, ChainProvider + DexAdapter plugin patterns
- `frontend/` — Next.js 14 app with React hooks (`useBalance`, `usePortfolio`, `useHolons`, etc.)
- `conductor/tracks/` — 20+ completed narrative tracks documenting features and architectural decisions
- `tests/` — xUnit test projects (unit + integration)

## Getting Started

**Prerequisites:**
- .NET 8 SDK
- Node 20+
- podman or Docker (for SurrealDB)

**Backend:**
```bash
dotnet restore && dotnet build
```

**SDK:**
```bash
cd sdk/oasis-wallet
npm install
npm test
```

**Frontend:**
```bash
cd frontend
npm install
npm run dev
```

**SurrealDB (local dev):**
```bash
podman run -d --name oasis-surreal -p 5441:8000 \
  surrealdb/surrealdb:v1.5.4 start \
  --auth --user root --pass root memory
```

See `RUNBOOK.md` §1 for detailed setup and image-pin caveats.

## Key Docs

- `PROVIDERS.md` — API surface (15 controllers, 29 endpoints, 84 SDK methods) and provider architecture
- `API_SYNC.md` — Controller ↔ SDK regression mapping; use before shipping controller changes
- `RUNBOOK.md` — Day-to-day workflows, architecture phases, SurrealDB setup
- `conductor/tracks.md` — Feature track catalog and status
- `conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md` — Pre-launch safety gates
- `Persistence/SurrealDb/CONVENTION.md` — Schema naming and design conventions

## Known Open Items

**E1 image pin:** ~~Blocks all integration test runtime evidence~~ — **RESOLVED 2026-06-12.** Swapped storage URI to `rocksdb:///data/db` in `docker-compose.dev.yml` + `podman-compose.yml` (RocksDB syncs its WAL per commit; G1 durability preserved). A 2.x/3.x bump that restores `surrealkv` default-on is tracked separately as `surrealdb-major-upgrade`.

**Wormhole VAA gate:** `Secp256k1VaaSignatureVerifier` is registered; awaiting confirmation that `api-safety-hardening` is ready for launch.

**SDK/API drift:** W2 audit identified 7 specific gaps (listNfts missing, updateSTARODK, swap/quote, etc.). See `API_SYNC.md` and track `self-audit-one-fix` for details.

## License

(TBD)
