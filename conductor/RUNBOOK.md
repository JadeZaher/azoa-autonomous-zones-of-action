---
type: runbook
---

# AZOA — Session Runbook

**Last updated:** 2026-10-17 (evening) · **Branch:** `feat/platform-stub-closeout` ·
**Last commit:** see `git log -1` — round 4 lanes + fix batch merged; **`main` push for live QA in progress**.

> **Rewritten from scratch 2026-10-17** at the owner's request. The previous 2,127-line runbook
> (Aug–Sep 2026 programs: Phases 0–4, the ArdaNova production-readiness waves, launch-readiness L0,
> decisions D1–D55) is preserved verbatim in git: `git show 0522fdf:conductor/RUNBOOK.md`.
> Standing decisions from it that still bind are summarized in §3.
>
> **Two files are named RUNBOOK.md.** This one is the *session/continuation* runbook. The root
> [`RUNBOOK.md`](../RUNBOOK.md) is the *operational* reference (dev-up/down, deploy, diagnostics).
> Don't merge them, and don't restate ops procedures here.

---

## 1. Goal

**Active program: `platform-stub-closeout`** ([track](tracks/platform-stub-closeout_20261015/plan.md)).
Owner's words (2026-10-15): *"Close out all existing stubs on the platform, making sure especially the
KYC/KYB is set up. Identomat first for sure. But also Stripe, with absolutely no stubs or deferrals
unless absolutely needed, tracked in a deferred.md in the conductor track."* Plus: consolidate all
branches into `main`; *"push back on deferrals unless impossible."*

**Method (owner-set):** a four-round loop. Each round = two parallel independent assessments
(adversarial + optimism/sycophancy) → reconciled plan → adversarial plan review + fix → parallel
implementation waves → independent review and fix passes (≤2 cycles per lane).

**Constraints that bind:**
- **One integrated build/test/lint/typecheck/schema sweep at the very end**, after round 4. Collect
  fixes through the rounds; no build→fix loops. Static review ≠ executed verification — say which.
- `deferred.md` holds only genuine external blockers with evidence, owner, unblock and closure check.
  Implementation size is never a deferral; next-round work is "carry-forward", not deferral.
- Identomat workflows don't exist yet (owner-authorized mocking; config IDs never fabricated).
  Stripe stays in dev/test mode.
- `git push`, tags, NuGet/npm publishes and Railway deploys are the **owner's** steps unless granted.
- The program ends with a merge-to-main sweep (`--no-ff` of the feature branch, sweep on main, push
  when green and the owner says so).

**Other tracks:** `launch-readiness` (L0 closed 2026-09-09, L1–L7 not started — paused while this
program runs) and `terminology-neutralization-cutover` (consumer cutover) remain in
[tracks.md](tracks.md); neither is being worked now.

---

## 2. State

**Round 4 executed 2026-10-17: all 7 lanes + D-8 + the W3 fix batch are merged into
`feat/platform-stub-closeout`; the whole solution compiles (0 errors, warnings ≤ 25). The integrated test
sweep has NOT run — most new tests are *Authored* only.** Owner decision (2026-10-17): push `main` for
**live QA on Railway** (owner turns off Railway "Wait for CI"; Railway SurrealDB volume reset per D-9) and
defer further loops until after QA → [`round4-post-qa-deferrals.md`](tracks/platform-stub-closeout_20261015/round4-post-qa-deferrals.md).

- **History:** Codex (VS Code, rollout `01a13fa5`, `~/.codex/sessions/2026/10/15/`) ran rounds 1–3 on
  2026-10-15 and stopped mid-round-3 at 23:04Z. Claude Code checkpointed it as `3d08ace`, finished
  round 3 with lanes N/A/B/C/V, and committed the join as `baf1224`. Session `oasis-sleek-38` merged
  that with its retired-vocabulary sweep as `0522fdf`; this branch fast-forwarded to it.
