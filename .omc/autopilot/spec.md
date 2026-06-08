# Autopilot Spec — Live-Suite Stabilization

**Date**: 2026-06-07
**Idea**: Drive OASIS WebAPI integration tests (backend OASIS.WebAPI.LiveTests + frontend Frontend.jsonl) to a passing state for confident real-world local-dev use. Plus build the local JSONL exception logger the user requested for diagnosing the wave of SurrealDB issues we anticipate.

---

# Part I — Requirements Analysis (from Analyst)

## Functional Requirements
- **FR-1** Stable auth chain end-to-end (register → login → capture JWT → Authorization: Bearer in all downstream calls).
- **FR-2** Rate-limit headroom — full 806-case suite must finish without self-rate-limiting.
- **FR-3** Auth scheme works for both JWT and ApiKey (MultiScheme policy).
- **FR-4** SurrealDB schema/shape conformance per domain (Avatar, Holon, Wallet, BlockchainOperation, ODK, NFT all at parity with Avatar happy path).
- **FR-5** Chained-CRUD continuity across `{{ns.var}}` extraction.
- **FR-6** Local exception logger — gitignored JSONL, dev-only, capture SurrealQL errors + 5xx + unhandled exceptions, full inner-chain + offending statement.
- **FR-7** Suite isolation (parallel-by-suite execution preserved).
- **FR-8** Frontend.jsonl 9/9 must not regress (regression gate).

## Non-Functional Requirements
- **NFR-1** Full suite ≤ 90s wall-clock.
- **NFR-2** Deterministic — per-run namespace wipe in Surreal before harness.
- **NFR-3** Every failure has captured server-side context (exception log + response body in markdown).
- **NFR-4** Confidence metric: happy-path 100% / defensive ≥95% / stress 100% under dev config.
- **NFR-5** No production observability (local file + console only).
- **NFR-6** Greenfield — breaking changes OK, no compat shims.

## Implicit Requirements
- **IR-1** NO green-by-suppression. No `expectedStatus` relaxation; no test deletions.
- **IR-2** Bugs in API, fixes NEVER in test data. (Exception: `extract` path corrections are harness misconfiguration, not relaxation.)
- **IR-3** SurrealDB pivot must apply to ALL stores, not just Avatar.
- **IR-4** Frontend.jsonl is the regression gate — any break = immediate rollback.
- **IR-5** Blockchain_Devnet failures (24/30) are real bugs, not env config.
- **IR-6** SurrealDB diagnostics first-class via the exception logger.
- **IR-7** Gitignored, dev-only, redaction allow-list for secrets.

## Out of Scope
- Production observability (OTel/Seq/AppInsights).
- CI integration.
- Performance beyond suite budget.
- Refactors not driven by failing tests.
- Frontend tsc per `[[no-frontend-typecheck]]`.
- New test suites beyond what exists (Frontend.jsonl is the only new one).
- Compat shims per `[[greenfield-prelaunch-no-compat]]`.
- Auth scheme changes.
- Bridge / wormhole fixes (Tier 0).

## Failure Categorization (611 failures across 14 suites)
| Bucket | Count | % |
|---|---|---|
| 429 rate-limit | ~245 | 40% |
| 401 auth-not-set-up | ~155 | 25% |
| 404 chained-CRUD breakage | ~140 | 23% |
| 400 validation | ~60 | 10% |
| 5xx / exceptions | ~5 | 1% |
| Other | ~6 | 1% |

## Key Risks
- **R-1** Test pollution between runs → mitigation: per-run namespace wipe.
- **R-2** Cross-suite ID collisions → mitigation: suite-scoped identity prefix.
- **R-3** Malicious/QA suites EXPECT 4xx — do not "fix" by relaxing API.
- **R-4** SurrealDB v3 adapter drift across stores — audit every store.
- **R-5** Rate-limit relaxation leaking to security posture → dev multiplier (not disable), gated on `IsDevelopment()`.
- **R-6** Exception logger as side-channel for secrets → redaction key list.
- **R-7** Harness extract-path fragility — silent extraction misses produce 404 cascades. Audit every extract.
- **R-8** Frontend.jsonl regression = deal-breaker.
- **R-9** Confidence without fixed target = unbounded scope → NFR-4 above.
- **R-10** Scope creep into API redesign — triage drives scope.

---

# Part II — Technical Specification (from Architect)

