---
type: passoff
track: final-hardening-cutover
phase: H
audience: fable-deep-review (fresh session, no prior context)
created: 2026-07-06
---

# Phase H — Deep-Review Pass-off (for a fresh Fable session)

**You are reviewing, from a cold start, the work done in Phase H ("Alpha Gate") of
the `final-hardening-cutover` track of the Azoa (formerly OASIS/oasis-sleek) repo.**
This document is self-contained: it gives you the project shape, exactly what
changed and why, the risk map, what is *verified* vs *assumed*, and the specific
open findings a first review pass already surfaced. Your job is a **deep,
independent, skeptical** review — do NOT trust the claims below; verify them against
the tree and push on the soft spots this doc flags.

---

## 0. Orientation — what this repo is

- **Azoa**: a .NET 10 WebAPI backend (`AZOA.WebAPI.csproj`, `net10.0`) with a
  Controllers→Managers→Services→Stores→**SurrealDB** spine. SurrealDB (v**3.1.4**)
  is the sole storage engine (EF/Postgres/InMemory all removed).
- **TS SDK** at `sdk/azoa-wallet/` (npm `@azoa/sdk`), **Next.js 14 frontend** at
  `frontend/`.
- Pre-launch, greenfield, no live data, no compat shims. The whole track's premise:
  after it ships, the only remaining launch action is `railway up` + provisioning
  secrets/guardian sets — **zero code**.
- **Blockchain honesty posture**: Algorand real bridge value is LIVE;
  Solana/Wormhole/Ethereum value routes are **fail-closed and disabled**
  (`RealValueEnabled=false`). Simulated mode (`Blockchain:Mode=Simulated`) gives
  deterministic `sim:tx:` settlement for dev/test.

Key conventions you must apply when judging the code:
- **Directory-level docs over verbose inline comments.** Terse one-line
  doc-comments in code; the "why" belongs in a same-directory `AGENTS.md`. A
  multi-paragraph inline comment block is a *finding*, not good style.
- **Run tests once at the end**, never per-fix. **Never run the frontend typecheck**
  (`tsc`) — it's pre-existing noise (`frontend/` has unrelated type errors).
- Zero new compiler warnings vs a **28-warning pre-existing baseline**.
- SurrealDB schema is **C#-first**: decorated POCOs in
  `Persistence/SurrealDb/Models/` are the source of truth; `.surql` goldens under
  `Persistence/SurrealDb/Generated/Schemas/` are emitted and byte-equivalence-gated
  by `tests/AZOA.WebAPI.Tests/Persistence/SurrealDb/AttributePocoByteEquivalenceTests.cs`
  — **never hand-edit a golden**; regenerate via `AZOA_REGENERATE_GOLDENS=1`.

---

## 1. Why Phase H exists

The track had already shipped (Phases A–G). Two fresh-eyes audits then found the
"only railway up remains" claim was *happy-path* true but hid the operator
lifecycle + one un-guarded dangerous default. Phase H closes **8 alpha-blockers**
(H1–H8). Full spec: `conductor/tracks/final-hardening-cutover/spec.md` → "Phase H"
section (acceptance criteria H-AC1..H-AC10). Close-out narrative:
`CLOSEOUT-PHASE-H.md` in the same dir.

It was executed via a parallel-agent orchestration (`/ultrapilot`), then an
architect validation pass returned **GO, all 10 items PASS**. A subsequent
`/code-styleguides` swarm review (5 lanes) returned clean-to-minor. **You are the
next, deeper pass** — the architect pass explicitly did NOT re-audit the 37-failure
integration tail item-by-item, and the styleguide pass was conformance-only. Treat
both as "already checked the obvious" and go after what they couldn't.

---

## 2. Exactly what changed (the review surface)

30 source files. Full manifest lives in this session's `/tmp/phaseh_files.txt`; the
load-bearing ones, grouped:

