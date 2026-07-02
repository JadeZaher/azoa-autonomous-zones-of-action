---
type: plan
track: quest-value-engine-expressiveness
created: 2026-07-02
status: pending
---

# Implementation Plan: quest-value-engine-expressiveness

## Overview

Eight phases. A–G build features (tests WRITTEN per phase, TDD-light: test
first, implement, refactor — but NOT run per-fix); H is the single integrated
build + test sweep including the new e2e suite (user test-execution policy).
Commits per phase: `[quest-value-engine-expressiveness] <imperative>`.
All file paths repo-relative. Spec §Background V1–V14 are the locked design
decisions — do not re-litigate them.

## Phase A: F1 — output-binding core (`$from`)

Goal: `{"$from": "<path>"}` resolves at dispatch time on both engines, fails
closed, and validates at definition/publish time (FR-1, AC-1a..1f).

Tasks:

- [ ] Task: Extract the shared path parser (V12). New
  `Services/Quest/Predicates/GatePath.cs` with `TryParse(string, out IReadOnlyList<string> segments, out string error)`
  using the exact lexer segment rules of `GatePredicateEvaluator` (GUID-friendly
  post-dot segments); refactor `GatePredicateEvaluator.ParsePath`/`PathNode` to
  consume it. Tests first: `GatePathTests` pin `upstream.n.a.b`, `holon.<guid>.status`,
  rejects (`a..b`, empty, operator chars, non-Guid where segment rules break).
- [ ] Task: Binding walker + resolver. New `Services/Quest/QuestConfigBindingResolver.cs`:
  (a) `FindBindings(configJson)` — walks JSON, yields `$from` property-value
  nodes with their paths; malformed binding objects (extra keys, non-string
  value, array-element position) are errors; (b)
  `TryResolveAsync(configJson, node, quest, upstreamExecutions, avatarId, ct, out resolvedJson, out error)`
  — builds the GateCheck-shaped scope (`upstream.<nodeName>` from incoming-edge
  source executions with non-empty Output; `holon.<id>` via `IHolonManager.GetAsync`
  owner-scoped, not-found == non-owned, reuse/extract `GateCheckNodeHandler.HolonStateJson`),
  substitutes resolved `JsonElement`s verbatim. Tests first:
  `QuestConfigBindingResolverTests` — happy path, nested property, missing
  member, non-owned holon, extra-key binding, array-element binding, no-binding
  passthrough (returns input unchanged, zero-cost).
- [ ] Task: Wire the legacy engine. In `Managers/QuestManager.cs` `ExecuteAsync`
  node loop (before handler dispatch, ~line 481) and the single-node run path
  (~line 575): resolve bindings; on error, record the execution `Failed` with
  the resolver error (cascade-skip then applies downstream). Tests:
  `QuestManagerBindingTests` — AC-1a ($from-bound Transfer amount reaches the
  handler; mock `TransferAsync` verify), AC-1c fail-closed + downstream skip.
- [ ] Task: Wire the durable engine. In `Services/Quest/Workflow/QuestNodeStepHandler.cs`
  before its config deserialize/handler dispatch: same resolve-or-record-Failed
  seam (the Failed record flows into the existing saga fail path). Test: AC-1b
  at handler level with upstream executions in scope.
- [ ] Task: Definition-time validation. Extend `Services/Quest/QuestNodeConfigRegistry.Validate`:
  binding pre-pass (grammar check per binding via `GatePath.TryParse` + root
  must be `upstream`/`holon` + `holon.` second segment Guid-shaped) then strict
  round-trip on the `$from`-stripped shadow (V1 — strip the property, run
  existing `StrictOptions` deserialize). Add publish-only overload/parameter
  taking the node's direct-upstream name set for the `upstream.<nodeName>`
  graph check; call it from the publish gate in `QuestManager.PublishAsync`.
  Tests: AC-1d (i)–(v), AC-1e.
