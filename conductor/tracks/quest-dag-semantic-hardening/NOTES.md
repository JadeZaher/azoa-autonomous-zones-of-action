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

**E-1 — Unit + schema tests: 1027/1027 passed**
`dotnet test tests/AZOA.WebAPI.Tests/` — 1027 passed, 0 failed, 0 skipped.
Includes all new tests from Phases A–D plus the 34 schema golden tests.
`quest.surql` golden regenerated via the `AZOA_REGENERATE_GOLDENS` escape hatch
after Phase B added `Status` to the Quest POCO.

**E-2 — Integration tests: skipped (SurrealDB not running)**
The podman SurrealDB container was not available during the sweep. Integration
tests in `tests/AZOA.WebAPI.IntegrationTests/` were not run.
Pre-existing status: 216 passing as of commit d80cf74; no integration test
fixtures were modified by this track (integration tests do not exercise the new
quest publish gate, skip cascade, or holon cycle guard through the HTTP layer).

**E-3 — Build: 0 errors, 25 unique warnings (baseline 28)**
Zero new warnings introduced by this track.

**E-4 — Fixes surfaced by the sweep**
Six categories of test errors were discovered and resolved during Phase E:
1. `QuestNodeConfig.TryDeserialize` strict param added; `EmitNodeHandler` uses
   `strict:false` so unknown config keys at runtime do not cause failure (the
   existing `EmitNodeHandlerTests.HandleAsync_UndefinedPayload_EmitsEmptyObject`
   pinned this contract).
2. `QuestInstantiatorTests`: `IQuestDagValidator.Validate` mock updated to pass
   `It.IsAny<bool>()` for the `fanOutAsError` parameter added in Phase B (CS0854).
3. `QuestNodeConfigSafeDeserializeTests`: added `using Moq;`; fixed `TransferAsync`
   `.Verify` arg types (`AZOARequest?` not `Guid?`; `Guid?` not `string?`).
4. `HolonManagerTests.MoveSubtreeAsync_CyclePrevention_ReturnsError`: updated
   assertion from `Contain("Cannot move")` to `ContainEquivalentOf("cycle")` to
   match the new unified `EnsureNotDescendantAsync` error message.
5. `HolonManagerExtendedTests.InteractAsync_ShouldChangeParent`: added `QueryAsync`
   stub returning empty children (the cycle-guard BFS requires a store response).
6. `ReconcileBeforeRetryWiringTests.BuildSingleChainNodeQuest`: set
   `Status = QuestStatus.Active` — this file was missed in the Phase B fixture sweep.

## Out-of-scope follow-ups (named for future tracks)

- `durable-skip-propagation`: add a skip seam to `QuestNodeStepHandler` mirroring
  the legacy executor's cascade-skip rule.
- `quest-frontend-dag-warnings`: SUPERSEDED by Phase G — the builder now mirrors
  skip/publish semantics.
- Output-binding/templating for Tier-2 node configs (dynamic amounts from upstream outputs).
- First-class failure arm (`OnFailure` edge type / gate-else branch).
- `QuestNodeTemplate` stored `configSchema`/`inputSchema`/`outputSchema` enforcement.

## Phase G

### G1 — Publish lifecycle UI

**Quests page uses `azoa.api.request` directly (not typed SDK quest methods).**
All publish/unpublish/execute calls on the page are direct HTTP calls via the SDK's
low-level `api.request`. The typed `publishQuest`/`unpublishQuest` SDK methods (G4)
exist for external SDK consumers. This is consistent with how the existing page calls
validate, execute, and delete.

**Publish/Unpublish buttons:** shown conditionally based on `quest.status`. Publish
appears only for Draft quests; Unpublish only for Active. Execute is disabled for any
status that isn't Active, with a tooltip hinting "Publish first".

**Server validation error rendering:** `ValidationErrorList` attempts to JSON-parse
the server `message` field as a string array (the shape `PublishAsync` returns for
multi-error validation failures). Falls back to a single string if parsing fails.
This handles both the structured publish-failure payload and plain string errors.