### 2a. The 8 blockers
| Blocker | Files | Claim to verify |
|---|---|---|
| H1 simulated-mode prod guard | `Providers/Blockchain/BlockchainProviderFactory.cs`, `Program.cs:~127` | Production + `Mode=Simulated` throws at boot; Dev/IntegrationTest allowed; defaults to `Live` when unset so it can't over-fire. Test: `tests/AZOA.WebAPI.Tests/Core/BlockchainProviderFactorySelectionTests.cs` |
| H2 admin-token mint | `Managers/AvatarManager.cs` (GenerateJwt/StampOperatorAdminIfSeeded), `Services/Admin/SeedAdminHostedService.cs`, `Services/Admin/AdminBootstrapOptions.cs`, `docs/NODE-HOST.md §8.9` | Config-only fail-closed seed; stamps `operator:admin` only when `AdminBootstrap:SeedEmail`+`SeedSecret` both set + email matches; partial config throws at boot in Prod AND at mint (defense-in-depth). `SeedSecret` is an **arming toggle**, not a presented challenge — verify no path grants admin without it configured. Tests: `AdminBootstrapTests.cs` |
| H3 frontend API-URL | `frontend/Dockerfile`, `frontend/next.config.js`, `frontend/src/lib/runtime-config.ts`, `frontend/src/app/layout.tsx`, `frontend/src/lib/azoa.ts`, `api.ts` | API URL resolved at RUNTIME (plain `API_URL` env read server-side, injected as `window.__RUNTIME_CONFIG__`), not baked at `next build`. `transpilePackages: ['@azoa/sdk']` (was `@azoa/wallet-sdk`). |
| H4 backup/restore | `scripts/surrealdb/{backup,restore,ContainerRuntime}.ps1`, `tests/.../Gates/G5_RestoreDrillTest.cs` | Real `surreal export`/`import` via docker\|podman exec (`Find-ContainerRuntime`); G5 is a genuine 13-table SHA-256 round-trip; `RunPwsh` falls back `pwsh`→`powershell.exe`. |
| H5 version/CHANGELOG | `AZOA.WebAPI.csproj` (Version 0.1.0-alpha), `sdk/azoa-wallet/package.json`+`LICENSE`, root `CHANGELOG.md` | — |
| H6 CI | `.github/workflows/ci.yml` | build + unit + SDK vitest on push/PR, .NET 10, integration excluded. |
| H7 value-path triage | see §2b | 3 product bugs fixed. |
| H8 doc-drift | `docs/GO-TO-PROD.md`, `docs/RESIDUAL-RISK-RUNBOOK.md`, `docs/NODE-HOST.md`, `README.md`, `RUNBOOK.md` | No current-state `.NET 8` or SurrealDB `1.5.4`; deprecation banners → NODE-HOST; sagas-default + G1-live corrections recorded. |

### 2b. The 3 GENUINE product bugs (H7 — the highest-value part to re-verify)
These were found because a silent test-harness bug had been *masking* them.
1. **All 5 MCP tools broken in production.** `Mcp/Tools/{HolonTraverse,QuestReachability,NftOwnershipGraph,AvatarScopedQuery,VectorSearch}Tool.cs`
   compared `record<>` link columns (`id`, `avatar_id`, `parent_holon_id`,
   `quest_id`, node ids) against **bare-hex** strings ⇒ every query returned 0 rows
   against real store data. Fix: bind `SurrealLink.ToLink("<table>", SurrealId.ToSurrealId(guid))`.
   Also `HolonTraverseTool.peer_holon_ids` POCO `string?` → `JsonElement?` (stored
   `array<string>`). Rationale in `Mcp/AGENTS.md §record-id-binding`.
   **⚠ Verify**: that this is a real query-layer fix and not a test-only mask; that
   ALL 5 tools now bind consistently (one — VectorSearchTool — was found bypassing
   the shared `SurrealId.ToSurrealId` helper and was aligned late; confirm).
