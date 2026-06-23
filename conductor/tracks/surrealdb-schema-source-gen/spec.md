# SurrealDB Schema Source Generator — Specification

> **SUPERSEDED 2026-06-03.** The Mermaid-first pipeline described in this spec was
> replaced by the C#-first attribute scanner (`AttributeSchemaScanner` + `SurqlEmitter`
> in `packages/Azoa.SurrealDb.Schema/`). See the authoritative as-built reference in
> [surreal-schema-package-retro/spec.md](../surreal-schema-package-retro/spec.md) and
> the canonical convention doc at [Persistence/SurrealDb/CONVENTION.md](../../../Persistence/SurrealDb/CONVENTION.md).

## Goal
**Tier 1.6** — derive C# domain POCOs, typed `SurrealQuery<T>` builders, and
typed `RecordId<T>` from the `.mermaid` schema sources that
[[surrealdb-client-package]] sub-wave 1.5a already established as the
schema source-of-truth. Eliminate the manual schema↔POCO drift that today
requires hand-written `Models/*.cs` to track every `.mermaid` field
addition; promote field-name and type errors from runtime SurrealQL parse
failures to **compile-time errors**.

Realistic effort: **~1 week**, single owner. New companion package
(fourth in the suite) sitting alongside Client / Schema / Analyzer.

## Why this exists

Sub-wave 1.5a delivered the engine boundary (homebake client + Mermaid
schema source + analyzer) but left the **application↔schema mapping**
hand-maintained:

- Every `.mermaid` field has a corresponding hand-typed `.cs` property.
  When the schema changes, the developer must remember to update both
  files. The `Quest`/`QuestRun` split done by [[quest-temporal-fork-model]]
  needed `[Obsolete]` annotations + a `<NoWarn>CS0618</NoWarn>` block
  precisely because this manual mapping was load-bearing — there was no
  way to compile-time-prove the application matched the schema.
- Today's `SurrealQuery` builder is stringly-typed at every field name:
  `.Where("staus = $s", new { s = "active" })` (typo in `status`) compiles
  fine, fails at runtime as a SurrealQL parse error against the SCHEMAFULL
  table. SRDB0001 cannot catch this — it's a correct *parameterized* query,
  just against a non-existent column.
- `RecordId` is currently a single untyped value class. Cross-table
  references (e.g. `bridge_tx.idempotency_key_record : record<idempotency_key_store>`)
  are not enforced; the application can pass a `wallet:abc` record id into
  a field that expects `idempotency_key_store:...` and the error surfaces
  only on insert, not on assignment.

**The single highest-leverage change** is generating the application's
domain layer from the schema, not maintaining them in parallel.

## Scope (one new package, one source generator, one typed builder companion)

### 1. `Azoa.SurrealDb.SourceGen` (netstandard2.0 Roslyn analyzer assembly)
- `IIncrementalGenerator` reading `*.mermaid` files via the
  `AdditionalFiles` MSBuild item (consumers add `<AdditionalFiles
  Include="Persistence/SurrealDb/Schemas/source/*.mermaid" />` to their
  csproj — wired automatically when the package is referenced).
- Parses `.mermaid` via the existing `Azoa.SurrealDb.Schema.Mermaid`
  parser (the source-gen package depends on the schema package; no
  re-implementation).
