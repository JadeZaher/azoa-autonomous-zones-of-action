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
| [dapp-composition](tracks/dapp-composition/spec.md) | `[~]` | DappSeries — compose quest chains into deployable dApp contracts via STAR generation pipeline. **Manager + 2 controllers + 5 validators + STAR pipeline land on source-gen'd POCOs (`DappSeries`/`DappSeriesQuest` from `.mermaid` schemas 210/220).** 12 unit tests green; 535/535 total. Integration tests + Swagger smoke pending convention decision on POCO partial-class extension across all surreal tables. EF migration tasks DEFERRED to surrealdb-migration wave-2. |
| [api-safety-hardening](tracks/api-safety-hardening/spec.md) | `[x]` | **Tier 0** — bridge exactly-once/replay/atomicity, idempotency spine, chain reconciliation, 33 validators, rate limiting, InMemory out of prod DI. **Impl + multi-agent review APPROVE; 537/537 unit green.** Pre-launch gates + ops in [RESIDUAL-RISK-RUNBOOK](tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) §4 (`IVaaSignatureVerifier` — Wormhole value flow fail-closed until done). Harness rebuild → surrealdb-migration |
| [durable-saga-orchestration](tracks/durable-saga-orchestration/spec.md) | `[ ]` | **Tier 1 (architecture)** — reusable hardened durable-saga + transactional-outbox + async-step module (backoff/dead-letter, first-class compensation); bridge = consumer #1; no broker (homebake); pluggable trigger converges on SurrealDB LIVE queries. Dep: api-safety-hardening |
| [architecture-decoupling](tracks/architecture-decoupling/spec.md) | `[x]` | **Tier 1** — per-aggregate persistence seam (8 `I*Store`, god interface + `IQuestRepository` deleted), `IQuestNodeHandler` 34-handler registry (QuestManager ctor 9→3, 315-line switch gone), `ExecutionOrder` dedup, bounded `IMemoryCache`, OpenTelemetry + live `/health`. **Impl + independent review APPROVE-WITH-SIMPLIFICATIONS; 532/532 unit green, `scripts/passoff.ps1` exit 0 (api-safety regression intact), `scripts/passoff-architecture.ps1` GREEN, live `/health` 200.** Precondition for SurrealDB satisfied |
| [quest-temporal-fork-model](tracks/quest-temporal-fork-model/spec.md) | `[x]` | **Tier 1** — definition/runtime split for quests: `QuestRun` + `QuestNodeExecution` separated from `Quest`/`QuestNode`; `ForkAsync(runId, atNodeId, reason)` produces lineage-tracked fork with parent → `Forked` + in-flight cancelled; intra-iteration validator unchanged. **COMPLETE** (commits `80c6233` + `6dfbe13` + `63b232b` co-landed with surrealdb-client-package sub-wave 1.5a). Hand-off `SURREAL-SCHEMA-HINTS.md` consumed verbatim by surrealdb-migration tasks 3/9/10. 7 fork/lineage tests green |
| [surrealdb-client-package](tracks/surrealdb-client-package/spec.md) | `[~]` | **Tier 1.5** — homebake `Oasis.SurrealDb.Client` (HTTP + WS+LIVE) + `.Schema` (Mermaid source-of-truth + generator + migration runner CLI) + `.Analyzer` (relocated SRDB0001) replacing pre-1.0 `SurrealDb.Net` SDK + archived `surrealdb-migrations` tool. **Sub-wave 1.5a COMPLETE** (tag `surrealdb-client-package-1.5a-complete` at `88f6b26`; 803 tests across 4 suites; pass-off 11/11; code-review HIGH×7 closed in `c611d6a`). Sub-wave 1.5b (WebSocket+LIVE+saga adoption) DEFERRED — opportunistic. Public publish deferred 3–6mo. Deps: architecture-decoupling, api-safety-hardening. **Now unblocks** surrealdb-migration wave-2 |
| [surrealdb-schema-source-gen](tracks/surrealdb-schema-source-gen/spec.md) | `[x]` | **Tier 1.6** — Roslyn `IIncrementalGenerator` deriving C# POCOs + typed `SurrealQuery<T>` + typed `RecordId<T>` from `.mermaid` schema sources. Eliminates manual schema↔POCO drift; promotes field-name/type errors from runtime SurrealQL parse failures to compile-time errors. **COMPLETE** (tag `surrealdb-schema-source-gen-complete`; 889+ tests across 5 suites; new `Oasis.SurrealDb.SourceGen` 4th package; generator emits 7 POCOs into `OASIS.WebAPI.Generated.SurrealDb` namespace; pass-off section 12 drift gate green). Hand-written legacy `Models/{Wallet,BlockchainOperation,ConsumedVaaRecord,Idempotency/IdempotencyRecord}.cs` intentionally retained pending wave-2 surrealdb-migration adapter rewires (see commit 3 deviation note). Deps: surrealdb-client-package sub-wave 1.5a (DONE). **Independent of** sub-wave 1.5b |
| [surrealdb-migration](tracks/surrealdb-migration/spec.md) | `[~]` | **Tier 2** — replace EF/Postgres/InMemory with SurrealDB single engine behind the seam; 7 guardrails (G1–G7) as acceptance; graph remodel. **Wave-1 complete** (618/618 unit, pass-off green): SDK pin, container, integration-test harness rebuild, 7 value-table SCHEMAFULL schemas, query layer, analyzer. **Wave-2 adapters now gated on [[surrealdb-client-package]] sub-wave 1.5a;** LIVE-saga adoption on sub-wave 1.5b. Quest-table schema (task 3 quest portion, tasks 9–10) consumes `SURREAL-SCHEMA-HINTS.md` from quest-temporal-fork-model. Postgres fallback REMOVED. Deps: architecture-decoupling, api-safety-hardening, surrealdb-client-package (wave-2), quest-temporal-fork-model (quest schema only) |
| [mcp-surface](tracks/mcp-surface/spec.md) | `[ ]` | **Tier 3** — MCP server over SurrealDB graph (quest/holon traversal, NFT graph, HNSW vector). Dep: surrealdb-migration |

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
