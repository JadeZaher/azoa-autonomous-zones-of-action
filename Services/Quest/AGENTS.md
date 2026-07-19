---
type: agent-notes
scope: Services/Quest
created: 2026-07-02
---

# Services/Quest — Agent Notes

## §acting-avatar: whose identity a node runs under (C1/H1)

**Rule: every identity side-effect resolves against `context.ActingAvatarId`
(the RUNNER), NEVER `context.Quest.AvatarId` (the quest owner/author).**

The two are equal when the owner runs their own quest. They DIVERGE on a
marketplace run — a non-owner starting another avatar's PUBLIC quest. The
`QuestRun` row is stamped `run.AvatarId = runner`; both executors thread that
runner id end-to-end as the acting avatar:

- `QuestManager.ExecuteAsync` (legacy sync) and `ExecuteNodeAsync` (single-node)
  pass `run.AvatarId` into the binding resolver, `ChainCapabilityGate`, and the
  `QuestNodeExecutionContext(actingAvatarId: run.AvatarId, …)` ctor.
- `QuestNodeStepHandler.ExecuteAsync` (durable saga) loads the run once and uses
  `run.AvatarId` for the same three seams.

A handler that reads the acting avatar to choose whose wallet/key/holon/asset to
touch MUST read `context.ActingAvatarId`. The ONLY `context.Quest.AvatarId`
usages that survive are non-identity (provenance/attribution to the author) —
there are currently none in handler dispatch. Holon *output projection* of a
holon's own `holon.AvatarId` field (GateCheck/resolver `HolonStateJson`) is not
an identity decision and is left as-is.

Regression coverage: `Quest/QuestActingAvatarAuthorizationTests.cs`.

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

- `configSchema` / `inputSchema` JSON-Schema fields are a named follow-up (AC-4c).
  Do not add them now. (The `outputSchema` half of AC-4c is now un-deferred — see
  §output-schema below.)
- Handler unit tests exercising malformed config → Failed are in
  `tests/AZOA.WebAPI.Tests/Quest/QuestNodeConfigSafeDeserializeTests.cs`.

## §output-schema: authoritative per-node-type OUTPUT catalog

`QuestNodeOutputSchema` (`Services/Quest/QuestNodeOutputSchema.cs`) declares, for
EVERY `QuestNodeType`, the top-level shape of the JSON its handler serializes to
`QuestNodeExecution.Output`. It is the counterpart a `$from`-binding validator uses
to prove predecessor references (presence + type) — the `outputSchema` half of the
previously-deferred AC-4c item (§node-config "Explicitly unenforced", line ~40).

**Model.** `NodeOutputShape` is one of three cases:
- **Known** — non-empty `Fields` (field→`OutputFieldType`) describing the top-level
  object. A `$from` whose first path segment is not a declared field fails presence.
- **None** — empty `Fields`, `Open == false`: a pure side-effect node with no
  readable output. Any `$from` into it MUST fail presence. (No current node is None;
  the case exists for future side-effect-only handlers.)
- **Open** — `Open == true`: free-form / dynamic-key output the validator can't
  type-check (`Condition` passes config through, `ComposeOutputs` is a
  `{nodeName: outputJson}` map, `Emit` is a tenant-shaped payload). Admit by ancestry;
  skip type-checking.

**Field naming is PascalCase, not camelCase.** `QuestNodeJson.Options` sets no
`PropertyNamingPolicy` (only `PropertyNameCaseInsensitive`), so System.Text.Json
emits the C# property names verbatim. A camelCased catalog entry would break the
validator (a wrong field name silently defeats presence checking). Enums serialize as
**numbers** (no `JsonStringEnumConverter` on those options); `Guid`/`DateTime`
serialize as strings.

