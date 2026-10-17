# Managers — design notes

## Swap fee denomination

Swap flat fees require the persisted `input-base-units-v1` declaration. Missing
or legacy denomination is disclosed as unconfigured and cannot produce a swap
quote, including a legacy zero policy. The existing authorized schedule update
can explicitly adopt either zero or nonzero values under its version CAS and
immutable audit; no background migration guesses a denomination. Other operation
fee semantics are unchanged. Exact unchanged policy retries return the current
version without another audit. DEX quotes retain the chosen denomination/version.

## Value-access choke points

`IValueAccessService` is the shared fail-closed readiness decision for direct
allocation, fungible-token creation, and NFT mint/transfer/burn. Managers call it
before wallet, Holon mutation, operation persistence, or provider work; an outer
idempotency or immutable denial record may already exist so a rejected retry is
auditable. Tenant-driven calls pass the authenticated acting tenant, while
direct calls resolve node-default authority. This centralizes readiness without
moving ArdaNova project/task economics into AZOA or replacing AZOA's independent
ownership, consent, governance, fee, and custody checks.

Project definition and ordinary collaboration paths do not consume this gate.
Action value nodes inherit it through the same managers; a new value-bearing
manager must use this seam rather than call `IKycGateService` directly.

## Tenant profile provisioning

`TenantManager.ProvisionChildAsync` is idempotent on the authenticated tenant id
plus `ExternalUserId`. The unclaimed user retains `OwnerTenantId` only as the
tenant-scoped onboarding/list/resolve correlation required by the Surreal unique
index. It does not grant signing authority: child credentials still require a
live user consent grant, carry `act_as_tenant`, and are scope-ceilinged. User
claim clears `OwnerTenantId` and invalidates older child credentials.

User ids created by this path are deterministic SHA-256-derived identities of
`(tenantId, externalUserId, v1)`, so concurrent retries converge on one row in
addition to the composite unique index. Creation uses the store's create-only
boundary; it never UPSERTs a deterministic identity. After user claim clears
`OwnerTenantId`, resolve/retry reads that deterministic id, verifies the external
subject and tenant-compatible ownership state, and returns the original without
touching password, auth-wallet, KYC, or ownership fields. Synthetic
usernames/emails use only a short digest rather than embedding the subject.

`TenantCustodialAccountManager` composes this existing user with
`IWalletManager.BootstrapWalletAsync` and `IKycManager`; it is orchestration, not
a second identity store. Ensure is a stable `PUT` resource and the wallet id is
deterministic per `(user, chain, v1)`. Its raw tenant idempotency key is hashed
inside a tenant-partitioned ledger key; replay is bound to the original external
subject and then refreshes current status without a second create. Status is
ready only when custody,
provider, platform-wallet ciphertext/address, and authoritative Azoa KYC approval
are all present. Configuration/provider failures return an explicit unavailable
state and never start provisioning.

The present key wrapper is not production custody. Capabilities allow it only
under `DevelopmentOnly` + Development host + simulated blockchain mode. Live or
non-development runtimes fail closed, and `KmsHsm` stays unavailable until a real
adapter is registered. Tenant bootstrap retains no encrypted seed phrase.
Identity and KYC are separate readiness dimensions: unavailable wallet custody
does not prevent deterministic user creation or an available KYC flow.
`WalletProvisioningReady` gates only wallet creation; aggregate readiness still
requires identity, provider, wallet, and approved KYC.

A completed ensure replay is convergent rather than frozen: after validating the
cached tenant/subject binding it reruns only create-only/idempotent stages. An
identity-only success therefore adds its deterministic wallet later when an
approved custody adapter becomes available, using the same stable key.

Once a caller wins an idempotency claim, the deterministic provisioning work and
ledger settlement run independently of request cancellation; cancellation is
propagated only after that claim reaches a terminal state. A caller that loses a
live `InProgress` claim performs a bounded read-only wait and replays the owner's
terminal result. It never executes or settles the shared effect. Failed claims
stay terminal until the store gains an atomic lease/reclaim operation; a loser
cannot safely appoint itself the new owner. Operation types include the external-
subject hash, so one subject cannot observe or advance another subject's claim.

KYC completed replays remain `ensure active attempt`: after a terminal completion,
`IKycManager` may reuse the current attempt or roll an expired/rejected attempt
forward. A concurrent observer that first saw `InProgress` instead replays the
owner's cached response without calling the provider a second time. A later
transient provider failure never overwrites the earlier completed record.

The KYC session route defines its stable key as `ensure active attempt`. The KYC
ledger, not the tenant, owns attempt sequencing: an unexpired pending attempt is
replayed, a rejected/expired attempt permits one new pending attempt, and a live
approval refuses redundant enrollment. Begin persists the attempt before any
documents exist; submit atomically attaches the first validated document set.
Each attempt is stamped with the current provider/policy/assurance provenance.
Begin expires rather than replays an attempt after any of those values change.
Direct document submission rejects a stale attempt and requires Begin to create
the new provider-owned attempt, so one adapter never validates another's row.
Tenant projections omit provider session ids and stored document references.

## §skip-semantics — ActionManager.ExecuteAsync skip loop

The in-process executor walks nodes in topological order and skips a node when
any incoming edge qualifies:

| Edge type    | Skip condition (post action-dag-semantic-hardening FR-1) |
|---|---|
| `Control`    | source state is `Failed` **or `Skipped`** |
| `Conditional`| source state is `Failed` **or `Skipped`**, regardless of `Condition` text |

Prior to FR-1 the rules were weaker: Control skipped only on `Failed`; Conditional
required `!string.IsNullOrEmpty(Condition)` (so empty-condition edges were inert,
letting downstream nodes run through a failed gate). The P0 hazard: a payout chain
behind a failed eligibility GateCheck would run from the second hop onward.

The `HIGH#7 G2` `expectedState: ActionNodeState.Pending` guard on the skip
update-row call is unchanged — it prevents a concurrent `ForkAsync` cancellation
from being silently overwritten.

