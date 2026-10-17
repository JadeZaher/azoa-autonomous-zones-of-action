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

## §tenant-funded — D-8: a tenant-priced payout is paid from the tenant's own balance (round 4)

Status: **built** (critic verdict ACCEPT-WITH-CHANGES; all seven required changes and the three owner
decisions of 2026-10-17 are applied). Code: `TenantPayoutLedger.cs`, `TenantPayoutFunding.cs`,
`TenantPayoutTopUp.cs`, `TenantPayoutSweep.cs`, `OffRampService.Funding.cs`, `TenantPayoutBalanceController.cs`;
POCOs `Persistence/SurrealDb/Models/TenantPayoutFloat.cs`.

**The defect (audit-2 H3).** A tenant-driven cash-out was priced by the tenant's own
`tenant_price_schedule` and paid from the **node's** Stripe balance, so a tenant registering a generous
price spent the operator's money.

### Who pays = who priced (critic rule 5)

`funding_tenant_id` (`fiat_payout`, `option<record<user>>`, immutable after the claim) is set from the
**same schedule record** that priced the row (`OffRampContext.FundingTenantId`: the schedule's tenant when
its scope is not `node`), never from the caller's tenant and never re-resolved. A tenant driver priced by
the node fallback (`AllowNodeDefaultScheduleFallback`) is node-funded at the node price. At send,
`RefuseUnfundedSendAsync` re-reads `row.PriceScheduleId`: a tenant-scoped price requires
`funding_tenant_id` = that tenant **and** a `Reserved` debit for exactly this cash-out; a node-scoped price
requires no funding tenant. Anything else parks — including a pre-D-8 tenant-priced row with no debit.

### Funding source — a per-tenant top-up float

- `tenant_payout_float` — one row per `(tenant, currency, environment)` (UNIQUE), counters `credited`,
  `reserved`, `finalized`, `version`. **available = credited − finalized − reserved.**
- `tenant_payout_credit` — one per settled top-up (record id from the checkout intent, UNIQUE
  `intent_id`). Top-ups ride the existing Checkout rail with purpose `tenant-payout-float`
  (allowlisted) and a **server-built admission key** `payout-float:{tenant}:{key}`; ordinary keys start
  with a 32-hex subject, so a caller-chosen key can never forge one. The rail refuses that purpose
  without such a key and refuses such a key on any other purpose; it gates top-ups on the tenant's KYB
  (`KybCapability.PayoutPriceSchedule`) instead of personal KYC.
- **Tenant binding (critic rule 6):** `TenantPayoutTopUpHandler` binds the tenant from the
  server-recorded intent — the admission key's tenant must equal the hosted session's
  `subject_user_id` — never from provider metadata. The on-ramp settlement handler ignores top-up
  intents (it must never issue units for them).
- **Owner decision (a): credit = `balance_transaction.net`.** The handler re-reads the session and the
  payment's latest charge with its expanded balance transaction (`IReversalFactSource.GetPaymentBalanceAsync`);
  the tenant bears Stripe's processing fee. Currency must match the charge or nothing is credited.
- **Maturity (critic rule 6):** `available_at = settled + the rail's declared SettlementProfile window`,
  and only when that window covers the dispute horizon (`Fiat:ChargebackWindowDays`). No declared window,
  or a shorter one → `available_at` absent → the credit **never** matures on its own.
- **Promotion and reversal (critic rule 3):** every credit change is `UPDATE credit WHERE version AND
  state = $from` plus the float version CAS in one `BEGIN … COMMIT`, `THROW` if either matched nothing.
  Promotion `Pending → Available` adds `net − reversed`; a later credit simply raises `credited`, so it
  nets against a deficit first. `TenantPayoutTopUpReversalHandler` re-reads the charge (refunds return the
  gross) and, when disputed, the dispute's balance impact (amount **plus dispute fee**, owner decision a),
  and sets the credit's authoritative `reversed` total; on an Available credit `credited` moves by the
  difference and may go negative. A negative float covers nothing and shows as `deficit` on the views.

