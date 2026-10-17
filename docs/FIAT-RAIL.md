# FIAT-RAIL — the retired sidecar's invariants, ported forward

`fiat-sidecar/` (a standalone Express bridge around the SDK's
`AzoaStripeOrchestrator`) was deleted on 2026-09-08 per RUNBOOK **D43/D45**:
Stripe checkout, webhook signature verification, and the dispute/refund event
stream now bake into AZOA as a first-class `Fiat.USD` rail
(`FiatRailProvider` implementing `Interfaces/IValueRailProvider.cs`) instead
of living in a separately-deployed service with no deployment target and no
CI job. This document is that former service's AGENTS.md invariants location
— the sidecar is gone, but the hard-won rules it encoded are not, until the
port lands.

## The retired service, for the record

- Dev port: **4000** (`fiat-sidecar/Dockerfile` `EXPOSE 4000`, compose
  service `fiat-sidecar` mapped `4000:4000` under the opt-in `fiat` profile).
- Depended on `azoa-api` being healthy and called it over
  `http://azoa-api:5000` with a bearer `AZOA_API_KEY`.
- Two routes: `POST /api/checkout` and the Stripe webhook receiver.
- Whoever builds `FiatRailProvider` does not need to keep port 4000 free for
  it — the new rail lives inside the AZOA process on the API's own port, not
  as a separate listener.

## Invariants that MUST be ported, not re-derived

Each of these closed a shipped defect. Re-deriving them from scratch risks
reopening the same bug; carry the rule, not just the intent.

### §pricing — mint quantity is server-side, never client-supplied

The minted quantity was computed at webhook time from the
**Stripe-confirmed** `session.amount_total` (cents) times an operator price
schedule (`TOKENS_PER_CENT`), floored to an integer. Client checkout
metadata carried only identity/routing (`userId`/`assetId`/`chainType`/
`kind`) — never the mint quantity. This closed a CRITICAL money-printer
where a client could mint an arbitrary amount for a trivial payment.

Two units, never conflated:
- **fiat cents** (`amount_total`, Stripe minor units) — the money that
  cleared.
- **AZOA base units** (integer string) — the token quantity.

⚠ **Open question the port must answer (RUNBOOK D43, unresolved):** is an
operator-configured tokens-per-cent schedule a *rail parameter* (fine inside
AZOA) or *consumer economics* (must stay in the caller, per D7/D16 — AZOA
receives already-decided amounts and never computes economics)? The sidecar
treated it as the former; decide before the port fixes it in place.

### §auth — checkout is authenticated, routing values are allowlisted

