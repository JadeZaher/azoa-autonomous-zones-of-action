# Durable Workflow Engine — Specification

## Status
Decision record + track. **Tier 0.5/1 — the CENTERPIECE enabler** of the
`workflow-engine` initiative (Track 2 of 4: value-path-wiring → **durable-workflow-engine**
→ economic-primitive-nodes → workflow-sdk). Direction sourced from
`conductor/REVIEW-economic-substrate-2026-06-16.md` **Part C** (Quest execution-model
gap analysis). Nothing in this track is implemented yet.

## Goal
Make the Quest DAG a **durable, step-addressable, consumer-driven workflow
engine** so an external consumer (ArdaNova) can push an actor (player / user /
tenant) through a multi-phase process it *designed once* (a `QuestTemplate`) and
runs many actors through. A run must be able to **PAUSE between nodes**, survive
a process restart, and be **advanced externally** — the user's literal pseudocode
`quest(holonStep1).step(holonStep2B)`.

The engine supports a **HYBRID** advancement model (confirmed with the user):

1. **Consumer-driven step advance** — a run suspends after a node; the consumer
   calls `advance(runId, fromNodeId)` (via SDK, Track 4) to push the actor into
   the next phase. This is the literal `step(...)` primitive.
2. **Engine-driven with gate/wait suspension** — the engine auto-advances
   through the DAG but **PARKS at explicit gate / wait nodes** until a
   `signal(runId, gateId, payload)` arrives or a timer fires (the existing saga
   trigger).

A workflow author picks, **per-edge / per-node**, whether the next hop is
`manual-advance`, `auto`, or `gated` (signal/timer). Both modes coexist in one
run.

The worked example this engine must support **generically** (the economics stay
in ArdaNova; OASIS supplies only the mechanism):

> platform-token → project-token swap → **HOLD until** (phase-met │
> project-cancelled) → **on-cancel** refund platform tokens (compensation) →
> **on-continue** grant equity → equity used to pay freelancers, or swapped →
> platform → fiat.

The **HOLD-until-or-compensate** and the **branch** are the load-bearing engine
capabilities this track delivers. The swap / grant / transfer *nodes* are the
`economic-primitive-nodes` track.

## Background — the core problem (file:line evidence)

`QuestManager.ExecuteAsync` runs the **entire DAG synchronously in one HTTP
call** and **cannot pause between nodes**:

- The run is created, every node's `QuestNodeExecution` is pre-created Pending
  (`QuestManager.cs:216-242`), then a single `foreach` walks
  `quest.Nodes.OrderBy(n => n.ExecutionOrder)` to completion in-process
  (`QuestManager.cs:251-369`). The method returns only once every node is
  terminal (`QuestManager.cs:371-379`). There is **no suspension point** — no
  way to express "vest over steps", "HOLD until settlement", or "wait for an
  external phase-met signal".
