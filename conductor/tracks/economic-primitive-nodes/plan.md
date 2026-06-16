# Economic-Primitive Nodes — Plan

> Track 3 of the **workflow-engine** initiative. Builds against the real
> handler/registry/manager code verified in `spec.md`. Read
> `conductor/REVIEW-economic-substrate-2026-06-16.md` Parts B + C first.

## Locked constraint (do not violate)

OASIS = **generic primitives only**. Every node moves value by tenant params
and contains **zero** economics. No `project`, `project-token`, `equity`,
`vesting`, pricing, accounting, or token-semantics anywhere in
`Services/Quest/Handlers/*` or the new config POCOs. Brand-leak test enforces
this (TODO-11).

## Final `QuestNodeType` values added (6)

Appended to `Models/Quest/QuestEnums.cs:19-70` (order is append-only; existing
rows persist as `(int)QuestNodeType`, so **add at the end**):

| New member | Handler | Wraps | Mechanism (no economics) |
| --- | --- | --- | --- |
| `GateCheck` | `GateCheckNodeHandler` | safe predicate evaluator | compare upstream JSON / injected reads → Pass/Fail |
| `Swap` | `SwapNodeHandler` | `ISwapManager.GetSwapTransactionAsync` | DEX rate; OASIS only passes params |
| `Grant` | `GrantNodeHandler` | `INftManager.MintAsync` | mint-to-actor (avatar from run ctx) |
| `Transfer` | `TransferNodeHandler` | `INftManager.TransferAsync` | move-to-actor (avatar from run ctx) |
| `Refund` | `RefundNodeHandler` | `INftManager.TransferAsync` (reverse) | transfer-back / clawback-deferred |
| `Emit` | `EmitNodeHandler` | none (output sink) | serialize tenant output; tenant settles |

> That is **6 new node types**. `Refund` and `Transfer` could collapse if the
> reverse-transfer is literally a `Transfer` with swapped actors — see
> Decision D5; the recommendation keeps `Refund` distinct so the saga can
> declare it as compensation by type. **No dedicated `Hold` type** (Decision D4).

Existing `Condition` (`QuestEnums.cs:68`) is **superseded** by `GateCheck`; the
no-op `ConditionNodeHandler` is removed (Decision D1).

## Decisions (resolve before building)

### D1 — GateCheck replaces Condition (RESOLVED: replace, don't add alongside)
The no-op `ConditionNodeHandler` (`ConditionNodeHandler.cs:15-21`) and the
dead `QuestEdge.Condition` evaluation are superseded. **Remove the `Condition`
no-op handler; add `GateCheck`.** Keep the `Condition` enum member for
persistence-order safety but stop registering a handler for it (or map it to
the GateCheck handler with a deprecation note). The DAG-skip logic in
`QuestManager.cs:266-279` already does the gating once GateCheck returns Fail —
**no manager change needed**.

### D2 — Predicate language + evaluator (RESOLVED: tiny whitelisted AST, no eval)
The predicate is a **small whitelisted expression** over upstream JSON outputs
+ injected reads. **Grammar (closed):**
- Literals: number, string, bool.
- Field path: `upstream.<nodeName>.<jsonPath>` and `reads.<name>` (balance, kyc,
  signal) — resolved from `context.UpstreamExecutions`
  (`ComposeOutputsNodeHandler.cs:35-38` pattern) + injected read map.
- Comparison: `== != < <= > >=`. Boolean: `&& || !`. Parens.
- **Nothing else** — no function calls, no member invocation, no indexers
  beyond static path, no I/O.

**Evaluator choice:** hand-roll a tiny recursive-descent parser → typed AST →
interpreter over `JsonElement`, OR use a well-scoped existing safe expression
lib. **Recommendation: hand-roll** (≈150 LOC, zero new dependency, matches the
homebake preference) so the whitelist is provably closed and the
"no-arbitrary-code" test (TODO-09) is trivially true. Reject `DataTable.Compute`,
Roslyn scripting, and any `eval`-style path. The predicate string is a tenant
param; the *grammar* is OASIS mechanism.

### D3 — Where injected reads come from (RESOLVED: upstream outputs, not chain)
GateCheck does **not** call chain or KYC managers directly (that would couple a
gate to value/identity managers and risk economics creep). Instead:
- **Balance** read = the output of an upstream `WalletGetPortfolio` node
  (`WalletGetPortfolioNodeHandler.cs:18-25`), referenced as
  `upstream.<portfolioNode>.…`.
- **KYC status** = the output of an upstream KYC-read node (kyc-module track) if
  present, else a tenant-supplied `reads.kyc` value.
- **External signal** = presence/value of an upstream Emit/signal node's output.

So GateCheck stays **store-free and manager-free** — it only reads
`context.UpstreamExecutions`. This is the minimal, safest design and keeps the
node mechanism-only.