- Emits one `partial class` per entity per `.mermaid` file, into the
  namespace declared via an MSBuild property `AzoaSurrealDbModelsNamespace`
  (default: the assembly's root namespace + `.Generated.SurrealDb`).
- Each generated class includes:
  - Strongly-typed properties matching the schema fields, with C# types
    derived from SurrealDB types via a deterministic mapping table
    (`string`→`string`, `int`→`long`, `decimal`→`decimal`, `datetime`→
    `DateTimeOffset`, `option<X>`→`X?`, `record<T>`→`RecordId<T>`,
    `array<X>`→`IReadOnlyList<X>`, `bool`→`bool`, `duration`→`TimeSpan`).
  - A `JsonPropertyName` attribute on every property matching the
    SurrealDB column name (snake_case → PascalCase translation by
    convention; opt-out via `%% @surreal.csharp.property name=...`).
  - A static `SchemaName` property naming the SurrealDB table (e.g.
    `public static string SchemaName => "wallet";`).
  - The class is `partial` so consumers can extend with non-persisted
    helpers without losing source-gen integration.
- **Strict enum policy:** any `string ASSERT INSIDE [...]` field generates
  a C# enum + `JsonStringEnumConverter` opt-in. Round-trips against
  `Azoa.SurrealDb.Client`'s `SurrealJsonOptions.Default` automatically.

### 2. `RecordId<T>` (added to `Azoa.SurrealDb.Client`)
- Generic struct extending the existing `RecordId` shape with a type
  parameter pinning the target table. Implicit conversion to untyped
  `RecordId` for callers that don't care about the type parameter.
- Equality and JSON converter inherit from the base.
- Generated POCOs use `RecordId<TQuest>` for `quest_id` fields, etc.

### 3. `SurrealQuery<T>` (added to `Azoa.SurrealDb.Client.Query`)
- Typed companion to the existing `SurrealQuery`. `SurrealQuery<TWallet>.From()`
  returns the typed builder; `.Where(w => w.Status == WalletStatus.Active)`
  consumes a lambda whose expression tree is translated into SurrealQL by
  a minimal expression visitor (member access → column name, constant →
  parameter binding, equality/inequality/comparison → SurrealQL operator).
- Expression-visitor scope is intentionally minimal: equality, inequality,
  AND/OR composition, `string.IsNullOrEmpty`, `.Contains(value)` for arrays.
  More complex predicates fall back to the stringly-typed `SurrealQuery`
  (no silent feature gap — the visitor throws `NotSupportedException` with
  a clear message and a fallback recipe).
- `.OrderBy(w => w.CreatedAt)`, `.Select(w => new { w.Id, w.Status })` for
  projection, `.Limit/.Start/.Fetch` mirror the untyped builder.

## Acceptance
- New package `packages/Azoa.SurrealDb.SourceGen/` compiles clean; unit
  tests under `tests/Azoa.SurrealDb.SourceGen.Tests/` cover: golden-file
  fixtures (one `.mermaid` input + one expected generated `.cs` per
  aggregate), incremental regeneration determinism (re-run produces
  byte-identical generated source), and the C#-type-mapping table.
- The 7 wave-1 `.mermaid` sources under
  `Persistence/SurrealDb/Schemas/source/` produce 7 generated POCOs in
  `AZOA.WebAPI/Generated/SurrealDb/` (or wherever the source-gen output
  is materialized). Existing hand-written `Models/Wallet.cs`, `Models/BridgeTx.cs`,
  etc. are **deleted** — the generated classes replace them.
- `AZOA.WebAPI.csproj` adds `<PackageReference Include="Azoa.SurrealDb.SourceGen" />`
  (via `ProjectReference` initially; semver-pinned package later).
- `SurrealQuery<T>` companion typed builder ships under
  `Azoa.SurrealDb.Client.Query`; 30+ unit tests covering the
  expression-visitor scope (positive + every `NotSupportedException`
  shape). `SurrealQuery<TWallet>.From().Where(w => w.Status == WalletStatus.Active)`
  emits SurrealQL byte-identical to the untyped
  `SurrealQuery.Of("SELECT * FROM wallet").Where("status = $status", new { status = "active" })`.
- `dotnet build` stays 0 errors / warnings ≤19 baseline.
- Tests stay 803+ green (post-c611d6a baseline); source-gen tests add to
  the count.
- Pass-off gate `scripts/passoff-surrealdb-wave1.ps1` stays exit 0.
- One new pass-off section asserts every wave-1 schema generates a POCO
  whose `SchemaName` matches the table name in the `.mermaid` source —
  drift gate at the application layer mirroring `WaveOneInRepoSyncTests`.

## Out of scope (explicit non-goals — guard against scope creep)
- **No EF Core model regeneration.** EF is being deprecated; touching it
  is wasted effort.
- **No fluent API for `DEFINE TABLE`/`DEFINE FIELD` from C# attributes.**
  Schema is hand-authored in `.mermaid`; reverse direction (POCO → schema)
  would re-introduce the drift this track exists to eliminate.
- **No support for query-result projections beyond named-anonymous-object
  shapes.** `Select(w => new { w.Id, w.Status })` is supported; complex
  projection types (records-with-constructors, init-only properties) are
  follow-up.
- **No async LINQ provider (`IAsyncQueryable`).** The typed builder is a
  builder, not a LINQ provider; the surface is intentionally bounded to
  what an expression visitor can statically translate.
- **No source-gen-driven schema migration generation.** Schema migrations
  remain Mermaid-source-authored; source-gen is for the consumption side.

## Dependencies
- Requires [[surrealdb-client-package]] sub-wave 1.5a complete (DONE,
  tagged `surrealdb-client-package-1.5a-complete` at `88f6b26`).
- Depends on `Azoa.SurrealDb.Schema.Mermaid` parser (existing; no
  changes needed).
- **Blocks**: every `surrealdb-migration` wave-2 adapter task that touches
  the value tables. Those tasks become substantially smaller because the
  POCO mapping is generated.
- **Independent of** [[surrealdb-client-package]] sub-wave 1.5b
  (WebSocket + LIVE) — can ship before, in parallel with, or after.

## What this track absorbs

Nothing — this is a new track born from the code-review architecture
discussion on 2026-05-21. The hand-written `Models/*.cs` files it
replaces were not part of any prior track's deliverable; they predate
the homebake package suite.

## Decision criteria for sub-wave promotion (deferred)

The source generator ships as **internal-feed only** alongside the rest
of the package suite per the same publish-deferral decision locked in
sub-wave 1.5a. No public NuGet publish until at least 90 days post
[[surrealdb-client-package]] sub-wave 1.5b sign-off.
