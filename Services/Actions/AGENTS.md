---
type: agent-notes
scope: Services/Actions
created: 2026-07-02
---

# Services/Actions — Agent Notes

## §acting-user: whose identity a node runs under (C1/H1)

**Rule: every identity side-effect resolves against `context.ActingUserId`
(the RUNNER), NEVER `context.ActionDefinition.UserId` (the action owner/author).**

The two are equal when the owner runs their own action. They DIVERGE on a
marketplace run — a non-owner starting another user's PUBLIC action. The
`ActionRun` row is stamped `run.UserId = runner`; both executors thread that
runner id end-to-end as the acting user:

- `ActionManager.ExecuteAsync` (legacy sync) and `ExecuteNodeAsync` (single-node)
  pass `run.UserId` into the binding resolver, `ValueRailCapabilityGate`, and the
  `ActionNodeExecutionContext(actingUserId: run.UserId, …)` ctor.
- `ActionNodeStepHandler.ExecuteAsync` (durable saga) loads the run once and uses
  `run.UserId` for the same three seams.

A handler that reads the acting user to choose whose wallet/key/holon/asset to
touch MUST read `context.ActingUserId`. The ONLY `context.ActionDefinition.UserId`
usages that survive are non-identity (provenance/attribution to the author) —
there are currently none in handler dispatch. Holon *output projection* of a
holon's own `holon.UserId` field (GateCheck/resolver `HolonStateJson`) is not
an identity decision and is left as-is.

Regression coverage: `tests/AZOA.WebAPI.Tests/Actions/ActionActingUserAuthorizationTests.cs`.

## §node-config: Safe config deserialization

**Rule: every handler MUST use `ActionNodeConfig.TryDeserialize<T>` instead of
`JsonSerializer.Deserialize<T>(...)!`.**

Rationale: a raw `!`-forced deserialize throws `NullReferenceException` or
`JsonException` on malformed stored config, killing the whole execution loop
rather than marking the single node Failed. `TryDeserialize` catches both and
returns `false` with a descriptive error; the caller returns
`ActionNodeResults.Fail(cfgError)` — the node is marked Failed, skip-propagation
handles downstream nodes, and the engine continues.

### Adding a new handler

1. Declare a typed config DTO in `Models/Actions/NodeConfigs.cs` (required if the
   node takes any structured config; null = config-free).
2. Register the new `ActionNodeType` value in `ActionNodeConfigRegistry._map`
   (entry is mandatory — missing entries throw `NotSupportedException` at
   start-up, caught by the registry exhaustiveness test).
3. In `HandleAsync`: replace any `Deserialize<T>(...)!` with:
   ```csharp
   if (!ActionNodeConfig.TryDeserialize<MyConfig>(context.Node.Config,
           nameof(ActionNodeType.MyType), out var cfg, out var cfgError))
       return ActionNodeResults.Fail(cfgError);
   ```
4. Validation fires automatically at `AddNodeAsync`/`UpdateNodeAsync`
   (definition time) and at publish gate (`PublishAsync` calls
   `ValidateNodeConfigs`). No extra wiring needed.

### Explicitly unenforced

- `configSchema` / `inputSchema` JSON-Schema fields are a named follow-up (AC-4c).
  Do not add them now. (The `outputSchema` half of AC-4c is now un-deferred — see
  §output-schema below.)
- Handler unit tests exercising malformed config → Failed are in
  `tests/AZOA.WebAPI.Tests/Actions/ActionNodeConfigSafeDeserializeTests.cs`.

## §output-schema: authoritative per-node-type OUTPUT catalog

`ActionNodeOutputSchema` (`Services/Actions/ActionNodeOutputSchema.cs`) declares, for
EVERY `ActionNodeType`, the top-level shape of the JSON its handler serializes to
`ActionNodeExecution.Output`. It is the counterpart a `$from`-binding validator uses
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

**Field naming is PascalCase, not camelCase.** `ActionNodeJson.Options` sets no
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
`r.Result`, or a manager return-type change), update `ActionNodeOutputSchema` in the
same change. `GetShape` throws `NotSupportedException` for any unmapped `ActionNodeType`
(same guard as `ActionNodeConfigRegistry.GetConfigType`), so a newly-added node type
can't silently skip its schema — but it can't catch a stale field list. Treat the
catalog as part of the handler's contract surface.

## §action-operation-output-projection

Operation-producing handlers serialize the strict BlockchainOperationResponse
allowlist, and the fungible-token handler serializes FungibleTokenResultResponse.
These are the supported durable $from contracts: operation id, lifecycle, account
ids, and public transaction reference remain bindable; raw operation Parameters,
idempotency, initiator, tenant, signing, and provider fields never become workflow
output. ActionController also projects both single-node and execution-state reads
through safe DTOs, so legacy persisted output is redacted at the HTTP boundary while
the unmodified durable ActionNodeExecution.Output still serves internal binding.
Malformed legacy JSON fails closed to a null public output; exception Detail is
never a public node-output field.

## §output-binding: $from config bindings (action-value-engine-expressiveness F1)

**Syntax:** any scalar config field may be the object `{"$from":"path"}` (exactly
one key, string value). Extra keys = error; array-element position = error (V1).

**Path grammar (closed — shared with GateCheck via `GatePath.TryParse`):**
- `upstream.<nodeName>.<jsonPath>` — resolves against the named **direct
  incoming-edge** source node's `ActionNodeExecution.Output` JSON.
- `run.<nodeName>.<jsonPath>` — **run-scoped**: resolves against ANY prior node's
  `ActionNodeExecution.Output` in the run by name, not just direct-edge
  predecessors. Same shape as `upstream.`; scope is built over ALL run executions.
