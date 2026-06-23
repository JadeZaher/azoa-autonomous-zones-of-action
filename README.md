# AZOA Sleek

A .NET 8 WebAPI for the AZOA protocol with a TypeScript SDK and Next.js 14 frontend. Single storage engine: SurrealDB.

## Status

**Tests:** 916 unit tests green (2026-06-22). Integration tests run against a persistent podman SurrealDB.

**Latest:** the **user-self-sovereignty** initiative shipped — end users own their own
avatars (wallet-challenge login, Algorand ed25519) and a tenant may act for a user
**only** within a live, revocable `ConsentGrant`. The signing custody seam
(`KeyCustodyService`) is consent-gated and fails closed on every tenant-driven sign.
See `conductor/tracks/{user-sovereign-identity,tenant-consent-delegation}/spec.md`.
A separate security review of the auth + custody surface is still owed before launch.

**Tracks:** see `conductor/tracks.md` for the full feature catalog and status.

## Stack

- **.NET 8 WebAPI** — 15 controllers, 10 managers, dual auth (JWT + X-Api-Key)
- **SurrealDB** — sole data engine via `Azoa.SurrealDb.*` packages; no EF/InMemory in production paths
- **@azoa/wallet-sdk** — TypeScript SDK (vitest, ESM+CJS+DTS via tsup)
- **Next.js 14 frontend** — linked to SDK via `file:../sdk/azoa-wallet`
- **xUnit + FluentAssertions + Moq** — test stack; integration tests via persistent podman SurrealDB

## Repo Layout

- `Controllers/` — 15 HTTP endpoints (ApiKey, Avatar, AvatarNFT, BlockchainOperation, Bridge, DappComposition, DappSeries, Holon, Network, NFT, Quest, Search, STARODK, Swap, Wallet)
- `Managers/` — 10 business logic layers (QuestManager, HolonManager, DappCompositionManager, WalletManager, AvatarManager, NftManager, STARManager, SwapManager, BlockchainOperationManager, SearchManager)
- `Core/` — blockchain providers, base classes, error handling, auth handlers
- `Persistence/SurrealDb/` — SurrealDB store implementations; schema conventions in `CONVENTION.md`
- `Providers/Stores/Surreal/` — per-aggregate store interfaces (`I*Store`) and implementations
- `packages/Azoa.SurrealDb.{Client,Schema,Analyzer}/` — SurrealDB toolkit (C#-first schema authoring, Roslyn analyzer for injection guards)
- `sdk/azoa-wallet/` — TypeScript SDK with AzoaClient facade, ChainProvider + DexAdapter plugin patterns
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
cd sdk/azoa-wallet
npm install
npm test
```

**Frontend:**
```bash
cd frontend
npm install
npm run dev
```

**Full stack (SurrealDB + WebAPI + Frontend):**
```bash
./dev-up.sh          # or ./dev-up.ps1 on Windows
```
This brings up SurrealDB (`rocksdb:///data/db` on `localhost:8000`), the
WebAPI, and the frontend via `docker-compose.dev.yml`, then applies the
schema. Tear down with `./dev-down.sh`. See `DEVELOPMENT.md` for the full
setup, variants (host-run WebAPI, bring-your-own SurrealDB), and
troubleshooting.

See `DEVELOPMENT.md` for detailed setup and `RUNBOOK.md` for operations
(local stack control, production deploy, diagnostics).

## Key Docs

- `PROVIDERS.md` — API surface (15 controllers, 29 endpoints, 84 SDK methods) and provider architecture
- `API_SYNC.md` — Controller ↔ SDK regression mapping; use before shipping controller changes
- `DEVELOPMENT.md` — Developer setup, dev-up variants, conventions, troubleshooting
- `RUNBOOK.md` — Operations: local stack control, production deploy (Railway), diagnostics
- `conductor/tracks.md` — Feature track catalog and status
- `conductor/tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md` — Pre-launch safety gates
- `Persistence/SurrealDb/CONVENTION.md` — Schema naming and design conventions

## Known Open Items

**E1 image pin:** ~~Blocks all integration test runtime evidence~~ — **RESOLVED 2026-06-12.** Swapped storage URI to `rocksdb:///data/db` in `docker-compose.dev.yml` (RocksDB syncs its WAL per commit; G1 durability preserved). A 2.x/3.x bump that restores `surrealkv` default-on is tracked separately as `surrealdb-major-upgrade`.

**Wormhole VAA gate:** `Secp256k1VaaSignatureVerifier` is registered; awaiting confirmation that `api-safety-hardening` is ready for launch.

**SDK/API drift:** W2 audit identified 7 specific gaps (listNfts missing, updateSTARODK, swap/quote, etc.). See `API_SYNC.md` and track `self-audit-one-fix` for details.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
