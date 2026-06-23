# SurrealQL db pull — Plan

To be filled in when Wave 2 of [[surrealql-toolkit]] starts. Skeleton:

## Phase 1 — Mermaid emitter
1. [ ] `MermaidEmitter` from `LiveSchemaModel`.
2. [ ] Round-trip test scaffolding (pull → regen → apply → pull).

## Phase 2 — CLI + diff mode
3. [ ] `azoa-surreal db pull` subcommand.
4. [ ] `--diff` mode integration with drift detection.

## Phase 3 — RELATE edge support + index preservation
5. [ ] Edge-table emit honors `forked_from`/`executes` shape.
6. [ ] Index name + uniqueness preserved across round-trip.
