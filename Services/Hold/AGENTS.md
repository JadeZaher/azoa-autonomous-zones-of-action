# `Services/Hold` — the HOLD primitive (D14 / W3.4)

A hold is units committed for a named person, parked where nobody can spend them, until a
stated instant passes. It is the thing that makes "this is yours in N days" a fact instead
of a sentence in a terms page: before it existed, freshly issued stake landed in a person's
own account immediately, so the only way to undo a reversed payment was to claw back units
the node had already told them were theirs.

Four verbs, one ledger row per hold, and the row is the idempotency anchor.

## §holding-account — why the holding account is the reserve account

The item this implements asked for "a platform-custodial holding wallet" whose own
acceptance of each project asset is prepared through `CustodialRecipientPreparer`. That
shape does not close on this codebase, and the reason is worth keeping:

`ICustodialRecipientPreparer.PrepareAsync` **refuses** a recipient signing context that is
not a resolvable *user* context (`CustodialRecipientPreparer.cs`, the
`IsResolvableUserContext` guard, mirrored again inside `AlgorandProvider`). It refuses on
purpose — an acceptance can only be authorised by the recipient's own key, and a platform
context there would mean the node accepted on a person's behalf. A separately-derived
holding account has no user context and no wallet row, so it can never satisfy that guard;
it would also owe a per-asset account minimum on every asset it ever touched.

So the holding account **is** the platform reserve account:

- it is already an operator-owned custodial account behind `IKeyCustodyService`;
- it is the manager/reserve/freeze/clawback authority on every asset this node creates, so
  it can hold those assets with no preparation at all;
- the reserve's named-partition ledger already models exactly this — `ReserveDrawKind.Hold`
  is documented in `Services/Reserve/IPlatformReserve.cs` as "units are committed to a hold
  (W3.4) but have not yet left the reserve".

The control that replaces "prepare the holding account for each asset" is
`PlatformHoldService.OpenAsync`'s **refusal to hold an asset the reserve has no ledger row
for**. An asset the reserve never registered is one the holding account may well be unable
to receive, and the node says so instead of finding out on the rail.

`CustodialRecipientPreparer` is still on the critical path, where it belongs: **every
outbound move** — the release — prepares the **beneficiary** first. Without that, the first
release to a worker who has never held the asset fails on the rail with a message naming
the asset and its acceptance step, which is precisely the blockchain error a non-crypto
worker must never see.

## §funder-source — what is declared and not built

