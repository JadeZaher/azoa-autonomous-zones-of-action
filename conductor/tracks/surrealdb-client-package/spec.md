# Homebake SurrealDB Client Package — Specification

## Goal
**Tier 1.5** — replace the pre-1.0 `SurrealDb.Net` SDK + the archived
`surrealdb-migrations` tool + the embedded Roslyn analyzer with a project-owned
**three-package suite** (`Oasis.SurrealDb.Client` / `.Schema` / `.Analyzer`).
Postgres is **fully deprecated** — no fallback ramp. The package suite is the
durable engine boundary. Public NuGet publish is **deferred** until the API is
proven internally for 3–6 months; ship as internal-feed / repo-local package
until then.

Realistic effort: **~2–3 weeks**, sub-divided so adapter work can resume after
1.5a (~week 2).

## Why this exists (convergent evidence — full reports in
`.omc/research/surrealdb-migration-wave1/`)

Wave-1 of [[surrealdb-migration]] shipped against `SurrealDb.Net 0.10.2` and
hit the limits of that dependency. Independent review surfaced the same root
cause from multiple lenses:

- **5 of 8 personas + code-review** named the pre-1.0 SDK as the highest-risk
  single dependency. Only first-party SDK still in 0.x while server is 3.0 GA;
  3 open data-loss bugs (#234 tuple-element silent loss, #246 StackOverflow on
  recursive queries, #185 SemaphoreSlim breakage, #236 CBOR WS deserialization);
  wave-1 already hit one breaking change (`ThrowIfHasErrors`/`GetResults<T>()`
  → `EnsureAllOks`/`GetValues<T>(0)`); Futurist projects .NET 11 incompat
  ~Nov 2026 with ~55% probability.
- **Code-review C4/C5/C6** identified three concrete defects rooted in
  SDK-mediated behavior: `SurrealExecutor.GetValues<T>(0)` silently drops
  multi-statement results (breaks G2 conditional state transitions); enums
  serialize as int via SDK's default JSON config (every insert would fail
  against `TYPE string ASSERT INSIDE [...]`); we cannot pick the JSON
  converter without modifying SDK plumbing.
- **Archaeological persona** found `Odonno/surrealdb-migrations` **archived
  2026-04-11** with no real replacement. **SCHEMAFULL does not auto-backfill**
  — every field change is a 3-step ritual that the spec's G5 plan doesn't
  account for.