- `holon.<guid>.<field>` — resolves against the holon's current lifecycle state
  (typed fields + Metadata overlay, same as `GateCheckNodeHandler.HolonStateJson`).
- `reads.` root is GateCheck-local only and is NOT valid in $from paths (V12).

### §gate-predicate: run. in GateCheck predicates

`run.<nodeName>.<field>` is valid in GateCheck **gate predicates**, not just $from
bindings. `GateCheckNodeHandler.BuildScopeAsync` no longer hand-copies the scope
loops — it builds the `upstream.` and `run.` roots by calling the SHARED
`internal static` helpers `ActionConfigBindingResolver.BuildUpstreamScope`/
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
raw executions into `ActionNodeExecutionContext.UpstreamExecutions`/`AllRunExecutions`
(`ActionManager.ExecuteAsync` → `executionsByNode`; `ActionNodeStepHandler` →
`allRunExecutions`). A `run.<node>` with no execution yet resolves to no scope key,
so the evaluator throws unknown-path → gate fails CLOSED (same as a missing
`upstream.`). WHY: `GatePath.ValidRoots` accepts `run` for the shared grammar, so
the runtime scope must accept it too — a parseable, publish-passing predicate that
fails closed at runtime with "unknown path" was a trap (grammar and runtime MUST
accept the same roots).

**Runtime resolution** runs as a pre-pass in `ActionConfigBindingResolver.TryResolveAsync`
at BOTH dispatch seams:
- Legacy: `ActionManager.ExecuteAsync` node loop (before handler dispatch).
- Durable: `ActionNodeStepHandler` (before `handler.HandleAsync`).

The node's `Config` property is temporarily swapped to the resolved JSON, then
restored in a `finally` block. Handlers always see fully-resolved config.

**Fail closed:** missing path / non-owned holon / malformed binding ⇒ the node
`Fails` with a descriptive error naming the path. Never a silent default, never
an exception escaping into the engine.

**Definition-time validation** in `ActionNodeConfigRegistry.Validate`:
1. `ActionConfigBindingResolver.FindAndValidateBindings` — structural check (extra
   keys, array-element position, grammar via `GatePath.TryParse`, GUID-shaped
   holon ids).
2. At **publish time**: `upstream.<name>` (directUpstreamNames non-null) must
   name a direct incoming-edge source of that node; `run.<name>` (allNodeNames
   non-null) must name a node that exists ANYWHERE in the action — `run.` is
   intentionally broader than `upstream.` and does NOT require a direct edge.
   (Ancestry/type executability checking is a separate validator's job.)
3. **Shadow round-trip** (V1): strip `$from` property values from a shadow copy,
   then run the existing strict `StrictOptions` deserialization — absent (stripped)
   bound fields get defaults, while typos in non-bound fields still fail.

## §executability-validation: publish-time $from executability gate

`ActionDagExecutabilityValidator` (`IActionDagExecutabilityValidator`) rejects a DAG
at **publish only** when a node's `$from` binding input would NOT be satisfiable at
runtime — the "a DAG shouldn't validate if a node's execution will fail" rule. It
runs in `ActionManager.PublishAsync` AFTER the structural DAG, transition, and
per-node-config validators pass (so structural errors surface first), reusing
`DagValidationResult`'s error shape. `ExecuteAsync`/`StartWorkflowRun` are NOT
additionally gated — publish is the single strictest gate, matching the existing
design. It complements the grammar/existence check in
`ActionNodeConfigRegistry.Validate` (§output-binding step 2): that proves the named
node EXISTS; this proves the binding will actually RESOLVE.

For each `$from` path in each node's config (collected via
`ActionConfigBindingResolver.FindAndValidateBindings`, parsed with `GatePath`), three
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

**B. Field presence (against `ActionNodeOutputSchema.GetShape`):**
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
  (`ActionNodeConfigRegistry.GetConfigType`) along the config-property path that
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
  key; the bridge service user-namespaces it, so a re-evaluated node dedupes to
  ONE irreversible lock/burn — no double-bridge / double-burn.
- **Actor from run context.** The config body carries no user. Bridge stamps the
  row to `context.ActingUserId` (the RUNNER — see §acting-user); Back passes it
  as `callerUserId` so the reverse is IDOR-scoped to that user's own rows
  (mismatch ⇒ "not found").
- **`RequiresChainCapability == true`** for both — enforced at the dispatch seam
  and pinned by `ChainCapabilityFlagInvariantTests.Tier2`.

**Peg boundary (the load-bearing rule): the node MOVES value only; the tenant
computes the peg.** AZOA holds NO peg/valuation/collateral state. A "pegged
project token" is a plain `FungibleTokenCreate` fungible; it is pegged only in the
tenant's ledger. The canonical fractionalization template ends with an **`Emit`**
node that hands the peg/valuation config to the tenant (`project.token.pegged`);
redemption + peg maintenance are the tenant's. Do NOT add peg/pricing logic to any
node. See `frontend/src/components/action-builder/presets.ts`
(`fractionalize-canonical-lifecycle` + the three demonstrator presets).

## §skip-semantics

See `Services/Actions/Workflow/AGENTS.md §skip-semantics` for the durable-path
divergence (no skip seam in `ActionNodeStepHandler`; saga compensation instead).
Follow-up track: `durable-skip-propagation`.

## §action-webhook-emit

Emit requires durable workflow admission. `EmitNodeHandler` delegates to
`ActionEmitService`, which recovers the deterministic receipt and original event,
or retries atomic acceptance under the frozen action and original credential.
An accepted tenant event pins registration identity, destination revision, body
and body hash. Missing registration remains pending; malformed frozen config
has a durable failed receipt. Infrastructure failures reach the workflow boundary.