### D4 — Hold: composition, NOT a dedicated node (RESOLVED: compose)
**Recommendation: do not add a `Hold` node type.** "Hold until phase-met or
cancelled" = a `GateCheck` the **Track-2 engine suspends on** (parks the run
until the referenced signal/read can satisfy the predicate) + a `Refund`
declared as the on-cancel saga compensation. Rationale:
- The suspend/resume machinery is Track 2's (`durable-workflow-engine`);
  duplicating it as a node here violates the track boundary.
- A separate `Hold` node would carry no logic of its own — it would just be "a
  GateCheck that hasn't passed yet," which is exactly suspend semantics.
- **Track-2 contract this needs:** a handler-result signal meaning
  "not-failed, not-succeeded, re-evaluate when input arrives." `spec.md` flags
  this; if Track 2 cannot express it without a parking node, revisit and add a
  `Hold` member then (documented fallback, not the default).

### D5 — Refund distinct from Transfer (RESOLVED: keep distinct)
`Refund` is mechanically a reverse `Transfer`, but it is declared to the saga
**by node type** as the compensation step. Keeping a distinct `Refund` type lets
the Track-2 saga wire "compensation = the Refund node" declaratively and lets
the brand/mechanism tests target it. Soulbound reversal fails closed (clawback
deferred, `DEPLOY-STEPS-TODO.md:224-227`).

### D6 — Emit: output-only first (RESOLVED: in-band output; webhook deferred)
`Emit` writes a tenant-shaped payload to `QuestNodeExecution.Output` (consumed
by ArdaNova reading the run). **No outbound webhook in this track** — an
outbound callback sink is deferred to the `workflow-sdk` track. This keeps
fiat/payout settlement entirely on the tenant side and avoids OASIS holding any
delivery/retry state for tenant economics.

### D7 — Holon↔asset link: the one schema change (RESOLVED: copy source_holon_id)
Add `asset_id` (`option<string>`), `tx_hash` (`option<string>`), and `holon_id`
(`record<holon>`, copy the `source_holon_id` pattern at `OperationLog.cs:90-94`)
to `OperationLog`. Populate `Holon.token_id`/`chain_id` (`Holon.cs:60-61,68-69`)
from the mint/grant result in the Grant path. Schema regen (G6 SCHEMAFULL).