## 1. Tech Stack with Rationale
| Layer | Choice | Rationale |
|---|---|---|
| Runtime | .NET 8 WebAPI + Next.js 14 (no change) | Persisting per scope |
| Persistence | SurrealDB v3 via homebake `Oasis.SurrealDb.Client` | Recently pivoted; AvatarStore is reference |
| Exception logger | `ILoggerProvider` + `System.Text.Json` (stdlib only) | IR-7 redaction trivial in pure JSON; no Serilog/NLog per greenfield-no-compat |
| Suite isolation | **Suite-scoped identity prefix**, NOT serialization | Serializing kills NFR-1; per-suite `{{suitePrefix}}` removes R-2 collisions |
| Rate-limit posture | **`RateLimiting:DevMultiplier` knob**, limiter stays ON | Honors R-5; multiplier gated on `IsDevelopment()` |
| DB reset | `oasis-surreal reset` subcommand on existing Schema CLI | Avoids hardcoding namespace strings in `dev-up.ps1` |

## 2. Architecture Overview

### A. Exception Logger
- Location: `Core/Diagnostics/` under `OASIS.WebAPI/`
- Types: `JsonlExceptionLoggerProvider : ILoggerProvider`, `JsonlExceptionLogger : ILogger`, `JsonlExceptionWriter` (bounded `Channel<JsonlEntry>` capacity 1024, drop-oldest), `JsonlEntry` (record), `JsonlExceptionLoggerOptions`
- File layout: `logs/exceptions/YYYY-MM-DD.jsonl`, UTC date in filename, rotates by next-entry check
- JSONL schema includes: `ts`, `level`, `category`, `eventId`, `message`, `exceptionType`, `exceptionMessage`, `stack`, `innerChain` (array), `requestId`, `requestMethod`, `requestPath`, `statusCode`, optional `surrealStatement`, optional `surrealParams` (redacted)
- Redaction deny-list: `password`, `passwordhash`, `apikey`, `x-api-key`, `authorization`, `token`, `secret`, `mnemonic`, `privatekey` (substring match; replacement `[REDACTED]`)
- DI: `builder.AddJsonlExceptionLogging()` extension; **registered only when `IsDevelopment()`**
- Middleware: `Core/Diagnostics/JsonlExceptionMiddleware.cs` runs INSIDE `DebugExceptionMiddleware`; captures pipeline failures including 401/429/5xx; does NOT alter responses (observer-only)
- Surreal-aware capture: executors propagate `Statement` + `Params` via `Exception.Data["SurrealStatement"]` / `Data["SurrealParams"]` so middleware can promote them into the JSONL row

### B. Rate-Limit Dev Config
- `appsettings.Development.json` gains `RateLimiting:DevMultiplier: 100`
- `Program.cs:134-141`: one-line multiplier read + `IsDevelopment()`-gated; non-Dev forces multiplier=1
- Stress_RapidOperations.jsonl rescaled if needed so it still trips the limiter under the dev multiplier

### C. Auth-Chain Audit
- `AvatarController.Login` returns `OASISResult<string>` with JWT in `Result` → harness extract `{"token":"result"}` is CORRECT (verified)
- 401 wave root cause: extraction silently fails when the seed `register_avatar` step in a parallel suite collides (R-2). Fix: per-suite identity prefix.
- Harness change: `HttpTestClient.Substitute` logs a warning when `{{token}}` pattern remains unsubstituted; that case marked `Inconclusive` not `Failed`
- `[Authorize]` audit produces `docs/auth-audit.md`; controller stays authoritative; suite Authorization headers added where missing
- JWT lifespan 24h — dwarfs 90s budget; no change

### D. SurrealDB Store Audit
- Method per store: verify (1) UPSERT + RETURN AFTER pattern, (2) `EnsureAllOk()`, (3) `ISurrealRecord` impl, (4) `Guid.ParseExact(id, "N")` without manual strip on the `id` property
- Quest stores: `StripIdPrefix` MUST stay for FK columns (`quest_id`, `avatar_id`, etc.) — package converter only narrows scope to `id`. Removable only from `FromSurrealId` for the `id` property.
- 13 stores to audit; SurrealAvatarStore is reference; SurrealBridgeStore is out-of-scope (Tier 0)
- Quest suites are out-of-scope for this sweep (no JSONL suite hits them)

### E. Harness Hardening
- New `suiteVars` block at file head of each `.jsonl` (parsed by `JsonlTestParser`; optional, backward-compat)
- Per-suite prefix = suite filename stem lowercased; merged into substitution context before first case
- `extract`-path audit: mechanical walk through 14 jsonl files; correct paths to actual controller response shape; IR-1 enforced — no `expectedStatus` reductions
- Frontend.jsonl: ZERO edits (FR-8 / regression gate)

## 3. File Structure

