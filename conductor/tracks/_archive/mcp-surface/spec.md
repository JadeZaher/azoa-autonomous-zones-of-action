# MCP Surface — Specification

## Goal
Tier 3: expose an MCP server over the SurrealDB-backed infrastructure so AI
agents can query the holon/quest graph and NFT relationships, plus
vector/embedding retrieval. This is the strategic payoff of the SurrealDB
choice — graph-native traversal + an official MCP integration story that
Postgres+jsonb does not provide without bolt-ons.

## Scope
- MCP server exposing read (and scoped write) tools over: quest DAG
  reachability/traversal, holon polyhierarchy traversal, NFT ownership graph,
  avatar-scoped queries.
- Native graph traversal (`->`/`<-`, recursive, shortest-path) rather than
  recursive-CTE equivalents.
- `HNSW` vector index on holon/quest metadata for semantic AI retrieval.
- AuthN/AuthZ: MCP tool calls respect the same avatar scoping + auth as REST;
  no tool bypasses authorization or value-moving idempotency guarantees.

## Constraints
- MCP write tools that touch irreversible chain ops MUST route through the
  same idempotency + state-guard path as REST (`api-safety-hardening` G2) —
  no privileged backdoor.
- Parameterized SurrealQL only (G3) for any tool that interpolates model input.
- Read tools are avatar-scoped; no cross-tenant leakage.

## Acceptance
- MCP server reachable by an MCP host; tools cover quest reachability, holon
  traversal, NFT graph, vector search.
- A representative agent query ("quests reachable from node X respecting
  prerequisites") is a single graph query, not multi-step app code.
- Auth + idempotency invariants verified for any write-capable tool.

## Dependencies
Requires `surrealdb-migration` (graph model + SurrealDB engine). Indirectly
requires `api-safety-hardening` (idempotency invariants honored by write tools).
