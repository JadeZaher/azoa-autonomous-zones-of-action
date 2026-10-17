---
type: Module Notes
title: Services/Fiat/OffRamp — work → stake → dollars, closed
tags: [fiat, off-ramp, cash-out, connect, source-aging, W8.1]
---

# Services/Fiat/OffRamp

The on-ramp (`Services/Fiat/OnRamp/`) turns money into units. The reversal pipeline
(`Services/Fiat/Reversal/`) unwinds that when the money un-clears. **This directory is the other
end of the loop: units back into a person's own bank account.**

Until it existed, everything the product promises — present a problem, get sponsored, bring in
workers who take stake for their work — stopped one step short. A worker could earn stake and
never turn it into rent money. The plan's own words: *"work → stake → dollars is unreachable at
every layer and the UI says so in three places."*

## §direction — money in and money out are not symmetric

Three asymmetries drive every design decision here, and none of them is cosmetic.

1. **Money in is authorised by the payer; money out is authorised by the node.** A card payment
   is something a person does to us. A payout is something we do to them, out of the platform's
   own balance, and nothing but this node's own checks stands between a request and a wire. So
   the destination bank account is never a request field — it is resolved from the person's own
   durable onboarding record, every time (`PayoutAccountService.RequireReadyAccountAsync`).
2. **Money in can be taken back for months; money out cannot be un-sent.** That is what §aging is
   about: units bought with a card that can still be charged back may not leave the platform.
3. **The failure modes are not equally survivable.** On the on-ramp the disaster is "charged and
   not credited". Here it is "units taken and no money sent" — which is why the two halves are a
   saga with that state *named* rather than a method with a gap in the middle.

## §aging — the source-aged cash-out rule (Q-X2)

The platform unit is one fungible asset. On chain, units bought with a card and units earned by
working are the same units. So "can this person cash out?" cannot be answered from a balance.

`on_ramp_lot` is the only per-issuance provenance there is, and W8.1 consumes it:

```
locked   = Σ (quantity − consumed) over lots whose reversal window is still OPEN
committed = Σ quantity over this person's unfinished cash-outs
cashable = holding − locked − committed
```

Three consequences, each deliberate:

- **Units with no lot are unaged.** Units received for work or from selling stake were never
  bought with reversible money, so nothing has to age before they can leave. They never appear in
  `locked`, which is exactly how the rule expresses "unaged" — by not mentioning them.
- **A lot with no recorded window is NEVER mature** (`OffRampAging.IsMature`). The window is
  written from the rail's own declared `SettlementProfile`; a missing one means the node could not
  establish when the money stops being reversible, and unknown must lock rather than release.
  Treating it as mature would be a fail-open on the one question this rule exists to answer.
- **Consumption is FIFO over MATURED lots** (`OffRampAging.PlanConsumption`), oldest `issued_at`
  first — which is the order `IOnRampLotLedger.ListIssuedForUserAsync` already returns. The
  watermark it raises is the same `consumed_base_units` a chargeback recovery raises, because from
  the cash-out rule's point of view a unit taken back by a chargeback and a unit already cashed
  out are the same unit: gone. Two counters that could disagree would not be an improvement.

`committed` is what stops two cash-outs in flight from spending the same units. It is a
read-after-write check rather than a lock, so a genuinely simultaneous pair can both pass it; the
CAS on each lot and the chain's own balance are the backstops behind it, and neither can be
talked past.

## §saga — the order is the design

`OffRampSaga`, two forward steps and one compensation step:

| Step | What moves | Status after |
|---|---|---|
| `collect-cash-out-units` | the person's units → the reserve, signed with THEIR key | `UnitsCollected` |
| `send-cash-out-money` | the platform's money → their bank | `Paid` |
| `return-cash-out-units` | the reserve's units → back to them, platform-signed | `Returned` |

