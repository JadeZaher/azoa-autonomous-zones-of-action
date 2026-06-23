# Holon-Transformation Nodes — Specification

> **Directory-name note (legacy):** this track lives at
> `conductor/tracks/economic-primitive-nodes/` and keeps that path + commit
> prefix `[economic-primitive-nodes]` to preserve the committed `tracks.md`
> link and git history. The name is **legacy** from an earlier, too-narrow
> "economic primitives" framing. The corrected framing — and the real title —
> is **Holon-Transformation Nodes**.

## Goal

Ship a small, coherent library of **generic quest node handlers** under **one
generic node-handler SPI**, where the base abstraction is the **holonic
transformation**, and blockchain/economic actions are an **opt-in capability**
layered on top — not the center.

The framing inverts the old spec:

- **Base layer = holonic transformations.** A quest is a DAG of operations on
  holons (create, transform, compose, link, branch, gate, emit). A holon may be
  **pure metadata** or carry chain data. Most of these nodes touch nothing but
  Holon records + the holon graph + metadata.
- **Opt-in layer = chain/economic actions.** Mint, swap, transfer, grant,
  refund are the **subset** of nodes that *tack on-chain effects onto a holon*.
  They are optional. A holon can flow through the whole quest as pure metadata
  and never touch a wallet, an ASA, or any chain.

The mechanism is one generic SPI plus a **capability flag**: each node declares
whether it requires a chain capability. The engine performs a **pre-execution
capability check** and **refuses** to run a chain-requiring node unless the run
has a wallet bound. **Holon transforms and chain actions are the same shape;
the distinction is a capability check, NOT a type hierarchy** (see D1).

Each node is still a `QuestNodeType` + an `IQuestNodeHandler` + one DI line,
following the existing handler shape exactly:
`JsonSerializer.Deserialize<TConfig>(context.Node.Config)` → call one AZOA
manager / primitive → serialize the result → `Ok`/`Fail`
(`Services/Quest/Handlers/NftMintNodeHandler.cs:19-26`).

### The "chain optional" key point (make this explicit)

The capability flag is exactly what lets **"chain data and actions be tacked on
or unused in quest design."** The same holon, same quest, chain optional:

1. A holon is created and flows through **Tier-1** transforms as **pure
   metadata** (`HolonCreate → HolonInteract → GateCheck → Emit`) — no wallet,
   no ASA, no chain call.
2. A **Tier-2** node *optionally* realizes that holon on-chain
   (`Grant`/`MintNode` writes `token_id` / `chain_id` via the Part-B-#1 typed
   link).
3. Downstream **Tier-1** transforms keep operating on the now-chain-backed
   holon's metadata, unchanged.

A **pure-metadata quest** = every node `RequiresChainCapability == false`; it
never binds a wallet. A **chain quest** binds a wallet, unlocking the Tier-2
nodes via the capability check.

### The mechanism-only constraint still holds (for the Tier-2 subset)

The chain-action subset ships **mechanism only**. Every Tier-2 node MOVES value
by *tenant-supplied params*; **none** contains economic logic — no pricing, no
accounting, no token semantics, no "project" concept, no "equity" type, no
vesting math, no cancel conditions. The consumer (ArdaNova) defines *what* a
project token is, the swap *rates*, the cancel *conditions*; AZOA only
*executes the mechanism* the tenant parameterizes. Tier-1 transforms are
likewise pure mechanism: they shape holons + metadata; they encode no domain
semantics.

The worked example this node set must support **generically** (AZOA never
names any of these economic concepts — they are all tenant `asset_type` values
+ `metadata` + tenant params):

> platform-token → project-token **swap** → **HOLD** until (phase-met |
> cancelled) → on-cancel **refund** platform tokens → on-continue **grant**
> equity → equity used to pay freelancers **OR** swapped → platform → fiat.

Mapped to generic nodes, with **zero** economic vocabulary in AZOA:

