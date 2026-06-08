# Autopilot Implementation Plan — Live-Suite Stabilization

**Source spec**: `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md`
**Date**: 2026-06-07
**Branch**: `api-safety-hardening`

This plan converts spec §6 (Execution Order) into per-task implementation contracts. Each
task is self-contained — a Phase-2 executor SHOULD be able to pick up its block and ship the
work without further interview. Paths are absolute Windows-style per `[[config-driven-calls]]`
and the harness convention. All four waves below honor the cross-cutting constraints from
spec §7 (`expectedStatus` is read-only; no compat shims; no new NuGet) and from user memory
(`[[greenfield-prelaunch-no-compat]]`, `[[no-frontend-typecheck]]`, `[[self-documenting-over-comments]]`).

---

## Wave 1 (2-wide parallel) — Foundation

### Task W1-A1: Exception Logger + Surreal-Aware Capture

- **Agent tier**: executor (sonnet) — multi-file but mechanical; spec §2.A is fully specified.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (lines 86–94 schema; 122–130 paths)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/DebugExceptionMiddleware.cs` (response-shaping owner — DO NOT replicate; observer-only)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Program.cs` (lines 510–595 — middleware pipeline ordering; `UseMiddleware<DebugExceptionMiddleware>` at 516, `UseRouting` at 575, `UseAuthentication` at 577)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Client/Connection/HttpSurrealConnection.cs` (lines 110–166: all rethrow sites — `throw new SurrealProtocolException` at lines 148, 166, 316, 324, 330)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Client/Query/DefaultSurrealExecutor.cs` (lines 33–92 — the wrappers that flow `query.Build()` + `query.Params` into the connection; this is where Statement+Params are known at the call site)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.Development.json` (existing keys)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.gitignore` (append target)
- **Files to CREATE**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlEntry.cs` — POCO/record matching spec §2.A schema (ts/level/category/eventId/message/exceptionType/exceptionMessage/stack/innerChain/requestId/requestMethod/requestPath/statusCode/surrealStatement/surrealParams). Use `record` with camelCase via `System.Text.Json.Serialization.JsonPropertyName` attributes. `surrealParams` is `Dictionary<string,object?>` post-redaction.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlExceptionLoggerOptions.cs` — `Enabled`, `Directory` (default `logs/exceptions`), `RedactionKeys` (defaults from spec §2.A redaction deny-list), `MaxEntrySizeBytes` (default 32_768), `MinimumLevel` (default `LogLevel.Warning`).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/RedactionFilter.cs` — pure helper: substring-match keys (case-insensitive) over a `Dictionary<string,object?>` and over JSON string content; returns redacted copy. Replacement literal: `"[REDACTED]"`.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlExceptionLogger.cs` — `ILogger` impl. `BeginScope` returns no-op disposable; `IsEnabled` filters at `Options.MinimumLevel`; `Log<TState>` builds a `JsonlEntry` and posts to the writer's channel (drop-oldest on overflow).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlExceptionWriter.cs` — singleton background worker holding a bounded `Channel<JsonlEntry>` (capacity 1024, `BoundedChannelFullMode.DropOldest`). One `Task` consumer that serializes via `System.Text.Json.JsonSerializer` (camelCase, ignore-null), writes to `Options.Directory/YYYY-MM-DD.jsonl` (UTC), rotates by re-deriving the date on every entry write. Hosted via `IHostedService` interface OR via `IAsyncDisposable` started on first use — pick `IHostedService` for clean shutdown via `Program.cs`.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlExceptionLoggerProvider.cs` — `ILoggerProvider` returning a shared `JsonlExceptionLogger`; receives the writer via constructor injection.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Diagnostics/JsonlExceptionMiddleware.cs` — pipeline observer. **CRITICAL idiom**: implement as `await _next(context); then inspect context.Response.StatusCode` (NOT try/catch only) — 401 from `UseAuthentication` and 429 from `UseRateLimiter` are NOT exceptions; they are status codes set downstream of this middleware. Pattern:
    ```csharp
    try {
      await _next(context);
      if (context.Response.StatusCode >= 400) { /* log status-based incident */ }
    } catch (Exception ex) {
      // log exception with ex.Data harvest, then re-throw so DebugExceptionMiddleware owns shaping
      throw;
    }
    ```
    On exception, reads `requestId` from `TraceIdentifier`, harvests `ex.Data["SurrealStatement"]` and `ex.Data["SurrealParams"]` if present, emits an `ILogger<JsonlExceptionMiddleware>.LogError` (which the JSONL provider captures). **MUST NOT** alter the response — observer-only per spec §2.A. Re-throws original exception so `DebugExceptionMiddleware` still owns shaping.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Extensions/DiagnosticsExtensions.cs` — `public static WebApplicationBuilder AddJsonlExceptionLogging(this WebApplicationBuilder builder)` — gated on `builder.Environment.IsDevelopment()`. Binds `Diagnostics:JsonlExceptionLogger` config section, registers `JsonlExceptionWriter` as singleton + `IHostedService`, registers the provider via `builder.Logging.AddProvider(...)`.
- **Files to MODIFY**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Program.cs` — call `builder.AddJsonlExceptionLogging();` BEFORE `builder.Build()`; insert `app.UseMiddleware<JsonlExceptionMiddleware>();` AFTER `app.UseRouting()` (line 575) and BEFORE `app.UseAuthentication()` (line 577) so 401/429 also flow through the observer (spec §2.A explicit).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.Development.json` — add a top-level `"Diagnostics"` block:
    ```
    "Diagnostics": {
      "JsonlExceptionLogger": {
        "Enabled": true,
        "Directory": "logs/exceptions",
        "MinimumLevel": "Warning"
      }
    }
    ```
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.gitignore` — append a single section:
    ```
    # Dev-only JSONL exception logs (IR-7)
    logs/exceptions/
    ```
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Client/Connection/HttpSurrealConnection.cs` — at each `throw new SurrealProtocolException(...)` site inside `ExecuteRawAsync` (lines 148, 166), BEFORE `throw`, construct an exception object first, then populate `ex.Data["SurrealStatement"] = sql; ex.Data["SurrealParams"] = parameters; throw ex;`. Same pattern at lines 316, 324, 330 (parse failures — Statement is still in scope via the outer `sql` parameter at those call sites; check scope before assuming).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Client/Query/DefaultSurrealExecutor.cs` — wrap the body of `DispatchAsync` (lines 83–91) in `try { ... } catch (Exception ex) when (ex is not OperationCanceledException) { ex.Data["SurrealStatement"] = query.Build(); ex.Data["SurrealParams"] = query.Params; throw; }`. This catches BOTH transport faults and post-parse `EnsureAllOk` failures bubbling from `QueryAsync` / `QuerySingleAsync`.
- **Files NOT to touch** (anti-scope-creep):
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/DebugExceptionMiddleware.cs` — response-shaping owner (spec §3 LEFT ALONE). Diagnostics middleware sits in front; never swallows or rewrites.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Models/Responses/OASISResult.cs` — error envelope is stable.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/OASIS.WebAPI.csproj` — no new NuGet (acceptance criterion 6). `System.Threading.Channels` is in the shared framework.
- **Implementation notes** (exact order):
  1. Create the 7 `Core/Diagnostics/` files. `JsonlEntry` first, then `Options`, then `RedactionFilter`, then `Writer`, then `Logger`, then `Provider`, then `Middleware`.
  2. Create `Extensions/DiagnosticsExtensions.cs` with the gated `AddJsonlExceptionLogging` extension.
  3. Wire `Program.cs`: builder-side call before `Build()`; middleware-side after `UseRouting`/before `UseAuthentication`.
  4. Add the `Diagnostics` section to `appsettings.Development.json`.
  5. Append the `logs/exceptions/` ignore stanza to `.gitignore`.
  6. Patch `HttpSurrealConnection.cs` rethrow sites: **BEFORE editing**, run `Grep('throw new SurrealProtocolException', path=packages/Oasis.SurrealDb.Client/Connection/HttpSurrealConnection.cs)` and patch EVERY match — the line numbers above (148, 166, 316, 324, 330) are GUIDANCE, not authoritative; the file may have shifted since this plan was authored. Refactor each `throw new SurrealProtocolException(...)` so the exception is named (`var ex = new SurrealProtocolException(...)`), then `ex.Data["SurrealStatement"] = sql; ex.Data["SurrealParams"] = parameters; throw ex;`. For sites that don't have `parameters` in scope (parse-time at the lines around 316/324/330), still set `SurrealStatement` from `sql`.
  7. Patch `DefaultSurrealExecutor.DispatchAsync` to wrap-and-rethrow with `Data["SurrealStatement"] = query.Build()` and `Data["SurrealParams"] = query.Params`. **Important**: cache `query.Build()` into a local before the dispatch call so the same string can be stamped on the exception without rebuilding; do NOT change `_connection.ExecuteRawAsync(query.Build(), ...)` semantics.
  8. `dotnet build` to confirm.
- **Acceptance**:
  - `dotnet build OASIS.WebAPI.sln` succeeds, zero warnings introduced.
  - Sending an invalid body to `POST /api/avatar/register` produces a row in `logs/exceptions/2026-06-07.jsonl` containing `requestPath`, `statusCode`, `exceptionType` filled and any `password` field redacted to `[REDACTED]` (grep for the literal `"password"` outside of redaction; must be zero hits with cleartext).
  - `git check-ignore logs/exceptions/2026-06-07.jsonl` returns the path (acceptance §7-5).
  - Forcing a SurrealQL syntax error against any happy-path endpoint produces a row with `surrealStatement` populated and `surrealParams` redacted on key match.
  - Pipeline ordering verified: a 401 (no auth) and a 429 (synthetic burst) both produce JSONL rows (spec §2.A "captures 401/429/5xx").
- **Dependencies**: W1-B1 (W1-B1 lands first; both edit `Program.cs`).
- **Parallel-safe with**: every W2-D task and W2-E1 once W1-B1 has merged.

---

### Task W1-B1: Rate-Limit Dev Multiplier + Stress Re-tune

- **Agent tier**: executor-low (haiku) — single config knob + light stress audit.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§2.B, R-5 lines 63, 98–99)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Program.cs` (lines 134–141 + lines 175–210 — limiter setup; the multiplier MUST land BEFORE `AddRateLimiter` sees the values)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.Development.json` (full file — add new key)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.json` (read-only reference — confirm prod fallback baseline)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Stress_RapidOperations.jsonl` (56 lines today; audit how many actual HTTP cases — the suite name encodes intent per R-5)
- **Files to CREATE**: none.
- **Files to MODIFY**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.Development.json` — under existing `"RateLimiting"` (create if absent) add `"DevMultiplier": 100`.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Program.cs` — immediately after line 141 (after `rlFinancialQueue` read), insert:
    ```
    var rlDevMultiplier = builder.Environment.IsDevelopment()
        ? Math.Max(1, rlSection.GetValue<int?>("DevMultiplier") ?? 1)
        : 1;
    rlGlobalPermit    *= rlDevMultiplier;
    rlFinancialPermit *= rlDevMultiplier;
    ```
    The `Math.Max(1, ...)` guard prevents a 0 in dev config from disabling the limiter — `Enabled:false` is the right tool for that.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Stress_RapidOperations.jsonl` — audit total HTTP cases. With `DevMultiplier=100`, `Global:PermitLimit=120 → 12_000` per minute. To still exercise the limiter the suite SHOULD fire ≥`Financial:PermitLimit * DevMultiplier + 1` value-moving calls in one window (≥1001), or ≥12_001 global ones. R-5 forbids "leaking to security posture" but R-5 also says the limiter must STAY ON; preserve the suite's stress intent by either:
    - (a) generating bulk holon create/update entries in-file (cheap), OR
    - (b) leaving the suite as-is IF current case count after dev-multiplier still triggers ≥1 429 reject in a controlled test run.
    Choose (a) only if (b) fails empirically. Mechanical recipe for (a): expand the existing `stress_holon_X` block from 5 → 50, and the `stress_update_X` block from N → 200 (still well under per-request budget). Do NOT introduce loops or templating — the harness has no `repeat` directive.
