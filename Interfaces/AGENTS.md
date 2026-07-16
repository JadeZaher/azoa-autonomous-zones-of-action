# Interfaces — contract documentation

Interfaces are the authoritative home for XML API contracts. Document parameter
identity, idempotency/concurrency semantics, result meaning, and cancellation on
the interface member. Implementations use `/// <inheritdoc/>`; do not duplicate
contract prose that will drift.

Use `<inheritdoc cref="..."/>` only when a member intentionally implements or
adapts a differently named contract. Public implementations may add one terse
local note for implementation-specific behavior, with broader rationale in the
nearest directory `AGENTS.md`.

Reusable pure transformations belong in `Helpers/` or the owning reusable
package. A private static helper may remain beside one implementation only when
it is tiny, single-purpose, and has no second call site. On the second use, move
it instead of copying it.

## NFT transfer reservation contract

`IHolonStore` reservation methods form a conditional ownership workflow:

- reserve succeeds for one source-owned NFT and is idempotent only for the same
  settlement key and target;
- finalize moves ownership only for the matching active reservation and replays
  the same completed settlement as success;
- release clears only the matching source reservation and never moves ownership;
- `Result == false` is an expected contention/fingerprint conflict, while an
  unexpected storage failure is an exception observed at the boundary.

## Node fee settlement contract

`INodeFeeSettlementStore` persists an immutable intent plus an inert recovery
lease protocol. The deterministic identity is a hash derived from the parent
idempotency key and fee operation; exact immutable replays return the existing
row, while a changed economic decision conflicts. A recognized concurrent
duplicate create must be reread and compared, so identical decisions normalize
to replay rather than an arbitrary duplicate-key error.

Recovery is a bounded candidate scan followed by per-record conditional CAS. A claim
requires the scanned `StateVersion` and a due-or-expired lease predicate; it
issues a fresh opaque token and expiry. An exact live lease may either park a
mixed observation containing `Unknown`/`Failed` effects in
`AwaitingReconciliation`, leaving the parent `InProgress`, or supply two
distinct confirmed effect references to atomically
settle both rows. The latter is a persistence protocol only: it is not a
provider submission or worker activation seam. Stale leases, illegal state
transitions, a mismatched parent, and any terminal reversal return `false`
without mutating either paired row.
