---
type: plan
track: final-hardening-cutover
phase: H
created: 2026-07-05
---

# Phase H — Alpha Gate: execution plan

8 alpha-blockers from two fresh-eyes audits. Partitioned into **disjoint
file-ownership lanes** so up-to-5 parallel workers never touch the same file.
H7 and H4 are the two real engineering pieces; the rest are mechanical.

Ground rules (from CLAUDE.md + project memory):
- **Run tests ONCE at the very end** — apply ALL fixes, then a single
  `dotnet build` + full unit + integration sweep. Do NOT test after each fix.
- **Do NOT run frontend typecheck** ([[no-frontend-typecheck]]) — it's pre-existing
  noise. SDK `tsc` + `dotnet build` only.
- Directory-level docs over verbose comments; terse one-line doc-comments in code.
- Zero NEW warnings vs the 28-warning baseline ([[build-warning-baseline-2026-06-16]]).
- Greenfield, no compat shims ([[greenfield-prelaunch-no-compat]]).

## Lane partition (file ownership — no overlaps)

### Lane 1 — H1 Simulated-mode prod guard (backend)  ·  size S
Owns: `Providers/Blockchain/BlockchainProviderFactory.cs`, `Program.cs` (boot-guard
block only — coordinate the single insertion point), a new test under
`tests/AZOA.WebAPI.*Tests/`.
- Add `IsProduction() && IsSimulatedMode ⇒ throw` fail-fast. Prefer the guard at
  boot (Program.cs, next to the existing secret/CORS/durability guards ~:104-122,
  :818-838, :910-945) so it refuses to start, mirroring the established pattern —
  rather than throwing lazily on first `GetProvider`. If the factory needs
  `IHostEnvironment`, inject it.
- Test: Production env + `Blockchain:Mode=Simulated` ⇒ throws; Dev/IntegrationTest
  ⇒ allowed. Both directions.
- Acceptance: H-AC1.

### Lane 2 — H2 Admin-token mint path (backend)  ·  size S–M
Owns: `Managers/AvatarManager.cs` (`GenerateJwt` + a seed/bootstrap method),
`Core/AzoaScopes.cs` (read-only ref — `operator:admin` at :33), appsettings admin
seed key, and whichever seam you pick (a `SeedAdminHostedService` OR a controller
endpoint OR a CLI entry). Do NOT touch Program.cs's provider-guard block (Lane 1).
- Provide a fail-closed bootstrap that stamps the `operator:admin` scope claim into
  a JWT. Env-driven seed secret; Dev/first-boot only; **throw/skip fail-closed if
  the seed secret is absent in Production**. Interim `role=Admin` claim keeps working.
- Make NODE-HOST §8.9 onboarding executable exactly as written — align doc wording
  with the real mechanism (this doc edit is H2's, not H8's).
- Acceptance: H-AC2.

### Lane 3 — H3 Frontend API-URL + stale SDK ref (frontend)  ·  size S
Owns: `frontend/Dockerfile`, `frontend/next.config.js`, any new runtime-config
shim (`frontend/src/lib/oasis.ts` / an entrypoint script / a `/config` route).
- Make `NEXT_PUBLIC_API_URL` resolvable at **runtime**, not baked at `next build`
  (runtime env via server component / entrypoint `envsubst` / server-side proxy —
  pick the least-invasive that works with the existing Next 14 app).
- Fix `next.config.js:26` `transpilePackages: ['@azoa/wallet-sdk']` → `@azoa/sdk`
  ([[sdk-renamed-oasis-sdk]]).
- **No frontend typecheck.** Acceptance: H-AC3.

