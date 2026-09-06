---
type: Track Plan
title: Terminology neutralization cutover — plan
description: Wave-ordered execution plan for the Avatar→User / Quest→Action rename and its two-consumer cutover.
tags: [plan, terminology-neutralization-cutover_20260905, in-flight]
timestamp: 2026-09-05T00:00:00Z
resource: ./metadata.json
---

# Plan

The transform is `scripts/d28_rename.py`. `verify` is a **gate**: it must pass
before any `apply`. Waves are dependency-ordered, and the sweep is never split
by noun — `Program.cs` alone carries both, and 28 of ~30 `Interfaces/` files
reference both because `avatarId` was the ambient caller-identity parameter.

## Phase 0 — the transform and its gate ✅ DONE

- [x] Author the transform with per-casing guards and an explicit exclusion set.
- [x] **Adversarial review before first run** (separate context, prompted to
      refute). Returned 2 CRITICAL + 6 HIGH. Verdict: *"as delivered it does not
      run, and its C# rename target does not compile."*
- [x] Replace the original gate. It asserted
      `quest − request − question == guarded-matches`, which is a **tautology** —
      it reduces to "no substring `requestion` exists" and is blind to a third
      false-positive family. The tree contains `bequest` exactly once: in the
      runbook line claiming it doesn't.
- [x] Gate now: token classification + denylist, over-block detection, and a
      partition-coverage assertion.

## Phase 1 — backend, models, schemas, tests ✅ DONE

- [x] `apply models backend backend-root baselines tests` — 691 files.
- [x] `git mv` 250 paths (6 directories + 182 files, deepest-first).
- [x] Regenerate goldens: `AZOA_REGENERATE_GOLDENS=1 dotnet test`.
- [x] Re-base the 6 `CompatibilityBaselines` checksums, retaining generation-1
      values in-file so the re-base is auditable.
- [x] **Gate:** 0 errors / 23 warnings; 2431 passed / 1 skipped / 0 failed.

## Phase 2 — SDK ✅ DONE

- [x] `apply sdk`; rename `examples/avatar-crud.ts`.
- [x] `tsc` clean; **191 tests / 16 files** — this suite's status had been
      recorded as "genuinely unknown" since 2026-08-21.
- [x] `npm run build` (tsup) so the frontend can resolve renamed types.

## Phase 3 — frontend ✅ DONE

- [x] `apply frontend`; move 5 directories including the **load-bearing Next.js
      route dirs** (`/quests`→`/actions`, `/avatars`→`/users`,
      `/avatar-nfts`→`/user-nfts`) and 12 files.
- [x] Preserve the third-party `ui/avatar.tsx` primitive and its `<Avatar>`
      importer.
- [x] **Refresh `frontend/node_modules/azoa-sdk`** — it is a *copy*, not a
      symlink, and was 7 weeks stale, which had been making the frontend `tsc`
      gate pass vacuously.
- [x] **Gate:** tsc 0, build OK with renamed routes, 65 tests / 11 files.

## Phase 4 — documentation ✅ DONE

- [x] `apply docs misc` — 163 files; rename 11 archived track directories (D32).
- [x] Confirm idempotency: `verify` reports **0 files left to change**.

## Phase 5 — consumer cutover ✅ DONE 2026-09-06

### ArdaNova ✅ — `c:\Users\atooz\Documents\Escherbridge\ardanova`

**A CRITICAL silent break was found and fixed.** It would not have surfaced as
an error anywhere — it fail-closes every value action for every user:

`AzoaCustodialAccountGateway.cs` declared `StatusWire.AvatarId` with **no
`[JsonPropertyName]`**, relying on System.Text.Json's case-insensitive match
against AZOA's `avatarId`. Post-rename AZOA emits `userId`, and STJ **does not
throw on an unmatched property** — it yields `null`. That null then:
- trips `AzoaCustodialAccountService.cs:206` → permanent
  `"AZOA returned inconsistent custodial account readiness."` for every KYC'd
  account, and
- makes `AzoaValueEligibilityService.cs:49` (`!IsNullOrWhiteSpace(status.AvatarId)`)
  permanently false — and that service is the shared gate the repo's *uncommitted*
  security work had just wired into Opportunities, Tasks, TaskEscrows,
  FundingIntents and Swaps controllers.

