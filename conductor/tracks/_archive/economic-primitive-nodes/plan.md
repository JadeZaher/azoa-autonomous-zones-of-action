# Holon-Transformation Nodes — Plan

> Track 3 of the **workflow-engine** initiative. Builds against the real
> handler/registry/manager code verified in `spec.md`. Read
> `conductor/REVIEW-economic-substrate-2026-06-16.md` Parts B + C first.
>
> **Directory-name note (legacy):** the track dir is
> `conductor/tracks/economic-primitive-nodes/` and the commit prefix is
> `[economic-primitive-nodes]` — both kept to preserve the committed `tracks.md`
> link + git history. The real title is **Holon-Transformation Nodes**; the old
> "economic primitives" name was too narrow.

## The corrected framing (the inversion)

**Holonic transformations are the base layer; chain/economic actions are an
opt-in capability on top.** A quest is a DAG of operations on holons (create,
transform, compose, link, branch, gate, emit), where a holon may be **pure
metadata** or carry chain data. The blockchain/economic nodes (mint, swap,
transfer, grant, refund) are the **subset** that tack on-chain actions onto a
holon — optional, not the center. The distinction is a **capability check, not
a type hierarchy**: transforms and chain actions share one SPI shape.

## Locked decision

### D1 — Generic node SPI + capability flag (THE headline decision — user-locked)

**One generic node-handler SPI; each node declares whether it requires a chain
capability.** The engine **refuses** to run a chain-requiring node unless the
run has a wallet bound (a pre-execution capability check). Holon transforms and
chain actions are the **same shape**; the distinction is a capability check,
**NOT** a type hierarchy.

- **Mechanism:** add `bool RequiresChainCapability => false;` to
  `IQuestNodeHandler` (`Interfaces/Quest/IQuestNodeHandler.cs:18-32`),
  default-implemented to `false`. Tier-2 chain handlers override `=> true`.
- **Gate location:** the existing dispatch seam in `QuestManager`
  (`Managers/QuestManager.cs:328-337`) — after `_registry.TryGet(...)`, before
  `handler.HandleAsync(ctx)`. If `handler.RequiresChainCapability` and the run
  has no wallet bound, set `result = QuestNodeResults.Fail("chain capability
  required: no wallet bound to run")` and skip the handler. **Fails closed.**
- **Wallet-bound resolution:** the run's actor is `QuestRun.AvatarId`
  (`Models/Quest/QuestRun.cs:31`). "Has a wallet bound" = the actor avatar has a
  usable/default wallet (resolve via `WalletManager`;
  `KeyCustodyService.PlatformWalletId` `Managers/KeyCustodyService.cs:54` is the
  platform fallback). See D2 for whether to make the binding explicit.
- **Pure-metadata quest** = every node `RequiresChainCapability == false`; never
  binds a wallet, never touches an ASA/chain. **Chain quest** binds a wallet,
  unlocking Tier-2.
- **Alternative (documented, rejected):** a marker attribute
  `[RequiresChainCapability]` instead of the property. Rejected for the
  recommendation because the default-implemented interface member compiles all
  ~32 existing handlers unchanged and is reflection-free at the gate. Revisit
  only if a handler needs a *runtime* capability decision (none does today).

## Final `QuestNodeType` list — Tier-1 (no-chain) vs Tier-2 (chain)

### Tier 1 — holonic transformations (`RequiresChainCapability == false`)

| Member | Status | Handler | Operates on |
| --- | --- | --- | --- |
| `HolonCreate` | **exists** | `HolonCreateNodeHandler` | Holon record |
| `HolonUpdate` | **exists** | `HolonUpdateNodeHandler` | Holon record |
| `HolonDelete` | **exists** | `HolonDeleteNodeHandler` | Holon record |
| `HolonGet` | **exists** | `HolonGetNodeHandler` | Holon record |
| `HolonQuery` | **exists** | `HolonQueryNodeHandler` | Holon records |
| `HolonInteract` | **exists** | `HolonInteractNodeHandler` | graph edit (reparent/peers/metadata) |
| `HolonGetChildren` | **exists** | `HolonGetChildrenNodeHandler` | graph traversal |
| `HolonGetPeers` | **exists** | `HolonGetPeersNodeHandler` | graph traversal |
| `HolonGetAncestors` | **exists** | `HolonGetAncestorsNodeHandler` | graph traversal |
| `HolonGetDescendants` | **exists** | `HolonGetDescendantsNodeHandler` | graph traversal |
| `HolonPropagate` | **exists** | `HolonPropagateNodeHandler` | subtree BFS write |
| `HolonCompose` | **exists** | `HolonComposeNodeHandler` | subtree rollup view |
| `HolonClone` | **exists** | `HolonCloneNodeHandler` | deep subtree copy |
| `HolonMoveSubtree` | **exists** | `HolonMoveSubtreeNodeHandler` | subtree reparent |
| `ComposeOutputs` | **exists** | `ComposeOutputsNodeHandler` | upstream-output rollup |
| `GateCheck` | **NEW** | `GateCheckNodeHandler` | safe predicate over upstream JSON → Pass/Fail |
| `Emit` | **NEW** | `EmitNodeHandler` | serialize tenant output; tenant settles |
| `Transform`/`Branch` | **NEW (only if needed — D8)** | — | generic value/branch shaping if the existing set lacks it |

