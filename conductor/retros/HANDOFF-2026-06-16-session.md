# Session Hand-off — 2026-06-16

> **Purpose.** Resume this work in a fresh session with zero context loss. Read top
> to bottom, then pick up from "What to do next." Branch: `api-safety-hardening`.
> Working tree clean as of writing (only `conductor/.conductor_session_log` +
> two regenerated `.mermaid` flowcharts are untracked churn).

---

## 1. What happened this session (the arc)

Three things, in order:

1. **Shipped the `ardanova-provider-port` initiative** — all 6 tracks, parallel
   ULTRAPILOT, each `[x]` in `tracks.md`: signing-core-keystone (real Algorand
   keygen + `ITransactionSigner` seam + provider wiring), custody-key-management,
   db-only-null-provider, tenant-onboarding, kyc-module, fiat-stripe-bridge. Plus a
   fail-closed **Solana signer stub** (the seam is chain-agnostic; `GetSigner("Solana")`
   resolves but `Sign` errors with no side effect). Crypto independently reviewed.
2. **Removed Avatar `Karma`/`Level`** (user request) — per-avatar gamification belongs
   in a Holon, not identity columns. Also fixed a latent tenant-FK bug (`owner_tenant_id`
   was emitting non-optional, which would force every avatar to have a tenant).
3. **Reviewed for gaps** (`conductor/REVIEW-economic-substrate-2026-06-16.md`) →
   **scoped the `workflow-engine` initiative** (4 tracks) → **built track 1
   (value-path-wiring)** via /autopilot, including a /code-review that caught a
   Critical idempotency-key collision (the `int` clamp), fixed by widening amounts to
   `ulong`.

State at hand-off: **706/706 unit tests green, `dotnet build` 0 errors / 0 new warnings**
(28 pre-existing warnings baseline — see `memory/build-warning-baseline-2026-06-16.md`).

---

## 2. The workflow-engine initiative (the spine of "what's next")

The vision (confirmed with the user): make AZOA a **durable, consumer-driven workflow
engine**. An external app (ArdaNova, via the TS SDK) DESIGNS a multi-phase process as a
quest template and PUSHES an actor (player/user/tenant) through it phase-by-phase —
`quest(holonStep1).step(holonStep2B)`. The DAG suspends between phases (hybrid: explicit
`step()` advance OR engine auto-run that parks at gate/wait nodes until a `signal()` or
timer), survives restart, and runs first-class compensation on cancel.

**Two locked boundary decisions (do not re-litigate):**
- **AZOA = generic primitives only.** All economic semantics (swap rates, what a project
  token is, cancel conditions, vesting math) stay in ArdaNova.
- **Holonic transformations are the BASE; chain/economic actions are OPT-IN.** Via a
  **capability flag**: one generic node SPI where each node declares `RequiresChainCapability`
  (default `false`); the engine refuses a chain-requiring node unless a wallet is bound
  (fail-closed). A pure-metadata quest never touches a wallet/ASA. (This is why the
  `economic-primitive-nodes` track is retitled "Holon-Transformation Nodes" — dir name kept
  for the tracks.md link.)

**The 4 tracks, dependency order (specs are written + committed under `conductor/tracks/`):**

| # | Track | Status | One-line |
|---|-------|--------|----------|
| 1 | **value-path-wiring** | `[x]` **SHIPPED** this session | Custody-routed signing + real broadcast + ulong amounts + KYC choke point. |
| 2 | **durable-workflow-engine** | `[ ]` spec ready | **Centerpiece.** Suspendable, step-addressable Quest runs built ON the existing saga layer (`Services/Sagas/*` is already implemented + DI-wired). The ONE new capability needed: suspend-on-signal (a `Parked` step status + `SignalAsync`/timer un-park). New `QuestRunStatus` Suspended/AwaitingSignal/AwaitingTimer; advancement API (`advance(runId,nodeId)`, `signal(runId,gateId,payload)`). |
| 3 | **economic-primitive-nodes** (Holon-Transformation Nodes) | `[ ]` spec ready | One node SPI + `RequiresChainCapability`. Tier-1 holonic transforms (mostly already exist as `Holon*` handlers) + new `GateCheck` (real predicate eval, replaces the no-op `ConditionNodeHandler` + dead `QuestEdge.Condition`) + `Emit`. Tier-2 opt-in chain actions: `Swap`/`Grant`/`Transfer`/`Refund`. Plus the Holon↔on-chain-asset typed link. |
| 4 | **workflow-sdk** | `[ ]` spec ready | TS `@azoa/wallet-sdk` fluent run driver `quest(id).start({actor}).step(nodeId)` / `.signal()` / `.forActor(childAvatarId)`. |