- **Compile:** a real build of `0522fdf` (by `oasis-sleek-38`, in its own worktree) gives **247 unique
  first-wave errors**, the same set as `baf1224` alone. List with root causes:
  `.omc/research/round3-join-compile-errors-2026-10-16.txt` and
  [round3-join-ledger.md](tracks/platform-stub-closeout_20261015/round3-join-ledger.md) §Post-join state.
  Test projects never compiled.
- **SurrealForge (owner's library, `C:\Users\atooz\Programming\Projects\surrealforge`):** **1.3.0 is
  on nuget.org** (published 2026-10-17 09:29 MDT by the CI-gated publish workflow, run `39045148834`;
  all seven packages + symbols; restorable from 09:34). Code final at `b5a7d52`, `main` = `586e316`.
  Final review verdict PUBLISH (integration 47/47 on 3.2.4/3.1.4 × memory/RocksDB, 1202 unit).
- **CONFIRMED + FIXED (in SurrealForge, 2026-10-17): raw-transaction error attribution.** Measured on
  3.1.4 and 3.2.4: an aborted `BEGIN; …; COMMIT;` fails every slot ([1] "not executed due to a failed
  transaction", [2] culprit, [3] "…cancelled transaction", [4] "Cannot COMMIT: …"; a write conflict's
  "can be retried" appears only in [4]). `EnsureAllOk` used to throw slot [1], so **every AZOA conflict
  catch on a raw transaction (THROW markers, `IsUniqueViolation`) and every `RetryOnConflictAsync` was
  dead.** 1.3.0 throws the first non-collateral slot (`SurrealResponse.cs:107`, `FindBlamedSlot` `:125`).
  AZOA needs no edits — only the 1.3.0 bump.
- **Round 4 plan is FINAL** — [`round4-plan.md`](tracks/platform-stub-closeout_20261015/round4-plan.md)
  (R3, `c854f2c`): seven worktree lanes (L1 core/platform W0→W1→W2, L2 identity, L3 money incl. D-8
  tenant-funded payouts, L4 security, L5 chains, L6 verification, L7 frontend/SDK), self-merge in a fixed
  order guarded by `scripts/round4/merge-check.mjs`, per-lane + cross-cutting review → one fix batch →
  the sweep. Estimate 35–45 agent runs (~10 short re-checks), ~60–120M tokens, 5–7 days. Inputs, all
  recorded in the ledger §Round-4 inputs: both assessments, two controller audits, two liveness sweeps
  (the identity/consent one recovered from its transcript → `round4-input-liveness-identity.md`).
  **Executed** — merges #1–#9 + #5b (D-8); W3 review ledger `round4-review-ledger.md` (7 reviews, 1 FAIL
  remediated, all HIGH re-checked); fix batch merged `afaa46dc`…`75b23986`; final fix round (core2 / gates2 /
  money2 / frontend2) merging before the push. Execution notes in §8.
