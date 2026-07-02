---
type: notes
track: quest-dag-semantic-hardening
created: 2026-07-02
---

# Implementation Notes — quest-dag-semantic-hardening

## Decision log

### Phase A

**A-1 — Test scaffolding uses `Status = QuestStatus.Active` on in-memory quest**
The tests in `QuestManagerSkipPropagationTests` set `Status = Active` on the
domain `Quest` object directly (bypassing the publish flow). This is correct for
unit tests: we are testing the skip-propagation logic in `ExecuteAsync`, not the
publish gate. The publish gate tests live in Phase B.

**A-2 — `QuestManagerSkipPropagationTests` bypasses DAG validation**
Tests construct quests with correct topological order assigned manually and call
`ExecuteAsync` directly. `ExecuteAsync` runs `ValidateDAGAsync` internally which
re-validates the DAG structure. All test quests are structurally valid DAGs (no
cycles, correct entry/terminal flags). Note: Phase B adds the `Status == Active`
requirement to `ExecuteAsync`; the tests already set `Status = Active`.

**A-3 — Durable path divergence (AC-1c)**
`QuestNodeStepHandler` (durable engine) has NO skip seam. It advances via
`ResolveSingleSuccessor` (Control edges only) and handles failure via saga
retry/compensation. Aligning the two paths is a named follow-up track
(`durable-skip-propagation`). Documented in `Services/Quest/Workflow/AGENTS.md §skip-semantics`.

**A-4 — `QuestEdgeAddModel.Condition` enforcement location**
Per spec FR-1c, enforcement on the `QuestEdgeAddModel` surface is done via a new
`QuestEdgeAddModelValidator` (registered automatically by
`AddValidatorsFromAssemblyContaining<Program>()`). This mirrors the pattern of all
other model validators in `Validators/`. The manager's `AddEdgeAsync` also receives
FluentValidation errors from the framework pipeline before the method body runs,
so no second guard is needed in the manager.

### Phase B

**B-1 — `Quest.Status` reintroduced on definition**
The domain `Models/Quest/Quest.cs` gains `Status` (`QuestStatus`, default `Draft`).
The POCO `Persistence/SurrealDb/Models/Quest.cs` gains the corresponding
`[Inside(...)]` column. The `SurrealNote` on the POCO is updated to document this
as a deliberate reintroduction for the *definition* lifecycle (the quest-temporal-fork-model
removal was about *runtime* status, not definition status).

**B-2 — Schema golden regeneration**
After editing the POCO, goldens are regenerated in Phase E's `dotnet build` step
(the build runs `azoa-surreal generate-from-assembly` via the schema test project).
The schema tests (`AttributePocoByteEquivalenceTests`) are run as part of Phase E.

**B-3 — `IQuestManager` interface extensions**
`PublishAsync` and `UnpublishAsync` are added to `IQuestManager` and implemented
in `QuestManager`. They follow the `LoadOwnedQuestAsync` IDOR pattern.

**B-4 — Fan-out check placement**
`DagValidationResult.Warnings` added. The fan-out check lives in
`QuestDagValidator.Validate()` behind a `checkFanOut` parameter (default `false`
for backward compat). Callers that want it as an error pass `checkFanOut: true`;
the legacy `ValidateDAGAsync` path treats fan-out entries in `Warnings` only.

### Phase C

**C-1 — `TryDeserialize` helper location**
Added as `Services/Quest/QuestNodeConfig.cs` (static class). The helper returns
`bool` + `out T` + `out string error` pattern matching the existing codebase style.

**C-2 — Registry exhaustiveness**
The `QuestNodeConfigRegistry` maps every `QuestNodeType` value. Node types with
no config DTO (those that use a raw `IdConfig` or no-op) are registered explicitly
as config-free (null DTO type). The unit test pins all enum values.

**C-3 — GateCheckNodeHandler convergence**
`GateCheckNodeHandler` already had manual null-handling (the `?? new GateCheckNodeConfig()`
fallback). Converged onto `TryDeserialize` so behavior is consistent. The null
fallback is preserved as the helper's `defaultsOnNull` behaviour.

### Phase D

**D-1 — ScanForGuids array extension**
Extended `ScanForGuids` to collect `Guid` values from array elements under
`*holon*`-named properties. This is a targeted two-line change — no per-type
registry lookup needed (the typed registry approach would be larger and require
more coupling between `DappCompositionManager` and `QuestNodeConfigRegistry`).

**D-2 — Holon parent-cycle guard**
Extracted as `EnsureNotDescendantAsync(id, proposedParentId)` private method.
Applied in `CreateAsync`, `UpdateAsync`, `InteractAsync`, and `MoveSubtreeAsync`.
`CreateAsync` only checks when `ParentHolonId` is supplied (null parent = root,
no cycle possible).

### Phase E

**E-1 — SurrealDB availability**
If the podman `oasis-postgres` container / SurrealDB is not running at Phase E
time, integration tests are skipped. Unit tests + schema tests are run fully.
Results are recorded in this file with `[SKIPPED - SurrealDB unreachable]` tags.

## Out-of-scope follow-ups (named for future tracks)

- `durable-skip-propagation`: add a skip seam to `QuestNodeStepHandler` mirroring
  the legacy executor's cascade-skip rule.
- `quest-frontend-dag-warnings`: mirror skip/publish rules in the frontend builder's
  advisory `dagWarnings` display (zero frontend code changed here).
- Output-binding/templating for Tier-2 node configs (dynamic amounts from upstream outputs).
- First-class failure arm (`OnFailure` edge type / gate-else branch).
- `QuestNodeTemplate` stored `configSchema`/`inputSchema`/`outputSchema` enforcement.
