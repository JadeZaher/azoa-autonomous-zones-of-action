---
type: lane-checkpoint
round: 4
wave: W2
lane: L4
helper: B (resource IDOR)
branch: r4/security-idor
base: beae947
merges-into: r4/security (L4 lead merges with --no-ff on the `r4(security-idor): ready` head)
---

# Round 4 · L4 helper B — resource IDOR

Scope = plan §4 L4 **Helper B**. Every fix has a real-flow test through the controller (or manager)
seam over the real manager and in-memory stores, showing the other user denied and the owner allowed.
`HolonController.cs:94-122` (mint/exchange, L5's M9) is untouched.

## Scope closed

### audit-2 H1 — holon subtree clone copied private descendants
- `Managers/HolonManager.cs:563-670` `CloneAsync`: subtree BFS skips `!CanRead(child, caller)` (`:625`),
  same rule as `GetDescendantsAsync`; the skipped child's whole branch is pruned (its clone would have
  no parent). The full clone set is planned before any upsert, so an election-projection refusal no
  longer leaves a half-written clone. `NewParentId` must be an owned holon (`:580`).
- Test: `HolonIdorFlowTests.Clone_PublicTemplateWithSubtree_CopiesOnlyDescendantsTheCallerMayRead`.

### M6 — holon writes reaching other users' trees
- `EnsureOwnedParentAsync` (`HolonManager.cs:50`) on every `ParentHolonId` write: Create `:161`,
  Update `:206` (only when the parent changes), Interact `:316` (same), Clone `:580`, Move `:687`.
  Refusal text is `"Parent holon not found."` for missing and foreign alike.
- `MoveSubtreeAsync` (`:677`) settles ownership before the cycle walk (the cycle error used to answer
  for a holon the caller did not own).
- `PropagateAsync` (`:458`) writes only owned holons in the subtree and plans before writing.
- `UpdateAsync`/`DeleteAsync` fail closed on a null acting user (the `userId.HasValue &&` trap); all
  in-repo callers already pass one. `OwnershipRefusal` (`:45`) answers `"Holon not found."` for a holon
  the caller cannot read.
- Tests: `HolonIdorFlowTests.Reparenting_UnderAnotherUsersHolon_IsRefusedOnEveryWritePath_AndOwnParentIsAllowed`,
  `…Propagate_WritesOnlyTheCallersHolons_InAMixedOwnershipSubtree`,
  `…MutatingAnotherUsersPrivateHolon_ReadsAsNotFound_WhileTheOwnerSucceeds`.

### M1–M5 — actions
- **Existence oracles.** `ActionManager.cs:123-139` `LoadOwnedActionAsync` + `OwnershipRefusal`;
  `GetAsync` and `LoadStartableActionAsync` answer `"Action not found."` for missing and private-foreign
  (store miss text carried the id). `LoadOwnedRunAsync` (`:250-259`) answers `"Action run not found."`
  for both (runs are never public).
- **Validate route.** New owner-scoped `ValidateDAGAsync(actionId, userId)` (`ActionManager.cs:558`,
  interface `IActionManager.cs:26`); `ActionController.cs:104-112` uses it. Before, any caller could
  validate (and persist `ExecutionOrder` on) any action and read its validation errors.
- **Templates.** `GetTemplateAsync`/`ListTemplatesAsync`/`ListNodeTemplatesAsync` (`:903`, `:1036`) are
  owner-or-public (`CanReadAuthored`, `:139`); `InstantiateTemplateAsync` reads the template as the
  caller and resolves node templates as the template AUTHOR could read them (`:936`).
- **Authoring policy.** `DappDevelop` added to validate, publish, unpublish, template instantiate,
  node add/update/delete, edge add/remove, dependency add/remove (`ActionController.cs:104,124,141,372,
  421-515`). Runner routes unchanged.
- Tests: `ActionIdorFlowTests.AnotherUsersRun_IsIndistinguishableFromAMissingRun_OnEveryRunRoute`,
  `…ValidateRoute_OnAnotherUsersPrivateAction_IsRefusedAsNotFound_AndTheOwnerValidates`,
  `…Templates_PrivateOnesStayWithTheirAuthor_AndAPublicTemplateCannotLiftAThirdPartysPrivateNodeTemplate`.

