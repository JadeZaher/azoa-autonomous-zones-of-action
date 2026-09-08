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

**The backstop is a declared partition, not a movement.** When the treasury is short, the platform
reserve backs the difference by DECLARING a partition against itself, never by transferring into
the project. A transfer would silently recapitalise the project and make the platform the
counterparty of record for a promise it never made. A declaration keeps the exposure visible,
bounded (`MaxBackstopUnitsPerRequest`), and reversible.

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

## §vocabulary

Nothing in this directory's consumer-facing text may name a chain, a token, a wallet or a gas
fee. A person selling their stake reads about *units*, *funds*, a *project's treasury* and a
*place in the queue*. The rail words stay in the provider layer and in operator logs.
