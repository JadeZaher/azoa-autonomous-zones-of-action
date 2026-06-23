# Workflow SDK — Specification

## Status
Decision record + track. **Tier 1 — the consumer surface** of the
`workflow-engine` initiative (Track 4 of 4: value-path-wiring →
durable-workflow-engine → economic-primitive-nodes → **workflow-sdk**).
Direction sourced from `conductor/REVIEW-economic-substrate-2026-06-16.md`
**Part C** (Quest as the economic-workflow substrate). Nothing in this track is
implemented yet.

## Goal
Extend the cross-platform TypeScript SDK `@azoa/wallet-sdk` (at
`sdk/azoa-wallet/`) so a consumer app — **ArdaNova** first, any future app
after — can, **from its own backend**:

1. **DESIGN** a workflow *shape* once as a `QuestTemplate` (a DAG of generic
   nodes with `{{param}}` slots), and
2. **DRIVE** many actors (player / user / tenant) through **durable,
   step-addressable** quest runs of that shape.

The headline ergonomics is the user's pseudocode:

```ts
quest(holonStep1).step(holonStep2B)
```

— a fluent run driver where `quest(...)` opens a run handle and `.step(nodeId)`
pushes the actor into the next phase. The chain maps **1:1** onto the
`durable-workflow-engine` advancement endpoints (`advance` / `signal`), and the
SDK supports the same **HYBRID** model the engine exposes: a consumer can drive
**phase-by-phase** (`.step()`) OR **start-and-signal-at-gates** (`.start()` then
`.signal()`).

The locked constraint of the whole initiative holds here too: **the economic /
token domain stays in ArdaNova; AZOA — and therefore this SDK — exposes
mechanism-only primitives.** This SDK ships **no ArdaNova types** and no
economic semantics; it types the *generic* workflow mechanism only.

## Background — the substrate this track wraps (file:line evidence)

### What the SDK has today
The SDK is a typed HTTP client + fluent facades over the AZOA WebAPI, built for
browser / React Native / Lynx (`azoa-wallet-sdk_20260509/spec.md:5`, FR-8
`:95-104`).

- The facade `AzoaClient` composes `api` + `wallet` + `session` + `auth` +
  `holons` + `portfolio` (`sdk/azoa-wallet/src/client/azoa-client.ts:84-147`).
  Auth is JWT **or** API key: a Bearer token wins, else `X-Api-Key` is sent
  (`sdk/azoa-wallet/src/api/client.ts:1265-1269`); token refresh is
  deduplicated via `_refreshInFlight`
  (`sdk/azoa-wallet/src/api/client.ts:1291-1306`).
- The API client **already wraps the quest template surface** delivered by
  `quest-api`:
  - `createQuestTemplate` → `POST /api/quest/templates`
    (`sdk/azoa-wallet/src/api/client.ts:1042-1044`),
  - `getQuestTemplate` → `GET /api/quest/templates/{id}`
    (`:1047-1050`),
  - `listQuestTemplates` → `GET /api/quest/templates` (`:1053-1055`),
  - `instantiateQuestTemplate(templateId, parameters?)` →
    `POST /api/quest/templates/{id}/instantiate` (`:1058-1061`) — the
    `{{param}}` substitution entrypoint.
  - Quest DAG CRUD + the all-at-once `executeQuest` /
    `executeQuestNode` already exist (`:992-1037`), and the quest DTOs
    (`QuestResult`, `QuestNodeResult`, `QuestTemplateResult`, …) are typed at
    `sdk/azoa-wallet/src/api/client.ts:382-533`.
- **There is NO durable-run driver surface today.** A grep confirms the SDK has
  no `advance`, `signal`, `run-status`, or `forActor` method, and no
  `QuestRunStatus` type — `executeQuest` is the synchronous all-at-once call,
  not the step-addressable durable run this track needs. The advancement
  endpoints it must wrap are delivered by `durable-workflow-engine` (below).

