---
type: plan
track: ardanova-azoa-settlement-integration
created: 2026-07-19
status: in_progress
---

# Plan: ArdaNova to AZOA settlement integration

## Phase 0 - contract and provider gates

1. [x] Record the ArdaNova/AZOA/payment/KYC ownership boundary.
2. [x] Select Identomat as the first proposed live adapter so the reference
   Stripe Payments and KYC credentials remain vendor-separated.
3. [ ] Resolve the self-sovereign versus tenant-custodial ArdaNova launch model
   in `ardanova-financial-workflow-conformance`.
4. [ ] Consume the versioned capability, allocation, receipt, and reconciliation
   contracts produced by `settlement-primitives-dotnet-sdk`.

## Phase 1 - ArdaNova persistence and policy

5. [ ] Add provider-neutral `IParticipantReadinessPolicy` and explicit
   non-sensitive readiness reasons.
6. [ ] Add immutable payment-event and business-settlement ledgers with unique
   Stripe event, PaymentIntent, AZOA idempotency, and receipt correlations.
7. [ ] Gate funding, paid/token tasks, grants, payouts, transfers, and equivalent
   worker paths while leaving project creation and collaboration available.

## Phase 2 - payment and settlement adapters

8. [ ] Add Stripe Checkout/PaymentIntent creation with server-owned order facts,
   dedicated credentials, restricted logging, and no client-selected recipient.
9. [ ] Add raw-body Stripe webhook verification, account/type allowlists,
   event-ID deduplication, and a transactional outbox handoff.
10. [ ] Invoke the AZOA SDK once per stable business settlement key, persist the
    opaque receipt, and reconcile uncertainty without resubmission.

## Phase 3 - hosted KYC providers

11. [ ] Add a durable hosted-attempt admission and external-outcome CAS protocol
    to the AZOA provider boundary before enabling any hosted adapter.
12. [ ] Add Identomat begin/result/callback support with a configured API key,
    configuration ID, callback key, server-side result fetch, and minimal outcome
    persistence.
13. [ ] Evaluate Stripe Identity only through a separate boundary decision; do
    not reuse Payments credentials or enable a Stripe key in AZOA implicitly.
14. [ ] Enforce provider configuration and simulation rejection at startup for
    Production and Mainnet.

## Verification and activation

15. [ ] Run the test matrix against one live in-process AZOA/Surreal host and
    simulated Stripe/KYC transports.
16. [ ] Run the integrated .NET, SDK, frontend, template, and deployment gates
    once after all implementation fixes.
17. [ ] Resolve exact Railway project/environment/service IDs, provision secrets
    without printing them, deploy, and require terminal `SUCCESS` plus health and
    reconciliation evidence before reporting deployment success.

## Focused commit stopping points

1. `[ardanova-integration] define financial engine boundary and provider choice`
2. `[ardanova-integration] add readiness and settlement ledgers`
3. `[ardanova-integration] add Stripe payment event boundary`
4. `[kyc] add durable hosted-provider outcome protocol`
5. `[kyc] add the Identomat adapter`
6. `[ardanova-integration] add receipt-driven reconciliation`
7. `[verify] prove financial engine and deployment gates`