- *Historical — how the round-4 inputs arrived:*
  - adversarial assessment → `tracks/platform-stub-closeout_20261015/round4-assessment-adversarial.md` —
    **DONE 2026-10-17.** Bottom line: no KYC, KYB or Stripe journey works end to end even in principle,
    and the causes are internal defects. New: C2 the generated schema predates rounds 1–3 (61 tables, ~120
    fields missing; **missing UNIQUE indexes fail open**, so schema regen must precede any runtime work);
    C6 SurrealForge typed filters lowercase enum values (`ExpressionTranslator.cs` `NormaliseValue`), so
    the KYC active-submission guard fails open and operator queues are empty, and captured-null filters
    bind NULL not NONE (both queued into SurrealForge 1.3.0); C3 first-party KYC begin has no route; C7
    fungible creation isn't registered in DI (dev API should fail to boot); H1/H2 no Identomat refresh
    path, KYB gates nothing; H4/H5/H6/H10 exchange/DEX undeclared, child credentials 503, governance
    gates lifted without a verdict, webhook delivery disabled. Proposes WP0 (compile + DI) → WP1 (schema
    + stale index) → WP2–WP12 in parallel → sweep. **Coverage gap:** its fail-closed SurrealQL sweep
    (binding names, strict validator, array captures, positional offsets, 3.x syntax) never reported,
    so round 4 must re-run it.
  - reconciled `round4-plan.md` — drafted; adversarial critic **REJECT with 9 targeted changes**
    (liveness CRITICALs uncovered; `Program.cs` DI order can silently bypass `RiskHoldValueGate`;
    post-podman prerequisites; W1-vs-W0 race; SurrealQL re-sweep to W1; security-review gaps; unowned
    files). Owner decisions taken (§3 Round 4). Plan being revised around the compact executor-lane
    model; then a short critic re-review.
  - optimism-challenge assessment → `.../round4-assessment-optimism.md` — **DONE 2026-10-17.** Headlines:
    "KYC/KYB set up", "Stripe complete", "exactly-once" and "lanes A/C closed" are not yet true (KYC
    stores and WTT quotes don't compile; `OffRampService.cs:452` uses an undeclared `requestedQuantity`;
    Jupiter fee-cap has 9 unassigned locals; Tinyman parser reads a nonexistent SDK property).
    Probe-backed: Ganache 7.9.2 rejects EIP-1898 (switch fixtures to Hardhat); `surrealforge up` won't
    drop the stale index without `--allow-destructive`. It challenges IDENTOMAT-RECOVERY as a deferral
    (retry plus `/delete/` of late arrivals works without the vendor). Ten carry-forwards are unrecorded
    deferrals. Proposed sequencing: one fix batch → compile-until-clean (6–8 builds) → one full sweep →
    ≤3–4 targeted fix batches; ~9–12 waves total.
  - SurrealForge 1.3.0 re-review of `3aacd25..2067ae4`: **PUBLISH AFTER FIXES** — anchor both
    classifiers (strip the client prefix; require the vendor shape at the start, since user data echoed in
    errors can steer substring matches); capture parallel same-id / UNIQUE races; fix the pre-existing
    buffered-flush collateral blame (buffered transactions never reached `IsRetryableConflict`); scope
    the CHANGELOG claims. Fix pass running; then a final re-review → owner publishes.

### Review ledger

| Phase | Checkpoint | Findings | Verdict |
|---|---|---|---|
| Rounds 1–2 (Codex, 2026-10-15) | per-package cross-lane source reviews | see `round1-closeout.md`, `round2-closeout.md` | static-accepted; runtime pending |
| R3 lane N — NFT lifecycle | opus correctness + security, 1 cycle + LOWs | 2 HIGH stalls, cross-tenant MEDIUM, conflict-retry MEDIUM → fixed | **PASS** |
| R3 lane A — WTT bridge | opus correctness + security, 2 cycles | cancel-after-confirmed HIGH, tenant recipient/relayer-fee HIGH (owner decision), envelope wedge, 5-min heuristic, Platform-type spoof → fixed | **PASS-with-deferred** (LOWs) |
| R3 lane B — Hold/exchange/fiat reversal | opus correctness + security, 2 cycles | array-capture HIGH, reversal over-recovery HIGH ×2, custody-tenant MEDIUM, rounding, contention, watermark → fixed | **PASS-with-deferred** — 2 clawback crash-path MEDIUMs are MUST-FIX before real cards |
| R3 lane C — DEX/Ethereum | opus correctness + security, 2 cycles | executed-swap-as-absent HIGH, cleanup lane-pin HIGH, nonce holes, resolver never Fails, RPC-key leak → fixed; nonce reuse verified safe | **PASS-with-deferred** (LOWs + deploy-blocking index) |
| R3 lane V — verification harness | opus review, 1 cycle + LOWs | fakes encoded our assumption (D4 unsound), non-hermetic E2E, CI build broken → fixed | **PASS** |
| R3 orchestrator fixes | lane reviewers verified | signing-fence binding names; validator FOR/closure workarounds; `AssignRecord` for array captures (14 sites); compile cascade | **PASS** (verified by reviewers) |
| SurrealForge 1.2.1→1.3.0 | opus pre-publish review + 2 re-reviews | regex-literal regression, breaking `ToString()`, unanchored classifiers, buffered-flush blame → fixed | **PUBLISH** final `b5a7d52`; **published** 2026-10-17 |
| R3 join | `round3-closeout.md` | — | **CLOSED 2026-10-16** (`baf1224`) |
| Round 4 assessments | adversarial + optimism + 2 controller audits + 2 liveness sweeps | ledger §Round-4 inputs | **DONE** |
| Round 4 plan | opus critic: R1 REJECT (9 changes) → R2 ACCEPT-WITH-CHANGES → R3 final | `round4-plan.md` §Revision history | **FINAL** (`c854f2c`) |
| Round 4 execution | — | — | **W0 launching** |