### Dependency-status leak (`ActionManager.cs:1255-1351`)
- `AddDependencyAsync` refuses a depended-on action the caller cannot read (`:1314`) and a
  `DependsOnNodeId` absent from it.
- Satisfaction counts only Succeeded runs by the runner or the action owner (`:1389`); threaded through
  `EnforceDependenciesAsync(action, runnerUserId, …)` at both run-start seams and the frozen deployment
  path (`ActionManager.FrozenRuntime.cs:49`).
- Test: `ActionIdorFlowTests.Dependencies_CannotProbeAPrivateAction_AndAreSatisfiedOnlyByTheRunnersOrOwnersRuns`.

### Oracles — DappComposition (`DappCompositionManager.cs:556-563`)
- Holon-binding validation counts a holon resolved only when the series owner may read it (`:559`).
- Foreign series answers the store's own `"DappSeries {id} not found."` (`:62`); `AddActionAsync` with a
  foreign action reads like a missing one (`:128`); `ValidateAsync`/`ComposeAsync` ignore an action that
  is no longer the owner's (`:243`).
- Tests: `DappCompositionIdorFlowTests.Validate_TreatsAnotherUsersPrivateHolonExactlyLikeAMissingOne_AndResolvesTheOwnersHolon`,
  `…SeriesAndActionProbes_ReadLikeMisses_ForAnotherUser_WhileTheOwnerProceeds`.

### ActionGraphBundle `DappDevelop`
- `ActionGraphBundleController.cs:45,58,69,111`: POST, PUT, DELETE and ecosystem attach carry
  `DappDevelop` (generate/deploy already `DappManage`).
- `ActionGraphBundleManager.OwnershipRefusal` (`:105`): unreadable → NotFound (404), readable public
  foreign → Forbidden (403); null user refused on Delete/Generate/AddDappSeries/GetEcosystem (were
  `userId.HasValue &&` fail-open). Ecosystem references (`ValidateRefOwnershipAsync`) answer NotFound
  for foreign series/bundles.
- Test: `DappCompositionIdorFlowTests.BundleWrites_ByAnotherUser_Are404ForPrivate_403ForPublic_AndNeverLand_WhileTheOwnerWrites`.

### `AwaitingReconciliation` sweep → `{ scanned, results }` (liveness CRITICAL)
- `ActionManager.cs:1765` returns `ActionReconciliationSweepResult { Scanned, Results, Errors, HardStuck }`
  (`Models/Actions/ActionReconciliationResult.cs:42`); zero scanned says "No runs are awaiting
  reconciliation", never "Swept". `ActionController.cs:330` route type updated.
- The underlying "scans zero rows" cause (F-C6 enum casing) was fixed by SurrealForge 1.3.0 in W0.
- Test: `ReconcileBeforeRetryWiringTests.ReconcileSweep_OnParkedRun_ConfirmsTx_ResumesWithoutReMint`
  asserts `Scanned == 1, Errors == 0` (but see pre-existing failures below — the harness does not
  dispatch the node on this base).

## Published contracts (for L7 and the L4 lead)

- `POST /api/action/runs/reconcile-sweep` → `AZOAResult<{ scanned: number, results: ActionReconciliationResult[],
  errors: number, hardStuck: number }>` (was `AZOAResult<ActionReconciliationResult[]>`). L7's
  `frontend/src/app/(dashboard)/admin/parked-runs/page.tsx` must read `result.scanned` and toast
  nothing on `scanned === 0` or `errors > 0` (plan §4 L7).
- `GET /api/action/templates`, `GET /api/action/templates/{id}`, `GET /api/action/node-templates`: now
  owner-or-public (private templates of other users disappear / 404).
- Action authoring writes listed above now require `DappDevelop` (403 for a plain user JWT).
- Refusal texts: foreign private action → `"Action not found."`; foreign run → `"Action run not found."`;
  foreign bundle PUT/DELETE → 404 (private) / 403 (public); foreign parent → `"Parent holon not found."`.

## Hunks

None — no `Program.cs` or config changes.

## POCO deltas

None.

## Files outside the helper-B globs (necessary contract edits)

