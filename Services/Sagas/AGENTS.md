---
type: doc
scope: Services/Sagas
---

# Services/Sagas — durable saga / transactional-outbox module

Reusable, domain-agnostic saga skeleton: a durable outbox (`SagaStepRecord` via
`SurrealSagaStore`), a `SagaProcessor` that claims + advances due steps, a
swappable `ISagaTrigger` (polling today, SurrealDB LIVE-query later), and the
`SagaProcessorHostedService` singleton that drives it. A consumer =
one `ISagaDefinition` + its `IStepHandler<TPayload>`s, registered in DI.

## §activation — why the processor is ENABLED by default

`SagaOptions.Enabled` defaults to **`true`** and `appsettings.json` ships it
explicitly `true`.

This reverses the original pre-launch posture (ADR-0001, now **superseded**),
which defaulted it `false` on the premise that the module had **zero
consumers**. That premise went stale the moment the **durable quest workflow
engine** (`durable-workflow-engine`) shipped: it is a real consumer.

- `QuestManager.StartWorkflowRunAsync` creates a run and enqueues its entry
  node as a saga step under `QuestWorkflowSaga.Name` (`"quest-workflow"`).
- Dispatch happens **only** in `SagaProcessorHostedService.ExecuteAsync`.
- With `Enabled=false`, that service early-returned — so a durable run
  activated, enqueued its entry node, and **never executed**. The engine was
  inert under shipped config. Unit tests missed it because they drive the node
  handler directly, bypassing the processor. Recorded as
  `[[durable-quests-inert-sagas-disabled]]`; fixed in `final-hardening-cutover`
  Phase A1.

## §boot-guard — fail fast, never ship inert again

`SagaProcessorHostedService.ExecuteAsync` opens a scope on startup and, if
`Sagas:Enabled=false` **while any `ISagaDefinition` is registered**, throws
`InvalidOperationException`. Because a `BackgroundService` that throws from
`ExecuteAsync` tears down the host (default `StopHost` behaviour), this is a
hard boot failure — an operator who disables sagas while a consumer exists gets
an immediate, named error instead of a silently-inert durable engine.

A genuinely consumer-less deployment can still run `Enabled=false` without the
guard tripping (it only fires when a consumer is present), so the disabled path
remains available for a future skeleton-only host.

## §opaque-key-encoding — why GUID-shaped keys are tagged on write

`SurrealSagaStore` tags its opaque tokens (`correlation_key`, `step_name`,
`step_idempotency_key`, `gate_id`) with an `s_` sentinel before they reach
SurrealDB, and strips it on read (`EncodeOpaqueKey`/`DecodeOpaqueKey`).

Reason: `SurrealForge.Client` type-infers string literals. A value whose string
is a canonical GUID is inlined as a `uuid` literal (`u'...'`), and a
`table:id`-shaped value as a record-link literal. A `SCHEMAFULL` field declared
`TYPE string` then rejects it — *"Couldn't coerce value for field
`correlation_key` … Expected `string` but found `u'…'`"* (and likewise for the
`saga:{corr}:{step}` idempotency key). The durable quest-workflow path hits both:
correlation key = run id (`runId.ToString()`), entry step name = a node id (both
canonical GUIDs), and the idempotency key is colon-delimited (`saga:…:…`).

The schema is C#-first (`Persistence/SurrealDb/Models/SagaSteps.cs`), and a
`string` CLR property always emits `TYPE string`; there is no per-field type
override to make the field accept `uuid`. So the fix lives entirely in this
store: the keys are opaque tokens that only this store reads back, so tagging
them is transparent to every caller (`SagaKeys` idempotency keys are already
prefixed `saga:…` and never GUID-shaped, so they are untouched). Non-GUID step
/ gate names (the generic sample saga, author-defined gates) pass through
unchanged. This surfaced only once A1 exercised the durable path end-to-end —
exactly what the integration test now guards.

## §operator-surface — dead-letter inspection, requeue, cancel (Phase-F)

`Controllers/SagaOperatorController.cs` (`/api/admin/sagas`, `Operator` policy)
is the GENERIC operator layer over the durable outbox. Three verbs, each a
single-winner conditional `UPDATE` on `SurrealSagaStore`:

- **`GET  /dead-letters?status=…&limit=…`** → `ISagaStore.ListByStatusesAsync`.
  Lists steps at rest in `{DeadLettered (default), Parked, Cancelled}`,
  newest-updated first, with the diagnosis fields (saga/correlation/step/status/
  attempt/last_error/gate). Read-only.
- **`POST /{id}/requeue`** → `RequeueStepAsync`. Conditional on
  `status INSIDE [Parked, DeadLettered]` ⇒ `Pending`, `next_run_at=now`, lease +
  gate + `dead_lettered` cleared. Idempotent (a re-requeue matches zero rows) and
  REFUSES to revive a `Cancelled`/`Completed`/`InProgress` row (404). The
  processor claims it on the next tick.