### The backend contract this SDK binds to (sibling tracks)
- **`durable-workflow-engine`** (Track 2) ships the durable-run advancement
  surface on `QuestController` and the new run states:
  - `POST runs/{runId}/advance` — body `{ fromNodeId }` — the `step(nodeId)`
    primitive; resumes a `Suspended` manual-advance run from a node into its
    successor(s) (`durable-workflow-engine/plan.md:37`,
    `durable-workflow-engine/spec.md:150-156`).
  - `POST runs/{runId}/signal` — body `{ gateId, payload }` — delivers an
    external signal to a parked gate node
    (`durable-workflow-engine/plan.md:37`,
    `durable-workflow-engine/spec.md:150-156`).
  - A wait/timer node fires via the saga trigger — **no endpoint**
    (`durable-workflow-engine/plan.md:37`).
  - New run states `Suspended` / `AwaitingSignal` / `AwaitingTimer` on
    `QuestRunStatus` (`durable-workflow-engine/spec.md:138-142`,
    `:188-190`), plus the run read surface (`quest-api` shipped the
    `QuestRun` read methods per project memory).
  Both endpoints are `[Authorize]`, avatar-scoped from claims
  (`durable-workflow-engine/plan.md:37`). **Where these endpoints are not yet
  final, this SDK specs each method against the documented CONTRACT
  (request/response shape) and records the dependency.**