Full evidence for every row, with file:line, is in the [join ledger](tracks/platform-stub-closeout_20261015/round3-join-ledger.md).

---

## 3. Decisions

**This program (2026-10-16 / 10-17):**
- **Continue the four-round loop as planned**; checkpoint the Codex tree first (`3d08ace`) rather than
  converging early — owner's call.
- **Four test layers added** (lane V): backend HTTP flows, MSW feature slices with the real SDK client,
  Vitest browser mode, hermetic Playwright E2E (`azoa-e2e` compose project, ports 3200/5200).
- **SDK client-side `DexAdapter`/`JupiterAdapter`/`TinymanAdapter`: retire only** (round 4). SDK swaps
  go through the backend durable lifecycle; update `AGENTS.md:86` parity wording.
- **WTT under a tenant credential delivers only to the user's own custodial wallets; relayer fee must
  be `"0"`.** "Own" = proven custody (`IKeyCustodyService.CanSignAsync`); client wallet creation is
  always `External`. Signature-verified External wallets are a round-4 follow-up.
- **SurrealForge gaps are fixed in SurrealForge**, not worked around in AZOA. Owner grant 2026-10-17:
  *"feel free to send patches into surrealforge — we have the repo locally and own it."* Push/tag/publish
  stay with the owner.
- **`IsUniqueViolation` becomes public; the release ships as 1.3.0** (new public API → minor).
- **Runbook rewritten lean** (this file); old one in git.

**Round 4 (2026-10-17):**
- **SurrealForge 1.3.0 published final** (final code `b5a7d52`; integration 47/47 on
  3.2.4/3.1.4 × memory/RocksDB, 1202 unit). No local-pack/rc machinery. **Release flow changed
  2026-10-17 (owner-approved):** SurrealForge's `publish.yml` now runs via `workflow_run` after `CI`
  succeeds on a push to `main`, packs the exact CI-tested `head_sha`, and `--skip-duplicate` skips
  pushes that don't bump the version; the GitHub-Release trigger was removed (commit `203b76c`). The
  owner authorized pushing; `main` = `586e316` (1.3.0 merge), CI → publish in progress. A sweep-found
  library bug becomes a 1.3.1: bump `Directory.Build.props`, merge to `main`, push.
- **Compile-only builds per lane are allowed** (`dotnet build` / `tsc` / SDK build). Tests, lint gates,
  schema goldens and E2E still run once in the integrated sweep.
- **`AwaitingSignature` gets a submit-signed route** (exact-byte verification against the saved plan →
  broadcast → reconcile to terminal; idempotency key leaves `InProgress`).
- **Execution model:** ~5–7 large executor lanes, each in its own git worktree with full permissions,
  doing implementation + compile-check + its own self-review/fix loop, and merging its own branch into
  `feat/platform-stub-closeout` (ordered merges; rebase + compile clean before merging; `--no-ff`;
  never push or force). Then **one comprehensive independent review** (correctness + transaction scope,
  security, test quality) → one fix batch → the integrated sweep. Replaces per-lane reviewers.
- **Delegated child credentials:** build the missing pieces; enable in test configs; turn production on
  only after the route-authorization gate and tenant-branch flows pass the sweep.
- **KYB gates:** custodial accounts, tenant-on-behalf value scopes (at `ITenantConsentGate`), and
  payout price schedules.
- **Exchange/DEX activation:** declare the modules now; production on per chain only after that chain's
  local protocol fixture passes; anything still off at the end → `deferred.md` entry.
