# Tracks

> **Developer setup lives in [DEVELOPMENT.md](../DEVELOPMENT.md)**;
> **operations live in [RUNBOOK.md](../RUNBOOK.md)** (local stack control,
> production deploy, diagnostics). This file is the per-track catalog:
> shipped tracks collapse into one-liners; in-flight + pending tracks
> retain full context.
>
> Conventions in force: [Persistence/SurrealDb/CONVENTION.md](../Persistence/SurrealDb/CONVENTION.md).

## In flight

### Initiative: workflow-engine (scoped 2026-06-16)

Make OASIS a **durable, consumer-driven workflow engine**: an external app (ArdaNova,
via the TS SDK) DESIGNS a multi-phase process as a quest template and PUSHES an actor
(player/user/tenant) through it phase-by-phase — pseudocode `quest(holonStep1).step(holonStep2B)`.
The DAG suspends between phases (hybrid: consumer-driven `step()` advance OR engine-driven
auto-run that parks at gate/wait nodes until a `signal()` or timer), survives restart, and
runs first-class compensation on cancel. **OASIS ships generic mechanism-only nodes**
(GateCheck/Swap/Transfer/Grant/Hold/Refund/Emit); **all economic semantics stay in ArdaNova**
(swap rates, what a project token is, cancel conditions, vesting math). Worked example to
support generically: platform-token→project-token swap → HOLD until phase-met-or-cancelled →
on-cancel refund / on-continue grant equity → equity pays freelancers or swaps→platform→fiat.
Grounded in the 2026-06-16 review ([REVIEW-economic-substrate-2026-06-16.md](REVIEW-economic-substrate-2026-06-16.md)):
the durable engine builds ON the already-shipped saga layer (`Services/Sagas/*` — `durable-saga-orchestration`
already named "quest step dispatch" as a consumer); the one missing capability is suspend-on-signal.
Ordering: **value-path-wiring first (real value movement)** → durable-workflow-engine →
economic-primitive-nodes → workflow-sdk.

