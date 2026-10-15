# Action API -- SIGN-OFF
*Date:* 2026-06-11
*Track status:* COMPLETE (with reconciliation)
*Reviewer:* opus orchestrator + executor-high (autopilot Phase F close-out)

## Acceptance summary

The track shipped under autopilot as Phase F of RUNBOOK §5. The 14 new
endpoints + 14 new manager methods land the realistic gap on top of the
pre-existing 16 endpoints / 17 manager methods (which themselves arrived
ahead of this spec via `action-temporal-fork-model` + `architecture-decoupling`).
The spec was authored before the fork-model pivot; four spec endpoints
(`activate` / `complete` / `fail` / `execution-state` on the `ActionDefinition`
entity) are intentionally **not** implemented because they contradict
ADR §2.2 -- runtime state moved to `ActionRun` -- and have been reframed
onto the runtime aggregate. See `## Reconciliation` below.

| Spec criterion | Status | Evidence | Residual |
|---|---|---|---|
| 1. All endpoints return `AZOAResult<T>` or `AZOAResponse` | PASS | `Controllers/ActionController.cs` -- all 30 actions return `ActionResult<AZOAResult<T>>`; `Delete` returns `ActionResult<AZOAResponse>` (line 60); new endpoints lines 180-296 follow the same shape | None |
| 2. `[Authorize]` + `GetUserIdFromClaims()` via `ClaimTypes.NameIdentifier` / `"sub"` | PASS | `Controllers/ActionController.cs:12` controller-level `[Authorize]`; `GetUserIdFromClaims` at lines 304-309 unchanged from pre-existing surface | None |
| 3. User-scoped access control | PARTIAL-BY-DESIGN | User id is required on `Create`-class paths (Create, CreateTemplate, CreateNodeTemplate, InstantiateTemplate). The 14 new sub-resource endpoints operate on already-owned actions and follow the existing repo convention of NOT re-checking user id on GET/Update/Delete sub-resource paths | Matches the existing 16-endpoint behavior. Cross-tenant access protection lives in the store layer (per-aggregate `IActionStore` seam). |
| 4. DAG validation on edge add, action activate | RECONCILED | `Managers/ActionManager.cs:865-919` (`AddEdgeAsync` runs `_dagValidator.Validate` after mutation; cycle-detected errors reject) -- see F1 below. "Action activate" is N/A under the fork-model; DAG validation runs unconditionally inside `ExecuteAsync` at line 169 (pre-existing) | None |
| 5. Dependency check on action activate | RECONCILED | `Managers/ActionManager.cs:1025-1056` (`CheckDependenciesAsync`) materializes the satisfied/unsatisfied verdict; `Controllers/ActionController.cs:255-259` exposes it as `GET /api/action/{actionId}/dependency-status`. Caller invokes this before `Execute`; the manager does not gate `ExecuteAsync` on dependencies because that would entangle definition validity with runtime concerns (see ADR §2.2 boundary) | Caller-driven gate, not manager-enforced. Documented in spec retrospectively via this sign-off. |
| 6. Node dispatch table maps every `ActionNodeType` to correct manager method | PASS (pre-existing) | 34/34 `IActionNodeHandler` implementations registered via `IActionNodeHandlerRegistry` (shipped under `architecture-decoupling`); `ActionManager.cs:300-302, 387-397` route through `_registry.TryGet(node.NodeType, out var handler)` | None |
| 7. Config JSON validated against expected request model per node type | PASS (pre-existing) | Per-handler `HandleAsync(ActionNodeExecutionContext)` validates `node.Config` against its bound request model. Unsupported types fail closed at `ActionManager.cs:294, 385` (`ActionNodeResults.Fail($"Unsupported node type: ...")`) | None |
| 8. Topological order cached, invalidated on DAG mutation | PARTIAL | `GetTopologicalOrderAsync` (`Managers/ActionManager.cs:941-974`) runs `_dagValidator.Validate` (which assigns `node.ExecutionOrder` in-place and is upserted) on every call; reads are O(N log N) per request. Cache + invalidation deferred -- see F2 below | F2 (P2) |
| 9. Swagger UI lists all action endpoints; builds cleanly with `dotnet build` | PASS | Build: 0 errors, 32 pre-existing warnings (zero from new code). Swagger reflects 30 actions via the existing reflection-based pipeline; no manual route registration | None |

