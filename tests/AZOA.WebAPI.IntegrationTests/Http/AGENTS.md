# Http — backend HTTP flow tests

Flows through the **highest fast public seam**: `WebApplicationFactory` with real DI, real
authentication schemes and policies, routing, model binding, rate limiting and a real SurrealDB
namespace. Only the process edge is fake: Identomat and Stripe outbound HTTP, and DNS for webhook
receivers. Assertions are HTTP responses and committed rows, never call counts.

Run (needs the dev SurrealDB on `127.0.0.1:8020`, see root `AGENTS.md` §Test):

```powershell
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj -c Debug --filter "FullyQualifiedName~AZOA.WebAPI.IntegrationTests.Http"
```

## §factory — `Infrastructure/HttpFlowWebApplicationFactory.cs`

Subclass of `AZOATestWebApplicationFactory` (so the per-class namespace, test principals and
API-key forwarding stay identical). It configures a node with Identomat KYC/KYB and the Stripe
test rail enabled, using **random per-factory credentials** that exist only in memory.

- Settings go through `UseSetting` **and** `AddInMemoryCollection`: `FiatOptions` and the rate
  limiter are bound eagerly in `Program.cs`, before any late configuration source exists. This
  precedence was proven by execution in a `bb512cf` worktree (the webhook controller verified
  signatures with the injected secret).
- `Kyc:TenantProviderOverride` is enabled for every tenant (`*`) so the `/kyc` provider-choice
  payload can be read; operator selection is unaffected.
- Vendor base URLs are config-driven (`Kyc:Identomat:BeginBaseUrl/ResultBaseUrl`,
  `Fiat:ApiBaseUrl`), so the fake hosts below are real configuration, not a code seam.
- `RateLimiting:Financial/TenantCustodial:PermitLimit` are raised: onboarding a tenant spends
  operator "financial" permits and a class's own setup must not trip the limiter. The limiter
  stays in the pipeline.
- `ServerLog` captures warning-and-above host logs; `ShouldReturnAsync`/`ReadOkAsync` append the
  tail to every status assertion. The end sweep diagnoses from output, not reruns.

## §vendor-edge — `Infrastructure/VendorEdge.cs`

`ConfigureHttpClientDefaults` makes the edge the primary handler of **every**
`IHttpClientFactory` client, named or typed (stripe, the Wormhole adapter, anything added later).
Clients whose `Program.cs` registration sets its own primary handler (`identomat`,
`hosted-identity`, `webhook`, `action-webhook`, typed `WttGuardianTransport`) are also re-pointed
by name after `Program.cs`, so the edge wins regardless of how defaults are ordered. The edge
routes by host to a registered responder, forwards only declared infrastructure (the test
SurrealDB endpoint, `AllowPassthrough`) to the real network, and **throws for any other host**,
so an accidental real egress fails instead of leaving the box. The webhook SSRF guard gets a
resolver for `*.flow.test` (`WebhookSsrfGuard.UseResolver`), the only DNS seam.

Not covered: the chain providers construct `new HttpClient()` themselves (`AlgorandProvider`,
`EthereumProvider`, `SolanaProvider`); nothing in a test host can route them. Their injectable
handler is a constructor parameter the DI registration never supplies. Round-4 product item:
providers take their handler from DI (`IHttpClientFactory` or an injected `HttpMessageHandler`),
after which chain RPC fakes register on this edge like any vendor.
## §identomat-fake — `Infrastructure/IdentomatVendor.cs`