2. **AvatarNFT mint could never settle.** `nft_ownership.token_id` is required
   non-empty but no DTO/service path supplied it (broken since 2026-06-08). Fix:
   `Services/Avatar/AvatarNFTService.cs` auto-assigns `token_id` (chain-mint
   semantics) when the caller supplies none; `Models/Requests/AvatarNFTMintModel.cs`
   gained optional `TokenId`. **⚠ Verify** the auto-gen is appropriate (is a random
   `Guid` token id semantically right, or should mint require a caller-supplied /
   provider-derived id? This is a product-semantics call worth your scrutiny).
3. **Holon empty-collection edits were silent no-ops.** `Providers/Stores/Surreal/SurrealHolonStore.cs`
   `ToPoco` only serialized `PeerHolonIds`/`Metadata` when non-empty; SurrealForge's
   SET-based upsert omits null `option<>` fields, so emptying a collection never
   cleared the stored column (data-integrity bug). Fix: always serialize (empty →
   `[]`/`{}`). Plus `Persistence/SurrealDb/Models/OperationLog.cs` `Parameters` →
   `[Column(Flexible=true)]` (golden `operation_log.surql` regenerated).
   **⚠ Verify** there aren't OTHER stores with the same SET-omits-null latent bug on
   `option<>` collection columns — this was found in Holon; the same pattern could
   exist in Wallet/Quest/STAR stores. **This is the single most valuable thing you
   can do in this review.**

### 2c. The harness root-cause (test infra, but load-bearing)
`tests/AZOA.WebAPI.IntegrationTests/IntegrationTestBase.cs` — `ExecuteSurrealSqlAsync`
sent the pre-3.x JSON envelope `{query, params}`, which **SurrealDB 3.1.4 treats as
a literal string and silently no-ops** (params echoed, query never runs). Verified
against the live engine. Rewritten to `LET $name = <surql-literal>;` preludes
(binds scalars AND CONTENT objects, type-preserved) + throws on `status:ERR`.
Helpers: `BuildParamLets`, `ToSurqlLiteral`. Docs: test-dir `AGENTS.md`
§param-binding + §g5-seed-shapes. **⚠ Verify** `ToSurqlLiteral` covers the CLR types
callers actually pass (it was just hardened for InvariantCulture numerics +
DateTimeOffset/Guid arms; confirm no gap remains) and that the substring ERR-scan
(`"\"status\":\"ERR\""`) can't false-positive on an ERR token embedded in a
legitimate result payload.

---

## 3. What is VERIFIED vs ASSUMED (be skeptical of the assumed column)

| Claim | Evidence strength |
|---|---|
| `dotnet build` 0 err, no new warnings | **Verified** this session (full build after each change). |
| Unit suite 1235 pass / 0 fail / 1 skip | **Verified** (ran clean). |
| SDK 163 vitest pass | **Verified**. |
| Each previously-red integration class green (AvatarNFT 10/10, MCP 8/8, G5 1/1, Holon/STARODK 41/41) | **Verified in isolation** (ran each class/scoped group). |
| Full integration suite still shows 37 failures | **Verified count**; the *attribution* ("pre-existing shared-SurrealDB-container parallel contention, not regressions") is **partially assumed** — supported by: the exact same classes pass when run scoped, and it matches the documented `integration-test-namespace-isolation` history in project memory. **NOT** independently re-audited failure-by-failure. **⚠ This is the biggest soft spot. If you do one deep thing, pick apart a handful of those 37 and confirm they are genuinely contention, not a real regression hiding in the crowd.** |
| The 3 product bugs are real & fixed | **Verified** (reproduced against live SurrealDB 3.1.4, fix confirmed green). |
| MCP tools were broken in prod (not just tests) | **Verified** by the fixing worker against the real `DefaultSurrealExecutor` (bare-hex → 0 rows, link form → 1 row). Re-confirm the production call path actually uses the fixed binding. |

---

## 4. Open findings from the styleguide swarm (already triaged, your call on the rest)

Reports live in `.omc/reviews/lane{1..5}-*.md`. Severity ceiling was **HIGH** (no
critical/major). What was **already fixed** this session:
- `ToSurqlLiteral` InvariantCulture for numerics + `DateTimeOffset`/`Guid` arms
  (IntegrationTestBase.cs).
