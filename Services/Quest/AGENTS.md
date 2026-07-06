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

## §output-binding: $from config bindings (quest-value-engine-expressiveness F1)

**Syntax:** any scalar config field may be the object `{"$from":"path"}` (exactly
one key, string value). Extra keys = error; array-element position = error (V1).

**Path grammar (closed — shared with GateCheck via `GatePath.TryParse`):**
- `upstream.<nodeName>.<jsonPath>` — resolves against the named incoming-edge
  source node's `QuestNodeExecution.Output` JSON.
- `holon.<guid>.<field>` — resolves against the holon's current lifecycle state
  (typed fields + Metadata overlay, same as `GateCheckNodeHandler.HolonStateJson`).
- `reads.` root is GateCheck-local only and is NOT valid in $from paths (V12).

**Runtime resolution** runs as a pre-pass in `QuestConfigBindingResolver.TryResolveAsync`
at BOTH dispatch seams:
- Legacy: `QuestManager.ExecuteAsync` node loop (before handler dispatch).
- Durable: `QuestNodeStepHandler` (before `handler.HandleAsync`).

The node's `Config` property is temporarily swapped to the resolved JSON, then
restored in a `finally` block. Handlers always see fully-resolved config.

**Fail closed:** missing path / non-owned holon / malformed binding ⇒ the node
`Fails` with a descriptive error naming the path. Never a silent default, never
an exception escaping into the engine.

**Definition-time validation** in `QuestNodeConfigRegistry.Validate`:
1. `QuestConfigBindingResolver.FindAndValidateBindings` — structural check (extra
   keys, array-element position, grammar via `GatePath.TryParse`, GUID-shaped
   holon ids).
2. At **publish time** (directUpstreamNames non-null): `upstream.<name>` must
   name a direct incoming-edge source of that node.
3. **Shadow round-trip** (V1): strip `$from` property values from a shadow copy,
   then run the existing strict `StrictOptions` deserialization — absent (stripped)
   bound fields get defaults, while typos in non-bound fields still fail.

## §fractionalization: Bridge / Back nodes (final-hardening D1)

The flagship ArdaNova asset-fractionalization flow is **assembly over shipped
rails** — no new value primitive. Two Tier-2 nodes wrap the real Phase-B bridge:

- **`Bridge`** → `ICrossChainBridgeService.InitiateBridgeAsync` (lock/bridge an
  asset cross-chain). Output carries the bridge transaction `Id` + `LockTxHash`.
- **`Back`** → `ICrossChainBridgeService.ReverseBridgeAsync` (burn wrapped on
  target, release original on source). Its `BridgeTransactionId` is normally
  `$from`-bound to the upstream Bridge node's output `id`.

**Security discipline (reviewer-checked):**
- **No fabricated success.** Both route through the real bridge, which is
  fail-closed for unimplemented paths (Solana lock/burn) and gated by the
  `RealValueEnabled` kill switch. A service error maps to a Failed node.
- **Idempotency.** Each node seeds `{runId}:{nodeId}` as the client idempotency
  key; the bridge service avatar-namespaces it, so a re-evaluated node dedupes to
  ONE irreversible lock/burn — no double-bridge / double-burn.
- **Actor from run context.** The config body carries no avatar. Bridge stamps the
  row to `context.Quest.AvatarId`; Back passes it as `callerAvatarId` so the
  reverse is IDOR-scoped to that avatar's own rows (mismatch ⇒ "not found").
- **`RequiresChainCapability == true`** for both — enforced at the dispatch seam
  and pinned by `ChainCapabilityFlagInvariantTests.Tier2`.

**Peg boundary (the load-bearing rule): the node MOVES value only; the tenant
computes the peg.** AZOA holds NO peg/valuation/collateral state. A "pegged
project token" is a plain `FungibleTokenCreate` fungible; it is pegged only in the
tenant's ledger. The canonical fractionalization template ends with an **`Emit`**
node that hands the peg/valuation config to the tenant (`project.token.pegged`);
redemption + peg maintenance are the tenant's. Do NOT add peg/pricing logic to any
node. See `frontend/src/components/quest-builder/presets.ts`
(`fractionalize-canonical-lifecycle` + the three demonstrator presets).

## §skip-semantics

See `Services/Quest/Workflow/AGENTS.md §skip-semantics` for the durable-path
divergence (no skip seam in `QuestNodeStepHandler`; saga compensation instead).
Follow-up track: `durable-skip-propagation`.

## §quest-webhook-emit

**final-hardening F3 — the generic `quest.emit` webhook.** `EmitNodeHandler` still
serializes its tenant-shaped payload straight to `QuestNodeExecution.Output` (the
authoritative settlement surface — no fiat/payout math in AZOA). ON TOP of that, when
the run carries an `ActingTenantId` AND an `IQuestWebhookEmitter` is registered, the
handler ALSO enqueues a best-effort webhook outbox event via `QuestWebhookEmitter`.

- **Optional dependency.** The emitter is a nullable ctor param (default `null`) so the
  handler stays constructable with zero args and the pure-passthrough path is preserved
  when no tenant/emitter is present. `EmitNodeConfig.EventType` (optional) names the
  event; it defaults to `quest.emit`.
- **Never fails the node.** `QuestWebhookEmitter.EmitAsync` only writes an outbox row
  (no outbound HTTP — that is the delivery worker's job) and swallows every fault,
  including store exceptions. A webhook plumbing failure must not fail the `Emit` node.
- **Delivery + security** live in `Services/Webhooks/AGENTS.md §quest-webhook`: a
  parallel outbox to the consent one that reuses the shared registration store + SSRF
  guard + HMAC signer, so a tenant's ONE endpoint receives both consent and quest.emit
  events.
