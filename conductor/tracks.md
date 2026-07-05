# Tracks

> **Developer setup lives in [DEVELOPMENT.md](../DEVELOPMENT.md)**;
> **operations live in [RUNBOOK.md](../RUNBOOK.md)** (local stack control,
> production deploy, diagnostics). This file is the per-track catalog:
> shipped tracks collapse into one-liners; in-flight + pending tracks
> retain full context.
>
> Conventions in force: [Persistence/SurrealDb/CONVENTION.md](../Persistence/SurrealDb/CONVENTION.md).

## In flight

> **Restructured 2026-07-05.** All prior in-flight/pending tracks were consolidated
> into a single final engineering track (**final-hardening-cutover**) and archived.
> That track absorbs every remaining code gap so that after it ships the only work
> left is `railway up` + secret/guardian-set provisioning. Operator/deploy tasks
> (formerly `DEPLOY-STEPS-TODO.md`) now live in the operator guide
> [docs/NODE-HOST.md](../docs/NODE-HOST.md) §8. See the track spec for the full
> phase map and what each archived track contributed.

| Track | Status | Description |
|-------|--------|-------------|
| [final-hardening-cutover](tracks/final-hardening-cutover/spec.md) | `[ ]` Active (created 2026-07-05) | **The last engineering track before launch.** Absorbs all remaining implementation across 9 formerly-active tracks + the code items in the retired `DEPLOY-STEPS-TODO.md`. Phases: **A** correctness blockers (durable-quests-inert `Sagas:Enabled=false` fix — see [[durable-quests-inert-sagas-disabled]]; warning drift 28→53); **B** real cross-chain value primitives (real Solana signer, real bridge lock/burn/verify replacing logged-only + always-true stubs, bridge exactly-once close-out, reconcile-before-retry quest wiring, custody `byte[]`/rotation); **C** quest value-engine F2–F7 (OnFailure, Emit webhooks, QuestDependency gating, HolonType registry, unpublish TOCTOU, SDK/builder mirrors); **D** flagship economic flow (fractionalization `Bridge`/`Back` nodes + STAR-ODK ecosystem tree); **E** data/query hygiene (raw-SELECT sweep, backfill primitive, SurrealDB 3.x close-out); **F** saga operator surface (bridge-as-consumer dropped — hardened directly); **G** doc + bookkeeping close-out. **Terminal state: only `railway up` + secrets/guardian-sets remain — zero code.** Absorbs: quest-value-engine-expressiveness, bridge-safety-hardening, project-asset-fractionalization, star-odk-ecosystem-tree, durable-saga-orchestration, data-backfill-migrations, surreal-linq-adoption-sweep, surrealdb-major-upgrade, blockchain-recovery-and-portable-wallets. |

## Archived without absorption

- **user-sovereign-identity** and **tenant-consent-delegation** — code-complete and
  their once-"owed" security review was done + remediated in commit `10e5dad`
  (2026-06-22); archived as **shipped** (not absorbed). See
  [[consent-gate-architecture]].
- **dotnet-client-sdk** — remains **TABLED** (user decision 2026-06-18); a C# client
  is a post-launch convenience, not a launch blocker. Archived tabled, not done.
- **frontend-demo-harness** — effectively built (all 16 dashboard pages + a
  test-runner page exist); a light audit folded into final-hardening-cutover §G5.

## Shipped

28 tracks complete. One-line summaries — see each spec for detail.

