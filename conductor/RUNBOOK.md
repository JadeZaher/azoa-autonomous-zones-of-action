---
type: runbook
---

# AZOA — Session Runbook

**Last updated:** 2026-10-17 07:20 MDT · **Branch:** `feat/platform-stub-closeout` ·
**Last commit:** see `git log -1` (this rewrite lands on top of `0522fdf merge: feat/platform-stub-closeout round-3 join baf1224 into the vocabulary sweep`) — unpushed; `main` at `bb512cf`.

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

**Rounds 1–3 closed at source level; round 4 started 2026-10-17. Nothing has been compiled or tested
in this tree** except lane V's frontend harness smoke runs. Every "approved/closed" below is a
static-review verdict, not runtime proof.

- **History:** Codex (VS Code, rollout `01a13fa5`, `~/.codex/sessions/2026/10/15/`) ran rounds 1–3 on
  2026-10-15 and stopped mid-round-3 at 23:04Z. Claude Code checkpointed it as `3d08ace`, finished
  round 3 with lanes N/A/B/C/V, and committed the join as `baf1224`. Session `oasis-sleek-38` merged
  that with its retired-vocabulary sweep as `0522fdf`; this branch fast-forwarded to it.
- **Compile:** a real build of `0522fdf` (by `oasis-sleek-38`, in its own worktree) gives **247 unique
  first-wave errors**, the same set as `baf1224` alone. List with root causes:
  `.omc/research/round3-join-compile-errors-2026-10-16.txt` and
  [round3-join-ledger.md](tracks/platform-stub-closeout_20261015/round3-join-ledger.md) §Post-join state.
  Test projects never compiled.