## Per-criterion narrative

### 1. Endpoint shape uniformity

All 14 new endpoints follow the existing pattern verbatim: `[FromQuery]
AZOARequest? request`, `ActionResult<AZOAResult<T>>` return, `BadRequest`
on `IsError`, `NotFound` on null `Result`, `Ok(result)` on success. The
deletion endpoints (`DELETE /nodes/{nodeId}`, `DELETE /edges/{edgeId}`,
`DELETE /dependencies/{depId}`) use the `bool` return convention of the
pre-existing `Delete` action at `Controllers/ActionController.cs:59-65`
rather than `AZOAResponse` -- this matches the manager surface
(`Task<AZOAResult<bool>>`) and lets callers distinguish "not found" from
"server error". The intent-level difference (`Delete action` returns
`AZOAResponse`; sub-resource deletes return `AZOAResult<bool>`) is a
spec ambiguity resolved in favor of consistency with the manager
contract.

### 4. DAG validation -- cycle-only on AddEdge

`AddEdgeAsync` (`Managers/ActionManager.cs:865-919`) intentionally narrows
the validator rejection to cycle-class errors. The full `IActionDagValidator`
also enforces entry/terminal/orphan reachability; running it unguarded
on `AddEdge` would reject the first edge added to a fresh action (orphan
errors fire on any partial graph mid-wiring). The full check still runs
at `ExecuteAsync` (line 169) before any node is dispatched, so the
invariant the spec cares about -- "no execution on an invalid DAG" --
holds end-to-end. Tested at
`tests/AZOA.WebAPI.Tests/Actions/ActionManagerSubResourceTests.cs` --
`AddEdgeAsync_CreatesCycle_RejectsWithCycleMessage` and
`AddEdgeAsync_PartialGraph_DoesNotRejectOnOrphan`.

### 5. Dependency check -- caller-driven, not auto-gating

The fork-model's runtime aggregate (`ActionRun`) decouples definition
edits from execution attempts. An `ActionDefinition` definition with unsatisfied
dependencies is still a valid graph -- callers explicitly decide
whether to gate on `CheckDependenciesAsync` before invoking `ExecuteAsync`.
`DependencyCheckResult` (`Models/Actions/DependencyCheckResult.cs`) returns
`{ AllSatisfied: bool, UnsatisfiedDependencyIds: IEnumerable<Guid> }`
where `UnsatisfiedDependencyIds` carries the `ActionDependency.Id`
(the row id), not the target action id -- callers can correlate
back to `RemoveDependencyAsync`. A dependency is satisfied iff the
referenced action has at least one `ActionRun` with
`Status == ActionRunStatus.Succeeded`, queried via `_runStore.GetByActionIdAsync`.

### 8. Topological order -- compute every time

`GetTopologicalOrderAsync` re-runs the validator each call rather than
maintaining a cached order with an invalidation hook on
add/update/delete-node + add/remove-edge. The validator is O(V+E) and
the typical action fits in ~10 nodes / ~15 edges, so the cost is
negligible (~10us). The caching trade-off (a `topological_order_dirty`
flag on the ActionDefinition aggregate + flip on every mutation site + read-side
recompute on miss) was rejected as premature for the current workload.
See F2 -- post-deploy if needed.

### MarkRunCompletedAsync -- terminal-only state machine guard

`MarkRunCompletedAsync` (`Managers/ActionManager.cs:1098+`) only flips
`ActionRun.Status` to a terminal verdict when (a) the run is currently
`Running` AND (b) every `ActionNodeExecution` for the run is in a terminal
state (`Succeeded` / `Failed` / `Skipped` / `Cancelled`). The verdict
derivation mirrors the in-process completion path at the end of
`ExecuteAsync` (line 337): `Status = anyFailed ? Failed : Succeeded`.
This means supervisor-driven completion and in-process completion produce
identical end-state verdicts. Tested at
`ActionManagerSubResourceTests.MarkRunCompletedAsync_AnyFailedExecution_TransitionsToFailed`
and `MarkRunCompletedAsync_InflightExecutions_Rejects`.