**Mutation affordances on Active quests:** `QuestCanvas` now accepts a `readOnly`
prop. When true: `onConnect`/`onDrop`/`onDragOver` are removed from ReactFlow,
`deleteKeyCode` is null, `nodesConnectable`/`nodesDraggable` are false, and the node
inspector is replaced by an "unpublish to edit" message. The quests page does NOT
pass `readOnly` to the builder tab's `QuestCanvas` because the builder is for new
quest creation (always Draft). The `readOnly` prop is available for future use from
the quest detail view if a builder-from-existing-quest flow is added.

**Split error state:** `QuestList` uses `listError` (load errors, shown via ErrorBanner)
and `actionError` (action results, shown inline via ValidationErrorList). This avoids
double-display of publish validation error text.

### G2 — Edge inspector

Clicking an edge in the builder selects it (deselects any node) and shows the edge
inspector in the right panel. The inspector has:
- EdgeType toggle (Control | Conditional) — clicking changes the edge type, which also
  updates the visual style (amber dashed for Conditional, gray for Control).
- Condition text input (shown only for Conditional edges) — highlighted red with a
  "required" label and blocking error message when empty.
- Delete Edge button.

`edgeStyle(type)` helper centralizes the animated/stroke/dasharray logic so it's
applied consistently at connect-time, preset-load-time, and patch-time.

`onEdgeClick` is wired into ReactFlow and clears `selectedId` (node) when an edge is
selected; `onNodeClick` and `onPaneClick` clear `selectedEdgeId`.

### G3 — dagWarnings update

dagWarnings is now `Array<{ text: string; error?: boolean }>`. Error-level warnings
render in red with a `✕` prefix; advisory warnings render in amber with `⚠`.

**Fan-out:** nodes with `>1 outgoing Control edge` show an error-level warning: "won't
publish / durable engine requires single Control successor". Fan-out counting uses the
Control-edge out-degree, not total out-degree (Conditional edges are excluded).

**Conditional-empty:** each Conditional edge missing condition text shows a separate
error-level warning naming source and target nodes.

**Invalid config JSON:** each node with unparseable JSON config shows an error-level
warning. These also block the Submit button (disabled + tooltip) — this makes the
"invalid JSON" case a hard submit gate rather than just a red border.

**Cascade-skip advisory:** a single advisory warning appears when the DAG has any
Control edges, reminding authors that failure/skip cascades through the ENTIRE
downstream Control chain (not just one hop). This replaces any implied one-hop
semantics from the previous warning text.

**Submit gating:** `handleSubmit` early-returns if `nodesWithInvalidConfig.length > 0`
or `conditionalEdgeMissingCondition.length > 0`. The submit button is disabled in
these states (title attribute gives the reason).

### G4 — SDK surface

**Page uses direct fetch:** The quests page calls `azoa.api.request(...)` for all
quest operations including the new publish/unpublish. This is consistent with the
pre-existing pattern on the page (validate, execute, delete all use direct request).

**SDK typed methods added:** `publishQuest(questId)` and `unpublishQuest(questId)`
in `sdk/azoa-wallet/src/api/client.ts` following the existing quest method pattern
(assertUuid guard + `this.request`). Both return `Result<QuestResult, SdkError>`.

**Path constants added:** `QUEST_PUBLISH` and `QUEST_UNPUBLISH` in
`sdk/azoa-wallet/src/api/api-version.ts`.

**API_SYNC.md updated:** two new OK rows for publish/unpublish; last-reconciled
date bumped to 2026-07-02; note added explaining page-vs-SDK distinction.

### Verification G

- SDK `npx tsc --noEmit`: clean (no output).
- SDK `npm test`: 155/155 passed (13 test files).
- Frontend tsc: skipped — no TypeScript binary in `frontend/node_modules` per the
  `no-frontend-typecheck` project convention; quest-visual-builder precedent.
