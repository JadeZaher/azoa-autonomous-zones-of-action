# Durable Workflow Engine — Plan

Build order: **decide the saga-vs-new-engine question first (D1), extend the
saga step-machine with suspend/signal, map the Quest run onto a saga instance,
then expose the advancement API and prove it survives a restart.** Every phase
keeps the existing **quest** tests and all **`api-safety-hardening`** safety
tests green — those suites are the regression gate. Tests run **once at the end
of each phase**, not per-fix.

## Decisions

| # | Decision | Choice (recommended) | Rationale / evidence |
|---|----------|----------------------|----------------------|
| **D1** | **Build a new engine, or build on the saga layer?** (the headline) | **Build ON the saga layer.** A durable Quest run = a saga instance; node = forward step; gate/wait = a parked step; refund-on-cancel = a declared compensation step. | `durable-saga-orchestration/spec.md:14` already names "quest step dispatch" as an intended consumer. The saga skeleton is **already implemented + DI-wired** (`Services/Sagas/*`, `SurrealSagaStore`, `saga_steps` schema, `Program.cs:539-556`) — verified, not stub. Inventing a second durable runtime would duplicate the G2 claim, lease-reclaim, retry/backoff, and compensation that already exist and are SurrealDB-backed. |
| **D2** | Where does **suspend / wait-for-signal** live? | **Extend the saga step-machine** with a `Parked` (`AwaitingSignal`) `StepStatus` + a `SignalAsync(correlationKey, gateId, payload)` conditional un-park. A timer node is a parked step with `NextRunAt` set forward (fires via the existing `GetDueStepIdsAsync`). | The saga layer has **no** suspend state today (`SagaStatus.cs` `StepStatus { Pending, InProgress, Completed, Compensating, DeadLettered }` — no parked member; `SagaProcessor.cs` never parks). This is the **single capability the saga layer lacks**, and no other track owns it — so this track delivers it. |
| **D3** | Keep or deprecate the all-at-once `ExecuteAsync`? | **Keep it** for fully-`auto` DAGs (back-compat for existing quest tests); **route any DAG containing a `manual-advance` / `gated` edge through the durable engine.** Do not deprecate yet. | `ExecuteAsync` (`QuestManager.cs:198`) is exercised by existing tests and the `POST /{id}/execute` controller route. A hard cutover would churn the green suite for no benefit; a per-edge-semantics branch is additive. |
| **D4** | Hard or soft dep on `durable-saga-orchestration`? | **SOFT.** The foundation is shipped; this track advances only the suspend/signal piece and is the first real saga consumer. Do **not** wait for bridge adoption / generalization. | `tracks.md:41` marks the track `[ ]` but notes the skeleton "already delivered by surrealdb-migration wave-2". The pending work there (bridge adoption) is orthogonal to suspend/resume. |
| **D5** | Where does **per-edge advancement semantics** (`manual` / `auto` / `gated`) get stored? | On the **node `Config`** (opaque JSON, `QuestNode.cs:24`) and/or a new `QuestEdgeType` member (`Gated`) + reuse `QuestEdge.Condition` (`QuestEdge.cs:16`) as the gate id. Prefer node `Config` for the wait/gate marker; edge type for the manual-vs-auto hop. | Reuses existing shape (REVIEW Part C "templates + `{{param}}` is exactly the tenant-supplies-parameters shape"). Avoids a schema migration where the opaque `Config` bag suffices. `Condition` is currently dead data (`QuestManager.cs:266`), so repurposing it as the gate id is non-breaking. |
| **D6** | Does the **predicate evaluator** (balance≥X, KYC==approved) live here? | **No.** This track delivers SUSPEND/RESUME + signal plumbing only. The `GateCheck` handler that evaluates a tenant-supplied predicate is the `economic-primitive-nodes` track. | REVIEW Part C #1: "build a real `GateCheck` handler" is the minimal new economic piece — explicitly a *different* track. This track defines the contract a gate node plugs into. |
| **D7** | Resume-point durability source of truth? | The **`saga_steps` row** (parked step carries `CorrelationKey` = `runId`, `StepName` = nodeId, plus the next-hop semantics in `Payload`) **plus** the `QuestRun.Status` projection. No in-memory continuation. | Mirrors the saga crash-safe contract (`SagaStepRecord.cs:74-80` lease reclaim). Restart re-derives from durable rows — exactly what the acceptance restart-test asserts. |