- **Tenant-priced payouts are tenant-funded** (owner chose this over the min-price default): a tenant
  price schedule is allowed, but a payout at a tenant price is funded from the tenant's own balance, not
  the node's. Needs a tenant-funded payout source and a balance-coverage check before any send.
- **D-8 owner decisions (2026-10-17):** float credited net of Stripe fees; tenant-funded collects go to a
  tenant-attributed reserve partition; cross-scope arbitrage capped via `on_ramp_lot.pricing_scope_key`. D-8 critic verdict
  ACCEPT-WITH-CHANGES, all 7 changes built (merged `9f484c6b`).
- **Schema generation reset (2026-10-17):** fresh DB instead of baselines + forward migrations. **Owner step before the next
  Railway deploy: reset the SurrealDB volume** (drops what is there today; pre-launch, no customer data).
- **Merged to `main` + pushed once mid-round (owner, 2026-10-17):** `main` = `e0deb11` (W0). First CI run red as expected;
  Railway waits for green CI, so production stayed on `c6c55dd`. Next push only after the sweep.
- Defaults taken (not asked): Identomat recovery closed via retry + `/delete/`; Veriff → `deferred.md`
  unless test credentials arrive; Eth/Sol custody parity stays design-only (accepted-with-changes
  amendment) with an owner line in `deferred.md`; minimal first-party UI (verify identity, add funds,
  cash out); the live Identomat drill needs the owner to create the two configs (raised when L2 reaches it).

**Standing decisions from the prior runbook that still bind** (full text: `git show 0522fdf:conductor/RUNBOOK.md`):
- **D8** — Identomat callback is an untrusted trigger; `/result/` is the only authority.
- **D23/D24/D25** — person-key uniqueness per tenant by default; AML screening ships with KYC; `KycIdentity` has retention + erasure.
- **D43/D45** — AZOA owns the Stripe rail (fiat is first-class, no sidecar). **D44** — token-native, chain-first; fiat is only on/off-ramp.
- **D46** — KYC is uniform: full document KYC plus a provisioned custodial wallet.
- **D13/D15** — amounts carry currency (no `ulong`); USD only for now.
- **D22** — the bridge is generic over capability and protocol.
- **D27/D36** — production custody before real fiat value; custody seam provider-agnostic, KMS vendor deferred.
- **D28/D35/D51** — terminology neutralization; `ActionGraphBundle`, `ActionDefinition`, `User`. Enforced now by `tests/AZOA.WebAPI.Tests/Architecture/RetiredVocabularyGuardTests.cs` (no quest/avatar/STAR/ODK/game words in shipped source).
- **No `git push` from a workflow** — record READY TO PUSH; the push is an explicit owner step.

---

## 4. Assumptions

Highest reversal cost first. The top line is the likeliest to be wrong.

1. **Static-review approvals hold at runtime** · default: treated as unverified until the sweep · to
   reverse: the sweep will show it; budget several fix waves (four fail-closed classes were found by
   reading alone, and the sweep will surface layered errors).
2. **Identomat standard mode has no child session; the parent id is the session token** (D4
   downgraded; fakes default to standard mode) · to reverse: if the owner's real workflows enable
   additional-information mode, adapt the adapter + fakes; confirm when workflows exist.
3. **The WTT "custodial only" reading of "user's own wallets"** · to reverse: add a persisted
   signature-proof flag for External wallets and accept those (a planned follow-up anyway).
4. **SurrealForge 1.3.0 behavior changes are acceptable for other consumers** (`IsUniqueViolation`
   tightened; strict check one-way) · to reverse: a 1.3.1 patch.

---

## 5. Environment & gotchas

- **Repo/branch:** `feat/platform-stub-closeout` (local only, unpushed). `main` = `bb512cf`, 60 ahead of `upstream/main`.
  Single remote: `upstream` (`github.com/JadeZaher/azoa-autonomous-zones-of-action`).
