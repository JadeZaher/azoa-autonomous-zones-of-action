# Economic-Primitive Nodes — Specification

## Goal

Tier 1 (the generic, mechanism-only node library). Ship a small library of
**generic quest node handlers** that a consumer (ArdaNova) composes into
durable economic workflows. Each node is a new `QuestNodeType` + an
`IQuestNodeHandler` + one DI line, following the existing handler shape
exactly: `JsonSerializer.Deserialize<TConfig>(context.Node.Config)` → call one
OASIS manager / primitive → serialize the result → `Ok`/`Fail`
(`Services/Quest/Handlers/NftMintNodeHandler.cs:19-26`).

The **hard constraint** is the whole point of the track: **OASIS ships
mechanism only**. Every node MOVES value by *tenant-supplied params*; **none**
contains economic logic — no pricing, no accounting, no token semantics, no
"project" concept, no "equity" type, no vesting math, no cancel conditions.
ArdaNova defines *what* a project token is, the swap *rates*, the cancel
*conditions*; OASIS only *executes the mechanism* the tenant parameterizes.

The worked example this node SET must support **generically** (note: OASIS
never names any of these economic concepts — they are all tenant `asset_type`
values + `metadata` + tenant params):

> platform-token → project-token **swap** → **HOLD** until (phase-met |
> cancelled) → on-cancel **refund** platform tokens → on-continue **grant**
> equity → equity used to pay freelancers **OR** swapped → platform → fiat.

Mapped to generic primitives, with **zero** economic vocabulary in OASIS:

| Worked-example step (ArdaNova words) | Generic OASIS node (mechanism only) |
| --- | --- |
| platform-token → project-token swap | **SwapNode** (wraps `ISwapManager`; rate from the DEX) |
| HOLD until phase-met or cancelled | **GateCheckNode** predicate + the engine's suspend (Track 2) |
| on-cancel refund platform tokens | **RefundNode** (saga compensation = transfer-back) |
| on-continue grant equity | **GrantNode** (mint-to-actor an ASA/holon) |
| pay freelancers / swap → fiat | **EmitNode** (post a typed output; tenant settles) |

## Background — the substrate already exists (file:line evidence)

The handler SPI + registry + execution context are exactly the "tenant supplies
economic parameters into a node" shape the review's Part C calls for.

### Handler SPI and registry (the seam every node plugs into)
- `IQuestNodeHandler` — `QuestNodeType NodeType { get; }` +
  `Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext, ct)`
  (`Interfaces/Quest/IQuestNodeHandler.cs:18-32`).
- `QuestNodeHandlerRegistry` builds the `QuestNodeType → IQuestNodeHandler`
  map from DI and **throws on a duplicate type**
  (`Services/Quest/QuestNodeHandlerRegistry.cs:16-30`) — the exactly-one-per-type
  invariant the house rules require.
- `QuestNodeType` enum (~30 members) — adding a kind = enum member + handler +
  DI line (`Models/Quest/QuestEnums.cs:19-70`).
- Result helpers: `QuestNodeResults.Ok(json)` / `.Fail(msg)`
  (`Services/Quest/QuestNodeResults.cs:19-28`); shared
  `QuestNodeJson.Options` (case-insensitive) for every (de)serialize
  (`Services/Quest/QuestNodeJson.cs:12-15`).

### Every handler is the same shape (the template to copy)
- Value handler, manager-backed: deserialize → `await _manager.Method(...)` →
  serialize `r` → `Fail(r.Message)` on error else `Ok(outputJson)`
  (`Services/Quest/Handlers/NftMintNodeHandler.cs:19-26`,
  `NftTransferNodeHandler.cs:18-25`,
  `WalletGetPortfolioNodeHandler.cs:18-25`).
- The actor identity is **always taken from the run context, never the body**:
  handlers pass `context.Quest.AvatarId` into the manager
  (`NftMintNodeHandler.cs:22`, `NftTransferNodeHandler.cs:21`).
- Per-node config DTOs are plain POCOs in `Models/Quest/NodeConfigs.cs`
  (e.g. `NftTransferNodeConfig` `:47-51`, `IdConfig` `:12-15`) — the new nodes
  add their own config POCOs here.