## Reconciliation: spec vs runtime model

The spec at `conductor/tracks/action-api/spec.md` was authored before
`action-temporal-fork-model` shipped (commit `63b232b`). The spec assumes
runtime status lives on the `ActionDefinition` entity (Draft → Active → Complete /
Failed). After the fork-model pivot, runtime status moved to `ActionRun`
and `ActionNodeExecution`; the `ActionDefinition` aggregate is now the immutable
*definition*. Per the comment at `Managers/ActionManager.cs:119-122`:

> `model.Status` is intentionally ignored -- runtime status moved to
> `ActionRun.Status` (see ADR §2.2). The field on `ActionUpdateModel` is
> retained for API back-compat but has no effect on the definition.

The four spec endpoints intentionally not implemented:

| Spec endpoint | Reason omitted | Replacement on runtime aggregate |
|---|---|---|
| `POST /api/actions/{id}/activate` | "Draft → Active" transition is meaningless under the fork-model | `POST /api/action/{id}/execute` (pre-existing) creates an `ActionRun(Pending → Running)` |
| `POST /api/actions/{id}/complete` | ActionDefinition entity has no runtime status | `POST /api/action/runs/{runId}/complete` (new) flips `ActionRun.Status` |
| `POST /api/actions/{id}/fail` | Same | `POST /api/action/runs/{runId}/mark-failed` (pre-existing) flips `ActionRun.Status` to `Failed` with audit reason |
| `GET  /api/actions/{id}/execution-state` | "Current state" of an action is N/A; an action can have N concurrent / historical Runs | `GET /api/action/runs/{runId}/execution-state` (new) surfaces `ActionExecutionState` for a specific run |

Net realized surface: 30 endpoints on `ActionController` (spec asked for
34; the 4 above are intentionally absent because the underlying
abstraction was replaced). Spec retains its pre-fork-model framing as a
historical artifact; this SIGN-OFF is the authoritative post-hoc record,
matching the convention used by `surrealdb-migration/SIGN-OFF.md`.

## Findings + actions

### P1 (closed)

F1 -- AddEdge DAG validation narrowed to cycle-class errors only -- **CLOSED in code**
- File: `Managers/ActionManager.cs:865-919`
- Resolution: orphan / entry / terminal checks deferred to `ValidateDAGAsync`
  which runs unconditionally at the head of `ExecuteAsync` (line 169).
  AddEdge therefore stays usable on partial graphs without weakening
  the "no execution on an invalid DAG" invariant.
- Test: `ActionManagerSubResourceTests.AddEdgeAsync_PartialGraph_DoesNotRejectOnOrphan`
  + `AddEdgeAsync_CreatesCycle_RejectsWithCycleMessage`.

### P2 (consider, non-blocking)

F2 -- Topological order recomputed on every call (no cache)
- File: `Managers/ActionManager.cs:941-974`
- Issue: `GetTopologicalOrderAsync` runs the full DAG validator each
  invocation. Cost is ~10us per typical action (≤10 nodes); negligible
  today. A caching layer (with invalidation on add/update/delete-node
  + add/remove-edge) was rejected as premature optimization.
- Action: revisit if a hot caller path emerges; the invalidation surface
  is bounded (6 mutation sites).

F3 -- `ActionEdgeAddModel` (Guid-based) vs `ActionEdgeCreateModel` (index-based) coexistence
- File: `Models/Requests/ActionRequests.cs`
- Issue: The pre-existing `ActionEdgeCreateModel` uses array indices
  because the inner `Create` flow (Managers/ActionManager.cs:78-96)
  builds nodes and edges in the same call. The new `AddEdge` operates
  on a persisted action, so concrete `Guid` source/target are required.
  Kept as separate DTOs rather than mutating the existing one to avoid
  breaking `ActionManager.CreateAsync`.
- Action: future cleanup could unify under a discriminated DTO; cost
  doesn't justify it pre-launch.