The output is the accepted payload. Acceptance proves a durable delivery obligation,
not HTTP delivery or receiver business settlement. Non-tenant Emit has a local
receipt without an event. No optional best-effort producer remains. Delivery and
retry use `Services/Webhooks/AGENTS.md`; accepted effects cannot be recalled.
## §economic-manifest — value-moving-node disclosure for marketplace runs

`ActionEconomicManifestBuilder.Build(action, registry, versionHash)` produces the
`ActionEconomicManifest` (`Models/Actions/ActionEconomicManifest.cs`) that the run-start
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
- **Hash binding.** The manifest carries the action's `PublishedVersionHash` so a
  disclosure is tied to the exact graph revision
  (`Managers/AGENTS.md §published-version-hash`).

## §refund-linkage — a Refund may only reverse a real upstream debit (HIGH)

`RefundNodeHandler` used to be a second, attacker-directed `Transfer`: it moved
`RefundNodeConfig.NftId` to `RefundNodeConfig.Request.TargetUserId`, both
creator-authored, with no check that any debit had actually occurred. On a public
marketplace run that let a creator publish a benign-looking "Refund" node that
drained the runner's asset out of order.

The guard: a Refund now REQUIRES a succeeded `Transfer` node earlier in the SAME
run whose `NftId` equals the refund's `NftId` (`TryFindReversibleTransfer`, walking
`context.AllRunExecutions` — the run-wide map, NOT `UpstreamExecutions` — mapped back
to `context.ActionDefinition.Nodes` for the node type). AllRunExecutions is load-bearing: the
debit may sit any number of hops upstream (e.g. `Transfer → GateCheck → Refund`), and
`UpstreamExecutions` holds only DIRECT-edge predecessors, so scanning it would fail
closed on a legitimate multi-hop refund. No matching debit ⇒ fail closed
(`NoUpstreamDebitMessage`). The reversal is DERIVED from that debit, not from config:
recipient is forced to `context.ActingUserId` (the run actor = the Transfer's
original sender) and the wallet is reused from the debit; `cfg.Request` contributes
only the memo. So `cfg.Request.TargetUserId` can no longer redirect the asset.

The soulbound-check read is an UNSCOPED trusted read (`GetAsync(nftId,
callerUserId: null)`), NOT runner-scoped. By the time a Refund runs, the upstream
Transfer has already reassigned the NFT's `holon.UserId` to the recipient
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

## ActionInstantiator — duplicate parameter keys are rejected

`InstantiateAsync` parses user-supplied `parametersJson`. System.Text.Json
preserves duplicate object keys, so `{"x":1,"x":2}` used to throw
`ArgumentException` out of `ToDictionary` and surface as a 500. Both the
validate path (`EnsureNoDuplicateKeys`, called from `ValidateParameters`) and
the dictionary build (`BuildParameters`) now reject duplicate keys outright
with a clean `InvalidOperationException` — one consistent rule so the
first-match `TryGetProperty` validation and the dictionary never disagree.
## §rail-capability — the pre-execution value-rail capability gate (D1 + D9)

`ValueRailCapabilityGate` decides whether a node that declares `RequiresChainCapability` may
run. Both dispatch seams call it before `HandleAsync`: the legacy synchronous `ActionManager`
executor and the durable `ActionNodeStepHandler` saga step. Enforcing it in exactly one
helper, called from both, is what stops the durable path becoming a bypass.

**What it replaced.** A static `HasWalletBoundAsync` that returned true if the actor had ANY
wallet bound. That is wrong in both directions as soon as a second rail exists:

- an Algorand-wallet holder PASSES a fiat payout they cannot receive;
- a fiat-only user — precisely the persona who should never see a blockchain — FAILS a
  pure fiat payout.

The rule is now "does the actor have a usable destination on the rail this operation will
settle on", which fixes both with one question.

**Which rail.** `IActionNodeHandler.ResolveSettlementRailId(node)` reads it from the node's
declared config: a Bridge node settles its outbound leg on `SourceChain` (that is where the
actor's value must be and where the lock is signed), a FungibleTokenCreate on its configured
`ChainType`. Returning null means "the platform default rail", which the gate resolves from
`IBlockchainProviderFactory.DefaultRailId`, falling back to
`BlockchainConfig.DefaultChainFallback` so the gate and the config class cannot drift apart.
The rail is read from the node DEFINITION rather than the `$from`-resolved config because the
two seams resolve bindings at different points; a chain type is a static routing decision,
not a runtime value.

**How the answer is found.** A registered `IRailDestinationDirectory` for that rail wins;
otherwise the actor's wallet directory scoped TO THAT RAIL (`WalletQueryRequest.ChainType`,
plus a post-filter so a store that ignores the filter cannot widen the answer). The directory
interface is the seam a fiat rail plugs into — a payout destination is a bank account or a
card, not a wallet, and lives in its own store. When that store exists it implements
`IRailDestinationDirectory` and neither the gate nor either dispatch seam changes.

**Fails closed, without exception.** A lookup error, a directory that throws, an unnamed
rail, and an empty result are all "no capability". A transient database fault must never be
the reason value moves.

**The account pause, and why its "empty case" is answered at BOOT (OI-28).** The gate also
carries the reversal risk flag: an account with an unresolved reversal moves no value on any
rail through any node. It reads that flag through `IFiatReversalLedger`, which is a TRAILING
OPTIONAL constructor parameter on the gate and on both seams — it has to be, because dozens of
positional unit fixtures construct `ActionManager` and `ActionNodeStepHandler`.