### Lane 4 — H4 Real backup/restore scripts + G5 gate (infra)  ·  size M  ·  REAL WORK
Owns: NEW `scripts/surrealdb/backup.ps1`, NEW `scripts/surrealdb/restore.ps1`,
`tests/AZOA.WebAPI.IntegrationTests/Gates/G5_RestoreDrillTest.cs` (only if the gate
needs realigning to the real scripts).
- Write real scripts wrapping `surreal export` / `surreal import` via
  `docker exec` (preferred) / `podman exec`, auto-detected by a `Find-ContainerRuntime`
  helper (the surrealdb-migration SIGN-OFF describes the exact intended shape —
  mirror `start-test-container.ps1`'s detection). Log resolved runtime on startup.
- The G5 drill must be a true seed → backup → REMOVE NAMESPACE → restore → SHA-256
  round-trip. Verify the existing test passes against the new scripts.
- Only if real scripts prove infeasible this pass: honestly reduce the gate so it
  asserts nothing phantom (last resort — the point is a real recovery path).
- Acceptance: H-AC4.

### Lane 5 — H5 + H6 + H8 Version / CI / doc-drift (packaging + docs)  ·  size S each
Owns (all non-overlapping with lanes 1–4):
- H5: WebAPI `.csproj` `<Version>`/`<InformationalVersion>`, `sdk/azoa-wallet/package.json`
  `version`, NEW root `CHANGELOG.md`, verify/add `sdk/azoa-wallet/LICENSE`.
- H6: NEW `.github/workflows/ci.yml` — `dotnet build` (0 err) + unit tests + SDK
  `vitest`, on push + PR. Not a deploy pipeline.
- H8: doc-drift sweep across `docs/` + `RUNBOOK.md` + `README.md` + PASSOFF:
  `.NET 8`→`.NET 10`; SurrealDB `1.5.4`→`3.1.4`; deprecation banners atop
  `docs/GO-TO-PROD.md` and `docs/RESIDUAL-RISK-RUNBOOK.md` → `docs/NODE-HOST.md`;
  fix dead §7 ref; record the two audit corrections (sagas-default=true+boot-guard;
  G1 durability live/green on 3.1.4). **Do NOT edit NODE-HOST §8.9** (Lane 2 owns it).
- Acceptance: H-AC5, H-AC6, H-AC8.

### Lane 6 (serialized after 1–5, or a dedicated worker) — H7 Bucket-D triage  ·  size M  ·  REAL WORK
Owns: investigation across `Controllers/HolonController.cs`, `Managers/HolonManager.cs`,
`Controllers/STARODKController.cs`, `Managers/STARManager.cs`, and the failing
integration tests (`Holon.Interact`/`Mint`/`Exchange`, `STARODK.Deploy`). May edit
product code IF a real bug is found; otherwise records accepted-with-reason.
- Root-cause each failure. Fix genuine product bugs; for test-design/env issues,
  record written evidence and leave failing with a documented reason. No silent
  acceptance. Re-state the integration tail count.
- Also **note** (not fix) the `ConnectWalletAsync` signature-verification gap
  (`WalletManager.cs:406-411`): verify it cannot grant value-bearing capability
  outside the real WalletAuth flow; record the finding. The fix is a §H-followup.
- Acceptance: H-AC7.

## Final sweep (single pass, after ALL lanes land) — H-AC9, H-AC10
1. `dotnet build` — 0 errors, no new warnings vs 28-baseline.
2. Full unit suite + integration suite. Integration tail = only the H7-accounted
   failures, count re-stated.
3. SDK `npm run build` (tsup) + `vitest` green.
4. Confirm terminal state: only `railway up` + secret/guardian-set provisioning
   remain. Update CLOSEOUT.md, flip metadata `status` back to `shipped`, collapse
   this track's row in `tracks.md` with the Phase H summary.

## Conflict map (why lanes are safe to parallelize)
- Program.cs: **Lane 1 only.** Lane 2's admin seam is a new file/hosted-service, not
  a Program.cs edit — if DI registration is unavoidable, Lane 2 appends its own line
  and Lane 1 reviews the merge point.
- `next.config.js` / Dockerfile: **Lane 3 only.**
- `docs/NODE-HOST.md`: **Lane 2 owns §8.9**; Lane 5 (H8) touches every OTHER doc but
  not NODE-HOST §8.9.
- Product managers (Holon/STAR): **Lane 6 only.**