**Rejected alternative (critic rule 7, corrected): debiting the tenant's Stripe connected account.**
Connect account debits pull from the connected account's **Stripe balance** (synchronous when that balance
is available), not a bank pull. The rejection stands on: (1) eligibility — account debits are limited by
connected-account type and region; (2) coupling — every pricing tenant would have to be onboarded as a
Connect account under the platform's control, tying KYB to Connect onboarding; (3) coverage — the node
would need a pre-funded tenant Stripe balance it cannot reserve against, so two concurrent payouts could
both pass a balance read and one debit fail after its transfer left. The float puts the money on the
platform first, so coverage is a local, synchronous compare-and-swap.

### Coverage check — reserve before anything moves

In `RequestAsync`, after the claim and availability check and **before** consuming lots or starting the
saga: `TenantPayoutFunding.ReserveAsync` promotes matured credits, then one transaction creates
`tenant_payout_debit:{payoutId}` (record id = payout id, UNIQUE `payout_id`) and raises `reserved` under
the float's version CAS. Coverage is checked on the version it CASes (`available ≥ amount`); a lost CAS
re-reads and re-checks. Insufficient → typed refusal `TENANT_PAYOUT_BALANCE_INSUFFICIENT`, the row is
`Refused`, no unit moved and no Stripe call was made. No DB transaction spans a Stripe or chain call.

### Exactly-once accounting

| Transition | Authoritative trigger | Float effect |
|---|---|---|
| `Reserved → Finalized` | payout `Paid` (server-fetched bank payout), or a terminal row whose transfer exists | `reserved −= a−r`, `finalized += a−r` |
| `Reserved → Released` | terminal no-money row (`Refused`/`Returned`) **and** no transfer ever existed | `reserved −= a` |
| `reversed` raised (Reserved or Finalized) | Stripe's cumulative `amount_reversed` | the state's counter `−= delta` |

- **Critic rule 1 — release only on proof of no transfer.** A row that never held a pay claim (and has
  no transfer id) releases directly — e.g. every refusal inside `RequestAsync`. Any row that ever held a
  pay claim releases only when a **complete** `ListTransfersByGroupAsync("azoa-payout-{id}")` page shows
  zero transfers for it; an unreadable or incomplete page, or more than one transfer, leaves it Reserved.
  Stripe 409 / `idempotency_error` maps to *busy* (ambiguous), never *refused*, so the send leg parks
  instead of abandoning.
- **Critic rule 2 — one reversal source in every state.** `reversed_minor_units = min(a, max(stored,
  Stripe amount_reversed))`, applied in Reserved and Finalized. A partial failed-bank restitution
  therefore releases exactly what Stripe returned, and a following `transfer.reversed` with the same
  cumulative figure adds nothing. `Released` means only "no transfer ever existed".
- **Invariant:** `balance = credits − finalized − reserved`, with `credits = Σ Available (net − reversed)`,
  `finalized = Σ Finalized (a − r)`, `reserved = Σ Reserved (a − r)`. `ReadSnapshotAsync` reads the float
  and its entries in **one transaction**; `DescribeAsync` lists **every** (currency, environment) float
  with counters, recomputed sums, `consistent`, pending credits and a separate `deficit`.
- Triggers: saga steps (`SettleAsync`, bank payout outcomes, operator resolutions, failed-bank recovery)
  and `PayoutReversalEventHandler` (fails the event on a lost sync so it is redelivered or swept).

### Sweep (critic rule 4)

`TenantPayoutSweepWorker` (1 min) promotes matured credits and walks `Reserved` debits older than 30 min:
a terminal payout (`Paid`/`Refused`/`Returned`/`Reversed`) is re-synced (repairs a lost transition write,
e.g. a `RefuseAsync` release that failed); a payout still `Requested` an hour after its claim with **no
collect attempt** (crash between reserve and `StartSagaAsync`) has its collect claim fenced
(claimed + failed, so a late saga can never move units) and is abandoned, which releases the debit.

### Owner decision (b) — tenant-funded collects return units to the tenant

The collect leg sets `collect_partition = tenant-payout:{tenant}` and calls
`IPlatformReserve.ReattributeReturnAsync` (see `Services/Reserve/AGENTS.md` §reattribution): the on-ramp
partition returns the units and gives up the same budget; the tenant partition's budget grows by it.
Returning units for such a cash-out draws `Issue` from that same partition.

### Owner decision (c) — no cross-scope arbitrage

