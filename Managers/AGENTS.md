# Managers — design notes

## §skip-semantics — QuestManager.ExecuteAsync skip loop

The in-process executor walks nodes in topological order and skips a node when
any incoming edge qualifies:

| Edge type    | Skip condition (post quest-dag-semantic-hardening FR-1) |
|---|---|
| `Control`    | source state is `Failed` **or `Skipped`** |
| `Conditional`| source state is `Failed` **or `Skipped`**, regardless of `Condition` text |

Prior to FR-1 the rules were weaker: Control skipped only on `Failed`; Conditional
required `!string.IsNullOrEmpty(Condition)` (so empty-condition edges were inert,
letting downstream nodes run through a failed gate). The P0 hazard: a payout chain
behind a failed eligibility GateCheck would run from the second hop onward.

The `HIGH#7 G2` `expectedState: QuestNodeState.Pending` guard on the skip
update-row call is unchanged — it prevents a concurrent `ForkAsync` cancellation
from being silently overwritten.

The **durable engine** (`Services/Quest/Workflow/`) has no equivalent skip seam —
see `Services/Quest/Workflow/AGENTS.md §skip-semantics`.

## §publish-lifecycle — Quest definition status

A Quest definition carries a `Status` field (`QuestStatus`, default `Draft`)
introduced by `quest-dag-semantic-hardening` FR-2. The lifecycle:

```
Draft ──publish──▶ Active ──unpublish──▶ Draft
```

- **Publish** (`PublishAsync`): runs the full validation stack (structural DAG +
  engine-profile fan-out check as error + per-node config strict round-trip), then
  flips `Draft → Active` via the F6 compare-and-swap (see below) BEFORE persisting
  fresh `ExecutionOrder`. The CAS is the serialization point — a lost race returns
  a publish-conflict error, never a torn double-publish.
- **Unpublish** (`UnpublishAsync`): refused while any `QuestRun` for the quest is
  non-final; otherwise flips `Active → Draft` via the same CAS (conflict on a lost
  race).
- **Execute / StartWorkflowRunAsync**: require `Status == Active`, then re-confirm
  the definition is STILL Active at the version read (F6 `TryConfirmQuestStateAsync`)
  before committing a run; a concurrent unpublish that moved the row yields a
  run-conflict error. Error message names the publish requirement explicitly.
- **Mutation endpoints** (`AddNodeAsync`, `UpdateNodeAsync`, `DeleteNodeAsync`,
  `AddEdgeAsync`, `RemoveEdgeAsync`): rejected when `Status == Active`; caller
  must unpublish first.

Execute-time re-validation is kept as defence-in-depth (does not replace the
publish gate; both run independently).

**TOCTOU guard — optimistic concurrency (F6, RESOLVED).** The Quest definition
carries a `Version` (`long`) optimistic-concurrency token (`quest.version` column,
default 0). Every lifecycle status transition is a compare-and-swap against it,
mirroring the bridge's `TryTransitionBridgeStatusAsync` discipline (return the
affected-row count VERBATIM; the store never asserts==1, retries, or RMW):

- `IQuestStore.TryTransitionQuestStatusAsync(id, expected, next, expectedVersion)`
  issues one conditional `UPDATE ... SET status = $next, version = version + 1
  WHERE status = $expected AND version = $expectedVersion RETURN AFTER`. Affected
  == 1 → won (status flipped, version bumped); 0 → lost the race / stale version.
  Used by `PublishAsync` (Draft→Active) and `UnpublishAsync` (Active→Draft).
- `IQuestStore.TryConfirmQuestStateAsync(id, expected, expectedVersion)` is a
  conditional no-op self-write (`SET version = version WHERE status = $expected
  AND version = $expectedVersion`) returning affected count without mutating the
  row. Used by `ExecuteAsync` / `StartWorkflowRunAsync` at run-start to confirm
  the definition has not been unpublished/modified since the ownership read.