- **Edge conditions are dead data.** `QuestEdge.Condition` is a `string?`
  (`QuestEdge.cs:13-16`) that is stored and round-tripped but **only read as a
  presence flag** for failed-predecessor skipping
  (`QuestManager.cs:266: if (edge.EdgeType == Conditional && !string.IsNullOrEmpty(edge.Condition))`).
  It is never parsed or evaluated. `Condition` node handler is a no-op
  (REVIEW Part C #1).
- **Supervisor hooks anticipate externally-driven runs but not per-node
  resume.** `MarkRunCompletedAsync` (`QuestManager.cs:1138`) and
  `MarkRunFailedAsync` (`QuestManager.cs:575`) let an *external* supervisor
  close a run, and `ForkAsync` (`QuestManager.cs:465`) already cancels in-flight
  node executions — but there is **no "advance run from node N" resume API**.
- `QuestRunStatus` has no suspended state: `Pending / Running / Succeeded /
  Failed / Forked / Cancelled` (`QuestRunStatus.cs:15-38`).

REVIEW Part C #3 names the fix exactly: *"a node that suspends the run + a
per-node advance run from node N resume API … The pending durable-saga
track is the natural home — the SAME suspendable/durable engine that Part-A
M2 (multi-step allocation compensation) wants."*

## The headline architectural decision — build ON the saga layer (see plan.md D1)

**Recommendation: build the durable/suspendable engine ON TOP OF the existing
saga layer, NOT a new orchestration runtime.** A durable Quest run maps onto a
saga instance with near-zero new orchestration vocabulary:

| Workflow concept | Saga concept (already built) |
|---|---|
| A durable Quest run | A saga instance (one `CorrelationKey`) |
| A `QuestNode` | A forward `ISagaStep` |
| Node advancement | Step dispatch (`SagaProcessor.OnStepSucceededAsync`) |
| A gate / wait node | A step that **PARKS** until a signal / timer |
| Refund-on-cancel | A declared **compensation step** |
| Exactly-once advance | The G2 conditional-claim (`TryClaimDueStepAsync`) |
| Crash-safe resume | Lease reclaim of an `InProgress` row |

This is not speculative: `durable-saga-orchestration/spec.md:14` already **names
"quest step dispatch"** as an intended saga consumer. This track *realizes* that
consumer.

**Current real state of the saga layer (verified, file:line):** the saga
*skeleton* is **fully implemented and DI-wired** — it is NOT a stub, despite
`durable-saga-orchestration` still showing `[ ]` Pending in `tracks.md:41`
(the skeleton was delivered by `surrealdb-migration` wave-2; the track's
*remaining* Phase 2/3 work — bridge adoption, generalization — is what is
pending). Concretely present and registered:

- `ISagaContracts.cs` — `ISagaDefinition` (named ordered forward steps +
  per-step `CompensationStepName`), `IStepHandler<TPayload>`, `ISagaStep`.
- `ISagaCoordinator.cs` — `StartAsync<TPayload>(sagaName, correlationKey, payload)`
  + concrete `SagaCoordinator` enqueuing the first forward step.
- `ISagaStore.cs` + `SurrealSagaStore.cs` — durable store against the
  `saga_steps` SurrealDB table (DDL committed at
  `Persistence/SurrealDb/Generated/Schemas/saga_steps.surql`); G2 single-winner
  `TryClaimDueStepAsync`, lease-reclaim `GetDueStepIdsAsync`, `CompleteStepAsync`,
  `ScheduleRetryAsync`, `CompensateStepAsync`, `DeadLetterStepAsync`,
  `EnqueueNextStepAsync`.
- `SagaProcessor.cs` — claim → dispatch handler → advance / retry-backoff /
  compensate / dead-letter; crash-safe re-entry.
- `PollingSagaTrigger.cs` + `SagaProcessorHostedService.cs` — the pluggable
  trigger (polling now → SurrealDB LIVE later).
- `SagaStatus.cs` — `SagaStatus { Running, Completed, Compensated, DeadLettered }`
  and `StepStatus { Pending, InProgress, Completed, Compensating, DeadLettered }`.
- All registered in `Program.cs:539-556`.

**The one capability the saga layer does NOT yet have — and this track must add
it:** a step cannot **PARK on an external signal/timer**. `StepStatus` has no
`Parked` / `AwaitingSignal` member, and `SagaProcessor` only ever moves a step
`Pending → InProgress → Completed | Compensating | DeadLettered` — there is no
"this step is waiting for `signal(...)`" state and no API to deliver that signal.
**No saga consumer (`ISagaDefinition`) is registered in DI today** — the bridge
adoption (saga Phase 2) has not landed — so this track is the *first* real saga
consumer **and** must extend the saga step-machine with suspend-and-signal.

**Dependency resolution (D1 detail):** this track has a **SOFT** dependency on
`durable-saga-orchestration` (the foundation it needs is already shipped) and a
**HARD** obligation to advance the saga step-machine's *suspend/signal*
capability — which this track scopes and delivers, because no other track owns
it. We do **not** wait for the rest of `durable-saga-orchestration` (bridge
adoption / generalization) to land.

## Scope `[ ]`

- `[ ]` **Run lifecycle = durable + suspendable.** Extend `QuestRunStatus`
  (`QuestRunStatus.cs`) with `Suspended`, `AwaitingSignal`, `AwaitingTimer`. A
  suspended run persists a **resume point** (which node, what it is waiting on,
  and the next-hop semantics). Survives process restart (durable via the
  `saga_steps` row + the `QuestRun` row; no in-memory continuation).
- `[ ]` **Suspend/signal in the saga step-machine.** Add a `Parked` (a.k.a.
  `AwaitingSignal`) `StepStatus` and a `SignalAsync(correlationKey, gateId,
  payload)` path that atomically un-parks a parked step (conditional UPDATE,
  G2 discipline) so the processor resumes it on the next tick. A timer/wait node
  is a parked step whose `NextRunAt` is set forward; the existing
  `GetDueStepIdsAsync` fires it. **This is the saga-layer extension this track
  owns** (see D1, D2). No new orchestration runtime.
- `[ ]` **Step-addressable advancement API** on `QuestController`:
  - `advance(runId, fromNodeId)` — the `step(nodeId)` primitive: resume a
    manual-advance run from a specific node into its successor(s).
  - `signal(runId, gateId, payload)` — deliver an external signal to a parked
    gate node.
  - a wait/timer node fires via the existing saga trigger (no new endpoint).
  These are distinct from the all-at-once `ExecuteAsync` — **decide** whether to
  keep `ExecuteAsync` for fully-`auto` DAGs or deprecate it (D3). The
  `MarkRunCompleted` / `MarkRunFailed` supervisor hooks (`QuestManager.cs:1138`,
  `:575`) are the precedent for externally-driven runs and are reused, not
  rebuilt.
- `[ ]` **Edge/advancement semantics.** A node (or its outgoing edge) declares
  whether progression is `manual-advance`, `auto`, or `gated` (signal/timer).
  Reuse `QuestEdge` (`SourceNodeId` / `TargetNodeId` / `Condition` / `EdgeType`,
  `QuestEdge.cs`). This track makes **suspension / advancement** real; it does
  **NOT** build the predicate evaluator inside a gate node — that is the
  `economic-primitive-nodes` track's `GateCheck` handler (cross-referenced;
  REVIEW Part C #1).
- `[ ]` **Consumer-designed shapes via templates.** Runs are instantiated from a
  `QuestTemplate` (`QuestInstantiator` + `{{param}}` substitution already exist —
  REVIEW Part C "Already there"). ArdaNova designs the workflow shape once
  (template), parameterizes per-actor, and runs many actors through the durable
  engine. The HOLD-until-or-compensate and the on-cancel/on-continue branch are
  the load-bearing capabilities this track proves end-to-end.
- `[ ]` **Exactly-once + crash-safe.** Every advancement claims via the existing
  G2 conditional-UPDATE / idempotency primitive (`QuestManager` already uses
  `TryClaimPendingAsync`, `QuestManager.cs:304`; the saga store uses
  `TryClaimDueStepAsync`). A crash mid-advance leaves the run **resumable, not
  poisoned**. This also absorbs REVIEW Part-A **M2** (multi-step allocation
  compensation = a declared saga compensation step).
- `[ ]` **Cross-tenant / ownership.** Runs are avatar-scoped via the existing
  `GetAvatarIdFromClaims` + `LoadOwnedRunAsync` precedent
  (`QuestManager.cs:467`). A tenant advancing a child's run uses the
  `tenant-onboarding` authorization. Integrate the existing auth — do **not**
  rebuild it.

## Acceptance (house rules)

- `[ ]` `QuestRunStatus` extended with `Suspended` / `AwaitingSignal` /
  `AwaitingTimer`; persisted (SurrealDB sole engine; schema regen if a typed
  column changes).
- `[ ]` `advance(runId, fromNodeId)` and `signal(runId, gateId, payload)`
  endpoints exist on `QuestController`, avatar-scoped, and appear in **Swagger**.
- `[ ]` The saga step-machine gains a **parked/awaiting** state and a signal path
  with the same G2 single-winner discipline (no double-resume under concurrent
  signals).
- `[ ]` A test proves a run: **SUSPENDS** at a gate node → **SURVIVES** a
  simulated process restart (drop in-memory state; re-resolve from the durable
  rows) → **RESUMES** on `signal(...)` → and runs a **compensation step on
  cancel** (the refund-on-cancel of the worked example).
- `[ ]` Zero-warning `dotnet build`.
- `[ ]` `dotnet test` green, including: all existing **quest** tests, all
  **`api-safety-hardening`** safety tests (exactly-once / replay /
  reconciliation), and the new suspend/restart/resume/compensate test.
- `[ ]` Commits follow `[durable-workflow-engine] <verb> <subject>`.

## Out of scope (sibling tracks — referenced as deps/dependents)

- **The generic economic node handlers** — `GateCheck` predicate evaluation,
  `Swap` / `Hold` / `Transfer` / `Grant` / `Refund` — are the
  **`economic-primitive-nodes`** track. This track builds the **SUSPEND /
  RESUME machinery + signal plumbing**; the predicate evaluator and the
  value-moving handlers are *that* track. (REVIEW Part C #1, #2.)
- **The TS SDK `quest()` / `step()` / `signal()` surface** is the
  **`workflow-sdk`** track (Track 4). This track exposes the HTTP endpoints the
  SDK wraps.
- **Value-path broadcast / custody fixes** (REVIEW Part-A C1/C2/H1–H4) are the
  **`value-path-wiring`** track (Track 1). This engine **DEPENDS** on it: a
  workflow node that moves value needs real signing + real broadcast, else a
  "grant equity" node silent-no-ops (REVIEW C2). The engine's suspend/resume is
  buildable and testable *before* value-path lands (using mechanism-only / mock
  nodes), but a *real* economic run requires Track 1.
- **The ArdaNova economic semantics** (what a project-token is, vesting math,
  equity rules) stay entirely in ArdaNova. OASIS supplies mechanism only.
- **No broker / external infra.** Same homebake stance as
  `durable-saga-orchestration` (poll now → SurrealDB LIVE later).
- **No fork-merge, no inter-iteration cycles** — inherited non-goals from
  `quest-temporal-fork-model`.

## Tier
**Tier 0.5 / 1** — the centerpiece enabler. It unblocks the economic workflow
substrate (REVIEW Synthesis: *"the single highest-leverage build … a
durable/suspendable execution engine"*) and is simultaneously the home for
Part-A M2 (allocation compensation).

## Dependencies

- **`quest-temporal-fork-model`** ✓ shipped — provides the definition/runtime
  split (`QuestRun` / `QuestNodeExecution`) and the G2 per-node claim this track
  resumes against.
- **`durable-saga-orchestration`** — **SOFT** dependency. The saga *foundation*
  (`Services/Sagas/*`, `SurrealSagaStore`, `saga_steps` schema, DI at
  `Program.cs:539-556`) is **already shipped** (via `surrealdb-migration`
  wave-2), so this track does **not** block on the rest of that track landing.
  This track **advances** the saga step-machine's suspend/signal capability —
  the one piece the saga layer lacks today — and is the **first real saga
  consumer**. (See D1.)
- **`value-path-wiring`** (Track 1) — **HARD** dependency for *real economic*
  runs (a value-moving node needs real signing/broadcast). The suspend/resume
  machinery itself is buildable + testable against mechanism-only nodes before
  Track 1 lands.
- **`economic-primitive-nodes`** (sibling/dependent) — owns the `GateCheck`
  predicate + value node handlers this engine's gate/wait nodes invoke. This
  track defines the suspend/resume contract those handlers plug into.
- **`workflow-sdk`** (Track 4, dependent) — wraps the advancement endpoints.

## House rules (carried into Acceptance)
Zero-warning build; `dotnet test` green incl. the suspend→restart→resume→
compensate test; existing quest + `api-safety-hardening` tests stay green;
SurrealDB sole engine; new `QuestRunStatus` values persisted; Swagger lists the
new advancement endpoints; one commit per `[durable-workflow-engine] <verb>
<subject>`.
