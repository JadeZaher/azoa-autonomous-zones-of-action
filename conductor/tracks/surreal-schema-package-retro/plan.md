# Surreal Schema Package Retro — Plan

Source spec: [spec.md](spec.md)  
Audit source: `.omc/audit/w3-surrealdb-toolkit.md`  
Supersedes: `conductor/tracks/surrealdb-schema-source-gen/spec.md`

This track is documentation-only. No builds, no tests, no code changes.

---

## Phase 1 — Create `Persistence/SurrealDb/CONVENTION.md`

- [ ] Check whether `Persistence/SurrealDb/CONVENTION.md` already exists
  (referenced in `conductor/tracks.md` header). If it does, read it first
  to avoid clobbering existing content.
- [ ] Create or extend `CONVENTION.md` to include an **As-built schema pipeline**
  section covering:
  - The three attribute types in
    `packages/Azoa.SurrealDb.Client/Schema/SurrealAttributes.cs`:
    `[SurrealTable]`, `[SurrealIndex]`, `[SurrealRelation]`.
  - A POCO authoring walkthrough: decorating a new C# class, running
    `azoa-surreal generate-from-assembly <path/to/AzoaWebAPI.dll>`, and
    confirming the output `.surql` appears in
    `Persistence/SurrealDb/Generated/Schemas/`.
  - The 26 canonical POCOs in `Persistence/SurrealDb/Models/` as the source
    of truth for all SurrealDB table definitions.
  - The 7 generated flowcharts in `Persistence/SurrealDb/Generated/Flowcharts/`
    as read-only visualisation output (not authoring input).
  - The acceptance gate: `AttributePocoByteEquivalenceTests`.
  - The migration CLI command set: `migrate up | status | dry-run | reset`
    (note `migrate down` is intentionally stubbed).
- [ ] Include a "Do not edit generated files" callout for
  `Persistence/SurrealDb/Generated/` — edits there are overwritten on the next
  `generate-from-assembly` run.

## Phase 2 — Fix stale `RUNBOOK.md` references

- [ ] Read `RUNBOOK.md` §4 in full; identify all references to:
  - `Persistence/SurrealDb/Schemas/source/` (deleted Mermaid source dir)
  - `Azoa.SurrealDb.SourceGen` (removed package)
- [ ] Read `RUNBOOK.md` §8 in full; identify the same stale references.
- [ ] Update §4: replace each stale reference with the C#-first equivalent
  (`Persistence/SurrealDb/Models/`, `azoa-surreal generate-from-assembly`,
  `Azoa.SurrealDb.Schema`).
- [ ] Update §8: same replacements.
- [ ] Do not touch any other RUNBOOK section.

## Phase 3 — Cross-link superseded track + final grep

- [ ] Add a SUPERSEDED banner at the top of
  `conductor/tracks/surrealdb-schema-source-gen/spec.md`:

  ```
  > **SUPERSEDED 2026-06-03.** The Mermaid-first pipeline described here was
  > replaced by the C#-first attribute scanner. See the authoritative
  > as-built reference in
  > [surreal-schema-package-retro/spec.md](../surreal-schema-package-retro/spec.md).
  ```

- [ ] Grep the repo (excluding `conductor/tracks/surrealdb-schema-source-gen/`
  and this retro spec) for:
  - `Schemas/source` — should return zero hits outside historical docs.
  - `Azoa.SurrealDb.SourceGen` — should return zero hits outside historical docs.
  - Resolve any live hits or add a comment explaining why the reference is
    intentionally retained.
- [ ] Move `tracks.md` row for `surreal-schema-package-retro` to `[x]` Shipped.
