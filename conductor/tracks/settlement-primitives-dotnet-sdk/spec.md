---
type: spec
track: settlement-primitives-dotnet-sdk
created: 2026-07-19
status: in_progress
horizon: alpha
repository: azoa
activation_gate: >-
  SDK value writes ship only after AZOA exposes a versioned, ownership-safe
  operation receipt/reconciliation contract. The SDK must remain neutral about
  the unresolved consumer onboarding model in ardanova-financial-workflow-conformance.
depends_on:
  - integration-test-isolation-debt
related:
  - ardanova-financial-workflow-conformance
  - node-operator-governance
  - fiat-stripe-bridge
  - dotnet-client-sdk
---

# Track: settlement-primitives-dotnet-sdk

## Goal

Establish AZOA as the home for generic value-operation primitives and ship the
foundation of a maintainable .NET SDK. A server-side consumer can determine
capability and readiness, submit an idempotent allocation, obtain a durable
receipt, and reconcile an uncertain outcome without learning node internals or
reimplementing custody rules.

The first consumer is ArdaNova, but the packages are AZOA products: they contain
no ArdaNova types, project-share rules, task policy, funding valuation, or
payment-provider behavior.

## Boundary

AZOA owns:

- account, wallet, KYC, consent, fee, custody, idempotency, and reconciliation
  primitives;
- authoritative capability/readiness and operation-state responses;
- authorization and ownership checks for every value operation; and
- a stable, versioned HTTP contract for supported SDK surfaces.

Consumers own:

- payment-provider collection and provider-webhook verification;
- product-specific funding, project units, escrow, task rewards, disclosures,
  and eligibility policy; and
- user-facing presentation and recovery language.

The SDK never receives private keys or schema-owner credentials. It never
calculates fee economics, chooses a recipient from untrusted actor input, or
automatically re-submits a write whose first outcome is uncertain.

## Package family

The repository will add a versioned package family under `sdk/dotnet/`:

```text
src/Azoa.Sdk.Contracts     # pure public records, value types, and protocol states
src/Azoa.Sdk               # typed HTTP client, auth, idempotency, and reconciliation
src/Azoa.Sdk.AspNetCore    # IHttpClientFactory/DI/options integration
tests/Azoa.Sdk.Tests       # transport, replay, ambiguity, and in-process contract tests
samples/SettlementWorker   # minimal server-side allocation/reconciliation worker
```

`Azoa.Sdk` stays independent of `AZOA.WebAPI.csproj`. The ASP.NET package is the
only package that depends on ASP.NET-specific configuration and DI abstractions.

## Versioned contract

The first supported API group is the settlement primitive surface:

1. Capability and readiness reads expose KYC, wallet, consent, and supported
   operation capability without returning secrets.
2. Allocation writes accept a caller-scoped idempotency key and return a typed
   operation receipt containing a stable opaque reference, replay indicator,
   fee/result facts, and a terminal-or-reconciliation-required state.
3. Receipt and reconciliation reads are authorization-scoped to the caller and
   never expose an operation merely because its identifier was guessed.
4. An uncertain transport or provider result becomes
   `AwaitingReconciliation`; callers poll or explicitly reconcile with the same
   opaque reference/idempotency binding. The SDK does not blind-retry the write.
5. API version/capability compatibility is checked in CI from a checked-in
   contract fixture or generated OpenAPI snapshot before a package is promoted.

The current tenant allocation path can replay a stable idempotency key, but an
unrelated recipient cannot safely poll a raw owner-scoped operation ID. This
track therefore requires an ownership-safe receipt/status route, or an explicit
replay-only contract, before claiming a polished settlement SDK surface.

Delegated acting-as credentials are excluded until the API policy exists. The
current child-credential endpoint is unavailable, so the SDK must default-deny
that path rather than mirror stale workflow-client behavior.

## Developer experience

- Typed `HttpClient` configuration using `IHttpClientFactory`, explicit
  `AzoaSdkOptions`, and no secret logging.
- Explicit API-key or bearer authentication, with no mutable actor identity in
  request bodies.
- A no-throw result/error model that distinguishes validation/authorization,
  terminal failure, transport failure, and `AwaitingReconciliation`.
- Typed idempotency keys and operation references; request correlation is
  propagated without exposing internal storage keys.
- A simulation/test harness that exercises an in-process AZOA host and records
  replay, KYC/wallet denial, ownership, timeout, and reconciliation behavior.
- Deterministic package metadata, clean-consumer sample, API compatibility
  checks, and a NuGet publishing/provenance plan. Package artifacts are not
  deployed to Railway; node/API changes retain the Railway blueprint gate.

## Non-goals

- A full replacement for the TypeScript SDK or historical 95-endpoint parity.
- Stripe/fiat collection, payment-provider webhooks, or payment valuation.
- Project shares, task compensation, consumer product policy, or branding.
- Browser wallet signing, private-key custody, real-value activation, or
  automatic recovery from ambiguous writes.
- Selecting between self-sovereign and tenant-custodial consumer onboarding
  before the active conformance track resolves that decision.

## Acceptance criteria

1. The package family compiles, packs deterministically, and a clean sample
   consumes the exact packed artifacts.
2. Each write surface propagates one caller-scoped idempotency key and proves
   replay does not produce a second value effect.
3. KYC, wallet, consent, ownership, and capability denials return typed
   fail-closed results and do not invoke an effect.
4. Ambiguity produces a durable reconciliation outcome; no SDK retry can create
   a second write.
5. Receipt/status reads cannot be used to enumerate or inspect another
   tenant's or actor's operation.
6. In-process API contract tests cover the live AZOA surface, not only mocks.
7. CI detects supported-contract drift, builds/tests/packs the SDK, and keeps
   node deployment validation separate from package publication.
8. ArdaNova can consume only generic primitives; no consumer economics or
   product-specific terminology appears in public SDK source.