- `Interfaces/Managers/IActionManager.cs` — owner-scoped `ValidateDAGAsync` overload, template reads take
  `callerUserId`, sweep return type. Unlisted file (L1 by default); it is the contract of
  `ActionManager`, which this helper owns, and no other lane edits it in round 4.
- `Models/Actions/ActionReconciliationResult.cs` — appended `ActionReconciliationSweepResult` (new class).
- `Managers/AGENTS.md` — new §holon-owned-parent, §action-read-scope; updated §holon-parent-cycle,
  §action-dependency-enforcement, §parked-run-sweep.

## Needs other lane

- **L4 lead — `Controllers/AGENTS.md:130-135` (§cross-tenant-read-scope):** the line "Holon `Clone`
  cross-user template mechanic — already own-or-public gated" should point at
  `Managers/AGENTS.md §holon-owned-parent` (clone now also filters descendants); §dapp-composition-rbac
  (`:137-152`) should list the action authoring routes and ActionGraphBundle writes as `DappDevelop`.
- **L4 helper A — route inventory:** entries below for `RouteAuthorization/L4.json` (helper B did not
  create the file to avoid a merge conflict).
- **L4 lead — `Providers/Stores/Surreal/SurrealActionStore.cs:39-44`:** `ActionDefinition.Dependencies` is
  not persisted by the Surreal store (documented gap), so dependency enforcement only bites in-memory.
  Pre-existing; out of helper B's globs.
- **L7 — parked-runs page:** consume the new sweep body (above).

## Route inventory (for helper A to merge into `RouteAuthorization/L4.json`)

