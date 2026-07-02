---
type: spec
track: quest-value-engine-expressiveness
created: 2026-07-02
status: pending
---

# Track: quest-value-engine-expressiveness

## Overview

**Feature.** Successor to `quest-dag-semantic-hardening` (shipped 2026-07-02).
That track hardened definition-time safety; this one completes the DEFERRED
expressiveness features from the 2026-07-01 two-round review, whose goal is:
*"almost anyone can define their own economic value engine; flexible enough for
simple game quests bridging actions across games."* Seven locked features:

| # | Feature | Keystone file(s) |
|---|---|---|
| F1 | Output-binding (`{"$from": path}`) in node configs | `Services/Quest/QuestNodeConfig.cs`, `QuestNodeConfigRegistry.cs`, both engines |
| F2 | First-class failure arm: `QuestEdgeType.OnFailure` | `Managers/QuestManager.cs` skip loop, `Services/Quest/Workflow/*`, `QuestDagValidator` |
| F3 | `quest.emit` webhook events (generalized consent outbox) | `Services/Webhooks/`, `Services/Consent/ConsentWebhookEmitter.cs` |
| F4 | `QuestDependency` enforcement at run start | `Managers/QuestManager.cs:1241` `CheckDependenciesAsync` |
| F5 | Opt-in Holon AssetType/metadata registry | new `HolonTypeRegistry` table + Manager/Controller, `Managers/HolonManager.cs` |
| F6 | Unpublish TOCTOU guard (optimistic concurrency) | `Providers/Stores/Surreal/SurrealQuestStore.cs`, `QuestManager` publish/run-start |
| F7 | SDK + builder mirrors (thin) | `sdk/azoa-wallet/src/workflow/node-config.ts`, `frontend/src/components/quest-builder/` |

Greenfield pre-launch ([greenfield-prelaunch-no-compat]): semantics may change
outright, no compat shims.

## Background — verified findings & VERIFY resolutions (all locked)

All decisions below were verified against the code on 2026-07-02; **no open
questions remain.**

