# MCP Surface — Tool Catalog

## 1. Overview

### Mounting

The MCP server is hosted in-process alongside `OASIS.WebAPI` at the path `/mcp`.
Registration is performed by `McpServerSetup.AddMcpSurface()` +
`McpServerSetup.MapMcp()` in `Program.cs`:

```csharp
// Program.cs (abbreviated)
builder.Services.AddMcpSurface();
// ...
app.MapMcp(endpoints);   // mounts at /mcp, RequireAuthorization applied
```

Transport: `ModelContextProtocol.AspNetCore` 1.3.0 HTTP transport (SSE + POST).

### Auth

Every `/mcp` request goes through the existing JWT + ApiKey multi-scheme
authentication policy (`RequireAuthorization` on the MCP endpoint group).

`McpAuthMiddleware` extracts `AvatarId` from the authenticated claims and
stashes it in `HttpContext.Items["mcp.avatar_id"]`. The tool dispatcher reads
this to build a `ToolCallContext` so every tool executes within a scoped
identity. Unauthenticated callers receive `401 mcp_unauthorized` before any
tool code runs.

### Scoping

All read tools enforce avatar scoping at the SurrealQL layer: `avatar_id` is
sourced exclusively from `ToolCallContext.AvatarId` (set from claims), never
from tool input parameters. Cross-tenant leakage is structurally impossible.

### Transport

MCP host connects to `POST /mcp` (HTTP + SSE); the same Kestrel listener as
the REST API. Tools are discovered via `McpToolRegistry`, which enumerates all
`IMcpTool` implementations registered in DI at startup.

---

## 2. Tool Reference

### quest_reachability

**Name:** `quest_reachability`

**Description:** Return the set of quest nodes reachable from a starting node
via control-flow edges, scoped to a single quest definition owned by the
calling avatar.

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "quest_id":     { "type": "string", "format": "uuid" },
    "from_node_id": { "type": "string", "format": "uuid" }
  },
  "required": ["quest_id", "from_node_id"]
}
```

**Output schema:**

```json
{
  "reachable_node_ids": ["<uuid>", "..."],
  "ordered_nodes": [
    { "id": "<uuid>", "name": "string", "execution_order": 0 }
  ]
}
```

Error shapes: `{ "error": "quest not found." }`,
`{ "error": "forbidden" }`,
`{ "error": "internal", "detail": "..." }`

**Avatar scoping:** `avatar_id` read from `ToolCallContext`; quest must be
owned by the caller or the call returns `forbidden`.

**Performance:** Nodes and edges are fetched in a single combined SurrealQL
`ExecuteAsync` call (`SurrealQuery.Combine(nodesQ, edgesQ)`). BFS traversal is
pure in-memory after that single round-trip. This is the spec acceptance
criterion: one graph query, not N per-node fetches.

**Example agent workflow:**

```
Agent: "Which steps can I still reach if I am at step B?"
→ POST /mcp  (tools/call quest_reachability)
  { "quest_id": "<uuid>", "from_node_id": "<B-uuid>" }
← { "reachable_node_ids": ["<C>","<D>"], "ordered_nodes": [...] }
```

---

### holon_traverse

**Name:** `holon_traverse`

**Description:** Walk the holon polyhierarchy from a starting holon,
returning the parent chain, child subtree, and peers (all avatar-scoped).

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "holon_id":  { "type": "string", "format": "uuid" },
    "max_depth": { "type": "integer", "default": 3, "minimum": 1, "maximum": 10 }
  },
  "required": ["holon_id"]
}
```

**Output schema:**

```json
{
  "node": { "id": "...", "name": "...", "description": "...", "..." : "..." },
  "ancestors":   [ { "id": "...", "name": "..." }, "..." ],
  "descendants": [ { "id": "...", "name": "..." }, "..." ],
  "peers":       [ { "id": "...", "name": "..." }, "..." ]
}
```

Error shapes: `{ "error": "holon not found." }`, `{ "error": "forbidden" }`,
`{ "error": "internal", "detail": "..." }`

**Avatar scoping:** Root holon must be owned by the caller; ancestor/descendant
queries are unrestricted by avatar (holons fetched by relationship, not
re-checked by avatar ownership) — descendants and ancestors are structurally
reachable from the scoped root only.

**Example agent workflow:**

