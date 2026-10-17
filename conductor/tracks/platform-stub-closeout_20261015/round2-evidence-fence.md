---
type: implementation
---

# Subject evidence serialization

Root governance review found that locking a selected AML result did not prevent
a newer same-lineage HIT from being appended concurrently. The shared
`KycEvidenceFence` joins the existing subject/tenant attempt guard rather than
introducing a second authority record.

Submission creation, direct submission updates, hosted completion, manual review,
attempt retirement/expiry shortening, identity binding and erasure, AML result
append, subject erasure and retention erasure all write that guard in the same
transaction as their mutation. Governance writes it before selecting latest
proof. Profile and tenant selection mutations retain their separate authority
guards and governance locks their actual selected records as well.

Identity erasure compares observed membership in its conditional record update;
a concurrently appended member makes erasure conflict, requiring a new read and
all affected subject guards. Retention sweeps read bounded candidates, then use
typed per-record conditional erasure under their subject guards. Parameter names
are scoped before composing the flat transaction.

Source review and runtime verification are separate. Regression coverage is being
authored; no tests or schema generation have run in this round.