**Units first, money second, always.** If the money went first, a node that died between the two
halves would have paid for units it never took — an unbounded loss with no record. Taking the
units first makes the crash window the survivable one, and `UnitsCollected` is a durable, named,
compensated state rather than a gap inside one method.

The collect leg is a **burn-equivalent**, not a burn: the units go back to the platform reserve
and the reserve partition they were issued from gets its availability back
(`ReserveDrawKind.Return`). Supply is created once, at genesis (D47); nothing here destroys any.

Every leg claims `IIdempotencyStore` before it moves anything, and a leg whose outcome could not
be established **parks the cash-out for a person** instead of retrying blind. Retrying a move that
may have landed is how you take somebody's units twice.

**A spent claim is never re-sent under, even after a failure that provably broadcast nothing.**
The store has no release operation, so a failed key is terminal for that leg: the step keeps
reporting "not established yet" until the saga's budget runs out, compensation gives the units
back (or refuses a cash-out that never took any), and the person starts a new one with a new
request key. That is the same trade the reversal ladder makes, for the same reason — the only
alternative is a second send under a claim whose first outcome the node cannot see.

## §account — one payout account per person, and no cached "ready"

Onboarding happens at the **first cash-out** — the one moment paperwork is expected — not at
sign-up. Stripe Connect Express carries the identity and (for entities) the business verification,
which is what covers KYB at the payout boundary.

It rides the hosted-session seam rather than a table of its own: the admission key is derived
(`{userId:N}:payout-account`), the provider session id IS the connected account, and the redirect
is the onboarding link. That is what `hosted_session` is for. The claim is create-if-absent, and
the Stripe create runs behind the shared creation fence (`Providers/Fiat/AGENTS.md`
§creation-fence): one creation token per claim keys both the Stripe idempotency key and the attach,
so a double-tap, a crash or a lost response cannot open two accounts. Reading status
(`GetStatusAsync`, `RequireReadyAccountAsync`) is a read-only admission lookup and claims nothing.

**Readiness is never stored.** `payouts_enabled` can be withdrawn by the provider at any moment —
a missing document, a failed verification — so a cached "onboarded" flag would be a lie the
instant that happens, and the node would learn about it by sending money that bounces. Every
decision re-reads the account server-side, and the check is `payouts_enabled` specifically: not
"details submitted", not "charges enabled".

## §collect-quantity — collect always takes the claimed quantity (round 4)

`CollectUnitsAsync` once read `requestedQuantity ?? row.QuantityBaseUnits` with no
`requestedQuantity` in scope (CS0103). History (`git log -S requestedQuantity`, `3d08ace`) shows the
optional `requestedQuantity` parameter was added only to `ReturnUnitsAsync`, for the partial
restitution path (`RestoreReservedUnitsAsync` returns a reserved slice). The collect line was a
copy of that edit. **Decision:** collect has no partial form — it moves exactly the quantity that
was claimed and checked against `cashable` in `RequestAsync`. Accepting an override there would
let the saga take a different amount than the availability check approved.

## §reversal — `transfer.reversed` runs the other way

`PayoutReversalEventHandler` records a reversed payout in **W4.3's ledger**, so an operator sees
one board for "money that moved and then un-moved". Three things about it are load-bearing:

- **It does not start the compensation saga.** That ladder claws units back from the payer. On a
  reversed payout the payer is *owed*, not owing; running it would take units from somebody whose
  bank refused the platform's own payment. The row is written at `NeedsOperator`, and both
  `RecoverAsync` and `SettleShortfallAsync` return immediately for that status — so the ladder
  cannot reach it even if somebody clicks.
- **It does not flag the account.** A payout bouncing is not evidence against the person.
- **The amount is re-read from the provider**, never parsed from the body. A body-supplied
  reversal figure is the on-ramp's money-printer read backwards: it would decide how much the node
  owes somebody.