**Recommended next build: track 2 (durable-workflow-engine).** It's the centerpiece and
unblocks 3 + 4. Verified de-risk: the saga foundation (`SurrealSagaStore`, `SagaProcessor`,
`SagaCoordinator`, `PollingSagaTrigger`, `saga_steps` schema) is already shipped, so the
engine mostly REALIZES the saga track's already-named "quest step dispatch" consumer + adds
suspend-on-signal. Read `conductor/tracks/durable-workflow-engine/spec.md` + `plan.md`
(plan.md D1 is the headline saga-vs-new-engine decision, already resolved → build on saga).

---

## 3. Two OPEN user requests NOT yet actioned (carry these forward)

### R1 — Reusable Asset model (bytes-in-DB now, bucket later)
> "store images and documents as bytes in the db in an asset model that we can shift out
> to a bucket later (stub s3 url and other needed metadata). asset should be mostly reusable
> for any assets that azoa will serve with an appendable mime-type related table or maybe
> relationship property to itself."

**State:** does NOT exist — no `Asset*.cs` model anywhere in the repo. This is net-new.
**Shape to build (a new track, e.g. `asset-store`):**
- A generic `Asset` SurrealDB POCO (pattern: `Persistence/SurrealDb/Models/Holon.cs`):
  `id`, owner `avatar_id` link, `bytes` (the blob — `option<bytes>` or base64 string now),
  `mime_type`, `size_bytes`, `sha256`, `original_filename`, and a **stubbed `storage_url`**
  + `storage_backend` ("db" now, "s3" later) so the bucket migration is a column flip + a
  backfill, not a schema change. Per `data-engine-decision`/greenfield: no migration shim.
- **Appendable mime-type** — the user offered two shapes; recommend a small
  `mime_type` validation set + a **self-relationship** (`Asset.parent_asset_id` /
  `peer` like Holon) so derived assets (thumbnail, transcode, OCR text) link to their
  source. Decide in the track's plan.md: a separate `mime_type` lookup table vs. a
  free `mime_type` string + an allow-list config (lean: free string + config allow-list,
  mirroring the KYC MIME allow-list already in `ManualKycProviderService`).
- Manager + Avatar-scoped controller (IDOR pattern: owner = authenticated avatar, body
  owner ignored — STARODK precedent), `AZOAResult<T>`, IKycGate optional.
- **Bucket-later stub:** record `AZOA_ASSET_STORAGE_BACKEND` + a future S3 bucket as a
  `DEPLOY-STEPS-TODO.md` entry; the `storage_url` is null/db-backed until then.
- Tie-in: this is the natural home for the **Holon↔asset metadata** and could back the
  `Emit`/document-output nodes in the workflow engine.

### R2 — Frontend full-test-package, runnable locally
> "lets update the front end especially the full tests package to run locally"