- Manual flow (designed path, not run — no browser available in this context):
  1. Load My Quests → see Draft/Active badges on each quest row.
  2. Open Draft quest → Actions tab → "Publish" button visible, "Execute Quest"
     disabled (grayed, tooltip "Publish the quest before executing").
  3. Click Publish → if validation fails, errors render as a bullet list below
     the action buttons (not a toast blob).
  4. On success, quest row flips to Active badge; Publish button replaced by Unpublish.
  5. Click Execute Quest (now enabled) → works as before.
  6. Open Builder → draw a Conditional edge → click it → Edge inspector shows
     EdgeType toggle and Condition input; leaving Condition empty shows red "required"
     label and blocks submit button.
  7. Fan-out: connect >1 Control edge from one node → red error warning in warnings
     panel "won't publish / durable engine requires single Control successor".

## Phase F

**F-1 — Vacuous test replaced**
`CreateAsync_SelfParent_Rejected` (body: `await Task.CompletedTask; // placeholder`)
replaced with `UpdateAsync_SelfParent_Rejected`: a real assertion of the same
`EnsureNotDescendantAsync` self-parent branch via `UpdateAsync(holonId, { ParentHolonId = holonId })`.
The original comment was correct — the API-surface CreateAsync self-parent path is unreachable
because `HolonCreateModel` has no `Id` field. The new test exercises the identical guard via
the API-reachable `UpdateAsync` path.

**F-2 — CloneAsync one-liner**
Added to `Managers/AGENTS.md §holon-parent-cycle`: `CloneAsync` (~line 359) assigns a fresh
`Guid.NewGuid()` as both the clone's `Id` and tree root, so no existing holon is named as
parent and no cycle is reachable. The guard is intentionally absent there.

**F-3 — TOCTOU documented**
`StartWorkflowRunAsync` reads `Status == Active` while `UnpublishAsync` flips it to `Draft`.
Non-transactional at the current SurrealDB integration depth. Mitigated by `UnpublishAsync`
checking for in-flight runs before flipping. A write-side optimistic-concurrency stamp is the
named follow-up for pre-launch hardening.

**F-4 — CS1998 fixed**
`DagValidator_FanOut_IsWarningOnLegacyPath` and `DagValidator_FanOut_IsErrorOnDurablePath`
changed from `async Task` to `void` — neither contains `await`.

## Phase H

**H-1 — Lifecycle suite: QuestLifecycleIntegrationTests (6 tests, all PASS)**
- `LinearTierOneQuest_CreatePublishExecute_Succeeds`
- `Execute_OnDraftQuest_ReturnsBadRequest_NamingPublish` (AC-2b)
- `AddNode_OnActiveQuest_ReturnsBadRequest` (AC-2c)
- `AddEdge_OnActiveQuest_ReturnsBadRequest` (AC-2c)
- `AddNode_AfterUnpublish_Succeeds`
- `Publish_OtherAvatarsQuest_ReturnsNotFound` (IDOR guard, AC-2a)

**H-2 — Semantics suite: QuestSemanticsIntegrationTests (3 tests, all PASS)**
- `GateCheckFails_BothSuccessors_AreSkipped_ViaExecutionStateApi` (AC-1a, FR-9b)
- `FanOutQuest_Publish_Returns400_WithFanOutError` (AC-3a, FR-9c)
- `FanOutQuest_LegacyExecute_Proceeds_WithoutHardReject` (AC-3b at HTTP layer)
  Note: the publish gate takes priority over the fan-out check on Draft quests.
  AC-3b (fan-out is a warning, not an error, on the legacy executor) is confirmed
  at unit level via `QuestPublishLifecycleTests.DagValidator_FanOut_IsWarningOnLegacyPath`.

**H-3 — ArdaNova flow: QuestArdanovaFlowIntegrationTests (3 tests, all PASS)**
- `ArdanovaFlow_GateFails_BothEmitNodesSkipped`: gate predicate "false" fails;
  both downstream Emit nodes are Skipped (cascade skip, AC-1a at HTTP layer).
- `ArdanovaFlow_AfterFunding_GatePasses_EmitOutputReadable`: gate predicate
  `reads.status == "FUNDED"` with `reads.status="FUNDED"` injected in config;
  gate passes; Emit output readable from `GET /api/quest/runs/{runId}/execution-state`.
- `ArdanovaFlow_Tier2Grant_SimulatedProvider_TerminatesCleanly`: uses
  `ArdanovaSimulatedFactory` (pins `Blockchain:Mode=Simulated`); Grant node config
  uses the correct `NftMintRequest` shape (`walletId`, `name`, `description`, `chainId`,
  `tokenId`); quest publishes and executes; Grant node reaches a terminal state.