- **Round-4 worktrees** (created by the lanes; `git worktree list`): `..\oasis-sleek-r4-<lane>` on
  `r4/<lane>`; W0 helpers on `r4/core-chains` and `r4/core-fiat`. The main checkout must stay clean —
  lanes merge into it. Worktrees are removed after the W3 review.
- **Parallel session `oasis-sleek-38`** works in worktree `C:\Users\atooz\Programming\Projects\oasis-sleek-vocab`
  on `chore/retired-vocabulary-sweep`. Protocol: it merges our checkpoints only at quiet points (we
  send a hash), re-applies its rename as a logged pass, returns a merged hash; we fast-forward. It
  never writes to this checkout. `round3-join-ledger.md` lines quoting the retired word list are pinned
  as history in its `sweep.py`.
- **⚠ INCIDENT 2026-10-17 07:43:34 MDT — the podman WSL machine is gone.** The WSL distribution
  `podman-machine-default` was unregistered (`wsl -l` shows no distributions; the `wsldist` folder is
  empty; no Lxss registry entries). Every container and volume inside it was lost, including
  `azoa-dev-surrealdb` and its data and `logseq-ai-hub`. **Cause (found 2026-10-17):** a separate
  Claude Code disk-cleanup session in `C:\Users\atooz` (session `f315249a…`) ran
  `wsl --shutdown` + `wsl --unregister Ubuntu` + `wsl --unregister podman-machine-default` at 13:43Z,
  after the owner selected "Nuke everything now" (334 GB reclaimed). Its preview listed the Ubuntu
  home contents but not the podman containers' data. The SurrealForge agent's throwaway test
  containers were running and were killed mid-run. The base image and cache survive, so the machine
  can be recreated empty. **Recovered 2026-10-17 08:53 (owner decision): `podman-machine-default`
  recreated empty** — podman 5.7.1, 6 CPU / 2 GiB / 100 GiB, **rootless** (`podman machine set
  --rootful` if needed; SurrealDB RocksDB containers use `--user 0:0`), Docker-API pipe proxy not
  available (podman CLI is fine). `azoa-dev-surrealdb` does not exist until `.\dev-up.ps1 -ResetDb`
  rebuilds it (pre-launch, reseedable); `logseq-ai-hub` needs its own redeploy by the owner.
- **Containers (podman, before the incident):** `azoa-dev-surrealdb` (SurrealDB **3.1.4**, host :8020,
  the shared dev DB); `logseq-ai-hub` owned :3000 (another app). E2E runs as compose
  project `azoa-e2e` (own volume; ports 3200/5200). SurrealForge tests use throwaway containers
  (`surrealforge-test-121` :8031 / 3.1.4 :8032) — reap after use.
- **Credentials:** values live in the ignored root `.env` under `IDENTOMAT_API_KEY`,
  `STRIPE_SECRET_API_KEY_DEV`, `STRIPE_PUBLISHABLE_API_KEY_DEV` (+ `_LIVE` — do not use),
  `Fiat__Test__WebhookSecret`, `Kyc__Identomat__CallbackApiKey`. Never print them; scan commits for
  them (a node one-liner compared every `.env` value against staged files at each checkpoint — keep
  doing that).
- **Sweep prerequisites, in order:** `round4-plan.md` §8 (P-1 compose provider under rootless podman →
  P0 1.3.0 restore → P1 schema regen → P2 upgrade rehearsal → P3 `dev-up -ResetDb` → P4 SDK build →
  P5 local chains, Stripe CLI, Identomat configs). The frontend imports the *built* `azoa-sdk`, so
  `npm run build` the SDK before any frontend typecheck.
- **Railway upgrade risk (was "DEPLOY-BLOCKING"; downgraded by the round-4 plan §1):** no `.surql` on
  `main` defines `ethereum_preparation`, so the stale `ethereum_preparation_nonce` index can't exist
  there; W1 ships a defensive `REMOVE INDEX IF EXISTS` anyway (`surrealforge up` never drops indexes
  without `--allow-destructive`). The real risk is new non-optional fields and UNIQUE indexes against
  old rows — W1's old-row rule and sweep P2 (upgrade rehearsal) cover it.