`on_ramp_lot.pricing_scope_key` records the scope whose schedule priced each lot. A **node-funded**
cash-out whose FIFO plan draws a lot priced by a tenant scope is refused when it would pay more per unit
than that lot was sold for (`node rate < lot rate`, in units per minor unit). Tenant-funded exits are
uncapped: the tenant pays.

### Fixtures (unit, `OffRampTests.TenantFunded.cs`, `TenantPayoutTopUpTests.cs`, over a ledger fake with the production commit contract; the Surreal store was probed against SurrealDB 3.1.4)

Two concurrent reservations against a balance covering one; uncovered request refused with the typed
code before anything moves; reservation racing a top-up promotion; Paid → Finalized then a partial and a
duplicate `transfer.reversed`; crash between reserve and send → one transfer; abandon before send →
released once; 409 at send keeps Reserved and parks; partial restitution releases only the recovered
amount and a repeat adds nothing; credit promotion racing its reversal; crash after reserve before the
saga → sweep closes and fences; pre-D-8 tenant-priced row with no debit parks; tenant driver with node
fallback → node-funded at node price; arbitrage cap (theory); top-up credited net once, never matures
without a window, subject/key mismatch credits nothing, refund reverses by the gross. Mutation-checked:
removing the coverage check and the `max()` reversal together turns four of them red.

## §operator-resolution — audited exits for parked and reversed cash-outs (round 4)

`POST /api/admin/fiat-payouts/{id}/resolve` (Operator policy, financial limiter) with
`{ "resolution": "record-paid" | "return-units", "reason": "…" }` → `OffRampService.ResolveForOperatorAsync`
(`OffRampService.Resolution.cs`). Only `NeedsOperator` and `Reversed` rows qualify. Evidence is never
the operator's word:

- **Stripe transfer record:** every cash-out transfer is created in transfer group
  `azoa-payout-{payoutId}`; the resolution lists that group (`ListTransfersByGroupAsync`). An
  incomplete page, more than one transfer, or a transfer whose livemode/amount/currency/destination
  differ from the row refuses with nothing changed.
- **record-paid** (`NeedsOperator` only): requires exactly one unreversed matching transfer. With a
  bank payout on the row, its server-fetched status decides: `paid` → `Paid`; `failed`/`canceled` →
  refused (use `/recover`); otherwise → `BankPending`, where the bank-payout lifecycle continues.
- **return-units** on `NeedsOperator`: refused while any transfer exists. The collect leg decides what
  is returned: claim absent or definitively failed → nothing moved, lots released, `Refused`;
  completed → the units are returned through the prepared-transfer path (`ReturnUnitsAsync`, keyed and
  idempotent), lots released, `Returned`; ambiguous → the transaction id recorded with the ambiguity
  marker (` tx=`) is re-read on chain (`GetTransactionConfirmationAsync`): confirmed = collected,
  failed on chain = not collected, anything else refuses. An InProgress collect claim (crash with no
  recorded transaction) still refuses: only a by-hand chain reconciliation can decide it.
- **return-units** on `Reversed`: reinstates `units_owed_base_units` once Stripe's `amount_reversed`
  covers the recorded reversal, under key `fiat-off-ramp:reinstate-{reversedMinorUnits}:{id}` so a
  later, larger reversal reinstates only its own increment. Refused while a failed-bank recovery owns
  the row.

Every resolution appends `{actor, action, reason, at, priorStatus, transferId}` to
`recovery_audit_json` through `PayoutAudit.Append`, which keeps earlier entries of other shapes
verbatim (the failed-bank recovery writes through the same helper).

## §stale-pay-claim — a pay claim left InProgress by a crash (round 4)

The pay leg claims `fiat-off-ramp:pay:{id}` and creates the Connect transfer under the stable Stripe
key `azoa-payout-{id}`. A crash between the two left the claim InProgress forever ("already in
progress"). Now a claim InProgress for at least 5 minutes and less than 23 hours (Stripe keeps keys
≥ 24 h) is replayed under the same Stripe key: Stripe returns the transfer the lost attempt created,
or creates the one it never sent — never a second. A concurrent duplicate gets Stripe's 409 and parks
for the operator resolution above. Past 23 hours the claim stays put and only the operator resolution
(transfer-group evidence) can move the row.