| # | VERIFY point | Resolution (verified evidence) |
|---|---|---|
| V1 | F1 registry shadow validation | **Strip `$from` properties from a shadow copy of the config JSON, then run the existing strict round-trip on the shadow.** `QuestNodeConfig.StrictOptions` only *disallows unknown members* (`UnmappedMemberHandling.Disallow`); missing members deserialize to defaults, so an absent (stripped) bound field passes while typos in non-bound fields still fail. Cheaper than type-appropriate placeholders (which would require per-field target-type reflection). v1 restriction: `$from` is legal only as an object **property value** (any depth), never as an array **element** (stripping an array element changes shape). |
| V2 | F2 mixed-incoming-edge rule | **A node runs iff (no Control/Conditional source is Failed-or-Skipped) AND (if the node has ≥1 incoming OnFailure edge, at least one OnFailure source is Failed).** An OnFailure source that Succeeded or was Skipped does not activate its edge; a node whose OnFailure trigger never fired is Skipped. This yields the must-hold property: GateCheck →(Control) A, GateCheck →(OnFailure) B gives exactly one of A/B running per run; cascade downstream of the skipped arm follows the existing FR-1 cascade-skip rules (`QuestManager.cs:395-419`). |
| V3 | F2 run-status derivation | Current rule `anyExec Failed ⇒ run Failed` (`QuestManager.cs:518-520`) would mark a *handled* failure as a failed run. **New rule: a Failed node execution is "handled" iff its node has ≥1 outgoing OnFailure edge (by construction the arm then ran); run is Failed iff any UNHANDLED Failed execution exists, else Succeeded.** |
| V4 | F2 POCO `[Inside]` | Confirmed: `Persistence/SurrealDb/Models/QuestEdge.cs:27-57` has nested `QuestEdgeTypeKind { Control, Conditional }` + `[Inside("Control", "Conditional")]`. Both get `OnFailure` added, mirroring the full domain enum; goldens regenerated via pipeline ([quest-run-status-inside-constraint-gap] lesson — never hand-edit `.surql`). |
| V5 | F2 durable seam | `QuestNodeStepHandler` records the execution `Failed` then returns `StepResult.Fail` at BOTH the fresh-failure seam (~line 266) and the idempotent-replay seam (~line 502); the saga retry of an already-Failed-recorded execution is a no-op replay, so consulting OnFailure at these two seams IS the "retries exhausted" point for deterministic failures (transient pre-record failures still burn saga retries first). If the failed node has exactly one OnFailure successor, advance to it via the same mechanism as the normal single-successor hop (~line 292) instead of `StepResult.Fail`. >1 OnFailure successors = publish-time error for the durable profile (mirrors the Control fan-out treatment). Chain-reconciliation park states (`AwaitingReconciliation`) are unchanged — OnFailure consult happens only on a terminal `Failed` record. |
| V6 | F3 outbox exists? | **YES — F3 is a generalization, not a descope.** Verified: `Services/Consent/ConsentWebhookEmitter.cs` (transactional-outbox enqueue), `Services/Webhooks/ConsentWebhookDeliveryWorker.cs` (hosted drainer with `WebhookHmacSigner` replay-resistant HMAC, `WebhookSsrfGuard` re-validated per POST, per-tenant secret), `Providers/Stores/Surreal/SurrealConsentWebhookOutboxStore.cs` (single-winner conditional transitions), `IWebhookRegistrationStore.GetByTenantAsync(tenantId)`. |
| V7 | F3 registration model | Consent webhooks resolve the endpoint **per TenantId** (`GetByTenantAsync(evt.TenantId)`). `QuestRun` carries `ActingTenantId` (`QuestManager.cs:351/550/1427`). **Lock: `quest.emit` delivers to the `WebhookRegistration` of `run.ActingTenantId`; a run with null `ActingTenantId` enqueues nothing (no addressable endpoint — silent no-op, logged debug).** The same registered endpoint receives both event families, distinguished by wire event type. |
| V8 | F3 enqueue transactionality | Quest execution writes are NOT in a transaction with the outbox (unlike the consent grant Upsert). **Lock: best-effort enqueue immediately after the Emit execution is recorded Succeeded; enqueue failure is logged and NEVER fails the node or run (observe-only, mirrors AC8 posture of the consent bridge).** |
| V9 | F5 registry scope | **Global registered `asset_type` (unique table-wide) with owner attribution; readable by all authenticated avatars; mutation IDOR-scoped to the registering owner** (user-locked preference; matches the "published vocabulary for cross-game predicates" goal — a per-avatar scope would defeat cross-game reads). |
| V10 | F6 conditional update | `SurrealQuestStore.UpsertQuestAsync` has NO conditional form. Precedents exist: `SurrealQuestRunStore.UpdateAsync(run, expectedStatus)` (`WHERE status = $_expected RETURN AFTER`, line ~188), `SurrealBridgeStore` (~line 511), saga store single-winner. **Lock: add a targeted `TryTransitionStatusAsync(questId, from, to)` to `IQuestStore`/`SurrealQuestStore` — a single-field conditional `UPDATE quest SET status = $_to WHERE status = $_from RETURN AFTER`; empty result = lost race.** Smaller and safer than a conditional whole-aggregate Upsert (which fans out to nodes/edges). |
| V11 | F6 ordering | **Lock: (i) `UnpublishAsync`: conditional flip Active→Draft FIRST (single-winner), THEN the in-flight-run check; if in-flight runs exist, revert to Active and refuse. (ii) run start (`ExecuteAsync` + `StartWorkflowRunAsync`): create the run row, THEN re-read quest status; if no longer Active, mark the run Cancelled (reason names the unpublish race) and return an error.** Every interleaving now fails in the safe direction: either unpublish sees the run row and refuses, or the run sees Draft and aborts. Worst case is a spurious refusal/abort — acceptable pre-launch; never a run against a Draft quest. |
| V12 | F1 path grammar authority | The `upstream.<node>.<jsonPath>` / `holon.<guid>.<field>` grammar lives inside `GatePredicateEvaluator` (private lexer, GUID-friendly post-dot segments, closed grammar). **Lock: extract a small shared path parser (e.g. `Services/Quest/Predicates/GatePath.TryParse`) used by both the evaluator's `PathNode` construction and the new binding resolver, so ONE grammar authority exists.** Binding roots in v1: `upstream.` and `holon.` only (`reads.` is GateCheck-config-local and has no meaning outside a gate). |
| V13 | F4 current state | `CheckDependenciesAsync` (`QuestManager.cs:1241-1268`) checks *any* Succeeded run of the depended-on quest — it ignores the avatar, `DependencyType`, and `DependsOnNodeId`, and nothing calls it on the execute paths. `QuestDependencyType = { Required, Optional }` confirmed (`Models/Quest/QuestEnums.cs:117`). Run terminal state is `QuestRunStatus.Succeeded` (not "Completed") — ACs use `Succeeded`. |
| V14 | F7 SDK surface | Node-config builders live at `sdk/azoa-wallet/src/workflow/node-config.ts` (pure builders mirroring `Models/Quest/NodeConfigs.cs`); no SDK string-literal `'Control'` edge-type union was found — the edge type surface is located and extended (or introduced) in Phase G. `publishQuest`/`unpublishQuest` already exist (`api/client.ts`, predecessor Phase G4). |