- **Files NOT to touch**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/appsettings.json` — prod fallback stays at multiplier=1 (IsDevelopment-gated).
  - Any other `*.jsonl` file — E2 owns broader JSONL edits.
- **Implementation notes**:
  1. Add the `DevMultiplier: 100` key under `RateLimiting` in `appsettings.Development.json` (create the `RateLimiting` block if it does not yet exist there — note today the file has NO `RateLimiting` block; literals in `Program.cs` are the fallback).
  2. Insert the multiplier read + multiplication immediately after line 141 in `Program.cs`.
  3. Audit Stress_RapidOperations.jsonl line count after stripping comments. If <1001 HTTP cases, expand the rapid_holon and rapid_update blocks (recipe above) so it still trips `Financial` limiter without trivializing the test (R-5).
  4. Keep IR-1: do NOT change any `expectedStatus` field in the stress suite. The stress suite expects 200 on every burst; that posture is preserved by the dev multiplier.
- **Acceptance**:
  - `dotnet build` succeeds.
  - With `ASPNETCORE_ENVIRONMENT=Production` set, the multiplier is forced to 1 (manual smoke: read the value at runtime via a temp `Console.WriteLine` then revert; OR cover via integration test in a future task — not required for this PR).
  - With Development, full Stress_RapidOperations.jsonl runs without self-rate-limiting; spec acceptance §7-2 row "Stress_RapidOperations: 100%".
  - `git diff` on Stress_RapidOperations.jsonl shows ZERO `expectedStatus` mutations (acceptance §7-7).
- **Dependencies**: none — runs FIRST (before W1-A1).
- **Parallel-safe with**: nothing in Wave 1 (W1-A1 must wait — both edit `Program.cs`). Once W1-B1 merges, W1-A1 plus the W2 wave can fan out.

---

## Wave 2 (12-wide parallel) — Store Parity + Harness Hardening

The 11 store tasks (D1..D11) and the harness task (E1) all touch disjoint files. SurrealAvatarStore
and SurrealBridgeStore are explicitly out of scope (reference + Tier 0 per spec §2.D line 111).

### Tasks W2-D1..W2-D11 — Per-Store Parity Sweep (shared contract)

All eleven tasks share the same shape; only the target store differs. Map:

| Task | Target store |
|------|--------------|
| W2-D1 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealApiKeyStore.cs` |
| W2-D2 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealBlockchainOperationStore.cs` |
| W2-D3 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealHolonStore.cs` |
| W2-D4 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealNftStore.cs` |
| W2-D5 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealQuestNodeExecutionStore.cs` |
| W2-D6 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealQuestRunStore.cs` |
| W2-D7 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealQuestStore.cs` |
| W2-D8 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealQuestTemplateStore.cs` |
| W2-D9 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealStarStore.cs` |
| W2-D10 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealWalletStore.cs` |
| W2-D11 | `c:/Users/atooz/Programming/Projects/oasis-sleek/Core/Idempotency/SurrealIdempotencyStore.cs` (NOTE: lives outside `Providers/Stores/Surreal/`. Apply same 4 invariants but verify reference pattern transfers — it stores idempotency receipts, not domain entities, so UPSERT-with-RETURN-AFTER may not be applicable. Pattern-match against current store behavior rather than blindly imitating SurrealAvatarStore. Also: this file has an uncommitted local edit on the current branch (`git status` shows `M`). Executor MUST coordinate — either rebase the in-flight edit or treat the current working-tree version as the new baseline.) |

- **Agent tier**: executor-low (haiku) per store IF the audit finds the store already mirrors Avatar. Bump to executor (sonnet) only when the diff exceeds ~30 LoC OR the store needs UPDATE→UPSERT conversion. The executor SHOULD probe first (read+diff), then self-classify and proceed.
- **Inputs (read before editing — same for every D task)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§2.D lines 108–112)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealAvatarStore.cs` — the REFERENCE store (UPSERT + RETURN AFTER, `EnsureAllOk()`, `ISurrealRecord`, `Guid.ParseExact(id, "N")` without manual strip on the `id` property).
  - The target store file (per row above).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Client/ISurrealRecord.cs` (the marker interface).
- **Files to CREATE**: none.
- **Files to MODIFY**: the single target store file from the row above.
- **Files NOT to touch**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealAvatarStore.cs` — reference, do not "improve".
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/Providers/Stores/Surreal/SurrealBridgeStore.cs` — Tier 0, out-of-scope.
  - Any POCO under `c:/Users/atooz/Programming/Projects/oasis-sleek/Persistence/SurrealDb/Models/**` — POCO shapes are owned by the schema/generator layer (Persistence layer), not the store layer.
  - For quest stores (D5, D6, D7, D8): `StripIdPrefix` call sites on FK columns (`quest_id`, `avatar_id`, `template_id`, ...) — spec §2.D lines 110–111 say ONLY the `id` property can drop the helper.
- **Implementation notes (per store, in order)**:
  1. Read `SurrealAvatarStore.cs` lines 30–100 to internalise the reference pattern.
  2. Read the target store top-to-bottom.
  3. Audit against the 4 invariants from spec §2.D:
     - (a) UPSERT + RETURN AFTER pattern in the write path (NOT bare UPDATE).
     - (b) `EnsureAllOk()` called on the `SurrealResponse` after every `ExecuteAsync`.
     - (c) The POCO implements `ISurrealRecord` — if not, mark in summary; do NOT add (POCOs are out-of-scope, see Files NOT to touch).
     - (d) The `id` property uses `Guid.ParseExact(value, "N")` directly (no manual `StripIdPrefix` strip on the id-column).
  4. Apply MINIMAL diffs to fix only the invariants violated. Do not refactor naming, comments, or unrelated methods.
  5. For quest stores (D5–D8) the surgical rule is "only `id` drops `StripIdPrefix`; every FK column KEEPS it". Validate by grepping the store for `StripIdPrefix` and confirming the post-edit set is exactly the FK call sites.
  6. If the audit finds the store already at parity, the executor writes a NO-OP diff and reports "no changes — already at parity" as the acceptance evidence. This is the expected outcome for the stores closest to Avatar.
  7. Do NOT shape responses; do NOT add per-store logging — that's a cross-cutting concern owned by W1-A1.
- **Acceptance** (per store):
  - `dotnet build` succeeds.
  - `git diff` on the single target store touches no other file.
  - All 4 invariants present in the post-edit file (mechanical grep: `UPSERT`, `EnsureAllOk()`, `Guid.ParseExact`).
  - For quest stores: `git diff` shows `StripIdPrefix` removals ONLY on the `id` column's `Guid.ParseExact` call site.
- **Dependencies**: none — Wave 2 root.
- **Parallel-safe with**: every other D-task in Wave 2, and W2-E1.

---

### Task W2-D12: SurrealDB Reset CLI verb + dev-up wiring

- **Agent tier**: executor (sonnet) — small but cross-cutting (Schema CLI + PowerShell script).
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§1 table row "DB reset", §3 CREATED list, NFR-2)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Schema/Program.cs` — current CLI entry point and verb wiring
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Schema/Cli/HttpConnectionAdapter.cs` — the SurrealDB HTTP connection helper to reuse
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Schema/Cli/MigrationRunner.cs` (or equivalent) — re-run after wipe so the namespace is migrated cleanly
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/dev-up.ps1` — the dev-stack launcher (modify to invoke `oasis-surreal reset` before API start)
- **Files to CREATE**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Schema/Cli/ResetCommand.cs` — implements the `reset` verb. Logic: `REMOVE NAMESPACE <ns>; DEFINE NAMESPACE <ns> ...; USE NS <ns> DB <db>; ...` then invoke the existing migration runner to bring the empty namespace back to current schema head.
- **Files to MODIFY**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/packages/Oasis.SurrealDb.Schema/Program.cs` — wire the `reset` verb into the CLI's verb dispatch (mirror the existing `up` / `down` verb registration).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/dev-up.ps1` — before the API container starts, invoke `dotnet run --project packages/Oasis.SurrealDb.Schema -- reset` (or whatever the CLI launch idiom is). Skip if `OASIS_SKIP_RESET=1` env var is set (so devs can opt out).
- **Files NOT to touch**:
  - Schemas under `c:/Users/atooz/Programming/Projects/oasis-sleek/Persistence/SurrealDb/Schemas/**` — owned by the source-of-truth pipeline.
  - `dev-down.ps1` — stop logic stays unchanged.
- **Implementation notes**:
  1. Read the existing `up` verb implementation in `Program.cs` + the migration runner to mirror argument-binding, NS/DB resolution from config or args, and exit codes.
  2. The `reset` verb: idempotent. Connect, `REMOVE NAMESPACE IF EXISTS <ns>;`, `DEFINE NAMESPACE <ns>;`, `USE NS <ns> DB <db>;`, then call `MigrationRunner.RunUpAsync()` so the empty namespace is brought current. Print one-line summary `[reset] wiped ns=<ns>, ran N migrations`.
  3. In `dev-up.ps1`, after SurrealDB is confirmed reachable but BEFORE the API container starts: invoke the reset command and gate continuation on its exit code. Honor `$env:OASIS_SKIP_RESET -eq "1"` to skip (the user may want to keep data across runs).
  4. Document the behavior in a one-line comment at the top of `ResetCommand.cs` per `[[self-documenting-over-comments]]` (WHY, not WHAT).
- **Acceptance**:
  - `dotnet run --project packages/Oasis.SurrealDb.Schema -- reset` against the local podman SurrealDB exits 0 and the `oasis` namespace contains only the migrated schema (no rows in any user table).
  - `dev-up.ps1` runs reset by default, skippable via `OASIS_SKIP_RESET=1`.
  - No existing `up` / `down` verb behavior regressed.
- **Dependencies**: none — Wave 2 root.
- **Parallel-safe with**: every other Wave 2 task (independent files).

---

### Task W2-E1: Harness Hardening — suiteVars + Inconclusive Status

- **Agent tier**: executor (sonnet) — parser change + harness wiring + model addition.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§2.E lines 114–118)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Parsers/JsonlTestParser.cs` (full file — single class, ~76 lines)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/TestHarness.cs` (full file — `RunSuiteAsync` at line 75 owns per-suite context init at line 82)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/HttpTestClient.cs` (lines 27–118 — `ExecuteAsync` + `Substitute`)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Models/TestResult.cs` (full file — `Passed:bool` today; add a status enum)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Models/TestSuite.cs` (full file — add `SuiteVars`)
- **Files to CREATE**: none (modify existing models).
- **Files to MODIFY**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Models/TestResult.cs` — add a `TestStatus` enum (`Passed`, `Failed`, `Inconclusive`, `Skipped`) and a `Status` property. Keep the existing `Passed:bool` for backward-compat with `MarkdownReporter` (set `Passed = Status == TestStatus.Passed`).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Models/TestSuite.cs` — add `public Dictionary<string,string> SuiteVars { get; set; } = new();`.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Parsers/JsonlTestParser.cs` — extend `ParseFileAsync`:
    - On the first non-comment, non-empty line, if the parsed JSON contains a `"_suiteVars"` object property and NO `"id"` property, treat it as the suite-level config block — copy each string-valued field into `suite.SuiteVars` and DO NOT add it to `suite.Cases`. Continue parsing remaining lines as cases.
    - Backward compat: if no `_suiteVars` block is present, behavior is identical to today. Convention: `{"_suiteVars":{"suitePrefix":"avatar"}}` as the first non-comment line.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/TestHarness.cs` — in `RunSuiteAsync` at line 82, initialise `var context = new Dictionary<string, string>(suite.SuiteVars);` instead of `new Dictionary<string, string>()`, so `{{suitePrefix}}` resolves on case #1.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/HttpTestClient.cs` — wrap the existing `Substitute` calls in `ExecuteAsync`:
    - After all substitutions complete (just before `await _httpClient.SendAsync(request)` at line 68), scan the resolved Authorization header (if any) and the resolved path/body for any remaining `{{...}}` token. If found:
      - Set `result.Status = TestStatus.Inconclusive`;
      - Set `result.Error = "Unsubstituted token after suite context merge: <token>"`;
      - Log a `Console.Error.WriteLine($"[INCONCLUSIVE] {suiteName}/{testCase.Id}: unsubstituted {{...}} → likely upstream extract miss");`
      - SKIP the network call — `return result` early. Spec §2.E: "marked Inconclusive not Failed".
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Reporters/MarkdownReporter.cs` — only if needed to render the new status. Minimal change: treat `Inconclusive` distinct from `Failed` in the summary line so V1 can grep for it.
- **Files NOT to touch**:
  - Any `.jsonl` file under `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/` — E2 owns that edit pass. E1 keeps the harness backward-compatible so today's suites still pass.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/Program.cs` — entry point; no behavior change required.
- **Implementation notes**:
  1. Add the `TestStatus` enum + `Status` property to `TestResult.cs`. Default to `TestStatus.Failed` on construction; flip to `Passed` when assertion succeeds (mirrors the existing `Passed=true` flow in `EvaluateStatus`).
  2. Add `SuiteVars` to `TestSuite.cs`.
  3. Patch the parser: read the first parsed JSON object; detect the suite-vars sentinel (`_suiteVars` property AND no `id` property); transfer string values to `suite.SuiteVars`; do not add to `Cases`.
  4. Patch `TestHarness.RunSuiteAsync` to seed `context` from `suite.SuiteVars`.
  5. Patch `HttpTestClient.ExecuteAsync` to detect remaining `{{...}}` post-substitution and short-circuit to `Inconclusive`.
  6. Patch `MarkdownReporter.Render` to surface `Inconclusive` counts in the per-suite summary (one new column or one new bullet).
- **Acceptance**:
  - `dotnet build` succeeds.
  - Running today's suites (NO jsonl edits yet) produces identical pass/fail counts (backward-compat).
  - A synthetic test with `Authorization: Bearer {{nonexistent}}` produces `Inconclusive` (not Failed) and does NOT hit the API.
  - `tests/live-test-results.md` shows the Inconclusive column.
- **Dependencies**: none.
- **Parallel-safe with**: every D-task and the other Wave-2 tasks.

---

## Wave 3 (serial: C1 → E2) — Audit + JSONL Edits

E2 must read `docs/auth-audit.md` produced by C1. The earlier framing as "2-wide
parallel with overlap" understated the dependency — running them concurrently
produces a race where E2 can hallucinate against a missing file. Run STRICTLY
serially within this wave.


### Task W3-C1: Auth-Chain Audit → `docs/auth-audit.md`

- **Agent tier**: executor (sonnet) — broad read, narrow write.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§2.C lines 101–107)
  - Every file in `c:/Users/atooz/Programming/Projects/oasis-sleek/Controllers/*Controller.cs` — 15 controllers.
  - Every file in `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/*.jsonl` — 19 suites (verified by `ls`).
- **Files to CREATE**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/docs/auth-audit.md` — the deliverable.
- **Files to MODIFY**: none.
- **Files NOT to touch**:
  - Any controller (audit deliverable is documentation only).
  - Any `.jsonl` (that's E2).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Frontend.jsonl` — regression gate (FR-8 / IR-4); referenced in audit but NOT flagged for edit.
- **Implementation notes** (deliverable structure):
  1. Build a table for every controller: `Controller | Action | HTTP route | AuthAttribute (`[Authorize]` / `[AllowAnonymous]` / inherited) | Scheme requirement (`JWT` / `ApiKey` / `MultiScheme` / `None`)`.
  2. Build a second table: per `.jsonl` suite, per case `id`, the resolved `method + path` (after `{{suitePrefix}}` substitution where applicable), the matched controller action, the case's Authorization header presence, the case's `expectedStatus`.
  3. Build a third "mismatch" table: rows where the case expects `200/2xx` AND the controller action requires auth AND the case lacks an `Authorization` header. Each row gets a "fix-prescription" column: "add `Authorization: Bearer {{<authVar>}}`" — choose the var name from whichever earlier case extracted a token in that suite (typically `{{suitePrefix}}_auth.token`).
  4. Explicitly mark a fourth bucket of "INTENTIONAL 401" rows — cases in `*_Malicious.jsonl` that EXPECT a 401 to verify rejection. Those MUST NOT be touched by E2 (IR-1 + R-3).
  5. The audit is the source of truth for E2's diff. Keep it under 500 lines; use compact tables.
- **Acceptance**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/docs/auth-audit.md` exists.
  - Every `[Authorize]` controller action appears in table 1.
  - Every `.jsonl` case appears in table 2 (count check: rows in table 2 == total non-comment lines across the 20 suites minus the `_suiteVars` lines).
  - Mismatch table has at least one row per `_Malicious` and `_QA` suite if any exist (spec failure-distribution row "401 auth-not-set-up: 25%" implies ~155 cases).
- **Dependencies**: none — Wave 3 root. Must complete and land `docs/auth-audit.md` before W3-E2 starts.
- **Parallel-safe with**: nothing in Wave 3 — E2 reads C1's output, so the wave is strictly serial.

---

### Task W3-E2: JSONL Edit Pass — suitePrefix + Extract Corrections + Auth Headers

- **Agent tier**: executor (sonnet) — mechanical-with-judgment.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/docs/auth-audit.md` — produced by W3-C1; THIS IS THE ONLY CROSS-DEPENDENCY in Wave 3.
  - All 19 files in `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/*.jsonl` for reference.
  - Every controller referenced in the audit, to verify extract paths against real response shapes (`OASISResult<T>` envelope: `IsError`, `Message`, `Result`).
- **Files to CREATE**: none.
- **Files to MODIFY** — every `.jsonl` EXCEPT `Frontend.jsonl` and `Stress_RapidOperations.jsonl`:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/AvatarController.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/AvatarController_QA.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/AvatarController_Malicious.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/HolonController.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/HolonController_QA.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/HolonController_Malicious.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/BlockchainOperationController.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/BlockchainOperationController_QA.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/BlockchainOperationController_Malicious.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/STARODKController.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/STARODKController_QA.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/STARODKController_Malicious.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/E2E-Flows.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/CrossController_E2E.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Blockchain_Devnet.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/MaliciousPayloads.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/QA-EdgeCases.jsonl`
- **Files NOT to touch** (R-10 + FR-8 + R-5):
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Frontend.jsonl` — regression gate (FR-8, IR-4).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/live-tests/Stress_RapidOperations.jsonl` — W1-B1 owns it.
  - Any controller, store, model, or harness file.
- **Implementation notes** (per suite, in order):
  1. Insert a `_suiteVars` line as the first non-comment line: `{"_suiteVars":{"suitePrefix":"<stem>"}}` where `<stem>` is the filename without `.jsonl`, lowercased, with `_` retained. Example for `AvatarController_Malicious.jsonl`: `{"_suiteVars":{"suitePrefix":"avatarcontroller_malicious"}}`.
  2. Find every hardcoded test email/username (search regex `"email":"[^@]*@[^"]*\.oasis"` and `"username":"[^"]*"`) and prefix them with `{{suitePrefix}}_`. Example: `"email":"avatar@test.oasis"` → `"email":"{{suitePrefix}}_avatar@test.oasis"`. R-2 mitigation.
  3. For every `"extract":{...}` clause, cross-walk against the matched controller (use `docs/auth-audit.md` to locate it). Verify the JSON path resolves on the actual `OASISResult<T>` shape (`result.id` for `Result.Id`, `result` for `Result` when `T = string`). Correct any mismatched paths (IR-2 exception: extract path correction is harness misconfiguration, not relaxation).
  4. For every case flagged in C1's mismatch table where auth is required but no `Authorization` header is present, add `"Authorization":"Bearer {{<authVar>}}"` to the `headers` object — picking the `<authVar>` from the suite's earlier login extraction.
  5. IR-1 ENFORCEMENT — do NOT modify any `expectedStatus`, `expectedStatusRange`, or remove any case. The mechanical check at the end: `git diff -U0 -- '*.jsonl' | grep -E '^\\-\\s*"expectedStatus' | wc -l` MUST be 0.
  6. Frontend.jsonl and Stress_RapidOperations.jsonl: ZERO bytes changed.
- **Acceptance**:
  - All 17 listed `.jsonl` files (19 total minus Frontend + Stress) have a `_suiteVars` head line.
  - `git diff -- tests/OASIS.WebAPI.LiveTests/live-tests/Frontend.jsonl` is empty.
  - `git diff -- tests/OASIS.WebAPI.LiveTests/live-tests/Stress_RapidOperations.jsonl` is empty (or only contains W1-B1's bulk-expansion if that task also landed here — but those are different commits / different PR lanes).
  - `git diff -U0 tests/OASIS.WebAPI.LiveTests/live-tests/ | Select-String -Pattern '^-\s*"expectedStatus'` returns ZERO hits (acceptance §7-7).
  - Running the harness end-to-end shows the 401-bucket cases (~155) reduced toward zero on happy-path suites.
- **Dependencies**: W3-C1 (audit file required as input).
- **Parallel-safe with**: nothing inside Wave 3 (linear with C1); fully parallel with Wave 2 tasks IF they have landed.

---

## Wave 4 (serial) — Triage + Verify

### Task W4-F1: 4xx Triage + Real-Bug Fixes

- **Agent tier**: executor-high (opus) — needs broad reasoning across controller/manager/store/store boundaries.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§7 acceptance criteria, especially IR-1/IR-2/R-3)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/live-test-results.md` — regenerated by the harness run F1 will perform.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/logs/exceptions/2026-06-07.jsonl` — from W1-A1; the diagnostic side-channel for any 5xx.
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/docs/auth-audit.md` — for context on intentional 4xx cases.
  - Any failing controller / manager / store identified from the report.
- **Files to CREATE**: none expected.
- **Files to MODIFY**:
  - Whatever real-bug surface the triage uncovers — likely a subset of `Controllers/`, `Managers/`, `Providers/Stores/`, or `Persistence/SurrealDb/Models/`.
  - **NOT** any `.jsonl` file (IR-1).
- **Files NOT to touch** (HARD):
  - Any `.jsonl` file (IR-1 — never edit `expectedStatus`; never add `Skip:true`; never delete cases).
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/frontend/**` per `[[no-frontend-typecheck]]`.
  - Bridge/wormhole code per spec §2-OutOfScope.
  - The exception-logger surface from W1-A1 (it's the diagnostic tool, not the patient).
- **Implementation notes**:
  1. Run the dev stack: `pwsh c:/Users/atooz/Programming/Projects/oasis-sleek/dev-up.ps1` after invoking the schema reset (`oasis-surreal reset` if the CLI verb exists; otherwise the package's existing `migrate up` + a manual `REMOVE NAMESPACE ... DEFINE NAMESPACE ...` pair as a one-shot — spec §1 line 82 expects a `reset` verb but that's listed as CREATED in §3; if NOT yet built, F1 can ship a clean dev DB by stopping the podman container, dropping its volume, and restarting via `dev-up.ps1`).
  2. Run `dotnet run --project c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests`.
  3. Open `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/live-test-results.md` + `c:/Users/atooz/Programming/Projects/oasis-sleek/logs/exceptions/2026-06-07.jsonl`.
  4. Triage each remaining failure into one of four buckets:
     - **Real API bug** — fix at the controller/manager/store. IR-2: bugs in API, fixes NEVER in test data.
     - **Malicious/QA regression** — the case EXPECTS 4xx and the API now produces 200; that's a controller validation gap. Fix the API to reject (R-3).
     - **Surreal-store gap missed by Wave 2** — escalate by re-running the relevant D-task's audit; fix the store.
     - **Harness misconfig still present** — fix the `extract` path or the JSONL header in a follow-up to E2 (still NOT an `expectedStatus` edit).
  5. After every batch of fixes, defer the sweep — per `[[test-policy]]` rule, run tests ONCE at the very end of F1 (the verifier V1 will be the second pass).
  6. Discipline: no compat shims, no new files matching `*Adapter*`, `*Compat*`, `*Legacy*`, no `[Skip]` attributes, no new NuGet (acceptance §7-6, §7-8).
- **Acceptance**:
  - Happy-path suites (`AvatarController`, `HolonController`, `BlockchainOperationController`, `STARODKController`, `E2E-Flows`, `CrossController_E2E`, `Blockchain_Devnet`, `Frontend`): 100% (spec §7-2).
  - `_Malicious` and `_QA` suites combined: ≥95% (spec §7-2).
  - `Stress_RapidOperations`: 100% under dev multiplier (spec §7-2).
  - Wall-clock ≤ 90 s (spec §7-3 / NFR-1).
  - F1 hands off to V1 with `tests/live-test-results.md` + `logs/exceptions/2026-06-07.jsonl` populated.
  - **Sweep-iteration ceiling: 2.** If targets are still unmet after the 2nd full-suite sweep, F1 STOPS, writes `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/triage-residue.md` with per-bucket counts + per-failure exception-log `requestId` refs, and hands off. NO unbounded debugging. R-9 bound.
- **Dependencies**: W1-A1, W1-B1, ALL W2-D*, W2-E1, W3-C1, W3-E2.
- **Parallel-safe with**: nothing — serial.

---

### Task W4-V1: Verifier (DIFFERENT executor instance)

- **Agent tier / subagent_type**: `oh-my-claudecode:verifier` (sonnet). MUST be invoked with this exact `subagent_type` — a different agent type from W4-F1 (which uses `oh-my-claudecode:executor-high` opus). Enforces `<execution_protocols>` "never self-approve in the same active context" + spec line 205 verifier-separation rule. Do NOT invoke as `oh-my-claudecode:executor` with a "verify" prompt — that would share the same agent identity.
- **Inputs (read before editing)**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/.omc/autopilot/spec.md` (§7 acceptance criteria 1–8)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/live-test-results.md` (post-F1)
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/logs/exceptions/2026-06-07.jsonl`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/docs/auth-audit.md`
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/OASIS.WebAPI.csproj` (verify no new NuGet packages — diff against `main`)
  - `git diff main -- tests/OASIS.WebAPI.LiveTests/live-tests/` (verify zero `expectedStatus` mutations)
- **Files to CREATE**:
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/coverage-summary.md` — per-suite pass/fail/Inconclusive with cross-references to exception-log entries (paste exception-log `requestId` for any failure carrying server-side context).
- **Files to MODIFY**: none.
- **Files NOT to touch**: literally anything else. The verifier is read-only across source code.
- **Implementation notes** (verification checklist — each item is mechanical):
  1. Re-run `dotnet run --project c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests` in a clean shell (different process than F1's last run).
  2. Confirm acceptance §7-2 row-by-row:
     - Frontend.jsonl: 9/9 (IR-4 regression gate).
     - AvatarController / HolonController / BlockchainOperationController / STARODKController: 100%.
     - E2E-Flows / CrossController_E2E / Blockchain_Devnet: 100%.
     - `_Malicious` suites combined: ≥95%.
     - `_QA` suites combined: ≥95%.
     - Stress_RapidOperations: 100%.
  3. Confirm acceptance §7-3: wall-clock ≤ 90 s. Pull from `tests/live-test-results.md` `TotalDurationMs`.
  4. Confirm §7-4: `logs/exceptions/2026-06-07.jsonl` populated, redaction working (grep `password` literal in cleartext → ZERO hits outside `[REDACTED]`).
  5. Confirm §7-5: `git check-ignore logs/exceptions/2026-06-07.jsonl` returns the path.
  6. Confirm §7-6: `git diff main -- OASIS.WebAPI.csproj` shows ZERO new `<PackageReference>` lines.
  7. Confirm §7-7: `git diff main -U0 -- '**/*.jsonl' | Select-String '^-\s*"expectedStatus'` returns ZERO hits (acceptance §7-7).
  8. Confirm §7-8: `git diff main --stat | Select-String -Pattern 'Adapter|Compat|Legacy'` returns ZERO hits.
  9. Write `tests/OASIS.WebAPI.LiveTests/coverage-summary.md` with one section per suite, the pass/fail/Inconclusive count, and a "diagnostic refs" subsection that pastes any matching exception-log `requestId`/`timestamp` for failures.
  10. If ANY criterion fails, V1 reports failure with the specific bullet — does NOT attempt fixes. F1 (or a follow-up F1 lane) handles regressions.
