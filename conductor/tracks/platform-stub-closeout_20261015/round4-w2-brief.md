---
type: lane-brief
round: 4
wave: W2
---

# Round 4 · W2 shared brief (every W2 lane and helper reads this first)

Repo `C:\Users\atooz\Programming\Projects\oasis-sleek` (.NET 10 WebAPI, root `AZOA.WebAPI.csproj`
compiles every `**/*.cs`; SurrealDB 3.1.4 via SurrealForge 1.3.0; TS SDK `sdk/azoa-wallet`; Next.js
`frontend/`). Windows: Bash (Git Bash) or PowerShell — if node `execSync` fails under Bash with
`cmd.exe ENOENT`, run the node scripts from PowerShell. Base: `feat/platform-stub-closeout` after
merge #2 (`1912e86` + record `2b58a3e`): whole solution compiles, warnings ≤ 25, IDE0005 enforced.

## Read before coding
1. `round4-plan.md` — §0 decisions, §1 ground truth, **§3 execution model (all of it: lane loop, merge
   protocol, merge check, Program.cs ordering rule, shared files, inventories)**, **your lane in §4**,
   §4.8 table ownership, §4.9 route contracts, §7 fail-closed class list.
2. `round4-sql-sweep.md` — findings tagged with your lane are **in scope** (SQL-01..13).
3. `round4-ci-main-e0deb11.md` — first CI run: failing tests in your owned area are in scope.
4. `round4-lane-L1-core.md` (W0 notes for L2–L7) and `round4-lane-L1-schema.md` §For W2 lanes.
5. `round3-transaction-scope-review.md` (checklist on every store/fence you touch), root `AGENTS.md`,
   the `AGENTS.md` of every directory you edit, `Persistence/SurrealDb/AGENTS.md` (old-row rule,
   unique-index audit, partial-index NONE behaviour).

## Rules that changed since the plan was written
- **Tests:** still no full test runs. You MAY run your own already-failing or newly authored unit tests
  by filter (`dotnet test tests/AZOA.WebAPI.Tests/... --filter "FullyQualifiedName~X"`) to confirm a fix.
  No integration/E2E runs, no `dev-up`, no containers (exception: a throwaway SurrealDB 3.1.4 podman
  container `--user 0:0` on a free port 8041–8049 to probe a SurrealQL behaviour; reap it).
- **New POCO fields:** on an existing table use `option<>` or `[Default("ALWAYS …")]`, plus a backfill
  migration line if a compare-and-swap reads the field; on a baselined table add a forward migration in
  the same change. Regenerate schema with the project generator (never hand-edit `.surql`); the sweep
  regenerates again at P1.
- **`dotnet build-server shutdown` is machine-wide — never run it** (other lanes are building). Kill
  only processes you started.
- Unused usings fail the build (IDE0005 is real now).

## Worktree, branch, ownership
`git worktree add ../oasis-sleek-r4-<lane> -b r4/<lane> feat/platform-stub-closeout`. Helpers:
`../oasis-sleek-r4-<lane>-<helper>` on `r4/<lane>-<helper>`, branched from the same base; edit only the
helper subtree named in plan §4; the lane lead merges the helper branch (`git merge --no-ff`) when its
head subject starts with `r4(<lane>-<helper>): ready`. Never edit outside your owned globs (plan §4) —
write cross-lane needs under `## Needs other lane` in your record with file:line + change.

## Loop (plan §3.2)
Implement → compile-check (`dotnet build azoa.sln -c Debug`: 0 errors, warnings ≤ 25; SDK
`npm run build` / frontend `npm run typecheck` when touched) → self-review & fix ≤2 iterations
(transaction scope, security checklist on every changed route/service, §7 classes, flow-tests rules for
authored tests, AGENTS.md pruning gate) → inventory files (§3.5) → lane record
`round4-lane-<lane>.md` (`type: lane-checkpoint`, with `## Hunks` for every Program.cs/config hunk,
info string `hunk path=<file>`) → commit WIP as you go (`r4(<lane>): wip …`).

## Merging (plan §3.3) — DO NOT merge until the orchestrator signals you
When ready, commit with subject `r4(<lane>): ready …` and report "ready to merge" (stop). The
orchestrator signals lanes in order L1p → L2 → L3 → L4 → L5 → L6 → L7, one at a time. On the signal:
in your worktree `git merge feat/platform-stub-closeout` (never rebase; conflict hunks in files owned
by an already-merged lane keep that lane's side) → compile clean → `node scripts/round4/merge-check.mjs`
→ `node scripts/round4/secret-scan.mjs . "feat/platform-stub-closeout HEAD"` → in the main checkout
(clean, on `feat/platform-stub-closeout`): `git merge --no-ff r4/<lane>`, `git diff --quiet r4/<lane> HEAD`,
merge check on HEAD → commit `merged: <hash>` into your record frontmatter → report "merged".

## Hard rules
Never push, force, rebase or amend merged commits; never touch `main`, other worktrees or `azoa-*`
containers; never print `.env` values; Stripe test mode only; commit messages end with
`Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Rationale in directory `AGENTS.md`, terse
one-line doc-comments in code. Stub only what is truly externally blocked — fail-closed, labelled, named
unblock — and log it; never stub what can be built.