```json
[
  {"route":"POST api/holon","controller":"Holon","action":"Create","class":"non-value","scope":"DappDevelop","enforcement":"owner = caller; parent must be owned"},
  {"route":"PUT api/holon/{id}","controller":"Holon","action":"Update","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; new parent must be owned"},
  {"route":"DELETE api/holon/{id}","controller":"Holon","action":"Delete","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/holon/{id}/interact","controller":"Holon","action":"Interact","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; new parent must be owned"},
  {"route":"POST api/holon/{id}/propagate","controller":"Holon","action":"Propagate","class":"non-value","scope":"DappDevelop","enforcement":"owner root; writes owned holons only"},
  {"route":"POST api/holon/{id}/clone","controller":"Holon","action":"Clone","class":"non-value","scope":"DappDevelop","enforcement":"owner-or-public source; readable descendants only; owned parent"},
  {"route":"POST api/holon/{id}/move","controller":"Holon","action":"MoveSubtree","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; owned parent"},
  {"route":"POST api/action","controller":"Action","action":"Create","class":"non-value","scope":"DappDevelop","enforcement":"owner = caller"},
  {"route":"PUT api/action/{id}","controller":"Action","action":"Update","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"DELETE api/action/{id}","controller":"Action","action":"Delete","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/action/{id}/validate","controller":"Action","action":"Validate","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/action/{id}/publish","controller":"Action","action":"Publish","class":"non-value","scope":"DappDevelop","enforcement":"owner-only, version CAS"},
  {"route":"POST api/action/{id}/unpublish","controller":"Action","action":"Unpublish","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/action/{id}/execute","controller":"Action","action":"Execute","class":"value-moving","scope":"authenticated + captured signing credential","enforcement":"owner or published+invited; quota; economic consent"},
  {"route":"POST api/action/{id}/start-workflow","controller":"Action","action":"StartWorkflow","class":"value-moving","scope":"authenticated + captured signing credential","enforcement":"owner or published+invited; quota; economic consent"},
  {"route":"POST api/action/{id}/nodes/{nodeId}/execute","controller":"Action","action":"ExecuteNode","class":"value-moving","scope":"authenticated","enforcement":"owner-only"},
  {"route":"POST api/action/runs/{runId}/fork","controller":"Action","action":"Fork","class":"non-value","scope":"authenticated","enforcement":"run owner-only"},
  {"route":"POST api/action/runs/{runId}/mark-failed","controller":"Action","action":"MarkRunFailed","class":"non-value","scope":"authenticated","enforcement":"run owner-only"},
  {"route":"POST api/action/runs/{runId}/complete","controller":"Action","action":"MarkRunCompleted","class":"non-value","scope":"authenticated","enforcement":"run owner-only"},
  {"route":"POST api/action/runs/{runId}/advance","controller":"Action","action":"Advance","class":"non-value","scope":"authenticated","enforcement":"run owner-only"},
  {"route":"POST api/action/runs/{runId}/signal","controller":"Action","action":"Signal","class":"non-value","scope":"authenticated","enforcement":"run owner-only"},
  {"route":"POST api/action/runs/{runId}/reconcile","controller":"Action","action":"Reconcile","class":"non-value","scope":"authenticated","enforcement":"run owner-only; never re-broadcasts"},
  {"route":"POST api/action/runs/reconcile-sweep","controller":"Action","action":"ReconcileSweep","class":"non-value","scope":"Operator","enforcement":"cross-user by design; never re-broadcasts"},
  {"route":"POST api/action/templates","controller":"Action","action":"CreateTemplate","class":"non-value","scope":"DappDevelop","enforcement":"author = caller"},
  {"route":"POST api/action/templates/{id}/instantiate","controller":"Action","action":"InstantiateTemplate","class":"non-value","scope":"DappDevelop","enforcement":"template owner-or-public; node templates as author"},
  {"route":"POST api/action/node-templates","controller":"Action","action":"CreateNodeTemplate","class":"non-value","scope":"DappDevelop","enforcement":"author = caller"},
  {"route":"POST api/action/{actionId}/nodes","controller":"Action","action":"AddNode","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; Draft only"},
  {"route":"PUT api/action/{actionId}/nodes/{nodeId}","controller":"Action","action":"UpdateNode","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; Draft only"},
  {"route":"DELETE api/action/{actionId}/nodes/{nodeId}","controller":"Action","action":"DeleteNode","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; Draft only"},
  {"route":"POST api/action/{actionId}/edges","controller":"Action","action":"AddEdge","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; Draft only"},
  {"route":"DELETE api/action/{actionId}/edges/{edgeId}","controller":"Action","action":"RemoveEdge","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; Draft only"},
  {"route":"POST api/action/{actionId}/dependencies","controller":"Action","action":"AddDependency","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; target owner-or-public"},
  {"route":"DELETE api/action/{actionId}/dependencies/{depId}","controller":"Action","action":"RemoveDependency","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"PUT api/action/{id}/run-access","controller":"Action","action":"SetRunAccess","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/action/{id}/invite","controller":"Action","action":"Invite","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"DELETE api/action/{id}/invite/{userId}","controller":"Action","action":"RevokeInvite","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/action/{id}/access-requests","controller":"Action","action":"RequestAccess","class":"non-value","scope":"authenticated","enforcement":"requester = caller"},
  {"route":"POST api/action/access-requests/{requestId}/decision","controller":"Action","action":"DecideAccessRequest","class":"non-value","scope":"DappDevelop","enforcement":"action owner-only"},
  {"route":"POST api/action/access-requests/{requestId}/withdraw","controller":"Action","action":"WithdrawAccessRequest","class":"non-value","scope":"authenticated","enforcement":"requester-only"},
  {"route":"POST api/actiongraphbundle","controller":"ActionGraphBundle","action":"CreateOrUpdate","class":"non-value","scope":"DappDevelop","enforcement":"owner = caller; by (name, user)"},
  {"route":"PUT api/actiongraphbundle/{id}","controller":"ActionGraphBundle","action":"Update","class":"non-value","scope":"DappDevelop","enforcement":"owner-only; 404 unreadable / 403 public"},
  {"route":"DELETE api/actiongraphbundle/{id}","controller":"ActionGraphBundle","action":"Delete","class":"non-value","scope":"DappDevelop","enforcement":"owner-only"},
  {"route":"POST api/actiongraphbundle/{id}/generate","controller":"ActionGraphBundle","action":"Generate","class":"non-value","scope":"DappManage","enforcement":"owner-only"},
  {"route":"POST api/actiongraphbundle/{id}/deploy","controller":"ActionGraphBundle","action":"Deploy","class":"value-moving","scope":"DappManage","enforcement":"owner-only; deployment service authority (helper A)"},
  {"route":"POST api/actiongraphbundle/{id}/ecosystem/dapp-series","controller":"ActionGraphBundle","action":"AddDappSeries","class":"non-value","scope":"DappDevelop","enforcement":"bundle + reference owner-only"},
  {"route":"POST api/dapp-series/{id}/compose","controller":"DappComposition","action":"Compose","class":"non-value","scope":"DappManage","enforcement":"series owner-only"},
  {"route":"POST api/dapp-series/{id}/generate","controller":"DappComposition","action":"Generate","class":"non-value","scope":"DappManage","enforcement":"series owner-only"},
  {"route":"POST api/dapp-series/{id}/deploy","controller":"DappComposition","action":"Deploy","class":"value-moving","scope":"DappManage","enforcement":"series owner-only; deployment service authority (helper A)"}
]
```

