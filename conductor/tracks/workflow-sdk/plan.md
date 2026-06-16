# Workflow SDK — Plan

Build order: **types + path constants first, then the thin template/run-read
client, then the fluent `quest()` driver (the headline), then the `forActor`
child-credential auth, then optional node-config builders.** Every phase keeps
the existing ~82-test vitest suite green and the SDK `tsc` clean — those are the
regression gate. Tests run **once at the end** (SDK `tsc` + vitest only; **no
frontend typecheck**, per project memory `no-frontend-typecheck`).

The whole track is **additive** to `sdk/oasis-wallet/src/` — no existing public
symbol changes shape. The driver wraps endpoints from `durable-workflow-engine`
(Track 2); where an endpoint is not yet final, code the SDK method against the
documented CONTRACT and keep the dependency note in `spec.md`.

## Decisions

| # | Decision | Choice (recommended) | Rationale / evidence |
|---|----------|----------------------|----------------------|
| **D1** | Where does the new surface live? | A new `sdk/oasis-wallet/src/workflow/` module: a `WorkflowClient` (template authoring + run-status reads, thin over `OasisApiClient`) and a `WorkflowRunHandle` + `quest()` factory (the fluent driver). Composed on the facade as `oasis.workflow`. | Mirrors the existing `client/` + `api/` split and the way `holons` / `portfolio` are composed in `OasisClient` (`sdk/oasis-wallet/src/client/oasis-client.ts:143-146`). Keeps the driver out of the already-large `api/client.ts`. |
| **D2** | Add new HTTP plumbing, or reuse `request`? | **Reuse `OasisApiClient.request` / `requestBare`.** The driver builds paths + bodies and calls the existing primitive with `extraHeaders` for the idempotency key. No new fetch path. | The idempotency-key passthrough is already proven by `executeSwap` via `extraHeaders` (`sdk/oasis-wallet/src/api/client.ts:879-893`); `request` already does auth, 401-refresh, OASISResult unwrap (`:1105-1148`). Inventing a parallel transport would duplicate the refresh-dedup + error model. |
| **D3** | How does `quest(...)` disambiguate template vs quest vs run? | **Explicit modifiers / variants** — `quest.fromTemplate(id)`, `quest(questId)`, `quest.run(runId)` (or a `.forRun()` modifier) — **no id-shape sniffing.** | Sniffing a UUID's meaning is fragile and surprising; explicit entrypoints are self-documenting and testable. (spec.md Open Q1.) |
| **D-AUTH** | How is the child Bearer token threaded for `forActor` without disturbing the tenant `X-Api-Key`? | **Per-call `Authorization` override via `extraHeaders`** on `request`, scoped to one run handle. The global `OasisApiClient` config (tenant `X-Api-Key`) is untouched. | `fetchWithAuth` already merges `extraHeaders` over the built auth headers (`sdk/oasis-wallet/src/api/client.ts:1270-1274`), so a handle-scoped `Authorization: Bearer <childJWT>` wins for that run's calls only. Avoids mutating shared client state. If a cleaner seam is wanted, add an optional `token` arg to `request`; the `extraHeaders` route works today. |
| **D4** | Acquire the child credential eagerly or lazily? | **Lazily, on the first advancement call for that actor; cache per handle; re-acquire on expiry** reusing the refresh-dedup discipline. | Mirrors the existing `_refreshInFlight` dedup (`sdk/oasis-wallet/src/api/client.ts:1291-1306`) — don't invent a second refresh path. A `.forActor()` that never drives shouldn't burn a credential. |
| **D5** | Ship `nodeConfig` builders now or defer? | **Land the driver + template + actor surface first; add builders in this track once `economic-primitive-nodes` freezes its config shapes.** Raw `Config` strings accepted meanwhile (forward-compat escape hatch). | The configs are opaque JSON on the wire (`durable-workflow-engine/plan.md:18`). Typing them before Track 3 finalizes risks churn; the raw-string escape hatch unblocks authoring now. (spec.md Open Q3 / FR-5.) |
| **D6** | `gateId` guard — UUID or free string? | **`assertUuid`**, per the current engine model where `StepName = nodeId` (`durable-workflow-engine/plan.md:24,30`). Relax to a non-empty-string guard only if Track 2 finalizes `gateId` as a free label. | Keeps the path-traversal guard uniform with every other interpolated id (`sdk/oasis-wallet/src/api/client.ts:569-576`). Recorded as a contract dependency (spec.md Open Q4). |
| **D7** | Handle ergonomics — eager calls or queued thenable? | **Thenable (PromiseLike) handle**: each chained method awaits the prior op and returns the same handle; the chain is `await`-able and short-circuits on the first `err` (Result discipline, no throw). | Delivers the literal `await quest(a).start({...}).step(b)` from the user's pseudocode while preserving the SDK's `Result<T, SdkError>` no-throw contract (`oasis-wallet-sdk_20260509/spec.md:124-128`). |