- `VectorSearchTool` avatar binding aligned to the shared `SurrealId.ToSurrealId`
  helper (lane-2 HIGH H1).
- 4 line-number self-references stripped.

**Deliberately deferred (candidates for your review to confirm or escalate):**
- **Doc-comment relocation**: verbose `<summary>`/inline blocks in
  `SeedAdminHostedService`, `AdminBootstrapOptions`, `BlockchainProviderFactory`,
  `AvatarManager`, and the 5 MCP tools duplicate rationale that per-convention
  belongs in a directory `AGENTS.md` (`Services/Admin/AGENTS.md` and
  `Providers/Stores/Surreal/AGENTS.md` don't exist yet). Low-risk but a real
  convention drift.
- **`layout.tsx:32`** — the `window.__RUNTIME_CONFIG__` inline script uses
  `JSON.stringify` which does NOT escape `</script>` or U+2028/2029. Source is
  env-only (not attacker-controllable) so **not exploitable today**, but flagged as
  defense-in-depth. Judge whether to harden.
- **`G5_RestoreDrillTest.MakeHex32`** is a naming-lie (identical to `MakeHex64`,
  emits 64 chars). Cosmetic; caller wants 64.
- Minor test nits: brittle ERR substring scan; double `SelectAllRowsAsync`
  round-trips; a dead `new Guid[3]` initializer in `McpVectorSearchTest.cs`.

---

## 5. Where to point your deepest scrutiny (ranked)

1. **SET-omits-null across OTHER stores.** The Holon empty-collection data-loss bug
   (§2b.3) is a *pattern*. Grep every `Surreal*Store.cs` `ToPoco`/upsert for
   `option<>` collection columns (`array<...>`, `option<object>`) that are only
   serialized when non-empty. If Wallet/Quest/STAR/NFT stores share it, emptying a
   collection silently fails there too. **Highest expected value.**
2. **The 37-failure integration tail.** Independently reproduce a sample; confirm
   contention (namespace/scheme collisions, shared-container races) vs a masked
   real regression. The attribution is the least-verified claim in the whole phase.
3. **AvatarNFT `token_id` auto-gen semantics.** Is minting a random Guid token id
   the right product behavior, or a papering-over of a missing real mint pipeline?
4. **MCP tool binding, production path.** Confirm the tools are actually reachable
   and the fixed binding is on the live query path (not only exercised by tests).
5. **Admin seed threat model.** `SeedSecret` as an arming toggle: trace every way a
   principal could end up with `operator:admin`. Confirm fail-closed at boot AND at
   mint under hot-reloaded partial config in Production.

---

## 6. How to run things (so you can verify, not just read)

- Build: `dotnet build AZOA.WebAPI.csproj` (expect 0 err; ~17 warnings = pre-existing
  baseline, none new).
- Unit: `dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj`.
- Integration (needs live SurrealDB 3.1.4 on `127.0.0.1:8000`, root/root — bring the
  container up per `RUNBOOK.md`/`scripts`): scope with
  `--filter "FullyQualifiedName~<Class>"`. **Do NOT** judge the full-suite 37-tail
  without scoping — that's the contention artifact.
- SDK: `cd sdk/azoa-wallet && npm test`.
- **Do NOT run the frontend typecheck.** Review frontend by reading only.
- The live SurrealDB HTTP `/sql` contract that bit the harness (for your own probes):
  SurrealQL in a `text/plain` body; params via `LET $x = <literal>;` prelude or
  query-string (scalars only); headers `Surreal-NS`/`Surreal-DB` (NOT legacy
  `NS`/`DB`).

---

## 7. Deliverable expected from your pass

A ranked findings report (critical→nit) with file:line + concrete repro or
suggested fix, explicitly stating for the top-5 scrutiny items above whether you
**confirmed** the claim, **refuted** it, or **couldn't determine** it and why. Flag
anything that would block an alpha tag. Do not rewrite product code without calling
out the change; test/doc trivial nits may be applied inline.
