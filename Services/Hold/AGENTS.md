# `Services/Hold` — durable custody commands (D14 / W3.4, B2)

A hold is units committed for a named person and parked where nobody can spend them until a
stated time has passed. Every change to a hold is a **durable command**. The command is
admitted under the caller's actual authority, its accounting is committed atomically with an
immutable receipt, and any value that moves on a rail goes through exact saved wire. There is no
legacy `PlatformHoldService`, `HoldController`, sweep worker or separate reserve-draw writer any
more. Their callers were migrated in round 3 (B3-09).

## §holding-account — why the holding account is the reserve account

A separately derived "holding wallet" cannot satisfy the custodial recipient guard: acceptance
needs the recipient's own key, and a platform context there would mean the node accepted on a
person's behalf. So the holding account **is** the platform reserve account. It already holds
every asset this node creates, and `ReserveDrawKind.Hold` models units committed but not yet
gone. A reserve-backed `Open` is refused unless the exact network/protocol reserve ledger row
exists (`SurrealHoldCommandStore.CommitLocalAsync`). An asset the reserve never registered is one
the holding account may not be able to receive.

## §command-model — local accounting versus rail movement

- **Local commands** (`Open`, `Refund`, `ReleaseIntoHold`) are calculated by
  `HoldLocalCommandPlan` and `HoldReserveMutationPlan`, which are pure. The store then writes the
  liability, the partition counters, the command and the immutable receipt in **one**
  transaction. Replaying the same request key returns the saved receipt, even after authority
  is lost.
- **Rail commands** (release out, funder in) save a rail plan. `HoldRailExecutor` separates
  interactive preparation from restricted recovery, and recovery may only observe or rebroadcast
  bytes already submitted. Accounting projects only from finalized evidence of the exact saved
  wire (`HoldRailCompletionPlan`).
- Actual recipient acceptance is its own stage. It has its own request identity, authority and
  signed bytes. Transfer consent cannot manufacture it, and timers never synthesize it.

## §chained-holds — `ReleaseIntoHold` and the window rule

A child window may never end earlier than its parent's. Otherwise a chain of instant holds would
be a way out of the funder's window. The child inherits the parent's partition commitment
(`InheritsReserveCommitment`), so exactly one row in a chain performs the terminal draw. A parent
cannot refund after its child has committed.

## §purchased-liabilities — economic sources settle only through their source

A liability with `EconomicSourceId` (opened by an exchange purchase) was paid for by the
participant. `HoldLocalCommandPlan` therefore refuses ordinary local `Refund` and
`ReleaseIntoHold` for it (B3-04). Only three things can settle it:

1. the original exchange source: settlement, or compensation once absence is proved;
2. a release to its beneficiary, including the exact purchase mandate fenced by
   `HoldReleaseExchangeFence`;
3. an exact fiat reversal recovery (`Services/Fiat/Reversal` §provenance).

The store's local transition `WHERE` repeats `EconomicSourceId == null`, so a racing source
commitment cannot slip in between the read and the write.

## §mandates — automatic release

`HoldReleaseMandateService` persists the server-produced disclosure before explicit owner
acceptance, and its worker drives releases through the same `HoldRailExecutor`. It can never
obtain recipient acceptance or start a new transaction generation. Two closing rules keep a
mandate from outliving the liability it serves:

- A local `Refund` or `ReleaseIntoHold` of a hold with an Active mandate cancels that mandate and
  clears `ActiveReleaseMandateId` in the same transaction. The transition `WHERE` pins the
  mandate pointer that was read.
- A rail completion that leaves a *different* Active mandate behind cancels it atomically
  (`SurrealHoldCommandStore.Completion`).

Owners manage mandates with revoke and reauthorize. The `/holds` page journals each request
before sending it (`HoldMandatePanel`).

## §participant-renewal — stages do not stall on login expiry (B3-14)

Participant stages store the authority that was current when each step was taken. They do not
store the login session. When a credential expires, it is renewed explicitly; time alone never
renews it:

- transfer cost consent: `/continue` with a new request (`AuthorizeRailAsync` replaces
  `CurrentAuthorityJson` but keeps the quote, stage and saved bytes);
- recipient acceptance: `/accept` with a new `RequestId` (replaces
  `CurrentAcceptanceAuthorityJson`);
- mandate: `/reauthorize` (`SurrealHoldReleaseMandateStore.ChangeAuthorityAsync`).

Bytes that are already submitted keep reconciling with no live authority at all.

## §workers and §durable-registration

`HoldRegistration.AddPlatformHold` checks its prerequisites, then registers the following with
`TryAddScoped`: `SurrealHoldCommandStore` (as `IHoldCommandStore` and `IExchangeHoldSourceStore`),
`SurrealHoldReleaseMandateStore` (as `IHoldReleaseMandateStore`), `HoldRailExecutor`,
`HoldCommandService`, `HoldReleaseMandateService` and `ActionHoldOperationService`. The last is
also registered as an `IActionExternalOperationResolver`. Two hosted workers are added:

- `HoldReconciliationWorker`: observes and rebroadcasts submitted transfer and acceptance bytes
  only;
- `HoldReleaseMandateWorker`: prepares only stages that an Active mandate already authorized.

Neither worker signs for a participant.

## §node-quartet — four descriptors, one durable adapter

`Hold`, `Release`, `HoldRefund` and `ReleaseIntoHold` are Tier-2 descriptors. Each one declares
its rail (`ResolveSettlementRailId`) and returns `HoldNodeRail.RequiresDurableAdmission()` if
anything invokes it directly. Effects run only through `ActionHoldOperationService`. That service
admits the frozen node input under the run's saved signing witness. Its key is
`action:{run}:{node}:hold`, so a retried step addresses the same command. It reports the
command's progress, and outputs `HoldNodeResult.From(view, intent)` only from the completed
immutable receipt. Signing scope is `transfer:sign` (`ActionSigningScopes`). `AutoRelease`
defaults to `false`. A workflow mandate needs an explicit `Mandate` selection and
`RecipientWalletId`. `HoldRefund` exists because `ActionNodeType.Refund` is the NFT reversal node.
One node type cannot have two handlers.

## §tenant-scope and §reads

An `Open` draws a reserve partition only under `ReservePartitionScope.Refuse` with the caller's
actual draw authority: the credential's tenant, the reserve's own tenant, or a node operator. A
partition one tenant declared cannot be overdrawn by another.

Settling an existing liability (local `Refund`, rail `Release` and `FundedRefund`) also requires
`HoldCustodyScope.AdmitsSettlement`. That means the owner's or beneficiary's own login (no tenant),
or the exact tenant holding custody. A child credential that another tenant issued for the same
person cannot refund or release a hold this tenant does not hold. `ReleaseIntoHold`, the opening
and the mandates already enforce the same rule. Discovery is scoped server-side:
tenant callers see only their exact actor/tenant commands; a person sees commands they acted on
or receive.

## History

The source-slice record for this design is in
`conductor/tracks/platform-stub-closeout_20261015/round3-hold-*.md` and
`round3-lane-B-resume.md`. Source acceptance is not runtime proof. The integrated sweep is still
outstanding.
