---
type: Module Notes
title: Services/Fiat/OnRamp — the fiat → units on-ramp saga
tags: [fiat, on-ramp, pricing, lots, W4.2]
---

# Services/Fiat/OnRamp

What happens between "a card cleared" and "the person has a balance they can spend".

The rail itself (`Providers/Fiat/`, `Services/Fiat/StripeWebhookInbox.cs`) proves a payment
happened. Nothing in it credits anything. This directory is the one component that turns a
verified payment into units, and it is the `IFiatSettlementHandler` whose absence
`FiatRailStartup.Validate` refuses to boot the live environment without.

## §pricing — where a quantity is allowed to come from

**One rule, and it is structural, not a review note: the number of units a payment buys is
`amount_total` (re-read from the provider, server-side) × a price this node registered
beforehand. Nothing else may contribute to it.**

- The webhook body cannot carry a quantity. `FiatEvent` has no amount column at all, and
  `StripeEventEnvelope` does not parse one, so there is nothing for a forged body to say.
- The checkout create call stamps identity and routing into provider metadata and never a
  quantity, a rate or a unit count (`StripeGateway.CreateCheckoutSessionAsync`).
- The price is a durable, versioned, append-only row (`tenant_price_schedule`). Registering a
  new price writes a new version; it never edits the current one. An issuance records the exact
  row that priced it, so the arithmetic is re-derivable years later.
- `PriceScheduleMath.TryConvert` **floors** to whole base units and **refuses zero**. Rounding up
  would create units nobody paid for; issuing zero would take money and give nothing.
- The rate is stored in ONE direction — base units per minor unit of currency, as a decimal
  string, never bound through a floating-point value. At the platform unit's 2 decimals, a base
  of "1 unit = $0.01" is `"100"`: one cent buys one whole unit, which is 100 base units.

**Who owns the price.** A price belongs to a *scope*: a tenant's own compact id, or the literal
`node` for the operator's default. A payment driven by a tenant is priced by that tenant's
schedule. It falls back to the operator's default only when `Fiat:OnRamp:AllowNodeDefaultScheduleFallback`
is on — off by default, because substituting one party's price for another's is a silent
economic decision and the refusal it replaces is visible and fixable.

**A retired latest version means STOP SELLING.** The on-ramp refuses rather than reaching back to
an older active version: an older row is a price nobody is offering any more.

## §value-stamp — the same price decides what the operation was worth

`PriceScheduleDeclaredRateStore` implements `ITenantDeclaredRateStore`, which is what
`TenantDeclaredValueRateSource` reads to stamp an operation's USD value (D16). That is
deliberate and load-bearing: without it the schedule would decide *quantities* while a second,
independently configured rate decided *what the operation was worth*, and the two could drift
silently on a financial record.

It refuses rather than guessing when the answer is not knowable from a chain and an asset alone:
a retired price, two or more TENANT scopes with no node-wide price to resolve between them (an
operator alert, not a warning), or a store it cannot read all produce "unpriced", which is the
honest D16 answer. Several scopes *including* the node-wide one resolve to the node-wide price —
a scope-less read is a node-level read, so the operator's own schedule is the one it can name
(OI-64; see `PriceScheduleDeclaredRateStore.TryChooseScope`). The configured `ValueRates:Declared` store
stays composed underneath for assets that have no registered schedule.

`OnRampRegistration.AddOnRampValueRateSource` **replaces** the declared-rate registration rather
than relying on `TryAdd` ordering, because a wiring change in `Program.cs` must not be able to
quietly disconnect this.

## §lots — why a fungible asset needs per-issuance provenance

The platform unit is one fungible ASA. On chain, units bought with a card and units earned by
working are the same units, and the node keeps no balance ledger of its own (D47 — chain is the
source of truth). So nothing on chain or in the node can answer "was this unit paid for by a
card that can still be charged back?"

`on_ramp_lot` is that answer, and the only one there is. One row per fiat-funded issuance:
who, how many units, out of how much money, at which price version, against which charge, and
`reversal_window_ends`. W8.1's source-aged cash-out rule consumes lots **FIFO** — oldest
`issued_at` first — via `IOnRampLotLedger.ListIssuedForUserAsync`. Units received from selling
work or stake have no lot at all and are therefore **unaged**: they were never bought with
reversible money, so nothing needs to age before they can leave.

`reversal_window_ends` is `issued_at` plus the **rail's own** declared window
(`IValueRailProvider.SettlementProfile`), not a constant chosen here. If the rail cannot be
resolved, the issuance is refused rather than issued with a window this code invented.

## §retries — neither forever, nor giving up silently