That optionality is a hole, and `AdmitValueNodeAsync` cannot close it: with no ledger there is
nothing to ask, and refusing every node would turn a composition mistake into a runtime
mystery. So the empty case is answered one level up. Both seams implement
`IValueNodePauseSeam`, `ValueRailStartup.GuardAccountPauseWired(app.Services)` resolves each one
at boot and asks it, and a container composed without
`ReversalRegistration.AddFiatReversalPipeline` **refuses to start** naming the seam that lost
the ledger. That call is the only production reader of
`ValueRailCapabilityGate.IsAccountPauseEnforced`; before it existed the property's only readers
were assertions in its own test file, which is a control whose only reader is its own test.
Held by `tests/AZOA.WebAPI.Tests/Actions/ValueRailStartupTests.cs`.

## §not-enabled-node-types — "not enabled" is a control, not a badge

`Services/Actions/NotEnabledActionNodeTypes` holds the node types that are fully
implemented and dispatchable but must NOT run yet. Today that is exactly four:
`GovernanceProposalCreate`, `GovernanceVote`, `GovernanceTally`, `CredentialRevoke`.

**Why a gate at all.** These four were described as "not enabled" in the runbook, in both
design track specs, and on the builder's palette badge — while `Program.cs`
assembly-scans every `IActionNodeHandler` in the `Handlers` namespace and registers it,
`ActionNodeConfigRegistry` accepts their configs, and both dispatch seams would have
called `HandleAsync`. A published action reaching `GovernanceVote` WOULD have cast a
ballot; `GovernanceTally` WOULD have emitted a count its own design cannot trust;
`CredentialRevoke` WOULD have attempted an Algorand clawback. The badge was frontend copy
and nothing more. That is the §inert-control class in `Services/AGENTS.md`, so the fix
takes the shape §rail-capability already proved.

**Three refusal points, one sentence.** `NotEnabledActionNodeTypes.RefusalMessage` is the
only message, so publish and both seams read identically:

1. `ActionManager.PublishAsync` — a blocked type cannot enter an `Active` action at all;
2. `ActionManager.ExecuteAsync`, the in-process executor — refused before `HandleAsync`;
3. `ActionNodeStepHandler`, the durable saga step — refused before `HandleAsync`.

Publish alone is not enough: actions published before the gate landed are already
`Active`, so only the seams can stop one running. One seam alone is not enough either,
for the same reason the rail gate lives in both — **the durable path must not be a way
around what the in-process path enforces.**

**Why each member is here, and what removes it:**

- **Governance cannot be trusted to count.** The tally reads raw child holons, so a
  ballot created outside the handler is counted without ever taking a uniqueness claim
  (§governance): one verified user can post N vote holons and all N count. Tracked by
  `conductor/tracks/governance-vote-of-record_20260906`.
- **The credential rail cannot authorize.** The only mint path stamps
  `issuer_user_id` = the holder, which makes the "issuer, never the holder" rule in
  §credential-revoke vacuous — the party the credential would be taken from is the
  party entitled to take it. Tracked by
  `conductor/tracks/credential-third-party-issuance_20260906`.

Neither primitive is absent and neither works. Describe both as **implemented and
gated**; a reader who believes either extreme will make a wrong decision.

**Removing a member is the LAST step of its track, never the first.** The gate is what
makes the not-enabled claim true. Deleting a member before the defect above is fixed
re-creates precisely the situation the gate was added to end.

**Tests that fail when the enforcement is removed** —
`tests/AZOA.WebAPI.Tests/Actions/NotEnabledNodeTypeGateTests.cs`.
`BothDispatchSeams_ConsultTheGate` and `Publish_RefusesABlockedNodeType` read the seams'
SOURCE and fail if any of the three stops calling `IsBlocked`; the failure being guarded
is "somebody deleted the check", which a behavioural test over a handler cannot see.
`EveryBlockedType_IsANamedTrackedException` fails if the set grows without naming the
track that lifts it, so a temporary gate cannot quietly become permanent dead surface.

## §loan-tracker — the principal-only loan primitive (D21)

`LoanOpen` / `LoanRepaymentRecord` / `LoanBalance` are **Tier-1**. D21 calls the loan
"a tracker, not a new value primitive": a Holon carrying a disbursement and a repayment
ledger, whose balance is a pure projection. Nothing in this lane signs, broadcasts, or
touches a wallet, so none of the three overrides `RequiresChainCapability`.

**Money movement is not this lane's job.** A tenant composes the existing Tier-2
`Grant`/`Transfer` for the actual disbursement and `Transfer` for the actual repayment,
then records the fact with `LoanOpen`/`LoanRepaymentRecord`. There is deliberately no
`LoanDisburse` node that moves value — that would re-create the value primitive D21
refuses. The `reference` on each config is where the tenant ties the record back to the
tx that really moved.

**Shape.** The loan is a Holon with `AssetType = "LoanTracker"`; each ledger row is a
CHILD Holon with `AssetType = "LoanLedgerEntry"` and `entryKind` in
{`Disbursement`, `Repayment`, `WriteOff`}. Vocabulary, metadata keys and the opt-in
`holon_type_registry` seeds live in `Services/Lending/LoanHolonTypes.cs`; no new table
exists or is needed.

**No interest, anywhere.** No `interest`, `apr`, `rate`, `accrual`, `compound`, `term`,
or penalty-bearing `dueDate` field may appear on any config, metadata key, projection
output or DTO in this lane — D21: "no interest-accrual node may ever exist — the absence
is the feature", and reversing it (§4 assumption 5) "requires scholar review, not just
code". The absence is enforced, not aspirational:
`tests/AZOA.WebAPI.Tests/Lending/NoInterestPrimitiveGuardTests.cs` reflects over every
type, member, parameter, persisted metadata key and output key in the lane, plus the
`Loan*` members of `ActionNodeType`, and fails on any of those substrings. The scan is
scoped to the lane on purpose: an enum-wide "rate" search would trip over `ActionGraphBundleGenerate`.

