---
type: Track Spec
title: AZOA production readiness for ArdaNova
description: Produce reviewed AZOA-side evidence for a real ArdaNova financial launch without duplicating consumer or settlement tracks.
tags: [feature, ardanova-azoa-production-readiness_20260727, pending, production, launch-gate]
timestamp: 2026-07-27T00:00:00Z
resource: ./metadata.json
---

# AZOA production readiness for ArdaNova

## Overview

Take AZOA from a deployable fail-closed node shell to a reviewed production node
that ArdaNova can consume for real financial settlement. This is an AZOA-side
release-evidence track: it converges existing contract tracks, production
custody/KYC, one selected chain route, CI/supply-chain evidence, and an ordered
Railway promotion at one exact commit.

It does not re-specify work already owned by:

- [`ardanova-financial-workflow-conformance`](../ardanova-financial-workflow-conformance/spec.md)
  for the generic identity/wallet/KYC/allocation/reconciliation contract;
- [`settlement-primitives-dotnet-sdk`](../settlement-primitives-dotnet-sdk/spec.md)
  for the generic .NET contracts and client;
- [`ardanova-azoa-settlement-integration`](../ardanova-azoa-settlement-integration/spec.md)
  for ArdaNova’s Stripe, readiness, and consumer reconciliation behavior; or
- [`provider-boundary-conformance`](../provider-boundary-conformance_20260727/spec.md)
  and [`chain-value-routes`](../chain-value-routes/spec.md) for provider
  architecture and chain-route implementation.

## Background

AZOA’s Railway graph, schema job, runtime database isolation, digest-pinned
images, health gates, and fail-closed real-value switch are strong foundations.
The node cannot yet claim financial production readiness because:

- the latest public release evidence is not green at the current commit;
- the frontend dependency audit has high-severity findings;
- full live-Surreal integration evidence is not available at the assessed
  commit;
- production KYC remains unavailable scaffolding and custody is not KMS/HSM
  backed;
- no provider currently exposes a complete reviewed bridge lifecycle; and
- the canonical self-sovereign ArdaNova contract conflicts with later
  tenant-custodial example documentation.

Stripe Payments remains entirely in ArdaNova. AZOA receives only a generic,
already-authorized settlement request through the .NET SDK and never receives
Stripe credentials, raw events, valuation, or product economics.

## Launch definition

This track is green only when both milestones are proven:

1. **Production node milestone:** the AZOA control/data plane, live hosted KYC,
   production custody, CI, artifacts, and Railway deployment are green with
   real-value routes still disabled.
2. **Financial activation milestone:** one explicitly named chain/provider has
   complete conformance and testnet evidence, operator funding/custody is ready,
   ArdaNova’s generic contract fixture reconciles exactly once, and the
   real-value switch is enabled only for that reviewed route.

A healthy fail-closed deployment may be reported as deployed, but not as
financially launch-ready.

## Functional requirements

### FR-1 — Canonical integration model (P0)

Acceptance criteria:

1. The self-register, self-run avatar model and shared/managed AZOA node decision
   in `conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md` is either reaffirmed or
   deliberately superseded by one versioned decision.
2. Conflicting tenant-custodial examples are relabeled as a generic alternate
   mode and cannot silently redefine ArdaNova’s launch path.
3. The selected model is reflected in the conformance matrix, SDK sample,
   credentials/scopes, operator runbook, and ArdaNova contract fixture.
4. No ArdaNova-branded production type, endpoint, provider, or persistence model
   is introduced into AZOA.

### FR-2 — Dependency convergence without duplication (P0)

Acceptance criteria:

1. The conformance track publishes the authoritative endpoint/auth/idempotency/
   state/secret matrix and passes its live-host tests.
2. The .NET SDK track produces deterministic packages, a clean consumer, and
   ownership-safe receipt/reconciliation behavior.
3. The ArdaNova settlement integration track consumes those exact versioned
   artifacts in its simulated Stripe-to-receipt contract proof.
4. This track records immutable links to the dependency evidence rather than
   copying their requirements or test matrices.

### FR-3 — Green repository release gate (P0)

Acceptance criteria:

1. Release build succeeds with zero errors and no new warnings beyond an
   explicitly approved, ratcheted baseline.
2. Unit, schema, full live-Surreal integration, conformance, SDK, and contract
   suites pass at one commit.
3. Frontend dependency audit reports zero known vulnerabilities at the release
   threshold; lint, typecheck, and production build pass.
4. Fiat sidecar build, Railway template validation, container builds, migration
   checks, and changed-file pruning pass.
5. Protected CI is green for the exact promoted commit; a newer local build
   cannot substitute for repository CI evidence.

### FR-4 — Production custody and signer readiness (P0)

Acceptance criteria:

1. Production wallet encryption/signing uses a reviewed KMS/HSM-backed custody
   implementation or an equivalently reviewed external signer; a config-derived
   data key cannot authorize real value.
2. Key creation, rotation, revocation, recovery, backup/restore, least privilege,
   audit, and incident procedures have test and operator evidence.
3. API/runtime/schema identities and key-management authority remain separated;
   no secret appears in images, logs, health, conformance artifacts, or frontend.
4. The selected chain provider receives signing material only through the
   provider-owned custody capability.

### FR-5 — Live provider-neutral KYC (P0)

Implementation ownership remains with the existing
`ardanova-azoa-settlement-integration` track. This readiness track version-pins
and verifies that adapter's evidence; it does not create a second KYC lifecycle.

Acceptance criteria:

1. One hosted production KYC adapter is selected and implemented behind the
   existing provider-neutral lifecycle; Identomat remains the current proposed
   reference unless an explicit decision changes it.
