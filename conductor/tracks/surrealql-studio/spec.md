# SurrealQL Studio — Specification

## Status
Pending. Created 2026-05-27. **Tier 3.** Constituent track of the
[[surrealql-toolkit]] program (Wave 3). See umbrella
[ADR](../surrealql-toolkit/spec.md) for the strategic framing.

## Goal
A read-only **browse + typed query + EXPLAIN UI** for a running
SurrealDB namespace. Avatar-scoped using the same auth pattern as
`mcp-surface`, so it's multi-tenant safe. Prisma Studio analogue, with
graph-native traversal as the differentiator.

## Why
The toolkit is half-blind without a way to see what's in the database
during development. Operators need it for prod forensics
(bridge-tx state inspection, saga step queue depth, etc.). Agents
(via MCP) benefit from the same structured-introspection primitives.

## Acceptance
1. Web UI accessible at `/studio` on the OASIS WebAPI host (gated by
   the same JWT+ApiKey multi-scheme as `/mcp`).
2. Table browser: list tables, paginate rows, click a record to see
   FK-resolved neighbors (graph traversal as native UI motion, not
   SQL).
3. Query playground with typed completion against the Mermaid schema
   + EXPLAIN output for the typed query.
4. Per-table row count + index list visible without writing SurrealQL.
5. Reuses `McpAuthMiddleware`-derived `AvatarId` for row-level
   scoping (no superuser surface even for admins; admins see
   their own data via their own avatar).
6. Read-only — no write operations from studio. Writes go through
   the regular API surface so idempotency + saga + audit are not
   bypassed.

## Approach
- Reuse the existing `oasis-surreal` schema introspection (from
  drift detection) for the metadata layer.
- Single-page app served by an embedded static-files middleware (no
  separate frontend build pipeline; bundle is checked into the
  package).
- Query execution routes through `SurrealQuery<T>` so SRDB0001
  (G3 injection defence) applies to every operator query.
- MCP integration: the studio metadata API (`/studio/api/tables`,
  `/studio/api/explain`) is also exposed as MCP tools so agents
  can introspect with the same auth + scoping.

## Dependencies
- [[surrealql-drift-detection]] for the schema-introspection layer.
- [[mcp-surface]] for the auth + avatar-scoping pattern.

## Out of scope
- Write operations (intentional — keeps the audit + idempotency
  surface intact).
- Schema editing UI (the Mermaid sources are the source of truth;
  editing them in a UI undermines that principle).
- Multi-tenant administrative views (superuser surface).

See [plan.md](plan.md) for phase-by-phase build order (TBD).