The **durable engine** (`Services/Actions/Workflow/`) has no equivalent skip seam —
see `Services/Actions/Workflow/AGENTS.md §skip-semantics`.

## §publish-lifecycle — Action definition status

An action definition carries a `Status` field (`ActionStatus`, default `Draft`)
introduced by `action-dag-semantic-hardening` FR-2. The lifecycle:

```
Draft ──publish──▶ Active ──unpublish──▶ Draft
```

- **Publish** (`PublishAsync`): runs the full validation stack (structural DAG +
  engine-profile fan-out check as error + per-node config strict round-trip), then
  flips `Draft → Active` via the F6 compare-and-swap (see below) BEFORE persisting
  fresh `ExecutionOrder`. The CAS is the serialization point — a lost race returns
  a publish-conflict error, never a torn double-publish.
- **Unpublish** (`UnpublishAsync`): refused while any `ActionRun` for the action is
  non-final; otherwise flips `Active → Draft` via the same CAS (conflict on a lost
  race).
- **Execute / StartWorkflowRunAsync**: require `Status == Active`, then re-confirm
  the definition is STILL Active at the version read (F6 `TryConfirmActionStateAsync`)
  before committing a run; a concurrent unpublish that moved the row yields a
  run-conflict error. Error message names the publish requirement explicitly.
- **Mutation endpoints** (`AddNodeAsync`, `UpdateNodeAsync`, `DeleteNodeAsync`,
  `AddEdgeAsync`, `RemoveEdgeAsync`): rejected when `Status == Active`; caller
  must unpublish first.

Execute-time re-validation is kept as defence-in-depth (does not replace the
publish gate; both run independently).

**Publish also refuses a not-enabled node type.** Before the validation stack,
`PublishAsync` walks the definition's nodes and refuses any whose type is on
`Services.Actions.NotEnabledActionNodeTypes`, returning that class's single shared
refusal sentence. This is one of THREE refusal points, not the only one: an action
published before the gate landed is already `Active`, so the two dispatch seams
(`ExecuteAsync` here and `ActionNodeStepHandler`) carry the same check. See
`Services/Actions/AGENTS.md` §not-enabled-node-types.
`NotEnabledNodeTypeGateTests.Publish_RefusesABlockedNodeType` reads this method's source
and fails if the check is deleted — the guarded failure is removal, not a wrong
answer.

**TOCTOU guard — optimistic concurrency (F6, RESOLVED).** The action definition
carries a `Version` (`long`) optimistic-concurrency token (`action.version` column,
default 0). Every lifecycle status transition is a compare-and-swap against it,
mirroring the bridge's `TryTransitionBridgeStatusAsync` discipline (return the
affected-row count VERBATIM; the store never asserts==1, retries, or RMW):

- `IActionStore.TryTransitionActionStatusAsync(id, expected, next, expectedVersion)`
  issues one conditional `UPDATE ... SET status = $next, version = version + 1
  WHERE status = $expected AND version = $expectedVersion RETURN AFTER`. Affected
  == 1 → won (status flipped, version bumped); 0 → lost the race / stale version.
  Used by `PublishAsync` (Draft→Active) and `UnpublishAsync` (Active→Draft).
- `IActionStore.TryConfirmActionStateAsync(id, expected, expectedVersion)` is a
  conditional no-op self-write (`SET version = version WHERE status = $expected
  AND version = $expectedVersion`) returning affected count without mutating the
  row. Used by `ExecuteAsync` / `StartWorkflowRunAsync` at run-start to confirm
  the definition has not been unpublished/modified since the ownership read.