## Phase 1 — Saga step-machine: suspend / signal / timer (the D2 extension)
1. `[x]` Add `StepStatus.Parked` (`AwaitingSignal`) to `SagaStatus.cs`; add a `GateId`/`SignalKey` column to `SagaStepRecord` + `saga_steps.surql` (schema regen, G6 `SCHEMAFULL`).
2. `[x]` `ISagaStore.ParkStepAsync(id, gateId, resumeAt?, ct)` — conditional `WHERE Status==InProgress` ⇒ `Parked`, persist gate id + optional timer `NextRunAt`; and `ISagaStore.TrySignalAsync(correlationKey, gateId, payload, ct)` — conditional `WHERE Status==Parked AND gate_id==$g` ⇒ `Pending`, `NextRunAt=now` (G2 single-winner: a duplicate signal un-parks at most once). SurrealDB impl in `SurrealSagaStore`.
3. `[x]` `StepResult.Park(gateId, resumeAt?)` so a handler can *request* suspension; `SagaProcessor.ProcessOneAsync` honors it (park instead of complete/retry). A `Parked` step is skipped by the claim scan until signalled or its timer is due (`GetDueStepIdsAsync` already filters `NextRunAt<=now`, so a timer-armed parked step becomes due naturally).
4. `[x]` Skeleton tests (saga-level, no quest yet): park → not claimed while parked; signal → resumes once even under two concurrent signals; timer-armed park → resumes when `NextRunAt` passes; restart (drop processor, re-create) → parked step still resumes. Keep all `api-safety-hardening` tests green.

## Phase 2 — Map a durable Quest run onto a saga instance (D1)
5. `[x]` Extend `QuestRunStatus.cs` with `Suspended`, `AwaitingSignal`, `AwaitingTimer`; persist (schema regen if a typed column gains values). Update the run-status derivation in `QuestManager` so a parked node projects to the right run status.
6. `[x]` `IQuestWorkflowSaga` definition: register a generic `ISagaDefinition` (the FIRST real saga consumer) whose forward steps dispatch one `QuestNode` each via the existing `QuestNodeHandlerRegistry` (`_registry.TryGet`, `QuestManager.cs:328`). The saga `CorrelationKey` = `runId`; `StepName` = the node id; the step handler runs the node and, per the node's advancement semantics (D5), either completes (auto → next), parks (gated/wait → `StepResult.Park`), or completes-and-stops (manual → run goes `Suspended`).
7. `[x]` `StartWorkflowRunAsync(questId, avatarId)` — instantiate from `QuestTemplate` if templated (reuse `QuestInstantiator`), create the `QuestRun` + per-node `QuestNodeExecution(Pending)` (reuse `QuestManager.cs:216-242`), then `ISagaCoordinator.StartAsync("quest-workflow", runId, payload)` instead of the in-process `foreach`. Reuse the G2 per-node `TryClaimPendingAsync` (`QuestManager.cs:304`) inside the step handler so the saga advance and the node claim share one exactly-once primitive.
8. `[x]` Declare the **compensation step** for the worked example: a forward "grant/allocate" step declares a `CompensationStepName` (refund) so a cancel routes through the saga's first-class compensation (`CompensateStepAsync`, `ISagaStore.cs:84`). This absorbs REVIEW Part-A **M2**.

