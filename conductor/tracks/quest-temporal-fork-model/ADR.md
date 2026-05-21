# ADR — Quest Temporal / Forkable DAG Model

**Status:** Accepted (track `quest-temporal-fork-model`, wave parallel to `surrealdb-client-package`).
**Date:** 2026-05-21
**Spec:** [`spec.md`](spec.md)
**Plan:** [`plan.md`](plan.md)
**Downstream consumer:** [`surrealdb-migration`](../surrealdb-migration/) tasks 3 (quest portion), 9 (graph remodel), 10 (holon polyhierarchy).

---

## 1. Context

`Models/Quest/Quest.cs` conflates **definition** (the workflow shape — nodes,
edges, dependencies, template ancestry) with **runtime** (which attempt is
in flight, what each node returned, when it ended). The same row is mutated
in place: `Quest.Status`, `QuestNode.State`, `QuestNode.Output`, and
`QuestNode.Error` all change during execution.

The consequences this track removes:

1. **A quest can be executed exactly once.** Re-running mutates the same
   row and clobbers the previous attempt's per-node outputs. No audit trail.
2. **Forking is impossible.** There is no lineage pointer. The prior
   attempt's outputs would be overwritten before a forked branch could
   record them.
3. **Failed branches are not first-class.** A failed run is just the
   previous run's row in a `Failed` state — re-running silently erases it.

`Services/QuestDagValidator.cs` already enforces strict intra-iteration
acyclicity. That validator stays untouched (see §4 — non-goal).

## 2. Decision

Split the model into **definition** and **runtime**:

