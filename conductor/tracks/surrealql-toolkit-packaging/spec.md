# SurrealQL Toolkit Packaging — Specification

## Status
Pending. Created 2026-05-27. **Tier 4.** Constituent track of the
[[surrealql-toolkit]] program (Wave 4). See umbrella
[ADR](../surrealql-toolkit/spec.md) for the strategic framing.

## Goal
Publish the `oasis-surreal` toolkit (and its supporting packages —
`Oasis.SurrealDb.Client`, `.Schema`, `.Analyzer`, `.SourceGen`) to a
public NuGet feed as a coherent product. Gated on Waves 1-3
stabilizing + OASIS dogfooding the toolkit in prod for ≥3 months.

## Why
The toolkit's strategic value (per the umbrella ADR) is realized only
if external developers can adopt it. Public packaging is the moment
the toolkit becomes a product. Until then it's a private utility set.

## Acceptance
1. All five packages published to NuGet under the same version line:
   `Oasis.SurrealDb.Client`, `.Schema`, `.Analyzer`, `.SourceGen`,
   and the `oasis-surreal` `dotnet tool` global package.
2. Docs site (mkdocs or similar) hosted with:
   - Quickstart (5-minute "hello SurrealDB" walkthrough)
   - CLI reference (every subcommand documented)
   - Annotation reference (`@surreal.*` directives)
   - Migration guide (for existing Prisma users)
   - Graph-native cookbook (RELATE, recursive traversal, HNSW)
3. Sample repositories (≥3): minimal CLI usage, ASP.NET + DI usage,
   graph-heavy domain (social-network-like) usage.
4. Versioning policy documented: semver, deprecation window, breaking-
   change SLA (e.g. 6 months notice on annotation removal).
5. Telemetry: opt-in OpenTelemetry exporter so usage signal flows
   without leaking customer data. Off by default; documented as
   strictly opt-in.
6. CI matrix covers .NET 8 + .NET 9 + (when released) .NET 10;
   SurrealDB 1.5.x + 2.x.
7. ≥1 external user adopts before end of year (success indicator,
   not gate).

## Approach
- Authoritative package metadata in each `.csproj` (Authors, License,
  Description, PackageProjectUrl, RepositoryUrl, Tags).
- Public repo (forked / separate from oasis-sleek) so external users
  don't see OASIS-internal noise.
- Sample repos hosted under the same org.

## Dependencies
- All Waves 1-3 constituent tracks stable (no breaking changes in
  ≥3 months).
- OASIS dogfooding signal: public packaging is a no-op if OASIS itself
  isn't running the toolkit in prod.

## Out of scope
- Paid support / SLA — open-source, community-supported.
- Marketplace of plugins / community-authored generators — could grow
  organically; not part of v1.

See [plan.md](plan.md) for phase-by-phase build order (TBD).