**Partial repayment and write-off are ordinary states.** A repayment below the
outstanding balance returns `Ok` and leaves the status `Open`; a write-off returns `Ok`
and moves it to `WrittenOff`. Neither is a `Fail`. **Over-repayment is the one genuine
error** — it means the ledger disagrees with the value that actually moved, so the row is
refused rather than clamped. Independently, the projection floors `outstanding` at zero,
so even a bad row already in the store cannot express a negative debt.

**Amounts.** Every amount is a `RailAmount` persisted as a base-unit decimal STRING in
holon metadata (`Persistence/SurrealDb/Models/AGENTS.md` §amount-columns; D13 — `ulong`
overflows at ~18.4 ETH in wei), written and read through `RailAmount.ToBaseUnitString()`
and `RailAmount.TryParse` via `LoanAmountMetadata`. This lane TIGHTENS the "absent reads
as zero" half of that rule: an absent or unparseable amount throws
`LoanLedgerFormatException`, which the handlers turn into a `Fail`. A ledger entry that
cannot say how much it moved must never read as having moved nothing.

**Projection.** `LoanBalanceProjection` is the ONLY place a balance is computed — a pure
fold, no IO — so a gate reading `LoanBalance` output and a `LoanRepaymentRecord` checking
headroom can never disagree. `outstanding = principal − Σ repayments − Σ write-offs`, in
`BigInteger`, floored at zero. Disbursement rows are recorded facts, not additions: the
tracker's own `principal` is authoritative. Every entry must carry the tracker's unit — a
mixed-unit ledger (an ALGO repayment against a USDC principal) throws rather than
summing. Output is
`{ loanId, principal, totalRepaid, totalWrittenOff, outstanding, status, entryCount }`,
every amount a string. These keys are camelCase, pinned with `[JsonPropertyName]` — the
one deliberate exception to §output-schema's PascalCase observation, so the catalog entry
for these three types must use these names.

**Reads are runner-scoped and owner-strict** (same posture as D20 / §acting-user):
`LoanLedgerReader` resolves the tracker with `context.ActingUserId` and then requires
`loan.UserId == ActingUserId`, so a PUBLIC tracker owned by someone else is still
refused. Missing, unreadable, foreign-owned and wrong-typed all return the SAME message,
so a caller cannot probe for another user's loans. A ledger row the runner may not read
aborts the whole projection rather than dropping silently out of the sum — a dropped
repayment understates the debt.

**Two deliberate non-atomic seams.**
- `LoanOpen` writes the tracker and then its disbursement row. If the second write fails
  the node fails with the tracker id in the message: a tracker with no disbursement row
  would project a principal nobody received, so it must be reconciled, not ignored.
- `LoanRepaymentRecord` mirrors the DERIVED status onto the tracker (for
  `holon.<id>.status` gate predicates) as a BEST-EFFORT write. A failed mirror comes back
  as a note on a successful result, because failing the node after the ledger row landed
  would invite a retry that records the repayment twice. The rows are the truth; the
  status field is a mirror. The `reference` field is the replay key that makes such a
  retry safe: the same reference on the same tracker records once.

## §governance: consumer proposals, ballots and tallies (D20)

Three node types, `GovernanceProposalCreate` / `GovernanceVote` / `GovernanceTally`,
all **Tier-1**: none overrides `RequiresChainCapability`, none touches custody. That
is deliberate — D20 says the tally "runs in a pure-DB (Tier-1) node, needing no
chain capability and no custody", which is exactly what lets governance ship in
parallel with the KMS/custody workstream.

**No new tables.** A proposal is a Holon (`AssetType = "GovernanceProposal"`); a
ballot is a child Holon (`AssetType = "GovernanceVote"`, `ParentHolonId = proposalId`,
`UserId = the runner`). `Services/Governance/ConsumerGovernanceHolonTypes.cs` owns
both names, both metadata vocabularies, and the two `holon_type_registry` seed rows a
tenant registers to make a malformed ballot fail at write time instead of tally time.
Not to be confused with the operator-side `NodeGovernance*` files in the same folder:
those govern what this NODE will run, these describe what a tenant's MEMBERS vote on.
(`NodeGovernanceController` is likewise a different thing from anything here.)

### The uniqueness consultation is the primitive

`GovernanceVoteNodeHandler` injects `IPersonUniquenessGate` and calls `TryClaimAsync`
BEFORE the ballot holon is created. Without it, one human holding N verified users
votes N times — D3 names one-vote governance as exactly the case that needs
one-human-one-claim, and verified users are cheap to multiply. Three rules hold it
together:

1. **The uniqueness domain comes from the PROPOSAL, never from the vote node's
   config.** Scope is read from the proposal's `uniquenessScope` metadata; the
   operation key is `governance:proposal:{proposalHolonId}`, derived by
   `ConsumerGovernanceHolonTypes.UniquenessOperationKeyFor` and cross-checked against
   the key the proposal recorded. `GovernanceVoteNodeConfig` deliberately has no scope
   field: if each voter chose their own domain, nobody would ever collide and the gate
   would be decorative. A missing/unparsable scope, or a key that is not the derived
   one, fails CLOSED.
2. **A failed ballot write releases the claim.** `ReleaseAsync` runs on the create
   failure path; a held claim with no ballot behind it disenfranchises that human for
   the rest of the proposal.
3. **The claim is spent last.** Option validity, the voting window, and the
   one-ballot-per-user check all run first, so a rejected ballot costs no claim.

Numeric scope strings are rejected (`TryParseScope`): `"1"` is
`PersonUniquenessScope.Global` by ordinal, and a tenant ballot must never become a
node-global one without someone spelling it out.

### A ballot never carries a person key

