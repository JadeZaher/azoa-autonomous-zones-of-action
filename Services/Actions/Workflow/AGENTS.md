# Action workflow recovery

Frozen input and domain correlation are persisted before handler effects. Recovery uses
that exact payload; a transaction hash alone cannot prove the promised effect or network.
Legacy Running rows without correlation require audited operator recovery.

The DAG scheduler reads every predecessor before admitting a node, uses version CAS for
Skipped/Failed outcomes, and deterministic saga identity for all ready successors.
Control and Conditional edges suppress targets after unsuccessful predecessors;
OnFailure targets require at least one failed source. Conditional text uses the shared
predicate evaluator and run/upstream scopes. Predicate errors are failed outcomes.
A run succeeds only after the entire frozen graph is terminal, every failure has a
recovery arm, and manual gates have been explicitly released. Unhandled failure remains
owned by saga compensation rather than being overwritten with an optimistic success.

ManualAdvanceReleased is persisted and monotonic. Parallel branches cannot bypass a
manual gate, and repeated release cannot enqueue duplicate successors. Timer deadlines
are persisted once; timer resumption never rearms the original wait. Frozen deployment
internal dependencies wait for exact prior occurrence nodes and skip after failed proof.

Successor admission uses deterministic run/node identity and an immutable payload hash
separate from the payload signaling may update. Source access, quota reservation,
frozen run, node executions, and first step are admitted atomically.

Runtime evidence remains pending the final integrated sweep. Legacy frontdoor migration,
remaining effect adapters, and operator recovery are active Round 2 implementation work.
