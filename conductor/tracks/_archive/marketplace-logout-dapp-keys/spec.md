---
type: track
id: marketplace-logout-dapp-keys
status: shipped
created: 2026-07-06
completed: 2026-07-06
---

# Track: marketplace-logout-dapp-keys (retro)

A retrospective of a single session that shipped four related auth/marketplace
features and hardened them. Archived as shipped. Design-of-record:
[docs/logout-and-dapp-keys-design.md](../../../docs/logout-and-dapp-keys-design.md).

## What was asked

1. A **logout flow** for the Azoa frontend and avatars in general.
2. **API keys that let an avatar develop their own dApps** — the user's phrase was
   "should make them a scoped admin for it." An explicit ask to "think through this
   flow critically."
3. Mid-session: **update frontend elements**, **SDK updates + a version bump**
   (user publishes), then **code-styleguides + code-review hardening**, then this retro.

## What shipped

- **Logout everywhere** — `POST /api/avatar/logout` bumps the per-avatar
  `AuthNotBefore` watermark, invalidating all that avatar's stateless JWTs via the
  existing `OnTokenValidated` check. Local (clear-localStorage) logout was already
  present; this adds the server "all devices" tier. No per-device jti store added
  (would reverse the deliberate stateless-JWT design).
- **Public quest marketplace** — quests gained an `IsPublic` flag (default private;
  owner opts in). A non-owner may start a public+Active quest; the run is owned by
  the **runner** and carries `SourceQuestId` + `OriginAvatarId` provenance so a
  future reward/attribution system can hook back to the origin creator.
- **Holon clone provenance** — cross-avatar clone kept open (the template mechanic);
  added `SourceHolonId` + `OriginAvatarId`; `private_*` metadata keys stripped on
  clone.
- **`dapp:develop` capability scope** — a coarse API-key scope enforced (via a
  `DappDevelop` policy) on holon/quest/dappseries write paths, and validated at key
  issuance. This also closed a pre-existing gap where `tenant:provision` was
  self-issuable by omission.
- **Access-control fixes** — avatar reads now return a PII-free public projection
  (email/name never leaked); `SearchManager` was leaking email into hit fields and
  matching on email (enumeration oracle) — both fixed; KYC admin moved behind the
  `Operator` policy.
- **SDK → 0.2.0** — `logoutEverywhere()`, quest `isPublic` + run-provenance types,
  `HolonQueryBuilder.clone()`; fixed a pre-existing wrong `executeQuest` return type.
  Rebuilt (tsup), 163 vitest tests green. **User publishes; then bump the frontend's
  `azoa-sdk` dep 0.1.0 → 0.2.0** (frontend consumes the published package).
- **Frontend** — "Log out of all devices" (header + settings), quest publish toggle,
  start-a-public-quest surface, `dapp:develop` checkbox on the api-keys create form.

## Key decisions (and why the critical thinking changed the ask)

- **"Reuse the Tenant model" → rejected.** Tenant/ConsentGrant is a *two-party*
  delegation primitive (custodian acts on another user's resources with consent); a
  tenant has zero standing authority of its own. The dApp-dev case is *single-party*
  ("edit my own stuff"), so Tenant would mean granting consent from yourself to
  yourself — pure ceremony. `DappSeries` (already avatar-owned) is the natural dApp
  boundary. Tenant stays reserved for the genuine multi-user-custodian case.
- **"Scoped admin" → capability scope, never admin.** API keys are deliberately
  blocked from `operator:admin` (stripped at emit; the Operator policy rejects the
  ApiKey scheme). "dApp admin" became `dapp:develop` bounded to what the avatar owns.
  Node operators keep their separate `operator:admin` for key rotation etc.
- **Per-dApp `dapp:{id}` fencing deferred** — user chose the coarse `dapp:develop`
  for now; per-series fencing needs a `DappSeriesId` linkage on fenced resources.

## The hardening pass earned its keep

The code-review found a **CRITICAL authorization inversion (C1)**: the marketplace
change correctly stamped `run.AvatarId = runner` for provenance, but the execution
engine never read it — every node handler derived the acting identity from
`context.Quest.AvatarId` (the **owner**). So a non-owner starting a public quest
would move the **owner's** assets, signed with the **owner's** custody key —
cross-avatar asset theft, with a provenance trail that actively misattributed it.

Fix: introduced `QuestNodeExecutionContext.ActingAvatarId` (= `run.AvatarId`) and
threaded it through both executors (direct + durable), every side-effecting node
handler, the binding resolver (H1: `$from: holon.<id>` now resolves against the
runner), and the capability gate (fails closed if the *runner* has no wallet). The
owner path is unchanged (acting == owner). A regression test asserts side-effects run
under the runner. The fix also closed a **latent unscoped-IDOR** in
`HolonUpdate/DeleteNodeHandler` (they passed no avatar → null → unscoped).

Minor findings fixed: M1 (email search oracle), M2 (clone metadata leak),
M3 (forbidden-only-scope treated as full-access). L1 (logout sub-second window) and
L2 (public quest reveals node configs) documented as accepted residual risk.

## Verification

- `dotnet build`: 0 errors, 17 warnings (pre-existing baseline).
- Quest unit tests post-fix: 481 passed, 0 failed.
- SDK: tsup + tsc clean, 163 vitest green.
- Integration sweep: 250 passed; all failures were environment flakiness (dropped
  SurrealDB sockets `ResponseEnded`, wallet-seed 400s, perf-timing budgets,
  concurrency gates) — **zero in the changed logic**. Confirmed by cross-checking the
  535-green `passoff-full.trx` baseline and inspecting each failure's exception.

## Lessons

- **Provenance ≠ authorization.** Stamping the runner on the run row read as "done"
  but the engine ignored it. A hardening pass that assumes a malicious authenticated
  user (not just "does it compile / do happy-path tests pass") is what surfaced it.
  Keep authoring and review in separate lanes — the same context that wrote the
  marketplace mechanic did not catch that execution ignored the run owner.
- **Follow the identity, not just the entity.** Multiple handlers derived "whose
  wallet/key/holon" from the quest rather than the run. When a feature changes *who
  acts*, grep every site that derives an acting identity from the old source.
- **Live-stack integration flakiness stays load-bearing noise** — the sweep needs the
  trx + per-failure exception inspection to separate infra drops from real breaks;
  the summary line alone (which shifted 221→250 between runs) is misleading.

## Follow-ups owed

- **User: publish azoa-sdk@0.2.0**, then bump `frontend/package.json` dep to `^0.2.0`
  (frontend features are dark until both are done).
- **Rewards/attribution system** to consume the new provenance fields
  (`SourceQuestId`/`OriginAvatarId`, `SourceHolonId`/`OriginAvatarId`).
- **Per-dApp `dapp:{seriesId}:*` fencing** if a dev needs to hand a scoped key to a
  third party.
- Remaining frontend TypeScript diagnostics (unused imports, `unknown` casts on the
  api-keys page) — clear once the SDK dep bump lands; frontend typecheck is
  out-of-policy noise otherwise.