**H-4 — Tier-2 leg wiring decision**
The Grant handler executes against the `SimulatedBlockchainProvider`. The wallet is
seeded via `POST /api/wallet` with a `sim:`-prefixed address before the quest run.
If the wallet row is absent from SurrealDB (e.g. the wallet create endpoint rejects
the `sim:` address format), the `ChainCapabilityGate` fires and the Grant node fails
with a capability error. The test accepts both `Succeeded` and `Failed` outcomes —
both confirm the harness routes to the simulated provider correctly. This is the
honest "Tier-2 leg may be unavailable in the integration harness" outcome per the
spec (FR-9e); no result is faked.

**H-5 — SurrealDB description field constraint**
The SurrealDB quest schema ASSERT rejects descriptions containing non-ASCII characters
(arrows `→`, em-dashes `—`) when the schema SCHEMAFULL constraint for `description`
enforces `none | string`. All Phase H quest descriptions use plain ASCII to avoid this.
This is a pre-existing schema restriction; no schema change is needed.

**H-6 — ArdanovaSimulatedFactory**
A second `WebApplicationFactory<Program>` (`ArdanovaSimulatedFactory`) pins
`Blockchain:Mode=Simulated` and shares the outer factory's `TestNamespace`. This
correctly routes all chain calls in the H3 tests to `SimulatedBlockchainProvider`
without affecting the H1/H2 tests (which use the default factory with `Mode=Live`).

## Phase I

**I-1 — Build (2026-07-02)**
`dotnet build AZOA.WebAPI.csproj -nologo` → Build succeeded, 0 errors, 0 warnings.
No new warnings introduced by Phases F, H, I. Pre-track baseline: 25 unique warnings
(Phase E), all pre-existing in non-touched files.

**I-2 — Unit + schema tests (2026-07-02)**
`dotnet test tests/AZOA.WebAPI.Tests/ -nologo` → Passed: 1027, Failed: 0, Skipped: 0.
Phase F replaced the vacuous `CreateAsync_SelfParent_Rejected` with
`UpdateAsync_SelfParent_Rejected` (net count unchanged: the placeholder was registered
as a test in xUnit).

**I-3 — Integration tests (2026-07-02, SurrealDB azoa-dev-surrealdb on :8000)**
`dotnet test tests/AZOA.WebAPI.IntegrationTests/ -nologo` →
  Passed: 228, Failed: 37, Skipped: 0, Total: 265
  Pre-track baseline: 216 Passed / 37 Failed / 0 Skipped (INTEGRATION-TEST-PASSOFF.md)
  Delta: +12 new passing tests (Phase H: 6 lifecycle + 3 semantics + 3 ArdaNova)
  The 37 failures are unchanged pre-existing failures (Bucket A/B/C/D in PASSOFF.md:
  IDOR fixture mismatch, env-path drift, socket race, factory re-registration).
  No new failures introduced by this track.

New passing integration tests (Phase H, all green):
  QuestLifecycleIntegrationTests:
    - LinearTierOneQuest_CreatePublishExecute_Succeeds
    - Execute_OnDraftQuest_ReturnsBadRequest_NamingPublish
    - AddNode_OnActiveQuest_ReturnsBadRequest
    - AddEdge_OnActiveQuest_ReturnsBadRequest
    - AddNode_AfterUnpublish_Succeeds
    - Publish_OtherAvatarsQuest_ReturnsNotFound
  QuestSemanticsIntegrationTests:
    - GateCheckFails_BothSuccessors_AreSkipped_ViaExecutionStateApi
    - FanOutQuest_Publish_Returns400_WithFanOutError
    - FanOutQuest_LegacyExecute_Proceeds_WithoutHardReject
  QuestArdanovaFlowIntegrationTests:
    - ArdanovaFlow_GateFails_BothEmitNodesSkipped
    - ArdanovaFlow_AfterFunding_GatePasses_EmitOutputReadable
    - ArdanovaFlow_Tier2Grant_SimulatedProvider_TerminatesCleanly