Checkout required a bearer token compared with a **constant-time compare**
that **fails closed** when the secret is unset (401, not "trust the
request"). Routing values (`assetId`/`chainType`/`kind`) were validated
against server-side allowlists. CORS used an origin allowlist, never
allow-any-origin.

### §webhook-mapping — transient vs terminal outcomes get different HTTP statuses

The allocation seam can return a RETRYABLE in-progress failure before the
chain transaction hash is recorded. Returning a plain error there makes
Stripe retry forever, eventually give up, and leave the customer charged
with no tokens. The mapping that avoided this:

| Outcome | HTTP | Rationale |
| --- | --- | --- |
| Webhook validation error (bad signature, missing metadata, bad `session.id`) | **400** | Stripe does not retry a 4xx; the request is genuinely malformed. |
| Ignored (not `checkout.session.completed`) | **2xx** | Nothing to do. |
| Settled (AZOA confirmed) | **2xx** | Done. |
| Accepted-pending (AZOA in-progress, no tx hash yet) | **2xx** | Backend reconciliation settles from chain truth; stop Stripe retries. |
| Failed-terminal (genuine failure) | **500** | Stripe retries with bounded backoff. |

### §idempotency

`Idempotency-Key = session.id` (always present, unique per checkout).
`session.payment_intent` was previously used but can be null/non-string,
silently falling through to content-key dedup and colliding distinct
payments. A missing/empty `session.id` must be rejected, never proceeded
past.

### §errors

Handlers never echo raw exception text (Stripe internals / backend
method+path) to the caller. Full detail is logged server-side with a
correlation id; the client gets a generic message plus that opaque id.

## Wiring status

All four Wave 4 registrations are live in `Program.cs`, in the
**Wave 4 settlement composition** block below the saga layer:
`AddFiatOnRamp`, `AddOnRampValueRateSource`, `AddFiatReversalPipeline` and
`AddReversalRiskValueGate`. **`AddFiatOffRamp` (W8.1) sits directly below them**
and follows the same rule. Each verifies its collaborators and throws at boot
rather than registering something that cannot work.

## Registration is not capability (OI-29)

`FiatRailStartup.Validate` runs **before** `builder.Build()` and reads
**configuration and the container**: the environment name, both secrets, the
return-origin allowlist, the amount bounds, and whether an
`IFiatSettlementHandler` is registered at all. That last check proves **a type
is in the container and nothing more**. It was once described in this file as
meaning "a node cannot charge a real card while nothing is able to credit it",
and the very next paragraph then listed two things the node still needed before
it could credit anything — the guarantee falsified itself four lines later.

`FiatRailStartup.GuardLiveCapabilityAsync` is the capability half. It runs
**after** `builder.Build()`, because the two facts that actually decide whether
a cleared payment can be credited are rows, not settings:

1. the reserve partition named by `Fiat:OnRamp:ReservePartition` is declared on
   the platform unit's reserve ledger
   (`IPlatformReserve.DeclarePartitionsAsync`) **and still has units available**
   — a declared-but-exhausted partition refuses a draw exactly like an
   undeclared one, so presence alone is not the capability; and
2. a **current** price is registered for (chain, platform asset): active, not
   future-dated, and inside `PriceScheduleLimits.MaxDeclarationAge`. That is the
   same read `OnRampSettlementHandler` performs at webhook time, kept in step
   deliberately — a guard that accepted a price the settlement path would refuse
   would pass a node that still cannot credit.

A blank `Fiat:OnRamp:ReservePartition` resolves to **no partition**, not back to
the `"on-ramp"` C# default: `OnRampOptions` is bound with `.Get<T>()`, so an
environment variable set to `""` **replaces** the default rather than leaving it.
The probe therefore resolves blank to the narrowest answer, in code.

Neither guard touches a store for a disabled rail or a `test` environment, so a
dev node still boots against an empty database.

### Where it is enforced

The same probe — `FiatCreditCapabilityProbe.ProbeAsync` — also runs **at the
counter**: `FiatRailProvider.BeginCheckoutAsync` calls it in the live
environment, after the identity gate and **before** the durable intent is
claimed or a payment page exists. That is the control that actually stops a real
card being charged into a void, because a boot guard only proves the state at
boot — an operator can retire the last price schedule at 3pm on a node that
booted at 9am. A refusal returns the consumer-safe `RAIL_UNAVAILABLE` message
and logs the operator-facing reason; nothing durable is written.

`GuardLiveCapabilityAsync` is written, unit-tested
(`tests/AZOA.WebAPI.Tests/Fiat/FiatRailStartupCapabilityTests.cs`) **and wired**:
`Program.cs` calls
`await AZOA.WebAPI.Providers.Fiat.FiatRailStartup.GuardLiveCapabilityAsync(app.Services, fiatOptions);`
immediately after `var app = builder.Build();`, so a live node whose on-ramp
partition or price schedule is missing refuses to start rather than accepting a
card it cannot credit. Both controls are live at once and they answer different
questions: the boot guard proves the node's state at boot, the checkout probe
proves it again per payment, because an operator can retire the last price
schedule at 3pm on a node that booted at 9am.

The checkout probe is additionally **tenant-scoped**: it asks whether the price
that would actually settle *this* payment exists, following the settlement
handler's own precedence (the driving tenant's schedule first, the operator's
node-wide default only where `Fiat:OnRamp:AllowNodeDefaultScheduleFallback`
permits it). The boot guard keeps the node-wide question, because at boot there
is no payment and no tenant to ask about.

Both facts remain **operator work** before the first real card: declare the
`on-ramp` partition, and register a first price schedule
(`POST /api/fiat/price-schedule` as the node operator, `UnitsPerMinorUnit: "100"`
for the settled 1 ARDA = $0.01 base at 2 decimals). What changed is that
forgetting either now refuses the checkout instead of charging the card and
parking the lot `Stuck`.

## The off-ramp — money out (W8.1)

`Services/Fiat/OffRamp/` is the other end of the loop: units back into a
person's own bank account, through the same provider's Connect payouts.
`OffRampRegistration.AddFiatOffRamp` verifies all thirteen collaborators and
throws at boot rather than letting `api/fiat/payouts` answer with a container
error. Every default is fail-closed, so an **absent** `Fiat:OffRamp` section is
a working configuration in which cashing out reports that it is not available —
it never half-works.