- **SurrealForge (owner's library, `C:\Users\atooz\Programming\Projects\surrealforge`):** release
  **1.3.0** on branch `fix/1.2.1-reserved-tables-and-declared-variables`, last known commit **`2067ae4`**
  (reviewed through `3aacd25`, verdict PUBLISH; `318f2ad`/`a0edfa4` add the public, tightened
  `IsUniqueViolation`, verified identical on SurrealDB 3.1.4 and 3.2.4; `2067ae4` makes
  `SurrealResponse.EnsureAllOk` blame the culprit slot — see below). Pre-publish re-review of
  `3aacd25..2067ae4` was running at handoff. **Not published.**
- **CONFIRMED + FIXED (in SurrealForge, 2026-10-17): raw-transaction error attribution.** Measured on
  3.1.4 and 3.2.4: an aborted `BEGIN; …; COMMIT;` fails every slot ([1] "not executed due to a failed
  transaction", [2] culprit, [3] "…cancelled transaction", [4] "Cannot COMMIT: …"; a write conflict's
  "can be retried" appears only in [4]). `EnsureAllOk` used to throw slot [1], so **every AZOA conflict
  catch on a raw transaction (THROW markers, `IsUniqueViolation`) and every `RetryOnConflictAsync` was
  dead.** 1.3.0 throws the first non-collateral slot (`SurrealResponse.cs:107`, `FindBlamedSlot` `:125`).
  AZOA needs no edits — only the 1.3.0 bump.
- **Round 4, in flight at handoff** (background agents of the previous session; they may not
  survive the session ending):
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
  - reconciled `round4-plan.md` — being written by an opus planner (then an adversarial critic review,
    then owner decisions in one round).
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
| SurrealForge 1.2.1→1.3.0 | opus pre-publish review + re-review | regex-literal regression, breaking `ToString()` → fixed; `\z`, escape list, graph hops | **PUBLISH** through `3aacd25`; delta after it needs a re-review |
| R3 join | `round3-closeout.md` | — | **CLOSED 2026-10-16** (`baf1224`) |
| Round 4 | — | — | **in progress** (assessments) |

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
- **Parallel session `oasis-sleek-38`** works in worktree `C:\Users\atooz\Programming\Projects\oasis-sleek-vocab`
  on `chore/retired-vocabulary-sweep`. Protocol: it merges our checkpoints only at quiet points (we
  send a hash), re-applies its rename as a logged pass, returns a merged hash; we fast-forward. It
  never writes to this checkout. `round3-join-ledger.md` lines quoting the retired word list are pinned
  as history in its `sweep.py`.
- **Containers (podman):** `azoa-dev-surrealdb` (SurrealDB **3.1.4**, host :8020, the shared dev DB —
  never wipe without reason); `logseq-ai-hub` owns :3000 (another app — leave it). E2E runs as compose
  project `azoa-e2e` (own volume; ports 3200/5200). SurrealForge tests use throwaway containers
  (`surrealforge-test-121` :8031 / 3.1.4 :8032) — reap after use.
- **Credentials:** values live in the ignored root `.env` under `IDENTOMAT_API_KEY`,
  `STRIPE_SECRET_API_KEY_DEV`, `STRIPE_PUBLISHABLE_API_KEY_DEV` (+ `_LIVE` — do not use),
  `Fiat__Test__WebhookSecret`, `Kyc__Identomat__CallbackApiKey`. Never print them; scan commits for
  them (a node one-liner compared every `.env` value against staged files at each checkpoint — keep
  doing that).
- **Sweep prerequisites, in order** (full list: ledger §Final-sweep prerequisites): `.\dev-up.ps1 -Reset`
  once (also drops the stale unique index `ethereum_preparation_nonce`); regenerate `.surql` for every
  new POCO field (lanes A/B/C/N); `npm run build` the SDK before any frontend typecheck (frontend
  imports the *built* `azoa-sdk`).
- **DEPLOY-BLOCKING:** don't deploy this branch to Railway until `ethereum_preparation_nonce` is
  removed there. **Verified 2026-10-17: `surrealforge up` skips index drops without
  `--allow-destructive`**, so ship an explicit `REMOVE INDEX ethereum_preparation_nonce ON
  ethereum_preparation` migration step. Otherwise every new nonce reservation for a sender fails.
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

### ▶ RESUME HERE (2026-10-17 — round 3 closed, round 4 starting)

1. **Verify state.** `git status` (expect clean) and `git log -3` (expect the runbook commits on top
   of `0522fdf`). In the SurrealForge repo, `git log --oneline -3` on
   `fix/1.2.1-reserved-tables-and-declared-variables` (expect `2067ae4` or later). Check the runbook
   §2 ledger / §8 entry for the 1.3.0 re-review verdict, and whether
   `round4-assessment-adversarial.md` and `round4-assessment-optimism.md` exist in the track directory.
2. **Finish SurrealForge 1.3.0.** The code is done at `2067ae4`. If the pre-publish re-review of
   `3aacd25..2067ae4` has no recorded verdict, re-run it (an opus reviewer, read-only, prompted to
   refute: the public and tightened `IsUniqueViolation`, and `EnsureAllOk` culprit selection). Apply
   any fixes, then hand the owner the publish commands (merge → `git tag v1.3.0` →
   `git push origin v1.3.0` → `gh release create v1.3.0`). After the publish, bump AZOA to 1.3.0 at
   `AZOA.WebAPI.csproj:109,110,113`, `tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj:30`
   and `tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj:45`.
3. **Round 4 assessments.** If either file is missing, re-run it. Two independent opus agents,
   read-only on code, each writing its own file:
   - **Adversarial:** hidden semantic stubs; states with no exit; fail-closed classes not yet swept;
     cross-tenant/IDOR; end-to-end traces of KYC, KYB and the Stripe on-ramp → webhook → funds → payout →
     reversal; the original inventory in `plan.md`; the 247 errors by root cause; whether `deferred.md`
     meets its bar.
   - **Optimism-challenge:** audit every completion claim against the evidence that actually exists;
     find a "do it now" path for each deferral and carry-forward; check that one sweep is realistic;
     produce a three-way table (done / built-but-unverified / not done).
4. **Reconcile both into `round4-plan.md`** (OKF frontmatter). **Put one decision to the owner in
   the plan:** the optimism assessment's sequencing (compile-until-clean over 6–8 builds, then one full
   sweep, then ≤3–4 targeted batches) bends the one-sweep rule; ask whether compile iterations count
   as "the sweep". Also decide each of the ten unrecorded deferrals it lists: do it in round 4, or
   record it in `deferred.md` with evidence. Seed the plan from the ledger in this order:
   1. Compile convergence. Root causes: `Helpers.Encoding` vs `System.Text.Encoding` (50),
      `Algorand.Utils.Encoder` vs `System.Text.Encoder` (17), `ValidateAddressFormat` never written (17),
      missing `using SurrealForge.Client` for `SurrealLink` (11), then the long tail.
   2. **Security findings received during round 4** (ledger §Round-4 inputs; two controller audits,
      4 HIGH + 11 MEDIUM):
      - Deployments and contract calls skip signing-scope checks (a `dapp:manage` key moves wallet value).
      - Holon subtree clone copies private descendants.
      - `dapp-role` accepts any-scope keys and lets a manager demote other managers.
      - Tenants price node-funded cash-outs (blocked while off-ramp is disabled).
      - Then the MEDIUMs, plus a **route-authorization gate**: every value-moving route captures the
        credential and requires its signing scope.
      Then lane B's two clawback crash-path MEDIUMs, a gate before real cards. Consider fixing the
      idempotency ledger's `Failed`-key re-claim in SurrealForge.
   3. Stripe defects D1, D2 and D8.
   4. The SRDB0001 layer (~35 interpolated `SurrealQuery.Of` sites). Consider an analyzer-recognized
      identifier-template builder in SurrealForge.
   5. The stale-index deploy fix.
   6. D5, D6, D7 and D9 (providers take an injected HttpClient).
   7. SDK `DexAdapter` retirement and the `AGENTS.md:86` update.
   8. Architecture gates: transaction scope, cross-tenant, allow-path real-DB fixtures for every
      fence, interleaving tests.
   9. Mock debt in the integration tests.
   10. Per-lane slice/journey specs on lane V's harness.
   11. Per-lane LOW carry-forwards.