- **LIVE-query reliability** (issues #5068 hang, #5014 empty events, #5160
  "CRITICAL Live Queries stop working", #5070 still open) — the server's
  documented contract is "single-node + best-effort-ordered." Wave-2's
  saga-trigger rewrite cannot trust the SDK to layer the at-least-once
  semantics we need; we have to track sequence + replay ourselves anyway.

**The single highest-leverage change** is taking ownership of the engine
boundary so each of the above stops being someone else's bug.

## Scope (three sub-packages, one repo, one owner, one release cadence)

### 1. `Oasis.SurrealDb.Client` (netstandard2.0 + net8.0 multi-target)
- HTTP transport (`POST /sql`) — stable, JSON-in-JSON-out, our default.
- WebSocket transport with RPC framing — only for LIVE; deferred to 1.5b.
- Query builder with strict-by-default param validation:
  `.Where()` / `.OrderBy()` / `.Limit()` / `.Start()` / `.Return()` / `.Fetch()` /
  `.Relate()` / **`.UpdateOnly(table, id).Where(field, value).Set(field, value)`**
  (the G2 conditional-state-transition primitive that does not exist today).
- `SurrealIdentifier` allowlist + reserved-word denylist (closes code-review H4).
- Multi-statement composition with explicit per-statement OK/error/values access
  (closes code-review C5).
- `JsonStringEnumConverter` registered by default; custom converters for
  `RecordId`, `DateTime`, `Duration`, `decimal` (closes code-review C6).
- Explicit transaction shape: `BeginTransactionAsync()` returns a disposable;
  COMMIT on dispose-success, CANCEL on dispose-exception (closes negative-space G-C).
- Connection pool, reconnect, auth, namespace/database scoping.
- LIVE subscription model (1.5b only) with **at-least-once delivery via
  client-side sequence tracking + outbox-replay-on-reconnect**. Documented
  contract: "best-effort-ordered server + at-least-once client."

### 2. `Oasis.SurrealDb.Schema` (netstandard2.0)
- **Mermaid ER as schema source-of-truth.** One `.mermaid` file per aggregate.
  Schema IS the docs (renders natively in GitHub / GitLab / IDE preview).
- Annotation DSL with strict namespacing: `%% @surreal.schemafull`,
  `@surreal.index unique fields=[a,b,c]`, `@surreal.assert "$value INSIDE [...]"`,
  `@surreal.option`, `@surreal.relate`, `@surreal.live`. Parser rejects unknown
  `@surreal.*` directives (no freeform-comment drift).
- Generator: Mermaid → `.surql`. Deterministic output (re-run produces
  byte-identical SQL on unchanged source).
- Migration runner: ordered apply + per-file checksum + `schema_migration`
  table + `--dry-run` (closes B7; replaces archived `surrealdb-migrations`).
- CLI tool (`oasis-surreal`): `migrate {up,down,dry-run,status}`,
  `validate <file>`, `generate <file>`.

### 3. `Oasis.SurrealDb.Analyzer` (netstandard2.0)
- Relocated SRDB0001 from current `analyzers/SurrealQlSafetyAnalyzer/`.
- Ships as a **companion package** — consumers opt in via
  `PackageReference Include="Oasis.SurrealDb.Analyzer" PrivateAssets="all"`.
  Keeps the OASIS-strict opinion from imposing on outside consumers when the
  package eventually publishes.
- Extend SRDB0001 to follow one-hop variable resolution (closes code-review H3
  largest bypass).

## Sub-wave shipment (staged inside the 2–3 week window)

- **1.5a (week 1–2, foundation)** — all three packages: HTTP transport, query
  builder, multi-statement, JSON ser/de, transactions, Mermaid parser +
  generator + migration runner + CLI, analyzer relocation, OASIS integration
  (delete `Core/SurrealDb/Query/`, delete current `analyzers/`, delete
  SDK-pin enforcement, regenerate 7 wave-1 schemas from `.mermaid` sources).
  **Unblocks [[surrealdb-migration]] wave-2 adapter work.**
- **1.5b (week 3+, LIVE)** — WebSocket transport + LIVE subscriptions +
  at-least-once bookkeeping + saga `LiveQuerySagaTrigger`. Saga adopts LIVE
  as opt-in alongside polling; polling remains the default trigger until
  90-day reliability soak passes.

## Acceptance
- All three packages compile clean as project references (multi-target);
  unit tests in `tests/Oasis.SurrealDb.*Tests/` green.
- OASIS replaces `SurrealDb.Net` PackageReference with project references to
  the new packages. `SurrealDb.Net` removed from every `.csproj`.
- `OASIS.WebAPI/Core/SurrealDb/Query/`, `analyzers/SurrealQlSafetyAnalyzer/`,
  `scripts/surrealdb/check-sdk-pin.ps1`, the `VerifySurrealSdkPin` MSBuild
  target, and `tests/OASIS.WebAPI.Tests/Core/SurrealDbSdkPinTests.cs` are all
  **deleted** — their work now lives in the packages. G4 narrows to
  "pin our own package via `Directory.Build.props`" (trivial; we own semver).
- The 7 wave-1 `.surql` files are re-authored as `.mermaid` sources under
  `Persistence/SurrealDb/Schemas/source/`; generator output matches or
  improves on the originals (byte-similar except for stylistic normalization).
- `scripts/passoff-surrealdb-wave1.ps1` stays green end-to-end. Sections 2,
  3, 7 update to assert package-pin + new analyzer wiring.
- `dotnet build` stays 0 errors / ≤17 warnings baseline. 618+ unit tests stay
  100% green; package-suite tests add to the count.
- Sub-wave 1.5b only: at-least-once delivery test suite passes; subscription
  bookkeeping survives reconnect gap > 5s with zero event loss against the
  recovery test harness.

## Out of scope (explicit non-goals — guard against scope creep)
- **No public NuGet publish.** Build with package boundary discipline (semver,
  changelog, public-API doc-comments) but ship to internal feed / repo-local
  only. Publishing decision deferred 3–6 months after 1.5b lands.
- **No open-sourcing repo split.** Packages live under `/packages/` in the
  OASIS repo for now. Repo extraction happens at-or-after publish decision.
- **No support for SurrealDB clustering / TiKV / FoundationDB backends.**
  Single-node SurrealKV only (matches OASIS deployment shape).
- **No SurrealQL parser.** Builder emits SQL strings; we don't parse the
  language. (Analyzer detects string-construction patterns, not semantics.)
- **No automatic schema diffing / migration generation from model classes.**
  Schema is hand-authored in `.mermaid`; migration runner only applies files.
- **No replacement for the saga / outbox abstractions** — `LiveQuerySagaTrigger`
  is one new `ISagaTrigger` implementation in [[durable-saga-orchestration]],
  not a re-architecture.

## Dependencies
- Requires [[architecture-decoupling]] (seam exists).
- Requires [[api-safety-hardening]] (G2 idempotency semantics must survive
  the engine-layer swap).
- **Blocks** [[surrealdb-migration]] wave-2 adapter tasks (5, 6, 7, 8, 8a,
  8b). Quest-schema work in [[surrealdb-migration]] tasks 3 (quest portion),
  9, 10 is independently gated on [[quest-temporal-fork-model]].
- Independent of [[quest-temporal-fork-model]] — they can ship in parallel.

## What this track absorbs from elsewhere

These items previously lived in [[surrealdb-migration]] / the strategic
synthesis hand-off and **move here**:

| Was | Becomes |
|---|---|
| `surrealdb-migration` task 4 (parameterized query layer) | Phase 3 of this track |
| `surrealdb-migration` task 6 (conditional state transitions) | Query-builder `.UpdateOnly().Where().Set()` (Phase 3) |
| `surrealdb-migration` task 8a (LIVE-query saga trigger) | Phase 8–9 (sub-wave 1.5b) |
| `surrealdb-migration` task 14 (gated migration job) | Phase 4 (Mermaid + runner + CLI) |
| Strategic-review B4 (multi-statement swallow) | Phase 2–3 (executor design) |
| Strategic-review B5 (enum-as-int serialization) | Phase 2 (JSON config default) |
| Strategic-review B6 (LIVE saga trigger reliability) | Phase 8–9 (client-side at-least-once) |
| Strategic-review B7 (archived migration tool) | Phase 4 (Mermaid runner) |
| Strategic-review B8 (server-pin / SDK-compat coupling) | **Dissolves** — own client decouples server-pin from SDK |

Strategic-review **A9 (Postgres CI shadow) is dropped** — Postgres is fully
deprecated; no fallback ramp.