## Phase 1 — Types + path constants (foundation, no behavior)
1. `[ ]` Add the run-driver DTOs to a new `sdk/oasis-wallet/src/workflow/types.ts`:
   `WorkflowRunStatus` (the run + node-execution read shape incl.
   `Suspended` / `AwaitingSignal` / `AwaitingTimer`,
   `durable-workflow-engine/spec.md:138-142`), `AdvanceParams` (`{ fromNodeId }`),
   `SignalParams` (`{ gateId; payload }`), `StartRunParams`
   (`{ actor; params }`), and `WorkflowRunResult`. Extend the existing
   `QuestRunStatus`-adjacent types in `api/client.ts:382-533` only if a shared
   enum is cleaner — prefer additive new types.
2. `[ ]` Add path constants to `sdk/oasis-wallet/src/api/api-version.ts`:
   `QUEST_RUN_ADVANCE(runId)`, `QUEST_RUN_SIGNAL(runId)`,
   `QUEST_RUN_STATUS(runId)`, `TENANT_CHILD_CREDENTIAL(avatarId)`; add `"tenant"`
   to the `ApiController` union (`api-version.ts:19-30`). Pin the exact
   run-status route against the shipped `quest-api` run read surface (spec.md
   Open Q5).

## Phase 2 — Thin WorkflowClient: template authoring + run reads (FR-1)
3. `[ ]` `WorkflowClient` in `sdk/oasis-wallet/src/workflow/client.ts`, thin over
   `OasisApiClient`: `createTemplate` / `getTemplate` / `listTemplates`
   delegate to the already-present `createQuestTemplate` / `getQuestTemplate` /
   `listQuestTemplates` (`sdk/oasis-wallet/src/api/client.ts:1042-1055`).
   `assertUuid` on `templateId`.
4. `[ ]` `instantiate(templateId, params)` → `instantiateQuestTemplate`
   (`sdk/oasis-wallet/src/api/client.ts:1058-1061`), returning a **bound run
   handle** (Phase 3) rather than the raw `QuestResult`, so DESIGN flows into
   DRIVE.
5. `[ ]` `getRunStatus(runId)` → `GET` the run-status route (Phase 1 constant),
   mapping to typed `WorkflowRunStatus`; `assertUuid(runId)`.

## Phase 3 — The fluent `quest()` driver (the headline, FR-2 + FR-4)
6. `[ ]` `WorkflowRunHandle` + `quest()` factory in
   `sdk/oasis-wallet/src/workflow/run.ts`:
   - `quest(questId)` / `quest.fromTemplate(templateId)` / `quest.run(runId)`
     (D3) open a handle.
   - `.start({ actor, params })` instantiates+starts a durable run, binds the
     handle to the returned `runId`, returns `this`.
   - `.step(nodeId, { idempotencyKey? })` → `POST runs/{runId}/advance` body
     `{ fromNodeId: nodeId }` via `request(..., extraHeaders)` (D2). Chainable.
   - `.signal(gateId, payload, { idempotencyKey? })` →
     `POST runs/{runId}/signal` body `{ gateId, payload }`. Chainable.
   - `.status()` → `WorkflowClient.getRunStatus(runId)`.
   - `.onSuspend(cb)` fires when a call leaves the run awaiting; result accessors
     expose latest `runId` / `status` / terminal output.
7. `[ ]` Make the handle a **thenable** (D7): `then` resolves the queued chain to
   `Result<WorkflowRunStatus, SdkError>`; first `err` short-circuits.
8. `[ ]` Guards + idempotency (FR-4): `assertUuid` on `runId` / `nodeId` /
   `gateId` (D6) before interpolation (pattern at
   `sdk/oasis-wallet/src/api/client.ts:569-576`); optional `idempotencyKey` sets
   the `Idempotency-Key` header (pattern at `:879-893`); amounts in params stay
   **strings**; verbose method+path errors (`:1202-1217`).

## Phase 4 — Actor abstraction: `forActor` child credential (FR-3)
9. `[ ]` `.forActor(childAvatarId)` on the handle: `assertUuid(childAvatarId)`;
   record the target child for lazy credential acquisition.
10. `[ ]` Lazy child-credential acquisition (D4): on the first advancement call
    for the actor, `POST /api/tenant/avatars/{childAvatarId}/credential`
    (`tenant-onboarding/spec.md:124-131`) using the tenant `X-Api-Key`; cache the
    returned child JWT per handle; re-acquire on expiry via the existing
    refresh-dedup discipline (`sdk/oasis-wallet/src/api/client.ts:1291-1306`).
11. `[ ]` Thread the child JWT as a per-run `Authorization: Bearer` override via
    `extraHeaders` (D-AUTH) on the advance/signal calls only; the tenant
    `X-Api-Key` remains the principal for the credential-acquisition call.
12. `[ ]` Direct end-user (non-tenant) path: when no `.forActor()` is set, the
    run uses the active session token unchanged — the child-credential path is
    engaged **only** for a tenant acting for a child. **No brand leak**:
    `forActor` takes a plain avatar id; nothing names ArdaNova or tenant concepts
    in the public type.