**Top-level only (by design).** Most handlers serialize the WHOLE `AZOAResult<T>`, so
their catalogued shape is the wrapper — `IsError`/`Message`/`Result`/`Detail` — with
the domain payload nested under `Result` (Object, or Array for the list-returning
query/traversal handlers, or a Boolean/Number scalar for delete/move/propagate).
`Bridge`/`Back` are the exception: they serialize `r.Result` DIRECTLY, so their
catalogued shape is the flat `BridgeTransactionResult`. `GateCheck` is a fixed literal
`{"pass":true}`. Deep validation into `Result.<field>` is a follow-up; the catalog is
faithful at the top level.

The persisted bridge `IdempotencyKey` is `[JsonIgnore]` and therefore deliberately
absent from the Bridge/Back flat output catalog. It remains available only to the
bridge store, replay, and reconciliation paths.

**Sync contract (load-bearing).** The catalog is only as trustworthy as its match to
the handlers — a wrong field name silently defeats the validator. When a handler's
`Output` changes (a new serialized field, a switch between whole-`AZOAResult<T>` and
`r.Result`, or a manager return-type change), update `QuestNodeOutputSchema` in the
same change. `GetShape` throws `NotSupportedException` for any unmapped `QuestNodeType`
(same guard as `QuestNodeConfigRegistry.GetConfigType`), so a newly-added node type
can't silently skip its schema — but it can't catch a stale field list. Treat the
catalog as part of the handler's contract surface.

## §quest-operation-output-projection

Operation-producing handlers serialize the strict BlockchainOperationResponse
allowlist, and the fungible-token handler serializes FungibleTokenResultResponse.
These are the supported durable $from contracts: operation id, lifecycle, account
ids, and public transaction reference remain bindable; raw operation Parameters,
idempotency, initiator, tenant, signing, and provider fields never become workflow
output. QuestController also projects both single-node and execution-state reads
through safe DTOs, so legacy persisted output is redacted at the HTTP boundary while
the unmodified durable QuestNodeExecution.Output still serves internal binding.
Malformed legacy JSON fails closed to a null public output; exception Detail is
never a public node-output field.

## §output-binding: $from config bindings (quest-value-engine-expressiveness F1)

**Syntax:** any scalar config field may be the object `{"$from":"path"}` (exactly
one key, string value). Extra keys = error; array-element position = error (V1).

**Path grammar (closed — shared with GateCheck via `GatePath.TryParse`):**
- `upstream.<nodeName>.<jsonPath>` — resolves against the named **direct
  incoming-edge** source node's `QuestNodeExecution.Output` JSON.
- `run.<nodeName>.<jsonPath>` — **run-scoped**: resolves against ANY prior node's
  `QuestNodeExecution.Output` in the run by name, not just direct-edge
  predecessors. Same shape as `upstream.`; scope is built over ALL run executions.
- `holon.<guid>.<field>` — resolves against the holon's current lifecycle state
  (typed fields + Metadata overlay, same as `GateCheckNodeHandler.HolonStateJson`).
- `reads.` root is GateCheck-local only and is NOT valid in $from paths (V12).

### §gate-predicate: run. in GateCheck predicates

