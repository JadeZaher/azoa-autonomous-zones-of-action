---
type: doc
scope: Core/Surreal
---

# Core/Surreal — SurrealDB primitives

Storage-engine primitives that are domain-agnostic and reused across every
SurrealDB-backed store. SurrealDB is the sole storage engine.

## §transient-conflict — optimistic-concurrency retry (`SurrealTransientConflict`)

SurrealDB **3.x** (RocksDB) changed how it handles concurrent writers that
contend the same row under a conditional `UPDATE`. On **1.5.x** those writes
serialized transparently; on **3.x** the engine surfaces a **retryable**
`Transaction conflict: Resource busy ... this transaction can be retried`
error to the loser instead of letting the conditional predicate silently
resolve to `affected == 0`.

This is not a bug to swallow blindly — the engine is explicitly telling us to
retry. `SurrealTransientConflict` is the single shared home for that contract:

- `IsRetryableConflict(Exception)` — message-token match (`Transaction
  conflict` / `Resource busy` / `can be retried`). Message-matched because the
  client raises a plain exception type; the tokens are stable across 3.x.
- `RetryOnConflictAsync<T>(op, ct, maxRetries = 8)` — bounded retry loop with a
  small exponential-ish backoff plus per-attempt jitter to break the herd. On
  retry the **winner's** write has already landed, so a single-winner
  conditional UPDATE loser resolves cleanly to its `affected == 0` / no-op path
  (returns null / `Won == false`) rather than throwing.

**Consumers.** `Core/Idempotency/SurrealIdempotencyStore.TryClaimAsync` (the
original E3 precedent) and the saga single-winner seams in
`Services/Sagas/SurrealSagaStore` (`TryClaimDueStepAsync`, `TrySignalAsync`,
`GetDueStepIdsAsync`). See `Services/Sagas/AGENTS.md` §transient-conflict-retry
for which saga seams are wrapped and why the single-owner transition paths
(Complete / ScheduleRetry / Compensate / DeadLetter / Park) are deliberately
**not** wrapped.

**Only wrap genuinely-contended conditional UPDATEs.** A path where the caller
already holds the row's `InProgress` lease has no concurrent contender, so
wrapping it adds latency-on-error for a conflict that cannot occur.