## Phase 5 — Optional node-config builders (FR-5, gated on Track 3 — D5)
13. `[ ]` `nodeConfig` namespace in `sdk/oasis-wallet/src/workflow/node-config.ts`
    with pure typed builders for the shipped generic node types
    (`gateCheck` / `swap` / `grant` / `transfer` / `hold` / `refund` / `emit`),
    each returning the serialized `Config` string the template DTO expects.
    Types **only** generic mechanism params (amounts as **strings**); **never** a
    rate or token meaning. Raw `Config` string always accepted (escape hatch).
14. `[ ]` Defer the concrete builder shapes until `economic-primitive-nodes`
    freezes the configs; until then keep the raw-string path and stub the builder
    types against the documented contract. (This phase may land in a follow-up
    commit once Track 3 ships — the driver does not block on it.)

## Phase 6 — Exports, README, tests (FR-6 + house rules)
15. `[ ]` Export the new surface from `sdk/oasis-wallet/src/index.ts`
    (`sdk/oasis-wallet/src/index.ts:72-82` pattern): `quest`, `WorkflowClient`,
    `WorkflowRunHandle`, `nodeConfig`, and the new types.
16. `[ ]` Compose `oasis.workflow` on the facade in
    `sdk/oasis-wallet/src/client/oasis-client.ts` exactly as `holons` /
    `portfolio` are constructed (`:143-146`).
17. `[ ]` Document the new methods in the SDK README (create one if absent —
    `sdk/oasis-wallet/README.md` does not exist today) and the `api-version`
    constants; show `quest(step1).step(step2B)`, the hybrid start+signal path,
    and `forActor`.
18. `[ ]` Tests in `sdk/oasis-wallet/tests/workflow/` using the existing
    `ApiConfigBuilder` + `vi.stubGlobal("fetch", …)` harness
    (`sdk/oasis-wallet/tests/api/self-audit-one-fix.test.ts:13-24`):
    - **ordered-call**: `quest(a).start({...}).step(b).step(c)` ⇒ start →
      `advance {fromNodeId:b}` → `advance {fromNodeId:c}` (assert
      `mockFetch.mock.calls`, mirror `self-audit-one-fix.test.ts:218-245`);
    - **hybrid**: `start → signal {gateId, payload}`;
    - **idempotency passthrough**: `.step(.., {idempotencyKey})` sets the header
      (mirror `:247-257`);
    - **assertUuid**: non-UUID `runId`/`nodeId`/`gateId`/`childAvatarId` throws
      before any `fetch` (mirror `:290-295`);
    - **child-credential acquisition**:
      `quest(...).forActor(childId).start({...})` issues the credential POST
      first (tenant `X-Api-Key`), then uses the child JWT as `Authorization:
      Bearer` on advance/signal;
    - **status/onSuspend**: `.status()` maps `Suspended`/`AwaitingSignal`/
      `AwaitingTimer`; `.onSuspend` fires when a call leaves the run awaiting.

## Verification (run ONCE at the end)
19. `[ ]` `pnpm --filter @oasis/wallet-sdk tsc --noEmit` (or the repo's SDK
    typecheck script) — clean. **No frontend typecheck** (project memory).
20. `[ ]` `pnpm --filter @oasis/wallet-sdk test` (vitest) — green, including the
    existing ~82 tests and the new `tests/workflow/` suite.
21. `[ ]` `pnpm --filter @oasis/wallet-sdk build` (tsup) — ESM + CJS + DTS emit
    unchanged; new symbols present in the `.d.ts`.
22. `[ ]` Grep the SDK `src/` for `ArdaNova` / brand terms and for
    `btoa`/`atob`/`Buffer` → **zero** hits (NO brand leak; cross-platform).
23. `[ ]` spec.md decisions kept current; new methods documented in README +
    `api-version` constants.

## Commit strategy
One commit per logical unit, message format **`[workflow-sdk] <verb>
<subject>`**, e.g.:
- `[workflow-sdk] add run-driver types + advance/signal/credential path constants`
- `[workflow-sdk] add WorkflowClient template-authoring + run-status reads`
- `[workflow-sdk] add quest() fluent run driver (start/step/signal/status)`
- `[workflow-sdk] thread child credential for forActor (tenant acts for child)`
- `[workflow-sdk] add typed nodeConfig builders for generic node params`
- `[workflow-sdk] export workflow surface + README + vitest suite`

Each commit leaves the SDK `tsc` clean and the vitest suite green (single sweep
at the end of the phase, per the test-once policy).

## Known follow-ups (out of this track, recorded for the initiative)
- **`economic-primitive-nodes`** (Track 3) must freeze its generic node config
  shapes before the `nodeConfig` builders (Phase 5) can be fully typed; the
  driver + template + actor surface ship first with raw-config escape hatch.
- **`durable-workflow-engine`** (Track 2) owns the `advance` / `signal` /
  run-status endpoints + the `Suspended` / `AwaitingSignal` / `AwaitingTimer`
  states; if its final route/body shapes differ from the documented contract,
  update the Phase 1 constants + Phase 3 bodies (single point of change).
- **`frontend-demo-harness`** consumes this SDK to render a demo workflow UI —
  out of scope here.
- **Run-status route confirmation** (spec.md Open Q5) — pin `.status()` against
  the shipped `quest-api` run read path during Phase 1.