| Worked-example step (ArdaNova words) | Generic AZOA node | Tier |
| --- | --- | --- |
| holon lifecycle / metadata shaping | **HolonCreate/Update/Interact/Compose/…** (exist) | Tier 1 |
| HOLD until phase-met or cancelled | **GateCheckNode** predicate + the engine's suspend (Track 2) | Tier 1 |
| pay freelancers / swap → fiat (hand-off) | **EmitNode** (post a typed output; tenant settles) | Tier 1 |
| platform-token → project-token swap | **SwapNode** (wraps `ISwapManager`; rate from the DEX) | Tier 2 |
| on-continue grant equity | **GrantNode** / **MintNode** (mint-to-actor an ASA) | Tier 2 |
| move an ASA to an actor | **TransferNode** (`INftManager.TransferAsync`) | Tier 2 |
| on-cancel refund platform tokens | **RefundNode** (saga compensation = transfer-back) | Tier 2 |

## Background — the substrate already exists (file:line evidence)

The handler SPI + registry + execution context are exactly the "operate on a
holon by tenant-supplied params" shape this track needs. **Much of Tier 1
already exists** — the track's Tier-1 job is mostly audit + fill two genuine
gaps (GateCheck, Emit).

### Handler SPI and registry (the seam every node plugs into)
- `IQuestNodeHandler` — `QuestNodeType NodeType { get; }` +
  `Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext, ct)`
  (`Interfaces/Quest/IQuestNodeHandler.cs:18-32`). **This is the SPI the
  capability flag extends** (see D1 + "The SPI change" below).
- `QuestNodeHandlerRegistry` builds the `QuestNodeType → IQuestNodeHandler`
  map from DI and **throws on a duplicate type**
  (`Services/Quest/QuestNodeHandlerRegistry.cs:16-30`) — the exactly-one-per-type
  invariant the house rules require.
- `QuestNodeType` enum (~32 members) — adding a kind = enum member + handler +
  DI line (`Models/Quest/QuestEnums.cs:19-70`).
- Result helpers: `QuestNodeResults.Ok(json)` / `.Fail(msg)`; shared
  `QuestNodeJson.Options` for every (de)serialize.

### The handler dispatch seam (where the capability check inserts)
- `QuestManager` dispatches a node by `_registry.TryGet(node.NodeType, out var
  handler)` then `await handler.HandleAsync(ctx)`
  (`Managers/QuestManager.cs:328-337`). **The pre-execution capability check
  inserts exactly here** — between `TryGet` and `HandleAsync` — and returns a
  `Fail(...)` if `handler.RequiresChainCapability` is true and the run has no
  wallet bound. No new dispatch loop; one guard at the existing seam.

### The actor / wallet binding on a run
- The actor is `Quest.AvatarId` (`Models/Quest/Quest.cs:19`) carried onto the
  run as `QuestRun.AvatarId` (`Models/Quest/QuestRun.cs:31`). Handlers already
  take the actor from `context.Quest.AvatarId`, never the body
  (`NftMintNodeHandler.cs:22`).
- There is **no explicit wallet-id field on the run today** — "has a wallet
  bound" is resolved by checking the actor avatar has a usable/default wallet
  (`WalletManager` / `KeyCustodyService.PlatformWalletId`
  `Managers/KeyCustodyService.cs:54`). The capability check resolves the bound
  wallet from the run's actor; **whether to add an explicit
  `QuestRun.BoundWalletId` is a decision (D2).**

### Tier-1 holon surface — ALREADY EXISTS (audit, don't rebuild)
`HolonManager` is the full holonic-transformation surface
(`Managers/HolonManager.cs`):
- CRUD + query: `CreateAsync:32`, `UpdateAsync:51`, `DeleteAsync:78`,
  `QueryAsync:89`, `GetAsync:22`.
- Graph edits: `InteractAsync:95` — the graph-edit Swiss-army (reparent via
  `NewParentHolonId:104`, add/remove peers `:107-116`, set/remove metadata
  `:118-128`).
- Traversal: `GetChildrenAsync:134`, `GetPeersAsync:140`,
  `GetAncestorsAsync:154`, `GetDescendantsAsync:179`.
- Holonic ops: `PropagateAsync:209` (BFS write down a subtree),
  `ComposeAsync:265` (subtree rollup view), `CloneAsync:321` (deep subtree
  copy), `MoveSubtreeAsync:393` (cycle-guarded reparent of a subtree).