This closes all three TOCTOU classes: (a) unpublish racing a run-start (the
confirm misses once unpublish bumps version), (b) double-publish (both CAS the
same Draft/version; exactly one wins), (c) mutate-on-Active races (any status
move bumps version, invalidating a stale reader's expected token). The in-flight
run check in `UnpublishAsync` is retained as an additional, earlier gate. Both
`SurrealQuestStore` and `InMemoryQuestStore` implement the CAS faithfully.

Reference-sharing note: the in-memory store's CAS mutates the same `Quest`
instance the manager holds, so the manager assigns the authoritative post-CAS
shape (`version = expectedVersion + 1`, by assignment not `+=`) rather than
double-incrementing.

## §holon-parent-cycle — HolonManager parent-cycle guard

`EnsureNotDescendantAsync(holonId, proposedParentId)` is a shared private helper
introduced by `quest-dag-semantic-hardening` FR-6. It:

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

## §holon-type-registry — opt-in Holon AssetType/metadata registry (F5)

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
platform-wide constraint on every tenant's holons. No per-avatar ownership: this is
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

`NftManager.MintAsync` is the direct and quest route. It applies the same
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

## §quest-dependency-enforcement

**final-hardening F4 — dependencies are ENFORCED at run start (fail-closed).**
`QuestManager.CheckDependencySatisfactionAsync(quest)` is the single source of truth for
what "satisfied" means: each `QuestDependency` is satisfied iff the depended-on quest has
≥1 `Succeeded` run. It takes an ALREADY-AUTHORIZED quest and does NO ownership scoping.
Two callers:
- `CheckDependenciesAsync(questId, avatarId)` — the public manual-check endpoint —
  OWNER-scopes first (`LoadOwnedQuestAsync`, IDOR) then delegates.
- `EnforceDependenciesAsync(quest, request)` — the run-start gate — passes the quest
  already authorized by `LoadStartableQuestAsync`, so a NON-OWNER marketplace run is not
  rejected. **Bug fixed (quest-invitations-approval):** the gate previously routed through
  the owner-scoped `CheckDependenciesAsync`, so ANY non-owner run of a public quest failed
  with "Quest is owned by a different avatar" (fail-closed) — it broke marketplace runs, not
  just invite-gated ones. Called on BOTH run-start paths (`ExecuteAsync` legacy +
  `StartWorkflowRunAsync` durable) AFTER DAG validation and BEFORE any run/exec rows are
  written, so an unsatisfied dependency rejects cleanly with no orphaned run.

- **Fail-closed.** A faulted dependency check (not just an unsatisfied one) also rejects
  the run — we must not start when we cannot prove the dependencies hold.
- **Placement vs F6.** The gate sits before the F6 TOCTOU publish-state confirm in
  `ExecuteAsync` and after the fan-out check in `StartWorkflowRunAsync`. It touches
  neither the publish/unpublish/status-transition methods nor `TryConfirmQuestStateAsync`
  — those are the F6 seams.

## §ecosystem-tree — STARManager ecosystem tree + tree-walking codegen (D2)

**final-hardening-cutover Phase D / D2** (absorbs `star-odk-ecosystem-tree`). A
STARODK owns an **Ecosystem**: a TREE of `EcosystemNode`s, each attaching a
`DappSeries` (or a nested STARODK) as a composable dApp. Storage POCOs:
`Persistence/SurrealDb/Models/{Ecosystem,EcosystemNode}.cs`; store
`IEcosystemStore` / `SurrealEcosystemStore`. Domain shapes (Guid-typed) live in
`Models/Ecosystem/EcosystemModels.cs`.

**Tree modelling.** `Ecosystem` is the root record (one per STARODK, keyed by
`star_odk_id`). `EcosystemNode.parent_node_id` self-references to form the tree;
`parent_node_id == null` means the node hangs directly off the ecosystem root.
`ref_kind` + `ref_id` name the attached entity — `ref_id` is a **polymorphic**
Guid('N') hex (a DappSeries.Id OR a STARODK.Id), NOT a typed `record<>` FK,
because SurrealDB `record<>` columns are single-table.

**IDOR.** `AddDappSeriesAsync` / `GetEcosystemAsync` scope by route STARODK id +
authenticated `avatarId` (STARODK precedent, `§`-less: same `IsOwnedBy` guard as
`CreateOrUpdateAsync`). The attached DappSeries/STARODK is **re-validated** to be
owned by the same avatar (`ValidateRefOwnershipAsync`) — a caller cannot attach
another avatar's series. No owner id is ever read from the request body.

**Cycle guard.** `AssembleTree` mirrors the holon parent-cycle precedent
(`§holon-parent-cycle`): while folding the flat node list into a parent/children
tree it walks each node up its parent chain tracking a `visited` set; a repeat id
sets `cycleError` and the operation fails. `AddDappSeriesAsync` also rejects a
`parentNodeId` that is not in the same ecosystem.

**Tree-walking codegen.** `GenerateEcosystemCode` composes the single-dApp
descriptor (`GenerateDappCode` shape) across the whole tree depth-first
(`WalkNode`) into one composed JSON artifact stored on the owning STARODK's
`GeneratedCode`. It regenerates on every attach. **Value caveat:** the descriptor
records the target chain but does NOT itself move value; real cross-chain value in
the composed tree flows through the Phase-B bridge — **Algorand real, Solana
fail-closed**. Do not claim end-to-end Solana ecosystem value works.

## §quest-run-quota — marketplace run-start quota (treasury/runner-drain guard)

**Problem.** A public quest's run-start mints a fresh `RunId` every time, so the
per-`(run, node)` idempotency surface does nothing against unbounded *fresh* runs:
a hostile (or buggy) client can spin up arbitrarily many marketplace runs of one
quest and drain whatever the quest's economic nodes move.

**Guard.** `QuestManager.EnforceRunQuotaAsync(questId, avatarId, isOwner)` is called
on BOTH run-start seams (`ExecuteAsync` + `StartWorkflowRunAsync`) right after the
startable-quest load and BEFORE any run/exec rows are written — a breach rejects
cleanly with no orphaned run. It counts the avatar's NON-terminal runs of that quest
(`IQuestRunStore.GetByQuestIdAsync` filtered by `AvatarId` + `!Status.IsTerminal()`)
and rejects when the count ≥ the configured ceiling.

**Config-driven** (`Quest` section, bound once at construction into
`QuestRunQuotaOptions` — user global rule: config over hardcoded):

| Key | Meaning |
|---|---|
| `Quest:MaxRunsPerAvatarPerQuest` | Non-owner ceiling of concurrent non-terminal runs per quest. `<= 0` disables the quota. |
| `Quest:OwnerLimitMultiplier` | Owner's ceiling = base × this. `<= 0` ⇒ owner exempt (unbounded). |

**Fail-closed.** A faulted run-store count rejects the start (we cannot prove the
avatar is under quota). Owner running their own quest is not draining a foreign
treasury, so they get the multiplied (or unbounded) ceiling.

## §economic-consent — pre-run disclosure + consent gate for marketplace runs

**Problem (CRITICAL enabling gap).** A public quest's `Transfer`/`Swap`/`Grant`/
`Refund`/`FungibleTokenCreate`/`Bridge`/… nodes auto-fire against the RUNNER (the
`ActingAvatarId` invariant — side-effects run as the runner, not the owner) with no
disclosure. A non-owner could commit a run without ever seeing that it moves their
assets.

**Manifest.** `QuestEconomicManifestBuilder.Build` (see
`Services/Quest/AGENTS.md §economic-manifest`) walks the DAG in `ExecutionOrder` and
lists every value-moving node (a node whose registered handler declares
`RequiresChainCapability`, OR whose type is in the explicit economic set) with a
best-effort declared destination/amount. Surfaced two ways:

- **Preview** — `PreviewRunAsync(questId, avatarId)` returns the manifest for a
  startable quest (scoped exactly like a run-start) so the frontend can disclose
  "this quest moves assets" BEFORE the runner commits. (Controller wiring to a
  `GET …/preview` route is a follow-up owed to the controller owner.)
- **Consent gate** — `EnforceEconomicConsent(quest, isOwner, acknowledgeEconomicEffects)`
  on BOTH run-start seams. For a NON-owner run whose manifest has ≥1 economic node,
  the start is rejected unless `acknowledgeEconomicEffects == true`. Owner runs are
  exempt (they authored the quest). The flag is a new optional param on
  `ExecuteAsync` / `StartWorkflowRunAsync` (defaults false → existing callers
  unchanged); the controller currently always passes the default, so the gate is
  **fully enforced at the manager** but the runner-facing "I acknowledge" toggle is a
  controller/frontend follow-up.

**Scope note / deferred.** This ships the manifest + the acknowledge-flag gate + the
preview surface — the smallest honest thing that closes "no consent surface exists".
A *persisted* consent-grant (an auditable record that avatar X acknowledged manifest
hash H at time T) is deliberately deferred as a follow-up; the acknowledge flag is
transient per-call today.

## §dapp-series-duplicates — (series, quest) row uniqueness in DappCompositionManager

A `DappSeriesQuest` row is logically unique per `(seriesId, questId)`, but the store
does not enforce it — so a duplicate row is a corrupt-state possibility the manager
must tolerate WITHOUT crashing. The hazard: several validators key entries by
`QuestIdGuid` via `ToDictionary(e => e.QuestIdGuid)`, which throws
`ArgumentException` on a duplicate key and surfaces as a 500.

Three coordinated guards keep the whole surface duplicate-safe:

1. **`AddQuestAsync` rejects up front.** Before inserting, it checks the existing
   series entries and refuses a second add of the same quest — the primary
   prevention (a duplicate row should never be created through the normal path).
2. **`ValidateAsync` surfaces pre-existing duplicates as a clean validation
   failure**, not a crash: it `GroupBy(QuestIdGuid)` and reports each duplicated
   quest as a diagnostic (`InputMappingConsistency`/`NoCircularDependencies` set
   false), instead of letting the downstream `ToDictionary` throw. This catches
   corrupt rows that predate guard #1 or were written out-of-band.
3. **`OrderByQuestId` is duplicate-tolerant** by construction: it builds the lookup
   with an indexer loop (`map[e.QuestIdGuid] = order`), last-writer-wins, so even
   an unexpected duplicate never throws out of the order lookup. Consistent with the
   `quests[questGuid] = …` indexer assignment in `ValidateAsync` (also
   last-writer-wins), which `ValidateAsync`'s duplicate rejection makes moot anyway.

## §published-version-hash — immutable published quest version (bait-and-switch guard)

**Problem.** A creator could `unpublish → edit a benign node into a Transfer →
republish` under the SAME quest id that runners already trust, with no visible change
of identity.

**Guard.** `PublishAsync` computes a stable content hash of the node/edge graph
(`QuestPublishedVersion.ComputeHash` — SHA-256 over id-sorted node
type/name/entry/terminal/config + endpoint-sorted edge shape) and stores it on
`Quest.PublishedVersionHash`. Every run-start stamps the quest's current hash onto
`QuestRun.PublishedVersionHash`, binding the run to the exact revision the runner
saw/consented to. A later edit + republish recomputes a DIFFERENT hash, so:

- the hash is exposed on the quest (and thus on marketplace listing reads) — a
  changed hash is a visible new version, not a silent mutation;
- a historical run whose stamp no longer equals the live quest's hash is detectable
  evidence of a post-hoc tamper.

**Persistence.** `published_version_hash` is a plain `option<string>` column on both
`quest` and `quest_run` (not an FK). Mapped in `SurrealQuestStore` /
`SurrealQuestRunStore`; POCOs + goldens updated. Null while a quest was never
published.

**Deferred.** Full immutable-snapshot STORAGE of the whole published graph (so an old
run can be replayed against the exact bytes) is a follow-up; this ships the hash +
run-stamping + surfacing, which makes tampering DETECTABLE and binds runs — the
correctness floor. Recomputation on republish is intentional (the new hash IS the new
version identity).

## §quest-invitations — invite-gated quests + request/approve flow

*Track: quest-invitations-approval.* Adds a **run-authorization** dimension
(`Quest.RunAccess`) orthogonal to `IsPublic` (discoverability). `Open` = today's
behaviour (anyone who can view may run/fork). `InviteOnly` = only the owner +
`Quest.InvitedAvatarIds` may run/fork; the quest stays fully **viewable** by any
non-owner (marketplace + node graph + economic preview) — invite gating restricts
*running*, never *viewing*. `GetAsync` is unchanged (`owner || IsPublic`).

### The single run-start chokepoint
`LoadStartableQuestAsync` gates BOTH `ExecuteAsync` and `StartWorkflowRunAsync`. After
the `IsPublic` + `Active` checks it adds: non-owner + `RunAccess == InviteOnly` +
requester ∉ `InvitedAvatarIds` → reject "This quest requires an invitation — request
access." The owner path (early return) is untouched — an owner always runs their own
quest regardless of `RunAccess`.

`ForkAsync` re-runs the quest's nodes, so it carries the SAME gate (checked on the
RUN's avatar, `parent.AvatarId`, not the caller) alongside the fork run-quota gate.

**Revoked-mid-run semantics (decided):** the gate fires only on NEW starts/forks. An
in-flight run whose avatar lost their invitation is *not* torn down — it may finish and
even fork from its still-Running state, because the fork gate reads the live
`InvitedAvatarIds` but the parent run was already authorized at start. Revocation blocks
*future* starts, not the current run. (If a harder "revoke kills in-flight" is ever
needed, gate the per-node step handler too — deliberately out of scope here.)

### Request/approval state machine (`QuestAccessRequest`)
`Pending → Approved` (owner; appends `RequesterAvatarId` to `InvitedAvatarIds`),
`Pending → Rejected` (owner), `Pending → Withdrawn` (requester). Approved/Rejected/
Withdrawn are **terminal + immutable** — the store has NO status-CAS, so the manager
enforces `Status == Pending` before every transition and rejects a terminal→* move.

**Idempotency (≤1 Pending per (quest, requester)):** `RequestAccessAsync` first calls
`GetPendingForQuestAndRequesterAsync`; a live Pending is returned as-is (idempotent), so
a double-submit never opens two rows. A prior Rejected/Withdrawn does NOT block — a fresh
Pending opens. Owner-requesting or already-invited → rejected at the manager with a clear
message (no request needed).

### IDOR scoping
- Owner ops (`SetRunAccess`, `Invite`, `RevokeInvite`, `ListAccessRequests`) go through
  `LoadOwnedQuestAsync` — a non-owner gets the owner-mismatch rejection.
- `DecideAccessRequestAsync` loads the request, then `LoadOwnedQuestAsync` on the
  request's quest — a non-owner of THAT quest cannot decide it (cross-quest IDOR closed).
- `WithdrawAccessRequestAsync` verifies `request.RequesterAvatarId == caller` — one
  requester cannot withdraw another's request.
- Controller reads the caller from the JWT (`GetAvatarIdFromClaims`); any body-supplied
  avatar is ignored (`Invite` body carries the *target*, not the actor).

Owner is always implicitly invited — never stored in `InvitedAvatarIds` (`SetRunAccess`
seed + `InviteAvatar` filter the owner out). Invite add/revoke are idempotent (add skips
a duplicate write; revoke no-ops when absent).

### DI owed
`Program.cs` must register the store: `builder.Services.AddScoped<IQuestAccessRequestStore, SurrealQuestAccessRequestStore>();` (the concrete store is owned by the persistence worker). `QuestManager` takes it as an OPTIONAL trailing ctor param (like `IConfiguration`) so the positional unit-test fixtures keep compiling; DI always supplies the real store.

## avatar-dapp-rbac — `AvatarManager.AssignDappRoleAsync`

- Persists a target avatar's `DappRole` through `IAvatarStore.UpsertAsync`
  (`UpdateAsync` deliberately never touches DappRole — role changes go only through
  this dedicated, authority-gated path).
- Authority ladder (fail-closed): operator sets anything incl. manager (bootstrap);
  a manager sets only developer/user; everyone else denied. Role validated against
  the `AzoaDappRoles` allowlist first, so an operator:admin-yielding value is
  impossible. Authority is passed as `bool actingIsOperator, bool actingCanManage`
  (computed by the controller from claims) to keep the manager free of ClaimsPrincipal.