Verified on both sides before fixing: AZOA's `TenantCustodialAccountStatusResponse`
was `AvatarId` at HEAD and is `UserId` now. **Fix: pin the wire name explicitly**
rather than rename ArdaNova's internals — minimal blast radius against 81
uncommitted files. Same treatment applied to `AzoaWireModels.cs` (dead today,
identical latent bug).

Also updated: 6 route literals (`/api/quest*`→`/api/action*`, incl. the
`QuestPrefix` **credential-scoping allowlist** — stale there would have rejected
every action call client-side before it left the process), `/api/avatar/register`
→ `/api/user/register`, contract doc-comments, path-traversal test cases, the
`run-azoa-e2e.ps1` harness, and the response fixture.

Client wrapper renamed under the same guarded regex; pin bumped `^0.1.0`→`^0.3.0`.

**Gates:** .NET `dotnet build` 0 errors · **1050 tests passing** (233 API + 817
Application) · client `tsc` exit 0 · **210 tests / 33 files**.

### Taawun ✅ NO CHANGE REQUIRED — `c:\Users\atooz\Programming\tawun`

Surveyed and verified directly. **Taawun has no wire coupling to AZOA at all:**
- **Zero HTTP calls** to AZOA anywhere in `src/pkg/financial/`.
- `AzoaClient` is **inert** — every method returns `ErrLegacyAzoaDisabled`; its
  own test constructs it with the literal host `invented.invalid`.
- **Zero `Avatar` references in any Go file** — Taawun models actors as opaque
  party-ID strings, so that half of the rename cannot affect it.
- Its `Quest*` vocabulary is **Taawun's own local orchestration state**, SQLite-
  backed, behind Taawun's own REST surface. Per D28's own principle — consumer
  vocabulary belongs to the consumer — renaming it is **not** required.

⚠ Two hazards recorded should a cosmetic pass ever be wanted: `Action` is
**already a live identifier** in Taawun (`shura/rbac.go` permission verbs, plus
a pre-existing unrelated `QuestStep.Action` field), so a text substitution would
collide; and the `quests`/`quest_id`/`quest_events` SQL identifiers are
**persisted data**, needing a migration rather than a rename.

### Integration contract ✅

`ARDANOVA-AZOA-INTEGRATION-CONTRACT.md` is now **byte-identical** across both
repos (sha256 `fb7218c0de877fda37c21ef7…`), zero residual old nouns.
⚠ **They had already drifted before this work** — ArdaNova's copy dated Jul 18
still referenced the pre-rename repo name `oasis-sleek/` and had different table
formatting. The file's own "both copies are byte-identical" invariant was being
violated silently; it is worth a periodic diff check.

### ⛔ Blocking prerequisite for ArdaNova deployment

**`azoa-sdk@0.3.0` is not published.** ArdaNova's client now pins `^0.3.0` and
typechecks green **only** because the local build was copied into its
`node_modules`. Publish before ArdaNova's CI can pass. Proof the cutover is
correct: against the real renamed build, `tsc` exits 0 and all 210 tests pass;
against the stale 0.1.0 the only failures are 5 "no exported member" errors
naming exactly the renamed symbols.

## Phase 6 — strands 2 and 3 ⬜ NOT STARTED

`scripts/d28_rename.py report-vocab` locates every site. These are **sentence
rewrites, not token swaps** — mechanically replacing "riba-free" yields
ungrammatical prose. The constraint must survive the rewrite: a
principal-only loan tracker is defined by the *absence* of an interest-accrual
primitive, and that absence is the feature.

## Phase 7 — deployment reconciliation ⬜ NOT STARTED (D29/D31)

- [ ] Determine how the `schema_migration` checksum ledger treats a renamed
      file: new row, or skip on name match? Answer this **before** running
      anything.
- [ ] Wipe and re-seed. ⚠ The two `first-node-demo` API keys **cannot** be
      re-seeded — SHA-256 storage, plaintext unrecoverable. Re-issue and
      coordinate with whoever holds them (scopes suggest ArdaNova).
- [ ] One combined apply: new tables + `operation_log.amount` widening + the
      renamed schema.

## Verification lane

One sweep at the end of each phase, never a test→fix→test loop:
`dotnet build --no-incremental` + `dotnet test`; SDK `tsc` + vitest;
frontend `tsc` + `build` + vitest.