> The 14 `Holon*` members + `ComposeOutputs` already exist
> (`QuestEnums.cs:21-35, 69`). The track's Tier-1 job is **audit + confirm**
> these are chain-free, **add GateCheck + Emit**, and add a `Transform`/`Branch`
> node **only if D8 finds a genuine gap** (default: do not add).

### Tier 2 — chain actions (`RequiresChainCapability == true`, opt-in)

| New member | Handler | Wraps | Mechanism (no economics) |
| --- | --- | --- | --- |
| `Swap` | `SwapNodeHandler` | `ISwapManager.GetSwapTransactionAsync` | DEX rate; AZOA only passes params |
| `Grant` (a.k.a. Mint) | `GrantNodeHandler` | `INftManager.MintAsync` | mint-to-actor (avatar from run ctx) + populate `Holon.token_id`/`chain_id` |
| `Transfer` | `TransferNodeHandler` | `INftManager.TransferAsync` | move-to-actor (avatar from run ctx) |
| `Refund` | `RefundNodeHandler` | `INftManager.TransferAsync` (reverse) | transfer-back / clawback-deferred |

> **4 new Tier-2 node types**, all `RequiresChainCapability == true`. `Grant` is
> mint-to-actor (the spec's MintNode/GrantNode); a separate `Mint` member is not
> added — Grant covers it. `Refund` is kept distinct from `Transfer` so the
> Track-2 saga can declare "compensation = the Refund node" by type (D7).

### Existing `Condition` member — superseded

`Condition` (`QuestEnums.cs:68`) is **superseded** by `GateCheck`; the no-op
`ConditionNodeHandler` is removed (D3). The enum member is kept for
persistence-order safety; no handler is registered for it.

### Net new node types: **6** (`GateCheck`, `Emit` Tier-1; `Swap`, `Grant`, `Transfer`, `Refund` Tier-2), plus a conditional 7th (`Transform`/`Branch`) only if D8 finds a gap.

## Decisions

### D2 — Make the wallet binding explicit? (RESOLVED: resolve-from-actor; defer explicit field)
Do **not** add a `QuestRun.BoundWalletId` field in this track. The capability
check resolves "has a wallet bound" from the run's actor avatar
(`QuestRun.AvatarId:31`) via `WalletManager`. If durable-workflow-engine
(Track 2) introduces an explicit per-run wallet binding, the capability check
reads that instead — a one-line change. Note the seam; don't pre-build it.

### D3 — GateCheck replaces Condition (RESOLVED: replace, don't add alongside)
Remove the no-op `ConditionNodeHandler` (`ConditionNodeHandler.cs:15-21`); add
`GateCheck`. Keep the `Condition` enum member for persistence-order safety but
stop registering a handler for it. The DAG-skip logic in
`QuestManager.cs:266-279` already gates once GateCheck returns Fail — **no
manager change needed beyond the capability gate (D1).**

### D4 — Predicate language + evaluator (RESOLVED: tiny whitelisted AST, no eval)
A **small whitelisted expression** over upstream JSON outputs + injected reads.
**Grammar (closed):**
- Literals: number, string, bool.
- Field path: `upstream.<nodeName>.<jsonPath>` and `reads.<name>` — resolved
  from `context.UpstreamExecutions` (`ComposeOutputsNodeHandler.cs:35-38`
  pattern) + injected read map.
- Comparison: `== != < <= > >=`. Boolean: `&& || !`. Parens.
- **Nothing else** — no function calls, no member invocation, no indexers beyond
  static path, no I/O.

**Evaluator:** hand-roll a tiny recursive-descent parser → typed AST →
interpreter over `JsonElement` (~150 LOC, zero new dependency, homebake
preference) so the whitelist is provably closed and the no-arbitrary-code test
(T11) is trivially true. Reject `DataTable.Compute`, Roslyn scripting, any
`eval`-style path. `RequiresChainCapability == false` (reads upstream JSON only).

### D5 — Injected reads come from upstream outputs, not chain (RESOLVED)
GateCheck does **not** call chain/KYC managers directly (that would couple a
gate to value/identity managers and risk economics creep, and would make a
pure-metadata gate require a capability). Instead:
- **Balance** = output of an upstream `WalletGetPortfolio` node
  (`WalletGetPortfolioNodeHandler.cs:18-25`), referenced as
  `upstream.<portfolioNode>.…`.
- **KYC status** = output of an upstream KYC-read node (kyc-module) if present,
  else a tenant-supplied `reads.kyc`.
- **External signal** = presence/value of an upstream Emit/signal node's output.

GateCheck stays **store-free and manager-free** — it only reads
`context.UpstreamExecutions`, keeping it Tier-1 / chain-free.

### D6 — Hold: composition, NOT a dedicated node (RESOLVED: compose)
"Hold until phase-met or cancelled" = a `GateCheck` the **Track-2 engine
suspends on** (parks the run until the referenced signal/read can satisfy the
predicate) + a `Refund` declared as the on-cancel saga compensation. No `Hold`
node type. The suspend machinery is Track 2's; a `Hold` node would carry no
logic of its own. **Track-2 contract this needs:** a handler-result signal
meaning "not-failed, not-succeeded, re-evaluate when input arrives." If Track 2
cannot express it without a parking node, revisit and add `Hold` then
(documented fallback, not the default).

### D7 — Refund distinct from Transfer (RESOLVED: keep distinct)
`Refund` is mechanically a reverse `Transfer`, but it is declared to the saga
**by node type** as the compensation step, and the brand/mechanism tests target
it. Soulbound reversal fails closed (clawback deferred,
`DEPLOY-STEPS-TODO.md:224-227`).

### D8 — Emit: output-only first (RESOLVED: in-band output; webhook deferred)
`Emit` writes a tenant-shaped payload to `QuestNodeExecution.Output` (consumed
by ArdaNova reading the run). **No outbound webhook in this track** — deferred
to `workflow-sdk`. Keeps fiat/payout settlement entirely tenant-side; AZOA
holds no delivery/retry state.

### D9 — A separate generic `Transform`/`Branch` node? (RESOLVED: don't add unless a gap is found)
Audit the existing Tier-1 set first. `HolonInteract` covers metadata/graph
transform; `GateCheck` covers branching (Fail gates downstream);
`ComposeOutputs` covers rollup. **Default: add no extra node.** Only if the
audit finds a genuine generic gap (e.g. a pure value-shaping transform that is
NOT a holon edit) add a Tier-1 `Transform` node, `RequiresChainCapability ==
false`. Record the audit outcome in the T-audit task.

### D10 — Holon↔asset link: the one schema change (RESOLVED: copy source_holon_id)
Add `asset_id` (`option<string>`), `tx_hash` (`option<string>`), and `holon_id`
(`record<holon>`, copy `source_holon_id` at `OperationLog.cs:90-94`) to
`OperationLog`. Populate `Holon.token_id`/`chain_id` (`Holon.cs:60-61,68-69`)
from the mint/grant result in the Grant path. Schema regen (G6 SCHEMAFULL).

### D11 — Queryable metadata: DEFER (RESOLVED)
Do not scope server-side `WHERE metadata.x=y` (Part B #2). GateCheck reads
upstream outputs, not the store. Note the deferral; revisit if a server-side
gate need appears.

## Task flow and dependencies

```
T1 (SPI capability flag + enum + DI scaffolding)
 ├─ T2 Tier-1 audit (confirm Holon* + ComposeOutputs are chain-free; D9 decision)
 ├─ T3 GateCheck (D4 evaluator, RequiresChainCapability=false) ──┐
 ├─ T4 Emit (D8, RequiresChainCapability=false)                  │
 ├─ T5 Swap   (RequiresChainCapability=true) ──┐                 ├─ T10 composed-DAG tests
 ├─ T6 Grant  (RequiresChainCapability=true) ──┤                 │      (swap→gate→grant, gate→refund-on-fail)
 ├─ T7 Transfer(RequiresChainCapability=true) ─┤                 │
 └─ T8 Refund (RequiresChainCapability=true) ──┘                 │
T9  engine capability gate (QuestManager seam) + reject-test + pure-metadata-quest test
T12 Holon↔asset link (schema regen) — parallel to T3–T8
T11 mechanism-only + safe-evaluator + tier-flag tests
T13 brand-leak test + zero-warning build + full sweep (ONCE, at end)
```

## Detailed TODOs

- **T1 — SPI capability flag + enum members + config POCOs + DI.**
  Add `bool RequiresChainCapability => false;` to `IQuestNodeHandler`
  (`IQuestNodeHandler.cs:18-32`). Append `GateCheck, Emit, Swap, Grant,
  Transfer, Refund` to `QuestNodeType` (`QuestEnums.cs:69`, **append-only**;
  existing rows persist as `(int)`). Add config POCOs to
  `Models/Quest/NodeConfigs.cs` (`GateCheckNodeConfig` with `Predicate` string +
  read bindings; `EmitNodeConfig` opaque `JsonElement Payload`; `SwapNodeConfig`;
  `GrantNodeConfig`/`TransferNodeConfig`; `RefundNodeConfig`). One
  `AddSingleton<IQuestNodeHandler, …>` per handler.
  **Acceptance:** SPI default keeps all existing handlers compiling at `false`;
  registry builds with no duplicate-type throw (`QuestNodeHandlerRegistry.cs:21-27`).

- **T2 — Tier-1 audit (D9).**
  Confirm the 14 `Holon*` handlers + `ComposeOutputs` form a coherent
  transformation set and each reports `RequiresChainCapability == false`. Decide
  (D9) whether a generic `Transform`/`Branch` node is genuinely missing; default
  is **no new node**. **Acceptance:** audit note recorded; all Tier-1 handlers
  chain-free.

- **T3 — GateCheckNodeHandler + safe evaluator (D4, D5).**
  Hand-rolled recursive-descent parser → AST → interpreter over `JsonElement`,
  resolving `upstream.<node>.<path>` from `context.UpstreamExecutions` and
  `reads.<name>` from an injected read map sourced from upstream outputs.
  Returns `Ok {"pass":true}` on Pass; `Fail("gate not met: <predicate>")` on Fail
  so the engine skips downstream (`QuestManager.cs:275-279`).
  `RequiresChainCapability == false`. **Acceptance:** Pass + Fail covered;
  malformed predicate → `Fail` with a clear parse error, never an exception
  escape.

- **T4 — EmitNodeHandler (D8).**
  Serialize `EmitNodeConfig.Payload` (+ optionally merge referenced upstream
  outputs) to `QuestNodeExecution.Output`. No webhook.
  `RequiresChainCapability == false`. **Acceptance:** pure pass-through; no
  settlement/fiat/payout computation.

- **T5 — SwapNodeHandler (`RequiresChainCapability => true`).**
  Deserialize `SwapNodeConfig` → `await _swapManager.GetSwapTransactionAsync(req,
  idempotencyKey)` (`ISwapManager.cs:22`) → serialize. Idempotency key from run
  context. **Acceptance:** params passed through; mocked `ISwapManager` test
  asserts no rate computed in the handler.

- **T6 — GrantNodeHandler (`RequiresChainCapability => true`; + Holon↔asset, with T12).**
  Copy `NftMintNodeHandler` (`NftMintNodeHandler.cs:19-26`); actor =
  `context.Quest.AvatarId`. On success, populate `Holon.token_id`/`chain_id`
  from the mint result (T12 link). **Acceptance:** mints via `INftManager.MintAsync`
  with run-context avatar; body avatar ignored.

- **T7 — TransferNodeHandler (`RequiresChainCapability => true`).**
  Copy `NftTransferNodeHandler` (`NftTransferNodeHandler.cs:18-25`); actor from
  run context. **Acceptance:** transfers via `INftManager.TransferAsync`; body
  avatar ignored.

- **T8 — RefundNodeHandler (`RequiresChainCapability => true`, D7).**
  Reverse transfer via `INftManager.TransferAsync` (swap actors per
  `RefundNodeConfig`). Soulbound → `Fail("soulbound reversal requires clawback
  primitive — deferred (H2 / signing D7)")` (`DEPLOY-STEPS-TODO.md:224-227`).
  **Acceptance:** reverse transfer works; soulbound path fails closed.

- **T9 — Engine capability gate + reject + pure-metadata tests (D1).**
  Insert the pre-execution capability check at the dispatch seam
  (`QuestManager.cs:328-337`): if `handler.RequiresChainCapability` and no wallet
  is bound to the run → `Fail("chain capability required: no wallet bound to
  run")` before `HandleAsync`. **Acceptance:** (1) a Tier-2 node is **rejected
  pre-execution** when no wallet/capability is bound (fails closed, no broadcast);
  (2) a **pure-metadata quest** of only Tier-1 nodes (`HolonCreate →
  HolonInteract → GateCheck → Emit`) runs **end-to-end with NO wallet bound and
  NO chain call** (no `INftManager`/`ISwapManager`/provider invocation).

- **T10 — Composed-DAG determinism tests.**
  With mocked managers, build `swap → gate → grant` (gate Pass ⇒ grant runs;
  gate Fail ⇒ grant skipped via `QuestManager.cs:275-279`) and
  `gate → refund-on-fail`. **Acceptance:** both DAGs deterministic; both gate
  branches exercised.

- **T11 — Mechanism-only + safe-evaluator + tier-flag tests.**
  Per handler: assert deserialize → call manager/primitive → serialize (no
  economic computation). GateCheck: assert the evaluator cannot execute
  arbitrary code (`System.…`, method-call, indexer payloads → rejected at
  parse). Assert every Tier-1 handler reports `RequiresChainCapability == false`
  and every Tier-2 handler reports `true`. **Acceptance:** all green.

- **T12 — Holon↔asset link primitive (D10, schema regen).**
  Add `asset_id`/`tx_hash` `option<string>` + `holon_id` `record<holon>` columns
  to `OperationLog` (copy `source_holon_id` at `OperationLog.cs:90-94`). Populate
  `Holon.token_id`/`chain_id` from mint/grant results. Regenerate SCHEMAFULL
  (G6). **Acceptance:** typed link round-trips through SurrealDB; schema tests
  green.

- **T13 — Brand-leak guard + final sweep (ONCE).**
  Grep/test that `Services/Quest/Handlers/*` and new config POCOs contain no
  `ArdaNova|project-token|project|equity|vesting` strings. Then run the full
  `dotnet build` (zero warnings, `conductor/workflow.md:18`) + `dotnet test`
  sweep **once** at the end (run-once-at-end policy). **Acceptance:** zero
  warnings; all tests green; no brand leak.

## Commit strategy

One commit per TODO, message form `[economic-primitive-nodes] <verb> <subject>`
(prefix kept to match the legacy directory), e.g.:
- `[economic-primitive-nodes] add RequiresChainCapability flag to IQuestNodeHandler SPI`
- `[economic-primitive-nodes] add GateCheck/Emit Tier-1 + Swap/Grant/Transfer/Refund Tier-2 node types + DI`
- `[economic-primitive-nodes] implement safe whitelisted GateCheck predicate evaluator`
- `[economic-primitive-nodes] add EmitNode output sink (tenant settles)`
- `[economic-primitive-nodes] wrap ISwapManager in SwapNode (chain capability, mechanism only)`
- `[economic-primitive-nodes] add Grant/Transfer/Refund chain nodes (actor from run ctx)`
- `[economic-primitive-nodes] enforce pre-execution chain-capability gate at dispatch seam`
- `[economic-primitive-nodes] add typed Holon-asset link to OperationLog (Part B #1)`
- `[economic-primitive-nodes] mechanism-only + pure-metadata + capability-gate + composed-DAG tests`

## Success criteria

- One generic SPI + `RequiresChainCapability` flag; the engine refuses a
  chain-requiring node pre-execution when no wallet is bound (fails closed).
- 6 net-new `QuestNodeType` values (2 Tier-1, 4 Tier-2), one handler + one DI
  line each; registry one-per-type invariant holds.
- Tier-1 transforms (existing 14 `Holon*` + `ComposeOutputs` + new
  `GateCheck`/`Emit`) all `RequiresChainCapability == false`; a pure-metadata
  quest runs end-to-end with no wallet and no chain call.
- Tier-2 chain nodes (Swap/Grant/Transfer/Refund) all
  `RequiresChainCapability == true`; wrap real managers; actor always from run
  context; soulbound refund fails closed.
- GateCheck gates downstream via the existing engine skip; predicate evaluator
  provably safe (no arbitrary code).
- Emit keeps fiat/equity-payout economics in ArdaNova.
- Holon↔asset typed link lands (the one schema change).
- Mechanism-only + pure-metadata + capability-rejection + composed-DAG +
  brand-leak tests green.
- Zero-warning build; `dotnet test` green; SurrealDB sole engine.
- `tracks.md` row → `[x]` Shipped.