| Track | Status | Description |
|-------|--------|-------------|
| [value-path-wiring](tracks/value-path-wiring/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 0 — value-flow correctness blocker.** Fix the review's Part-A gaps so workflow value-nodes can be real: wire `IKeyCustodyService` into `AlgorandProvider` (C1 — stop signing every op with the platform key); make `AllocationManager` mints actually broadcast + record real `TxHash` (C2); persist the allocation idempotency key for crash recovery (H1); move the KYC gate into `NftManager.MintAsync` so raw `POST /api/nft/mint` is gated too (H3); widen provider amount `int`→`ulong` (H4); preserve txId on confirm-timeout (M1). Deps: signing-core-keystone ✓ + custody ✓ + kyc ✓ + fiat ✓ (all shipped). |
| [durable-workflow-engine](tracks/durable-workflow-engine/spec.md) | `[ ]` | **Tier 0.5 — centerpiece.** Make Quest runs durable + step-addressable + suspendable, built on the saga layer (`Services/Sagas/*`): a run = a saga instance, a node = a step, a gate/wait node = a parked step, refund-on-cancel = a declared compensation step. New `QuestRunStatus` Suspended/AwaitingSignal/AwaitingTimer; advancement API (`advance(runId,nodeId)`, `signal(runId,gateId,payload)`, timer un-park); crash-safe resume. The one saga extension needed: suspend-on-external-signal (a `Parked` status). Soft dep on durable-saga-orchestration (foundation already shipped); hard dep on value-path-wiring for real-value runs. |
| [economic-primitive-nodes](tracks/economic-primitive-nodes/spec.md) | `[ ]` | **Tier 1 — Holon-Transformation Nodes** (dir name legacy). Generic node-handler library under **one SPI + a `RequiresChainCapability` flag**: holonic transformations are the base layer, chain/economic actions are **opt-in**. **Tier 1 (chain-free):** the existing 14 `Holon*` + `ComposeOutputs` transforms, plus new `GateCheck` (real predicate eval, replaces no-op `ConditionNodeHandler` + dead `QuestEdge.Condition`) and `Emit` (hand settlement to the tenant) — a pure-metadata quest runs end-to-end with no wallet. **Tier 2 (`RequiresChainCapability=true`, opt-in):** `Swap` (wraps `ISwapManager`/DEX), `Grant`/`Transfer` (mint/move to the run's actor), `Refund` (first-class compensation) — engine refuses these pre-execution unless a wallet is bound (fails closed). Plus the Holon↔on-chain-asset typed link (review Part B #1) bridging a metadata holon to its chain realization. ZERO economics in handlers. Deps: durable-workflow-engine + value-path-wiring (Tier-2 only). |
| [workflow-sdk](tracks/workflow-sdk/spec.md) | `[ ]` | **Tier 1.** Extend `@oasis/wallet-sdk` (TypeScript) with the consumer surface: template authoring/instantiate methods + a fluent thenable run driver — `quest(id).start({actor,params}).step(nodeId)` / `.signal(gateId,payload)` / `.status()` / `.forActor(childAvatarId)` (lazily acquires a tenant-issued child JWT). Maps 1:1 to the engine endpoints; hybrid drive modes; idempotency-key + assertUuid + amounts-as-strings conventions. No brand leak (generic workflow SDK). Deps: durable-workflow-engine + economic-primitive-nodes + tenant-onboarding ✓. |

### Initiative: ardanova-provider-port (started 2026-06-15)

Make OASIS the custodial blockchain provider ("avatar wallet manager") for ArdaNova
and future apps. Algorand first; generic/cross-chain where feasible. Economic/token
domain (dual-gate, allocation, treasury) stays in ArdaNova — OASIS exposes blockchain
primitives + wallet provisioning. KYC + Stripe folded in (ArdaNova gift). Every
production stub tracked in [DEPLOY-STEPS-TODO.md](DEPLOY-STEPS-TODO.md). Key finding:
**both repos stub signing today** (ArdaNova's `SignTransaction` fakes it too) — OASIS
already has `Algorand2` + `BouncyCastle 2.x` wired, so the keystone is *finish real
signing*, not *port working signing*. Decisions: per-user AES-GCM custody (default),
generic `ITransactionSigner` from day one, simulated `NullBlockchainProvider` for
DB-only mode, six focused tracks. Ordering: **signing-core-keystone first (blocks all)**
→ tenant-onboarding + custody + db-only (parallel) → kyc → fiat.

| Track | Status | Description |
|-------|--------|-------------|
| [signing-core-keystone](tracks/signing-core-keystone/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 0 — keystone.** Real Ed25519 keygen (replace HMAC placeholder in `WalletKeyService.cs:116-129`) + real Algorand signing behind a generic `ITransactionSigner` (replace hard-error stubs in `AlgorandProvider.cs:148-164`). Canonical msgpack via `Algorand2`; no new deps. Blocks the entire initiative. See [spec](tracks/signing-core-keystone/spec.md) + [plan](tracks/signing-core-keystone/plan.md). |
| [custody-key-management](tracks/custody-key-management/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 0.** Per-user decrypt-just-in-time-to-sign custody resolver (`IKeyCustodyService`) with IDOR guard, key rotation/re-wrap, and a platform-signer seam. KMS/HSM is a deploy-stub (B3). `byte[]` key zeroing constraint (P1). Dep: signing-core-keystone. |
| [db-only-null-provider](tracks/db-only-null-provider/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 1.** Simulated `NullBlockchainProvider` via the existing chain factory — deterministic `sim:`-prefixed addresses + tx hashes + a simulated balance ledger; config-selected per `OASIS:BlockchainMode`. "DB, no blockchain" mode for dev/test/demo + no-chain tenants. Distinguishability guardrail (H3). |
| [tenant-onboarding](tracks/tenant-onboarding/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 0.5 — enabling.** Multi-tenant: a tenant Avatar owns a fleet of user Avatars via `OwnerTenantId` FK + `tenant:provision` scope on the existing API-key infra; `ExternalUserId` maps tenant's user→Avatar. Cross-tenant isolation is the security crux (B5). Dep of fiat. |
| [kyc-module](tracks/kyc-module/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 1.** Port ArdaNova's provider-agnostic KYC (Manual default + Veriff stub) as SurrealDB POCOs keyed to AvatarId; `KycManager` → `OASISResult<T>`; reusable `IKycGateService` to gate wallet-generate + mint. No brand leak. Provider secrets are a deploy-stub (P4). |
| [fiat-stripe-bridge](tracks/fiat-stripe-bridge/spec.md) | `[x]` Shipped 2026-06-16 | **Tier 1.** Thin OASIS-side seam: idempotent, KYC-gated, tenant-callable wallet-provision + asset-allocation primitive that ArdaNova calls after Stripe settles. Heavy Stripe/economic lifting STAYS in ArdaNova; OASIS holds no Stripe secrets. Idempotency is a hard requirement (B4). Deps: signing-core + kyc + tenant. |

## Pending

| Track | Status | Description |
|-------|--------|-------------|
| [frontend-demo-harness](tracks/frontend-demo-harness/spec.md) | `[ ]` | shadcn/ui demo harness — full functional test coverage of every OASIS feature. 6 phases, ~8-10 days. Independent of the SurrealDB cutover. See [spec](tracks/frontend-demo-harness/spec.md) + [plan](tracks/frontend-demo-harness/plan.md). |
| [durable-saga-orchestration](tracks/durable-saga-orchestration/spec.md) | `[ ]` | **Tier 1 (architecture)** — reusable hardened durable-saga + transactional-outbox + async-step module; bridge = consumer #1; no broker (homebake); pluggable trigger converges on SurrealDB LIVE queries. Dep: api-safety-hardening ✓, surrealdb-migration ✓. **Both deps now done — track is unblocked.** SagaSteps schema + `ISagaTrigger` / `ISagaStore` foundation already delivered by surrealdb-migration wave-2. |
| [data-backfill-migrations](tracks/data-backfill-migrations/spec.md) | `[ ]` | **Tier 2 — standalone (umbrella framing retired).** First-class data-backfill primitive (`oasis-surreal backfill` CLI + `IBackfill` modules + `data_migration` ledger). F6 FK rewrite is the first real consumer (rewrite FK string columns as `record<table>`). |
| [surrealdb-major-upgrade](tracks/surrealdb-major-upgrade/spec.md) | `[ ]` | **Tier 1 (infrastructure).** Pin is `1.5.4` everywhere; upstream has shipped 2.x and 3.x. Bump server image, `surrealdb.net` SDK, audit SurrealQL surface, revalidate G1 durability gate, sweep docs. 5 phases (DECISION → image → SDK → G1 → docs). Triggered 2026-06-12 by the `surrealkv` feature-flag mismatch in the 1.5.4 image. |

## Shipped

26 tracks complete. One-line summaries — see each spec for detail.

| Track | Summary |
|---|---|
| [dapp-composition](tracks/dapp-composition/spec.md) | **Shipped 2026-06-11 (phase-G).** DappSeries — compose quest chains into deployable dApp contracts via STAR generation. Manager + 2 controllers + 5 validators on source-gen'd POCOs (`DappSeries`/`DappSeriesQuest`). All 10 Acceptance Criteria ticked. 18/18 integration tests green. Closeout also fixed two pre-existing harness bugs (env-name `"Testing"`→`"IntegrationTest"` in test factories; Program.cs Swagger gate broadened to mount in `IntegrationTest` env). |
| [surrealdb-client-package](tracks/surrealdb-client-package/spec.md) | **Shipped 2026-05-24.** Tier 1.5 — homebake `Oasis.SurrealDb.Client` + `.Schema` + `.Analyzer` replacing pre-1.0 `SurrealDb.Net` SDK. Sub-wave 1.5a complete (tag `surrealdb-client-package-1.5a-complete` @ `88f6b26`); 1.5b (WebSocket+LIVE+saga adoption) deferred opportunistically; public publish deferred 3–6mo. Schema SoT pivoted from Mermaid to C#-attributed POCOs on 2026-06-03 — see [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md). |
| [surrealdb-migration](tracks/surrealdb-migration/spec.md) | **Shipped 2026-05-24 (task 9).** Tier 2 — replace EF/Postgres/InMemory with SurrealDB single engine; 7 guardrails (G1–G7) as acceptance. Wave-1 (618/618 unit green): SDK pin, container, integration-test harness rebuild, 7 SCHEMAFULL schemas, query layer, analyzer. Wave-2 quest stores authored. Postgres fallback REMOVED. Tasks 10/11/A10 deferred to post-deploy — see [SIGN-OFF.md](tracks/surrealdb-migration/SIGN-OFF.md). |
| [self-audit-one-fix](tracks/self-audit-one-fix/spec.md) | **Tier 0.5 — pre-launch hygiene. Shipped 2026-06-11.** Closed all 10 audit findings (Buffer→base64Decode in Jupiter; Tinyman decimals + dead-slippage; canonical Algorand msgpack — decision overridden from "remove" to "implement"; settings page chains typing + `getApiUrl()` accessor; native `listNfts()`; `PUT /api/starodk/:id` + typed `updateSTARODK()` — decision overridden from "alias" to dedicated route; `HOLON_COMPOSE` path constant; swap page → typed `getSwapQuote()`/`executeSwap()` + idempotency-key plumbing; `useWallets` → `listWallets()`; AuthWrapper 8-file dead cluster deleted). Also closed a high-sev IDOR on STARODK upsert surfaced by the PUT-route widening (lookup scoped by route id + authenticated avatar; caller-supplied `model.AvatarId` ignored). 36 unit tests cover the IDOR closure; 3 integration tests written but pending separate harness fix (per-test SurrealDB namespace not propagated to WebAPI executor — see follow-up `integration-test-namespace-isolation`). |
| [quest-api](tracks/quest-api/spec.md) | **Phase F (RUNBOOK §5) shipped 2026-06-11.** Quest REST API — 14 new endpoints + 14 new manager methods landed on the post-fork-model runtime (nodes/edges/dependencies sub-resources + `QuestRun` read surface + `MarkRunCompletedAsync`). 30 total endpoints on `QuestController`; 4 obsolete `Quest`-status endpoints (`activate`/`complete`/`fail`/`execution-state`) intentionally reframed onto `QuestRun` per ADR §2.2. Phase E (POCO cutover) now READY but not gating; see [SIGN-OFF.md](tracks/quest-api/SIGN-OFF.md). |
| [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md) | **Tier 1.6 — knowledge capture.** Retro for the C#-first schema pipeline that replaced `surrealdb-schema-source-gen` on 2026-06-03 (Mermaid→POCO deleted; `AttributeSchemaScanner` + `SurqlEmitter` in `Oasis.SurrealDb.Schema` is authoritative). Absorbed into `Persistence/SurrealDb/CONVENTION.md` + RUNBOOK §4/§8 fixes + 6 live doc-comment cleanups + SUPERSEDED banner on the predecessor spec. Shipped 2026-06-11. |
| [core-api](tracks/core-api/spec.md) | Unified provider pattern, base abstractions, `OASISResult` / `OASISResponse` models. |
| [avatar-api](tracks/avatar-api/spec.md) | Avatar controller (register, login, CRUD) — OAuth-like identity + multi-wallet. |
| [holon-api](tracks/holon-api/spec.md) | Holon controller (CRUD, query, cross-provider search, mint, exchange). NFTs as storage-backed holons. |
| [star-api](tracks/star-api/spec.md) | STAR dapp-generator API (scaffold, configure, deploy dapps that operate on holons). |
| [startup-config](tracks/startup-config/spec.md) | `Program.cs` wiring — Swagger, JWT, middleware, manager DI. |
| [tests](tracks/tests/spec.md) | Baseline test suite. Stryker mutation score 59.41%. Suite at 567/567 unit green (2026-06-05 HEAD snapshot; count grows per shipped track). 2026-06-10 audit: 934 .NET tests discoverable across all projects; 146 of them are SkippableFact in IntegrationTests that silently skip when SurrealDB is down. |
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
| [surrealdb-schema-source-gen](tracks/surrealdb-schema-source-gen/spec.md) | **Tier 1.6 — SUPERSEDED 2026-06-03.** Mermaid→POCO pipeline deleted; replaced by C#-first attribute scanner (`AttributeSchemaScanner` + `SurqlEmitter` in `Oasis.SurrealDb.Schema`). 26 POCOs now in `Persistence/SurrealDb/Models/` with `[SurrealTable]` attributes; `AttributePocoByteEquivalenceTests` is the new acceptance gate. `Oasis.SurrealDb.SourceGen` package + test shell removed 2026-06-10. See [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md) for the full as-built reference. |
| [mcp-surface](tracks/mcp-surface/spec.md) | **Tier 3** — read-only MCP surface (5 tools + auth scoping + HNSW vector search) at `/mcp` via ModelContextProtocol.AspNetCore. Closed 2026-05-25 (`295d67c`); write tools deferred. |

## Historical status snapshots (moved from RUNBOOK 2026-06-12)

RUNBOOK.md was restructured into a true operations runbook on 2026-06-12.
Its prior status-snapshot, shipped-retro, forward-sequencing, phased-plan,
and open-questions content (a point-in-time record, not live track status)
was relocated verbatim to
[retros/runbook-status-2026-06-12.md](retros/runbook-status-2026-06-12.md).
This catalog above remains the authoritative source for live track status.