### D8 — Queryable metadata: DEFER (RESOLVED)
Do not scope server-side `WHERE metadata.x=y` (Part B #2). GateCheck reads
upstream outputs, not the store; a tenant shapes the gate input by fetching
`asset_type` + filtering client-side. Note the deferral; revisit if a server-side
gate need appears.

## Task flow and dependencies

```
T1 (enum + DI scaffolding)
 ├─ T2 GateCheck (D2 evaluator) ──┐
 ├─ T3 Swap                       │
 ├─ T4 Grant ──┐                  ├─ T8 composed-DAG tests (swap→gate→grant,
 ├─ T5 Transfer│                  │      gate→refund-on-fail)
 ├─ T6 Refund ─┘                  │
 └─ T7 Emit ──────────────────────┘
T9  Holon↔asset link (schema regen) — parallel to T2–T7
T10 mechanism-only + safe-evaluator tests
T11 brand-leak test + zero-warning build + full sweep (ONCE, at end)
```

## Detailed TODOs

- **T1 — Enum members + config POCOs + DI scaffolding.**
  Append `GateCheck, Swap, Grant, Transfer, Refund, Emit` to `QuestNodeType`
  (`QuestEnums.cs:69`, append-only). Add config POCOs to
  `Models/Quest/NodeConfigs.cs` (`GateCheckNodeConfig` with `Predicate` string +
  optional read bindings; `SwapNodeConfig`; `GrantNodeConfig`/`TransferNodeConfig`
  reusing `NftMintRequest`/`NftTransferNodeConfig` shapes; `RefundNodeConfig`;
  `EmitNodeConfig` with opaque `JsonElement Payload`). Register one
  `AddSingleton<IQuestNodeHandler, …>` per handler near the existing handler DI
  block. **Acceptance:** registry builds with no duplicate-type throw
  (`QuestNodeHandlerRegistry.cs:21-27`); build green.

- **T2 — GateCheckNodeHandler + safe evaluator (D2, D3).**
  Hand-rolled recursive-descent parser → AST → interpreter over `JsonElement`,
  resolving `upstream.<node>.<path>` from `context.UpstreamExecutions`
  (`ComposeOutputsNodeHandler.cs:35-38` pattern) and `reads.<name>` from an
  injected read map sourced from upstream outputs (D3). Returns `Ok` with
  `{ "pass": true/false }` on Pass; returns `Fail("gate not met: <predicate>")`
  on Fail so the engine skips downstream (`QuestManager.cs:275-279`).
  **Acceptance:** Pass and Fail both covered; malformed predicate → `Fail` with
  a clear parse error, never an exception escape.

- **T3 — SwapNodeHandler.**
  Deserialize `SwapNodeConfig` → `await _swapManager.GetSwapTransactionAsync(req,
  idempotencyKey)` (`ISwapManager.cs:22`) → serialize result. Idempotency key
  from run context. **Acceptance:** handler passes tenant params through; a test
  with a mocked `ISwapManager` asserts no rate is computed in the handler.

- **T4 — GrantNodeHandler (+ Holon↔asset population, with T9).**
  Copy `NftMintNodeHandler` (`NftMintNodeHandler.cs:19-26`); actor =
  `context.Quest.AvatarId`. On success, populate `Holon.token_id`/`chain_id`
  from the mint result (T9 link). **Acceptance:** mints via `INftManager.MintAsync`
  with run-context avatar; body avatar ignored.

- **T5 — TransferNodeHandler.**
  Copy `NftTransferNodeHandler` (`NftTransferNodeHandler.cs:18-25`); actor from
  run context. **Acceptance:** transfers via `INftManager.TransferAsync`;
  body avatar ignored.

- **T6 — RefundNodeHandler (D5).**
  Reverse transfer via `INftManager.TransferAsync` (swap actors per
  `RefundNodeConfig`). For a soulbound asset, `Fail("soulbound reversal requires
  clawback primitive — deferred (H2 / signing D4)")`
  (`DEPLOY-STEPS-TODO.md:224-227`). **Acceptance:** reverse transfer works;
  soulbound path fails closed with the documented message.

- **T7 — EmitNodeHandler (D6).**
  Serialize `EmitNodeConfig.Payload` (+ optionally merge referenced upstream
  outputs) to `QuestNodeExecution.Output`. No webhook. **Acceptance:** output is
  a pure pass-through; no settlement/fiat/payout computation.

- **T8 — Composed-DAG determinism tests.**
  With mocked managers, build `swap → gate → grant` (gate Pass ⇒ grant runs;
  gate Fail ⇒ grant skipped via `QuestManager.cs:275-279`) and
  `gate → refund-on-fail`. Assert deterministic outputs/skips across repeated
  runs. **Acceptance:** both DAGs deterministic; both gate branches exercised.

- **T9 — Holon↔asset link primitive (D7, schema regen).**
  Add `asset_id`/`tx_hash` `option<string>` + `holon_id` `record<holon>` columns
  to `OperationLog` (copy `source_holon_id` at `OperationLog.cs:90-94`). Populate
  `Holon.token_id`/`chain_id` from mint/grant results. Regenerate SCHEMAFULL
  schema (G6). **Acceptance:** typed link round-trips through SurrealDB; schema
  tests green.

- **T10 — Mechanism-only + safe-evaluator tests.**
  Per handler: assert it only deserializes → calls a manager/primitive →
  serializes (no economic computation). For GateCheck: assert the evaluator
  cannot execute arbitrary code (feed `System.…`, method-call, indexer payloads
  → all rejected at parse). **Acceptance:** all green; no-arbitrary-code proven.

- **T11 — Brand-leak guard + final sweep (ONCE).**
  Grep/test that `Services/Quest/Handlers/*` and new config POCOs contain no
  `ArdaNova|project-token|project|equity|vesting` strings. Then run the full
  `dotnet build` (zero warnings, `conductor/workflow.md:18`) + `dotnet test`
  sweep **once** at the end (per the run-once-at-end policy). **Acceptance:**
  zero warnings; all tests green; no brand leak.

## Commit strategy

One commit per TODO, message form `[economic-primitive-nodes] <verb> <subject>`,
e.g.:
- `[economic-primitive-nodes] add GateCheck/Swap/Grant/Transfer/Refund/Emit node types + DI`
- `[economic-primitive-nodes] implement safe whitelisted GateCheck predicate evaluator`
- `[economic-primitive-nodes] wrap ISwapManager in SwapNode (mechanism only)`
- `[economic-primitive-nodes] add Grant/Transfer/Refund value nodes (actor from run ctx)`
- `[economic-primitive-nodes] add typed Holon-asset link to OperationLog (Part B #1)`
- `[economic-primitive-nodes] add EmitNode output sink (tenant settles)`
- `[economic-primitive-nodes] mechanism-only + composed-DAG + safe-evaluator tests`

## Success criteria

- 6 new `QuestNodeType` values, one handler + one DI line each; registry
  one-per-type invariant holds (`QuestNodeHandlerRegistry.cs:21-27`).
- GateCheck gates downstream via the existing engine skip; predicate evaluator
  provably safe (no arbitrary code).
- Swap/Grant/Transfer/Refund wrap real managers; actor always from run context;
  soulbound refund fails closed.
- Emit keeps fiat/equity-payout economics in ArdaNova.
- Holon↔asset typed link lands (the one schema change).
- Mechanism-only + composed-DAG-determinism + brand-leak tests green.
- Zero-warning build; `dotnet test` green; SurrealDB sole engine.
- `tracks.md` row → `[x]` Shipped.
```