- **`economic-primitive-nodes`** (Track 3) ships the generic node types the
  templates compose — `GateCheck` / `Swap` / `Transfer` / `Grant` / `Hold` /
  `Refund` / `Emit` — each with an opaque JSON `Config`
  (REVIEW Part C #1/#2, `conductor/REVIEW-economic-substrate-2026-06-16.md:184-190`).
  This SDK types the **generic mechanism params** of those configs (optional
  builders, §FR-5); it does **not** type ArdaNova economics.
- **`quest-api`** ✓ shipped — template CRUD + instantiate + run read surface
  (already wrapped, above).
- **`tenant-onboarding`** ✓ shipped — issues a short-lived **child credential**
  via `POST /api/tenant/avatars/{id}/credential`
  (`tenant-onboarding/spec.md:124-131`); asserts `child.OwnerTenantId == tenant`
  before issuing; the credential's subject is the child avatar
  (`tenant-onboarding/spec.md:85-89`). This SDK threads that child credential so
  a tenant principal can act FOR a child actor (§FR-3).

## Functional Requirements

### FR-1: Template authoring surface (DESIGN once)
**Description:** Typed, ergonomic methods so a consumer DESIGNS a workflow shape
once and runs many actors through it. Wrap the existing template endpoints with
a thin, discoverable namespace (e.g. `azoa.workflow.templates`) over the
already-present `createQuestTemplate` / `getQuestTemplate` /
`listQuestTemplates` / `instantiateQuestTemplate`
(`sdk/azoa-wallet/src/api/client.ts:1042-1061`).
**Acceptance Criteria:**
- `createTemplate(params)` creates a `QuestTemplate`
  (`POST /api/quest/templates`); returns the typed `QuestTemplateResult`.
- `getTemplate(templateId)` / `listTemplates()` read templates; `assertUuid` on
  `templateId`.
- `instantiate(templateId, params)` instantiates a run from a template with
  `{{param}}` values (`POST /api/quest/templates/{id}/instantiate`), returning
  the run handle (FR-2) — not just the raw `QuestResult` — so DESIGN flows
  straight into DRIVE.
- No new HTTP plumbing: these reuse `AzoaApiClient.request` and the existing
  typed DTOs. The namespace is **additive ergonomics**, the wire calls are
  unchanged.
**Priority:** P0

### FR-2: Fluent run driver (the headline ergonomics — DRIVE)
**Description:** A `quest(questOrTemplateId)` entrypoint returning a **run
handle** whose chainable methods map 1:1 onto the durable-workflow-engine
advancement endpoints. This is the literal `quest(step1).step(step2B)`.
**Acceptance Criteria:**
- `quest(idOrRef)` opens a handle. `idOrRef` is a quest id, a template id, or a
  prior run id (disambiguated by an explicit `.fromTemplate()` / `.forRun()`
  modifier or by the entrypoint variant — see Open Questions Q1).
- `.start({ actor, params })` instantiates + starts a durable run for `actor`
  (an avatar id; see FR-3) with `{{param}}` values. Returns the handle (now
  bound to a concrete `runId`) so the chain continues.
- `.step(nodeId)` issues `POST runs/{runId}/advance` with body
  `{ fromNodeId: nodeId }` — the consumer-driven advance. **Chainable:**
  `quest(a).start({...}).step(b).step(c)` issues `start → advance(b) →
  advance(c)` in order.
- `.signal(gateId, payload)` issues `POST runs/{runId}/signal` with body
  `{ gateId, payload }` — un-parks a gated node. The hybrid model is explicit:
  `.start(...)` then `.signal(...)` is the start-and-signal-at-gates path;
  `.step(...)` is the phase-by-phase path; both compose on the same handle.
- `.status()` polls the run + node-execution state (incl. `Suspended` /
  `AwaitingSignal` / `AwaitingTimer`) via the run read surface; returns a typed
  `WorkflowRunStatus`.
- `.onSuspend(cb)` registers a callback invoked when a `.step()` / `.signal()` /
  `.start()` call leaves the run in a suspended/awaiting state, surfacing the
  resume point (which node, what it awaits). Result accessors expose the latest
  `runId`, `status`, and terminal output.
- Every interpolated `runId` / `nodeId` / `gateId` is `assertUuid`-guarded
  before URL interpolation (mirrors `sdk/azoa-wallet/src/api/client.ts:569-576`).
  (`gateId` is a node id in the engine's model — `StepName = nodeId` per
  `durable-workflow-engine/plan.md:24,30` — so the UUID guard applies; if the
  final contract makes `gateId` a free string, relax to a non-empty-string guard
  and record it — Open Questions Q4.)
- The chain returns a thenable/awaitable handle so `await quest(a).start(...)
  .step(b)` resolves to the final `Result<WorkflowRunStatus, SdkError>`, and any
  step failing short-circuits with the `SdkError` (no throw — Result discipline,
  `azoa-wallet-sdk_20260509/spec.md:124-128`).
**Priority:** P0

### FR-3: Actor abstraction (act FOR a child avatar)
**Description:** The actor a run targets is an AZOA **Avatar**. For
ArdaNova-as-tenant, the actor is a **child avatar** provisioned via
`tenant-onboarding`. The SDK lets a **tenant principal** (authed by
`X-Api-Key`) act FOR a child by acquiring and using the child's short-lived
credential.
**Acceptance Criteria:**
- `quest(...).forActor(childAvatarId)` (and/or `.start({ actor: childAvatarId
  })`) acquires a child credential via
  `POST /api/tenant/avatars/{childAvatarId}/credential`
  (`tenant-onboarding/spec.md:124-131`) and uses the returned child JWT as the
  Bearer token for that run's advancement calls — while the tenant's
  `X-Api-Key` remains the principal for the credential-acquisition call itself.
- Credential acquisition is **lazy and cached per actor for the handle's
  lifetime**; re-acquired on expiry (reuse the existing refresh-dedup discipline,
  `sdk/azoa-wallet/src/api/client.ts:1291-1306`, rather than inventing a second
  refresh path).
- When the consumer is **not** a tenant (a direct end-user JWT session),
  `.forActor()` is unnecessary — the run uses the active session token; the
  child-credential path is only engaged when a tenant principal targets a child.
- `assertUuid(childAvatarId, "childAvatarId")` before interpolation; the
  credential call is verbose-errored with method+path on failure.
- **No tenant/child types leak into the generic surface** beyond the child
  credential acquisition — `forActor` takes a plain avatar id; the
  tenant-credential mechanics are an internal auth detail, not an ArdaNova
  concept (NO brand leak, House Rules).
**Priority:** P0

### FR-4: Errors + idempotency on value-moving advances
**Description:** Advancement calls that move value carry an `Idempotency-Key`,
reusing the existing header plumbing; all ids are guarded; errors are verbose.
**Acceptance Criteria:**
- `.step(...)` and `.signal(...)` accept an optional `{ idempotencyKey }` that
  sets the `Idempotency-Key` request header, reusing the `extraHeaders` path
  proven by `executeSwap` (`sdk/azoa-wallet/src/api/client.ts:879-893`). When
  absent, the server falls back to its deterministic content key (same contract
  as swap).
- All amounts that appear in node configs or run params are **strings**
  (arbitrary precision — project memory; `azoa-wallet-sdk_20260509` amount
  convention). The SDK never coerces an amount to `number`.
- `assertUuid` on `runId` / `nodeId` / `gateId` / `avatarId` /
  `childAvatarId` / `templateId` (path-traversal guard, the project-wide rule).
- Every error carries `method + path` (the SDK's verbose-error convention,
  `sdk/azoa-wallet/src/api/client.ts:1202-1217`); methods return
  `Result<T, SdkError>`, never throw (except the synchronous `assertUuid`
  input-guard throw, matching `updateSTARODK`'s pre-send throw at
  `sdk/azoa-wallet/tests/api/self-audit-one-fix.test.ts:290-295`).
**Priority:** P0

### FR-5: Typed node-config builders (RECOMMENDED: ship light builders)
**Description:** Optional typed helpers so a consumer composes template node
configs type-safely for the **generic** `economic-primitive-nodes` mechanism
params (`GateCheck` predicate, `Swap` params, `Grant` params, …) — **without**
encoding any economics.
**Recommendation:** **Ship light builders** (a `nodeConfig.gateCheck({...})` /
`.swap({...})` / `.grant({...})` family returning the typed `Config` JSON
string) **plus** allow raw typed config objects. Rationale: the configs are
opaque JSON on the wire (`QuestNode.Config`,
`durable-workflow-engine/plan.md:18`); a thin typed builder catches shape errors
at author time and documents the mechanism params, while raw objects stay
available for forward-compat when `economic-primitive-nodes` adds a node type
the SDK hasn't typed yet. The builders are **pure** (no I/O), so they cost
nothing at runtime and tree-shake away when unused.
**Acceptance Criteria:**
- A `nodeConfig` namespace exposes typed builders for each shipped generic node
  type, each returning the serialized `Config` the template DTO expects.
- The builders type **only** the generic mechanism params (a `GateCheck`
  predicate descriptor, a `Swap` token-in/out + amount **string**, a `Grant`
  recipient + amount **string**) — **never** a rate, a token meaning, or any
  ArdaNova economic concept.
- A raw `Config` string is always accepted as an escape hatch (forward-compat
  for un-typed node kinds).
- Builder output is **deferred** until `economic-primitive-nodes` finalizes the
  config shapes; until then the builder types are specced against the documented
  contract and gated behind the dependency (so this track can land the driver +
  template + actor surface first and add builders when Track 3 lands — see
  plan.md phasing).
**Priority:** P1

### FR-6: Tests (mirror the existing ~82-test vitest suite)
**Description:** vitest tests that mock the API client / `fetch` and prove the
fluent chain issues the correct ordered HTTP calls and honors every guard.
**Acceptance Criteria:**
- **Ordered-call proof:** `quest(a).start({...}).step(b).step(c)` issues, in
  order: the instantiate/start call, `POST runs/{runId}/advance {fromNodeId:b}`,
  then `{fromNodeId:c}` — asserted against `mockFetch.mock.calls` exactly as
  `self-audit-one-fix.test.ts:218-245` asserts URL/method/body.
- **Hybrid proof:** `quest(a).start({...}).signal(g, payload)` issues
  `start → POST runs/{runId}/signal {gateId:g, payload}`.
- **Idempotency passthrough:** `.step(..., {idempotencyKey})` sets the
  `Idempotency-Key` header (mirror `self-audit-one-fix.test.ts:247-257`).
- **assertUuid guards:** a non-UUID `runId`/`nodeId`/`gateId`/`childAvatarId`
  throws before any `fetch` (mirror `self-audit-one-fix.test.ts:290-295`).
- **Child-credential acquisition:** `quest(...).forActor(childId).start({...})`
  first issues `POST /api/tenant/avatars/{childId}/credential` (with the
  tenant `X-Api-Key`), then uses the returned child JWT as the `Authorization:
  Bearer` header on the advance/signal calls.
- **Status mapping:** `.status()` returns a typed `WorkflowRunStatus` reflecting
  `Suspended`/`AwaitingSignal`/`AwaitingTimer`; `.onSuspend` fires when a call
  leaves the run awaiting.
- Tests use the existing `ApiConfigBuilder` + `vi.stubGlobal("fetch", …)`
  harness (`sdk/azoa-wallet/tests/api/self-audit-one-fix.test.ts:13-24`).
**Priority:** P0

## Non-Functional Requirements

### NFR-1: Cross-platform (no Node-only / no Buffer)
No `btoa`/`atob`/`Buffer`; pure-JS encoding only (`core/encoding.ts`), per
project memory and `azoa-wallet-sdk_20260509/spec.md:95-104`. The driver is
plain `fetch` + JSON; it introduces no platform-specific API. Targets browser /
React Native / Lynx unchanged.

### NFR-2: Build + types unchanged
ESM + CJS + DTS via tsup with the existing entry-point map; SDK `tsc` clean. New
public symbols are exported from `sdk/azoa-wallet/src/index.ts` and documented.

### NFR-3: Error model
All public methods return `Result<T, SdkError>` (discriminated union, no
thrown exceptions except the synchronous input-guard `assertUuid` throw); errors
carry `code`, `method+path` message, and `cause`
(`azoa-wallet-sdk_20260509/spec.md:124-128`).

### NFR-4: No brand leak
The SDK is a **generic** workflow SDK. **Zero** ArdaNova types, names, or
economic concepts appear in `sdk/azoa-wallet/src/`. `forActor` takes a plain
avatar id; node-config builders type only generic mechanism params. The
worked economic example (swap→hold→grant) lives in docs as *illustration*, never
as a typed ArdaNova entity.

### NFR-5: Testing parity
vitest, mocked API client / `fetch`, mirroring the existing ~82-test suite
shape and the `ApiConfigBuilder` harness. No live-network tests.

## User Stories

### US-1: Design once, run many
**As** an ArdaNova backend developer,
**I want** to define a workflow shape once as a template and instantiate a run
per user,
**So that** every user follows the same governed multi-phase flow.

**Given** a tenant `AzoaClient` (API-key auth) and a designed template
**When** I call `azoa.workflow.templates.instantiate(templateId, { amount:
"1000" })` for each user
**Then** each call returns a bound run handle ready to drive.

### US-2: Phase-by-phase drive (`quest(step1).step(step2B)`)
**As** an ArdaNova backend,
**I want** to push a user from one phase to the next on my schedule,
**So that** the user advances only when my business logic says so.

**Given** a started run handle
**When** I `await quest(questId).start({ actor, params }).step("node-2b")`
**Then** the SDK issues the start call, then `POST runs/{runId}/advance
{fromNodeId:"node-2b"}`, and resolves with the updated run status.

### US-3: Start-and-signal-at-gates (hybrid)
**As** an ArdaNova backend,
**I want** to start a run that auto-advances and parks at a HOLD gate, then
un-park it when a phase is met,
**So that** the engine drives the run and I only intervene at gates.

**Given** a started run parked at a gate
**When** I call `.signal(gateId, "phase-met")`
**Then** the SDK issues `POST runs/{runId}/signal {gateId, payload:"phase-met"}`
and the run resumes.

### US-4: Tenant acting for a child actor
**As** an ArdaNova tenant principal,
**I want** to drive a run on behalf of one of my users,
**So that** the run executes under that child's identity, not mine.

**Given** a tenant `AzoaClient` (API-key) and a provisioned child avatar
**When** I call `quest(templateId).forActor(childAvatarId).start({ params })`
**Then** the SDK acquires a child credential
(`POST /api/tenant/avatars/{childAvatarId}/credential`) and uses it as the
Bearer token for the run's advancement calls.

## Technical Considerations

- **Where the code lives.** New module under `sdk/azoa-wallet/src/workflow/`
  (mirrors the `client/` + `api/` split): a `WorkflowClient` (template authoring
  + run-status reads, thin over `AzoaApiClient`) and a `WorkflowRunHandle` /
  `quest()` factory (the fluent driver). Exposed on the facade as
  `azoa.workflow` (composed in `AzoaClient` exactly as `holons` / `portfolio`
  are, `sdk/azoa-wallet/src/client/azoa-client.ts:143-146`).
- **Path constants.** Add the run-advancement + tenant-credential paths to
  `API_PATHS` / `ApiController` in
  `sdk/azoa-wallet/src/api/api-version.ts:19-116`
  (`QUEST_RUN_ADVANCE(runId)`, `QUEST_RUN_SIGNAL(runId)`,
  `QUEST_RUN_STATUS(runId)`, `TENANT_CHILD_CREDENTIAL(avatarId)`), and add
  `"tenant"` to the `ApiController` union (`api-version.ts:19-30`).
- **Reuse the request primitive.** All calls go through
  `AzoaApiClient.request` / `requestBare` with `extraHeaders` for the
  idempotency key — no new fetch path (`sdk/azoa-wallet/src/api/client.ts:1105`,
  `:879-893`).
- **Auth threading for `forActor`.** The child credential is a Bearer token
  scoped to one run; the cleanest seam is a per-handle token override passed into
  `request` as an `Authorization` `extraHeader`, leaving the global
  `AzoaApiClient` config (the tenant `X-Api-Key`) untouched. Decide whether to
  add a first-class `token` arg to `request` or thread it via `extraHeaders`
  (plan.md D-AUTH).
- **Thenable handle.** The handle implements `then` (PromiseLike) so a chain is
  awaitable; each chained method returns the same handle with the pending
  operation queued/awaited, short-circuiting on the first `err`.
- **Contract-first against unshipped endpoints.** `advance` / `signal` /
  run-status come from `durable-workflow-engine`; node configs from
  `economic-primitive-nodes`. Spec each SDK method against the documented
  request/response shape and gate the builder surface behind the Track-3 config
  finalization — the driver + template + actor surface land first.

## Out of Scope

- **The .NET backend engine / nodes** — the durable engine, suspend/resume,
  predicate evaluation, and value-moving handlers are the
  `durable-workflow-engine` + `economic-primitive-nodes` tracks. This SDK only
  wraps their HTTP surface.
- **The frontend demo UI** — `frontend-demo-harness` track. No `frontend/`
  changes here; **do not run frontend typecheck** (project memory
  `no-frontend-typecheck`).
- **ArdaNova's economic logic** — rates, vesting math, what a token *is* — lives
  in ArdaNova and calls this SDK. The SDK types generic mechanism params only.
- **New auth schemes** — reuse the existing JWT / `X-Api-Key` plumbing and the
  tenant-onboarding child-credential primitive; no new signing or token format.
- **Wallet / signing changes** — value movement is the engine + value-path
  tracks; this SDK does not build or sign transactions for workflow nodes.

## Open Questions

1. **`quest()` overload disambiguation.** Should `quest(id)` infer
   template-vs-quest-vs-run from a separate `.fromTemplate()` / `.forRun()`
   modifier, or should there be distinct entrypoints
   (`quest.fromTemplate(id)` / `quest.run(runId)`)? (Recommendation: explicit
   modifiers — fewer surprises, no id-shape sniffing.)
2. **Auto-poll vs explicit `.status()`.** Should `.onSuspend` poll the run to
   detect the awaiting state, or rely solely on the advance/signal response
   carrying the new status? (Recommendation: prefer the response payload;
   `.status()` is the explicit poll for long-parked runs.)
3. **Builder timing.** Ship `nodeConfig` builders in this track gated behind
   `economic-primitive-nodes`, or split them into a follow-up once Track 3's
   config shapes are frozen? (Recommendation: land driver + templates + actor
   first; add builders in the same track once Track 3's shapes are final, raw
   config accepted meanwhile.)
4. **`gateId` shape.** Is `gateId` a node UUID (engine model: `StepName =
   nodeId`, `durable-workflow-engine/plan.md:24,30`) or a free string? Governs
   whether `assertUuid` or a non-empty-string guard applies. (Recommendation:
   treat as UUID per the current engine model; relax if Track 2 finalizes it as
   a free label.)
5. **Run-status endpoint shape.** Confirm the exact run read path/shape from the
   `quest-api` run read surface (`MarkRunCompleted` + run reads landed per
   project memory) so `.status()` maps to the right GET. (Recommendation: pin
   against the shipped run read route during implementation.)

## Tier
**Tier 1 — the consumer surface.** It is the dependent leaf of the
workflow-engine initiative: it ships no engine capability, only the
ergonomic TypeScript surface that lets ArdaNova design and drive workflows from
its backend. It lands **after** the engine endpoints exist (or is specced
contract-first against them).

## Dependencies

- **`durable-workflow-engine`** (Track 2) — **HARD.** Provides
  `POST runs/{runId}/advance` (`{fromNodeId}`), `POST runs/{runId}/signal`
  (`{gateId, payload}`), the timer-via-trigger model, and the `Suspended` /
  `AwaitingSignal` / `AwaitingTimer` run states this SDK's driver maps onto
  (`durable-workflow-engine/spec.md:150-156`, `:138-142`;
  `durable-workflow-engine/plan.md:37`). Specced contract-first where not final.
- **`economic-primitive-nodes`** (Track 3) — **SOFT** (builders only). Provides
  the generic node config shapes the optional `nodeConfig` builders (FR-5) type.
  The driver + template + actor surface does **not** block on it; raw config
  strings are accepted meanwhile.
- **`quest-api`** ✓ shipped — template CRUD + instantiate + run read surface,
  already wrapped (`sdk/azoa-wallet/src/api/client.ts:1042-1061`).
- **`tenant-onboarding`** ✓ shipped — the child-credential endpoint
  (`POST /api/tenant/avatars/{id}/credential`,
  `tenant-onboarding/spec.md:124-131`) the actor abstraction (FR-3) threads.
- **`azoa-wallet-sdk`** ✓ shipped — the SDK substrate (facade, API client,
  auth, `Result`/`SdkError`, `assertUuid`, idempotency plumbing) this track
  extends.

## House rules (carried into Acceptance)
SDK `tsc` clean + vitest green; ESM+CJS+DTS via tsup unchanged; cross-platform
(no `btoa`/`atob`/`Buffer`); `assertUuid` on every interpolated id; new methods
documented in the SDK README / `api-version` constants; **NO brand leak** (no
ArdaNova types in the SDK); one commit per `[workflow-sdk] <verb> <subject>`.