5. **Adversarial plan review** (`oh-my-claudecode:critic`, opus) → fix the plan → dispatch waves with
   **disjoint file ownership**. Compile convergence is wave 1, because everything runtime depends on
   it. Use `/slice` for partitioning.
6. **Per lane: review → fix (≤2 cycles) → re-review**, correctness plus security on money and auth
   paths, loading `round3-transaction-scope-review.md`. **Challenge every carry-forward on a money
   path;** in round 3 that found two reachable over-recovery bugs. Then write `round4-closeout.md`,
   commit, and offer `oasis-sleek-38` a merge hash at a quiet point.
7. **Integrated sweep, once,** in the ledger's prerequisite order:
   1. `dotnet build azoa.sln`
   2. unit tests
   3. integration tests (dev SurrealDB :8020)
   4. lane V's HTTP flows
   5. SDK vitest and build
   6. frontend `npm test`, `test:browser`, lint, typecheck and build
   7. E2E via `node e2e/scripts/e2e-stack.mjs up`
   8. schema goldens

   Record failures as one batch, fix them, and re-run only the affected gates.
8. **Merge-to-main sweep:** `--no-ff` the feature branch into `main`, sweep on main, and report READY
   TO PUSH. The owner pushes.

---

## 8. Session log

### 2026-10-16 → 10-17 — Codex resume, round 3 completed, round 4 started (Claude Code)
- Done: recovered the Codex trace from `~/.codex/sessions`; checkpoint `3d08ace`; lanes N/A/B/C/V
  delivered and reviewed (correctness + security), ≤2 fix cycles each; four fail-closed classes found
  and fixed tree-wide (fence binding names, validator FOR/closure, single-record-as-array capture,
  `user` reserved word → SurrealForge); compile cascade partly fixed; round-3 join `baf1224`; merged
  with the vocabulary sweep → `0522fdf`; SurrealForge 1.2.1 → 1.3.0 prepared and reviewed; runbook
  rewritten.
- Reviewed: every lane plus SurrealForge — see §2 ledger.
- In flight: round-4 assessments (×2, plus their controller-audit sub-agents); SurrealForge 1.3.0
  pre-publish re-review of `3aacd25..2067ae4`.
- Confirmed 2026-10-17: the raw-transaction error-attribution class was real (every AZOA raw-transaction
  conflict catch and transient retry was dead); fixed in SurrealForge `2067ae4`.
- Blocked: SurrealForge publish (owner); real Identomat workflows (owner — `deferred.md`
  IDENTOMAT-WORKFLOWS).
- Retro:
  - **Fail-closed code passes deny-path review.** Every big round-3 bug made a feature *refuse
    everything*. Only allow-path real-database tests catch that.
  - **Fakes derived from our own adapter agree with it by construction** (D4). Derive them from the
    captured vendor docs.
  - **Challenging carry-forwards on money paths pays:** the clawback rung hid two reachable
    over-recovery bugs plus a full-share clawback.
  - **When two APIs return the same error class, check they agree on which error they report**
    (`EnsureAllOk` vs the unit-of-work path).
- Next: §7 step 1.

---

## 9. Recommended invocations

1. **§7 step 2, finishing SurrealForge:** **one agent** (opus executor) in the SurrealForge repo,
   then **one opus code-reviewer** pre-publish. Trigger: a library-internal change with a real-server
   measurement; it's an injection-guard library, so the review is mandatory.
2. **§7 step 3, assessments:** **two parallel opus agents**, read-only, each writing its own file.
   Trigger: the owner's loop requires independent adversarial and optimism assessments.
3. **§7 step 5, waves:** **`/slice`, then a wave-ordered fan-out.** Trigger: wave 1 (compile
   convergence) must realize a tree before runtime waves; files partition by root cause and domain;
   two lanes must never share a store file.
4. **§7 steps 6–7, per-lane review and the sweep:** **`/code-review` high, plus `/security-review`**
   on fiat/custody/auth paths (non-negotiable). Trigger: money and auth paths. The sweep runs
   **inline**, because it's one sequential gate pass and failures batch.