### CREATED
- `Core/Diagnostics/JsonlExceptionLoggerProvider.cs`
- `Core/Diagnostics/JsonlExceptionLogger.cs`
- `Core/Diagnostics/JsonlExceptionWriter.cs`
- `Core/Diagnostics/JsonlEntry.cs`
- `Core/Diagnostics/JsonlExceptionLoggerOptions.cs`
- `Core/Diagnostics/JsonlExceptionMiddleware.cs`
- `Core/Diagnostics/RedactionFilter.cs`
- `Extensions/DiagnosticsExtensions.cs`
- `packages/Oasis.SurrealDb.Schema/Cli/ResetCommand.cs`
- `docs/auth-audit.md`

### MODIFIED
- `Program.cs` — `AddJsonlExceptionLogging`, RateLimiting:DevMultiplier, `UseMiddleware<JsonlExceptionMiddleware>()`
- `appsettings.Development.json` — RateLimiting:DevMultiplier + Diagnostics:JsonlExceptionLogger
- `.gitignore` — append `logs/exceptions/`
- `tests/OASIS.WebAPI.LiveTests/HttpTestClient.cs` — Inconclusive on unsubstituted tokens
- `tests/OASIS.WebAPI.LiveTests/Parsers/JsonlTestParser.cs` — `suiteVars` block
- `tests/OASIS.WebAPI.LiveTests/TestHarness.cs` — prime per-suite context
- 12 of 14 `live-tests/*.jsonl` — adopt `{{suitePrefix}}` + corrected extracts (Frontend.jsonl + Stress_RapidOperations.jsonl untouched)
- Non-Avatar `Providers/Stores/Surreal/Surreal*Store.cs` — parity sweep where audit finds drift
- `packages/Oasis.SurrealDb.Schema/Program.cs` — wire `reset` verb
- `dev-up.ps1` — invoke `oasis-surreal reset` before API start

### DELETED
- `StripIdPrefix` call sites in Quest stores for the `id` property only (helper + FK call sites preserved)

### LEFT ALONE (R-10 discipline)
- `Core/DebugExceptionMiddleware.cs` (response-shaping owner)
- All controllers (auth shape verified)
- `Models/Responses/OASISResult.cs`
- Bridge/wormhole code (Tier 0)
- `frontend/`
- `tests/OASIS.WebAPI.Tests/` and `tests/OASIS.WebAPI.IntegrationTests/` unless an audit fix touches them
- `Persistence/SurrealDb/Schemas/source/*.mermaid`

## 4. Dependencies
- **No new NuGet packages.** stdlib only.
- **No new dotnet tools.**

## 5. API / Interfaces
- `WebApplicationBuilder.AddJsonlExceptionLogging()` extension
- `JsonlExceptionLoggerOptions` POCO (Enabled, Directory, RedactionKeys, MaxEntrySizeBytes, MinimumLevel)
- **No new endpoints.** `/admin/reset-db` rejected — reset is CLI-only.
- **No breaking controller changes.**
- Harness JSONL format: additive (`suiteVars` optional at file head).

## 6. Execution Order (Phase 2)
```
Wave 1 (2-wide parallel):
  A1 — Exception logger + middleware
  B1 — Rate-limit dev multiplier + Stress re-tune audit

Wave 2 (12-wide parallel):
  D1..D11 — eleven small executors, one per non-Avatar SurrealDB store
  E1     — Harness extract-path audit + suiteVars schema parser

Wave 3 (2-wide parallel):
  C1 — Auth-chain audit (needs logger + harness changes)
  E2 — 12-file jsonl edit pass (suitePrefix + corrected extracts)

Wave 4 (serial):
  F1 — 4xx triage; full sweep; map remaining failures; fix forward
  V1 — verifier (DIFFERENT executor instance) — re-run, confirm acceptance
```

## 7. Acceptance Criteria

1. `dotnet test OASIS.WebAPI.sln`: 100% pass; no `[Skip]` additions; no test relaxation.
2. After `dev-up.ps1` + `oasis-surreal reset`, `dotnet run --project tests/OASIS.WebAPI.LiveTests`:
   - Frontend.jsonl: **9/9**
   - AvatarController/HolonController/BlockchainOperationController/STARODKController: **100%**
   - E2E-Flows / CrossController_E2E / Blockchain_Devnet: **100%**
   - `_Malicious` suites combined: **≥95%**
   - `_QA` suites combined: **≥95%**
   - Stress_RapidOperations: **100%** (under dev multiplier)
3. Wall-clock budget: **≤ 90 seconds**
4. `logs/exceptions/YYYY-MM-DD.jsonl` exists, populated, no secret values (grep verifies redaction)
5. `git check-ignore logs/exceptions/<file>` returns the path
6. No new NuGet package in `OASIS.WebAPI.csproj`
7. **Zero `expectedStatus` reductions** in the jsonl diff (mechanical check)
8. **Zero compat shims** — no new `*Adapter*`, `*Compat*`, `*Legacy*` files outside fixtures

Verifier separation enforced: V1 is a different executor than F1.