`HoldFundingSource.Funder` (a hold funded from a person's own account) is declared in the
enum and in the table's assert list, and `OpenAsync` **refuses it**. Building it needs an
idempotency anchor on the inbound leg — without one a retried open charges the funder
twice — and that anchor is `IBlockchainOperationManager.ExecuteAsync`, which this service
does not route through. Refusing is honest; treating it as reserve-sourced would issue node
units against a payment nobody made. Unblocking it is a routing change in `OpenAsync`, not
a redesign.

## §state-machine — and why `Releasing` exists

```
Pending ──draw ok──▶ Held ──window elapsed, recipient ready, draw ok──▶ Releasing ──paid──▶ Released
   │                  │                                                     │
   │ draw refused     │ given back                                          └── retried, never re-sent
   ▼                  ▼
Refunded           Refunded            Held ──continued──▶ ReleasedIntoHold (a second hold, same units)
```

`Releasing` is not decoration. The partition draw (`ReleaseHold`) commits **before** the
outbound move, because the alternative — move first, count after — understates what the
reserve has committed and lets a partition be oversubscribed. A row parked in `Releasing`
therefore holds units the ledger already counts as issued, so **a refund from `Releasing` is
refused**: crediting the partition again would hand the same declared budget out twice.

A release that was already broadcast is **reconciled, never re-sent**
(`ReconcileBroadcastReleaseAsync`): `Confirmed` completes the row, `FailedOnChain` allows a
resend, and `Pending`/`Unknown` refuse. A second send against a merely-unobserved payout is
how one hold pays twice.

## §chained-holds — `ReleaseIntoHold` and the window rule

Approving completed work is a release into a **second** hold: the funder's window ends and
the worker's begins over the same units. A plain release cannot express it, because a
release *ends* the commitment while the second window has to *keep* it.

Two rules make chaining safe:

1. **The child window may never be earlier than the parent's.** Otherwise a chain of
   instant holds would be the way out of the funder's window that a plain early release is
   refused for.
2. **The child inherits the parent's partition draw** (`InheritsReserveCommitment`), so
   exactly one row in a chain ever performs the `ReleaseHold` or `CancelHold` draw. Two
   draws for one commitment would double-count the partition in opposite directions.

Releasing *into* a hold is allowed **before** the parent's window elapses, because no value
leaves the holding account — only who the units are held for changes.

## §sweep — the hosted release

`HoldSweepWorker` releases due auto-release holds and finishes holds a crash left in
`Releasing`. Without it a hold window would be a promise nobody keeps: the beneficiary would
have to come back and ask for their own units on the right day.

Config (`Hold` section, bound in `HoldRegistration`; all optional):

| Key | Default | Meaning |
|---|---|---|
| `Hold:SweepEnabled` | `true` | Whether the sweep runs at all. |
| `Hold:SweepIntervalSeconds` | `60` | Clamped to 15…3600. |
| `Hold:SweepBatchSize` | `50` | Clamped to 1…200. |
| `Hold:SweepChains` | *(empty)* | Falls back to `Reserve:Chains`. |

With neither `Hold:SweepChains` nor `Reserve:Chains` the sweep has **no scope** and idles
with a warning. It will not guess the platform default chain: releasing value somewhere
nobody named is the quiet reach this node refuses everywhere else.

The sweep stamps **no acting tenant**. It is the node acting on its own timer, not a tenant
acting on a person's behalf, so it asks the custody seam for no consent grant it does not
have.

## §node-quartet — four node types, and why `HoldRefund` is not `Refund`

`Hold`, `Release`, `HoldRefund`, `ReleaseIntoHold` are Tier-2 (`RequiresChainCapability ==
true`), so the value-rail capability gate refuses them pre-execution at **both** dispatch
seams — the legacy `ActionManager` executor and the durable `ActionNodeStepHandler` step.
Each declares its rail from its own config via `ResolveSettlementRailId`, so the gate asks
whether the actor can settle *there* rather than anywhere.

`ActionNodeType.Refund` was already taken by the NFT-transfer reversal node, which is a
different authority (the run actor's, over an asset the actor sent) over a different asset
shape. One node type cannot have two handlers — `ActionNodeHandlerRegistry` throws on that
at startup — and overloading the existing one would have let an NFT reversal reach the hold
ledger. Hence `HoldRefund`, whose handler lives beside the NFT one in
`Services/Actions/Handlers/RefundNodeHandler.cs`.

Node-level identity: a hold node's id is **derived** from `(runId, nodeId)` unless the
config names one, so a retried saga step addresses the hold it already opened. Downstream
nodes name the *node* (`HoldNodeName`) rather than reproducing the derived id by hand, and
that name resolves through the same derivation the opening node used.

The beneficiary defaults to the **run actor**, never the action owner — the acting-user
invariant this codebase enforces wherever a node has an identity side effect. A marketplace
run holds units for the person who ran it.

## §tenant-scope — whose declared budget a hold may draw

A reserve ledger row registered by a tenant is *that tenant's* declared budget. A hold whose
acting tenant is anyone else is refused (`GuardTenantScope`), because sponsor exchanges and
contributor holds draw from the **same** reserve: a partition policed only in one tenant's
database can be overdrawn by another caller. A row with no tenant is the platform's own unit
and has no tenant owner to check.

The REST surface derives the acting tenant from the **principal**, never the body — the
allocation precedent — and a principal whose `act_as_tenant` claim disagrees with its own
subject is refused rather than resolved by coin flip.

## §reads — hold ids are not public

A hold id is the caller's own reference, so a bare id would be an enumerable window onto
other people's money. `HoldController.GuardVisibility` admits only the beneficiary, the
funder, or the tenant that drove the hold, and returns the same "no such hold" shape to
everyone else: confirming that a hold exists is itself the leak.

## B2 atomic accounting plan

HoldReserveMutationPlan reuses ReservePartitionCounters and ReservePartitionCodec to calculate exact partition mutations without writing them. Its output binds immutable reserve identity, protocol and expected version. The upcoming command store must compose that plan with aggregate and immutable command receipt in one transaction; invoking the old independent reserve draw followed by hold update is not equivalent. This new plan does not itself grant authority or enforce exactly-once execution.

## B2 provenance fields (authoring in progress)

New hold commands use a stable actor/tenant request key, while aggregate identity separately includes original owner, tenant, physical protocol and network. HoldLedger now carries immutable definition/hash, opening command and active command references; absent provenance marks historical records and must not be filled from a caller assertion. The composite identity index mirrors that scope. Command receipts preserve the outcome at that command's completion independently of later aggregate transitions. The new models are not yet wired to callers; store/service/authority migration remains required before activation.

HoldLocalCommandPlan now computes reserve opening/refund and custody-preserving chaining for SurrealHoldCommandStore. It never performs independent writes. The store checks live direct or exact saved workflow authority and joins liability, partition counters, command and immutable receipt atomically. A child retains original owner/protocol and cannot shorten the window; the parent cannot refund after chaining. Original admission receipts must prove the liability. This source slice supersedes the old separate-draw design only after cutover; the historical sections above describe callers still awaiting migration, not acceptance of their safety.

### Rail completion and recovery (B2 authoring)

HoldRailCompletionPlan accepts only exact saved-wire finalized evidence and the unchanged original liability. Funded inbound completion creates Held liability without drawing reserve partitions; reserve-funded release moves held partition units to issued accounting. SurrealHoldCommandStore joins that projection, any reserve counter mutation and the immutable original receipt in one transaction. Already-landed effects are accounted for independently of later credential expiry or workflow cancellation. HoldRailExecutor separates explicit interactive preparation from restricted recovery of submitted exact bytes. Recipient acceptance, no-effect replacement/cancellation and caller integration remain under implementation; these new types are not yet registered as a replacement for legacy callers.

Actual recipient acceptance uses its own frozen intent, request identity, authority and signed bytes on the original command. Transfer consent cannot manufacture this participant authority. Restricted execution observes/rebroadcasts only Submitted acceptance bytes and cannot advance an Admitted/Prepared participant stage. Confirmed acceptance allows the original transfer journey to continue; authoritative no-effect observations retain the original bytes and verdict for explicit recovery. HoldCommandService returns only participant-safe progress/quotes, never stored signing witnesses or wire. Admission, continuation and acceptance source is authored; route/SDK wiring, renewed recipient authorization, explicit replacement/cancellation, automatic release mandate and legacy/internal-caller cutover remain required.

Recipient reauthorization now appends immutable HoldCommandAuthorization receipts while preserving original acceptance authority and intent. Exact request replay cannot mutate the historical quote or wire. Owner cancellation requires current unrestricted owner authentication, not a child/scoped credential; it cannot authorize new economic effects. Cancellation joins the command CAS with active liability release and immutable audit, refuses ambiguous submitted acceptance/value movement, and preserves confirmed recipient acceptance and its costs. Outbound cancellation restores the prior Held liability; unpaid inbound cancellation marks the pending aggregate Cancelled without touching reserve partitions.


The recovery slice now permits replacing an acceptance generation only after its
exact saved wire has an authoritative no-effect proof. Replacement archives both
transfer and acceptance evidence/authority, rotates the quote and generation, and
requires original actor cost consent plus new actual-recipient acceptance. A
confirmed acceptance is retained rather than charged again. HoldReconciliationWorker
scans only Submitted transfer/acceptance records, moves due scheduling before
observation for fairness, and calls the restricted executor path. It never signs
or authorizes a new stage. Per-record failure isolation keeps later work moving.

HoldCommandsController exposes keyed admission, bounded participant discovery,
refresh, continuation, recipient acceptance, replacement and no-effect cancellation.
The immutable rail recipient is indexed for direct-recipient discovery; tenant
callers see only their exact original actor/tenant commands. These endpoints are
new source pending registration/review; legacy HoldController/action/internal
callers and automatic-release mandate cutover remain required before full activation.

### Exact automatic release source (B2 under review)

HoldReleaseMandateService persists a server-produced full disclosure before explicit
owner acceptance. Its separate worker resolves the saved mandate and uses the same
HoldRailExecutor as interactive releases. It cannot obtain recipient acceptance or
replace a transaction generation. New preparation rechecks current mechanics and
cost categories, while the store fences the saved mandate and original liability
at preparation and first egress. The restricted reconciliation worker remains
observation/exact-wire replay only. Neither worker is registered until source review
and caller cutover. Management/discovery UI, internal economic callers and legacy
worker retirement remain required implementation.

Workflow opening disclosure is a domain AwaitingConsent command while the exact
original node remains Running. Explicit acceptance composes the existing local
reserve plan with the mandate, original command receipt and acceptance audit in
one transaction. No reserve is drawn merely to display an offer. The later mandate
source fences the exact execution record and original immutable receipt; only a
Succeeded opening node can authorize its delayed release. Original acceptance
replay returns the saved receipt before current grant checks and never repeats the
opening. Frozen adapters, stale disclosure recovery and caller cutover still need
completion before this source is activated.

Unaccepted disclosure can now be replaced with fresh server costs under its original
mandate bounds, or withdrawn by the original actor's current full login. Either
transition competes with opening acceptance through the same command version and
keeps an immutable audit. Saved recovery requests use exact receipt lookup after
a version read; absence alone never resets a pending request. WorkflowAdmission
does not sign: it creates parked opening/rail consent or commits an already-authorized
local accounting command. Its callers must supply frozen authority and exact
source receipt linkage; the action adapter and legacy cutover remain under authoring.

Transfer, recipient acceptance, replacement and cancellation now expose ordered
request reconciliation: read the command version before its exact immutable audit.
Absent receipt at the original version is Unresolved; only a higher version proves
that old CAS cannot commit. Matching audit is Applied. HoldConsentIdentity preserves
the original canonical property casing of historical consent IDs and hashes.
The SDK and recovery panel retain exact request bodies and archives across lost
responses. Legacy economic callers and provider parity remain required work.

Exchange source opening uses the real tenant API-key actor as original owner,
with null authentication tenant and the same actor as custody tenant. The
beneficiary remains the separately consenting participant. HoldLocalCommandPlan
permits this own-tenant custody identity; the source transaction independently
checks the actual tenant credential and exact selected participant grant. Its
aggregate starts AwaitingSourceSettlement even though the opening accounting
receipt is complete. This prevents a committed but unpaid purchase from taking
the ordinary Held release/refund paths before authoritative source settlement.
