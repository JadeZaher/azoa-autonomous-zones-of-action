# Mcp — module notes

## §record-id-binding (MCP tool SurrealQL queries)

SurrealDB stores record ids and `record<...>` link columns as record values, NOT
bare-hex strings. Two consequences the MCP tools MUST honour:

- A record id (`SELECT ... WHERE id = $x`) only matches when `$x` is bound as the
  `table:hex` link form. A bare-hex string never equals `holon:hex` — the query
  silently returns zero rows.
- A `record<avatar>` / `record<holon>` / `record<quest>` link column
  (`avatar_id`, `parent_holon_id`, `quest_id`, `source_node_id`, ...) likewise
  only matches a `table:hex` link value. Comparing against bare hex returns zero
  rows; the value also reads back OUT as `"avatar:hex"`, so an ownership check
  against bare hex is always unequal.

Therefore every id / link comparison in `Tools/*.cs` binds
`SurrealLink.ToLink("<table>", SurrealId.ToSurrealId(guid))` (the `table:hex`
form), never `SurrealId.ToSurrealId(guid)` alone. This mirrors the store layer
(`Providers/Stores/Surreal/*`), which uses `SurrealLink.ToLink` on write and
`type::record($_t, $_id)` for id-scoped CRUD.

Verified live against SurrealDB 3.1.4 via the real `DefaultSurrealExecutor`:
`WHERE id = $barehex` → 0 rows; `WHERE id = $"holon:hex"` → 1 row.

### peer_holon_ids is a native array

`holon.peer_holon_ids` is `array<string>` of bare-hex ids. `HolonTraverseTool`
reads it as a `JsonElement?` array and binds each id via `SurrealLink.ToLink`.
Reading it into a `string` POCO field throws (the JSON is an array, not a string).

### Bug history

These bindings were wrong in the original Phase-H tools (all five compared bare
hex), so every MCP integration tool query returned empty / "not found" /
"forbidden". Root-caused and fixed 2026-07-06; regression-covered by
`tests/AZOA.WebAPI.IntegrationTests/Mcp/McpToolCatalogTests.cs` +
`McpVectorSearchTest.cs`.
