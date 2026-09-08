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
`AddReversalRiskValueGate`. Each verifies its collaborators and throws at boot
rather than registering something that cannot work, and
`FiatRailStartup.Validate` refuses the **live** environment unless an
`IFiatSettlementHandler` is registered — so a node cannot charge a real card
while nothing is able to credit it.

Two things are still owed before real cards, and neither is code: declare the
`on-ramp` partition on the platform unit's reserve ledger
(`IPlatformReserve.DeclarePartitionsAsync`), and register a first price schedule
(`POST /api/fiat/price-schedule` as the node operator, `UnitsPerMinorUnit: "100"`
for the settled 1 ARDA = $0.01 base at 2 decimals).

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
- **Which live Stripe integration wins** — ArdaNova already runs its own
  Stripe integration independent of the sidecar (`StripeWebhookController.cs`,
  `AzoaAllocationService.cs`). RUNBOOK D45 scopes the AZOA rail to the
  fiat↔ARDA on/off-ramp, not project checkout; see D43-D51 for the full
  decision record.

See `conductor/RUNBOOK.md` decisions **D9** (superseded), **D43**, **D45**
for the complete history and rationale behind this retirement.