The correlation is `metadata[azoa_intent_id]` on the transfer, which is why `FiatPayout` ids are
compact **Guids** rather than the SHA-256 hex `on_ramp_lot` uses: `StripeEventEnvelope` only
recovers a correlation that parses as a compact Guid, and a hex digest would leave
`transfer.reversed` with no way home. The ledger row uses a third scope word, `payout`, so it can
never collide with a refund or dispute of the payment that funded the same person's units.

## §vocabulary — a sponsor never reads a chain word

Everything a person sees here says *cash out*, *your money*, *your bank*, *on hold until*. The
`FiatPayoutErrors` messages are separate from `FiatConsumerErrors` for one reason: "adding funds
is not available" is the wrong sentence for somebody trying to take money OUT, and a message
describing the wrong operation reads as a bug even when the refusal is correct.

The request names an **amount of money**; the node works out the units. That direction matters —
it is the difference between a product for people who think in dollars and one that makes them
learn a token price first.

`OffRampAging.TryQuoteUnits` rounds **up** and `QuoteMinorUnits` rounds **down**, both in the
platform's favour by at most one base unit (a hundredth of a unit at two decimals). Rounding the
other way would leak out of the reserve on every single cash-out.

## §price — the same schedule, read backwards

Units are converted back into money at the **same registered `tenant_price_schedule` the on-ramp
sells at**, with the same staleness and retirement rules. A retired latest version means the scope
has stopped trading in BOTH directions: buying somebody out at an older price would pay at a price
nobody is offering any more.

There is no separate off-ramp rate, and there deliberately is no fee: a cash-out fee is a product
decision nobody has made, and inventing one here would be a silent economic decision of exactly
the kind §pricing in the on-ramp's notes refuses.

## §wiring — this directory is inert until `Program.cs` calls it

Nothing here is assembly-scanned. `Program.cs` must call, after `AddFiatOnRamp`,
`AddFiatReversalPipeline` and `AddReversalRiskValueGate`:

```csharp
AZOA.WebAPI.Services.Fiat.OffRamp.OffRampRegistration
    .AddFiatOffRamp(builder.Services);
```

Without it the node compiles, MVC still routes `api/fiat/payouts` and `api/admin/fiat-payouts`,
and every one of those routes answers with a container error — which is worse than not shipping
them, because a cash-out page that 500s reads as an outage rather than an unwired feature.
`AddFiatOffRamp` therefore checks every collaborator and throws at boot rather than registering
something that cannot work.

**There is no boot guard equivalent to `FiatRailStartup.Validate` for the off-ramp**, because the
failure it would prevent is not symmetric: a node that cannot cash out has taken nobody's money.
`Fiat:OffRamp:Enabled` defaults to **false**, and both `OffRampService` and `PayoutAccountService`
report "not available on this node" rather than half-working when it is off or under-configured.

## Configuration

```
Fiat:OffRamp:Enabled                    default false  -- the master switch
Fiat:OffRamp:ReservePartition           default "on-ramp"
Fiat:OffRamp:MinimumAmountMinorUnits    default 500      ($5.00)
Fiat:OffRamp:MaximumAmountMinorUnits    default 200000   ($2,000.00)
Fiat:OffRamp:MaxPayoutAttempts          default 5 (clamped 1..20)
Fiat:OffRamp:ConnectedAccountCountry    default "US"
Fiat:OffRamp:OnboardingReturnUrl        required when enabled
Fiat:OffRamp:OnboardingRefreshUrl       required when enabled
```

`Enabled` is separate from `Fiat:Enabled` on purpose: money in and money out are approved by the
provider separately (Connect platform review is its own external process with its own clock), so a
node that may take payments is not automatically a node that may send them.

The onboarding URLs are checked against `Fiat:Checkout:AllowedReturnOrigins` — the same allowlist,
because the open-redirect risk on a page where somebody types their bank details is the checkout
return page's risk aimed at a worse target.

## Cumulative transfer reversals