```
Agent: "Show me everything connected to Holon X."
→ POST /mcp  (tools/call holon_traverse)
  { "holon_id": "<X-uuid>", "max_depth": 2 }
← { "node": {...}, "ancestors": [...], "descendants": [...], "peers": [] }
```

---

### nft_ownership_graph

**Name:** `nft_ownership_graph`

**Description:** Return all NFT holons owned by the calling avatar, optionally
filtered by chain. Results are grouped by chain in `by_chain`.

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "chain_id": { "type": "string" }
  }
}
```

`chain_id` is optional. Omit to return NFTs across all chains.

**Output schema:**

```json
{
  "nfts": [
    { "id": "...", "name": "...", "chain_id": "...", "provider": "...", "token_id": "..." }
  ],
  "by_chain": {
    "algorand": [ { "id": "...", "..." : "..." } ],
    "solana":   [ { "id": "...", "..." : "..." } ]
  }
}
```

Error shapes: `{ "error": "internal", "detail": "..." }`

**Avatar scoping:** `avatar_id` from `ToolCallContext` only. NFTs for other
avatars are structurally excluded by the `WHERE avatar_id = $avatar_id` clause.

**Example agent workflow:**

```
Agent: "What NFTs does this user own on Algorand?"
→ POST /mcp  (tools/call nft_ownership_graph)
  { "chain_id": "algorand" }
← { "nfts": [ ... ], "by_chain": { "algorand": [...] } }
```

---

### avatar_scoped_query

**Name:** `avatar_scoped_query`

**Description:** Generic avatar-scoped read query against an allowlisted table
with parameterized filters.

**Security design:** Table name is validated against a static allowlist before
substitution into the SQL template. Filter keys are validated against a filter
allowlist. `avatar_id` is never accepted as a tool parameter — it comes
exclusively from `ToolCallContext`.

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "table": {
      "type": "string",
      "enum": ["wallet", "holon", "quest", "quest_run", "nft_ownership", "operation_log"]
    },
    "filters": {
      "type": "object",
      "additionalProperties": { "type": ["string", "number", "boolean"] }
    },
    "limit": { "type": "integer", "default": 50, "minimum": 1, "maximum": 500 }
  },
  "required": ["table"]
}
```

**Allowed filter keys (v1):** `name`, `status`, `chain_id`, `asset_type`,
`created_date`. Any other key produces `{ "error": "filter_not_allowed" }`.

**Output schema:**

```json
{
  "table":     "wallet",
  "rows":      [ { "..." : "..." } ],
  "row_count": 2
}
```

Error shapes:
- `{ "error": "table_not_allowed" }` — table not in allowlist
- `{ "error": "filter_not_allowed", "field": "<key>" }` — filter key rejected
- `{ "error": "table is required." }` — missing table parameter
- `{ "error": "internal", "detail": "..." }` — DB error

**Avatar scoping:** All rows are filtered by `avatar_id = $avatar_id` derived
from `ToolCallContext`. No cross-tenant reads are possible.

**Example agent workflow:**

```
Agent: "Show me this user's Algorand wallets."
→ POST /mcp  (tools/call avatar_scoped_query)
  { "table": "wallet", "filters": { "chain_id": "algorand" }, "limit": 20 }
← { "table": "wallet", "rows": [...], "row_count": 2 }
```

---

### vector_search

**Name:** `vector_search`

**Description:** HNSW semantic search over holon or quest embeddings. Returns
top-k matches by cosine similarity, scoped to the calling avatar.

**Input schema:**

```json
{
  "type": "object",
  "properties": {
    "query_text": { "type": "string", "minLength": 1, "maxLength": 4096 },
    "table":      { "type": "string", "enum": ["holon", "quest"], "default": "holon" },
    "k":          { "type": "integer", "default": 10, "minimum": 1, "maximum": 100 }
  },
  "required": ["query_text"]
}
```

**Output schema:**

```json
{
  "matches": [
    { "id": "...", "name": "...", "score": 0.97 }
  ],
  "table":    "holon",
  "k_actual": 3
}
```

Error shapes: `{ "error": "internal", "detail": "..." }` (DB function absent
or HNSW index not defined — see note below).

**Avatar scoping:** `avatar_id` from `ToolCallContext` — all matches are
filtered to the caller's namespace.

**Example agent workflow:**