## Functional Requirements

### FR-1 (F1, keystone): Output-binding for node configs

Static configs are the ceiling: amounts/recipients/assets are frozen at
definition time, so any computed value flow escapes to tenant backends. Fix: a
binding syntax INSIDE config JSON.

**Locked design:**

- **Syntax:** any string-or-scalar config field may instead be the object
  `{"$from": "<path>"}` — exactly one key, string value. An object containing
  `$from` plus any other key is a validation error (ambiguous). v1: legal only
  as an object property value, never as an array element (V1).
- **Path grammar:** the SAME closed grammar GateCheck resolves —
  `upstream.<nodeName>.<jsonPath>` and `holon.<guid>.<field>` — via the shared
  parser extracted per V12. **No expressions, no arithmetic, path lookup ONLY**
  (the closed-grammar no-arbitrary-code guarantee is preserved).
- **Runtime resolution (both engines), BEFORE `TryDeserialize`:** a
  type-agnostic pre-pass (new `Services/Quest/QuestConfigBindingResolver`)
  walks the config JSON, resolves every `$from` node against the same scope
  GateCheck builds (`GateCheckNodeHandler.BuildScopeAsync` shape): upstream
  executions of the node's incoming edges keyed `upstream.<nodeName>` + holon
  reads keyed `holon.<id>`, **owner-scoped to the run avatar with the same
  not-found-indistinguishable-from-non-owned posture**. The resolved
  JsonElement is substituted verbatim (no type coercion — a type mismatch is
  caught by the strict deserialization that follows). The resolver is wired at
  the node-dispatch seam of BOTH the legacy loop (`QuestManager.ExecuteAsync`,
  before handler invocation ~line 481) and the durable engine
  (`QuestNodeStepHandler`, before handler dispatch).
- **Fail closed:** missing path / unresolvable member / non-owned holon /
  malformed binding object ⇒ the node **Fails** with a descriptive error
  (same posture as GateCheck) — never a silent default, never an exception
  escaping into the engine.
- **Definition-time validation:** `QuestNodeConfigRegistry.Validate` gains a
  binding-aware pre-pass: (a) every `$from` value must parse under the closed
  grammar; (b) strict round-trip runs on the `$from`-stripped shadow (V1).
  At **publish** additionally (graph is final): every `upstream.<nodeName>`
  prefix must name an actual direct upstream node (a source of an incoming
  edge of that node); `holon.` refs are checked for Guid syntax only
  (existence stays runtime). At node create/update only (a)+(b) run (edges may
  not exist yet).
- Applies to ALL node configs uniformly — the pre-pass is type-agnostic,
  including config-free registry entries (bindings still resolve; there is
  just no DTO round-trip).

**Acceptance criteria:**