Stripe's `amount_reversed` is cumulative; `reversed` becomes true only for a full
reversal ([transfer object](https://docs.stripe.com/api/transfers/object)). A payout
already marked `Reversed` must still reconcile later partial reversals. The handler
updates the reversal ledger first with a version check, then advances the payout
from that ledger. Neither a stale provider read nor a duplicate delivery decreases
the recorded amount. A failed write returns a retryable outcome, allowing a later
delivery to repair the gap between ledger and payout. This path records owed units
only; it never starts the collection compensation saga or flags the recipient.

## Bank payout lifecycle

A Connect transfer funds the recipient's Stripe balance; it does not prove a bank
payout occurred. New Express accounts use manual payout schedules. Before funding
an account, the service configures and verifies its manual schedule; before a bank
payout it verifies the schedule again. This prevents combining automatic payouts
with this service's explicit payout requests.

`BankPending` begins after the funding transfer is durably recorded. A separate
`POST /v1/payouts`, scoped with `Stripe-Account`, creates the bank payout. Its
metadata carries the cash-out id. Only the server-fetched `paid` status sets
`Paid` and `PaidAt`; pending and in-transit statuses remain `BankPending`.
Connected-account callbacks must match the recorded account, payout reference,
metadata, currency, amount and environment. Failed or canceled payouts require
reconciliation of funds in the connected account before any units are returned.

The one-minute reconciliation worker handles lost callbacks and funds that are
not available yet. Creation attempts persist their start time before the request.
Ambiguous attempts reuse their key for at most 23 hours, then require provider
reconciliation before another create. A definitive insufficient-funds response
advances the attempt key without treating the payout as sent. A timeout or error
with an unknown outcome never authorizes a new key or unit compensation.

`fiat_event` retains connected-account and object correlation, without retaining
the event body. The inbox and payout ledger use typed reads, creates and
conditional updates; optimistic conflicts return retryable outcomes. An applied
inbox outcome cannot be overwritten by a racing nonterminal outcome.

Configure the endpoint for connected-account `payout.created`, `payout.updated`,
`payout.paid`, `payout.failed` and `payout.canceled` events as well as the existing
platform events. The configured endpoint secret must match that endpoint.

Stripe's [Account object](https://docs.stripe.com/api/accounts/object) does not
expose `livemode`. Account lookups use only the active environment's credentials
and must return the requested account id. Transfer and bank payout objects do
expose `livemode`, which is checked before their facts affect the cash-out.

## Failed bank payout restitution

`FailedBankPayoutRecovery` requires server-fetched failed/canceled bank payout and
matching original Connect transfer, environment, account, amount and metadata.
The operator reason and principal are persisted before recovery. A stable transfer
reversal request can be replayed for 23 hours; beyond that, authoritative transfer
reads may establish returned funds but no new reversal is sent blindly.

Returned units are capped by the proportional cumulative recovered amount. A
versioned reservation persists the target and value-move key before transfer;
reordered bank events cannot mark a recovery paid. The return claim prevents a
second transfer after a crash. Pending return transactions retain their hash and
claim key; only an authoritative confirmed chain verdict completes them. A reserve
draw accounting failure preserves the reservation, and replay repairs the draw.

Payout claims include payment environment and chain network. Legacy missing mode
fields become `unbound`, never the current configured mode. Such rows prevent
cash-out availability until classified; workers and provider effects require the
original mode. No live key is loaded through development aliases.


### Prepared restitution supersedes post-send receipts

The authoritative return identity is now the signed transaction persisted on the
`fiat-off-ramp-prepared` idempotency claim before submission. Racing preparations
reload the winning bytes; a lost response or payout CAS conflict replays that
same transaction. Pending transaction fields on the payout are diagnostics only.
The provider module checks exact transfer intent and network before submission
and observation. Algorand confirmation uses indexed transaction semantics and a
matching genesis block. Only finalized absence after the validity window or a
positive on-chain failure may clear the expected preparation for a replacement.
Ethereum/Solana module parity remains an active implementation subwave; unsupported
modules refuse before any value movement, and this is not a completed parity claim.