### Data-flow between nodes (what a gate reads, what a swap feeds)
- `QuestNodeExecutionContext.UpstreamExecutions` — already-completed
  predecessor executions keyed by source node id
  (`Models/Quest/QuestNodeExecutionContext.cs:56-60`).
- `ComposeOutputsNodeHandler` already gathers upstream outputs by reading
  `context.UpstreamExecutions[...].Output` (
  `Services/Quest/Handlers/ComposeOutputsNodeHandler.cs:27-41`) — the exact
  read a GateCheck predicate composes with, with no `IQuestNodeExecutionStore`
  dependency.
- `WalletGetPortfolioNodeHandler` reads balances
  (`WalletGetPortfolioNodeHandler.cs:18-25`) — the **balance read a
  balance-gate composes with** (a GateCheck does not read chain directly; it
  reads an upstream portfolio node's output, keeping the gate mechanism-only).

### The gate gap (what GateCheck fixes)
- `QuestEdge.Condition` is a `string?` that is **stored and round-tripped but
  never parsed/evaluated** (`Models/Quest/QuestEdge.cs:16`). It is used only as
  a *presence flag* for failed-predecessor skipping
  (`Managers/QuestManager.cs:266`).
- `ConditionNodeHandler` is a **no-op pass-through** — it returns
  `context.Node.Config` verbatim and evaluates nothing
  (`Services/Quest/Handlers/ConditionNodeHandler.cs:15-21`). The comment
  claiming "edge conditions handle branching" is the dead path: edges never
  evaluate.
- The engine **already** skips a node whose predecessor `Failed` on a Control
  edge (`QuestManager.cs:275-279`) and on a Conditional edge
  (`QuestManager.cs:266-273`). So a node that returns **Fail** gates its
  downstream **for free** — GateCheck only has to compute the *predicate*; the
  engine does the gating.

### Value movement already at the manager layer (nodes wrap, never reimplement)
- Swap exists: `ISwapManager.GetQuoteAsync` / `GetSwapTransactionAsync`
  (`Interfaces/Managers/ISwapManager.cs:8,22`), backed by `SwapManager`
  (`Managers/SwapManager.cs:44,87`) + `IDexAdapter` (Tinyman/Jupiter). A
  SwapNode **wraps** this; it does not reimplement DEX logic. **Rate/quote come
  from the DEX, never from OASIS** (`ISwapManager.cs:11-21` documents the
  unsigned-tx, client-signs flow).
- NFT/ASA value path: `INftManager.MintAsync` / `TransferAsync` / `BurnAsync`
  all `(…, Guid avatarId, …)` (`Interfaces/Managers/INftManager.cs:10-12`).
  Grant = mint-to-actor; Transfer = move-to-actor; both carry the run's
  `avatarId`.
- Soulbound mint primitive exists: `AlgorandProvider.CreateSoulboundAsaAsync`
  (`Providers/Blockchain/Algorand/AlgorandProvider.cs:506-508`) — returns the
  asset id but **persists it nowhere** (review Part B #1). The clawback-revoke
  primitive is **deferred** (D4 → H2, `conductor/DEPLOY-STEPS-TODO.md:224-227`).

### The Holon↔asset link gap (Part B #1, this track delivers it)
- `Holon.token_id` / `Holon.chain_id` columns already exist and are indexed
  via `(provider_name, chain_id)` (`Persistence/SurrealDb/Models/Holon.cs:60-61,
  68-69, 22`) but are **not populated from a mint result** today.
- `OperationLog` already carries `source_holon_id` / `target_holon_id` typed
  `record<holon>` links (`Persistence/SurrealDb/Models/OperationLog.cs:90-99`)
  and an opaque `Parameters` bag (`:72-75`) — but **no typed
  `asset_id` / `tx_hash` / `holon_id` column**. The only Holon↔op link today is
  the stringly-typed `BlockchainOperation.Parameters["holonId"]` (review
  Part B #1). This track adds the typed columns by copying the existing
  `source_holon_id` `record<holon>` pattern.

## Scope — the generic node handler library

Each item below is a new `QuestNodeType` + handler in `Services/Quest/Handlers/`
+ one DI line + (where needed) a config POCO in `Models/Quest/NodeConfigs.cs`.
**Every handler deserializes tenant-supplied `Config`; NONE contains
economics.**

### 1. GateCheckNode (the load-bearing node for "hold until phase-met")
Evaluates a **tenant-supplied predicate** against upstream outputs / an injected
read (balance, KYC status, external-signal presence) and returns **Pass** or
**Fail**. A `Fail` gates everything downstream via the engine's existing
failed-predecessor-skip on Control edges (`QuestManager.cs:275-279`).

- **Replaces** the no-op `ConditionNodeHandler`
  (`Services/Quest/Handlers/ConditionNodeHandler.cs`) and gives the dead
  `QuestEdge.Condition` string (`QuestEdge.cs:16`) a real evaluator at the
  *node* (not the edge).
- The predicate language **must be generic + safe**: a small **whitelisted
  expression evaluator** over the JSON of upstream outputs + injected reads —
  **NOT arbitrary code** (no `eval`, no reflection, no method calls, no I/O from
  the expression). Operators limited to comparison (`==`, `!=`, `<`, `<=`, `>`,
  `>=`), boolean (`&&`, `||`, `!`), and field-path lookup into upstream JSON.
  The exact grammar + evaluator choice is a **decision recorded in `plan.md`**.
- Inputs are mechanism-only: the predicate references *upstream node outputs by
  name* (the `ComposeOutputs` read, `ComposeOutputsNodeHandler.cs:35-38`) and
  injected reads (a portfolio-node output for balance; a KYC-status read; an
  external-signal node's presence). **The threshold value, the KYC level
  required, the "phase X met" meaning are tenant params — OASIS only compares.**

### 2. SwapNode
Wraps `ISwapManager.GetSwapTransactionAsync(request, idempotencyKey)`
(`ISwapManager.cs:22`) parameterized by tenant `Config` (in/out asset, amount,
minOut, slippage). **Mechanism only** — the rate/quote come from the DEX
adapter (`SwapManager.cs:44,87`), never from OASIS. Idempotency key plumbed
through from the run context as the existing swap path expects
(`ISwapManager.cs:14-21`).

### 3. TransferNode / GrantNode
Move/mint an ASA or holon to an actor by tenant params.
- **TransferNode** wraps `INftManager.TransferAsync(nftId, request, avatarId)`
  (`INftManager.cs:11`) — copy `NftTransferNodeHandler`
  (`NftTransferNodeHandler.cs:18-25`).
- **GrantNode** = mint-to-actor: wraps `INftManager.MintAsync(request, avatarId)`
  (`INftManager.cs:10`) — copy `NftMintNodeHandler`
  (`NftMintNodeHandler.cs:19-26`). The granted thing is whatever tenant
  `asset_type` the request names (e.g. ArdaNova's "equity"); OASIS sees an ASA.
- **Both carry the actor `avatarId` from `context.Quest.AvatarId`, never the
  body** (`NftMintNodeHandler.cs:22`).
- Both DEPEND on the value-path-wiring track (Track 1) for real broadcast — see
  Dependencies.

### 4. Hold (decision: dedicated node vs. composition — recommend in plan.md)
The user's "hold until phase-met or cancelled". The **HOLD itself is the
durable-workflow-engine's suspend machinery** (Track 2 parks the run); this
track does **not** build suspend/resume. The recommendation in `plan.md`:

- **Recommended:** Hold is **not** a dedicated node. "Hold until phase-met or
  cancelled" = a **GateCheckNode that the engine suspends on** (Track 2 parks
  the run until the gate's referenced signal/read is satisfiable) composed with
  a **RefundNode declared as the on-cancel compensation**. This keeps the node
  library minimal and puts the suspend semantics squarely in Track 2 where they
  belong. `plan.md` records this as the chosen design and notes the Track-2
  contract a suspending GateCheck needs (a "needs-input / re-evaluate-later"
  result signal).
- The alternative (a dedicated `HoldNode` type) is documented and rejected in
  `plan.md` unless Track 2's contract forces a distinct parking node.

### 5. RefundNode / CompensateNode (first-class compensation)
The compensation step that **reverses a prior Transfer/Grant/Swap**, declared as
the durable-workflow-engine saga compensation (Track 2 owns *when* it runs;
this track owns *the mechanism*). Mechanism = transfer-back / clawback:

- Transfer-back of a fungible/ASA via `INftManager.TransferAsync` (reverse
  direction, actor from run context).
- **Soulbound clawback is deferred** (H2 / signing D4,
  `DEPLOY-STEPS-TODO.md:224-227`): RefundNode **notes the dependency** and fails
  closed with a clear message when asked to reverse a soulbound asset until the
  clawback primitive lands. `plan.md` records this gap explicitly.

### 6. EmitNode / CallbackNode (keep fiat/equity-payout economics in ArdaNova)
Posts a **typed output** to the consumer (ArdaNova) — for "pay freelancers" or
"swap → fiat" steps where the **actual settlement happens on the tenant side**.
OASIS emits the signal/output; the tenant acts on it. This is the seam that
keeps fiat + equity-payout economics out of OASIS entirely. Mechanism: serialize
a tenant-shaped output payload (from `Config` + upstream outputs) as the node's
`QuestNodeExecution.Output`, optionally posting to a tenant-registered callback
sink. **No settlement, no fiat rails, no payout math in OASIS.** The callback
sink contract (in-band output only vs. an outbound webhook) is a **decision in
`plan.md`** (recommend: output-only first; webhook deferred to the SDK track).

### 7. Holon↔asset link primitive (review Part B #1)
The typed link so a minted/granted economic-object Holon records its **real
on-chain asset id + tx hash**:
- Populate `Holon.token_id` / `Holon.chain_id` (`Holon.cs:60-61, 68-69`) from
  the mint/grant result so a GrantNode/GrantMint ties the Holon to a real ASA.
- Add typed `asset_id` / `tx_hash` / `holon_id` columns to `OperationLog`
  (`Persistence/SurrealDb/Models/OperationLog.cs`) by copying the existing
  `source_holon_id` `record<holon>` link pattern (`OperationLog.cs:90-94`).
  Schema regen required (G6 SCHEMAFULL) — same note as the source_holon_id link.
- This is the **one schema change** in the track. It is mechanism (a typed
  link), not economics.

### 8. (Optional, decide in plan.md) Queryable metadata (review Part B #2)
`HolonQueryRequest` + `SurrealHolonStore.QueryAsync` filter only typed columns
(no `WHERE metadata.x = y`, review Part B #2). If a GateCheck needs a
server-side "all allocations where `metadata.status=vested`", scope a
metadata-key filter clause; **else defer and note**. Recommendation in `plan.md`:
**defer** — a GateCheck reads an upstream node's output (which the tenant shaped
by fetching `asset_type` + filtering client-side), so server-side metadata
filtering is not on this track's critical path.

## Acceptance criteria

- [ ] New `QuestNodeType` members added (final list in `plan.md` / summary),
      each with exactly one handler in `Services/Quest/Handlers/` and one DI
      registration; `QuestNodeHandlerRegistry` startup still throws on any
      duplicate (`QuestNodeHandlerRegistry.cs:21-27` invariant holds).
- [ ] **GateCheckNode** evaluates a tenant-supplied predicate over upstream JSON
      outputs + injected reads (balance via upstream portfolio output, KYC
      status, external-signal presence) and returns Pass/Fail; a Fail causes the
      engine's existing failed-predecessor-skip (`QuestManager.cs:275-279`) to
      gate downstream. `ConditionNodeHandler` no-op is removed/superseded.
- [ ] **Predicate evaluator is safe**: a test asserts the evaluator rejects /
      cannot execute arbitrary code (no `eval`, reflection, method calls, or
      I/O); only the whitelisted operator/field-path grammar evaluates.
- [ ] **SwapNode** wraps `ISwapManager` by tenant params; a test asserts the
      handler calls `GetSwapTransactionAsync` with the deserialized params and
      does NOT compute a rate (rate comes from the mocked DEX/manager).
- [ ] **TransferNode / GrantNode** call `INftManager.Transfer/Mint` with the
      actor `avatarId` taken from `context.Quest.AvatarId` (a test asserts the
      body-supplied avatar is ignored — mirrors the STARODK IDOR precedent).
- [ ] **RefundNode** performs transfer-back via `INftManager`; for a soulbound
      asset it fails closed with a clear "clawback primitive deferred (H2)"
      message (a test asserts this).
- [ ] **EmitNode** serializes a tenant-shaped output to
      `QuestNodeExecution.Output`; a test asserts no settlement/fiat/payout
      computation occurs (pure pass-through of tenant params + upstream output).
- [ ] **Holon↔asset link**: a mint/grant result populates `Holon.token_id` /
      `chain_id`; `OperationLog` gains typed `asset_id` / `tx_hash` / `holon_id`
      columns (record<holon> for the holon link); schema regen passes; a test
      asserts the link round-trips.
- [ ] **Mechanism-only tests** (the house-rule heart): for each new handler, a
      test asserts it performs **no economic computation** — it only
      deserializes params, calls a manager/primitive, and serializes; no
      pricing, accounting, token-semantics, or vesting math in the handler.
- [ ] **Composed-DAG determinism**: with mocked managers, the DAGs
      `swap → gate → grant` and `gate → refund-on-fail` run **deterministically**
      (same inputs ⇒ same outputs/skips), exercising the gate's Pass and Fail
      branches.
- [ ] **No brand leak**: a test / grep asserts no `ArdaNova`, `project-token`,
      `equity`, `vesting`, `project`, or any tenant-economic string appears in
      `Services/Quest/Handlers/*` or the new config POCOs.
- [ ] New `QuestNodeType` values **persist** (round-trip through the quest store)
      and the registry one-per-type invariant holds at startup.
- [ ] `dotnet build` passes with **zero warnings** (nullable enabled) per
      `conductor/workflow.md:18`.
- [ ] `dotnet test` green; SurrealDB remains the sole storage engine.
- [ ] Commits follow `[economic-primitive-nodes] <verb> <subject>`.
- [ ] `tracks.md` row moves to `[x]` Shipped.

## Out of scope (explicit non-goals — guard against scope creep)

- **The suspend/resume/signal engine machinery** — owned by the
  **durable-workflow-engine** track (Track 2). This track's nodes **run on**
  that engine; they do not build it. A suspending GateCheck only declares the
  "re-evaluate-later" intent; Track 2 parks the run.
- **The value-path broadcast / custody fixes** (C1/C2/H3, review Part A) —
  owned by the **value-path-wiring** track (Track 1). Swap/Transfer/Grant
  **depend on** real signing + broadcast landing there; until then they wrap the
  managers as-is (and inherit the simulated/real provider seam).
- **The SDK** — owned by the **workflow-sdk** track. No SDK methods here.
- **ALL economic semantics** — stay in ArdaNova: swap *rates*, *what a project
  token is*, *cancel conditions*, *vesting math*, payout/fiat logic. **Do NOT
  build a "project token" or "equity" type** — those are tenant `asset_type`
  values + `metadata` (review Part B: economic object = Holon + asset_type +
  metadata, zero schema change).
- **Soulbound clawback primitive** (H2 / signing D4) — deferred; RefundNode
  notes the dependency and fails closed for soulbound reversal.
- **Server-side queryable-metadata** (Part B #2) — deferred unless `plan.md`
  scopes it; recommended deferred.

## Tier

**Tier 1** — the generic node library that makes Quest the economic-workflow
*substrate*. It does not itself gate real value flow (Track 1 does) but is the
composition layer every tenant economic workflow is built from.

## Dependencies

- **durable-workflow-engine** (Track 2) — the suspend/resume host this track's
  nodes RUN on. GateCheck-as-hold needs its "re-evaluate-later" contract;
  RefundNode needs its saga-compensation hook.
- **value-path-wiring** (Track 1) — real value movement (C1 custody wiring, C2
  real broadcast, H3 KYC choke point, review Part A). Swap/Transfer/Grant
  DEPEND on real signing landing here.
- **quest-core** ✓ — the handler SPI + registry + execution context
  (`IQuestNodeHandler.cs`, `QuestNodeHandlerRegistry.cs`,
  `QuestNodeExecutionContext.cs`) already shipped.
- **signing-core-keystone** ✓ — the generic signer seam; soulbound mint
  primitive shipped (`AlgorandProvider.cs:506`), clawback deferred (H2).
