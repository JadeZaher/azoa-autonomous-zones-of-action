# Quest Temporal / Forkable DAG Model — Plan

## Tasks

### Design
1. [ ] Write `ADR.md` — definition/runtime split, fork semantics, explicit non-goals (no MetaNode rebuild, no inter-iteration cycles, no auto-compensation, no merge-of-forks). Reference the pasted external analysis and call out which pieces are accepted vs deferred
2. [ ] Write `SURREAL-SCHEMA-HINTS.md` — exact tables + `RELATE` edges that `surrealdb-migration` task 9 will materialize (`quest`, `quest_node`, `quest_edge`, `quest_run`, `quest_node_execution`, `forked_from`, `executes`). One file, no code

### Domain models
3. [ ] Add `Models/Quest/QuestRun.cs` (`Id`, `QuestId`, `AvatarId`, `Status` enum, `StartedAt`, `EndedAt?`, `ParentRunId?`, `ForkedAtNodeId?`, `ForkReason?`, `FailReason?`)
4. [ ] Add `Models/Quest/QuestNodeExecution.cs` (`Id`, `RunId`, `NodeId`, `State`, `Output?`, `Error?`, `StartedAt`, `EndedAt?`)
5. [ ] Extend `QuestStatus`/`QuestNodeState` only if a new value is required (`Forked`, `Cancelled`); reuse otherwise. Add `QuestRunStatus` enum to keep run vs definition status distinct
6. [ ] Remove `Quest.Status`, `Quest.CompletedDate`, `QuestNode.State`, `QuestNode.Output`, `QuestNode.Error` — definitions are immutable in-flight state. Keep `Quest.CreatedDate` (definition birthdate)

### Store seam
7. [ ] Add `IQuestRunStore` (CRUD over `QuestRun`, query by `QuestId`/`AvatarId`/`Status`, get-lineage). Add `IQuestNodeExecutionStore` (CRUD per `(RunId, NodeId)`, claim-pending conditional update preserving [[api-safety-hardening]] G2 semantics)
8. [ ] InMemory adapter for both; EF adapter wired but marked `[Obsolete("removed by surrealdb-migration")]` to avoid spending design time on Postgres mappings that get deleted
9. [ ] Register in DI (`Program.cs`)

### QuestManager rewrite (data plane only)
10. [ ] `IQuestManager.ExecuteAsync` now creates a `QuestRun`, returns its `Id`; existing call sites pass through unchanged
11. [ ] `QuestManager` reads/writes `QuestNodeExecution` keyed by `(runId, nodeId)`. Per-node handlers (`IQuestNodeHandler`) take a `QuestNodeExecutionContext` (carries `runId`, `nodeId`, `quest`, `inputs`). No mutation of `QuestNode`
12. [ ] Add `IQuestManager.ForkAsync(runId, atNodeId, reason)` — validates parent is `Running`, validates `atNodeId` belongs to the same quest definition, creates new `QuestRun` with lineage fields set, copies node-execution refs for `ExecutionOrder < forkPoint`, transitions parent `Running → Forked`, cancels parent's in-flight executions
13. [ ] Add `IQuestManager.MarkRunFailedAsync(runId, reason)` — supervisor-driven fail path (distinct from internal error path via `FailReason` audit field)

### Validator
14. [ ] No change to `QuestDagValidator` — it still validates a single quest definition (intra-iteration acyclicity). Add a unit test naming the invariant: "lineage tree is not validated for acyclicity here"

### Test port
15. [ ] Port `QuestManagerExecutionOrderTests` + `QuestNodeHandlerTests` to the new `(runId, nodeId)` shape; assert handler writes land on `QuestNodeExecution`
16. [ ] New tests:
       - re-running a `Succeeded` quest creates a new root `Run` (not a fork)
       - forking a `Running` quest produces a new `Run` with correct lineage + cancelled parent in-flight nodes
       - forking a non-`Running` run returns an error (state-machine guard)
       - forking at a node not in the quest definition returns an error
       - lineage query returns parent chain in order
       - existing 537+ unit tests still green

### Integration with surrealdb-migration
17. [ ] Confirm `SURREAL-SCHEMA-HINTS.md` is referenced from
       `tracks/surrealdb-migration/plan.md` tasks 3 and 9; coordinate with whichever ultrapilot/agent is implementing the SurrealDB schema to use those tables verbatim
18. [ ] Sign-off: ADR merged, schema-hints doc merged, all tests green, `dotnet build` zero warnings, [[surrealdb-migration]] task 3/9 unblocked