- **Acceptance** (V1's own done-criteria):
  - `c:/Users/atooz/Programming/Projects/oasis-sleek/tests/OASIS.WebAPI.LiveTests/coverage-summary.md` exists and lists ALL 20 suites.
  - Every spec §7 criterion checked off mechanically.
  - Final verdict line: `STATUS: GREEN` if all pass, `STATUS: BLOCKED: <which criterion>` otherwise.
- **Dependencies**: W4-F1.
- **Parallel-safe with**: nothing — serial after F1.

---

## Cross-cutting Notes

| Rule | Source |
|------|--------|
| No compat / adapter / legacy shims | `[[greenfield-prelaunch-no-compat]]` + spec §7-8 |
| Skip `frontend/` tsc | `[[no-frontend-typecheck]]` |
| Minimal comments, self-documenting | `[[self-documenting-over-comments]]` |
| Config-driven, real appsettings | `[[config-driven-calls]]` |
| Single test-sweep at the end of F1 | global CLAUDE.md "Test execution policy: run once at the end" |
| Authoring and review separated | global CLAUDE.md `<execution_protocols>`; enforced by V1 ≠ F1 (spec line 205) |
| Verifier reads, never writes source | spec line 205 + global `<verification>` |
| `expectedStatus` is sacrosanct | spec IR-1 + acceptance §7-7 |
| Frontend.jsonl is the regression gate | spec FR-8 / IR-4 |

### Wave-to-Task Quick Map

| Wave | Tasks | Parallelism |
|------|-------|-------------|
| 1 | W1-B1 → W1-A1 | Serial (both edit `Program.cs`); W1-B1 first |
| 2 | W2-D1..W2-D12, W2-E1 | 13-wide (all touch disjoint files) |
| 3 | W3-C1 → W3-E2 | Serial (E2 reads `docs/auth-audit.md`) |
| 4 | W4-F1 → W4-V1 | Strictly serial; V1 is a different `subagent_type` |

### TODO ID Index (for traceability across PRs, commits, and `state_*` writes)

- `W1-A1` — Exception logger + Surreal-aware capture
- `W1-B1` — Rate-limit dev multiplier + Stress re-tune
- `W2-D1` — SurrealApiKeyStore parity
- `W2-D2` — SurrealBlockchainOperationStore parity
- `W2-D3` — SurrealHolonStore parity
- `W2-D4` — SurrealNftStore parity
- `W2-D5` — SurrealQuestNodeExecutionStore parity
- `W2-D6` — SurrealQuestRunStore parity
- `W2-D7` — SurrealQuestStore parity
- `W2-D8` — SurrealQuestTemplateStore parity
- `W2-D9` — SurrealStarStore parity
- `W2-D10` — SurrealWalletStore parity
- `W2-D11` — SurrealIdempotencyStore parity
- `W2-E1` — Harness `suiteVars` + Inconclusive
- `W3-C1` — Auth-chain audit doc
- `W3-E2` — 18-file JSONL edit pass
- `W4-F1` — Triage + real-bug fixes
- `W4-V1` — Verifier (different instance)