`run.<nodeName>.<field>` is valid in GateCheck **gate predicates**, not just $from
bindings. `GateCheckNodeHandler.BuildScopeAsync` no longer hand-copies the scope
loops — it builds the `upstream.` and `run.` roots by calling the SHARED
`internal static` helpers `QuestConfigBindingResolver.BuildUpstreamScope`/
`BuildRunScope` directly, then merges its own gate-local `reads.`/`holon.` keys on
top. This is the single source of truth: a gate predicate and a `$from` binding
resolve the SAME root, so divergence is impossible by construction. (Previously the
handler kept an inlined copy of both loops, and the copy used a case-SENSITIVE
`Dictionary` while the resolver used `StringComparer.OrdinalIgnoreCase` — so a
`run.Mint.x` predicate against a node named `mint` resolved in `$from` but failed
CLOSED in a gate. The handler's merged scope dict is now `OrdinalIgnoreCase` too, so
all four roots look up uniformly.) The helpers preserve exact semantics:
empty-`Output` skip, last-writer-wins (publish rejects duplicate node names),
permissive JSON parse (unparseable output skipped). Both executors thread the same
raw executions into `QuestNodeExecutionContext.UpstreamExecutions`/`AllRunExecutions`
(`QuestManager.ExecuteAsync` → `executionsByNode`; `QuestNodeStepHandler` →
`allRunExecutions`). A `run.<node>` with no execution yet resolves to no scope key,
so the evaluator throws unknown-path → gate fails CLOSED (same as a missing
`upstream.`). WHY: `GatePath.ValidRoots` accepts `run` for the shared grammar, so
the runtime scope must accept it too — a parseable, publish-passing predicate that
fails closed at runtime with "unknown path" was a trap (grammar and runtime MUST
accept the same roots).

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
2. At **publish time**: `upstream.<name>` (directUpstreamNames non-null) must
   name a direct incoming-edge source of that node; `run.<name>` (allNodeNames
   non-null) must name a node that exists ANYWHERE in the quest — `run.` is
   intentionally broader than `upstream.` and does NOT require a direct edge.
   (Ancestry/type executability checking is a separate validator's job.)
3. **Shadow round-trip** (V1): strip `$from` property values from a shadow copy,
   then run the existing strict `StrictOptions` deserialization — absent (stripped)
   bound fields get defaults, while typos in non-bound fields still fail.

## §executability-validation: publish-time $from executability gate

`QuestDagExecutabilityValidator` (`IQuestDagExecutabilityValidator`) rejects a DAG
at **publish only** when a node's `$from` binding input would NOT be satisfiable at
runtime — the "a DAG shouldn't validate if a node's execution will fail" rule. It
runs in `QuestManager.PublishAsync` AFTER the structural DAG, transition, and
per-node-config validators pass (so structural errors surface first), reusing
`DagValidationResult`'s error shape. `ExecuteAsync`/`StartWorkflowRun` are NOT
additionally gated — publish is the single strictest gate, matching the existing
design. It complements the grammar/existence check in
`QuestNodeConfigRegistry.Validate` (§output-binding step 2): that proves the named
node EXISTS; this proves the binding will actually RESOLVE.

For each `$from` path in each node's config (collected via
`QuestConfigBindingResolver.FindAndValidateBindings`, parsed with `GatePath`), three
checks run. `holon.<guid>.<field>` paths SKIP all three (holon state is dynamic —
kept runtime fail-closed).

**A. Reachability / guaranteed-ancestor (roots `upstream`, `run`):**
- The referenced node (segment 1) must exist, else error.
- `upstream.<name>`: must be a **direct incoming-edge source** (that IS the runtime
  scope for `upstream.`). Non-predecessor ⇒ error.
- `run.<name>`: must be a **guaranteed ancestor** — present on EVERY path from an
  entry to the consumer. Computed by an iterative **dominator dataflow**:
  `dom(n) = {n} ∪ (⋂ dom(p) for p in preds(n))`, entries seeded `{entry}`, then self
  stripped so the set is proper ancestors. **`preds` is built from CONTROL edges
  ONLY** — Conditional/OnFailure arms fire only when a predicate passes / a source
  Failed, so a node reachable solely via such an arm is NOT guaranteed to have run
  and is rejected ("not guaranteed to have executed"). (Note: `upstream.`'s
  direct-predecessor set is built from ALL incoming edges, mirroring the runtime
  `BuildUpstreamScope`, which does not filter by edge type — only the dominator set
  is Control-only.) Small DAGs converge in a couple of passes.

**Preconditions (checked first, fail-closed):**
- **Node-name uniqueness** — `run.`/`upstream.` addressing is name-keyed, so duplicate
  names would let the validator (first-match) and runtime resolver (last-writer-wins)
  disagree on which node a binding resolves to. Duplicate names are rejected up-front.
- **Size cap** — `MaxNodes`/`MaxEdges` bound the O(N²·E) dominator pass so a hostile
  mega-graph can't turn publish into a CPU-exhaustion primitive.