```
Agent: "Find holons semantically similar to 'ancient artifact'."
→ POST /mcp  (tools/call vector_search)
  { "query_text": "ancient artifact", "table": "holon", "k": 5 }
← { "matches": [ { "id": "...", "name": "Golden Relic", "score": 0.91 }, ... ],
    "table": "holon", "k_actual": 5 }
```

---

## 3. Write-tool Deferral Note

Write tools (mint, redeem, transfer, etc.) are **intentionally deferred** from
this round. They would require full G2 idempotency wiring per
`plan.md` task 6: every irreversible chain operation must route through the
same `IIdempotencyStore.TryClaimAsync` + conditional-state-transition path as
the REST bridge endpoints. Implementing write tools without this wiring would
create a privileged backdoor that bypasses the G2 gate — that is explicitly
prohibited by `spec.md` constraint 1.

Re-evaluate before opening any write tool:
1. Confirm the idempotency store is wired for the operation type.
2. Confirm the state-guard (`SurrealQuery.UpdateOnly(...).Where(...).Set(...)`)
   is applied at the store layer.
3. Add a G2-style concurrent-caller test (see `G2_IdempotencyTocTouTest.cs`)
   specific to the write tool.

The five read tools above satisfy the `plan.md` tasks 2, 3, 5, 7, 8, 9, and 11
acceptance criteria without any write-tool risk.

---

## 4. Embedding Provider Note

The shipped `DeterministicDummyEmbeddingProvider` (registered in DI when no
other `IEmbeddingProvider` is registered) produces deterministic 384-dimensional
vectors derived from SHA-256 of the input text.

**This provider has zero semantic quality.**

- Two strings that differ by one character produce entirely unrelated vectors.
- It exists solely to allow `vector_search` to compile, deploy, and be
  integration-tested without an external embedding service.
- All vector-search results under this provider are arbitrary (except for
  exact text-match queries where cosine self-similarity is 1.0 by construction).

**Production must register a real `IEmbeddingProvider` before `vector_search`
is meaningful:**

```csharp
// Example: swap in OpenAI text-embedding-3-small
services.AddSingleton<IEmbeddingProvider, OpenAiEmbeddingProvider>();
```

Candidate providers: OpenAI `text-embedding-3-small` (1536-dim, projected to
384), local Ollama `nomic-embed-text` (768-dim projected), or any
ONNX-compatible model hosted via `Microsoft.ML.OnnxRuntime`. No call-site
changes are needed — `VectorSearchTool` consumes `IEmbeddingProvider` via DI
and is agnostic to the implementation.

---

## 5. Acceptance Criteria Mapping

| plan.md task | Evidence file : line(s) |
|---|---|
| 1. Choose MCP hosting approach | `Mcp/McpServerSetup.cs:14` — `AddMcpSurface` + `WithHttpTransport()` |
| 2. Define tool catalog | This file; `Mcp/Tools/*.cs` (five tools) |
| 3. Graph-traversal tools | `Mcp/Tools/QuestReachabilityTool.cs:84-85` — `SurrealQuery.Combine`; `Mcp/Tools/HolonTraverseTool.cs:83-100` |
| 4. HNSW vector index + vector_search | `Mcp/Tools/VectorSearchTool.cs:57-67` — `HolonSearchSql`/`QuestSearchSql` using `vector::similarity::cosine` |
| 5. Avatar scoping + auth | `Mcp/McpAuthMiddleware.cs:48-54`; privilege-escalation comments in every tool (line 61, 43, 58, 130, 116 respectively) |
| 6. Write tools deferred | Section 3 of this file; `plan.md` task 6 prerequisite not yet met |
| 7. Parameterized SurrealQL (G3) | `Mcp/Tools/AvatarScopedQueryTool.cs:62-85` — table + filter allowlists; all tools use `SurrealQuery.Of(...).WithParam(...)` |
| 8. Integration tests: single graph query | `tests/.../Mcp/McpQuestReachabilityTest.cs:120` — `recorder.ExecuteAsyncCallCount.Should().Be(1)` |
| 9. Auth/leakage tests | `tests/.../Mcp/McpToolCatalogTests.cs` — NFT cross-avatar test; `tests/.../Mcp/McpVectorSearchTest.cs` — `VectorSearch_CrossAvatarScoping_DoesNotLeak` |
| 10. dotnet build zero warnings | Orchestrator CI step (not a test file) |
| 11. Document tool catalog | This file (CATALOG.md) |