- **`POST /{id}/cancel`** → `CancelStepAsync`. Conditional on
  `status INSIDE [Pending, Parked, DeadLettered]` ⇒ `Cancelled` (terminal),
  operator reason stamped on `last_error`. Will not un-complete a `Completed`
  step nor yank a leased `InProgress` one (404). Idempotent.

### The `Cancelled` status vs `DeadLettered`

`DeadLettered` is the engine's AUTOMATIC retry-exhaustion outcome — still
requeue-able (an operator can fix the underlying cause and revive it).
`Cancelled` is an EXPLICIT human give-up: the requeue op refuses to revive it,
so it is the terminal "never run this again" verb. Both are invisible to every
processor scan (neither is `Pending` nor timer-`Parked`). `Cancelled` was added
to the `StepStatus` enum + POCO `[Inside]` set in Phase-F; the `.surql` was
REGENERATED from the POCO (never hand-edited — see `../../Persistence/SurrealDb/CONVENTION.md`).

### Relationship to the B4 quest reconcile sweep (do not duplicate)

`QuestController` already exposes `runs/{id}/reconcile` + `runs/reconcile-sweep`
(also `Operator`-guarded). Those are QUEST-DOMAIN-SPECIFIC: they probe *chain
truth* for a run parked in `AwaitingReconciliation` and branch
advance-reconciled / retry / park — a higher-level, chain-aware decision that
prevents double-mint on the Grant-bounty path. This saga operator surface is the
LOWER-LEVEL primitive beneath it: it knows nothing about chains or quests, only
about generic outbox steps (park/dead-letter/requeue/cancel). A quest that
dead-letters at the saga layer surfaces here for a raw requeue/cancel; a quest
parked on `AwaitingReconciliation` at the DOMAIN layer is resolved by the quest
reconcile endpoints (which never re-broadcast). They complement — the quest
endpoints own chain-truth reconciliation, this surface owns generic step
lifecycle recovery. Neither calls the other.

## §phase-F-closeout — track status

`durable-saga-orchestration` (folded into `final-hardening-cutover` Phase F):
**skeleton delivered + operator surface added; bridge adoption intentionally not
pursued.** Phase 1 (the reusable saga skeleton) shipped with a live consumer
(the durable quest workflow — the A1 fix made it active). Phase 2
(bridge-as-saga-consumer) was DROPPED: the bridge was hardened directly in
`final-hardening-cutover` Phase B, making the saga rewrite redundant (recorded
decision). This §operator-surface is the remaining F1 deliverable.

## §direct-invoke — tests and ops

The scoped `ISagaProcessor.ProcessDueStepsAsync` can always be invoked directly
(a fresh DI scope), independent of the hosted loop. This is the deterministic
tick used by the A1 integration test
(`QuestWorkflowDurableExecutionIntegrationTests`): rather than sleeping for a
poll interval, the test resolves `ISagaProcessor` and drains due steps
synchronously, then asserts the run advanced past its entry node.

## §transient-conflict-retry — SurrealDB 3.x concurrent-claim safety

Now that the processor is **live** (A1 set `Sagas:Enabled=true`), concurrent
processor ticks genuinely race the same rows. On SurrealDB **3.x** (RocksDB) a
contended conditional `UPDATE` no longer serializes silently — the loser gets a
**retryable** `Transaction conflict: Resource busy` exception. Left unhandled
that would surface as an unhandled throw in `SagaProcessorHostedService`.

`SurrealSagaStore` wraps the genuinely-contended conditional-UPDATE seams in the
shared `Core/Surreal/SurrealTransientConflict.RetryOnConflictAsync` primitive
(same 8-retry bounded backoff the idempotency store's E3 precedent uses):

- **`TryClaimDueStepAsync`** — *the* single-winner primitive; N ticks race the
  exact same due step. Retry → the winner's write lands, losers' predicate
  misses (`affected == 0` → null). One winner, no throw.
- **`TrySignalAsync`** — concurrent/duplicate signals race the same parked row's
  conditional un-park. Same single-winner shape → wrapped.
- **`GetDueStepIdsAsync`** — its reclaim + fire-timers conditional UPDATEs run on
  every tick over the shared due-set; concurrent ticks can contend the same
  stale-lease / timer rows. Wrapped so the batch scan never throws.

**Deliberately NOT wrapped** (single-owner paths — the caller already holds the
row's `InProgress` lease, so there is no concurrent contender):
`CompleteStepAsync`, `ScheduleRetryAsync`, `CompensateStepAsync`,
`DeadLetterStepAsync`, `ParkStepAsync`. Wrapping them would add
retry-on-error latency for a conflict that cannot occur. The operator surface
(`RequeueStepAsync` / `CancelStepAsync`) is left unwrapped too — single, rare
human-invoked admin actions, not a hot processor loop.

The proof is the integration test
`SurrealSagaStoreTests.TryClaimDueStep_Concurrent_ExactlyOneWins_NoConflictThrown`
(8 concurrent claims on one due step → exactly one winner, zero thrown
conflicts), mirroring the idempotency store's `TryClaim_Concurrent_ExactlyOneWins`.