- AC-1a: A Transfer node whose config `amount` is `{"$from": "upstream.gate.amount"}`
  executes with the upstream node's resolved value (unit: resolver + handler;
  legacy engine).
- AC-1b: The same binding resolves identically on the durable engine
  (`QuestNodeStepHandler` path; unit or manager-level).
- AC-1c: An unresolvable path (missing upstream member, holon not owned by the
  run avatar) fails the node closed with an error naming the path; downstream
  cascade-skip applies. No unhandled exception.
- AC-1d: Publish rejects: (i) a `$from` path that does not parse; (ii) an
  `upstream.<nodeName>` prefix naming a node that is not a direct upstream of
  the bound node; (iii) a `holon.` ref that is not Guid-shaped; (iv) a `$from`
  object with extra keys; (v) a `$from` in an array element.
- AC-1e: `QuestNodeConfigRegistry.Validate` accepts a config where a bound
  field replaces a scalar (shadow round-trip passes) while still rejecting an
  unknown member NOT under a `$from` (strict guarantee preserved).
- AC-1f: `holon.<id>` binding resolves against the holon's CURRENT state
  (typed fields + Metadata overlay, same as GateCheck's `HolonStateJson`).

### FR-2 (F2): First-class failure arm — `QuestEdgeType.OnFailure`

- New enum member `QuestEdgeType.OnFailure` (`Models/Quest/QuestEnums.cs:108`).
- **Legacy engine semantics** (skip loop, `Managers/QuestManager.cs:395-419`):
  the V2 rule — a node runs iff (no Control/Conditional source Failed/Skipped)
  AND (≥1 OnFailure source Failed, when any OnFailure edges exist). An
  OnFailure edge's target RUNS when its source Failed and is SKIPPED when its
  source Succeeded (inverse of Control). Cascade downstream of a run OnFailure
  branch behaves normally; downstream of a skipped arm cascades Skipped.
- **Run-status derivation** updated per V3 (handled-failure rule) at
  `QuestManager.cs:518-520`.
- **DAG validator** (`Services/QuestDagValidator.cs`): OnFailure targets are
  NOT orphans; reachability BFS includes OnFailure edges; the fan-out check
  counts Control edges only (unchanged); NEW durable-profile check — >1
  outgoing OnFailure edges from one node is an error where fan-out is an error
  (publish gate + `StartWorkflowRunAsync`) and a warning on the legacy path.
- **Durable engine** per V5: on a terminal Failed execution record, if the
  failed node has exactly one OnFailure successor, advance the saga to it (run
  continues down the failure arm) instead of `StepResult.Fail`; consult at both
  the fresh-failure and replay seams. This CLOSES the documented
  durable-skip-propagation divergence — update
  `Services/Quest/Workflow/AGENTS.md §skip-semantics` accordingly.
- **Edge input validation:** `Validators/QuestEdgeCreateModelValidator.cs` +
  `QuestEdgeAddModelValidator` accept `OnFailure`; `Condition` must be empty
  for OnFailure edges (condition is Conditional-only).
- **SurrealDB POCO** per V4: `QuestEdgeTypeKind` + `[Inside]` gain `OnFailure`;
  goldens regenerated via the pipeline.

**Acceptance criteria:**

- AC-2a (must-hold property): GateCheck →(Control) A, GateCheck →(OnFailure) B:
  gate passes ⇒ A Succeeded + B Skipped; gate fails ⇒ A Skipped + B ran —
  exactly one of A/B runs per run (unit, legacy engine; e2e in Phase H).
- AC-2b: Mixed incoming edges follow the V2 rule (unit matrix: Control-src
  states × OnFailure-src states).
- AC-2c: A run whose only Failed node has an OnFailure arm that ran ends
  `Succeeded`; a run with an unhandled Failed node ends `Failed` (V3).
- AC-2d: Validator — an OnFailure target is not reported as an orphan and is
  reachable; a node with 2 outgoing OnFailure edges fails publish and
  `StartWorkflowRunAsync` with a clear error, and is a warning on legacy
  execute.
- AC-2e: Durable engine — a quest `GateCheck →(Control) A / →(OnFailure) B`
  started via `StartWorkflowRunAsync` with a failing gate advances to B and
  the run reaches a terminal non-compensated state; with a passing gate it
  advances to A. `AGENTS.md §skip-semantics` divergence note updated to
  resolved.
- AC-2f: Edge input with `EdgeType=OnFailure` accepted on both input surfaces;
  `OnFailure` + non-empty `Condition` rejected. POCO `[Inside]` mirrors the
  full 3-value enum; schema goldens regenerated; schema tests green.

### FR-3 (F3): `quest.emit` webhook events

Generalize the consent-webhook bridge (V6 — it exists; NOT descoped).

- New event `quest.emit`: when an **Emit** node's execution is recorded
  Succeeded (both engines), enqueue a webhook event carrying
  `{runId, questId, nodeId, nodeName, payload, occurredAt}` where `payload` is
  the Emit node's output.
- **Addressing** per V7: the `WebhookRegistration` of `run.ActingTenantId`;
  null ActingTenantId ⇒ no-op.
- **New outbox**: `quest_webhook_event` SCHEMAFULL POCO + golden mirroring
  `consent_webhook_event`'s delivery columns (status/attempts/next_attempt_at/
  idempotency id); `SurrealQuestEmitOutboxStore` mirroring the consent store's
  single-winner conditional transitions.