Vote metadata is built from the CLOSED
`ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys` set — `option` and `castAt`,
nothing else — and re-checked against it before the write. A vote child is
consumer-readable through `HolonQuery`; a person key, a global person key, or any KYC
field on it instantly reconstitutes the D2 person-to-choice correlation. The person
key lives ONLY in `person_operation_claim`, which is node-operator-only. This is the
single highest-risk mistake in this lane, so it is asserted by name in
`GovernanceNodeHandlerTests.Vote_BallotCarriesOnlyOptionAndCastAt_NeverAPersonKey`.

### Tally, and why there is no anchor node

`GovernanceTally` reads `GetChildrenAsync(proposalId, ActingUserId)`, groups by
`metadata.option`, and emits
`{ proposalId, totalVotes, counts, tallyHash, tallyAt, closesAt }`. Every declared
option appears, including the ones nobody chose, so a zero cannot silently change the
preimage. `Services/Governance/GovernanceTallyHash.cs` is the ONE canonical preimage:
domain tag, proposal id, close time, total, then options sorted ordinal-ascending and
written LENGTH-PREFIXED (`{charCount}:{option}={count}`) so an option that embeds the
separators cannot forge a colliding tally. It contains NO VOTER IDENTITIES — that is
the whole reason a co-op tallies off chain rather than putting ballots on one, where
every member's choice would be permanently public.

**Anchoring is a composition, not a node.** A tenant that genuinely needs public
verifiability wires `GovernanceTally -> BlockchainExecute` with `$from` bound to
`tallyHash`, one transaction per proposal. Do not grow a fourth governance node type
for it: the chain surface stays where it is and governance stays free of custody.

### Read scoping (and the electorate limitation)

Proposal reads go through `HolonManager.GetAsync(id, context.ActingUserId)`, whose
owner-or-public rule already answers `"Holon not found."` for anything the runner may
not read. Both handlers then collapse missing, unreadable, and wrong-asset-type onto
ONE sentence (`GovernanceVoteNodeHandler.ProposalUnreadable`) so a vote node cannot be
used as a proposal-existence oracle across users.

This is the one place governance deviates from `§gate-predicate`'s owner-STRICT holon
rule, and the deviation is deliberate: a ballot you own is not an election. GateCheck
additionally rejects a holon owned by anyone else; a proposal must be readable by its
electorate, so `GovernanceVote`/`GovernanceTally` accept the manager's owner-or-public
answer and add no stricter check.

**How the electorate is assembled.** `HolonManager.CanRead` scopes both `GetAsync` and
`GetChildrenAsync` to owner-or-public, so a cross-user electorate needs a PUBLIC
proposal. `HolonCreateModel.IsPublic` / `HolonUpdateModel.IsPublic` now exist and a
proposal's asset type is on `HolonManager.PublishableAssetTypes` — see
`Managers/AGENTS.md` §holon-publication for why that is an allow-list rather than a free
consumer toggle.

**A ballot is deliberately NOT publishable.** `GovernanceVote` is absent from that
allow-list: a public ballot carries `Holon.UserId` and would reconstitute the
person-to-choice correlation the whole D20 design exists to prevent (see *A ballot never carries a
person key* above). A tally therefore still reads only ballots the tallier can read,
which is why the trustworthy count has to come from the privileged claim ledger rather
than from a holon scan — the open defect that keeps these nodes in
§not-enabled-node-types.

### Where the configs live (temporary)

`GovernanceProposalCreateNodeConfig`, `GovernanceVoteNodeConfig` and
`GovernanceTallyNodeConfig` are declared alongside their handlers rather than in
`Models/Actions/NodeConfigs.cs`, because the D20 lane does not own that file. Moving the
three DTOs there (names unchanged) is the remaining tidy-up.

Both central-file entries have LANDED — `ActionNodeConfigRegistry._map` has one entry
per type (a missing entry throws `NotSupportedException`, caught by the registry
exhaustiveness test) and `ActionNodeOutputSchema` has one shape per type plus its bucket
in `ActionNodeOutputSchemaGoldenTests`. **A governance node is therefore fully wired and
would run; the reason it cannot be published is `NotEnabledActionNodeTypes`**
(§not-enabled-node-types), not a missing registration. Do not read the DTO location as
a safety property: it never was one.

## §credential-revoke — soulbound revocation is the issuer's power (D19)

`CredentialRevokeNodeHandler` + `CredentialManager` + `AlgorandProvider.ClawbackAssetAsync`.
This is the only primitive in the codebase that moves value OUT of an account that did
not sign the transaction, so read the three rules below before touching any of them.

### 1. Authorization is the ISSUER, never the holder

Revocation is authorized against `nft_ownership.issuer_user_id` — the party that MINTED
the credential — compared with the run's acting user (node path) or the authenticated
caller (REST path). It is deliberately NOT derived from:

- `current_owner` / `NftOwnership.UserId` — that is the HOLDER. Authorizing on it is the
  exact inversion D19 exists to prevent: the person the credential is being taken from
  would be the person entitled to take it.
- the mint's `operation_log.user_id` — a mint proxied through a tenant credential stamps
  the tenant, not the issuing organisation.

`issuer_user_id` is nullable and **every row minted before D19 has none**, so a revoke of
such a row FAILS CLOSED with an explicit message rather than guessing. It is written only
through `ICredentialIssuerStore.SetIssuerUserIdAsync` — never through the ordinary
`IUserNFT` upsert, which cannot see the column (see §issuer-column below).

### 2. Nothing is assumed about the chain; four things are proven

`ClawbackAssetAsync` refuses rather than attempts, before a byte is signed:

1. the ASA exists on the active network;
2. its on-chain `clawback` address is **exactly** (ordinal) the account that will sign —
   the platform address `CreateSoulboundAsaAsync` stamps at mint;