- [ ] Task: Docs — `Services/Quest/AGENTS.md` §output-binding: syntax, V1
  shadow rationale, fail-closed posture, v1 restrictions (property-value only,
  no coercion, upstream/holon roots). One-line pointers in new files.
- [ ] Task: Commit Phase A.

## Phase B: F2 — OnFailure edge type (both engines + validator + POCO)

Goal: first-class failure arm with the exactly-one-arm property (FR-2,
AC-2a..2f); closes the durable-skip-propagation divergence.

Tasks:

- [ ] Task: Domain enum + POCO. Add `OnFailure` to `QuestEdgeType`
  (`Models/Quest/QuestEnums.cs:108`) and to
  `Persistence/SurrealDb/Models/QuestEdge.cs` `QuestEdgeTypeKind` +
  `[Inside("Control", "Conditional", "OnFailure")]` (V4). Regen goldens via
  the pipeline (`AZOA_REGENERATE_GOLDENS` escape hatch, predecessor B-2
  precedent) — never hand-edit `.surql`. Update the enum-mirror pinning test.
- [ ] Task: Legacy skip loop (V2+V3). Tests first —
  `QuestManagerOnFailureTests`: AC-2a exactly-one-arm both directions; AC-2b
  mixed-edge matrix (Control src Succeeded/Failed/Skipped × OnFailure src
  Succeeded/Failed/Skipped); OnFailure-src-Skipped does not activate; cascade
  below both arms; AC-2c run-status handled-failure rule. Implement in
  `Managers/QuestManager.cs`: skip decision at ~395–419 (OnFailure edges
  excluded from the Failed/Skipped-source skip rule; activation rule added)
  and run-status derivation at ~518–520 (Failed exec handled iff its node has
  ≥1 outgoing OnFailure edge).
- [ ] Task: DAG validator. `Services/QuestDagValidator.cs`: orphan check
  treats OnFailure targets as attached; reachability BFS traverses OnFailure
  edges; Control-only fan-out counting explicitly asserted unchanged; new
  check — >1 outgoing OnFailure edges = error when `fanOutAsError`/publish
  profile, warning on legacy. Tests in the existing validator suite (AC-2d).
- [ ] Task: Durable engine (V5). `Services/Quest/Workflow/QuestWorkflowEdges.cs`:
  add `ResolveOnFailureSuccessor(quest, nodeId)` (0/1/many; many is
  unreachable post-publish but guarded). `QuestNodeStepHandler.cs`: at the
  fresh-failure seam (~266) and the replay seam (~502), when the execution is
  terminally Failed and exactly one OnFailure successor exists, advance to it
  via the same mechanism as the ~292 single-successor hop instead of
  `StepResult.Fail`; chain-park states untouched. Tests: AC-2e both
  directions at step-handler level.
- [ ] Task: Edge input surfaces. `Validators/QuestEdgeCreateModelValidator.cs`
  + `QuestEdgeAddModelValidator`: accept OnFailure; reject OnFailure +
  non-empty Condition (AC-2f). Confirm enum JSON binding accepts the new
  member end-to-end (controller model binding test if a suite exists).
- [ ] Task: Docs — update `Services/Quest/Workflow/AGENTS.md` §skip-semantics:
  divergence RESOLVED via OnFailure advance; add `Managers/AGENTS.md`
  §onfailure-semantics (V2 rule + V3 handled-failure rationale).
- [ ] Task: Commit Phase B.

## Phase C: F6 — unpublish TOCTOU guard + lifecycle docs

Goal: optimistic-concurrency on the quest status transition; V11 ordering
(FR-6, AC-6a..6d).

Tasks:

