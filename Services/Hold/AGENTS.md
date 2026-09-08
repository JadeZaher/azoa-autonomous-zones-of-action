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
