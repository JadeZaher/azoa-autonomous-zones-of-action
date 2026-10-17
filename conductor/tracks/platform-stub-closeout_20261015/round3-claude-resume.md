---
type: handover-protocol
status: active
owner: root
---

# Round 3 resume: Codex → Claude Code handover (2026-10-16)

The Codex session (VS Code, rollout `01a13fa5`) ran 2026-10-15 12:59Z–23:04Z and stopped
mid-slice in round 3. Every authored change was checkpointed unmodified as commit `3d08ace`
on `feat/platform-stub-closeout`; `main` stays at `bb512cf`. Owner decision 2026-10-16:
continue the four-round loop exactly as planned (finish round 3 delivery and review, run
round 4 in full, then the single integrated sweep).

## Lane provenance

| Lane | Codex thread (`~/.codex/sessions/2026/10/15/`) | Domain at cutoff | State record |
|---|---|---|---|
| N (was root) | `rollout-2026-10-15T06-59-53-01a13fa5-ca26-*.jsonl` | NFT/credential lifecycle, replacement mint cost consent, its SDK/UI | `.omc/research/platform-closeout-checkpoint.md` (latest sections), `round3-nft-*.md` |
| A | `rollout-2026-10-15T07-00-24-*.jsonl` | Wormhole token transfer (WTT) bridge, Solana native output, shared Solana durable signing | `.omc/research/wtt-lane-checkpoint.md`, `round3-wtt-*.md` |
| B | `rollout-2026-10-15T07-00-18-*.jsonl` | Hold / exchange settlement / reserve cutover | `round3-hold-*.md`, `round3-exchange-*.md` |
| C | `rollout-2026-10-15T07-00-30-*.jsonl` | DEX: Ethereum swap recovery + allowance cleanup, Jupiter/Tinyman, contracts | `round3-dex-*.md`, `round3-ethereum-*.md`, `round3-jupiter-*.md` |

Reconstruct a lane's last steps without reading the raw JSONL:
`node .omc/research/claude-resume/codex-tail.mjs <rollout.jsonl> 25` (last 25 progress messages).
Per-lane file edit history and cross-lane overlaps: `.omc/research/claude-resume/codex-files.txt`.

## Rules every lane keeps

1. **Goal and deferral bar are unchanged** — see `plan.md` §Four-round loop and `deferred.md`.
   Implementation size is never a deferral. Work that is real but belongs to a later round is
   recorded as *round-4 carry-forward*, not as a deferral.
2. **No build, test, lint, typecheck or schema generation.** The owner's policy is one integrated
   sweep after round 4. Static self-review is expected; executed verification is not claimed.
3. **Generated SurrealQL is never hand-edited.** POCO edits are allowed; `.surql` regeneration
   runs once in the final sweep.
4. **Repo conventions** (root `AGENTS.md`): typed Surreal access first, every retained raw call
   carries `// raw: <invariant or waiver>`; interfaces own XML contracts and implementations use
   `/// <inheritdoc/>`; no unused usings (IDE0005 is an error); expected outcomes are typed
   results, unexpected exceptions bubble; role-first layout; rationale goes in the directory
   `AGENTS.md`, not long code comments. Fixtures follow real flows through public seams.
5. **No git operations.** The orchestrator commits at each join.

## Shared-file protocol

Codex lanes edited 114 code files in common. To keep four concurrent lanes from colliding:

- **Additive-only hot files**: `sdk/azoa-wallet/src/api/client.ts`, `sdk/azoa-wallet/src/api/index.ts`,
  `sdk/azoa-wallet/src/index.ts`, `Program.cs`, `frontend/src/app/api/operator/[...path]/route.ts`.
  Re-read immediately before every edit, append inside your own domain's region, never reorder or
  reformat, keep each edit small.
- **Lane A owns** `Providers/Blockchain/Solana/SolanaProvider.cs`, `SolanaProvider.Durable.cs`
  (including `SignDurableAsync`) and `contracts/solana/wtt-guard/src/lib.rs`. Other lanes put new
  behavior in their own partial file; a needed change to an A-owned core goes in the lane report
  as a request. Lane C may make additive edits confined to its own guard-instruction handler in
  `lib.rs`, re-reading first.
- Any other file last edited by a different lane: re-read, keep the change minimal, and list it
  under "cross-lane edits" in the lane report.

## Lane V: verification harness (added 2026-10-16)

Owner decision 2026-10-16: add four test layers to this program — backend HTTP flow tests,
an MSW feature-slice suite, Vitest browser mode, and Playwright E2E on the `dev-up` stack.
Lane V builds the harness now, in parallel with round 3; round-4 feature lanes write their own
slice and E2E specs on it.

Lane V owns: `tests/AZOA.WebAPI.IntegrationTests/Http/**` (new), additive edits to
`tests/AZOA.WebAPI.IntegrationTests/Factories/AZOATestWebApplicationFactory.cs`,
`frontend/e2e/**`, `frontend/playwright.config.ts`, `frontend/src/test/msw/**`,
`frontend/src/test/slices/**`, `frontend/vitest.config.ts`, the test devDependencies in
`frontend/package.json` + `package-lock.json`, any `docker-compose.e2e.yml`, and new CI jobs in
`.github/workflows/ci.yml`. Lanes N/A/B/C do not edit those paths. Lane V does not edit feature
source; a product defect it finds goes in its report.

Lane V gets the harness-smoke exception to the end-sweep policy: it runs the frontend
harness once in this tree (Vitest transpiles without type-checking). Anything that needs the API
compiled (HTTP flows, full E2E) first runs in the integrated sweep, unless a disposable worktree
at `bb512cf` can prove the harness mechanics against existing endpoints.

## Lane deliverables

1. On start, write `round3-lane-<X>-resume.md` (`type: lane-checkpoint`): the finite round-3
   convergence checklist for the lane — done, in flight at cutoff, remaining.
2. Bring the in-flight slice and the remaining checklist items to source-complete, with fixtures.
3. Update the same record at the end: files changed, items closed, round-4 carry-forward with the
   reason each is not round-3 work, cross-lane edits, and requests to other lanes.

After all four lanes join: independent adversarial source review per lane (separate context,
prompted to refute), one batch of fixes, then `round3-closeout.md`, then round 4.
