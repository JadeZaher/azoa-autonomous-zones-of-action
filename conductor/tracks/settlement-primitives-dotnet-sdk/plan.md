---
type: plan
track: settlement-primitives-dotnet-sdk
created: 2026-07-19
status: in_progress
---

# Plan: settlement-primitives-dotnet-sdk

## Phase 0 — contract and release boundary

1. [x] Establish the AZOA/consumer boundary: AZOA owns generic custody,
   readiness, idempotency, receipt, and reconciliation primitives; consumers
   retain payment-provider and product-economics policy.
2. [ ] Publish the supported settlement endpoint/auth/idempotency/state/error
   matrix and an API-version compatibility fixture. Reconcile it with the active
   ArdaNova conformance track without selecting its unresolved onboarding model.
3. [x] Implement and document the caller-authorized operation
   receipt/reconciliation route. Raw owner-scoped operation IDs remain
   insufficient for cross-recipient polling.
4. [ ] Verify the unsupported delegated acting-as endpoint remains absent from
   the .NET SDK until a reviewed AZOA authorization policy exists.

## Phase 1 — contracts and transport

5. [ ] Add `Azoa.Sdk.Contracts` with public readiness/capability, allocation,
   receipt, reconciliation, idempotency, and typed-error contracts.
6. [ ] Add `Azoa.Sdk` with explicit auth, safe JSON transport, correlation,
   idempotency propagation, and a no-blind-retry ambiguity result.
7. [ ] Add fixture-based and in-process tests for replay, denied capability,
   owner scope, terminal failure, timeout, and reconciliation-required results.

## Phase 2 — integration and consumer experience

8. [ ] Add `Azoa.Sdk.AspNetCore` for `IHttpClientFactory`, options validation,
   secret-safe diagnostics, and named/typed clients.
9. [ ] Add a deterministic simulation harness and a minimal settlement worker
   sample that submits once, persists the opaque receipt reference, and
   reconciles rather than resubmitting ambiguity.
10. [ ] Add a clean-consumer package test and SDK API/OpenAPI compatibility gate
    to CI; document preview/stable NuGet publication, SBOM, and provenance.

## Verification and release

11. [ ] Run unit, in-process API integration, clean-consumer, pack, contract
    drift, and existing node/SDK/Frontend CI gates as one integrated final sweep.
12. [ ] Verify the actual `.nupkg` artifacts, sample reconciliation behavior,
    and Railway blueprint contract. Do not activate real-value routes without
    P7 reconciliation wiring and operator custody evidence.

## Commit stopping points

1. `[settlement-primitives-dotnet-sdk] define SDK boundary and delivery plan`
   — this conductor track and catalog row only.
2. `[settlement-primitives-dotnet-sdk] add versioned settlement contracts`
   — public contract package and checked-in protocol fixture.
3. `[settlement-primitives-dotnet-sdk] add typed settlement HTTP client`
   — transport/auth/idempotency/reconciliation behavior plus tests.
4. `[settlement-primitives-dotnet-sdk] add ASP.NET integration and simulator`
   — DI/options, in-process harness, and worker sample.
5. `[settlement-primitives-dotnet-sdk] add package compatibility gates`
   — pack, clean consumer, API drift, and provenance CI.
6. `[fix/deploy] align Railway schema-job retry validator`
   — isolated deployment-tooling correction, never mixed with SDK/package work.