Each already has a node handler (`Services/Quest/Handlers/Holon*NodeHandler.cs`)
and a `QuestNodeType` member (`QuestEnums.cs:21-35`:
`HolonCreate/Update/Delete/Get/Query/Interact/GetChildren/GetPeers/GetAncestors/
GetDescendants/Propagate/Compose/Clone/MoveSubtree`). **These are the Tier-1
transformation set and are all `RequiresChainCapability == false`** — they touch
only Holon records + metadata + the graph; none calls a chain. The track
**confirms** this and adds the genuinely-missing generic transforms below.

### Data-flow between nodes (what a gate reads, what a transform feeds)
- `QuestNodeExecutionContext.UpstreamExecutions` — already-completed predecessor
  executions keyed by source node id
  (`Models/Quest/QuestNodeExecutionContext.cs:56-60`).
- `ComposeOutputsNodeHandler` already gathers upstream outputs by reading
  `context.UpstreamExecutions[...].Output`
  (`Services/Quest/Handlers/ComposeOutputsNodeHandler.cs:27-41`) — the exact
  read a GateCheck predicate composes with, with no `IQuestNodeExecutionStore`
  dependency.
- `WalletGetPortfolioNodeHandler` reads balances
  (`WalletGetPortfolioNodeHandler.cs:18-25`) — the **balance read a balance-gate
  composes with** (a GateCheck does not read chain directly; it reads an
  upstream portfolio node's output, keeping the gate mechanism-only and
  `RequiresChainCapability == false`).

### The gate gap (what GateCheck fixes) — the load-bearing Tier-1 add
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

### Value movement at the manager layer (Tier-2 nodes wrap, never reimplement)
- Swap exists: `ISwapManager.GetQuoteAsync` / `GetSwapTransactionAsync`
  (`Interfaces/Managers/ISwapManager.cs:8,22`), backed by `SwapManager`
  (`Managers/SwapManager.cs:44,87`) + `IDexAdapter` (Tinyman/Jupiter). SwapNode
  **wraps** this. **Rate/quote come from the DEX, never from AZOA**
  (`ISwapManager.cs:11-21`).
- NFT/ASA value path: `INftManager.MintAsync` / `TransferAsync` / `BurnAsync`
  all `(…, Guid avatarId, …)` (`Interfaces/Managers/INftManager.cs:10-12`).
  Grant/Mint = mint-to-actor; Transfer = move-to-actor; both carry the run's
  `avatarId`.
