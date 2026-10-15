---
type: Module Notes
title: Services/Fiat/Reversal — what happens when the money goes back
tags: [fiat, reversal, chargeback, dispute, compensation, D10, W4.3]
---

# Services/Fiat/Reversal

The on-ramp (`Services/Fiat/OnRamp/`) turns a cleared card payment into units. This directory is
the other half of that sentence: **what the node does when the payment un-clears.**

The failure it exists to prevent is specific and it is a money-printer. A sponsor pays $500,
receives units, spends them backing a project, then charges the payment back. Without this
pipeline the node has given away units for money it no longer has, the project's cap table is
diluted against every honest backer in it, and nothing in the system records that any of it
happened. That is the reason the plan says, in as many words: **do not open the on-ramp to real
cards before this lands.**

## §events — four facts, one standing row

Four provider events are handled, and each one lands twice: as its own `fiat_event` inbox row
(the per-delivery evidence — signature-verified bytes, digested) and as a move on one
`fiat_reversal` row (the standing fact of what is owed and what has been recovered).

| Event | What it means | What the pipeline does |
|---|---|---|
| `charge.refunded` | The node gave money back | Open/raise the reversal; start the unwind |
| `charge.dispute.created` | The payer asked their bank to take it back | Open the reversal; **flag the account, recover nothing** |
| `charge.dispute.funds_withdrawn` | The provider actually took it | Start the unwind |
| `charge.dispute.funds_reinstated` | The node won; the money came back | Close the reversal, lift the flag |

`funds_withdrawn` is a column, not a mood. It is derived from the SUM of the dispute's balance
transactions, not from the event's name or the dispute's status word: an event can be redelivered
out of order and a status word says who is winning the argument, while the balance says who is
holding the money.

**A dispute that has merely been raised recovers nothing.** Taking somebody's units back the
moment they ask their bank a question — a question that may be answered in the node's favour —
punishes them for asking. The proportionate response at that point is the account flag: no new
value actions while the question is open. `Fiat:Reversal:...:RecoverOnDisputeOpened` exists for an
operator who disagrees, and is off by default.

## §amounts — the on-ramp's pricing rule, read backwards

The on-ramp's rule is that a webhook body can never decide how many units a payment BUYS. The
same rule applies in this direction and for the same reason: **a webhook body can never decide how
many units this node takes OUT of somebody's account.**

So `IReversalFactSource` re-reads the charge and the dispute from the provider, server-side, per
delivery. `charge.amount_refunded` is *cumulative*, which is exactly what makes two partial
refunds accumulate onto one row instead of racing each other.

The unit figure is derived, never supplied:

```
target = ceil(lot.quantity_base_units × reversal_minor_units ÷ lot.fiat_minor_units)
         capped at lot.quantity_base_units
```

Rounds **up** — rounding down would leave the platform a fraction out of pocket on every partial
reversal — and the cap is what stops rounding up from ever taking back more than the payment
bought. The denominator is the LOT's money (what actually bought units), not the charge total.

## §correlation — the intent id is not in the delivery

A reversal names a **charge** or a **payment**. It does not name this node's funding intent: a
`charge.refunded` body carries no `client_reference_id`, and a dispute object carries its own
(usually empty) metadata rather than the payment's. So the on-ramp's whole keying scheme is
unavailable here.

The durable link back is `on_ramp_lot.source_payment_intent_id` / `source_charge_id`, and the
committed schema already indexes both. `IOnRampLotLookup` is that read and nothing more. Once the
lot is found, **every identity fact comes off it** — payer, tenant, chain, asset, intent — exactly
as the on-ramp takes the recipient from the intent and never from provider metadata.

No lot ⇒ `Unmatched`, with the money movement still recorded for the operator's books. A lot that
was never `Issued` (Refused, Stuck, still Pending) ⇒ also `Unmatched`, because the refund is the
node paying back money it already owed; there is nothing to take.

## §ladder — the compensation order, and who it protects

Three rungs, in the order that hurts the fewest honest people.

1. **The units themselves** (`Balance`). If they are still in the payer's account, they come
   straight back to the reserve with the asset's clawback authority, capped at
   `min(outstanding, what they actually hold)`, and the on-ramp partition gets its availability
   back with a `Return` draw. Without that draw the partition would shrink by every chargeback the
   node successfully recovered from.
