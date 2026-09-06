# ActionDefinition Temporal / Forkable DAG Model — Specification

## Goal
Tier 1 (architectural design + minimal in-memory cutover): split the action
model into **definition** vs **runtime** so that an action can be **re-run**,
**forked mid-execution**, and have **failed branches preserved as
first-class audit records** rather than overwritten. The SurrealDB schema
([[surrealdb-migration]] tasks 9–10) must reflect this split from day one,
so this track lands its data model + state machine **before** that schema is
written.

Scope is deliberately tight: this is the **runtime/template split + fork
lineage + reuse of the existing intra-iteration DAG validator**, not the
full "MetaNode / HolonDAG / time-sliced ledger" architecture that the
external analysis sketches. Most of that surface (templates, parameterized
instantiation, versioning) already exists today in [[action-template-existing]]
(`Models/ActionDefinition/ActionTemplate.cs`, `Services/ActionDefinition/ActionInstantiator.cs`).

## Current state (file:line evidence)
- `Models/ActionDefinition/ActionDefinition.cs` conflates *definition* and *runtime*: it carries
  both `Status` (lifecycle) and a `Nodes` collection whose `ActionNode.State`,
  `Output`, `Error` are mutated **in place** during execution
  (`Models/ActionDefinition/ActionNode.cs:19-28`).
- An action can therefore only be executed **once**: re-running mutates the
  same row. Forking is impossible — there is no lineage pointer, and the
  prior attempt's per-node outputs would be overwritten.
- `ActionDagValidator` (`Services/ActionDagValidator.cs`) enforces strict
  acyclicity over `action.Nodes` + `action.Edges`. This stays correct
  **within a single run** and is what the "intra-iteration acyclic / 
  inter-iteration cyclic" distinction in the pasted analysis maps onto —
  but the cyclic part lives in a *separate graph* (`ActionRun.ParentRunId`
  lineage), not in the validator.

## Design (what this track produces)

### Domain model
- `ActionDefinition` becomes the **definition** a user owns (name, nodes, edges,
  dependencies, template ancestry). It loses `Status`, and `ActionNode`
  loses `State`/`Output`/`Error`.
- New `ActionRun` — one execution attempt of an `ActionDefinition`. Fields:
  `Id`, `ActionId`, `UserId`, `Status` (Pending/Running/Succeeded/
  Failed/Forked/Cancelled), `StartedAt`, `EndedAt`, **`ParentRunId?`**,
  **`ForkedAtNodeId?`**, **`ForkReason?`**.
- New `ActionNodeExecution` — per-(run, node) record carrying `State`,
  `Output`, `Error`, `StartedAt`, `EndedAt`. Replaces the in-place
  mutation on `ActionNode`.
- `ExecutionOrder` stays on `ActionNode` (it is a property of the
  definition, computed once per validation — preserves the
  [[architecture-decoupling]] single-authority fix).

### State machine — fork semantics
- A `Run` is **forkable** while in `Running` (and explicitly **not** while
  `Succeeded` — that's a re-run, not a fork; re-runs create a new root
  `Run` with `ParentRunId = null`).
- Forking from `parent` at `nodeId`:
  1. New `Run` is created with `ParentRunId = parent.Id`,
     `ForkedAtNodeId = nodeId`, status `Pending`.
  2. Per-node executions for nodes with `ExecutionOrder < forkPoint` are
     **copied by reference** (same `ActionNodeExecution` row participates
     in both runs via a `RELATE` edge) — no recompute, no duplication.
  3. The parent `Run` transitions `Running → Forked` (terminal). Its
     in-flight node executions are marked `Cancelled`. Compensation
     (saga) for any already-applied irreversible chain ops on the parent
     is the saga's job, *not* this track's — fork records the intent;
     [[durable-saga-orchestration]] handles unwind.
- "Mark a branch failed" = transition `Running → Failed`. Indistinguishable
  from a normal failure except that the *caller* (e.g. a parent supervisor
  agent) chose it. Audit field `FailReason` distinguishes.

### Intra-iteration vs inter-iteration
- **Intra-iteration (within one `Run`):** `ActionDagValidator` stays
  authoritative — strict acyclicity, single `ExecutionOrder`. No change.
- **Inter-iteration (across `Run`s):** `ParentRunId` edges form a tree
  (forks branch, never merge). If a future need arises for **merging**
  forks (which the analysis flags but no current use case requires),
  that's a separate track. Today: tree, not DAG.
- The "cycles allowed across iterations" framing in the pasted analysis
  is therefore *not* introduced here — cycles are not needed for fork +
  re-run; they would only matter for a feedback-loop orchestrator
  (out of scope).

### Acceptance
- `ActionRun` + `ActionNodeExecution` models exist; `ActionDefinition.Status` and
  `ActionNode.State`/`Output`/`Error` removed.
- `ActionManager` execution path threads a `runId` through every handler
  invocation; writes go to `ActionNodeExecution`, never to `ActionNode`.
- `ForkAsync(runId, atNodeId, reason)` API on `IActionManager` produces a
  new `Run`, marks parent `Forked`, cancels parent's in-flight nodes.
- DAG validation still green on every existing template + action; existing
  537+ unit tests pass after migration.
- One **ADR** (`conductor/tracks/action-temporal-fork-model/ADR.md`)
  records the deliberate *non-goals*: no MetaNode rebuild, no
  time-sliced ledger, no inter-iteration cycle support, no automatic
  compensation (saga's job), no merge-of-forks.
- Schema spec hand-off doc (`SURREAL-SCHEMA-HINTS.md`) lists the exact
  tables + edges [[surrealdb-migration]] task 9 should produce
  (`action`, `action_node`, `action_edge`, `action_run`,
  `action_node_execution`, `RELATE action_run -> forked_from -> action_run`,
  `RELATE action_run -> executes -> action_node_execution`).

## Out of scope (explicit non-goals — guard against scope creep)
- **No MetaNode / HolonDAG rename.** `ActionTemplate` already plays that
  role; renaming would churn API + SDK for zero benefit.
- **No new orchestrator runtime.** Existing `ActionManager` +
  `ActionNodeHandlerRegistry` stays. Only the data plane changes.
- **No inter-iteration feedback cycles.** Lineage is a tree.
- **No snapshot pruning / state-explosion mitigation.** A run is cheap
  (one row + N node-execution rows). If volume becomes a problem post-
  launch, that's a separate ops track.
- **No automatic fork compensation.** Forking records intent; the saga
  decides what to undo.
- **No persistence engine work.** This track ships against the existing
  EF/InMemory seam; [[surrealdb-migration]] then implements the SurrealDB
  side using `SURREAL-SCHEMA-HINTS.md` as input.

## Dependencies
- Builds on [[architecture-decoupling]] (uses the per-aggregate
  `IActionStore` seam — no god-interface coupling).
- **Blocks** [[surrealdb-migration]] tasks 9 (action graph remodel),
  10 (holon polyhierarchy — touched only because `ActionDefinition` schema reshuffle
  may move shared columns), and the action portion of task 3
  (`SCHEMAFULL` schemas). Foundation tasks 1, 2, 4, value-table schemas
  (wallet/bridge/swap/NFT/operation-log), and saga schema (8a/8b) can
  start immediately in parallel.
- Independent of [[durable-saga-orchestration]] (fork records intent;
  the saga decides compensation).

## Realistic effort
~1 week for the design + in-memory cutover + test port, given the
existing `IActionStore` seam absorbs the storage-shape change. The
SurrealDB write-through happens later in [[surrealdb-migration]].
