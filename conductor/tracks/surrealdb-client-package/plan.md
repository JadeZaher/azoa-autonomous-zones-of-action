# Homebake SurrealDB Client Package — Plan

Sub-waves: **1.5a** (HTTP + builder + Mermaid + analyzer + integration,
~weeks 1–2, unblocks wave-2 adapters) and **1.5b** (WebSocket + LIVE
subscriptions + saga adoption, week 3+).

## Sub-wave 1.5a — Foundation + integration

### Phase 1 — Bootstrap
1. [ ] Create `packages/Oasis.SurrealDb.Client/` + `.Schema/` + `.Analyzer/` directories with `Oasis.SurrealDb.<Name>.csproj` (netstandard2.0 + net8.0 multi-target; version 0.1.0; package metadata: authors, description, repo URL; `IsPackable=true`; `IsPublishable=false` until decision)
2. [ ] Create matching `tests/Oasis.SurrealDb.<Name>.Tests/` projects (xunit + FluentAssertions + Moq, matching versions in main test project)
3. [ ] Wire all packages + test projects into `oasis-sleek.sln`
4. [ ] Add `Directory.Build.props` at repo root with `<OasisSurrealDbVersion>0.1.0</OasisSurrealDbVersion>` MSBuild property; future consumers reference via `Version="$(OasisSurrealDbVersion)"`