**B. Field presence (against `QuestNodeOutputSchema.GetShape`):**
- `Open` shape ⇒ SKIP presence/type (opaque; ancestry alone gates it).
- `None` shape (Open==false, empty Fields) ⇒ ANY path is an error ("produces no
  readable output"). (No live node type maps to None today; the branch is for
  future pure-side-effect nodes.)
- Known shape: the first post-name segment must match a declared field
  **case-insensitively** (the runtime resolver uses `PropertyNameCaseInsensitive`,
  so `upstream.bridge.id` matches declared `Id`). No match ⇒ error.
- **Deep paths STOP at the first level:** if the matched field is `Object`, `Array`,
  or `Unknown`, admit any further segments unchecked (the deep shape is not
  statically known — `upstream.x.Result.Id` is admitted because `Result` is Object).

**C. Best-effort scalar type match (provable mismatch only):**
- Only when the path is exactly `root.name.field` with `field` a KNOWN scalar
  (String/Number/Boolean) AND the consuming config field is a KNOWN scalar. The
  consumer field's CLR type is found by reflecting the config DTO
  (`QuestNodeConfigRegistry.GetConfigType`) along the config-property path that
  holds the binding, mapped to `OutputFieldType` (string/Guid/DateTime→String,
  int/long/decimal/…/enum→Number, bool→Boolean, IEnumerable→Array, else Object).
- Fires ONLY on a provable scalar-vs-scalar mismatch. If either side is
  Object/Array/Unknown/Open, or the path is deep, or the property can't be resolved
  ⇒ SKIP. Never false-positives.

## §fractionalization: Bridge / Back nodes (final-hardening D1)

The flagship ArdaNova asset-fractionalization flow is **assembly over shipped
rails** — no new value primitive. Two Tier-2 nodes wrap the real Phase-B bridge:

- **`Bridge`** → `ICrossChainBridgeService.InitiateBridgeAsync` (lock/bridge an
  asset cross-chain). Output carries the bridge transaction `Id` + `LockTxHash`.
- **`Back`** → `ICrossChainBridgeService.ReverseBridgeAsync` (burn wrapped on
  target, release original on source). Its `BridgeTransactionId` is normally
  `$from`-bound to the upstream Bridge node's output `id`.

**Security discipline (reviewer-checked):**
- **No fabricated success.** Both route through the real bridge, which requires
  providers to advertise the complete lock/mint/burn/release lifecycle and is
  gated by the `RealValueEnabled` kill switch. Real providers remain fail-closed;
  only the simulated route currently advertises the full capability. A service
  error maps to a Failed node.
- **Idempotency.** Each node seeds `{runId}:{nodeId}` as the client idempotency
  key; the bridge service avatar-namespaces it, so a re-evaluated node dedupes to
  ONE irreversible lock/burn — no double-bridge / double-burn.
- **Actor from run context.** The config body carries no avatar. Bridge stamps the
  row to `context.ActingAvatarId` (the RUNNER — see §acting-avatar); Back passes it
  as `callerAvatarId` so the reverse is IDOR-scoped to that avatar's own rows
  (mismatch ⇒ "not found").
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

## §economic-manifest — value-moving-node disclosure for marketplace runs

`QuestEconomicManifestBuilder.Build(quest, registry, versionHash)` produces the
`QuestEconomicManifest` (`Models/Quest/QuestEconomicManifest.cs`) that the run-start
consent gate and the `PreviewRunAsync` surface both consume. See
`Managers/AGENTS.md §economic-consent` for how the manager wires it.

- **What counts as economic.** `IsEconomicNode` returns true when the node's
  registered handler declares `RequiresChainCapability` (the authoritative signal —
  Swap/Grant/Transfer/Refund/FungibleTokenCreate/Bridge/Back), OR the node type is in
  the explicit `EconomicNodeTypes` set (adds the NFT mint/transfer/burn types; also
  defence-in-depth if a type ever ships without a registered handler). Union of the
  two, so a value-mover can never slip past both checks.
