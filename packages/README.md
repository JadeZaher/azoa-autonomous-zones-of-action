# AZOA Homebake SurrealDB Package Suite

The three packages in this directory replace the pre-1.0 `SurrealDb.Net`
SDK, the archived `surrealdb-migrations` tool, and the embedded
`analyzers/SurrealQlSafetyAnalyzer/` project. They are owned by this
repository, ship as project references inside AZOA, and are pinned via a
single MSBuild property so a "version drift" is a local edit (caught by
the wave-1 pass-off gate) rather than a NuGet auto-upgrade.

## Boundary -- one repo, three sub-packages

| Package | Target | Role |
|---|---|---|
| `Azoa.SurrealDb.Client` | `netstandard2.0;net8.0` | HTTP transport (`POST /sql`), parameterized `SurrealQuery` builder + `SurrealIdentifier` reserved-word denylist, multi-statement composition, explicit `BeginTransactionAsync()` shape, JSON converters with `JsonStringEnumConverter` registered by default, connection pool + jittered retry. WebSocket + LIVE subscriptions are reserved for sub-wave 1.5b. |
| `Azoa.SurrealDb.Schema` | `netstandard2.0;net8.0` (CLI on net8.0) | Mermaid ER parser, `.surql` generator, migration runner with `schema_migration` checksum table, `azoa-surreal` CLI (`migrate up\|status\|dry-run`, `generate <file>`, `validate <file>`). |
| `Azoa.SurrealDb.Analyzer` | `netstandard2.0` | Roslyn analyzer SRDB0001 (Error severity) -- bans string-interpolated / concatenated SurrealQL outside the safe layer. Includes one-hop variable resolution to close the largest code-review H3 bypass. |

## Version property -- single source of truth

```xml
<!-- Directory.Build.props (repo root) -->
<PropertyGroup>
  <AzoaSurrealDbVersion>0.1.0</AzoaSurrealDbVersion>
</PropertyGroup>
```

Every package's csproj has its own `<Version>` element; the wave-1
pass-off gate (`scripts/passoff-surrealdb-wave1.ps1` section 2 + 3)
asserts that all three match `<AzoaSurrealDbVersion>` and refuses the
build if they drift. Bump in lockstep.

## Internal-only status -- public NuGet publish deferred 3-6 months

These packages are **not** published to nuget.org today. They live in
this repo only and are consumed via `<ProjectReference>` (see snippet
below). Both `Azoa.SurrealDb.Client.csproj` and
`Azoa.SurrealDb.Schema.csproj` set `IsPackable=true` so a future
`dotnet pack` produces internal-feed-ready `.nupkg`s, but
`IsPublishable=false` is set on every package to guard against an
accidental `dotnet nuget push`. The publish decision is deliberately
deferred -- it gates on:

1. Zero LIVE-event-loss incidents during the 90-day internal soak (1.5b).
2. Zero schema-runner-corruption incidents.
3. Zero blocking bugs reported by any internal consumer.
4. At least one internal consumer in addition to AZOA.
5. Public API surface unchanged for >= 30 consecutive days.

Criteria detail lives in
`conductor/tracks/surrealdb-client-package/plan.md` Phase 11 task 54
(`packages/PUBLISH-CRITERIA.md` will be authored at sub-wave 1.5b sign-off).

## Consumer wiring -- AZOA.WebAPI.csproj

```xml
<!-- Normal compile-time reference for the client + JSON + query builder. -->
<ItemGroup>
  <ProjectReference
    Include="packages\Azoa.SurrealDb.Client\Azoa.SurrealDb.Client.csproj" />
  <!-- Analyzer-only reference: loaded into Roslyn by OutputItemType="Analyzer";
       NOT exposed as a compile-time library by ReferenceOutputAssembly="false". -->
  <ProjectReference
    Include="packages\Azoa.SurrealDb.Analyzer\Azoa.SurrealDb.Analyzer.csproj"
    OutputItemType="Analyzer"
    ReferenceOutputAssembly="false" />
</ItemGroup>
```

DI registration is provided by the
`AZOA.WebAPI/Extensions/SurrealDbServiceCollectionExtensions.cs`
helper:

```csharp
// Program.cs
using AZOA.WebAPI.Extensions;

builder.Services.AddAzoaSurrealDb(builder.Configuration);
// Binds Azoa.SurrealDb.Client.SurrealConnectionOptions from the
// "SurrealDb" configuration section by default. Registers an HTTP
// ISurrealConnection per scope.
```

## Schemas -- C# POCOs are the source of truth

The 26 decorated POCOs at `Persistence/SurrealDb/Models/<Name>.cs` are
the canonical authoring surface. The generated `.surql` siblings under
`Persistence/SurrealDb/Generated/Schemas/<name>.surql` are build
artifacts (regeneration is byte-identical thanks to the deterministic
emitter). The `AttributePocoByteEquivalenceTests` gate asserts the two
stay in sync; CI fails red on any drift.

See `Persistence/SurrealDb/CONVENTION.md` for the authoritative
convention doc covering naming, attribute usage, and field ordering.

To regenerate after a POCO edit:

```
azoa-surreal generate-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
```

## Tests

- `tests/Azoa.SurrealDb.Client.Tests/` -- 147 tests (HTTP transport, JSON,
  transaction, pool, query builder, identifier denylist).
- `tests/Azoa.SurrealDb.Schema.Tests/` -- 49 tests (Mermaid parser, surql
  emitter golden files, migration runner, CLI args, in-repo sync gate).
- `tests/Azoa.SurrealDb.Analyzer.Tests/` -- 15 tests (SRDB0001 + the
  one-hop bypass closure).

The AZOA-level wave-1 pass-off gate
(`scripts/passoff-surrealdb-wave1.ps1`) runs all three suites as part of
its section 4 (unit-tests-green) check; any package-suite failure fails
the gate.
