# Services/Quest/Workflow — design notes

## §skip-semantics — durable path divergence from legacy executor

**Legacy executor** (`QuestManager.ExecuteAsync`): runs nodes in topological order
and performs skip propagation inline before each node executes. From
`quest-dag-semantic-hardening` (FR-1) the rule is:

- **Control edge**: skip target when ANY source is `Failed` **or `Skipped``.
- **Conditional edge**: skip target when ANY source is `Failed` or `Skipped`,
  **regardless of `Condition` text** (the prior `!IsNullOrEmpty` guard is removed).

**Durable engine** (`QuestNodeStepHandler` + `QuestWorkflowEdges`): advances one
node at a time via saga steps, using `ResolveSingleSuccessor` (Control-only forward
hops). There is **no skip seam** on the durable path — the engine only follows
Control successors and expresses "stop" as `SuccessorKind.Terminal`. Failure on
the durable path surfaces as saga retry / compensation via `QuestCompensateStepHandler`,
not skip propagation.

**This is an intentional, documented divergence** (AC-1c / spec FR-1d).
- Skip semantics are only meaningful in the legacy in-process executor where all
  nodes execute in one call.
- The durable path is node-by-node and uses saga compensation for failure recovery.
- Aligning the two paths would require a skip seam in `QuestNodeStepHandler`
  (reading all predecessor execution states before self-advancing), which is a
  separate track (`durable-skip-propagation` — named follow-up, not in scope here).

The fan-out guard (`SuccessorKind.FanOut`) in `ResolveSingleSuccessor` is the
durable engine's existing hard-reject for >1 outgoing Control edge (see FR-3).