Holon `mint`/`exchange` (`HolonController.cs:94-122`) are value-moving and classified by L5 (M9 changes the
called method); GET routes are omitted (the gate covers POST/PUT/DELETE).

## Compile result

`dotnet build azoa.sln -c Debug --no-incremental`: **0 errors, 22 warnings** (≤ 25; none in touched files).

## Tests run (by filter, own area only)

Filter over Holon*, Action{Dependency,SubResource,Invitations,Publish,ForkLineage,ExecutionOrder}*,
ReconcileBeforeRetry*, DappComposition*, ActionGraphBundle*, Governance*, *IdorFlowTests:
**315 pass / 18 fail of 333**. Before this branch the same filter was 296 / 26 of 322: no new failure,
8 pre-existing fixed, 11 tests added (all pass).

- Fixed (stale vs round-3 contracts, in owned area): `ActionInvitationsTests` ×5 (no request key, no
  admission store), `HolonPublicationTests` ×2 (proposal create/unpublish via the generic route is now
  refused — test renamed to the refusal), `DappCompositionManagerTests.DeployAsync_RejectsWhenSeriesIsNotReady`
  (message wording).
- Updated for the new semantics: Holon mock tests (owned parent, fail-closed null user), bundle tests
  (Forbidden → NotFound for private), dependency tests (seed a readable depended-on action), sweep test
  (new body), integration `ActionGraphBundleControllerIntegrationTests.Update_DifferentUser_PrivateRecord_Returns404`
  (not run — integration).
- **Pre-existing, not fixed (18), recorded for the L4 lead / sweep:**
  - `ActionForkLineageTests` ×5 — assume the removed synchronous executor (`ExecuteAsync` returning
    `Succeeded`; fork of a run without a frozen snapshot → "Legacy run has no frozen definition").
    Needs a rewrite on the durable harness.
  - `ReconcileBeforeRetryWiringTests` ×4 — the chain node is never dispatched (`DispatchCount == 0`);
    the harness lacks the round-3 signing-admission inputs for a value node. Includes the sweep test.
  - `ActionPublishLifecycleTests.Publish_FanOutAction_Fails` — publish no longer rejects fan-out; needs a
    decision (durable scheduler supports fan-out vs AC-3a).
  - `GovernanceCommandValidationTests` ×8 — strict `ISurrealExecutor` mock misses the
    `governance_command_receipt` read; L1 governance area.

## Self-review (2 iterations)

- **Transaction scope:** no store or fence touched. Clone/propagate remain multi-statement
  non-transactional writes (pre-existing), now planned in full before the first write.
- **Security checklist:** authz on every changed route (policy + manager ownership); no acting-tenant
  change; input validation on `DependsOnNodeId`; no secrets/PII; no idempotency change; no bare ids into
  record links; existence oracles removed on every changed refusal.
- **§7 fail-closed classes:** no SurrealQL touched.
- **Pruning gate:** no raw SurrealQL, no catch-all, new manager methods use `<inheritdoc/>`, rationale in
  `Managers/AGENTS.md`, IDE0005 clean.
- **Behaviour changes to know:** a marketplace run whose HolonCreate/Clone node targets a parent owned by
  the action author (not the runner) is now refused — the ActingUserId invariant makes that a cross-user
  write. Dependency gates are satisfied by the runner's or owner's run only.
