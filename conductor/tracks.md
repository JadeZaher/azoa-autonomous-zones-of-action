# Tracks

> **Day-to-day work lives in [RUNBOOK.md](../RUNBOOK.md)** (current phase,
> in-flight coordination, sequencing). This file is the per-track
> catalog: shipped tracks collapse into one-liners; in-flight + pending
> tracks retain full context.
>
> Conventions in force: [Persistence/SurrealDb/CONVENTION.md](../Persistence/SurrealDb/CONVENTION.md).

## In flight

| Track | Status | Description |
|-------|--------|-------------|
| [dapp-composition](tracks/dapp-composition/spec.md) | `[~]` | DappSeries — compose quest chains into deployable dApp contracts via STAR generation pipeline. **Manager + 2 controllers + 5 validators + STAR pipeline land on source-gen'd POCOs (`DappSeries`/`DappSeriesQuest` from `.mermaid` schemas 210/220). Partial-class accessors added (`d318bcb`).** 12 unit tests green; 535/535 total. Integration tests + Swagger smoke pending Quest cutover (RUNBOOK §6 phase G). EF migration tasks DEFERRED to surrealdb-migration wave-2. |
| [surrealdb-client-package](tracks/surrealdb-client-package/spec.md) | `[x]` | **Closed 2026-05-24** (sub-wave 1.5a complete; 1.5b deferred opportunistically per yesterday actual state). **Tier 1.5** — homebake `Oasis.SurrealDb.Client` + `.Schema` + `.Analyzer` replacing pre-1.0 `SurrealDb.Net` SDK. **Sub-wave 1.5a COMPLETE** (tag `surrealdb-client-package-1.5a-complete` @ `88f6b26`; 803 tests; pass-off 11/11). Sub-wave 1.5b (WebSocket+LIVE+saga adoption) DEFERRED — opportunistic. Public publish deferred 3–6mo. **Now unblocks** surrealdb-migration wave-2. |
| [surrealdb-migration](tracks/surrealdb-migration/spec.md) | `[x]` | **Task 9 closed 2026-05-24** (quest graph schema + 3 Surreal stores; tasks 10/11/A10 deferred to post-deploy review). **Tier 2** — replace EF/Postgres/InMemory with SurrealDB single engine; 7 guardrails (G1–G7) as acceptance. **Wave-1 complete** (618/618 unit, pass-off green): SDK pin, container, integration-test harness rebuild, 7 value-table SCHEMAFULL schemas, query layer, analyzer. **Wave-2 in flight** (parallel /ultrapilot session): 3 SurrealQuest stores authored (1595 lines), 230_quest_graph_edges RELATE schemas, .surql files emitted for 080–200. Wave-2 stores consume hand-written `Models.Quest.*`; Quest cutover to generated POCOs is the next slice (RUNBOOK §6 phase E). Postgres fallback REMOVED. |

## Pending

| Track | Status | Description |
|-------|--------|-------------|
| [quest-api](tracks/quest-api/spec.md) | `[ ]` | Quest REST API — CRUD, execution orchestration, template management, node dispatch to holon/wallet/STAR managers. **~70% built** via the architecture-decoupling + quest-temporal-fork-model tracks (manager 755 lines, controller 184 lines, 34/34 handlers). **18 endpoints + 12 manager methods missing** per audit. Best landed after Quest cutover (RUNBOOK §6 phase F) so new endpoints sit on the post-cutover surface. |
| [frontend-demo-harness](tracks/frontend-demo-harness/spec.md) | `[ ]` | shadcn/ui demo harness — full functional test coverage of every OASIS feature. 6 phases, ~8-10 days. Independent of the SurrealDB cutover. See [spec](tracks/frontend-demo-harness/spec.md) + [plan](tracks/frontend-demo-harness/plan.md). |
| [durable-saga-orchestration](tracks/durable-saga-orchestration/spec.md) | `[ ]` | **Tier 1 (architecture)** — reusable hardened durable-saga + transactional-outbox + async-step module; bridge = consumer #1; no broker (homebake); pluggable trigger converges on SurrealDB LIVE queries. Dep: api-safety-hardening (done), surrealdb-migration. |
| [mcp-surface](tracks/mcp-surface/spec.md) | `[x]` | **Closed 2026-05-25** -- read-only MCP surface complete (5 tools + auth scoping + HNSW vector search). Write tools deferred to a future round. **Tier 3** — MCP server over SurrealDB graph (quest/holon traversal, NFT graph, HNSW vector). Dep: surrealdb-migration. |