- **Root `AZOA.WebAPI.csproj` compiles every `**/*.cs` in the repo**, including under `frontend/` —
  lane V's .NET egress probe is a `.csx` file for that reason.
- **Codex trace tools:** `.omc/research/claude-resume/codex-tail.mjs <rollout.jsonl> N` prints the last
  N progress messages of a Codex rollout; `codex-files.txt` has per-lane edit history. ChatGPT `cx_`
  share links 403 to WebFetch — read `~/.codex/sessions` instead.
- **Bash heredocs on this machine mangle backslashes** — write scripts with the Write tool.

---

## 6. Key files

- `conductor/tracks/platform-stub-closeout_20261015/round3-join-ledger.md` — **the** evidence file:
  every finding (file:line), owner decision, round-4 carry-forward, product defects D1–D9, sweep order.
- `.../round3-closeout.md` — one-page round-3 summary and round-4 seed.
- `.../round3-transaction-scope-review.md` — SurrealDB concurrency model + reviewer checklist (use it in every review).
- `.../round3-claude-resume.md` — lane protocol, shared-file rules, lane V ownership.
- `.../plan.md`, `.../deferred.md` — loop rules and the two genuine deferrals.
- `.omc/research/round3-join-compile-errors-2026-10-16.txt` — 247 compile errors.
- `Core/Surreal/AGENTS.md` — signing-fence binding rule; `AssignRecord` vs `Assign` (§generated-statement-scope).
- `Helpers/AGENTS.md` §Identity evidence serialization — why `KycEvidenceFence` must stay one statement.

---

## 7. Continuation plan

### ▶ RESUME HERE (2026-10-17 evening — live QA on Railway; loops deferred until after QA)

1. **Verify state.** `git status`, `git log -5 --oneline` on `feat/platform-stub-closeout` and `main`;
   `git worktree list` (round-4 worktrees `../oasis-sleek-r4-*` can be removed once their branches are merged).
   Check whether `main` was pushed (`git rev-list --count upstream/main..main` = 0) and which Railway deployment is live.
2. **If the push hasn't happened:** the gate is fix-core2 (API startup DI deadlock broken + host proven to start;
   9 `UpdateOnly`/`DeleteOnly`-without-`Where` write paths fixed). Merge each finished final-round branch with
   `scratchpad/merge-lane.ps1` (recreate from §5 if the scratchpad is gone: base merge in worktree → build →
   merge-check → secret-scan → `--no-ff` into the main checkout → identical-tree check). Then `git switch main`,
   `git merge --no-ff feat/platform-stub-closeout`, secret-scan `upstream/main..main`, push (owner-approved).
3. **QA deploy (owner steps):** Railway "Wait for CI" off on the AZOA services; reset the Railway SurrealDB
   volume (D-9 generation reset); confirm the deploy reaches healthy and the schema job applies `--no-reconcile`.
4. **During QA:** record every finding in `round4-post-qa-deferrals.md` §Recorded during QA. Don't fix one at a time —
   batch them.
5. **After QA:** one fix round over the QA findings + the deferral ledger's engineering items → the integrated
   sweep (plan §8) → CI green → **re-enable Railway "Wait for CI"** (release blocker) → close round 4
   (`round4-closeout.md`, metadata `round_states.4 = closed`).

**Agent git note:** mid-session the permission classifier started denying git to subagents; the owner chose
orchestrator-run merges with per-command approval. Fixers edit files and report changed-file lists; the
orchestrator commits and merges.

---

## 8. Session log

### 2026-10-17 (afternoon–evening) — round 4 executed (Claude Code, orchestrator)
- Done: W0 compile convergence (247 errors → 0) + W1 schema regen (131 tables, old-row DEFAULT ALWAYS) +
  seven W2 lanes merged in order (L7 swapped ahead of L6) + D-8 tenant-funded payouts (critic
  ACCEPT-WITH-CHANGES, owner D-8a/b/c); `main` pushed once mid-round at W0 (`e0deb11`, CI red as expected,
  Railway held); W3: 7 independent reviews (RV-L4 FAIL) → 6 fixers → bounded re-checks of every HIGH → merged;
  owner D-9 (generation reset) and D-10 (provenance on transfer, chain is source of truth).