3. the holder actually holds at least the units being revoked — deliberately WITHOUT
   requiring an unfrozen holding, because soulbound credentials are minted
   `defaultFrozen: true` and clawback is precisely the operation Algorand still permits
   on a frozen holding (`RequireOptedInAsync` would wrongly reject every revoke);
4. the destination is opted in and can receive it.

**Known limitation — a frozen destination.** Soulbound ASAs are minted
`defaultFrozen: true`, so an issuer wallet that opted in AFTER creation holds a FROZEN
position and proof 4 refuses the revoke ("the revocation destination ... is frozen").
That refusal is correct — the chain would reject the transfer — and the resolution is
operational (the platform is the freeze admin; unfreeze the destination, or revoke to an
account whose holding is not frozen), not a relaxation of the proof. Do not weaken proof
4 to make a demo pass.

The transaction is an `AssetClawbackTransaction`, **not** an `AssetTransferTransaction`:
in the Algorand2 SDK only the clawback type declares `AssetSender`. `Sender` is the
clawback authority (who signs); `AssetSender` is the account the units leave. Swapping
them signs a harmless self-transfer that reports success while revoking nothing.

### 3. A pending transaction is not a revocation

`BuildSignSubmitAsync` returns a SUCCESS result carrying
`OperationStatus.PendingConfirmationMarker` when a transaction was broadcast but did not
confirm in the poll window. `CredentialManager` maps that to
`OperationStatus.PendingConfirmation` + the tx hash, leaves the idempotency claim
`InProgress` (a duplicate replays "in progress" rather than re-broadcasting a second,
irreversible clawback), and the node handler reports it as a node FAILURE with the tx
hash attached so reconcile-before-retry verifies chain truth. A "success" with no tx id
is recorded `Unknown` and refused. Only a confirmed clawback is `Completed`.

### Tier and rail

`RequiresChainCapability => true` and `ResolveSettlementRailId(node) => cfg.ChainType`.
Both are load-bearing: the pre-execution gate (§rail-capability) is what stops a runner
with no destination on that rail reaching a signing path, and it is only consulted for
handlers that declare the flag. Both dispatch seams call the gate, so the flag is the
whole enforcement.

### The operation record

Written as an ordinary `CredentialRevoke` operation row (amount `RailAmount.OfChainAsset(1)`,
`AssetType = "Credential"`) so it lands in D16 analytics with every other value move. It
reads back UNPRICED, like every other row — only `UnpricedValueRateSource` exists — and
that is correct; do not special-case it.

It is persisted through `IBlockchainOperationStore` rather than
`IBlockchainOperationManager.ExecuteAsync`, because that method is the **executor**: it
removes `Parameters["TxHash"]` on entry and, having no `CredentialRevoke` case, would
record the row `Unknown` with the tx hash discarded — deleting the one piece of evidence
reconciliation needs. Adding a `case "CredentialRevoke"` there (plus a
`TryResolveStampableAmount` entry) would let the row move onto the ordinary path; until
then the store write is the honest one.

### §issuer-column — why the issuer bypasses the IUserNFT mapping

`SurrealNftStore` maps `IUserNFT` ↔ `NftOwnership` field-by-field, but `IUserNFT` has no
issuer property and its only writable carrier — the caller-supplied `Attributes` bag —
would let a holder name themselves issuer and revoke their own credential. So the column
is read and written through a second, narrow face on the same store,
`ICredentialIssuerStore` (`FindCurrentByAssetIdAsync` / `SetIssuerUserIdAsync`).
`ToNftPoco` leaves `IssuerUserId` null on purpose: `SurrealWriter` omits null `option<>`
fields from the SET, so an ordinary transfer or metadata edit PRESERVES the recorded
issuer instead of wiping the authorization anchor. This is the one field on that table
that is not §set-omits-null.

### Where the config lives (temporary)

`CredentialRevokeNodeConfig` is declared alongside its handler rather than in
`Models/Actions/NodeConfigs.cs`, because the D19 lane does not own that file — same
situation as the D20 governance configs above, and the same remaining tidy-up.

Both central-file entries have LANDED (`ActionNodeConfigRegistry._map` and
`ActionNodeOutputSchema` + its golden bucket). **The reason a `CredentialRevoke` node
cannot be published is `NotEnabledActionNodeTypes`** (§not-enabled-node-types): the rail
is implemented and gated, not unwired. Its blocking defect is the one named there — the
only mint path stamps `issuer_user_id` = the holder, so the issuer check below, though
correct as written, currently decides nothing.

### Refund is not a back door

`RefundNodeHandler` still refuses soulbound assets, and now for the accurate reason: a
Refund reverses a debit for the RUN ACTOR, while a clawback is exercised by the ISSUER
against a holder who did not consent. Routing soulbound reversals through Refund would
hand every marketplace runner an issuer's power over an asset they merely received.


## Round 2 local mutation receipts

The handler registry wraps local holon, wallet metadata/import and loan-ledger effects.
ActionLocalMutationService recreates the real manager against bounded overlays, captures
original row and query membership, and derives created entity IDs from the admitted command.
The atomic store fences current run/node intent, node policy, actor/tenant consent, and
all read witnesses before committing effects and receipt together. Table mutation locks
serialize graph membership against ordinary insert/delete writers. Generic plans reject
GovernanceProposal rows including descendants; only the governance authority service may
mutate their projections.

A deterministic rejected plan stores a failed receipt with no effects. This receipt
competes with successful plans under the same unique command identity, so late suspended
planners cannot commit after rejection. Existing receipt recovery needs no fresh execution
authority. Infrastructure exceptions propagate; commit conflicts require reconciliation.

## Audited action operator recovery

