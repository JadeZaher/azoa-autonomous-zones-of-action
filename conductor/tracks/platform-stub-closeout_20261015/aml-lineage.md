---
type: implementation-note
---

AML evidence now carries the hosted attempt/session/verified-subject lineage.
Approval completion persists that lineage; both value gates, tenant custodial
readiness and operator review select evidence for that exact approval. Delayed
obsolete clean results and their monitors remain auditable but cannot open the
current approval. Poll fan-out preserves each local monitor's lineage.

Screening persistence uses typed reads, writes and conditional mutations. The two
retained raw calls are atomic cross-table subject erasure and bounded retention
selection plus erasure; neither is ordinary single-statement CRUD. Poll state uses
typed compare-and-set retries and unique-create conflict recovery.

Regression fixtures cover lineage propagation, separate attempt registrations on
one shared vendor monitor, poll fan-out, current hit versus obsolete clean evidence,
and manual review/readiness matching. Tests remain pending the final integrated
sweep; this note is implementation evidence, not verification approval.