- Found and fixed along the way: double clawback on an ambiguous broadcast; one Ethereum tx settling two
  operations; Solana false "never landed"; DApp-manager escalation via child credentials; reversal pause
  bypassed by the bare KYC gate; NFT recipient resolution after duplicate wallets; payout setup permanent
  lock-out; silently empty operator pages.
- Found by L6, fixed in the final round (fix-core2): API startup DI deadlock (every host test + the real process
  hung); SurrealForge 1.3.0 `UpdateOnly`/`DeleteOnly` without `Where` throws (9 prod write paths).
- Retro: a fail-closed compile is not a running app — the deadlock passed every build, review and 4,700 unit
  tests; only starting the host found it. "Unknown treated as absent" recurred on three money rails.
  Cross-lane seams (scope delegation × scope-trusting policy) produced the worst security bug.

### 2026-10-16 → 10-17 — Codex resume, round 3 completed, round 4 started (Claude Code)
- Done: recovered the Codex trace from `~/.codex/sessions`; checkpoint `3d08ace`; lanes N/A/B/C/V
  delivered and reviewed (correctness + security), ≤2 fix cycles each; four fail-closed classes found
  and fixed tree-wide (fence binding names, validator FOR/closure, single-record-as-array capture,
  `user` reserved word → SurrealForge); compile cascade partly fixed; round-3 join `baf1224`; merged
  with the vocabulary sweep → `0522fdf`; SurrealForge 1.2.1 → 1.3.0 prepared and reviewed; runbook
  rewritten.
- Reviewed: every lane plus SurrealForge — see §2 ledger.
- Done since (2026-10-17 morning): podman machine recreated (rootless) after the incident; round-4
  assessments, two controller audits and two liveness sweeps recorded; SurrealForge 1.3.0 finished,
  reviewed, release flow switched to publish-after-CI (`203b76c`) and **published** (`586e316`);
  round-4 plan through three critic rounds to **final** (`c854f2c`).
- In flight: round-4 W0 (L1 `r4/core` + helpers A/B).
- Blocked on the owner: Identomat KYC/KYB configs + live sessions (`deferred.md`
  IDENTOMAT-WORKFLOWS); `logseq-ai-hub` redeploy.
- Retro:
  - **Fail-closed code passes deny-path review.** Every big round-3 bug made a feature *refuse
    everything*. Only allow-path real-database tests catch that.
  - **Fakes derived from our own adapter agree with it by construction** (D4). Derive them from the
    captured vendor docs.
  - **Challenging carry-forwards on money paths pays:** the clawback rung hid two reachable
    over-recovery bugs plus a full-share clawback.
  - **When two APIs return the same error class, check they agree on which error they report**
    (`EnsureAllOk` vs the unit-of-work path).
  - **A sub-agent report delivered mid-turn can be silently absorbed.** The identity/consent liveness
    sweep (2 CRITICAL) never reached the ledger until its transcript was read back. At every join,
    reconcile the list of launched agents against the reports recorded.
- Next: §7 step 1.

---

## 9. Recommended invocations

1. **W0 / W2 lanes:** **one opus executor per lane** (`oh-my-claudecode:executor-high`), background,
   each owning a worktree; opus helpers only on the disjoint subtrees plan §4 names. Trigger: the
   owner's compact executor-lane model, with lanes that self-review and self-merge.
2. **W1 re-sweep and the D-8 design critic:** **one read-only opus agent each.** Trigger: new
   fail-closed classes are found only by reading; D-8 is new money design.
3. **W3:** **parallel opus reviewers** (`/code-review` high + `/security-review` on every money/auth
   lane), separate contexts prompted to refute, then ≤3 fixers. Trigger: plan §7; never self-approve.
4. **Sweep:** **inline**, because it's one sequential gate pass and failures batch; fixers by domain in
   worktrees.