- **Delivery**: reuse `WebhookHmacSigner` (timestamped HMAC over the
  length-prefixed preimage — replay-resistant incl. occurredAt),
  `WebhookSsrfGuard` (re-validated per POST), per-tenant secret. Extract the
  consent worker's delivery core into a shared component parameterized by
  (outbox store, payload builder, wire event type) OR run a second hosted
  worker instance of the generalized worker — implementer picks the smaller
  diff; both workers keep the fresh-scope-per-tick shape.
- **Observe-only** per V8: best-effort enqueue post-success; a failed enqueue
  or delivery NEVER blocks/fails the node or run. In-band Emit output stays
  primary; the Emit handler stays chain-free.

**Acceptance criteria:**

- AC-3a: Emit node success on a run with `ActingTenantId` set enqueues exactly
  one `quest.emit` outbox row with the locked payload shape (both engines;
  unit/manager-level with mocked store).
- AC-3b: Null `ActingTenantId` ⇒ no row; enqueue-store failure ⇒ node still
  Succeeded, run unaffected, warning logged.
- AC-3c: The delivery worker POSTs the event to the tenant's registered
  endpoint with a valid timestamped HMAC and idempotency id; SSRF-blocked url
  dead-letters without a POST; a still-Pending row is transitioned
  single-winner (mirrors consent worker tests).
- AC-3d: A skipped or failed Emit node enqueues nothing.

### FR-4 (F4): QuestDependency enforcement

- **Gate at run start** — `ExecuteAsync` AND `StartWorkflowRunAsync`, after
  the Active check (and before run-row creation): every `Required` dependency
  must be satisfied **for the EXECUTING avatar**: the depended-on quest has
  ≥1 `Succeeded` run belonging to that avatar; if `DependsOnNodeId` is set,
  that node has a `Succeeded` execution in some run of that avatar.
- `Optional` = no gate (advisory only, surfaces in `CheckDependenciesAsync`).
- Unsatisfied ⇒ run refused with an error **naming the missing dependency**
  (depended-on quest id + node id when set).