2. **What those units bought** (`Hold` / `Purchase`). An open hold the money funded is given back
   (nothing moves on any rail — the units never left the holding account, so the hold service's own
   `CancelHold` draw is the whole accounting and this directory performs no second draw), or the
   asset they were exchanged for is taken back.
3. **The platform's own pocket** (`Reserve`). Whatever is left is recorded as
   `absorbed_quantity_base_units` — a bill, not a silence — and the account is flagged.

**Rung 2 only unwinds what can be ATTRIBUTED to this payment.** Taking back an arbitrary hold that
a disputing sponsor happens to benefit from would rob whichever honest backer actually funded it.
That attribution is `IReversedPurchaseProvenance` — see §provenance.

## §provenance — how rung 2 knows what a payment funded

**Shipped (OI-59).** `ExchangePurchaseProvenance` lives in this directory and
`ReversalRegistration.cs:92` wires it. The rules it works by:

- **FIFO over lots.** A person's purchases are walked oldest-first against their lots oldest-first,
  and only the share that drew on the *reversed* lot is ever claimed. Already-reversed lots still
  consume capacity — dropping them would shift later lots earlier and re-attribute a purchase that
  has been unwound once already. The same rule serves W8.1's cash-out.
- **What counts as a purchase.** An Exchange operation the subject made, buying the lot's own asset
  on the lot's own chain.
- **Identifying the commitment.** Exact first, on the hold id the exchange derives from its own
  correlation (`exchange:{correlation}`); failing that, a match on the whole shape the exchange
  opened the leg with — and *only* when exactly one candidate exists. Two matching commitments is
  an unanswerable question, and it refuses outright rather than guess.
- **The robbery guard.** A hold is claimed only when the reversed account is its beneficiary and
  nobody else funded it. Checked positively in `ClaimForHold`, never inferred.
- **The opt-out survives.** `UnavailableReversedPurchaseProvenance` is still registerable for a node
  that will not attribute; it returns nothing and reports `IsAvailable == false`, so no caller can
  read "no link" as "this person spent nothing", and the reason travels onto the reversal row.

It fails closed in the only direction that matters: with no provenance, nothing unattributable is
ever touched, and the shortfall becomes a *recorded* platform loss plus a flagged account.

## §flag — a control, not a badge

`fiat_reversal.account_flagged` would be decorative if the only thing that read it were a screen.
`RiskHoldValueGate` is the read side: it **wraps** the D46 value gate — the check every value
action already has to pass — and refuses an account carrying an unresolved flag. That closes the
loop where somebody charges back, keeps the units, and immediately buys more with another card.

Wrapping the existing gate rather than adding a second check is deliberate. A new gate would have
to be called from every value path by hand, and this codebase's recurring defect is a control that
nothing calls. This one is enforced wherever `IRealValueKycGate` already is — the on-ramp
(`OnRampSettlementHandler.ApplyAsync`) and the cross-chain bridge.

It fails closed: an unreadable ledger is a refusal, never an assumed "no flag".

**One honest wart.** When the gate refuses a flagged payer inside the on-ramp, the on-ramp parks
that lot with its own wording about identity verification — it does not know about risk holds, and
`Services/Fiat/OnRamp/` belongs to another item. The behaviour is right (nothing is credited, the
money is owed back); only the parked lot's sentence is imprecise, and the real reason is on the
reversal row and on the operator board.

## §saga — why two forward steps

`Services/Sagas/FiatCompensationSaga.cs`: `recover-reversed-units` → `settle-reversal-shortfall`,
with `escalate-unsettled-reversal` as the compensation on both.

The split is what makes the retry budget honest. Rung 1 and rung 2 fail the step **only** when the
outcome could not be established; a definite "nothing more is recoverable" is a *success* that
advances to settlement, instead of burning five attempts to re-reach a conclusion the node already
had. The escalation step is the floor: a reversal that cannot be settled is parked at
`NeedsOperator` with the reason on the row, rather than dead-lettering a saga step nobody watches.