- **Ordering.** Nodes are listed in `ExecutionOrder` (run order) so the caller reads
  the manifest top-to-bottom exactly as the run would fire them. The caller runs the
  DAG validator first (which assigns `ExecutionOrder`); `PreviewRunAsync` does so
  itself.
- **Best-effort disclosure, never throws.** `ExtractDisclosure` recursively scans the
  node's raw config JSON for the first destination-ish key
  (`recipientAddress`/`targetChain`/`toAddress`/…) and amount-ish key
  (`amount`/`total`/`quantity`), case-insensitive. An unparseable config yields
  `(null, null)` but the node STILL appears in the manifest — its *presence* is the
  load-bearing disclosure; its params are a bonus. Never fail manifest building on a
  malformed config.
- **Hash binding.** The manifest carries the quest's `PublishedVersionHash` so a
  disclosure is tied to the exact graph revision
  (`Managers/AGENTS.md §published-version-hash`).

## §refund-linkage — a Refund may only reverse a real upstream debit (HIGH)

`RefundNodeHandler` used to be a second, attacker-directed `Transfer`: it moved
`RefundNodeConfig.NftId` to `RefundNodeConfig.Request.TargetAvatarId`, both
creator-authored, with no check that any debit had actually occurred. On a public
marketplace run that let a creator publish a benign-looking "Refund" node that
drained the runner's asset out of order.

The guard: a Refund now REQUIRES a succeeded `Transfer` node earlier in the SAME
run whose `NftId` equals the refund's `NftId` (`TryFindReversibleTransfer`, walking
`context.AllRunExecutions` — the run-wide map, NOT `UpstreamExecutions` — mapped back
to `context.Quest.Nodes` for the node type). AllRunExecutions is load-bearing: the
debit may sit any number of hops upstream (e.g. `Transfer → GateCheck → Refund`), and
`UpstreamExecutions` holds only DIRECT-edge predecessors, so scanning it would fail
closed on a legitimate multi-hop refund. No matching debit ⇒ fail closed
(`NoUpstreamDebitMessage`). The reversal is DERIVED from that debit, not from config:
recipient is forced to `context.ActingAvatarId` (the run actor = the Transfer's
original sender) and the wallet is reused from the debit; `cfg.Request` contributes
only the memo. So `cfg.Request.TargetAvatarId` can no longer redirect the asset.

The soulbound-check read is an UNSCOPED trusted read (`GetAsync(nftId,
callerAvatarId: null)`), NOT runner-scoped. By the time a Refund runs, the upstream
Transfer has already reassigned the NFT's `holon.AvatarId` to the recipient
(`NftManager.TransferAsync`), so a runner-scoped read would fail the owner-or-public
gate and abort every legitimate refund. The drain-vector guard above — not the read's
ownership scope — is what authorizes the reversal.

Deferred follow-ups (not blocking):
- **Grant reversal.** A `Grant` is a mint-to-actor with no pre-existing `NftId`/prior
  owner, so there is no specific-asset debit to reverse; Grant is intentionally NOT
  matched. A real Grant-refund needs the minted asset id read back from the Grant
  execution output (not structured for that today).
- **`RefundsNodeId` disambiguation.** Matching by `NftId` is unambiguous only when a
  single upstream Transfer moved that asset. An optional `Guid? RefundsNodeId` on
  `RefundNodeConfig` would pin the refund to a specific Transfer node (and let the
  publish-time executability validator verify the link statically).

## QuestInstantiator — duplicate parameter keys are rejected

`InstantiateAsync` parses user-supplied `parametersJson`. System.Text.Json
preserves duplicate object keys, so `{"x":1,"x":2}` used to throw
`ArgumentException` out of `ToDictionary` and surface as a 500. Both the
validate path (`EnsureNoDuplicateKeys`, called from `ValidateParameters`) and
the dictionary build (`BuildParameters`) now reject duplicate keys outright
with a clean `InvalidOperationException` — one consistent rule so the
first-match `TryGetProperty` validation and the dictionary never disagree.
