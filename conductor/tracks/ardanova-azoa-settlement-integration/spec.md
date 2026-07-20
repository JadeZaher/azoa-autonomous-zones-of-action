---
type: spec
track: ardanova-azoa-settlement-integration
created: 2026-07-19
status: in_progress
horizon: alpha
depends_on:
  - settlement-primitives-dotnet-sdk
  - ardanova-financial-workflow-conformance
related:
  - fiat-stripe-bridge
  - node-operator-governance
---

# Track: ArdaNova to AZOA settlement integration

## Goal

Build the hardened consumer-side financial engine that lets a node operator add
payment and identity-provider credentials without moving consumer economics,
payment truth, or product authorization into AZOA.

ArdaNova may create projects and allow ordinary collaboration before a
participant is value-ready. Funding, paid or tokenized tasks, grants, payouts,
transfers, and any equivalent settlement action require one server-side
participant-readiness decision immediately before the first external value
effect. Immutable payment, denial, and idempotency records may be written first
so retries and recovery remain auditable.

This track replaces the missing `azoa-financial-engine-integration` reference.
It consumes the generic `settlement-primitives-dotnet-sdk` contract and does not
introduce ArdaNova types into AZOA packages.

## Ownership boundary

ArdaNova owns:

- Stripe Checkout/PaymentIntent creation, payment webhook verification, event
  deduplication, valuation, refunds, project/task economics, and product policy;
- its participant-readiness decision and authorization of every value action;
- the local payment-event, business-settlement, and reconciliation ledgers; and
- operator-facing recovery language and manual review of unresolved ambiguity.

AZOA owns:

- provider-bound KYC, wallet, consent, custody, fee, and capability primitives;
- caller-authorized idempotent allocation, opaque receipts, and observation-only
  reconciliation; and
- independent authorization of every AZOA value effect.

Stripe payment success is never KYC approval. ArdaNova readiness is never an
AZOA authorization bypass. AZOA never receives Stripe credentials or raw Stripe
events, and ArdaNova never receives wallet signing material or raw KYC evidence.

## KYC provider posture

Identomat is the first proposed live adapter for the reference deployment. Its
dashboard issues an API key and its hosted redirect/result APIs fit the
provider-neutral lifecycle while preserving the existing rule that AZOA holds
no Stripe credential. Its published Essentials plan currently has a monthly
minimum, so provider selection remains configurable rather than assuming it is
economical for every small node. The adapter must verify the configured callback
key with a constant-time comparison, fetch the authoritative result server-side,
and persist only a minimal normalized outcome plus non-reversible payload digest
rather than raw identity documents or result JSON.

Stripe Identity is a deferred convenience candidate where business location and
use case are eligible. Enabling it inside AZOA would require an explicit,
reviewed Identity-only exception to the established zero-Stripe-secret boundary,
a dedicated restricted Identity key, and a dedicated Identity webhook secret.
The Payments secret key and payment webhook secret may never be reused for KYC.
This track does not approve or enable that exception.

Manual/mock KYC and any future admin override are simulation-only. Current code
also requires Development, simulated blockchain mode, and real-value bridging
disabled for the manual flow. The broader Local/Test/CI/Testnet startup allowlist
only permits simulation configuration; it does not make the manual adapter
available outside Development. Startup must preserve those stricter conditions,
reject simulation paths outside that allowlist, and always reject them in
Production or on Mainnet. The target production gate must fail closed when the
selected real provider, policy, trust profile, API origin, or callback secret is
unavailable.

## Exactly-once settlement protocol

1. Verify the raw Stripe webhook and deduplicate its immutable event ID.
2. Validate the PaymentIntent/account/currency/amount against the stored
   ArdaNova order; never trust webhook metadata as product authority.
3. Persist the business settlement intent before calling AZOA.
4. Re-evaluate participant readiness and product authorization.
5. Submit once through the AZOA .NET SDK with a stable caller-scoped key such as
   `stripe:pi:{paymentIntentId}:allocation:v1`.
6. Persist AZOA's opaque receipt reference and state.
7. Complete only from a terminal successful receipt. An uncertain transport or
   `AwaitingReconciliation` result remains pending and is reconciled through the
   same receipt; it is never safe to resubmit the value write.

## Acceptance criteria

1. One configuration matrix names the owner and secret boundary for Stripe
   Payments, Stripe Identity, Identomat, ArdaNova, and AZOA.
2. Duplicate Stripe events and distinct events for one PaymentIntent converge
   on one business settlement and at most one AZOA write.
3. Every value-action family uses the same server-side readiness policy before
   the first external value effect; collaboration routes do not.
4. KYC pending, rejected, expired, unavailable, stale-policy, or mismatched
   provider outcomes deny value access without leaking provider evidence.
5. Hosted KYC begin and callback paths reserve durable attempts, authenticate
   callbacks/webhooks, deduplicate events, and CAS one terminal outcome.
6. SDK timeouts after submission produce a durable reconciliation state and no
   blind retry. The caller persists its original idempotency binding and verifies
   the returned opaque receipt reference; ownership denial is indistinguishable
   from absence.
7. Production/Mainnet startup rejects simulation KYC, mock providers, and admin
   overrides; real-provider secrets are supplied only at deployment.
8. Live-host tests prove the complete Stripe-event to AZOA-receipt flow with
   simulated external providers before any real-value activation.

## Non-goals

- Putting Stripe or ArdaNova economics into AZOA.
- Adding ArdaNova-branded runtime code to the AZOA repository; consumer behavior
  here is a contract/test fixture and is implemented in ArdaNova's repository.
- Treating KYC as legal advice or claiming one provider satisfies every
  jurisdiction's compliance obligations.
- Persisting raw identity documents, biometrics, provider result payloads, or
  secret-bearing redirect URLs in consumer settlement records.
- Deploying real credentials or activating Mainnet in this track.