| Track | Summary |
|---|---|
| [quest-dag-semantic-hardening](tracks/quest-dag-semantic-hardening/spec.md) | **Shipped 2026-07-02 (Tier 0.5 — pre-launch semantic safety, from the 2026-07-01 two-round quest/economic-gates review; two Opus reviews: APPROVE-WITH-FIXES, all fixes landed).** P0 fixed: skip now CASCADES through Control chains (a failed GateCheck stops the whole payout chain, not one hop) + Conditional edges skip regardless of condition text (empty-Condition now rejected at both input surfaces). Draft→Active **publish gate** (definition `Status` reintroduced — deliberate reversal of quest-temporal-fork-model's removal; publish runs DAG+transition+fan-out+config stack; execute requires Active; mutations rejected on Active; unpublish blocked with in-flight runs). Fan-out = publish/workflow-start ERROR (note: FR-2 makes the legacy warning-only path API-unreachable — recorded spec tension). Shared `QuestNodeConfig.TryDeserialize` replaced all 39 handler `Deserialize<T>(...)!` sites + `QuestNodeType`→DTO registry with exhaustiveness pin + strict round-trip at node add/update/publish (Emit runtime-permissive by design). `ExtractBoundHolonIds` now collects Guid arrays; holon parent-cycle guard on all `ParentHolonId` writes (`CloneAsync` provably safe, documented). Builder: publish UI, edge inspector (EdgeType+Condition), error-level blocking warnings; SDK `publishQuest`/`unpublishQuest`. E2e: 12 new integration tests incl. the ArdaNova flow (unfunded holon → gate fails closed + 2-node cascade skip → metadata FUNDED → passes → Emit readable; Tier-2 on simulated provider). 1027 unit + 228 integration green (37 pre-existing failures unchanged); zero new warnings. Known limits (documented): run-start/unpublish TOCTOU; durable-path skip divergence (follow-up `durable-skip-propagation`). Follow-ups: output-binding, `OnFailure` edge, Emit webhooks, QuestDependency enforcement, metadata schema registry. |
| [fungible-token-node](tracks/fungible-token-node/spec.md) | **Shipped 2026-06-21. Keystone for asset fractionalization (initiative: ardanova-rails).** Exposed the already-implemented Algorand fungible ASA creation (`IAlgorandASAModule.CreateASAAsync`, real total/decimals) through a manager seam + Tier-2 `FungibleTokenCreate` quest node — previously nothing above the provider could reach it (no node/manager/controller; mint was supply-1 NFT only). New: `FungibleTokenCreate` enum + `FungibleTokenCreateNodeConfig`; `IFungibleTokenManager`/`FungibleTokenManager` (KYC-gated + idempotent + provision-if-absent, mirrors AllocationManager discipline; total>0/decimals 0..19 validated pre-broadcast; `ulong Total`→`int` guarded by overflow check); `FungibleTokenCreateNodeHandler` (Tier-2, actor from run ctx, `{runId}:{nodeId}` idempotency seed, opt-in Holon↔asset link); DI in Program.cs; builder palette entry (Economic, `requiresChain`). AllocationManager's supply-1 mint path untouched (consolidation deferred). `dotnet build` 0 errors / zero new warnings vs 28-baseline; 3 new FungibleTokenManager xUnit tests green. |
| [quest-visual-builder](tracks/quest-visual-builder/spec.md) | **Shipped 2026-06-20.** Frontend-only — replaced the Quest DAG page's JSON-textarea authoring + numbered-text "DAG view" with a React Flow (`@xyflow/react` v12, **only new dep**) drag-and-drop builder. New `frontend/src/components/quest-builder/` package: `node-catalog.ts` (mirrors `QuestNodeType` 1:1, 9 color categories, `requiresChain` Tier-2 flag) + custom node + dependency-free longest-path auto-layout + read-only `dag-flow` + interactive `quest-canvas` (palette of built-ins **+ API node templates** with `tpl` badge, click/drag-add, drag-to-connect, inspector w/ live-validated JSON config) + node-template creator. Page rewired to 4 working tabs (My Quests→graph / Builder / Quest Templates browse+create / Node Templates browse+create); serializes to the **existing** `{nodes, edges}` index-ref contract — zero backend changes. `tsc --noEmit` clean on new files (scoped per no-frontend-typecheck). See [RETRO.md](tracks/quest-visual-builder/RETRO.md). Follow-ups: edit-in-place, schema-driven config forms, **asset fractionalization primitive (backend gap)**. |
| [dapp-composition](tracks/dapp-composition/spec.md) | **Shipped 2026-06-11 (phase-G).** DappSeries — compose quest chains into deployable dApp contracts via STAR generation. Manager + 2 controllers + 5 validators on source-gen'd POCOs (`DappSeries`/`DappSeriesQuest`). All 10 Acceptance Criteria ticked. 18/18 integration tests green. Closeout also fixed two pre-existing harness bugs (env-name `"Testing"`→`"IntegrationTest"` in test factories; Program.cs Swagger gate broadened to mount in `IntegrationTest` env). |
| [surrealdb-client-package](tracks/surrealdb-client-package/spec.md) | **Shipped 2026-05-24.** Tier 1.5 — homebake `Azoa.SurrealDb.Client` + `.Schema` + `.Analyzer` replacing pre-1.0 `SurrealDb.Net` SDK. Sub-wave 1.5a complete (tag `surrealdb-client-package-1.5a-complete` @ `88f6b26`); 1.5b (WebSocket+LIVE+saga adoption) deferred opportunistically; public publish deferred 3–6mo. Schema SoT pivoted from Mermaid to C#-attributed POCOs on 2026-06-03 — see [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md). |
| [surrealdb-migration](tracks/surrealdb-migration/spec.md) | **Shipped 2026-05-24 (task 9).** Tier 2 — replace EF/Postgres/InMemory with SurrealDB single engine; 7 guardrails (G1–G7) as acceptance. Wave-1 (618/618 unit green): SDK pin, container, integration-test harness rebuild, 7 SCHEMAFULL schemas, query layer, analyzer. Wave-2 quest stores authored. Postgres fallback REMOVED. Tasks 10/11/A10 deferred to post-deploy — see [SIGN-OFF.md](tracks/surrealdb-migration/SIGN-OFF.md). |
| [self-audit-one-fix](tracks/self-audit-one-fix/spec.md) | **Tier 0.5 — pre-launch hygiene. Shipped 2026-06-11.** Closed all 10 audit findings (Buffer→base64Decode in Jupiter; Tinyman decimals + dead-slippage; canonical Algorand msgpack — decision overridden from "remove" to "implement"; settings page chains typing + `getApiUrl()` accessor; native `listNfts()`; `PUT /api/starodk/:id` + typed `updateSTARODK()` — decision overridden from "alias" to dedicated route; `HOLON_COMPOSE` path constant; swap page → typed `getSwapQuote()`/`executeSwap()` + idempotency-key plumbing; `useWallets` → `listWallets()`; AuthWrapper 8-file dead cluster deleted). Also closed a high-sev IDOR on STARODK upsert surfaced by the PUT-route widening (lookup scoped by route id + authenticated avatar; caller-supplied `model.AvatarId` ignored). 36 unit tests cover the IDOR closure; 3 integration tests written but pending separate harness fix (per-test SurrealDB namespace not propagated to WebAPI executor — see follow-up `integration-test-namespace-isolation`). |
| [quest-api](tracks/quest-api/spec.md) | **Phase F (RUNBOOK §5) shipped 2026-06-11.** Quest REST API — 14 new endpoints + 14 new manager methods landed on the post-fork-model runtime (nodes/edges/dependencies sub-resources + `QuestRun` read surface + `MarkRunCompletedAsync`). 30 total endpoints on `QuestController`; 4 obsolete `Quest`-status endpoints (`activate`/`complete`/`fail`/`execution-state`) intentionally reframed onto `QuestRun` per ADR §2.2. Phase E (POCO cutover) now READY but not gating; see [SIGN-OFF.md](tracks/quest-api/SIGN-OFF.md). |
| [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md) | **Tier 1.6 — knowledge capture.** Retro for the C#-first schema pipeline that replaced `surrealdb-schema-source-gen` on 2026-06-03 (Mermaid→POCO deleted; `AttributeSchemaScanner` + `SurqlEmitter` in `Azoa.SurrealDb.Schema` is authoritative). Absorbed into `Persistence/SurrealDb/CONVENTION.md` + RUNBOOK §4/§8 fixes + 6 live doc-comment cleanups + SUPERSEDED banner on the predecessor spec. Shipped 2026-06-11. |
| [core-api](tracks/core-api/spec.md) | Unified provider pattern, base abstractions, `AZOAResult` / `AZOAResponse` models. |
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
| [azoa-wallet-sdk](tracks/azoa-wallet-sdk_20260509/spec.md) | Cross-platform Node SDK (`@azoa/wallet-sdk`) — client-side tx signing, AZOA API client, DEX adapters. 76+ tests. |
| [avatar-nft-service](tracks/avatar-nft-service/spec.md) | AvatarNFTService manager (17 methods), live blockchain balances in `WalletManager.GetPortfolioAsync`. |
| [azoa-client](tracks/azoa-client/spec.md) | `AzoaClient` facade — holon querying, avatar OAuth adapter, session management, portfolio aggregation. |
| [quest-core](tracks/quest-core/spec.md) | Quest DAG domain models — Quest, QuestNode, QuestEdge, QuestDependency, templates, DAG validation. |
| [api-safety-hardening](tracks/api-safety-hardening/spec.md) | **Tier 0** — bridge exactly-once/replay/atomicity, idempotency spine, chain reconciliation, 33 validators, rate limiting. Multi-agent review APPROVE. Pre-launch gates in [RESIDUAL-RISK-RUNBOOK §4](tracks/api-safety-hardening/RESIDUAL-RISK-RUNBOOK.md) (`IVaaSignatureVerifier` — Wormhole value flow fail-closed until done). |
| [architecture-decoupling](tracks/architecture-decoupling/spec.md) | **Tier 1** — per-aggregate `I*Store` seam, `IQuestNodeHandler` 34-handler registry (QuestManager ctor 9→3, 315-line switch gone), bounded `IMemoryCache`, OpenTelemetry + live `/health`. APPROVE-WITH-SIMPLIFICATIONS. Precondition for SurrealDB satisfied. |
| [quest-temporal-fork-model](tracks/quest-temporal-fork-model/spec.md) | **Tier 1** — definition/runtime split: `QuestRun` + `QuestNodeExecution` separated from `Quest`/`QuestNode`; `ForkAsync(runId, atNodeId, reason)` produces lineage-tracked fork. Hand-off [`SURREAL-SCHEMA-HINTS.md`](tracks/quest-temporal-fork-model/SURREAL-SCHEMA-HINTS.md) consumed by surrealdb-migration tasks 3/9/10. 7 fork/lineage tests green. |
| [surrealdb-schema-source-gen](tracks/surrealdb-schema-source-gen/spec.md) | **Tier 1.6 — SUPERSEDED 2026-06-03.** Mermaid→POCO pipeline deleted; replaced by C#-first attribute scanner (`AttributeSchemaScanner` + `SurqlEmitter` in `Azoa.SurrealDb.Schema`). 26 POCOs now in `Persistence/SurrealDb/Models/` with `[SurrealTable]` attributes; `AttributePocoByteEquivalenceTests` is the new acceptance gate. `Azoa.SurrealDb.SourceGen` package + test shell removed 2026-06-10. See [surreal-schema-package-retro](tracks/surreal-schema-package-retro/spec.md) for the full as-built reference. |
| [mcp-surface](tracks/mcp-surface/spec.md) | **Tier 3** — read-only MCP surface (5 tools + auth scoping + HNSW vector search) at `/mcp` via ModelContextProtocol.AspNetCore. Closed 2026-05-25 (`295d67c`); write tools deferred. |

## Historical status snapshots (moved from RUNBOOK 2026-06-12)

RUNBOOK.md was restructured into a true operations runbook on 2026-06-12.
Its prior status-snapshot, shipped-retro, forward-sequencing, phased-plan,
and open-questions content (a point-in-time record, not live track status)
was relocated verbatim to
[retros/runbook-status-2026-06-12.md](retros/runbook-status-2026-06-12.md).
This catalog above remains the authoritative source for live track status.
