# MCP Surface -- Plan

## Tasks

1. [x] **2026-05-25** Choose MCP hosting approach for the .NET service (MCP server endpoint / sidecar) and document it -- Mcp/McpServerSetup.cs:14 (in-process at /mcp via ModelContextProtocol.AspNetCore 1.3.0); AZOA.WebAPI.csproj:49-50; CATALOG.md sec 1.
2. [x] **2026-05-25** Define the MCP tool catalog: action-reachability, holon-traverse, nft-ownership-graph, user-scoped-query, vector-search -- Mcp/Tools/ActionReachabilityTool.cs:12, HolonTraverseTool.cs:11, NftOwnershipGraphTool.cs:12, UserScopedQueryTool.cs:19, VectorSearchTool.cs:20; CATALOG.md sec 2.
3. [x] **2026-05-25** Implement graph-traversal tools over SurrealDB (->/<-) -- ActionReachabilityTool.cs:84-85 uses SurrealQuery.Combine for single-round-trip nodes+edges fetch; HolonTraverseTool.cs:83-159 walks parent chain + descendant BFS (F9 follow-up: per-step QueryAsync rather than native recursive traversal).
4. [x] **2026-05-25** Add HNSW vector index on holon/action metadata; implement the vector-search tool -- Persistence/SurrealDb/Schemas/240_hnsw_indexes.surql (DEFINE INDEX hnsw_holon_embedding + hnsw_action_embedding DIMENSION 384 DIST COSINE); embedding field on holon (100_holon.surql:32) + action (150_action.surql:50); VectorSearchTool.cs:20 + EmbeddingProvider.cs:37 (placeholder).
5. [x] **2026-05-25** Enforce user scoping + existing auth on every MCP tool; no privileged bypass -- McpAuthMiddleware.cs:48-60 (claim extraction + 401 fail-closed); every tool reads user_id from ToolCallContext.UserId only (grep for args.GetProperty user_id returns ZERO hits); McpServerSetup.cs:58 RequireAuthorization on /mcp.
6. [DEFERRED] **2026-05-25** Route any write-capable tool touching irreversible chain ops through the api-safety-hardening idempotency + state-guard path -- DEFERRED: no write tools shipped this round. Rationale + re-entry checklist in CATALOG.md sec 3. Shipping write tools without full G2 wiring would have violated spec.md constraint 1.
7. [x] **2026-05-25** Parameterized SurrealQL only for model-input tools (G3); injection tests on tool inputs -- UserScopedQueryTool.cs:62-85 (table + filter allowlists); all SurrealQL via SurrealQuery.Of+WithParam or SurrealQuery.SelectAll routed through SurrealIdentifier.ForTable; McpInjectionSuiteTests.cs (6 hostile payloads through table/filter/query_text + wallet-count invariant).
8. [x] **2026-05-25** MCP integration tests: representative agent queries return correct, scoped results in a single graph query -- tests/AZOA.WebAPI.IntegrationTests/Mcp/McpActionReachabilityTest.cs:159 (LOAD-BEARING: ExecuteAsyncCallCount.Should().Be(1) against the SurrealQuery.Combine path); McpToolCatalogTests.cs (5 happy-path tests, one per tool).
9. [x] **2026-05-25** Auth/leakage tests: cross-tenant query rejected -- tests/AZOA.WebAPI.Tests/Mcp/McpAuthScopingTests.cs (5/5 unit); McpAuthScopingIntegrationTests.cs:178 (cross-tenant), :253 (anonymous -> 401); McpVectorSearchTest.cs (cross-user scoping + no-embeddings empty). Write-tool idempotency check N/A (task 6 deferred).
10. [x] **2026-05-25** dotnet build -- 0 errors, 19 warnings (within repo baseline); 540/540 unit tests green (was 535, +5 from W4 auth unit tests); 13 Category=Mcp integration tests discovered (SkippableFact, runtime gated on E1).
11. [x] **2026-05-25** Document the MCP tool catalog + example agent workflows -- conductor/tracks/mcp-surface/CATALOG.md (5 sections: overview/auth/scoping, per-tool reference with example workflows, write-tool deferral, embedder note, plan-task evidence mapping).

## Sign-off

See [SIGN-OFF.md](SIGN-OFF.md) -- track COMPLETE 2026-05-25.