- `CheckDependenciesAsync` (`QuestManager.cs:1241`) is tightened to the same
  avatar-scoped, type-aware, node-aware semantics (it currently checks any
  avatar's runs and ignores type/node — V13).
- **Cross-game bridging note:** this is the same-avatar cross-quest gate — the
  avatar is the bridge. Game A's quest completion (by an avatar) unlocks game
  B's quest (for that same avatar); no cross-avatar or cross-tenant leakage.

**Acceptance criteria:**

- AC-4a: A quest with a Required dependency on quest Q refuses `ExecuteAsync`
  and `StartWorkflowRunAsync` for avatar X while X has no Succeeded run of Q;
  the error names Q. After X's run of Q succeeds, both start paths proceed.
- AC-4b: Avatar-scoped: Y's Succeeded run of Q does NOT satisfy X's gate.
- AC-4c: `DependsOnNodeId` set ⇒ satisfied only when that node Succeeded in
  some run of the executing avatar (a run where it was Skipped does not count).
- AC-4d: Optional dependencies never block; `CheckDependenciesAsync` reports
  them advisory-only and applies the same avatar/node scoping.

### FR-5 (F5): Holon AssetType/metadata registry (opt-in)

- New `holon_type_registry` SCHEMAFULL POCO + golden: `asset_type` (string,
  **globally unique** — V9, unique index), `owner_avatar_id` (attribution),
  `required_metadata_keys: string[]`, `allowed_metadata_keys: string[]`
  (empty = open beyond required), `description`, timestamps.
- New `HolonTypeRegistryManager` + `HolonTypeController` (role-first layout):
  register / update / delete (mutation IDOR-scoped to the registering owner —
  STARODK precedent) + list / get-by-asset-type (public read: any
  authenticated avatar).
- **ENFORCEMENT IS OPT-IN:** `HolonManager` Create/Update validates the
  resulting `Metadata` ONLY when the holon's `AssetType` matches a registered
  type: required keys present; a key is permitted if it is in
  `required_metadata_keys ∪ allowed_metadata_keys`; unknown keys are rejected
  only when `allowed_metadata_keys` is non-empty. Unregistered AssetTypes stay
  fully free-form.
- Docs benefit: the registry is the published vocabulary for cross-game
  GateCheck predicates (`holon.<id>.<key>`); note it in the relevant AGENTS.md.

**Acceptance criteria:**

- AC-5a: Registering a duplicate `asset_type` fails; update/delete by a
  non-owner returns not-found semantics (IDOR); any authenticated avatar can
  read.
- AC-5b: Creating/updating a holon of a registered type missing a required
  metadata key is rejected with the key named; with all required keys it
  succeeds.
- AC-5c: With non-empty `allowed_metadata_keys`, an unknown key is rejected;
  with empty `allowed_metadata_keys`, extra keys pass.
- AC-5d: Holons with unregistered AssetTypes are unaffected on every
  create/update path (regression).
- AC-5e: Schema golden generated; schema tests green.

### FR-6 (F6): Unpublish TOCTOU guard

- Per V10: `IQuestStore.TryTransitionStatusAsync(questId, from, to)` —
  single-winner conditional `UPDATE quest SET status WHERE status = $_expected
  RETURN AFTER` (precedents: `SurrealQuestRunStore.UpdateAsync(expectedStatus)`,
  `SurrealBridgeStore`).
- Per V11 ordering: `UnpublishAsync` = conditional flip Active→Draft →
  in-flight-run check → revert+refuse if runs found. `PublishAsync` uses the
  same primitive for Draft→Active (after validation + ExecutionOrder persist).
  Run start = create run row → re-read quest status → if not Active, mark the
  run Cancelled (reason names the unpublish race) and return an error.
- Update `Managers/AGENTS.md §publish-lifecycle` known-limitation note
  (predecessor FR-7c) to **resolved**, describing the V11 interleaving
  argument.

**Acceptance criteria:**

- AC-6a: Two concurrent unpublishes: exactly one wins the conditional flip;
  the loser gets a clean already-Draft/lost-race error (store-level unit +
  manager test).
- AC-6b: Unpublish with an in-flight run reverts to Active and refuses
  (existing behavior preserved through the new order).
- AC-6c: Run start that loses the race to an unpublish leaves the run
  Cancelled with a descriptive reason and returns an error; no node executes
  against a Draft quest (manager-level test simulating the interleaving).
- AC-6d: AGENTS.md limitation note flipped to resolved.

### FR-7 (F7): SDK + builder mirrors (thin)

- **TS SDK** (`sdk/azoa-wallet/`): `from(path: string): { $from: string }`
  helper in `src/workflow/node-config.ts`; builder params that accept scalars
  widen to `T | { $from: string }` where cheap (at minimum Transfer/Grant/
  Refund amounts + recipient/asset ids). Edge type surface (located per V14)
  accepts `OnFailure`. `publishQuest`/`unpublishQuest` already exist. Vitest
  coverage for the new builders/types.
- **Builder frontend** (`frontend/src/components/quest-builder/`): edge
  inspector EdgeType select gains OnFailure with a distinct visual style (red
  dashed, via the existing `edgeStyle(type)` helper); `dagWarnings`
  understands OnFailure (target not an orphan; error-level warning for >1
  OnFailure successors from one node, mirroring the fan-out treatment); NO
  special `$from` UI in v1 (raw JSON entry suffices) — the invalid-JSON
  blocking submit gate stays. Scoped `tsc --noEmit` on changed files only
  ([no-frontend-typecheck]).

**Acceptance criteria:**

- AC-7a: `from('upstream.gate.amount')` produces exactly `{"$from":"upstream.gate.amount"}`;
  a Transfer builder accepting a bound amount serializes the binding verbatim
  into Config (vitest).
- AC-7b: SDK edge types accept `'OnFailure'`; SDK `tsc --noEmit` clean;
  SDK test suite green.
- AC-7c: Builder: an OnFailure edge is selectable in the edge inspector,
  renders red-dashed, is not flagged as orphaning its target, and 2 OnFailure
  edges out of one node produce an error-level warning. Scoped tsc clean on
  changed files.

## Non-Functional Requirements

- **NFR-1 (build hygiene):** zero NEW warnings vs the 28-warning baseline
  ([build-warning-baseline-2026-06-16]; raw-count caveat per predecessor I-1).
- **NFR-2 (test policy):** TDD-light — tests are written per phase, but ONE
  integrated `dotnet build` + full test sweep at the very end (Phase H),
  including integration tests with SurrealDB up. The 12 quest e2e tests from
  the predecessor (`QuestLifecycleIntegrationTests` / `QuestSemanticsIntegrationTests`
  / `QuestArdanovaFlowIntegrationTests`) are the pattern to extend.
- **NFR-3 (docs convention):** terse one-line doc-comments; rationale in
  directory-level AGENTS.md (`Services/Quest/AGENTS.md §output-binding`,
  `Managers/AGENTS.md §onfailure-semantics` + §publish-lifecycle update,
  `Services/Quest/Workflow/AGENTS.md §skip-semantics` update).
- **NFR-4 (schema discipline):** every `Persistence/SurrealDb/Models/` change
  regenerates goldens via the pipeline; `.surql` never hand-edited; POCO
  `*Kind` enums mirror domain enums in full.
- **NFR-5 (commits):** `[quest-value-engine-expressiveness] <imperative>`.
- **NFR-6 (closed grammar):** no eval of any kind anywhere in F1 — the binding
  resolver performs path lookup only, through the shared parser (V12).

## Out of Scope (recorded, not planned)

1. Frontend test harness (separate pending request — none exists).
2. Arithmetic/expressions in bindings — `$from` is path lookup ONLY in v1.
3. Webhook events beyond `quest.emit` (no quest.completed/failed events yet).
4. Cross-tenant holon schema sharing UX (registry read is global; sharing
   workflows are not built).
5. `QuestNodeTemplate` stored `configSchema`/`inputSchema`/`outputSchema`
   enforcement (carried over from predecessor AC-4c).
6. Consent/mainnet items; bridge Tier-0 hardening
   ([bridge-unsafe-pre-launch]).
7. `$from` in array elements; type coercion of resolved binding values.
8. A dedicated `$from` builder UI in the frontend config inspector.

## Dependencies

- **quest-dag-semantic-hardening (shipped 2026-07-02)** — publish gate,
  cascade-skip, `QuestNodeConfig`/`QuestNodeConfigRegistry`, e2e harness
  pattern. Hard dependency; all seams verified present.
- tenant-consent-delegation (shipped) — webhook bridge machinery reused by F3.
- durable-workflow-engine / economic-primitive-nodes (shipped) — engines and
  node handlers extended here.
- SurrealDB integration harness (per-class namespace pattern, commit d80cf74)
  for Phase H e2e.
