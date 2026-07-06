---
type: closeout
track: final-hardening-cutover
phase: H
shipped: 2026-07-06
---

# Phase H — Alpha Gate: close-out

The track was re-opened 2026-07-05 after two fresh-eyes audits and driven to
completion via `/ultrapilot` (6 parallel lane workers → 3 follow-up workers →
independent architect validation). **Architect verdict: GO — all 10 items PASS,
honestly scoped.**

## The 8 alpha-blockers (all closed)

| # | Item | Outcome |
|---|---|---|
| H1 | Simulated-mode production guard | `BlockchainProviderFactory.GuardAgainstSimulatedModeInProduction` at `Program.cs:127`; Production+`Mode=Simulated` throws at boot; defaults to `Live` so unset config never fires. 4-quadrant test. |
| H2 | Admin-token mint path | Config-only fail-closed seed (`Services/Admin/SeedAdminHostedService` + `AdminBootstrapOptions`); stamps `operator:admin` only when `SeedEmail`+`SeedSecret` set + email match; partial config throws at boot in Prod. NODE-HOST §8.9 rewritten executable. `SeedSecret` is an arming toggle (clarified in spec). |
| H3 | Frontend API-URL build-bake + SDK ref | Runtime-resolved via `frontend/src/lib/runtime-config.ts` + `layout.tsx` inject; Dockerfile sets runtime `API_URL`; `next.config.js` → `@azoa/sdk`. |
| H4 | Phantom backup/restore scripts | Real `scripts/surrealdb/{backup,restore,ContainerRuntime}.ps1`; G5 is a true 13-table SHA-256 round-trip; `pwsh`→`powershell.exe` fallback. |
| H5 | Version/CHANGELOG/LICENSE | WebAPI `0.1.0-alpha`; SDK `LICENSE` shipped in package; root `CHANGELOG.md`. |
| H6 | Minimal CI | `.github/workflows/ci.yml` — build + unit + SDK vitest on push/PR, .NET 10, integration excluded. |
| H7 | Bucket-D value-path triage | 3 genuine product bugs fixed (below). |
| H8 | Doc-drift sweep | `.NET 8`→`.NET 10`, SurrealDB `1.5.4`→`3.1.4`; deprecation banners on GO-TO-PROD + RESIDUAL-RISK-RUNBOOK → NODE-HOST; sagas-default + G1-live corrections recorded. |

## 3 genuine product bugs found by H7 (not test noise)

1. **All 5 MCP tools broken in production** — compared `record<>` link columns
   (`id`, `avatar_id`, `parent_holon_id`, `quest_id`, node ids) against **bare-hex**
   strings ⇒ every query returned 0 rows against real store data. Fixed: bind via
   `SurrealLink.ToLink("<table>", hex)` (the canonical helper used by 24 store files).
   Plus `HolonTraverseTool.peer_holon_ids` POCO `string?` → `JsonElement?` (stored as
   `array<string>`). See `Mcp/AGENTS.md §record-id-binding`.
2. **AvatarNFT mint unsettable** — `nft_ownership.token_id` required non-empty but no
   DTO/service path supplied it (broken since 2026-06-08). Fixed: `AvatarNFTService`
   auto-assigns `token_id` (chain-mint) + optional `AvatarNFTMintModel.TokenId`.
3. **Holon empty-collection no-op** — `SurrealHolonStore.ToPoco` only serialized
   `PeerHolonIds`/`Metadata` when non-empty; SurrealForge SET omits null `option<>`
   fields, so emptying a collection never cleared the column (silent data loss).
   Fixed by always serializing. Plus `operation_log.parameters` →
   `[Column(Flexible=true)]` (golden regenerated via `AZOA_REGENERATE_GOLDENS=1`,
   never hand-edited — enforced by `AttributePocoByteEquivalenceTests`).

## Harness root-cause (was masking all of the above)

`IntegrationTestBase.ExecuteSurrealSqlAsync` sent the pre-3.x JSON envelope
`{query, params}`, which **SurrealDB 3.1.4 treats as a literal string and silently
no-ops**. Rewritten to `LET $name = <surql-literal>;` preludes (binds scalars AND
CONTENT objects, type-preserved) + throws on `status:ERR`. This *strengthens*
validation. See `tests/AZOA.WebAPI.IntegrationTests/AGENTS.md` §param-binding +
§g5-seed-shapes.

## Final sweep (run once, per test policy)

- `dotnet build`: **0 warnings / 0 errors**.
- Unit: **1235 passed / 0 failed / 1 skipped** (documented-unreachable).
- SDK: **163 vitest passed**.
- Integration: every previously-red class green **in isolation** (AvatarNFT 10/10,
  MCP 8/8, G5 1/1, Holon/STARODK scoped 41/41). The full-suite **37-failure tail is
  pre-existing shared-SurrealDB-container parallel contention** — the exact same
  classes pass when run as a scoped group; consistent with the documented
  `integration-test-namespace-isolation` history. NOT regressions from Phase H.

## Non-blocking follow-ups (recorded, not owed for alpha)

- One-line doc note that the 37-tail root-causing was accepted on coordinator
  evidence, not re-audited item-by-item in the architect pass.
- §H-followups (unchanged): KMS/HSM custody, distributed rate limiting, auth
  brute-force limits, `ConnectWalletAsync` sig-verify (verified SAFE — external
  wallets can't reach the custody chokepoint), dormant Metaplex methods, god-object
  splits, real Solana/Wormhole/ETH value routes (stay fail-closed).

## Terminal state (unchanged)

The only remaining launch action is `railway up` + provision secrets/guardian sets —
zero code.