The failure this saga exists to prevent is a person charged with nothing credited. Two ways to
cause it: return an error for work that is merely in flight (the provider retries until it gives
up), or return success for work that did not happen (the charge disappears). So:

| Situation | Outcome | Provider does | Durable record |
|---|---|---|---|
| Units confirmed in the account | `Applied` (200) | stops | lot `Issued` |
| Async payment still clearing | `AcceptedPending` (202) | stops; `async_payment_succeeded` brings it back | no lot yet |
| Issuance submitted, not yet confirmed | `Failed` (500) | retries; the allocation ledger key replays the ORIGINAL issuance | lot `Pending` |
| Transient (rail unreadable, no price registered yet, genesis not run) | `Failed` (500) | retries | lot `Pending`, `attempt_count`++ |
| Terminal refusal (identity lapsed, wrong currency, payment too small to buy a unit) | `AcceptedPending` (202) | stops | lot `Refused` + reason |
| Attempt budget spent | `AcceptedPending` (202) | stops | lot `Stuck` + reason |

`Refused` and `Stuck` are the whole point: the provider is told to stop **without** the charge
becoming invisible. A parked lot is money the node owes back, with the reason on the row, and
`IOnRampLotLedger.ListNeedingOperatorAsync` is the operator's list of them. The reversal
pipeline (W4.3) is what actually returns the money.

A lot already `Issued` short-circuits every later delivery before the rail is even re-read: one
funding intent settles into at most one lot, because the lot's record id is derived from the
intent id.

## §authority — where each fact comes from

| Fact | Read from | Never from |
|---|---|---|
| Did the payment clear, and for how much | the provider, server-side, per delivery | the webhook body |
| Which account to credit | the durable `hosted_session` funding intent | provider metadata (it is only cross-checked) |
| Which asset to issue | `IPlatformTokenGenesis.GetStateAsync` | configuration duplicated here |
| How many units | provider amount × registered price | anything a client sent |
| How long until the units are cashable | the rail's `SettlementProfile` | a constant in this directory |

The issuance runs with `actingTenantId: null` — a **platform** action. The units leave the
operator's reserve on the operator's key because a payment to the operator cleared; no user key
is signed with, so no per-user consent grant is involved. The driving tenant is recorded on the
lot for attribution. The payer's identity approval is still required (D46), and it is checked
here as well as inside the allocation seam, because a payment that clears against a lapsed
identity must become a visible refund rather than a silent credit.

`OnRampSystemIdentity.SettlementAuthorityId` is a fixed id, not a credential: the allocation
seam partitions its idempotency ledger by API-key id, and a per-process value would make every
restart a second issuance for the same payment. No route accepts it and no authorisation check
consults it.

## §wiring — this directory is inert until `Program.cs` calls it

Nothing here is assembly-scanned. `Program.cs` (owned by W4.1 for this wave, which left the
contract in a comment at its Wave 4 composition block) must call both, in this order:

```csharp
// after AddPlatformTokenGenesis / AddAllocationIssuance and the Fiat.USD rail registrations
AZOA.WebAPI.Services.Fiat.OnRamp.OnRampRegistration
    .AddFiatOnRamp(builder.Services);

// AFTER AnalyticsRegistration.AddValueRateSources — it composes the store that call installs
AZOA.WebAPI.Services.Fiat.OnRamp.OnRampRegistration
    .AddOnRampValueRateSource(builder.Services);
```

Both throw at startup when a collaborator they need is missing, so a half-wired container fails
loudly at boot rather than booting a node that takes payments it never credits. Until the first
call lands, `FiatRailStartup.Validate` refuses to boot the LIVE environment (no
`IFiatSettlementHandler`) — which is the teeth, not the prose.

## Configuration

```
Fiat:OnRamp:ReservePartition                     default "on-ramp"
Fiat:OnRamp:AllowNodeDefaultScheduleFallback     default false
Fiat:OnRamp:MaxIssuanceAttempts                  default 5 (clamped 1..20)
```

Two staleness windows exist and they are deliberately different. `PriceScheduleLimits.MaxDeclarationAge`
(30 days) is how old a registered price may be before the on-ramp refuses to SELL at it;
`ValueRates:MaxDeclarationAge` (24 h by default) is how old it may be before the D16 stamp
refuses to PRICE with it. A price between the two sells units and records the operation
unpriced — the honest state, not a bug, and the lot still carries the exact money and quantity.

The reserve refuses a draw against a partition nobody declared, so a wrong `ReservePartition`
fails closed and is operator-fixable — it can never overdraw a partition earmarked for something
else.
