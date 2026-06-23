# Archived Tracks

Tracks here were retired without shipping. They remain in-tree so the
design rationale survives, but they are no longer part of the catalog
(conductor/tracks.md).

## 2026-06-10 — surrealql-toolkit family

The public-toolkit framing ("Prisma for SurrealQL") was abandoned per
RUNBOOK §1 (decided 2026-05-27). The AZOA-internal needs are met by
the existing `Azoa.SurrealDb.Client/Schema/Analyzer` packages plus the
C#-first attribute-driven schema authoring that landed 2026-06-03. The
five tracks below were never started and are kept here as a record.

- surrealql-toolkit/ — umbrella + DESIGN-mermaid-portfolio.md (historical)
- surrealql-drift-detection/ — `azoa-surreal drift` design
- surrealql-db-pull/ — `azoa-surreal db pull` design
- surrealql-studio/ — `/studio` UI design
- surrealql-toolkit-packaging/ — public NuGet packaging design

`data-backfill-migrations/` remains valid as a standalone track and is
NOT archived.