This closes all three TOCTOU classes: (a) unpublish racing a run-start (the
confirm misses once unpublish bumps version), (b) double-publish (both CAS the
same Draft/version; exactly one wins), (c) mutate-on-Active races (any status
move bumps version, invalidating a stale reader's expected token). The in-flight
run check in `UnpublishAsync` is retained as an additional, earlier gate. Both
`SurrealActionStore` and `InMemoryActionStore` implement the CAS faithfully.

Reference-sharing note: the in-memory store's CAS mutates the same `ActionDefinition`
instance the manager holds, so the manager assigns the authoritative post-CAS
shape (`version = expectedVersion + 1`, by assignment not `+=`) rather than
double-incrementing.

## §holon-parent-cycle — HolonManager parent-cycle guard

`EnsureNotDescendantAsync(holonId, proposedParentId)` is a shared private helper
introduced by `action-dag-semantic-hardening` FR-6. It:

1. Rejects self-parent (`holonId == proposedParentId`) immediately.
2. Calls `GetDescendantsAsync(holonId)` and rejects if `proposedParentId` is
   in the result set (cycle: the proposed parent is already a descendant).
3. Returns null when safe; returns an error string to surface as `IsError`.

The guard is called on every `ParentHolonId` write path:

| Method | Guard condition |
|---|---|
| `CreateAsync` | `model.ParentHolonId.HasValue` — self-parent only (new holon has no descendants yet) |
| `UpdateAsync` | `model.ParentHolonId.HasValue` — full cycle check |
| `InteractAsync` | `request.NewParentHolonId.HasValue` — full cycle check |
| `MoveSubtreeAsync` | always — full cycle check (original precedent) |

If you add a new `ParentHolonId` write path, call `EnsureNotDescendantAsync`
before persisting.

`CloneAsync` (`HolonManager.cs:~359`) is the one intentionally-unguarded
`ParentHolonId` write: it assigns a fresh `Guid.NewGuid()` as both the clone's
`Id` and the new tree root, so no existing holon is named as parent and no cycle
is reachable. No guard call is needed or correct there.

## §holon-publication — IsPublic is an allow-list, not a toggle (D20)

`HolonManager.CanRead` is owner-or-public, so setting `IsPublic` makes a holon readable
by **every caller on the node**. This repo has leaked cross-tenant through generic read
surfaces three times, so the flag is deliberately not a free consumer toggle:
`HolonCreateModel.IsPublic` / `HolonUpdateModel.IsPublic` are accepted only for asset
types on `HolonManager.PublishableAssetTypes`.

**Refusal, never a silent drop.** `PublicationError` fails the create/update outright.
Accepting the request and storing `IsPublic = false` would leave the caller believing an
audience can read something it cannot — for a governance proposal, that is an election
nobody can see. Unpublishing is always allowed; only publishing is gated.

**Membership rule.** A type earns a place on the allow-list only when the artefact is
meaningless unless published. A governance proposal is the notice its electorate votes
on, and an unpublished one cannot be voted on at all (D20), so
`ConsumerGovernanceHolonTypes.ProposalAssetType` is on the list.

**`GovernanceVote` is deliberately absent, and must stay absent.** A ballot holon carries
`Holon.UserId`; publishing ballots would reconstitute the person-to-choice correlation
that the D20 design, its closed vote-metadata allow-list, and the node-operator-only
`person_operation_claim` table all exist to prevent. A trustworthy public count comes
from a privileged tally that emits counts only, never from making the ballots readable.
Pinned by `tests/AZOA.WebAPI.Tests/Managers/HolonPublicationTests.cs`.

## §holon-type-registry — opt-in Holon AssetType/metadata registry (F5)

> Its neighbours in `HolonManager` include a governance-specific control. The proposal
> freeze and the electorate rule live here rather than in the governance services
> because this manager is the one write path shared by the governance action nodes and
> by `POST`/`PUT`/`DELETE /api/holon` — see `Services/Governance/AGENTS.md`
> §proposal-freeze.

`final-hardening-cutover` Phase C / F5. Purpose: let the platform *optionally*
constrain the free-string `Holon.AssetType`. Today `AssetType` is an unvalidated
`string?`; this adds a registry that governs *only the types explicitly registered*
in it. Everything else stays a free string — that "opt-in" nuance is load-bearing:
existing holon creation must never break in a greenfield pre-launch tree.

**Pieces:**

| Concern | Type |
|---|---|
| Table + POCO | `Persistence/SurrealDb/Models/HolonType.cs` → `holon_type_registry` |
| Store | `IHolonTypeRegistryStore` / `SurrealHolonTypeRegistryStore` (AssetType = record id + unique index) |
| Orchestrator + validation hook | `IHolonTypeRegistryManager` / `HolonTypeRegistryManager` |
| Operator surface | `HolonTypeRegistryController` (`/api/holon-types`) |
| Enforcement seam | `HolonManager.ValidateAssetTypeAsync` (Create + Update) |

**Opt-in decision table** (`HolonTypeRegistryManager.ValidateAsync`):

| AssetType state | Result |
|---|---|
| null / empty | allow (no type) |
| not registered | allow (free string) |
| registered but `IsActive == false` | allow (opt-out without delete) |
| registered + active, no `RequiredMetadataFields` | allow (type name alone is validated) |
| registered + active, required fields all present + non-empty | allow |
| registered + active, some required field missing/empty | **error**, message names the missing field(s) |

**Fail-open by design.** A registry read *failure* (store exception) is treated as
"unconstrained", NOT as a block — the registry is an additive vocabulary constraint,
never a security gate. Never fail a holon write because the registry was briefly
unreachable. (Contrast the GateCheck holon-state resolver, which is fail-*closed*
because it IS a security decision.)

**Scoping.** Reads (`GET /api/holon-types`, `GET /api/holon-types/{assetType}`) are
open to any authenticated caller — the vocabulary is public. Registration /
deactivate / delete are `Operator`-scoped (same hardened gate as backfill/reconcile:
JWT-scheme floor, an API key can never reach it) because the registry is a
platform-wide constraint on every tenant's holons. No per-user ownership: this is
a global vocabulary, so there is no owner-id IDOR surface to guard — the Operator
gate is the whole access-control story.

**`HolonManager` wiring.** The `IHolonTypeRegistryManager` dependency is an *optional*
constructor arg (`= null`). DI always supplies it; the null default only exists so the
three unit-test fixtures that do `new HolonManager(store)` keep compiling. A null
registry ⇒ validation skipped entirely (pure free-string behaviour).

## node-operator-governance

`INodeGovernanceGuard` is a security gate, unlike the opt-in holon type registry
above. The guard runs before idempotency claims or store writes on the governed
economic-engine seams:

| Seam | Governed dimensions |
|---|---|
| `AllocationManager.AllocateAsync` | `ChainType` + allocation asset type |
| `FungibleTokenManager.CreateAsync` | `ChainType` + `FungibleToken` |
| `HolonManager.CreateAsync` / `UpdateAsync` | `AssetType` |
| `NftManager.MintAsync` / `TransferAsync` / `BurnAsync` | chain + `NFT` |

The local node's policy is stored as one `node_governance_parameters:local`
record and changed only through the `NodeGovern` JWT-only surface. Every update
compares the observed version inside the write transaction and appends its
`node_governance_audit` row there; concurrent writers cannot publish the same
next version.
Persistent parameters override appsettings; no persistent row means appsettings
still apply. A persistence read error fails closed because this is an economic
authorization decision.

`NodeFeeScheduleManager` owns the singleton versioned fee table and immutable
audit snapshots. Updates are value-idempotent, accept an optional
`ExpectedVersion`, and use store-level compare-and-swap inside the same
transaction as the audit insert. A stale writer never creates a forked
`N -> N+1` audit history.

`NodeTreasuryManager` owns treasury routing policy separately from fee pricing.
Destinations are versioned per canonical chain/network, provider-validated on
both write and read, and changed with the matching immutable audit row in one
store transaction. Identical retries are value-idempotent even with a stale
`ExpectedVersion`; a different stale value conflicts. A configured destination
does not activate a fee consumer by itself: settlement must pin the destination
address and version inside its own durable claim before any economic effect.

`NodeFeeSettlementManager` is intentionally unregistered and has no controller
or value-path caller. It validates and prepares an immutable settlement intent,
then exposes only an inert recovery sweep: bounded due/stale-lease candidates
are claimed with a fresh token and state version and released into
`AwaitingReconciliation`. The store also has an explicit-call paired terminal
protocol for independently observed, distinct confirmed primary/fee effects; it
atomically completes the parent claim but has no manager, worker, or provider
caller. Nothing here submits, confirms, cancels, or authorizes a fee.
`NodeFeeSettlementRecoveryWorker` is an explicit-call seam, not a hosted
service, and has no provider dependency; activating it is therefore not a
Transfer-consumer activation. `PrepareAsync` now atomically admits an
`InProgress` parent idempotency claim and immutable settlement pair; a replay
returns the existing pair and a divergent immutable decision conflicts. It does
not complete or fail the parent claim; only the store's guarded paired terminal
protocol can do so. Value-path activation still needs reviewed lease-guarded
effect hand-offs and chain reconciliation.
Admission is deliberately narrower than recovery: its only legal input is a
fresh `Prepared` row with both effects `NotStarted`, zero counters/version, and
no operation link, transaction hash, lease, or reconciliation fields. The
parent key is canonicalized once before both its hash and deterministic record
id are derived. A normal outer idempotency claim using the same key fails closed
without creating or altering a settlement; it is not treated as a partial pair
to repair. There is no unpaired settlement-create manager/store seam.

`AllocationManager` quotes Mint/Transfer fees only after winning its outer
idempotency claim and before wallet/NFT/broadcast effects. The fee manager is a
required dependency: configuration read failures reject the allocation. Gross,
fee, net, and schedule version are pinned into the operation and replay payload;
duplicate allocation keys never requote. The outer allocation key participates
in the inner blockchain-operation key so two legitimate purchases with different
allocation keys cannot collapse. Mint fees settle as retained unminted supply.
Until a treasury transfer primitive lands, a nonzero Transfer fee rejects before
wallet/NFT/broadcast effects; zero-fee transfers sign from the caller's existing
source wallet and deliver to the route target's wallet. Self-transfer is rejected.

A chain-observed operation stores `IdempotencyResultPayload` on its row.
Reconciliation uses that serialized owner result, not a raw transaction hash,
when settling an orphaned outer claim. Once a transaction hash is observed,
failure of the outer completion write must leave the claim recoverable rather
than marking an already-broadcast effect Failed.

`NftManager.TransferAsync` reads the effective Transfer entry before its first
NFT read, ownership mutation, or operation write. A nonzero flat amount or bps
(including one that rounds to zero for a unit NFT) rejects the direct route until
the version-pinned, on-chain settlement flow replaces this containment gate. An
unavailable or malformed schedule also rejects, preventing the direct route from
bypassing configured fee policy while the settlement executor is inactive.

`NftManager.MintAsync` is the direct and action route. It applies the same
governance guard and rejects unavailable, malformed, or nonzero Mint entries
before KYC, Holon, or operation work because no direct Mint settlement exists.
Allocation persists its metadata through the shared `NftHolonFactory` only
after it has made and persisted a version-pinned quote inside its idempotency
claim; it neither reuses the direct route nor creates a second pending operation.
All three direct NFT mutations apply the governance guard before their writes.

Unexpected allocation or fungible-token exceptions always bubble to the host
logging boundary. Before entering a value-effect call, recovery may fail the
owned claim with a fixed non-sensitive message. Once a provider/value effect may
have started, the claim stays `InProgress` for reconciliation; never persist
`ex.Message` or mark an ambiguous effect Failed, because that can authorize a
duplicate broadcast.

`BlockchainOperationManager` resolves and stamps canonical `ChainType` plus
`ChainNetwork` before its first persistence write. The network is part of the
idempotency fingerprint: identical economics on Devnet and Mainnet are distinct
operations. Reconciliation uses those persisted coordinates and resolves legacy
default-chain rows at their persisted network.

Provider `AZOAResult<T>` failures are expected economic outcomes and remain
durable failed operations. A thrown provider exception is unexpected: it bubbles
to the host logging boundary only after recovery. With a durable transaction hash,
the row becomes `PendingConfirmation` and the claim stays nonterminal for chain
reconciliation. Without a hash, the row becomes `Unknown` and the claim is failed
terminally so automatic rebroadcast is impossible; manual reconciliation owns the
ambiguity. Only `BlockchainProviderNotFoundException` is translated to a generic
validation result—never classify providers by parsing exception messages. Apply
the same rule to governance managers: validation and CAS conflicts return results;
infrastructure or programming exceptions bubble.

`NodeTransparencyManager` projects those operator-only models into a separate
anonymous contract. It never forwards internal response objects because they
carry actor ids and raw JSON. It validates stored typed snapshots, returns a
bounded keyset page, and substitutes one generic unavailable message for store
error results so an anonymous caller cannot mine infrastructure details.

## §action-dependency-enforcement

**final-hardening F4 — dependencies are ENFORCED at run start (fail-closed).**
`ActionManager.CheckDependencySatisfactionAsync(action)` is the single source of truth for
what "satisfied" means: each `ActionDependency` is satisfied iff the depended-on action has
≥1 `Succeeded` run. It takes an ALREADY-AUTHORIZED action and does NO ownership scoping.
Two callers:
- `CheckDependenciesAsync(actionId, userId)` — the public manual-check endpoint —
  OWNER-scopes first (`LoadOwnedActionAsync`, IDOR) then delegates.
- `EnforceDependenciesAsync(action, request)` — the run-start gate — passes the action
  already authorized by `LoadStartableActionAsync`, so a NON-OWNER marketplace run is not
  rejected. **Bug fixed (action-invitations-approval):** the gate previously routed through
  the owner-scoped `CheckDependenciesAsync`, so ANY non-owner run of a public action failed
  with "Action is owned by a different user" (fail-closed) — it broke marketplace runs, not
  just invite-gated ones. Called on BOTH run-start paths (`ExecuteAsync` legacy +
  `StartWorkflowRunAsync` durable) AFTER DAG validation and BEFORE any run/exec rows are
  written, so an unsatisfied dependency rejects cleanly with no orphaned run.

- **Fail-closed.** A faulted dependency check (not just an unsatisfied one) also rejects
  the run — we must not start when we cannot prove the dependencies hold.
- **Placement vs F6.** The gate sits before the F6 TOCTOU publish-state confirm in
  `ExecuteAsync` and after the fan-out check in `StartWorkflowRunAsync`. It touches
  neither the publish/unpublish/status-transition methods nor `TryConfirmActionStateAsync`
  — those are the F6 seams.

## §ecosystem-tree — ActionGraphBundleManager ecosystem tree + tree-walking codegen (D2)

**final-hardening-cutover Phase D / D2** (absorbs `star-odk-ecosystem-tree`). A
ActionGraphBundle owns an **Ecosystem**: a TREE of `EcosystemNode`s, each attaching a
`DappSeries` (or a nested ActionGraphBundle) as a composable dApp. Storage POCOs:
`Persistence/SurrealDb/Models/{Ecosystem,EcosystemNode}.cs`; store
`IEcosystemStore` / `SurrealEcosystemStore`. Domain shapes (Guid-typed) live in
`Models/Ecosystem/EcosystemModels.cs`.

**Tree modelling.** `Ecosystem` is the root record (one per ActionGraphBundle, keyed by
`action_graph_bundle_id`). `EcosystemNode.parent_node_id` self-references to form the tree;
`parent_node_id == null` means the node hangs directly off the ecosystem root.
`ref_kind` + `ref_id` name the attached entity — `ref_id` is a **polymorphic**
Guid('N') hex (a DappSeries.Id OR an ActionGraphBundle.Id), NOT a typed `record<>` FK,
because SurrealDB `record<>` columns are single-table.

**IDOR.** `AddDappSeriesAsync` / `GetEcosystemAsync` scope by route ActionGraphBundle id +
authenticated `userId` (ActionGraphBundle precedent, `§`-less: same `IsOwnedBy` guard as
`CreateOrUpdateAsync`). The attached DappSeries/ActionGraphBundle is **re-validated** to be
owned by the same user (`ValidateRefOwnershipAsync`) — a caller cannot attach
another user's series. No owner id is ever read from the request body.

**Cycle guard.** `AssembleTree` mirrors the holon parent-cycle precedent
(`§holon-parent-cycle`): while folding the flat node list into a parent/children
tree it walks each node up its parent chain tracking a `visited` set; a repeat id
sets `cycleError` and the operation fails. `AddDappSeriesAsync` also rejects a
`parentNodeId` that is not in the same ecosystem.

**Tree-walking codegen.** `GenerateEcosystemCode` composes the single-dApp
descriptor (`GenerateDappCode` shape) across the whole tree depth-first
(`WalkNode`) into one composed JSON artifact stored on the owning ActionGraphBundle's
`GeneratedCode`. It regenerates on every attach. **Value caveat:** the descriptor
records the target chain but does NOT itself move value; real cross-chain value in
the composed tree flows through the Phase-B bridge — **Algorand real, Solana
fail-closed**. Do not claim end-to-end Solana ecosystem value works.

## §action-run-quota — marketplace run-start quota (treasury/runner-drain guard)

**Problem.** A public action's run-start mints a fresh `RunId` every time, so the
per-`(run, node)` idempotency surface does nothing against unbounded *fresh* runs:
a hostile (or buggy) client can spin up arbitrarily many marketplace runs of one
action and drain whatever the action's economic nodes move.

**Guard.** `ActionManager.EnforceRunQuotaAsync(actionId, userId, isOwner)` is called
on BOTH run-start seams (`ExecuteAsync` + `StartWorkflowRunAsync`) right after the
startable-action load and BEFORE any run/exec rows are written — a breach rejects
cleanly with no orphaned run. It counts the user's NON-terminal runs of that action
(`IActionRunStore.GetByActionIdAsync` filtered by `UserId` + `!Status.IsTerminal()`)
and rejects when the count ≥ the configured ceiling.

**Config-driven** (`ActionDefinition` section, bound once at construction into
`ActionRunQuotaOptions` — user global rule: config over hardcoded):

| Key | Meaning |
|---|---|
| `ActionDefinition:MaxRunsPerUserPerAction` | Non-owner ceiling of concurrent non-terminal runs per action. `<= 0` disables the quota. |
| `ActionDefinition:OwnerLimitMultiplier` | Owner's ceiling = base × this. `<= 0` ⇒ owner exempt (unbounded). |

**Fail-closed.** A faulted run-store count rejects the start (we cannot prove the
user is under quota). Owner running their own action is not draining a foreign
treasury, so they get the multiplied (or unbounded) ceiling.

## §economic-consent — pre-run disclosure + consent gate for marketplace runs

**Problem (CRITICAL enabling gap).** A public action's `Transfer`/`Swap`/`Grant`/
`Refund`/`FungibleTokenCreate`/`Bridge`/… nodes auto-fire against the RUNNER (the
`ActingUserId` invariant — side-effects run as the runner, not the owner) with no
disclosure. A non-owner could commit a run without ever seeing that it moves their
assets.

**Manifest.** `ActionEconomicManifestBuilder.Build` (see
`Services/Actions/AGENTS.md §economic-manifest`) walks the DAG in `ExecutionOrder` and
lists every value-moving node (a node whose registered handler declares
`RequiresChainCapability`, OR whose type is in the explicit economic set) with a
best-effort declared destination/amount. Surfaced two ways:

- **Preview** — `PreviewRunAsync(actionId, userId)` returns the manifest for a
  startable action (scoped exactly like a run-start) so the frontend can disclose
  "this action moves assets" BEFORE the runner commits. (Controller wiring to a
  `GET …/preview` route is a follow-up owed to the controller owner.)
- **Consent gate** — `EnforceEconomicConsent(action, isOwner, acknowledgeEconomicEffects)`
  on BOTH run-start seams. For a NON-owner run whose manifest has ≥1 economic node,
  the start is rejected unless `acknowledgeEconomicEffects == true`. Owner runs are
  exempt (they authored the action). The flag is a new optional param on
  `ExecuteAsync` / `StartWorkflowRunAsync` (defaults false → existing callers
  unchanged); the controller currently always passes the default, so the gate is
  **fully enforced at the manager** but the runner-facing "I acknowledge" toggle is a
  controller/frontend follow-up.

**Scope note / deferred.** This ships the manifest + the acknowledge-flag gate + the
preview surface — the smallest honest thing that closes "no consent surface exists".
A *persisted* consent-grant (an auditable record that user X acknowledged manifest
hash H at time T) is deliberately deferred as a follow-up; the acknowledge flag is
transient per-call today.

## §dapp-series-duplicates — (series, action) row uniqueness in DappCompositionManager

A `DappSeriesAction` row is logically unique per `(seriesId, actionId)`, but the store
does not enforce it — so a duplicate row is a corrupt-state possibility the manager
must tolerate WITHOUT crashing. The hazard: several validators key entries by
`ActionIdGuid` via `ToDictionary(e => e.ActionIdGuid)`, which throws
`ArgumentException` on a duplicate key and surfaces as a 500.

Three coordinated guards keep the whole surface duplicate-safe:

1. **`AddActionAsync` rejects up front.** Before inserting, it checks the existing
   series entries and refuses a second add of the same action — the primary
   prevention (a duplicate row should never be created through the normal path).
2. **`ValidateAsync` surfaces pre-existing duplicates as a clean validation
   failure**, not a crash: it `GroupBy(ActionIdGuid)` and reports each duplicated
   action as a diagnostic (`InputMappingConsistency`/`NoCircularDependencies` set
   false), instead of letting the downstream `ToDictionary` throw. This catches
   corrupt rows that predate guard #1 or were written out-of-band.
3. **`OrderByActionId` is duplicate-tolerant** by construction: it builds the lookup
   with an indexer loop (`map[e.ActionIdGuid] = order`), last-writer-wins, so even
   an unexpected duplicate never throws out of the order lookup. Consistent with the
   `actions[actionGuid] = …` indexer assignment in `ValidateAsync` (also
   last-writer-wins), which `ValidateAsync`'s duplicate rejection makes moot anyway.

## §published-version-hash — immutable published action version (bait-and-switch guard)

**Problem.** A creator could `unpublish → edit a benign node into a Transfer →
republish` under the SAME action id that runners already trust, with no visible change
of identity.

**Guard.** `PublishAsync` computes a stable content hash of the node/edge graph
(`ActionPublishedVersion.ComputeHash` — SHA-256 over id-sorted node
type/name/entry/terminal/config + endpoint-sorted edge shape) and stores it on
`ActionDefinition.PublishedVersionHash`. Every run-start stamps the action's current hash onto
`ActionRun.PublishedVersionHash`, binding the run to the exact revision the runner
saw/consented to. A later edit + republish recomputes a DIFFERENT hash, so:

- the hash is exposed on the action (and thus on marketplace listing reads) — a
  changed hash is a visible new version, not a silent mutation;
- a historical run whose stamp no longer equals the live action's hash is detectable
  evidence of a post-hoc tamper.

**Persistence.** `published_version_hash` is a plain `option<string>` column on both
`action` and `action_run` (not an FK). Mapped in `SurrealActionStore` /
`SurrealActionRunStore`; POCOs + goldens updated. Null while an action was never
published.

**Deferred.** Full immutable-snapshot STORAGE of the whole published graph (so an old
run can be replayed against the exact bytes) is a follow-up; this ships the hash +
run-stamping + surfacing, which makes tampering DETECTABLE and binds runs — the
correctness floor. Recomputation on republish is intentional (the new hash IS the new
version identity).

## §action-invitations — invite-gated actions + request/approve flow

*Track: action-invitations-approval.* Adds a **run-authorization** dimension
(`ActionDefinition.RunAccess`) orthogonal to `IsPublic` (discoverability). `Open` = today's
behaviour (anyone who can view may run/fork). `InviteOnly` = only the owner +
`ActionDefinition.InvitedUserIds` may run/fork; the action stays fully **viewable** by any
non-owner (marketplace + node graph + economic preview) — invite gating restricts
*running*, never *viewing*. `GetAsync` is unchanged (`owner || IsPublic`).

### The single run-start chokepoint
`LoadStartableActionAsync` gates BOTH `ExecuteAsync` and `StartWorkflowRunAsync`. After
the `IsPublic` + `Active` checks it adds: non-owner + `RunAccess == InviteOnly` +
requester ∉ `InvitedUserIds` → reject "This action requires an invitation — request
access." The owner path (early return) is untouched — an owner always runs their own
action regardless of `RunAccess`.

`ForkAsync` re-runs the action's nodes, so it carries the SAME gate (checked on the
RUN's user, `parent.UserId`, not the caller) alongside the fork run-quota gate.

**Revoked-mid-run semantics (decided):** the gate fires only on NEW starts/forks. An
in-flight run whose user lost their invitation is *not* torn down — it may finish and
even fork from its still-Running state, because the fork gate reads the live
`InvitedUserIds` but the parent run was already authorized at start. Revocation blocks
*future* starts, not the current run. (If a harder "revoke kills in-flight" is ever
needed, gate the per-node step handler too — deliberately out of scope here.)

### Request/approval state machine (`ActionAccessRequest`)
`Pending → Approved` (owner; appends `RequesterUserId` to `InvitedUserIds`),
`Pending → Rejected` (owner), `Pending → Withdrawn` (requester). Approved/Rejected/
Withdrawn are **terminal + immutable** — the store has NO status-CAS, so the manager
enforces `Status == Pending` before every transition and rejects a terminal→* move.

**Idempotency (≤1 Pending per (action, requester)):** `RequestAccessAsync` first calls
`GetPendingForActionAndRequesterAsync`; a live Pending is returned as-is (idempotent), so
a double-submit never opens two rows. A prior Rejected/Withdrawn does NOT block — a fresh
Pending opens. Owner-requesting or already-invited → rejected at the manager with a clear
message (no request needed).

### IDOR scoping
- Owner ops (`SetRunAccess`, `Invite`, `RevokeInvite`, `ListAccessRequests`) go through
  `LoadOwnedActionAsync` — a non-owner gets the owner-mismatch rejection.
- `DecideAccessRequestAsync` loads the request, then `LoadOwnedActionAsync` on the
  request's action — a non-owner of THAT action cannot decide it (cross-action IDOR closed).
- `WithdrawAccessRequestAsync` verifies `request.RequesterUserId == caller` — one
  requester cannot withdraw another's request.
- Controller reads the caller from the JWT (`GetUserIdFromClaims`); any body-supplied
  user is ignored (`Invite` body carries the *target*, not the actor).

Owner is always implicitly invited — never stored in `InvitedUserIds` (`SetRunAccess`
seed + `InviteUser` filter the owner out). Invite add/revoke are idempotent (add skips
a duplicate write; revoke no-ops when absent).

### DI owed
`Program.cs` must register the store: `builder.Services.AddScoped<IActionAccessRequestStore, SurrealActionAccessRequestStore>();` (the concrete store is owned by the persistence worker). `ActionManager` takes it as an OPTIONAL trailing ctor param (like `IConfiguration`) so the positional unit-test fixtures keep compiling; DI always supplies the real store.

## user-dapp-rbac — `UserManager.AssignDappRoleAsync`

- Persists a target user's `DappRole` through `IUserStore.UpsertAsync`
  (`UpdateAsync` deliberately never touches DappRole — role changes go only through
  this dedicated, authority-gated path).
- Authority ladder (fail-closed): operator sets anything incl. manager (bootstrap);
  a manager sets only developer/user; everyone else denied. Role validated against
  the `AzoaDappRoles` allowlist first, so an operator:admin-yielding value is
  impossible. Authority is passed as `bool actingIsOperator, bool actingCanManage`
  (computed by the controller from claims) to keep the manager free of ClaimsPrincipal.

## Allocation receipt boundary

`AllocationReceiptManager` reconstructs the internal ledger identity only from
the API-key caller context plus the required client idempotency key. It locates
the durable operation through the opaque correlation and then requires exact
persisted API-key and initiator-user matches. Missing ledger bindings, foreign,
and mismatched operation paths all return the same not-found result. A valid
failed/in-progress ledger with no operation is still returned as its own receipt.

Reconciliation is an observation-only call to `IReconciliationService`; it never
resubmits an allocation, retries a provider action, or uses a raw operation id
supplied by the caller. The receipt is projected from safe allocation facts only.
Dependency failures become a generic unavailable result after cancellation is
allowed to propagate; they never masquerade as an absent receipt.

## Participant readiness ordering

Economic route-availability checks (node governance and direct-route fee
containment) intentionally run before `IValueAccessService` participant
readiness. They are configuration-only, side-effect-free denials and avoid
consulting participant verification for a route the node cannot offer. Once a
route is available, value access is required before any idempotency claim,
wallet/holon mutation, or blockchain operation write.
## §rail-amount — the amount at the provider boundary, and what still caps it

`BlockchainOperationManager` hands providers a `RailAmount` (`Core/AGENTS.md`
§rail-amount). Two paths reach it, and they do not have the same range:

- **Burn / Transfer** read `Parameters["Amount"]`, which was always a string, and parse it
  straight to `BigInteger`. Not capped.
- **Mint** reads the typed `IMintOperation.Amount`, which is still `ulong` on the operation
  record, and widens losslessly. **Capped at `ulong.MaxValue` until that record widens** —
  an Ethereum mint above ~18.45 ETH cannot be expressed through the op row even though the
  provider surface can now carry it. Widening `IMintOperation.Amount` plus the `OperationLog`
  POCO and its Surreal mapping is the remaining half of D13.

`DeriveIdempotencyKey` is deliberately untouched by the widening: it reads the same fields it
always did, in the same string form, so no previously-settled key changes meaning.

## §chain-stamped-signing — one place stamps the chain (custody option 4)

`BuildSigningContext` stamps `SigningContext.ChainType` with the settling provider's
`ChainType` on EVERY context it builds — platform and per-user alike. That single site is
why no provider needs an edit and none can drift: a provider that forgets to name its chain
gets the custody door's Algorand-pinned legacy path and fails closed. Per-user contexts do
not strictly need it (those keys come from the wallet record, which is already chain-correct)
but stamping them too removes the "which contexts carry it" question entirely.

## §credential-revocation — CredentialManager (D19, implemented and gated)

`CredentialManager.RevokeAsync` is the seam behind both the `CredentialRevoke` node and
`POST /api/credential/{assetId}/revoke`. It claws a soulbound credential back to its
issuer with an Algorand `AssetClawback` — the only path in this codebase that moves an
asset out of an account that did not sign. The chain-level proofs and the node-level
rules live in `Services/Actions/AGENTS.md` §credential-revoke; what this directory
decides is the three manager-level facts below.

**Status first: the node type is on `NotEnabledActionNodeTypes` and the server refuses
it** (`Services/Actions/AGENTS.md` §not-enabled-node-types). The rail is written,
reviewed and tested; it is not runnable. The blocking defect is that the only mint path
stamps `issuer_user_id` = the holder, so the authorization below — correct as written
— currently decides nothing. Tracked by
`conductor/tracks/credential-third-party-issuance_20260906`.

1. **Authorization is the recorded ISSUER, compared against the acting user (node path)
   or the authenticated caller (REST path).** Never `current_owner` / `NftOwnership.UserId`
   — that is the HOLDER, and authorizing on it inverts the entire primitive. Never the
   mint's `operation_log.user_id` either: a mint proxied through a tenant credential
   stamps the tenant. A row with no recorded issuer is **unrevocable and fails closed**.
   `credential:revoke:sign` is its own scope for the same reason: a user who consented to
   "sign my transfers" must not thereby consent to a clawback of an asset they hold.
2. **Idempotency is partitioned by caller AND by asset.** One credential's cached result
   can never be replayed as another's, and a redelivered request never submits a second,
   irreversible clawback.
3. **`PendingConfirmation` is not a success, and says so twice.** A broadcast that did not
   confirm in the poll window comes back with `Status = PendingConfirmation`, the tx hash,
   `IsError = true`, and `Code = CredentialRevokeCodes.PendingConfirmation`. `IsError` —
   not a nested status string — is what every AZOA client helper branches on, so a
   status-only signal would read as a completed revocation. The idempotency claim stays
   `InProgress` so a duplicate replays "in progress" rather than re-broadcasting, and only
   a CONFIRMED clawback moves ownership through
   `ICredentialIssuerStore.RecordCredentialRevokedAsync`.

## §parked-run-sweep — re-probing runs parked on chain truth (W4.5)

A run whose chain action came back indeterminate parks in
`ActionRunStatus.AwaitingReconciliation` rather than retrying, because retrying a mint whose
outcome is unknown is how a double-mint happens. Something must therefore come back and ask the
chain again. That is this sweep.

Three decisions are worth stating, because each is easy to undo by accident.

1. **One implementation, two seams.** `ActionManager` implements `IParkedRunReconciler`
   alongside `IActionManager`, and both the operator-triggered
   `SweepReconciliationAsync` (`POST /api/action/runs/reconcile-sweep`) and the unattended
   `ReconcileAllParkedRunsAsync` (driven by `ReconciliationHostedService`) delegate to the same
   private `SweepParkedRunsAsync`. AZOA has two dispatch seams and a control enforced at only
   one of them is not enforced; the same rule applies to the thing that repairs them.

2. **Hard-stuck is judged AFTER the re-probe, never before.** A run whose park is old but which
   this pass just resolved is not stuck. Judging on park age alone would page an operator about
   runs the sweep had already fixed, and an alert that cries wolf is an alert nobody reads. See
   `SweepParkedRunsAsync`: the `hardStuckFloor` comparison sits inside the branch that checks
   the run is *still* `AwaitingReconciliation` after `ReprobeReconciliationAsync`.

3. **The report records; it does not decide.** `ParkedRunSweepReport.HardStuck` carries the run,
   its owner, how long it has been parked and how many nodes are still indeterminate, and hands
   that to the caller to alert on. The sweep never fails, cancels or force-completes a run: a
   value movement whose outcome is unknown is a question for a person, not for a timer.

One behaviour change worth knowing at the call site: `SweepReconciliationAsync` caps at
`DefaultParkedRunBatchSize` (500) runs per call where it was previously unbounded. An operator
draining a backlog larger than that calls it repeatedly; the background tick drains the rest
regardless.

## AML lineage projections

Custodial readiness and the operator AML queue use the current approval's AML
lineage, matching both value gates. The submission DTO carries this correlation
only within the process (`JsonIgnore`); it is not an API field. Operator decisions
append evidence under the same lineage and refuse obsolete screening references.
An approval without linked evidence remains under review and cannot be cleared by
an unrelated operator screening.

## Credential lifecycle network binding

Revocation resolves explicit historical network on initial admission and the immutable saved lifecycle on replay. A default-network change cannot redirect a retry. Cancellation tombstones return a typed cancellation before provider or credential projection access.

## Native operation projections

`BlockchainOperationManager` routes `DeployContract` and `CallContract` through the typed deployment journal, using `Parameters.DeploymentRequest` and an explicit request key. The projection ID is the scoped durable deployment ID. Its native state is refreshed from the deployment receipt on reads; preparation/submission never becomes success through generic operation settlement. Interactive continuation and cancellation use the deployment API. Action-native admission uses the frozen deployment node contract, not this general caller entry point. The old synchronous provider mutation wrappers are removed; Ethereum read-only calls use `IReadOnlyContractModule`.

### Consent provisioning authority

ConsentManager only serves scoped grant reads. User/tenant mutations use ConsentLifecycleService and the atomic lifecycle store. TenantManager provisioning requires a tenant witness and a registered atomic grant issuer. TenantCustodialAccountManager carries that same witness through idempotent ensure replay and convergence; it never fabricates a child principal. Identity creation remains create-only and precedes grant admission; a rejected grant produces an explicit failed provisioning result. Unexpected ensure failures attempt idempotency recovery then propagate to the centralized boundary.

TenantCustodialAccountManager uses BootstrapTenantWalletAsync, passing the original full TenantProvision/WalletManage/KycRead witness. It narrows only the identity-grant issuance witness to TenantProvision; it cannot synthesize a child principal. WalletManager delegates both existing and fresh deterministic wallet candidates to BootstrapTenantAsync and does not return historical wallet data before current tenant checks. General owner bootstrap remains separate.

### Claim credential recovery

Claims require the live invite and expected target ID from the invitation; a JWT subject alone cannot transfer ownership. The expected ID is a correlation check, never authorization. Consumers retain that ID before submitting. Lost successful responses recover through ordinary password login by userId (SDK loginByUserId), or a fresh wallet challenge. Consumed invites never mint replay JWTs. Password login accepts exactly one identity (email or userId), requires an active self-owned user, and verifies the current password. A failed wallet claim requires a fresh challenge but leaves the invite reusable if the ownership transaction did not commit. No password-derived command fingerprint or credential journal is stored.

TenantCustodialAccountManager keeps historical identity correlation separate from current access. GetStatus, provisioning convergence and cached response release require the actual tenant witness and exact current provisioning enrollment. ResolveCorrelatedChildAsync intentionally returns identity only; it is followed by current custody authority capture. Infrastructure errors in KYC orchestration preserve the original exception after updating an owned idempotency claim. New KYC effects require the captured witness at the datastore admission boundary in addition to response validation.


Tenant custody KYC calls BeginCustodialAsync/SubmitCustodialAsync with the captured
current enrollment witness. These select dedicated atomic store admissions;
ordinary self-service KYC retains its actual-user controller authority. Final
response validation is retained after reads/effects so historical provisioning
identity cannot reveal a claimed user's current wallet or KYC state. Existing
remote effects are accounted independently of later consent revocation.