**State:** `frontend/` (Next.js 14) has **NO test runner** — `package.json` scripts are
only `dev/build/start/lint`; there is no `test` script and no vitest/jest/playwright dep.
So "full tests package to run locally" = **stand up a frontend test harness from scratch**.
**Shape to build (a new track, e.g. `frontend-test-harness`, or fold into the existing
`frontend-demo-harness` pending track):**
- Add a runner (recommend **vitest** — already the SDK's runner, consistent) + React
  Testing Library for components, and optionally Playwright for E2E against a locally-run
  app. Add `"test"`, `"test:watch"`, `"test:ui"` scripts.
- **Honor the project rule:** `memory/no-frontend-typecheck.md` — do NOT run `frontend/`
  `tsc`; it's pre-existing noise. Tests, not typecheck.
- Coverage target: the SDK-integration surface (`frontend/src/lib/azoa*.ts` — the SDK
  singleton, auth context, the `useBalance/usePortfolio/useHolons/...` hooks) + the page
  flows. Mock the AZOA API at the SDK boundary.
- Local run = `cd frontend && npm test` green with the podman SurrealDB / mocked API.

Both R1 and R2 should be **scoped as their own tracks** before building (the user's working
rhythm is spec-first via conductor). Neither blocks the workflow-engine tracks.

---

## 4. Filed follow-ups (not tracks yet — from the review + deploy-stubs)

- **M2 — multi-step allocation compensation.** A mint failure after wallet provisioning
  strands the wallet. Belongs to **durable-workflow-engine** (same suspendable/saga engine).
- **M3 — child Bearer credential → allocation endpoint.** The child JWT lacks the `ApiKeyId`
  claim `AllocationController` requires (fail-closed today). Decide whether to derive the
  idempotency partition from `OwnerTenantId`. Belongs to **tenant-onboarding**.
- **B3 — KMS/HSM custody key store** (mainnet blocker, open). `IKeyCustodyService` is the
  swap seam. **B6 — mainnet enablement gate** stays gated until B1–B5 close (B3 still open).
- **P5 wallet-generate KYC** — mint path is now gated (value-path-wiring); `WalletManager.
  GenerateWalletAsync` gating is still owed (decide if zero-balance wallets may exist pre-KYC).
- **H1/H2 (signing) — Solana/Ethereum real keygen+signing**; **soulbound clawback-revoke**.
- **DecryptPrivateKeyBytes byte[] overload** (P1 residual) — orthogonal cleanup on
  `WalletKeyService` so the custody hex-string intermediate is never materialized.

See `conductor/DEPLOY-STEPS-TODO.md` status board for the authoritative open/closed list.

---

## 5. What to do next (recommended sequence)

1. **Build durable-workflow-engine** (track 2) — the centerpiece, unblocks 3+4. Spec+plan
   ready; build on the saga layer per plan D1. This is the highest-leverage next move.
2. Then **economic-primitive-nodes** (track 3, the holon-transformation + capability-flag
   node library) → **workflow-sdk** (track 4).
3. **Scope + build the two open user requests** (R1 Asset model, R2 frontend test harness) —
   independent of the workflow tracks; can interleave. Spec each as a track first.
4. Keep **B6 mainnet gated** until B3 (KMS) closes.

## 6. House rules (honor these — they shaped everything above)
- Zero-warning nullable `dotnet build`; `dotnet test` green; run the full sweep **ONCE** at
  the end of a multi-fix pass, not after each fix.
- SurrealDB is the SOLE engine — new entities = decorated POCOs in
  `Persistence/SurrealDb/Models/` (pattern: `Holon.cs`); regen goldens via
  `AZOA_REGENERATE_GOLDENS=1` (never hand-edit `Generated/Schemas/*.surql`).
- Keep authoring and review separate — do NOT self-approve crypto/value-path; use a
  `code-reviewer` lane. (This session's review caught a real Critical defect — worth it.)
- IDOR pattern: owned-resource lookups scoped by route id + authenticated avatar; body
  owner ids ignored (STARODK precedent).
- Avoid casts where possible (user's explicit bar — it surfaced the C-1 idempotency bug).
- Skip `frontend/` typecheck (`no-frontend-typecheck`); SDK `tsc` + vitest only.
- No brand leak: nothing ArdaNova-branded in AZOA code/config.
- Commit per track: `[<track>] <imperative verb> <subject>`.