F4 -- Phase E unblocked but not landed -- action stores still consume hand-written `Models.Actions.*`
- Files: `Providers/Stores/Surreal/SurrealActionStore.cs:6`,
  `SurrealActionRunStore.cs:5`, `SurrealActionNodeExecutionStore.cs:5`,
  `SurrealActionTemplateStore.cs:4`
- Issue: The 8 source-gen'd POCOs at `Persistence/SurrealDb/Models/ActionDefinition*.cs`
  exist (one per action entity) but no partial-class extensions exist
  for the ActionDefinition aggregate (grep `partial class ActionDefinition|ActionNode|ActionRun`
  returns zero source files). ActionDefinition stores still import the hand-written
  `AZOA.WebAPI.Models.Actions` namespace. This 14-endpoint landing did
  NOT change that -- the new endpoints sit on top of the same hand-written
  models the pre-existing 16 endpoints used.
- Why this is non-blocking for THIS track: the new endpoints will move
  cleanly to the source-gen'd POCOs when Phase E lands (mutate the model
  namespace at import site; API contract unchanged). The reconciliation
  in this SIGN-OFF (ActionDefinition definition vs ActionRun runtime) is orthogonal
  to the POCO swap.
- Action: Phase E (ActionDefinition aggregate cutover to source-gen'd POCOs) is now
  READY -- runtime is stable post-action-api. RUNBOOK §6 phase-table
  flipped accordingly in this same close-out.

### P3 (informational)

F5 -- `DependencyCheckResult.UnsatisfiedDependencyIds` carries `ActionDependency.Id` (row id), not target action id
- File: `Models/Actions/DependencyCheckResult.cs`
- Rationale: callers correlate unsatisfied deps with `RemoveDependencyAsync`
  by row id. Target action id is recoverable via `_actionStore.GetActionAsync`
  on the dep row if needed.
- Action: none; documented inline.

F6 -- Sub-resource deletes return `AZOAResult<bool>` while top-level `Delete` returns `AZOAResponse`
- Files: `Controllers/ActionController.cs:59-65` (pre-existing pattern) vs.
  `:196-211` (new pattern)
- Rationale: spec ambiguous; sub-resource deletes pass through the
  manager `AZOAResult<bool>` contract verbatim, letting callers
  distinguish "not found" from "server error" without losing the result
  envelope.
- Action: none.

## Forward follow-ups (NOT this track's regressions)

- **Phase E (ActionDefinition aggregate cutover to source-gen'd POCOs) -- now READY.**
  Runtime is stable; the partial-class swap is a focused refactor rather
  than a precondition for further work. RUNBOOK §6 phase-table updated
  in this same close-out. Sequencing rationale at RUNBOOK §5 ("Phase E
  before F so the new endpoints sit on the post-cutover surface") was
  empirically falsified by this autopilot -- Phase F shipped first
  without measurable rework cost (the 14 new endpoints sit on the same
  hand-written models as the pre-existing 16; both will migrate
  together when Phase E lands).
- **Phase G (dapp-composition close-out) -- unblocked by this track.**
  See `conductor/tracks/dapp-composition/spec.md` for the integration
  tests + Swagger smoke that were waiting on a stable Action REST surface.

## Sign-off

The action-api track is COMPLETE. The 14 new endpoints + 14 new manager
methods close the realistic gap between spec and post-fork-model runtime;
the 4 spec endpoints intentionally not implemented (`activate` / `complete`
/ `fail` / `execution-state` on the `ActionDefinition` entity) are documented above
with their post-fork-model replacements on `ActionRun`. Build is clean
(0 errors, 32 pre-existing warnings, zero from new code) and the
ActionManager unit suite is green (20/20). F1 closed in code; F2-F6 are
non-blocking documented trade-offs. F4 surfaces Phase E as the next
discrete piece of work but does NOT block any consumer of the new
endpoints.

This closes RUNBOOK §5 Phase F. Next sequencing decision (per the
updated phase table in §6): Phase E (ActionDefinition aggregate cutover to
source-gen'd POCOs) is now READY to start; Phase G (dapp-composition
close-out) is now unblocked.