Enabling it requires `Enabled`, `OnboardingReturnUrl` and `OnboardingRefreshUrl`,
and each URL must itself satisfy `Fiat:Checkout:AllowedReturnOrigins`: an
unbounded return URL on an identity-verification page is the same open redirect
the checkout page guards against. `ReservePartition` names the partition a
cash-out is drawn back into and should match `Fiat:OnRamp:ReservePartition`
unless an operator deliberately separates them.

The subject is always the `userId` **route** value — no body field can redirect
a cash-out — and the destination bank account is resolved by the node from that
person's own durable onboarding record, never from anything a caller sends. The
routes are `GET/POST /api/fiat/payouts/{userId}/account`,
`GET /api/fiat/payouts/{userId}/availability`, `POST /api/fiat/payouts/{userId}`
and `GET /api/fiat/payouts/{userId}`, with an operator console at
`/admin/fiat-payouts`. `Services/Fiat/OffRamp/AGENTS.md` is the authoritative
note on aging, the ledger and the saga.

A third thing is now owed alongside the two below before real money moves out:
the connected-account onboarding flow has to be reachable, which means both
onboarding URLs must resolve to pages this deployment actually serves.

## Bank payout completion

The Connect funding transfer and the bank payout have separate durable references.
`BankPending` means funds are transferred to the connected account and bank payout
processing is pending. `Paid` requires Stripe's server-fetched bank payout status
to be `paid`; a successful transfer alone cannot set it. Cash-out responses keep
the same shape and provide the current state through `status` and `statusMessage`.

The operator board can advance `BankPending` to reconcile it immediately. A worker
also reconciles pending bank payouts every minute. Configure connected-account
payout events on the signed webhook endpoint. See `Services/Fiat/OffRamp/AGENTS.md`
for manual schedules, provider idempotency windows and ambiguous-outcome handling.

## What is NOT yet built (still open after this retirement)

- ~~**D10's reversal-event pipeline**~~ — **BUILT** in this same wave (W4.3).
  `charge.refunded`, `charge.dispute.created`, `charge.dispute.closed` and
  `charge.dispute.funds_withdrawn` are first-class `fiat_reversal` ledger facts
  driving `FiatCompensationSaga` down a three-rung recovery ladder (balance →
  open holds → purchased units), with an operator console at
  `/admin/fiat-reversals`. See `Services/Fiat/Reversal/AGENTS.md`.
- ~~**The `FiatRailProvider` itself**~~ — **BUILT** in this same wave (W4.1):
  `Providers/Fiat/FiatRailProvider.cs`, registered through the rail-aware
  `BlockchainProviderFactory`.
- ~~**The on-ramp**~~ — **BUILT** in this same wave (W4.2): a cleared payment is
  priced by the tenant's registered schedule and issued from the platform reserve
  as an `on_ramp_lot`. See `Services/Fiat/OnRamp/AGENTS.md` for §pricing, §lots
  and §retries.
- ~~**The off-ramp**~~ — **BUILT** in Wave 8 (W8.1): a person's units become
  money in their own bank account through Connect payouts, aged against the
  chargeback window so freshly on-ramped funds cannot be cashed straight out.
  See `Services/Fiat/OffRamp/AGENTS.md`.
- **Which live Stripe integration wins** — ArdaNova already runs its own
  Stripe integration independent of the sidecar (`StripeWebhookController.cs`,
  `AzoaAllocationService.cs`). RUNBOOK D45 scopes the AZOA rail to the
  fiat↔ARDA on/off-ramp, not project checkout; see D43-D51 for the full
  decision record.

See `conductor/RUNBOOK.md` decisions **D9** (superseded), **D43**, **D45**
for the complete history and rationale behind this retirement.


Development enables Stripe **test** checkout and cash-out. The ignored root `.env`
loads only explicitly allowlisted DEV aliases; Compose forwards those same
canonical settings without mounting the file or importing live keys. Return URLs
default to localhost:3000; override canonical Checkout/OffRamp URLs and allowlisted
origins together when using another frontend port. Configuration availability does
not establish a funded asset: checkout issuance still requires platform-token
genesis, reserve inventory, an active current price schedule and current identity
approval. No successful live vendor workflow or payment is implied by local tests.

Operators can request `/api/admin/fiat-payouts/{id}/recover` with a reason after bank
failure. It refetches bank and funding facts, reverses recovered funding and restores
only the backed unit amount. Ambiguous transfer outcomes remain reserved for chain
reconciliation. Refund retries require `retryFailed: true` and the displayed
`expectedRefundAttempt`; pending or unknown prior refunds never allow another key.