- Soulbound mint primitive exists: `AlgorandProvider.CreateSoulboundAsaAsync`
  (`Providers/Blockchain/Algorand/AlgorandProvider.cs:506-508`) — returns the
  asset id but **persists it nowhere** (review Part B #1). The clawback-revoke
  primitive is **deferred** (D7 → H2, `conductor/DEPLOY-STEPS-TODO.md:224-227`).

### The Holon↔asset link gap (Part B #1 — the bridge between Tier-1 and Tier-2)
This is the **one schema change** and is what ties a Tier-1 holon to its
Tier-2 chain realization.
- `Holon.token_id` / `Holon.chain_id` columns already exist and are indexed
  via `(provider_name, chain_id)` (`Persistence/SurrealDb/Models/Holon.cs:60-61,
  68-69, 22`) but are **not populated from a mint result** today.
- `OperationLog` already carries `source_holon_id` / `target_holon_id` typed
  `record<holon>` links (`Persistence/SurrealDb/Models/OperationLog.cs:90-99`)
  and an opaque `Parameters` bag (`:72-75`) — but **no typed
  `asset_id` / `tx_hash` / `holon_id` column**. This track adds the typed
  columns by copying the existing `source_holon_id` `record<holon>` pattern.

## The SPI change (the locked D1 mechanism)

One generic SPI, extended with a single capability flag:

```csharp
public interface IQuestNodeHandler
{
    QuestNodeType NodeType { get; }

    /// True if this node requires a chain capability (a wallet bound to the
    /// run). The engine refuses to run such a node pre-execution when no
    /// wallet/capability is bound. Default false: holon transforms are pure
    /// metadata and need no chain. Only the Tier-2 chain-action subset
    /// overrides this to true.
    bool RequiresChainCapability => false;   // default-implemented; opt-in override

    Task<QuestNodeHandlerResult> HandleAsync(QuestNodeExecutionContext context,
                                             CancellationToken ct = default);
}
```

- **Default-implemented to `false`** so all ~32 existing handlers (every Tier-1
  holon/query/control node) compile unchanged and remain pure-metadata.
- The Tier-2 chain handlers override `=> true`. (A marker attribute is the
  documented alternative — see D1; the property is the recommendation.)
- **The engine gate** lives at the existing dispatch seam
  (`QuestManager.cs:328-337`): after `_registry.TryGet`, before
  `HandleAsync`, if `handler.RequiresChainCapability` and the run has no wallet
  bound → `result = QuestNodeResults.Fail("chain capability required: no wallet
  bound to run")` and skip the handler. Fails **closed**.

This is the whole inversion in one flag: **transforms and chain actions are the
same SPI shape; only the capability check differs.**

## Scope — two tiers under one generic SPI

### Tier 1 — Holonic transformation nodes (`RequiresChainCapability == false`, ZERO chain dependency)

Operate purely on Holon records + metadata + the holon graph. A quest using
only these needs **no wallet, no ASA, no blockchain at all**.

**(a) Audit / confirm the existing set is coherent and chain-free.**
The 14 `Holon*` handlers + `ComposeOutputs` already form the transformation
surface (mapped to `HolonManager` above). The track confirms each is
`RequiresChainCapability == false` and that together they cover the generic
create / read / graph-edit / traverse / propagate / compose / clone / move
shape. No rebuild; this is an audit + (where a handler is thin or missing a
generic transform) a fill.

**(b) Add the genuinely-missing generic transforms:**

1. **GateCheckNode** (the load-bearing gate). Evaluates a **tenant-supplied
   predicate** against upstream outputs / injected reads and returns **Pass**
   or **Fail**; a `Fail` gates everything downstream via the engine's existing
   failed-predecessor-skip (`QuestManager.cs:275-279`).
   - **Replaces** the no-op `ConditionNodeHandler` and gives the dead
     `QuestEdge.Condition` string a real evaluator at the *node* (not the edge).
   - The predicate language **must be generic + safe**: a **whitelisted
     expression evaluator** over the JSON of upstream outputs + injected reads —
     **NOT arbitrary code** (no `eval`, reflection, method calls, or I/O from
     the expression). Operators limited to comparison (`== != < <= > >=`),
     boolean (`&& || !`), and field-path lookup. Exact grammar + evaluator
     choice recorded in `plan.md` (D3).
   - Inputs are mechanism-only: references *upstream node outputs by name* (the
     `ComposeOutputs` read) and injected reads. **The threshold value, the KYC
     level, the "phase X met" meaning are tenant params — AZOA only compares.**
     `RequiresChainCapability == false` (reads upstream JSON, not chain).

2. **EmitNode** (hand settlement back to the tenant). Posts a **typed output**
   to the consumer (ArdaNova) — for "pay freelancers" / "swap → fiat" steps
   where the **actual settlement happens on the tenant side**. Serializes a
   tenant-shaped output payload (from `Config` + upstream outputs) as the
   node's `QuestNodeExecution.Output`. **No settlement, no fiat rails, no payout
   math in AZOA.** Pure metadata; `RequiresChainCapability == false`. Callback
   sink contract (in-band output vs. outbound webhook) is a decision in
   `plan.md` (D6 → output-only first; webhook deferred).

3. **Branch / Transform** (only if the existing set lacks generic workflow
   shaping). If `HolonInteract` + `GateCheck` + `ComposeOutputs` already cover
   metadata transform + branching, **no new node is added** — recorded as a
   decision in `plan.md` (D8). If a genuine generic gap exists (e.g. a pure
   value-shaping `TransformNode` distinct from a holon edit), add it here as
   Tier-1, `RequiresChainCapability == false`.

**(c) Confirm all Tier-1 nodes are `RequiresChainCapability == false`.** A test
asserts this for the whole Tier-1 set (the pure-metadata invariant).

### Tier 2 — Chain-action nodes (`RequiresChainCapability == true`, opt-in)

Attach blockchain settlement to a holon. Each carries the actor `avatarId` from
the run context, **DEPENDS on value-path-wiring (Track 1)** for real broadcast,
and **requires a wallet bound** (the capability check rejects it otherwise).

1. **MintNode / GrantNode** — mint-to-actor an ASA. Wraps
   `INftManager.MintAsync(request, avatarId)` (`INftManager.cs:10`); copy
   `NftMintNodeHandler` (`NftMintNodeHandler.cs:19-26`). The granted thing is
   whatever tenant `asset_type` the request names (e.g. ArdaNova "equity");
   AZOA sees an ASA. On success, populate `Holon.token_id` / `chain_id` from
   the mint result (the Part-B-#1 link). Actor from `context.Quest.AvatarId`.

2. **TransferNode** — move an ASA to an actor. Wraps
   `INftManager.TransferAsync(nftId, request, avatarId)` (`INftManager.cs:11`);
   copy `NftTransferNodeHandler` (`NftTransferNodeHandler.cs:18-25`). Actor from
   run context.

3. **SwapNode** — wraps `ISwapManager.GetSwapTransactionAsync(request,
   idempotencyKey)` (`ISwapManager.cs:22`) by tenant `Config` (in/out asset,
   amount, minOut, slippage). **Mechanism only** — rate/quote from the DEX
   adapter (`SwapManager.cs:44,87`), never AZOA. Idempotency key plumbed from
   the run context.

4. **RefundNode** — first-class compensation that **reverses a prior
   Transfer/Grant/Swap**, declared as the durable-workflow-engine saga
   compensation (Track 2 owns *when* it runs; this track owns *the mechanism*).
   Mechanism = transfer-back via `INftManager.TransferAsync` (reverse
   direction, actor from run context). **Soulbound clawback is deferred**
   (H2 / signing D7, `DEPLOY-STEPS-TODO.md:224-227`): RefundNode **fails closed**
   with a clear "clawback primitive deferred (H2)" message when asked to reverse
   a soulbound asset.

### The Holon↔asset typed link (Part B #1 — the Tier-1/Tier-2 bridge)
The typed link so a minted/granted holon records its **real on-chain asset id +
tx hash** — the seam between a pure-metadata holon and its chain realization:
- Populate `Holon.token_id` / `Holon.chain_id` (`Holon.cs:60-61, 68-69`) from
  the mint/grant result so a Grant/Mint ties the Holon to a real ASA.
- Add typed `asset_id` / `tx_hash` / `holon_id` columns to `OperationLog`
  (`OperationLog.cs`) by copying the existing `source_holon_id` `record<holon>`
  pattern (`OperationLog.cs:90-94`). Schema regen required (G6 SCHEMAFULL).
- This is the **one schema change** in the track. It is mechanism (a typed
  link), not economics.

### Hold (decision: composition, not a node — recommend in plan.md)
"Hold until phase-met or cancelled" is **not** a dedicated node. The HOLD itself
is the durable-workflow-engine's suspend machinery (Track 2 parks the run).
Recommendation (D5): Hold = a **GateCheckNode the Track-2 engine suspends on**
(parks the run until the referenced signal/read satisfies the predicate)
**composed with** a **RefundNode declared as the on-cancel compensation**. This
keeps the node library minimal and puts suspend semantics in Track 2.

### (Optional, decide in plan.md) Queryable metadata (review Part B #2 — DEFER)
`HolonQueryRequest` + `SurrealHolonStore.QueryAsync` filter only typed columns
(no `WHERE metadata.x = y`, review Part B #2). A GateCheck reads an upstream
node's output (tenant shaped it client-side), so server-side metadata filtering
is **not** on this track's critical path. **Defer and note** (D9).

## Acceptance criteria

- [ ] **SPI capability flag added**: `IQuestNodeHandler` gains
      `bool RequiresChainCapability => false;` (default false). All existing
      Tier-1 handlers compile unchanged and report `false`.
- [ ] **Engine capability gate**: at the dispatch seam
      (`QuestManager.cs:328-337`), a chain-requiring node is **rejected
      pre-execution** when no wallet is bound to the run — returns `Fail`
      **before** `HandleAsync`, never broadcasting. A test asserts a Tier-2
      node is rejected with a clear "chain capability required" message when no
      wallet/capability is bound (the gate **fails closed**).
- [ ] **Pure-metadata quest runs with no chain**: a test builds a quest of
      **only Tier-1 nodes** (e.g. `HolonCreate → HolonInteract → GateCheck →
      Emit`), runs it **end-to-end with NO wallet bound and NO chain call**, and
      asserts success (the capability gate is never triggered; no
      `INftManager` / `ISwapManager` / provider call occurs).
- [ ] **Tier-1 set is chain-free**: a test asserts every Tier-1 handler reports
      `RequiresChainCapability == false`.
- [ ] **Tier-2 set requires capability**: a test asserts each Tier-2 handler
      (Mint/Grant, Transfer, Swap, Refund) reports
      `RequiresChainCapability == true`.
- [ ] New `QuestNodeType` members added (final list in `plan.md` / summary),
      each with exactly one handler in `Services/Quest/Handlers/` and one DI
      registration; `QuestNodeHandlerRegistry` startup still throws on any
      duplicate (`QuestNodeHandlerRegistry.cs:21-27` invariant holds).
- [ ] **GateCheckNode** evaluates a tenant-supplied predicate over upstream JSON
      outputs + injected reads and returns Pass/Fail; a Fail causes the engine's
      existing failed-predecessor-skip (`QuestManager.cs:275-279`) to gate
      downstream. `ConditionNodeHandler` no-op is removed/superseded.
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
      test asserts it performs **no economic computation** — only deserialize
      params → call a manager/primitive → serialize; no pricing, accounting,
      token-semantics, or vesting math.
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
- [ ] Commits follow `[economic-primitive-nodes] <verb> <subject>` (prefix kept
      to match the legacy directory).
- [ ] `tracks.md` row moves to `[x]` Shipped.

## Out of scope (explicit non-goals — guard against scope creep)

- **The suspend/resume/signal engine machinery** — owned by the
  **durable-workflow-engine** track (Track 2). This track's nodes **run on**
  that engine; they do not build it. A suspending GateCheck only declares the
  "re-evaluate-later" intent; Track 2 parks the run.
- **The value-path broadcast / custody fixes** (C1/C2/H3, review Part A) —
  owned by the **value-path-wiring** track (Track 1). Swap/Transfer/Grant/Refund
  **depend on** real signing + broadcast landing there.
- **The SDK** — owned by the **workflow-sdk** track. No SDK methods here.
- **ALL economic semantics** — stay in ArdaNova: swap *rates*, *what a project
  token is*, *cancel conditions*, *vesting math*, payout/fiat logic. **Do NOT
  build a "project token" or "equity" type** — those are tenant `asset_type`
  values + `metadata` (economic object = Holon + asset_type + metadata, zero
  schema change beyond the Part-B-#1 link).
- **Soulbound clawback primitive** (H2 / signing D7) — deferred; RefundNode
  fails closed for soulbound reversal.
- **Server-side queryable-metadata** (Part B #2) — deferred (D9).

## Tier

**Tier 1** — the generic node library that makes Quest the holonic-workflow
*substrate*. The Tier-1 transformation nodes are chain-free and run today; the
Tier-2 chain-action subset depends on Track 1 for real value flow. The split is
enforced by one capability flag, not a type hierarchy.

## Dependencies

- **durable-workflow-engine** (Track 2) — the suspend/resume host this track's
  nodes RUN on. GateCheck-as-hold needs its "re-evaluate-later" contract;
  RefundNode needs its saga-compensation hook.
- **value-path-wiring** (Track 1) — real value movement (C1 custody wiring, C2
  real broadcast, H3 KYC choke point, review Part A). The **Tier-2** chain nodes
  DEPEND on real signing landing here; **Tier-1 has no such dependency**.
- **quest-core** ✓ — the handler SPI + registry + execution context
  (`IQuestNodeHandler.cs`, `QuestNodeHandlerRegistry.cs`,
  `QuestNodeExecutionContext.cs`) already shipped.
- **signing-core-keystone** ✓ — the generic signer seam; soulbound mint
  primitive shipped (`AlgorandProvider.cs:506`), clawback deferred (H2).