Shapes follow the vendor documentation (`.omc/research/identomat-api-2026-10-15.md`, "Result
examples") and `Providers/Kyb/IdentomatKybResultParser.cs`. `IdentomatSessionMode`:

- `Standard` (default, documented): `/begin` returns `{ id, url: "?session_token=<id>" }`; the id
  **is** the session token, so the adapter sending it to `/external-api/result/` is correct.
- `AdditionalInformation` (configuration with `additionalInformationRequest`): adds a child
  `additionalSessionId` carried by the url. Which token `/external-api/result/` accepts for that
  pair is undocumented; the variant assumes the url token and no test relies on it. Confirm with
  the vendor when real workflows exist, then pin it with a flow.

KYB `/result` and callbacks are keyed by the session `id` (docs: "Use this to … receive
callbacks"). Tests flip `IdentomatSession.Verdict` to play the vendor's review and post callbacks
with `IdentomatVendor.CallbackBody`.

Undecided vs manual review (round-4 fix batch, RV-L2 test gap): the documented `result` values are
`APPROVED | REJECTED | MANUAL_CHECK | ADDITIONAL_INFORMATION_REQUESTED`
(`identomat-callback-contract-2026-10-15.md`); the docs give no shape for a session nobody has
submitted yet. `Pending` therefore answers `{"result":null}` (no verdict, which the adapter maps to a
non-terminal `UNKNOWN` without manual review), and `ManualCheck` answers the documented
`MANUAL_CHECK`. The fake previously answered `MANUAL_CHECK` for every undecided session, which hid
L2-1/L2-2. `IdentityResultRequests` counts `/external-api/result/` calls for budget assertions.

The widget host also answers the documented `POST /external-api/delete/` (`company_key`, `session_token`):
`true` when it deletes, `{"argumentError":"session-not-found"}` otherwise (`Providers/Kyc/IdentomatSessionDeletion.cs`
treats both as gone). A deleted session stops answering `/external-api/result/`; `DeleteRequests` lets an orphan or
late-response flow assert the node actually asked (IDENTOMAT-RECOVERY, L2).

## §stripe-fake — `Infrastructure/StripeVendor.cs`

`POST /v1/checkout/sessions` (form-encoded) and `GET` by id, Bearer secret key checked.
`Idempotency-Key` behaves like Stripe: same key and parameters replay the session, same key with
different parameters is a 400 `idempotency_error`. `Seed` places a session Stripe already holds;
`CompletePayment`/`Expire` change Stripe's own state; `CheckoutEvent(..., claimedPaymentStatus)`
lets a delivery body disagree with that state to prove the authoritative re-fetch.
`SignatureHeader` implements Stripe's documented `t=…,v1=HMAC-SHA256("{t}.{payload}")` scheme
**independently** of the node's verifier, so a shared bug cannot make both sides agree.
## §fixture-contract — `Infrastructure/FrontendFixtureContract.cs`

The MSW slice fixtures in `frontend/src/test/msw/fixtures/*.json` are compared with the live
payloads these flows receive: property names, JSON value kinds (string, number, boolean, null,
object, array), nested objects, and array element shapes in both directions (every live element
against the fixture's first element, every fixture element against the live first element; a
fixture with an empty array where the live payload has elements fails). Covered: business verification capabilities/session/status,
webhook registration status, mutation result and withdrawal, tenant KYC selection and provider
choice.
## §flows

| Test class | Flow | Unhappy paths kept (why) |
|---|---|---|
| `BusinessVerificationFlowTests` | Tenant (operator-issued key) begins KYB → vendor reviews → callback → APPROVED; both callback ingresses | Replay after decision is a no-op (vendors redeliver); wrong callback key: identical 202 body and no decision, then the right key approves (anonymous oracle); rejected review is terminal for its request key |
| `IdentityVerificationFlowTests` | Tenant enrols a custodial member → hosted KYC session → callback → member gate `Approved`; tenant policy and provider-choice payload shapes | Callback claim ignored while vendor pending; replay keeps the decision; a forged key decides nothing where the genuine key, same vendor state, approves; vendor rejection → `Rejected` |
| `HostedSessionReconciliationFlowTests` | Vendor created the KYB session but the response was lost → tenant sees `NEEDS_OPERATOR` → operator queue → operator attaches the verified session → tenant resumes → approval | Reconciling a claim younger than five minutes is refused (would race a live creation) |
| `StripeWebhookFlowTests` | Money path: a signed `checkout.session.completed` claiming "paid" while Stripe's own session is unpaid decides nothing; Stripe clears, `async_payment_succeeded` → intent PAID, payer reads `complete` / `payment_received`. Expiry moves the intent once; a re-signed redelivery is answered from the inbox | One table: wrong secret, stale timestamp, livemode mismatch → 400, no inbox row. Payment deliveries are never answered 4xx |
| `WebhookRegistrationFlowTests` | Tenant registers a receiver; a lost response is replayed from the receipt without re-disclosing the secret; a withdrawn change can never apply | Stale `expectedVersion` → 409 |
| `ClaimInviteFlowTests` / `ClaimInviteWithheldTokenFlowTests` | Tenant provisions a member → mints a claim invite → member claims with a password it chose; storage holds only the `h1:` digest. With the production policy (`WithheldClaimTokenWebApplicationFactory`) the tenant's response carries no token and a 72 h expiry | Stored digest presented as a token, used invite (password unchanged), expired invite, legacy plaintext row: each 404 with no ownership change (each is a way a tenant or DB reader could take the account) |
| `RecoverySurfaceAuthorizationTests` | Who reaches `api/ethereum/recovery/{network}`, `api/operator/ethereum/recovery/{network}`, `api/operator/wallet-identities`, `api/operator/hosted-sessions/reconciliation` | Authorized callers get a non-404, non-5xx answer (an unconfigured feature is a 409) |

Time is arranged only where no route can: the reconciliation test ages `creation_claimed_at` with
a typed `SurrealWriter.UpdateOnly`; the Stripe tests seed the funding intent (and `Seed` the same
session at the fake) as `FiatRailProvider.BeginCheckoutAsync` leaves it, because opening one over
HTTP needs a KYC-approved payer; the E2E on-ramp journey covers that path.

Cut: no tests for request-validation variants already covered by unit tests
(`tests/AZOA.WebAPI.Tests/Kyb`, `Kyc`), no mock-interaction assertions, no "throws on null".
## §adding-a-flow (round-4 lanes N/A/B/C)

1. Derive from `HttpFlowTestBase` (`IClassFixture<HttpFlowWebApplicationFactory>` is inherited).
2. Onboard actors through routes: `OnboardTenantAsync()` (register + operator-issued tenant key),
   `Operator` (recent node-operator session), `Client` (first-party login), `Anonymous`.
3. A new third party reached through `IHttpClientFactory` gets its own `*Vendor` class registering
   a host on `Flow.Edge` and a config key pointing at that host in `VendorSettings()`; the edge
   already owns every factory client. **Chain RPC cannot be faked this way today**: the providers
   build their own `HttpClient` (see §vendor-edge). Until that product item lands, NFT (N), WTT /
   Solana (A), Hold settlement (B) and DEX (C) flows run against `Blockchain:Mode=Simulated` in a
   dedicated factory (`tests/AZOA.WebAPI.IntegrationTests/AGENTS.md` §treasury-provider-config),
   never against public RPC.
4. Assert responses and rows (`RowsAsync`, typed `SurrealQuery<T>`); add a fixture-contract check
   when a slice consumes the payload.

## §webhook-drain — `Infrastructure/WebhookDrainWebApplicationFactory.cs`, `WebhookOutboxDrainFlowTests.cs`

The flow host plus `Webhooks:Enabled=true`, a one-second poll, no startup delay and
`MaxAttempts=1` (one receiver failure dead-letters). The class declares
`IClassFixture<WebhookDrainWebApplicationFactory>` on top of the base's fixture; xUnit builds
both, but the unused base factory never starts a host. Each test owns a tenant and a unique
`receiver-<guid>.flow.test` host registered on the edge, so the shared worker cannot cross
tests. The event is produced by a real transition (tenant provisions a child → participation
grant → consent notification); the only arranged state is the crashed-worker lease in the
abandoned-attempt test, which no public route can create. Signature verification re-implements
the documented length-prefixed HMAC (`Core/Webhooks/WebhookHmacSigner.cs`) independently.

The abandoned-attempt arrangement resets only the current cycle's attempt count
below its budget, retaining lifetime attempts. Otherwise an exhausted dead-letter
row correctly classifies as `AttemptBudgetExhausted` before the expired-lease case.
The first-party KYC dead-attempt arrangements match the session's observed state
and state version, or a pending/in-review submission, through guarded typed writers.

First-party approval failures attach only the same attempt's hosted state,
authoritative outcome, outcome detail and available application error code.
Diagnostic reads run only after the observed assertion value is wrong; they never
include provider bodies, person/document attributes, session tokens or identifiers.
Expected approval and value-access assertions remain unchanged.

## §claim-invite — `Infrastructure/ClaimInviteFlowTestBase.cs`, `Infrastructure/WithheldClaimTokenWebApplicationFactory.cs`

`appsettings.IntegrationTest.json` sets `Tenant:ClaimInvites:ReturnTokenToTenant=true` so the default flow
host can hand the member side its token (the only place it is on; `Services/Auth/AGENTS.md` §claim-invites).
`WithheldClaimTokenWebApplicationFactory` overrides it back to `false` to prove the production response. The
password claim writes through the wallet-identity readiness gate, so `WalletIdentityReadyAsync` drives the
host's own `WalletAuthIdentityMigrationService` to `Ready` instead of racing the 5 s worker. Arranged state:
an expired invite (typed `UpdateOnly` of `expires_at`) and a legacy plaintext row (typed `Create`), because
no route can produce either. Ownership is read back as a Guid from the user row's `owner_tenant_id` link.
`Hosting/ClaimInviteLifetimeStartupTests` boots a host with an 8-day lifetime and expects the boot to fail.
