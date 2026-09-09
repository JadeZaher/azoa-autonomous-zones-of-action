---
type: Module Notes
title: Services/Reserve — the treasury-rate exchange and the buy-back that lets people sell
tags: [reserve, exchange, buy-back, liquidity, queue, W5.1, W8.2]
---

# Services/Reserve

The platform unit, the project treasuries it moves between, and the policy that decides whether
one exchange may happen at the rate its consumer declared. AZOA never computes a rate (D7): a
policy either admits the declaration or refuses it.

## §buy-back — what makes SELLING possible at all

`TreasuryRateExchangePolicy` refuses every SELL unless an `IExchangeLiquidityPolicy` is in the
container **and** that policy admits the sell.
`BuyBackLiquidityRegistration.AddBuyBackLiquidityPolicy` is the only thing that registers one,
and `Exchange:BuyBack:Enabled` defaults to false. The path is therefore closed twice over, and
opening it is a deliberate operator act rather than a deploy side-effect.

**Treasury first.** A sell is paid from the PROJECT's own treasury. That is what makes a buy-back
the project's promise rather than the platform's: a project that cannot afford to buy its units
back has not quietly borrowed the platform's reserve to look like it can. The bounds are
per-request (`MaxUnitsPerRequest`) and proportional (`MaxRequestShareOfTreasuryPercent`), over a
`TreasuryFloorBaseUnits` the treasury may not be drained below — so one large seller cannot empty
the pot ahead of everyone who comes after.

**The backstop is a declaration first, and a movement only on settlement.** When the treasury is
short, the promise made to a queued seller is a HOLD on the platform reserve's declared partition
(`ReserveToTreasuryBuyBackBackstop.ReserveAsync`) — visible, bounded by
`MaxBackstopUnitsPerRequest`, and reversible. Declaring rather than transferring is what stops the
platform becoming the counterparty of record for a promise it never made.

Only a sell that actually SETTLES converts that hold into a real reserve->treasury transfer
(`TopUpAsync`). A project that has already handed a seller their units has been recapitalised in
fact, and a partition that says otherwise is a number nobody stands behind.

**Head-of-line FIFO.** When even the backstop cannot fill a sell, the seller takes a PLACE rather
than a refusal, and is told the place. The queue is strictly first-come: a later-but-larger seller
never jumps, because the whole point of a queue is that a person who waited is not overtaken by a
person who waited less. A place is keyed on the exchange's own idempotency correlation, so a retry
re-reads its own place instead of taking a second one. A PREVIEW never takes a place — a quote
that queued would let a browsing user hold a slot they never meant to use, which is why
`ExchangeRateEvaluation.IsPreview` is load-bearing and not a hint.

**The context is required, and its absence fails closed.** The five trailing values on
`ExchangeRateEvaluation` (tenant, project reference, seller, request key, preview flag) are
optional only so the record stayed source-compatible with the call site that predates them. A
liquidity policy that cannot see the treasury a sell would be paid from REFUSES rather than
guessing — see `BuyBackLiquidityPolicy.ContextMissingMessage`. `ReserveExchangeManager` passes all
five; anything that constructs an evaluation without them gets a closed door, not a default.

**Known asymmetry.** The queue place is retired at ADMISSION (`ExchangeRatePolicy` `CloseAsync`,
~line 1049), not at settlement. A sell that is admitted and then fails to settle loses its place
and re-enters at the back under a new key. Moving the `CloseAsync` call to settlement success
would be strictly fairer, and belongs with whoever owns `Managers/ReserveExchangeManager.cs`.

## §three-outcomes — a movement the rail took and never confirmed

`ReserveToTreasuryBuyBackBackstop.TopUpAsync` has THREE outcomes, not two, and conflating two of
them is how a declared partition gets drawn down for units that may never have left.

Every provider's transact pipeline returns a **SUCCESS** result whose `Message` carries
`OperationStatus.PendingConfirmationMarker` when the transaction was broadcast and the confirm
poll ran out (`AlgorandProvider.BuildSignSubmitAsync`, and the Ethereum/Solana equivalents). So:

| Outcome | What the reserve does | What the seller keeps |
| --- | --- | --- |
| `IsError` — never broadcast | nothing; the hold stands and MAY be handed back | place + promise |
| pending marker — broadcast, unknown | nothing; the hold stands and may **NOT** be handed back | place + promise |
| confirmed | hold → issued, project treasury credited, claim completed | the fill |

Reading only `IsError` converted the hold to Issued, credited the project and called
`CompleteAsync` on the replay anchor — permanently, because a completed anchor is never
re-attempted. The unknown outcome is now recorded on the move claim as
`AmbiguousMoveReason`, and `ReleaseReservationAsync` **reads that back** and refuses the
give-back: the units may already be the project's, and returning the partition's availability
would let one declared budget back a second seller. Same rule and the same sentence shape as
`OffRampService.AmbiguousMoveReason`. Held at the counters by
`tests/AZOA.WebAPI.Tests/Exchange/BuyBackQueueTests.cs`.

## §exchange-settlement — what a group the rail merely ACCEPTED still owes (OI-33)

`ReserveExchangeManager` deliberately stops short of two things when
`ExchangeGroupSubmission.Confirmed` is false, and leaves both to chain truth:

1. it does **not** credit the project's treasury (crediting on acceptance would make a project
   spendably richer on a settlement chain truth has not agreed to, and that same balance is the
   availability control a later sell is paid out against); and
2. it does **not** unwind, because a group whose fate is merely unknown must not be compensated.

Neither refusal is a control until something COMPLETES it. `ExchangeSettlementReconciler` is
that something, and `ReconciliationService` calls it from the two branches where the operation's
status actually transitions — before the transition, because the transition is what settles the
idempotency claim and a settled claim is never revisited:

- **chain-confirmed** → `SettleConfirmedAsync` records the outstanding project credit under the
  exchange's own ledger key (so a re-run replays instead of doubling), flips
  `ExchangeProjectCreditApplied` to `"true"` and restates the stored replay payload. If it
  cannot, the operation is NOT advanced and is flagged for an operator.
- **chain-failed** → `CompensateFailedAsync` hands back exactly the commitment the row NAMES
  (`ExchangeCommitmentKind` / `ExchangeCommitmentHoldId` / `ExchangeCommitmentTreasurySpent`,
  stamped by `BuildOperation`). A row that names none is refused rather than guessed: a guessed
  unwind returns units nobody took.

An operation row only ever exists for a group the rail ACCEPTED — every in-request refusal
compensates and returns before `BuildOperation` runs — so these paths can never double-unwind.

## §vocabulary

Nothing in this directory's consumer-facing text may name a chain, a token, a wallet or a gas
fee. A person selling their stake reads about *units*, *funds*, a *project's treasury* and a
*place in the queue*. The rail words stay in the provider layer and in operator logs.