**Ambiguity is not retried.** A recovery move that was broadcast but not confirmed
(`OperationStatus.PendingConfirmationMarker`) parks the reversal for a person. Retrying blind is
how one reversal takes units twice — and unlike a payout, a clawback has no cheap reconciliation
read here.

Every value move is claimed on `IIdempotencyStore` first, keyed
`fiat-reversal-recovery:{reversalId}:{rung}:{amount}`. The amount is in the key on purpose: a
retry of the same move replays, while a **second partial refund** — which raises what is owed —
unlocks a new one.

## §lot — a reversal consumes the issuance it unwinds

A full reversal moves the `on_ramp_lot` to `Reversed`, which W8.1's FIFO read already excludes. A
partial one raises the lot's `consumed_base_units` watermark instead.

That is a deliberate overload and it is the honest one: from the source-aged cash-out rule's point
of view, a unit taken back by a chargeback and a unit already cashed out are the same unit —
gone — so one watermark is the right place for both. Two counters that could disagree with each
other would not be. (The `on_ramp_lot` table note still describes the watermark as W8.1's alone;
that file belongs to the on-ramp item.)

## §wiring — this directory is inert until `Program.cs` calls it

Nothing here is assembly-scanned, and the webhook controller resolves
`IEnumerable<IFiatEventHandler>` from the container. Without the calls below, a node **accepts
refund and dispute deliveries, answers them 202, and does nothing** — the exact shape of the
defect this codebase keeps finding. `Program.cs` (owned by W4.1 for this wave) must call both:

```csharp
// after OnRampRegistration.AddFiatOnRamp, AddPlatformHold, the reserve, the saga layer
// and builder.Services.AddHttpClient(StripeGateway.HttpClientName)
AZOA.WebAPI.Services.Fiat.Reversal.ReversalRegistration
    .AddFiatReversalPipeline(builder.Services);

// AFTER the IRealValueKycGate registration — it WRAPS that gate, and without this call
// the account flag is written and nothing reads it
AZOA.WebAPI.Services.Fiat.Reversal.ReversalRegistration
    .AddReversalRiskValueGate(builder.Services);
```

Both throw at startup when a collaborator they need is missing, so a half-wired container fails
loudly at boot rather than booting a node that silently discards chargebacks. The reversal handler
is registered as `IFiatEventHandler` **only** — never `IFiatSettlementHandler`, because that
marker is what proves a node can credit a card before it is allowed to charge one, and bookkeeping
must never be mistaken for it.

## Configuration

```
Fiat:Reversal:MaxRecoveryAttempts                       default 5   (clamped 1..20)
Fiat:Reversal:Default:RecoverFromBalance                default true
Fiat:Reversal:Default:RecoverFromOpenHolds              default true
Fiat:Reversal:Default:RecoverFromPurchasedUnits         default true
Fiat:Reversal:Default:AbsorbAtOrBelowMinorUnits         default 0   (absorb nothing automatically)
Fiat:Reversal:Default:RecoverOnDisputeOpened            default false
Fiat:Reversal:Default:FlagAccountOnDisputeOpened        default true
Fiat:Reversal:Default:FlagAccountOnShortfall            default true
Fiat:Reversal:Flows:<purpose>:<any of the above>
```

`<purpose>` is the flow the payment funded. Today the on-ramp is the only fiat flow that issues
anything reversible, so it is `on-ramp`; a flow nobody configured gets `Default` rather than
whichever entry happens to be first.

`AbsorbAtOrBelowMinorUnits` is the one knob that costs the platform money on purpose: a rail
charge for a recovery move can exceed a small reversal, and chasing somebody for two dollars costs
more than it returns. Making that explicit and operator-set is the difference between a policy and
an accident.

## §operator — the board

`FiatReversalOperatorController` (`api/admin/fiat-reversals`, `Operator` policy) lists what is
open and gives a person four verbs: run the unwind now, close it, restart a saga that never
started, and lift the pause on an account. The last is stamped with who lifted it — it is the one
verb that gives an account its ability to move value back. The frontend is
`frontend/src/app/(dashboard)/admin/fiat-reversals/page.tsx`.

The controller lives in this directory rather than under `Controllers/` because that directory
belongs to other items in this wave. MVC discovers controllers by type, so the route is identical
either way and moving the file later changes nothing.
