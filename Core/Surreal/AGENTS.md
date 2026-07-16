---
type: doc
scope: Core/Surreal
---

# Core/Surreal — SurrealDB primitives

Storage-engine primitives that are domain-agnostic and reused across every
SurrealDB-backed store. SurrealDB is the sole storage engine.

## Â§scalar-string-binding

`SurrealScalarString.ToCharacters` is the temporary reusable binding primitive
for a raw SurrealQL expression that must preserve a colon-bearing scalar string.
Bind its characters and reconstruct with `array::join($_value_chars, '')` so
SurrealDB 3.x cannot reinterpret `table:id`-shaped values as record ids. Use it
only where the consumed SurrealForge package cannot supply a typed scalar-string
binding; replace it when the package exposes that primitive. Do not recreate the
character-array workaround in individual stores. It maps a missing optional
value to an empty array; callers retain optionality with a separate boolean
parameter because SurrealQL evaluates both `IF` branches.

## §runtime-identity

`SurrealRuntimeConfigurationGuard` keeps the production API on the isolated
`SurrealRuntime` configuration section. Production requires a non-root,
database-scoped user and `AZOA_SKIP_MIGRATIONS=1`; the API container cannot
receive legacy `SurrealDb` credentials or run the schema tool at boot. The
separate schema job has `SURREALFORGE_*` credentials and remains an operations
gate until its SurrealDB 3.1.4 permissions are proven live. Built-in database
`EDITOR` is not a DDL-proof role, so do not claim full DDL separation from the
config split alone; see the `surreal-runtime-least-privilege` conductor track.

## §transient-conflict — optimistic-concurrency retry (`SurrealTransientConflict`)

> **Moved to the package.** `SurrealTransientConflict` now lives in
> `SurrealForge.Client.Idempotency` (SurrealForge ≥ 0.2.0). AZOA no longer
> carries a local copy — import `SurrealForge.Client.Idempotency` and use the
> package type. The contract below is unchanged and documents why the seam
> exists; the ledger also exposes it as a config knob
> (`IdempotencyLedgerOptions.RetryOnTransientConflict`).

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