2. Callback/webhook authentication, authoritative server-side result fetch,
   deduplication, CAS terminal outcome, expiry, trust/policy revision, and
   minimal persistence pass integration and security tests.
3. Manual, mock, unavailable, and administrative override paths fail startup in
   Production and always fail on Mainnet.
4. The operator readiness surface reports missing key names without exposing
   their values and cannot mark an incomplete adapter ready.

### FR-6 — One honest production chain route (P0)

Acceptance criteria:

1. The first route is named in a decision; Algorand is the default candidate
   because it has the most complete existing transaction path.
2. The route passes provider-boundary conformance for address/signature,
   transaction serialization, lock/mint/burn/release, observation/finality,
   custody, idempotency, ambiguity, and reconcile-before-retry.
3. Devnet/testnet round trips produce real transaction identifiers and prove no
   duplicate value effect under replay, timeout, concurrent retry, or delayed
   observation.
4. SDK/API/frontend capability projections agree before the route can enable.
5. Every other route remains individually disabled and honestly reported.

### FR-7 — ArdaNova contract proof (P0)

Acceptance criteria:

1. A live AZOA/Surreal host exercises account/wallet/KYC readiness, one
   allocation, receipt observation, ambiguity, reconciliation, replay, and
   cross-owner not-found behavior through the packed .NET SDK.
2. A simulated Stripe transport owned by the ArdaNova fixture proves duplicate
   events and multiple events for one PaymentIntent converge on one AZOA write.
3. No test treats Stripe success as KYC approval, readiness as AZOA
   authorization, pending submission as settlement, or timeout as retry
   permission.
4. The financial activation proof is repeated on the selected provider’s
   testnet with test funds before any Mainnet configuration is accepted.

### FR-8 — Reproducible promotion and operations (P0)

Acceptance criteria:

1. Promoted API/frontend images are immutable digest references with SBOM,
   provenance, and attestations tied to the exact green CI commit.
2. Railway promotion follows SurrealDB health → schema terminal success → API
   health → frontend health, using explicit project/environment/service IDs.
3. Post-deploy smoke checks prove auth, provider/KYC readiness, receipt
   observation, restart persistence, and secret-free health on the exact
   environment.
4. Backup/restore, rollback, key rotation, provider outage, reconciliation
   backlog, and incident-disable drills have reviewed evidence.
5. Real value cannot enable unless production custody, KYC, selected provider,
   reconciliation, and operator-funding gates all agree.

### FR-9 — Release evidence bundle (P0)

Acceptance criteria:

1. A machine-readable bundle records commit, CI run, package/image digests,
   migrations, capability manifest, provider/network, testnet transaction
   references, Railway deployment IDs, health timestamps, and reviewer
   approvals.
2. The bundle is secret-free, read-only in the promoted image, and independently
   verifiable.
3. Any failed, missing, stale, or mismatched gate makes the overall readiness
   result `NotReady`; partial success is reported by milestone, never flattened
   into green.

## Non-functional requirements

- **Security:** custody, KYC, provider, and deployment reviews must have no open
  P0/P1 findings before financial activation.
- **Reliability:** ambiguous chain or transport outcomes reconcile; they never
  trigger blind resubmission.
- **Auditability:** every green claim links to exact immutable evidence.
- **Privacy:** raw KYC evidence, provider payloads, Stripe data, signing material,
  and private endpoints do not enter conformance artifacts.
- **Operability:** disable, rollback, recovery, and reconciliation procedures are
  executable by a node operator without source changes.

## User stories

### Node operator

As a node operator, I want one fail-closed readiness result so that I cannot
enable real value with incomplete custody, KYC, provider, or deployment gates.

Given KYC is ready but custody is not KMS/HSM backed, when readiness is
evaluated, then the node reports financial activation `NotReady`.

### ArdaNova service

As an ArdaNova service, I want a stable generic SDK contract so that Stripe
events can settle exactly once without learning AZOA internals.

Given the AZOA write times out after possible submission, when ArdaNova receives
the SDK result, then it persists `AwaitingReconciliation` and never resubmits.

### Release reviewer

As a release reviewer, I want immutable evidence for one exact commit so that a
healthy local build or stale deployment cannot be mistaken for production proof.

Given CI and deployment reference different image digests, when the evidence
bundle is verified, then readiness is `NotReady`.

## Technical considerations

- Use the existing Railway serial rollout and conformance-image model; extend
  evidence rather than creating another deployment path.
- Keep the release gate aggregate and fail-closed, but preserve component-level
  reasons for operator remediation.
- Test funds and account addresses are deployment inputs; they do not replace
  missing chain primitives or conformance evidence.
- Align readiness terms with the typed settlement and provider manifests rather
  than adding product-specific booleans.

## Out of scope

- Federation, Holochain, DHT publication, peers, node discovery, or cross-node
  resolution. Existing future federation tracks remain untouched.
- ArdaNova’s product domain, Stripe implementation, valuation, or task economics.
- Implementing generic SDK/provider tasks already specified in their owning
  tracks.
- Enabling every blockchain family or accepting Mainnet secrets in source.
- Declaring legal/compliance sufficiency for a jurisdiction.

## Open questions to resolve in Phase 0

1. Confirm Algorand as the first production financial route or record another
   provider with equivalent conformance maturity.
2. Select the production KMS/HSM/external-signer implementation and operator.
3. Confirm Identomat as the first hosted KYC provider or approve another adapter
   under the same lifecycle.
4. Resolve the exact Railway project/environment/service IDs and operator
   funding thresholds without writing their secret values into this bundle.
