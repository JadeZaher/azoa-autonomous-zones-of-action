---
type: implementation
status: retired; source replacement accepted; runtime verification pending
---

# Retired person-operation claim writer waiver

The former conditional-UPSERT/database-clock single-statement waiver is retired. SurrealPersonOperationClaimStore now uses typed deterministic Create/UpdateOnly writes with Version CAS inside PersonOperationClaimFence's atomic database-time and expected-state guard. The coordinator independently accepted the bounded source replacement, including literal NULL rejection, absent-Version adoption, immutable cutoff and zero-CAS refusal.

The accepted design and test inventory are in [the typed claim amendment](round2-person-claim-typed-amendment.md). Final integrated schema/build/persistence verification is still mandatory and has not run. Retirement removes the source escape hatch; it does not claim runtime proof.