- [ ] Task: Store primitive (V10). `Interfaces/Stores/IQuestStore` +
  `Providers/Stores/Surreal/SurrealQuestStore.cs`:
  `TryTransitionStatusAsync(Guid questId, QuestStatus from, QuestStatus to, CancellationToken)`
  — `UPDATE type::record($_t, $_id) SET status = $_to WHERE status = $_from RETURN AFTER`
  (mirror `SurrealQuestRunStore.UpdateAsync` expectedStatus + bridge-store
  precedent); empty result ⇒ lost-race error result. Test first at store level
  (integration-style if the store suite runs against SurrealDB; else pinned
  via the store's existing test seam).
- [ ] Task: Manager ordering (V11). `Managers/QuestManager.cs`:
  `UnpublishAsync` (~223) = conditional flip → in-flight check → revert+refuse;
  `PublishAsync` (~190) = validate → persist ExecutionOrder → conditional
  Draft→Active flip (lost race = clean error). Run start (`ExecuteAsync` +
  `StartWorkflowRunAsync`): after run-row creation, re-read status; not Active
  ⇒ mark run Cancelled with race-naming reason + return error. Tests: AC-6a
  (mock store returns lost-race), AC-6b, AC-6c interleaving simulation.
- [ ] Task: Docs — flip `Managers/AGENTS.md` §publish-lifecycle TOCTOU
  known-limitation (predecessor FR-7c) to resolved with the V11 interleaving
  argument (AC-6d).
- [ ] Task: Commit Phase C.

## Phase D: F4 — QuestDependency enforcement

Goal: Required dependencies gate run start, avatar-scoped (FR-4, AC-4a..4d).

Tasks:

- [ ] Task: Satisfaction helper. Tests first — `QuestDependencyEnforcementTests`:
  AC-4a/4b/4c/4d matrix. Implement a private
  `CheckRequiredDependenciesAsync(quest, executingAvatarId)` in
  `Managers/QuestManager.cs`: for each `Required` dep, `_runStore.GetByQuestIdAsync`
  filtered to the executing avatar's `Succeeded` runs; when `DependsOnNodeId`
  set, `_executionStore.GetByRunIdAsync` per candidate run — node execution
  `Succeeded` (Skipped ≠ satisfied). Error message names the depended-on quest
  (and node) — verify a store query shape that avoids N+1 where cheap.
- [ ] Task: Gate both start paths. Call the helper in `ExecuteAsync` and
  `StartWorkflowRunAsync` after the Active check and BEFORE run-row creation;
  refusal returns the naming error.
- [ ] Task: Tighten `CheckDependenciesAsync` (~1241): same avatar-scoped,
  type-aware, node-aware logic via the shared helper; Optional deps surface as
  advisory (unsatisfied-but-not-blocking distinction in `DependencyCheckResult`
  — extend the model minimally if needed).
- [ ] Task: Docs — `Managers/AGENTS.md` §quest-dependencies: same-avatar
  cross-quest gate, "the avatar is the bridge" cross-game note.
- [ ] Task: Commit Phase D.

## Phase E: F3 — quest.emit webhooks

Goal: Emit success → outbox row → HMAC-signed delivery to the acting tenant's
registered endpoint; observe-only (FR-3, AC-3a..3d).

Tasks:

- [ ] Task: Outbox model + store. New POCO
  `Persistence/SurrealDb/Models/QuestEmitWebhookEvent.cs` (`quest_webhook_event`,
  SCHEMAFULL: tenant_id, run_id, quest_id, node_id, node_name, payload (json
  string), occurred_at, status `[Inside]` mirroring the consent delivery-status
  enum, attempt_count, next_attempt_at, last_error, idempotency_id, created_at)
  + regen goldens. New `Providers/Stores/Surreal/SurrealQuestEmitOutboxStore.cs`
  + interface, mirroring `SurrealConsentWebhookOutboxStore` (Enqueue /
  DequeueDue / MarkDelivered / MarkRetry / DeadLetter — single-winner
  `WHERE status = $_pending` transitions). Tests mirror the consent store's.
- [ ] Task: Emit seam (V7/V8). New `Services/Quest/QuestEmitWebhookEmitter.cs`
  (interface + impl, DI-registered in `Program.cs`): builds the event from
  (run, node, execution output), no-ops on null `run.ActingTenantId`,
  best-effort enqueue (log-warn on failure, never throw). Call it where the
  Emit node's execution is recorded Succeeded in BOTH engines: legacy loop
  terminal update (`QuestManager.cs` ~490–508) and
  `QuestNodeStepHandler` success path. Tests: AC-3a both engines (mocked
  store), AC-3b, AC-3d (skipped/failed Emit enqueues nothing).
- [ ] Task: Delivery worker generalization. Extract the reusable delivery core
  from `Services/Webhooks/ConsentWebhookDeliveryWorker.cs` (registration
  lookup via `IWebhookRegistrationStore.GetByTenantAsync`, SSRF re-check,
  timestamped HMAC via `WebhookHmacSigner`, retry/dead-letter policy from
  `WebhookOptions`) parameterized by (outbox store adapter, wire event type
  `quest.emit`, payload serializer); keep the consent worker's behavior
  byte-compatible (its existing tests must stay green unmodified — the
  refactor is wrong if they don't). Register the quest drainer as a hosted
  worker in `Program.cs`. Tests: AC-3c via the same harness style as the
  consent worker's tests.
- [ ] Task: Docs — `Services/Webhooks/AGENTS.md`: two event families, shared
  core, per-tenant isolation, quest.emit observe-only posture (V8).
- [ ] Task: Commit Phase E.

## Phase F: F5 — Holon type registry (opt-in)

Goal: published AssetType vocabulary with opt-in metadata enforcement (FR-5,
AC-5a..5e).

Tasks:

- [ ] Task: POCO + store. `Persistence/SurrealDb/Models/HolonTypeRegistry.cs`
  (`holon_type_registry`, SCHEMAFULL, unique index on `asset_type`,
  owner_avatar_id, required_metadata_keys, allowed_metadata_keys, description,
  timestamps) + regen goldens. `Providers/Stores/Surreal/SurrealHolonTypeRegistryStore.cs`
  + interface: Upsert / GetByAssetType / List / Delete; duplicate asset_type
  surfaces the unique-index violation as a clean error.
- [ ] Task: Manager + controller. `Managers/HolonTypeRegistryManager.cs`
  (register/update/delete owner-IDOR — lookup scoped by id + authenticated
  avatar, body-supplied owner ignored, STARODK precedent; list/get public
  read) + `Controllers/HolonTypeController.cs` (POST/PUT/DELETE/GET list/GET
  by asset-type), DI in `Program.cs`. Tests: AC-5a manager-level.
- [ ] Task: Opt-in enforcement. `Managers/HolonManager.cs` Create/Update: when
  `AssetType` matches a registered type, validate resulting Metadata (required
  present; permitted = required ∪ allowed; unknown rejected only when allowed
  non-empty). Unregistered types untouched. Tests: AC-5b/5c/5d incl.
  regression on every create/update path.
- [ ] Task: Docs — `Managers/AGENTS.md` §holon-type-registry: opt-in
  rationale, global-scope decision (V9), GateCheck vocabulary benefit.
- [ ] Task: Commit Phase F.

## Phase G: F7 — SDK + builder mirrors (thin)

Goal: thin client mirrors; no new frontend machinery (FR-7, AC-7a..7c).

Tasks:

- [ ] Task: SDK bindings. `sdk/azoa-wallet/src/workflow/node-config.ts`:
  export `from(path: string): { $from: string }` + `Bound<T> = T | { $from: string }`;
  widen Transfer/Grant/Refund/Swap builder scalar params to `Bound<...>` where
  cheap. Vitest: AC-7a (exact wire shape, builder serialization).
- [ ] Task: SDK edge types. Locate the QuestEdge/edgeType surface in
  `sdk/azoa-wallet/src` (V14 — likely `api/client.ts` types or untyped);
  extend/introduce the `'Control' | 'Conditional' | 'OnFailure'` union.
  Vitest + `npx tsc --noEmit` in the SDK (AC-7b).
- [ ] Task: Builder edge inspector + styles.
  `frontend/src/components/quest-builder/quest-canvas.tsx` (+ `dag-flow.tsx`
  if styles live there): EdgeType select gains OnFailure; `edgeStyle(type)`
  adds red-dashed for OnFailure (applied at connect/preset-load/patch time,
  predecessor G2 pattern); Condition input hidden for OnFailure.
- [ ] Task: dagWarnings. OnFailure targets not orphans; error-level warning
  for >1 OnFailure successors from one node ("won't publish / durable profile
  requires a single failure arm"); fan-out counting stays Control-only.
  Invalid-JSON blocking submit gate untouched; NO `$from` UI (out of scope §8).
- [ ] Task: Verification (scoped, no full harness): SDK `npm test` + SDK tsc;
  frontend scoped `tsc --noEmit` on changed files only
  ([no-frontend-typecheck]). Update `API_SYNC.md` if any new endpoint rows
  apply (HolonTypeController from Phase F).
- [ ] Task: Commit Phase G.

## Phase H: Single integrated sweep + new e2e

Goal: ONE build+test sweep proving the whole track (NFR-1/NFR-2); new e2e
extends the predecessor's 12-test integration pattern
(`tests/AZOA.WebAPI.IntegrationTests/`, per-class SurrealDB namespace,
`ArdanovaSimulatedFactory` precedent for Tier-2).

Tasks:

- [ ] Task: e2e — OnFailure branch. New `QuestOnFailureIntegrationTests`:
  create → publish → execute a GateCheck →(Control) success-arm /
  →(OnFailure) failure-arm quest through the HTTP layer; gate-pass run asserts
  success arm Succeeded + failure arm Skipped; gate-fail run asserts the
  inverse AND run status `Succeeded` (handled failure, AC-2c) — exactly one
  arm per run (AC-2a at HTTP layer).
- [ ] Task: e2e — $from-bound Transfer amount. Quest with an upstream node
  emitting an amount + a Transfer whose config amount is
  `{"$from":"upstream.<node>.<field>"}`, executed against the simulated
  provider (`ArdanovaSimulatedFactory`, wallet seeded per predecessor H-4);
  assert the RESOLVED amount reached the handler (execution row / error
  message carries it) and the node reaches a terminal state — honest-outcome
  posture per H-4, never faked.
- [ ] Task: e2e — dependency-gated run. Quest B Required-depends on quest A:
  B's execute refused with A named; run A to Succeeded (same avatar); B then
  executes. Second avatar still refused (AC-4b at HTTP layer).
- [ ] Task: Integrated sweep (run ONCE, in order):
  1. `dotnet build AZOA.WebAPI.csproj` — 0 errors, zero NEW warnings vs
     baseline ([build-warning-baseline-2026-06-16]).
  2. `dotnet test tests/AZOA.WebAPI.Tests/` — unit + schema (goldens for
     quest_edge, quest_webhook_event, holon_type_registry regenerated in
     B/E/F; `[Inside]` mirror tests green).
  3. SurrealDB up (podman) → `dotnet test tests/AZOA.WebAPI.IntegrationTests/`
     — predecessor 12 stay green; new e2e green; 37 pre-existing failures
     (PASSOFF buckets) unchanged, no new failures.
  4. SDK: `npm test` + `npx tsc --noEmit` (already run scoped in G; confirm).
  Fix-forward anything surfaced, then re-run the affected stage once.
- [ ] Verification: record per-suite results + any sweep-surfaced fixes in
  `conductor/tracks/quest-value-engine-expressiveness/NOTES.md` (decision-log
  style, predecessor precedent). [checkpoint marker]
- [ ] Task: Final commit + track wrap-up.