## Shipped

19 tracks complete. One-line summaries — see each spec for detail.

| Track | Summary |
|---|---|
| [core-api](tracks/core-api/spec.md) | Unified provider pattern, base abstractions, `OASISResult` / `OASISResponse` models. |
| [avatar-api](tracks/avatar-api/spec.md) | Avatar controller (register, login, CRUD) — OAuth-like identity + multi-wallet. |
| [holon-api](tracks/holon-api/spec.md) | Holon controller (CRUD, query, cross-provider search, mint, exchange). NFTs as storage-backed holons. |
| [star-api](tracks/star-api/spec.md) | STAR dapp-generator API (scaffold, configure, deploy dapps that operate on holons). |
| [startup-config](tracks/startup-config/spec.md) | `Program.cs` wiring — Swagger, JWT, middleware, manager DI. |
| [tests](tracks/tests/spec.md) | Baseline test suite. Stryker mutation score 59.41%. Suite now at 535/535 unit green. |
| [wallet-api](tracks/wallet-api/spec.md) | First-class Wallet API — CRUD, portfolio analytics, default-wallet management. |
| [nft-api](tracks/nft-api/spec.md) | Semantic NFT layer (mint, transfer, burn, metadata) on Holon infrastructure. |
| [search-api](tracks/search-api/spec.md) | Unified cross-entity search with pagination, filtering, faceted results. |
| [providers-and-cross-chain-bridge](tracks/providers-and-cross-chain-bridge/spec.md) | Algorand + Solana providers via REST/RPC, `BlockchainProviderFactory`, trusted + Wormhole cross-chain bridge. |
| [validation-mapping](tracks/validation-mapping/spec.md) | FluentValidation input pipeline + AutoMapper entity-DTO mapping layer. |
| [oasis-wallet-sdk](tracks/oasis-wallet-sdk_20260509/spec.md) | Cross-platform Node SDK (`@oasis/wallet-sdk`) — client-side tx signing, OASIS API client, DEX adapters. 76+ tests. |
| [avatar-nft-service](tracks/avatar-nft-service/spec.md) | AvatarNFTService manager (17 methods), live blockchain balances in `WalletManager.GetPortfolioAsync`. |
| [oasis-client](tracks/oasis-client/spec.md) | `OasisClient` facade — holon querying, avatar OAuth adapter, session management, portfolio aggregation. |
| [quest-core](tracks/quest-core/spec.md) | Quest DAG domain models — Quest, QuestNode, QuestEdge, QuestDependency, templates, DAG validation. |
| [api-safety-hardening](tracks/api-safety-hardening/spec.md) | **Tier 0** — bridge exactly-once/replay/atomicity, idempotency spine, chain reconciliation, 33 validators, rate limiting. Multi-agent review APPROVE. Pre-launch gates in [RESIDUAL-RISK-RUNBOOK §4](tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) (`IVaaSignatureVerifier` — Wormhole value flow fail-closed until done). |
| [architecture-decoupling](tracks/architecture-decoupling/spec.md) | **Tier 1** — per-aggregate `I*Store` seam, `IQuestNodeHandler` 34-handler registry (QuestManager ctor 9→3, 315-line switch gone), bounded `IMemoryCache`, OpenTelemetry + live `/health`. APPROVE-WITH-SIMPLIFICATIONS. Precondition for SurrealDB satisfied. |
| [quest-temporal-fork-model](tracks/quest-temporal-fork-model/spec.md) | **Tier 1** — definition/runtime split: `QuestRun` + `QuestNodeExecution` separated from `Quest`/`QuestNode`; `ForkAsync(runId, atNodeId, reason)` produces lineage-tracked fork. Hand-off [`SURREAL-SCHEMA-HINTS.md`](tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md) consumed by surrealdb-migration tasks 3/9/10. 7 fork/lineage tests green. |
| [surrealdb-schema-source-gen](tracks/surrealdb-schema-source-gen/spec.md) | **Tier 1.6** — Roslyn `IIncrementalGenerator` deriving C# POCOs + typed `SurrealQuery<T>` + `RecordId<T>` from `.mermaid` schemas. Eliminates schema↔POCO drift. 889+ tests. Now extended to emit 8 additional POCOs for quest + dapp-composition aggregates (`92ede75`). Generator updates for relationship parsing + FK emission planned — see [RUNBOOK §4.3](../RUNBOOK.md). |
