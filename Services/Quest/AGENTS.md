---
type: agent-notes
scope: Services/Quest
created: 2026-07-02
---

# Services/Quest — Agent Notes

## §node-config: Safe config deserialization

**Rule: every handler MUST use `QuestNodeConfig.TryDeserialize<T>` instead of
`JsonSerializer.Deserialize<T>(...)!`.**

Rationale: a raw `!`-forced deserialize throws `NullReferenceException` or
`JsonException` on malformed stored config, killing the whole execution loop
rather than marking the single node Failed. `TryDeserialize` catches both and
returns `false` with a descriptive error; the caller returns
`QuestNodeResults.Fail(cfgError)` — the node is marked Failed, skip-propagation
handles downstream nodes, and the engine continues.

### Adding a new handler

1. Declare a typed config DTO in `Models/Quest/NodeConfigs.cs` (required if the
   node takes any structured config; null = config-free).
2. Register the new `QuestNodeType` value in `QuestNodeConfigRegistry._map`
   (entry is mandatory — missing entries throw `NotSupportedException` at
   start-up, caught by the registry exhaustiveness test).
3. In `HandleAsync`: replace any `Deserialize<T>(...)!` with:
   ```csharp
   if (!QuestNodeConfig.TryDeserialize<MyConfig>(context.Node.Config,
           nameof(QuestNodeType.MyType), out var cfg, out var cfgError))
       return QuestNodeResults.Fail(cfgError);
   ```
4. Validation fires automatically at `AddNodeAsync`/`UpdateNodeAsync`
   (definition time) and at publish gate (`PublishAsync` calls
   `ValidateNodeConfigs`). No extra wiring needed.

### Explicitly unenforced

- `configSchema` / `inputSchema` / `outputSchema` JSON-Schema fields are a
  named follow-up (AC-4c). Do not add them now.
- Handler unit tests exercising malformed config → Failed are in
  `tests/AZOA.WebAPI.Tests/Quest/QuestNodeConfigSafeDeserializeTests.cs`.

## §skip-semantics

See `Services/Quest/Workflow/AGENTS.md §skip-semantics` for the durable-path
divergence (no skip seam in `QuestNodeStepHandler`; saga compensation instead).
Follow-up track: `durable-skip-propagation`.