### Phase 2 — Client core (HTTP transport + JSON + transactions)
5. [ ] `ISurrealConnection` interface + `HttpSurrealConnection` impl: `POST /sql`, basic-auth header, `Use` (namespace/database scoping), `Dispose` cleanup. No WebSocket yet
6. [ ] `SurrealResponse` model: list of `SurrealStatementResult` (each carries `Status` "OK"/"ERR", `Detail?`, `Values<JsonElement>?`). Closes code-review C5 design root
7. [ ] `SurrealJsonOptions` (static): `JsonStringEnumConverter` registered by default; custom converters for `RecordId`, `DateTime` (SurrealDB's `<datetime>` round-trip), `Duration`, `decimal` (string-on-wire). Closes code-review C6
8. [ ] Transaction wrapper: `BeginTransactionAsync()` returns `ISurrealTransaction : IAsyncDisposable`; emits `BEGIN TRANSACTION` on construction, `COMMIT TRANSACTION` on `CommitAsync`, `CANCEL TRANSACTION` on dispose-without-commit. Closes negative-space G-C
9. [ ] Connection pool (`SemaphoreSlim`-bounded, `MaxConnections` configurable, default 32); reconnect with jittered backoff on transport failure
10. [ ] Unit tests for phase 2: response parsing, enum round-trip, transaction commit/rollback, pool acquisition under contention

### Phase 3 — Query builder
11. [ ] Move `Core/SurrealDb/Query/SurrealQuery.cs`, `SurrealIdentifier.cs`, `ISurrealExecutor.cs`, `SurrealExecutor.cs` from `OASIS.WebAPI` INTO `packages/Oasis.SurrealDb.Client/Query/`. Keep public API unchanged for now; deletions in OASIS happen in Phase 6
12. [ ] Add SurrealQL reserved-word denylist to `SurrealIdentifier.ForTable` and `ForRecordId` (closes code-review H4). Source list: https://surrealdb.com/docs/surrealql/datamodel
13. [ ] Fluent clause builders on `SurrealQuery`: `.Where(string, paramsObj)`, `.OrderBy(field, dir)`, `.Limit(int)`, `.Start(int)`, `.Return("BEFORE"|"AFTER"|"DIFF"|"NONE")`, `.Fetch(path)`. Each returns new immutable `SurrealQuery` instance (no mutation)
14. [ ] `SurrealQuery.UpdateOnly(table, id).Where(field, value).Set(field, value)` — the **G2 conditional-state-transition primitive**. Emits `UPDATE $table WHERE id = $id AND $field = $value SET $field2 = $value2 RETURN AFTER`. Returns single-row affected count from per-statement result; caller asserts == 1. Closes code-review C5 use-case
15. [ ] `SurrealQuery.Relate(fromRid, edge, toRid).WithContent(obj)` — graph helper. Emits `RELATE $from -> $edge -> $to CONTENT $obj`
16. [ ] Multi-statement composition: `SurrealQuery.Combine(q1, q2, q3)` returns single `SurrealQuery` whose `ExecuteAsync` returns per-statement results. Ban semicolons inside individual `SurrealQuery.Of(...)` calls; `Combine` is the only legal way to send multiple statements. Closes code-review C5
17. [ ] `ISurrealExecutor` surface: `QueryAsync<T>(SurrealQuery, ct)`, `QuerySingleAsync<T>(...)`, `ExecuteAsync(...)` → `SurrealResponse`. Drop `int Count` from `ExecuteAsync` return; callers use per-statement `.AffectedCount`
18. [ ] Unit tests for phase 3: all builder shapes, conditional-state-transition single-row enforcement, reserved-word rejection, multi-statement result fan-out, RELATE emit

### Phase 4 — Schema package (Mermaid + generator + migration runner + CLI)
19. [ ] Tokenizer + parser for Mermaid ER syntax (the `erDiagram` block: entities, attributes with types, cardinality relationships). Produces `MermaidSchemaModel`
20. [ ] Annotation DSL parser: scans for `%% @surreal.<directive> <args>` comment lines associated with the immediately-preceding entity / attribute / relationship. Strict namespacing — unknown `@surreal.*` directives fail with file:line error. Initial directive set: `@surreal.schemafull` (entity), `@surreal.index unique fields=[a,b,c] name=...` (entity), `@surreal.assert "$value INSIDE [...]"` (attribute), `@surreal.option` (attribute, marks nullable), `@surreal.relate from=X to=Y edge=Z` (entity), `@surreal.live` (entity, declares LIVE-eligible)
21. [ ] Generator: `MermaidSchemaModel → SurqlEmitter → string`. Deterministic; identical input → identical output. Numbered file-prefix preserved (`010_wallet.mermaid` → `010_wallet.surql`)
22. [ ] Generator unit tests: golden-file fixtures (one `.mermaid` input + one expected `.surql` output per aggregate) for each of the 7 wave-1 tables
23. [ ] Migration runner: ordered file apply + SHA-256 per-file checksum + `schema_migration` table (`file_name`, `checksum`, `applied_at`, `applied_by`). `--dry-run` shows planned changes without executing. Refuses to re-apply a file whose checksum changed (force-reapply requires explicit `--force` flag). Closes B7
24. [ ] CLI tool `oasis-surreal` (`<PackAsTool>true</PackAsTool>` on `Oasis.SurrealDb.Schema.csproj`): `oasis-surreal migrate up`, `migrate status`, `migrate dry-run`, `generate <file.mermaid>`, `validate <file>`. Reads connection config from env vars or `--connection` flag
25. [ ] Integration tests for phase 4: schema_migration table created on first apply; re-apply is idempotent; checksum-mismatch refusal; dry-run is read-only

### Phase 5 — Analyzer package (relocation + bypass closure)
26. [ ] Move `analyzers/SurrealQlSafetyAnalyzer/` contents INTO `packages/Oasis.SurrealDb.Analyzer/`. Update target framework to netstandard2.0 (analyzers must). Update `AnalyzerReleases.Shipped.md` to mark v0.1.0
27. [ ] Companion-package wiring: `Oasis.SurrealDb.Analyzer.csproj` declares analyzer assets; consumers add `PackageReference Include="Oasis.SurrealDb.Analyzer" PrivateAssets="all"` (so the analyzer doesn't transit through to their consumers)
28. [ ] Extend SRDB0001: one-hop variable resolution. When call-site argument is `IdentifierNameSyntax`, follow the local declaration; if the declaration initializer is a banned pattern, report at the call site. Closes the largest code-review H3 bypass
29. [ ] Update analyzer unit tests to cover the new bypass closure + confirm existing tests still pass

### Phase 6 — OASIS integration (delete + replace)
30. [ ] Add `ProjectReference` from `OASIS.WebAPI.csproj` to `packages/Oasis.SurrealDb.Client/Oasis.SurrealDb.Client.csproj` and `packages/Oasis.SurrealDb.Analyzer/...csproj` (the latter with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`)
31. [ ] Remove `<PackageReference Include="SurrealDb.Net" Version="[0.10.2]" />` from `OASIS.WebAPI.csproj` and from `tests/OASIS.WebAPI.IntegrationTests/OASIS.WebAPI.IntegrationTests.csproj`. The integration-tests project gets `ProjectReference` to the package too (closes code-review H1 + H2 simultaneously)
32. [ ] Delete `OASIS.WebAPI/Core/SurrealDb/Query/` directory entirely (the types now live in the package; OASIS imports via `using Oasis.SurrealDb.Client;`)
33. [ ] Delete `analyzers/SurrealQlSafetyAnalyzer/` directory entirely + the `<ProjectReference … OutputItemType="Analyzer" …>` to it; replaced by the package reference in step 30
34. [ ] Delete `scripts/surrealdb/check-sdk-pin.ps1`, the `<SurrealDbNetPinnedVersion>` property + `<VerifySurrealSdkPin>` MSBuild target in `OASIS.WebAPI.csproj`, and `tests/OASIS.WebAPI.Tests/Core/SurrealDbSdkPinTests.cs`. Replaced by `Directory.Build.props` `<OasisSurrealDbVersion>` (step 4)
35. [ ] Re-author the 7 wave-1 schemas as `.mermaid` sources under `Persistence/SurrealDb/Schemas/source/{010_wallet,020_bridge_tx,030_swap_state,040_nft_ownership,050_operation_log,060_consumed_vaa_ledger,070_idempotency_key_store}.mermaid`. Apply the strategic-review fixes inline: B2 Wormhole VAA index correctness (`bridge_tx` + `consumed_vaa_ledger`); B3 empirical UNIQUE-on-nullable decision; reserved-word safety on every identifier
36. [ ] Run `oasis-surreal generate Persistence/SurrealDb/Schemas/source/*.mermaid` and commit the regenerated `.surql` files alongside; assert byte-similar (modulo stylistic normalization) to wave-1 output; CI gate that source and generated stay in sync
37. [ ] Update `tests/OASIS.WebAPI.IntegrationTests/IntegrationTestBase.cs` and `Factories/OASISTestWebApplicationFactory.cs` to use the new client (`IServiceCollection.AddOasisSurrealDb(...)`) instead of `SurrealDb.Net`'s `ISurrealDbClient`. Remove direct `HttpClient` SurrealDB probes
38. [ ] Update `scripts/passoff-surrealdb-wave1.ps1` section 2 to drop the `[G4 SDK-PIN OK]` literal assertion (the target no longer exists); replace with assert that `Oasis.SurrealDb.Client` is referenced at the version pinned in `Directory.Build.props`. Update section 3 (drift negative test) to fake-bump the `OasisSurrealDbVersion` property instead. Update section 7 to reference the new analyzer package path
39. [ ] Run `dotnet build` (0 errors, ≤17 warnings), `dotnet test` (618+ green plus new package-suite tests), `powershell -NoProfile -File scripts/passoff-surrealdb-wave1.ps1` (exit 0). Commit checkpoint

### Phase 7 — Sub-wave 1.5a sign-off
40. [ ] Tag commit `surrealdb-client-package-1.5a-complete`. Update `.omc/ultrapilot-state.json`. Notify [[surrealdb-migration]] wave-2 that adapter work is unblocked
41. [ ] Author `packages/README.md` documenting package boundary, version property, internal-only status, deferred publish decision

## Sub-wave 1.5b — WebSocket + LIVE subscriptions + saga adoption

### Phase 8 — WebSocket transport
42. [ ] `WebSocketSurrealConnection` impl alongside `HttpSurrealConnection`; selectable via `SurrealConnectionMode` (`Http` | `WebSocket`); HTTP stays the default. SurrealDB RPC message framing (JSON envelope: `id`, `method`, `params`). Method coverage: `signin`, `use`, `query`, `live`, `kill`
43. [ ] Request-id correlation: outgoing messages get monotonic id; incoming responses route to the awaiting `TaskCompletionSource` by id
44. [ ] Ping/pong heartbeat (configurable, default 30s); idle reconnect with state restoration (`use ns/db`, re-authenticate, re-subscribe LIVE if applicable)
45. [ ] Unit tests for phase 8: message framing round-trip, id correlation under concurrent in-flight requests, reconnect restores namespace + auth

### Phase 9 — LIVE subscriptions with at-least-once semantics
46. [ ] `ISurrealLiveSubscription<T>` interface: `OnEvent(Func<LiveEvent<T>, Task>)`, `OnReconnect(Func<Task>)`, `LastSeenSequence` (long?), `DisposeAsync` cleanup
47. [ ] Per-subscription **sequence tracking**: persist last-seen seq in a `live_cursor` SurrealDB table keyed by `(subscription_name, consumer_id)`. On `LIVE` setup, query the cursor; if non-null, replay missing events from the outbox by querying `>` cursor before resuming live stream
48. [ ] At-least-once delivery contract documented in `Oasis.SurrealDb.Client/docs/LIVE-SEMANTICS.md`: "Server is best-effort-ordered, single-node. Client guarantees: every event is delivered at least once across reconnects. Duplicates are possible; consumers must be idempotent. Order is preserved within a single subscription instance, NOT across reconnects."
49. [ ] Chaos test harness: scripted reconnect-during-event-stream test that asserts every committed change in the test window arrives at the consumer at least once. Target: 10k events, 100 forced reconnects, 0 lost events

### Phase 10 — Saga LIVE-trigger adoption
50. [ ] In [[durable-saga-orchestration]], implement `LiveQuerySagaTrigger : ISagaTrigger` alongside the existing polling trigger. Configurable per-saga: `Trigger = Polling | Live | Both` (default `Both` = LIVE primary, polling backup that asserts no-missed-events every 60s)
51. [ ] Wave-2 saga work (now in [[surrealdb-migration]] task 8a) adopts the new trigger; polling stays as default until 90-day reliability soak passes
52. [ ] Pass-off gate addition: new `scripts/passoff-surrealdb-1.5b.ps1` extends the wave-1 gate with: WebSocket round-trip; LIVE subscription event delivery; reconnect-replay no-loss assertion

### Phase 11 — Sub-wave 1.5b sign-off
53. [ ] Tag commit `surrealdb-client-package-1.5b-complete`. Author `packages/CHANGELOG.md` summarizing 0.1.0 → 0.2.0 transition (LIVE added)
54. [ ] Begin the 90-day internal-soak window before considering public publish. Document decision criteria in `packages/PUBLISH-CRITERIA.md`: (a) zero LIVE-event-loss incidents, (b) zero schema-runner-corruption incidents, (c) zero blocking bugs reported by any internal consumer, (d) at least one second internal consumer beyond OASIS, (e) API surface unchanged for ≥30 consecutive days

## Verification (all sub-waves)
- `dotnet build` exit 0, errors 0, warnings ≤17 baseline
- `dotnet test` 618+ unit tests green + new package-suite tests
- `powershell -NoProfile -File scripts/passoff-surrealdb-wave1.ps1` exit 0 (and the 1.5b variant when it lands)
- Zero references to `SurrealDb.Net` in any `.csproj` or `using` after Phase 6
- Zero references to `Core/SurrealDb/Query/` or `analyzers/SurrealQlSafetyAnalyzer/` (old paths) after Phase 6
- Generator round-trip: every `.mermaid` source regenerates byte-identical `.surql` on `oasis-surreal generate`

## Forbidden surface (unchanged from wave-1)
No worker touches `Models/Quest/**`, `Services/QuestDagValidator.cs`,
`Managers/QuestManager.cs`, `Services/Quest/QuestInstantiator.cs`, or any
`quest_run`/`quest_node_execution`/`quest_node`/`quest_edge`/`quest_template`/
`quest_dependency` schema. Reserved for [[quest-temporal-fork-model]].