## Phase 3 — Step-addressable advancement API (the `step(...)` / `signal(...)` primitives)
9. `[x]` `QuestManager.AdvanceAsync(runId, fromNodeId, avatarId)` — the `step(nodeId)` primitive: avatar-scoped via `LoadOwnedRunAsync` (`QuestManager.cs:467`); resumes a `Suspended` manual-advance run from `fromNodeId` into its successor(s) by enqueuing the next forward saga step. Guard: only `Suspended`/`AwaitingSignal` runs accept advance (mirror the `MarkRunCompletedAsync` state-machine guard, `QuestManager.cs:1148`).
10. `[x]` `QuestManager.SignalAsync(runId, gateId, payload, avatarId)` — delivers an external signal to a parked gate node via `ISagaStore.TrySignalAsync`; avatar-scoped; idempotent (duplicate signal un-parks at most once).
11. `[x]` `QuestController` endpoints: `POST runs/{runId}/advance` (body `{ fromNodeId }`) and `POST runs/{runId}/signal` (body `{ gateId, payload }`), both `[Authorize]`, avatar from claims (`GetAvatarIdFromClaims`), surfaced in **Swagger**. The wait/timer node needs **no** endpoint — the saga trigger fires it. Keep `POST {id}/execute` for `auto` DAGs (D3).
12. `[x]` Cross-tenant: a tenant advancing a child's run authorizes via the `tenant-onboarding` path (reuse, do not rebuild). Note the integration point in code; the actual auth is the existing seam.

## Phase 4 — Prove durability end-to-end + harden
13. `[x]` The **acceptance test**: build a 3-node template (swap → gate(HOLD) → grant, grant declares a refund compensation). Run an actor → assert run `Suspended`/`AwaitingSignal` at the gate; **simulate a restart** (dispose + recreate the processor/scope, drop all in-memory state); `signal(runId, gateId, "phase-met")` → assert it resumes and completes; in a second run, `cancel` at the gate → assert the **refund compensation step** runs (`SagaStatus.Compensated`). Mechanism-only / mock value nodes (Track 1 not required for the engine test).
14. `[x]` Branch coverage: on-cancel (compensation) vs on-continue (advance) from the same gate, proving the load-bearing branch.
15. `[x]` Concurrency: two `signal` calls race → one resume; a signal racing a timer → one resume; a crash mid-advance → resumable, not poisoned (G2 holds).

## Verification (run ONCE at the end)
16. `[x]` `dotnet build` — zero new warnings.
17. `[x]` `dotnet test` — green incl. existing quest tests, all `api-safety-hardening` safety tests, and the new suspend→restart→resume→compensate test.
18. `[x]` Swagger lists `runs/{runId}/advance` + `runs/{runId}/signal`.
19. `[x]` spec.md decisions kept current; SurrealDB sole engine; no broker / external infra introduced.

## Commit strategy
One commit per logical unit, message format **`[durable-workflow-engine] <verb> <subject>`**, e.g.:
- `[durable-workflow-engine] add Parked step status + park/signal store ops`
- `[durable-workflow-engine] add StepResult.Park and processor suspend handling`
- `[durable-workflow-engine] extend QuestRunStatus with Suspended/AwaitingSignal/AwaitingTimer`
- `[durable-workflow-engine] register quest-workflow saga definition + node-step handler`
- `[durable-workflow-engine] add advance/signal manager methods + controller endpoints`
- `[durable-workflow-engine] prove suspend→restart→resume→compensate`

Each commit leaves the build zero-warning and the existing suite green (single sweep at phase end, per the test-once policy).

## Known follow-ups (out of this track, recorded for the initiative)
- **`economic-primitive-nodes`** delivers `GateCheck` (predicate evaluation — REVIEW Part C #1) + `Swap`/`Hold`/`Transfer`/`Grant`/`Refund` node handlers (REVIEW Part C #2). This engine's gate/wait nodes invoke them via the contract defined in D6.
- **`value-path-wiring`** (Track 1) must land before a *real* economic run (a value-moving node needs real signing/broadcast — REVIEW Part-A C1/C2). The engine + its tests do not block on it (mechanism-only nodes).
- **`workflow-sdk`** (Track 4) wraps `advance` / `signal` into `quest(step1).step(step2B)` / `signal(...)`.
- **`durable-saga-orchestration`** bridge adoption (its Phase 2) remains independently pending; this track's suspend/signal extension is additive and does not block it.
- **SurrealDB LIVE-query trigger** (replaces `PollingSagaTrigger`) — converges in `surrealdb-migration`; the parked/timer model here is trigger-agnostic (the processor only ever asks "what is due?").
