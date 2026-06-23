# SurrealQL Drift Detection — Plan

To be filled in when Wave 1 of [[surrealql-toolkit]] lands. Skeleton:

## Phase 1 — Differ core
1. [ ] `SchemaIntrospector` — query `INFO FOR DB`, `INFO FOR TABLE *`
   via `ISurrealConnection`; parse into a `LiveSchemaModel`.
2. [ ] `LocalSchemaModel` — parse `.surql` files into the same shape
   as the live model (canonical form).
3. [ ] `SchemaDiffer` — produce a tree of drift entries.

## Phase 2 — CLI + output
4. [ ] `azoa-surreal drift` subcommand wiring.
5. [ ] Text renderer + JSON renderer.
6. [ ] `.surrealignore` honoring.

## Phase 3 — CI integration
7. [ ] Sample GitHub Actions workflow snippet in docs.
8. [ ] Exit-code contract documented (0 / 1 / 2 = error).