`ActionOperationRecoveryService` exposes sanitized inspection and an explicit audited retry of the original frozen operation. GET never invokes a domain resolver. Retry accepts only an exact execution version/hash and operator-scoped idempotency key/reason; server-selected registered capabilities are local, governance, deployment and NFT. Current actor/tenant authority remains enforced by the original domain. No operation-ID replacement, manual terminal verdict or fresh signing permission is accepted.

The store commits the action authority fence, exact parked generation, unchanged saga payload/admission hash and audit together. Replaying an old audit never wakes a later generation. Original signal payload is retained. Missing legacy correlation remains quarantined. See active track `round2-action-operator-contract.md` for review and unrun verification status.

## Confirmed grant attachment

ActionNftHolonLinkService handles the optional Holon attachment after a grant lifecycle completes. Its separate local receipt binds the frozen run/node/actor/tenant and exact lifecycle/asset/network/contract/final transaction evidence. The shared local mutation transaction checks current action, actor, delegated action scope, policy version and original Holon witness while committing the link and receipt together. Matching receipt replay precedes current ownership/readiness checks; no second mint or link is attempted. An existing token binding without this receipt is quarantined even when its chain/token pair matches, since that pair alone cannot prove contract identity. A racing committed receipt is reread before reporting a planning conflict. The real HolonManager runs over the staged overlay so publication, governance and type validation remain shared.

## Durable action recovery and admission (Round 2)

Both execute and start-workflow admit one asynchronous run. Their required Idempotency-Key
is scoped to action, authenticated actor and tenant; exact request replay precedes live
source checks. Acknowledged economic consent also requires ExpectedPublishedHash from
preview. The admitted frozen snapshot is immutable. New admissions serialize current
source revision/access/quota with run, execution rows and first saga step.

Each scheduler decision carries the run version. Fresh Pending-to-Running admission and
all node outcome updates advance the run epoch in one transaction, preventing stale
workers from starting effects after a sibling failure or cancellation. Successor saga
identity is deterministic and uses the shared opaque-key codec. Occurrence boundaries
wait for selected prior work, including successful failure handling, while skipped
unselected branches do not veto later occurrences. Only guaranteed prior outputs can bind.

NFT actions use an immutable run/node/actor/tenant/intent authority. Domain admission,
preparation authorization and first send are fenced; recovery only admits missing intent
or observes/rebroadcasts already authorized wire. Participant controls supply new signatures.
Grant attachment requires the separate atomic holon receipt before workflow success.
Refund references one exact succeeded frozen transfer and waits for the actual holder.
Read recovery invokes only a closed safe handler catalog under the original caller;
retryable read or local-plan errors remain recoverable without pinning a failed receipt.

The compensation handler does not claim to reverse value. Proven read-only/no-effect
failures may close; completed or unresolved effects park for domain compensation evidence.
Actual compensation adapters and their receipts remain required implementation work in
Round 3, alongside the remaining Hold/native/fungible/emit and bridge/DEX adapters.
# Participant signing authorization

Runs retain private authenticated credential evidence; economic nodes retain private exact scope/grant evidence. Neither field participates in public run/node JSON or frozen upstream-output serialization. `ActionSigningScopes` is the closed scope mapping over resolved immutable node input. Native deployment kinds are explicit; runtime registration has no signing scope. Hold scope policy remains part of the in-progress Round3 hold domain package, not an implicit grant.

An older frozen node without credential evidence parks as `NeedsParticipantAuthorization`. The original actor in the exact tenant context may authorize that unchanged node through `/api/action/executions/{executionId}/signing-authorization`, with expected version, intent hash, original request ID and review reason. The transaction records immutable participant consent and wakes only the matching parked saga. It refuses cancellation, changed input/version, wrong actor/tenant, revoked grants and existing historical node witnesses. Replaying the receipt never wakes a later saga generation. Runs without any frozen graph remain quarantined.

Pre-dispatch deterministic refusals persist `AdmissionRejection` with the node admission. Recovery projects that durable no-effect decision without dispatching a handler. A missing transaction hash alone is never a rejection receipt.

## Durable Emit acceptance

The new Emit acceptance store records the exact payload and optional webhook event atomically under frozen run authority. Acceptance means the event is durably owed, not delivered or settled by its receiver. Non-tenant runs retain a local receipt. Tenant runs require an active configured destination; absent registration remains retryable. Deterministic malformed config is a durable failed receipt. Event identity and destination remain unchanged during recovery. The replacement handler/resolver and leased delivery worker remain required before enabling this path.

## Frozen NFT workflow cost consent

Solana mint and Grant executions park with their original frozen reference until the actor explicitly accepts a disclosed cost offer. `/api/nft-workflow-costs/{executionId}/quote` returns the execution version and receipt; `/accept` consumes the caller-persisted request ID, exact version and authorization. Atomic acceptance records both original workflow authority and current participant consent while creating the lifecycle. The frozen input remains unchanged. Resolver observation never dispatches a fresh signature for this path; participant lifecycle controls continue admitted work.

`/reconcile` reads execution version before the immutable acceptance audit. A matching audit proves Applied even after cancellation or authority changes; absent audit only proves Obsolete after a version advance, otherwise remains Unresolved. Authentication supplies actor and tenant; request bodies cannot override them. Source fixtures cover parked recovery and ordered receipt reconciliation; execution is pending the integrated gate.

## Hold quartet recovery

The Hold resolver uses original frozen input and a network-qualified source. New
openings and human references require explicit Network; a changed server default
cannot redirect a recovered command. Named predecessors require the original run,
node and immutable receipt identity. Only a completed accounting receipt yields
successful output. Deterministic invalid input records HoldAdmissionRejected by
execution-version CAS before failure; missing authority/provider/source evidence
remains recoverable. The quartet is not activated until legacy caller cutover and
registration are complete. Tests are authored but the integrated sweep is pending.