| Aspect | Definition (today's `Quest`) | Runtime (new) |
| --- | --- | --- |
| Identity | `Quest.Id` (one per workflow) | `QuestRun.Id` (one per attempt) |
| Lifecycle | Immutable after creation (modulo CRUD edits to the shape) | `Pending → Running → Succeeded \| Failed \| Forked \| Cancelled` |
| Per-node state | None (definition is shape-only) | `QuestNodeExecution` keyed by `(RunId, NodeId)` |
| Lineage | None | `QuestRun.ParentRunId?` (tree, not DAG) |

### 2.1 New models (in `Models/Quest/`)

- **`QuestRun`** — one execution attempt. Fields: `Id`, `QuestId`,
  `AvatarId`, `Status` (`QuestRunStatus`), `StartedAt`, `EndedAt?`,
  `ParentRunId?`, `ForkedAtNodeId?`, `ForkReason?`, `FailReason?`.
- **`QuestNodeExecution`** — per-`(RunId, NodeId)` record. Fields: `Id`,
  `RunId`, `NodeId`, `State` (`QuestNodeState`), `Output?`, `Error?`,
  `StartedAt`, `EndedAt?`.
- **`QuestRunStatus`** — `Pending`, `Running`, `Succeeded`, `Failed`,
  `Forked`, `Cancelled`. **Distinct from `QuestStatus`** (which stays on
  the definition for draft/active/archived semantics) so that run-level
  and definition-level lifecycle never collide.
- **`QuestNodeState`** — extended with `Cancelled` (in addition to
  existing `Pending`, `Running`, `Succeeded`, `Failed`, `Skipped`). No
  `Forked` value — fork is a *run-level* status, never a *node-level* state.

### 2.2 What is removed from definition models

- `Quest.Status`
- `Quest.CompletedDate`
- `QuestNode.State`
- `QuestNode.Output`
- `QuestNode.Error`

These fields are **annotated `[Obsolete]`** in tasks 3–6 rather than
hard-deleted. `Managers/QuestManager.cs` (B2's track territory, tasks
10–13 of plan.md) still reads and writes them during this window. The
fields will be physically removed in B2's pass once the
`(runId, nodeId)`-keyed execution path replaces every consumer. The
`<NoWarn>CS0618</NoWarn>` in `OASIS.WebAPI.csproj` is scoped to this
window and disappears in the same commit B2 deletes the fields.

`Quest.CreatedDate` **stays** — it is the definition's birthdate, not a
runtime artifact.

### 2.3 Fork semantics

A run is **forkable while `Running`** (only):

1. New `QuestRun` is created with
   `ParentRunId = parent.Id`, `ForkedAtNodeId = nodeId`,
   `Status = Pending`.
2. `QuestNodeExecution` rows for nodes with
   `ExecutionOrder < forkPoint` are **referenced** from the new run via
   a SurrealDB `RELATE` edge (`executes`), not copied. No recompute.
3. Parent run transitions `Running → Forked` (terminal).
4. Parent's in-flight `QuestNodeExecution` rows transition to
   `Cancelled`.

A `Succeeded` run is **not** forkable. Re-running a `Succeeded` quest
creates a **new root** `QuestRun` with `ParentRunId = null` —
re-execution and forking are distinct primitives.

"Mark a branch failed" is just `Running → Failed` with `FailReason` set
by the caller. Same shape as a normal failure; the audit field
distinguishes supervisor-driven failure from internal error.

### 2.4 Intra-iteration vs inter-iteration

| Layer | Graph kind | Validator |
| --- | --- | --- |
| Intra-iteration (one run's node DAG) | Strict DAG, no cycles | `QuestDagValidator` (unchanged) |
| Inter-iteration (lineage across runs) | Tree (forks branch, never merge) | None — structurally a tree, can't cycle |

`QuestDagValidator` stays the single authority for intra-iteration
acyclicity. Lineage acyclicity is structural: `ParentRunId` is set once,
at creation, pointing to a pre-existing run. The lineage walk is
guaranteed to terminate at a root by construction; no validator needed.

A unit test (B2, plan task 14) names this invariant explicitly:
> "lineage tree is not validated for acyclicity here".

## 3. Hand-off to `surrealdb-migration`

This track produces [`SURREAL-SCHEMA-HINTS.md`](SURREAL-SCHEMA-HINTS.md)
— the exact SurrealDB tables and `RELATE` edges that
`surrealdb-migration` plan tasks 3 (quest portion) and 9 will
materialize verbatim. The doc is consumed at schema-write time; no .NET
code in this track touches SurrealDB.

Tables produced: `quest`, `quest_node`, `quest_edge`, `quest_run`,
`quest_node_execution`. RELATE edges: `forked_from` (quest_run →
quest_run), `executes` (quest_run → quest_node_execution).

## 4. Non-goals (explicit — guard against scope creep)

These are deliberately **out of scope** for this track. They were
considered when reading the broader "MetaNode / HolonDAG / time-sliced
ledger" external analysis and explicitly deferred:

| Non-goal | Why deferred |
| --- | --- |
| **MetaNode / HolonDAG rebuild** | `QuestTemplate` (`Models/Quest/QuestTemplate.cs`) + `Services/Quest/QuestInstantiator.cs` already serve the templated-DAG role. Renaming is API+SDK churn for zero behavior change. |
| **Time-sliced ledger** | Out of band for this seam. A run's `StartedAt`/`EndedAt` + per-node `StartedAt`/`EndedAt` already give point-in-time reconstruction. A separate ledger is an ops/observability track if it ever materializes. |
| **Inter-iteration feedback cycles** | Lineage is a **tree**. Cycles across runs would only matter for a feedback-loop orchestrator — no current use case requires it. If a future need arises, a separate track adds a different edge type with its own validator. |
| **Snapshot pruning / state explosion** | A run is cheap (one row + N node-execution rows). Volume mitigation is a post-launch ops concern, not a model-layer concern. |
| **Automatic fork compensation** | Forking **records intent** (`ForkedAtNodeId`, `ForkReason`, parent transition to `Forked`). What to undo on the parent branch is the saga's job — see [`durable-saga-orchestration`](../durable-saga-orchestration/). This track is intentionally decoupled from compensation. |
| **Merge-of-forks** | The lineage tree has no merge operation. If two forked branches both succeed, they remain independent histories. A merge primitive would require its own conflict-resolution semantics — separate track. |
| **Cycles-across-iterations framing from the external analysis** | Not introduced here. The analysis's "intra-iteration acyclic / inter-iteration cyclic" distinction is acknowledged but lineage-as-tree is sufficient for re-run + fork; cycles would only matter for orchestrator feedback loops (out of scope). |
| **Persistence engine work** | This track ships against the existing per-aggregate store seam (`Interfaces/Stores/IQuest*Store.cs`). SurrealDB write-through happens later in `surrealdb-migration`, consuming our schema-hints doc. |

## 5. Accepted pieces of the external "MetaNode" analysis

For clarity, the parts of the broader analysis we **do** adopt:

- **Definition/runtime split** — the central insight; this entire ADR.
- **Lineage as first-class** — `ParentRunId` + `ForkedAtNodeId` +
  `ForkReason` enable forensic audit of why a fork happened.
- **Per-attempt audit preservation** — failed branches survive as
  first-class rows, never overwritten by re-runs.
- **Single authoritative intra-iteration validator** — keeps the
  `QuestDagValidator` single-source-of-truth invariant established by
  [`architecture-decoupling`](../architecture-decoupling/).

## 6. Acceptance (this track)

- `QuestRun` + `QuestNodeExecution` models exist (tasks 3–4).
- `QuestRunStatus` + extended `QuestNodeState` exist (task 5).
- `Quest.Status` / `Quest.CompletedDate` / `QuestNode.State|Output|Error`
  annotated `[Obsolete]` with a `<NoWarn>CS0618</NoWarn>` window (task 6).
- `IQuestRunStore` + `IQuestNodeExecutionStore` defined; InMemory adapters
  implemented; EF adapters stubbed and marked `[Obsolete]` (tasks 7–8).
- DI registrations land in `Program.cs` in a clearly-delimited block (task 9).
- `SURREAL-SCHEMA-HINTS.md` published (task 2) — referenced from
  `surrealdb-migration` plan tasks 3 and 9.

## 7. Acceptance (downstream, owned by B2 tasks 10–16)

- `IQuestManager.ExecuteAsync` creates a `QuestRun`, returns its `Id`.
- `QuestManager` reads/writes `QuestNodeExecution` keyed by
  `(runId, nodeId)`. No mutation of `QuestNode`.
- `IQuestManager.ForkAsync(runId, atNodeId, reason)` exists with
  state-machine guards (parent must be `Running`; `atNodeId` must belong
  to the quest definition).
- `IQuestManager.MarkRunFailedAsync(runId, reason)` exists.
- Obsolete fields physically removed; `<NoWarn>CS0618</NoWarn>` window
  removed.
- 537+ unit tests still green; new tests covering re-run, fork happy
  path, fork state-machine guards, lineage query.
