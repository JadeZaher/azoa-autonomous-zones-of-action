# Managers — design notes

## §skip-semantics — QuestManager.ExecuteAsync skip loop

The in-process executor walks nodes in topological order and skips a node when
any incoming edge qualifies:

| Edge type    | Skip condition (post quest-dag-semantic-hardening FR-1) |
|---|---|
| `Control`    | source state is `Failed` **or `Skipped`** |
| `Conditional`| source state is `Failed` **or `Skipped`**, regardless of `Condition` text |

Prior to FR-1 the rules were weaker: Control skipped only on `Failed`; Conditional
required `!string.IsNullOrEmpty(Condition)` (so empty-condition edges were inert,
letting downstream nodes run through a failed gate). The P0 hazard: a payout chain
behind a failed eligibility GateCheck would run from the second hop onward.

The `HIGH#7 G2` `expectedState: QuestNodeState.Pending` guard on the skip
update-row call is unchanged — it prevents a concurrent `ForkAsync` cancellation
from being silently overwritten.

The **durable engine** (`Services/Quest/Workflow/`) has no equivalent skip seam —
see `Services/Quest/Workflow/AGENTS.md §skip-semantics`.

## §publish-lifecycle — Quest definition status

A Quest definition carries a `Status` field (`QuestStatus`, default `Draft`)
introduced by `quest-dag-semantic-hardening` FR-2. The lifecycle:

```
Draft ──publish──▶ Active ──unpublish──▶ Draft
```

- **Publish** (`PublishAsync`): runs the full validation stack (structural DAG +
  engine-profile fan-out check as error + per-node config strict round-trip), then
  persists fresh `ExecutionOrder` and flips `Draft → Active`.
- **Unpublish** (`UnpublishAsync`): refused while any `QuestRun` for the quest is
  non-final; otherwise flips `Active → Draft`.
- **Execute / StartWorkflowRunAsync**: require `Status == Active`; error message
  names the publish requirement explicitly.
- **Mutation endpoints** (`AddNodeAsync`, `UpdateNodeAsync`, `DeleteNodeAsync`,
  `AddEdgeAsync`, `RemoveEdgeAsync`): rejected when `Status == Active`; caller
  must unpublish first.

Execute-time re-validation is kept as defence-in-depth (does not replace the
publish gate; both run independently).

## §holon-parent-cycle — HolonManager parent-cycle guard

`EnsureNotDescendantAsync(holonId, proposedParentId)` is a shared private helper
introduced by `quest-dag-semantic-hardening` FR-6. It:

1. Rejects self-parent (`holonId == proposedParentId`) immediately.
2. Calls `GetDescendantsAsync(holonId)` and rejects if `proposedParentId` is
   in the result set (cycle: the proposed parent is already a descendant).
3. Returns null when safe; returns an error string to surface as `IsError`.

The guard is called on every `ParentHolonId` write path:

| Method | Guard condition |
|---|---|
| `CreateAsync` | `model.ParentHolonId.HasValue` — self-parent only (new holon has no descendants yet) |
| `UpdateAsync` | `model.ParentHolonId.HasValue` — full cycle check |
| `InteractAsync` | `request.NewParentHolonId.HasValue` — full cycle check |
| `MoveSubtreeAsync` | always — full cycle check (original precedent) |

If you add a new `ParentHolonId` write path, call `EnsureNotDescendantAsync`
before persisting.
