---
type: Implementation Plan
title: AZOA production readiness for ArdaNova plan
description: Hard-gated plan for green CI, production custody/KYC, one chain route, contract evidence, and Railway promotion.
tags: [ardanova-azoa-production-readiness_20260727, pending, production, launch-gate]
timestamp: 2026-07-27T00:00:00Z
resource: ./spec.md
---

# Implementation Plan: AZOA production readiness for ArdaNova

## Overview

Execute the [production-readiness specification](./spec.md) as an evidence
track. Owning tracks implement generic settlement, consumer integration, and
provider routes; this plan validates and composes their immutable outputs into
one production decision. Tests are authored before implementation groups, while
the complete build/lint/test sweep runs once after all planned fixes.

## Phase 0: Lock decisions and the release graph

Goal: remove ambiguous ownership and select the concrete production inputs.

Tasks:

- [ ] Task: Reaffirm the shared/managed-node, self-register, self-run ArdaNova
  launch model from the canonical integration contract or record one deliberate
  superseding decision with compatibility and migration impact.
- [ ] Task: Update tenant-custodial documentation to identify it as a generic
  alternate mode and align the ArdaNova conformance fixture, SDK sample, scopes,
  and operator language with the selected model.
- [ ] Task: Select and record the first production chain route (Algorand by
  default), its network, operator-funding thresholds, and account-owner roles
  without recording account secrets.
- [ ] Task: Select the KMS/HSM or external-signer backend and the first hosted
  KYC provider (Identomat by default), including their least-privilege and
  rotation owners.
- [ ] Task: Resolve the exact Railway project, environment, SurrealDB, schema,
  API, and frontend service IDs in the protected deployment environment.
- [ ] Task: Publish a dependency/evidence matrix linking every readiness claim
  to its owning track, artifact, reviewer, and hard exit gate.
- [ ] Verification: Architecture, security, and product reviewers approve the
  launch model and ownership matrix before implementation begins.
  [checkpoint marker]

Exit gate: identity/custody/KYC/chain/deployment decisions are explicit, and no
readiness requirement is duplicated across tracks.

## Phase 1: Restore one green repository baseline

Goal: make local and protected CI release gates agree at one commit before
production-specific activation work.

Tasks:

- [ ] Task: Add regression fixtures for the reported frontend advisories, then
  upgrade or replace vulnerable dependencies until the release audit threshold
  is zero without forced or out-of-range blind upgrades.
- [ ] Task: Ratchet the .NET warning baseline, fix provider/nullability warnings
  touched by the launch path, and make new warnings fail CI.
- [ ] Task: Restore the pinned SurrealDB integration environment and add a
  preflight that distinguishes unavailable infrastructure from passing tests;
  unavailable integration infrastructure must fail the release gate.
- [ ] Task: Add exact-SHA CI assertions for release build, unit, schema,
  full integration, conformance, SDK, frontend, fiat sidecar, Railway template,
  migration, and pruning jobs.
- [ ] Task: Add a release-gate test proving a newer local result or stale public
  CI run cannot authorize promotion.
- [ ] Verification: Review the complete planned fix set and the single
  integrated-sweep command without running partial full-suite loops.
  [checkpoint marker]

Exit gate: all known baseline failures have implemented fixes, integration
infrastructure is available, and protected CI is ready to evaluate one commit.

## Phase 2: Converge contract dependencies

Goal: consume completed generic artifacts without restating their
implementation plans.

Tasks:

- [ ] Task: Import the versioned endpoint/auth/idempotency/state/secret matrix
  and live-host evidence from `ardanova-financial-workflow-conformance`; reject
  missing or contradictory identity assumptions.
- [ ] Task: Consume the exact packed contracts/client artifacts and clean-
  consumer proof from `settlement-primitives-dotnet-sdk`; pin package version and
  protocol fixture digest.
- [ ] Task: Execute the AZOA half of
  `ardanova-azoa-settlement-integration` through the packed client against the
  in-process live-Surreal host and store only immutable evidence links here.
- [ ] Task: Add a boundary scan proving no ArdaNova type, Stripe credential,
  payment event, valuation rule, or project economics entered AZOA runtime or
  public SDK packages.
- [ ] Verification: Independent contract review confirms the three owning
  tracks agree on identity, auth, receipt binding, ambiguity, and secret
  ownership. [checkpoint marker]

Exit gate: conformance, SDK, and consumer-fixture artifacts are version-aligned,
live-host proven, and boundary-clean.

## Phase 3: Production custody and KYC evidence

Goal: implement production custody and consume the hosted-KYC implementation
owned by `ardanova-azoa-settlement-integration`.

Tasks:

- [ ] Task: Write custody contract tests for create/sign/rotate/revoke/recover,
  least privilege, key non-export, zeroing, outage, and audit, then implement the
  selected KMS/HSM or external-signer adapter behind the custody seam.
- [ ] Task: Write startup tests that reject config-derived production signing
  keys, incomplete custody configuration, mixed identities, and Mainnet use
  without the selected custody capability.
- [ ] Task: Consume and version-pin the hosted-KYC adapter, lifecycle tests, and
  production/Mainnet startup-rejection evidence from
  `ardanova-azoa-settlement-integration`; do not duplicate its admission,
  callback, result-fetch, deduplication, or CAS implementation here.
- [ ] Task: Add an evidence check that the pinned KYC adapter commit, policy
  fixture, configured provider, and operator readiness projection agree for the
  promoted environment.
- [ ] Task: Add secret-free operator readiness, rotation, provider-outage,
  reconciliation, and emergency-disable procedures and test their authorization
  boundaries.
- [ ] Verification: Security reviewer validates the custody implementation and
  the pinned KYC threat-model/test evidence, logs, configuration, and operator
  procedures with no open P0/P1 finding. [checkpoint marker]

Exit gate: the production node can be deployed with live KYC and production
custody while all real-value routes remain disabled.

## Phase 4: Qualify the first chain route

Goal: produce truthful testnet evidence for one route; keep every other route
off.

Tasks:

- [ ] Task: Consume the provider manifest, conformance harness, and selected
  provider output from `provider-boundary-conformance_20260727`.
- [ ] Task: Consume only the selected route’s completed implementation and
  evidence from `chain-value-routes`; do not activate unfinished Solana,
  Wormhole, EVM, or other profiles.
- [ ] Task: Write replay/concurrency/timeout/delayed-finality cases before the
  drill and instrument the selected route to record secret-free lock, mint,
  burn, release, observation, and reconciliation evidence.
- [ ] Task: Fund the designated testnet fee/vault accounts and run forward and
  reverse lifecycle drills using real transaction identifiers.
- [ ] Task: Compare API, .NET SDK, TypeScript SDK, frontend, and health
  capability projections for the selected and disabled routes.
- [ ] Verification: Provider and security reviewers verify chain truth,
  finality, custody, supply/vault invariants, and zero duplicate effects.
  [checkpoint marker]

Exit gate: one selected provider is testnet-proven end to end; every unqualified
provider is individually disabled and reported unavailable.

## Phase 5: Prove the ArdaNova financial contract

Goal: prove exactly-once consumer settlement through AZOA before production
value activation.

Tasks:

- [ ] Task: Run the existing simulated Stripe/KYC transport matrix through the
  packed .NET SDK and live AZOA/Surreal host, including duplicate event IDs,
  multiple events per PaymentIntent, denied readiness, and cross-owner reads.
- [ ] Task: Inject timeout-after-submit and indeterminate-chain outcomes and
  prove the consumer persists the original idempotency/receipt binding,
  transitions to `AwaitingReconciliation`, and never resubmits.
- [ ] Task: Run the same generic allocation/receipt/reconciliation contract on
  the selected provider’s testnet with test funds and verify terminal projection
  only from chain-confirmed evidence.
- [ ] Task: Scan artifacts, API payloads, logs, and audit records for Stripe/KYC
  credentials, provider payloads, signing material, raw idempotency keys, and
  internal exceptions.
- [ ] Verification: Cross-repository reviewer confirms one payment fact maps to
  at most one AZOA value effect and neither system bypasses the other’s
  authorization. [checkpoint marker]

Exit gate: the complete contract is exactly-once and fail-closed in simulation
and on the selected provider’s testnet.

## Phase 6: Integrated gate and immutable promotion

Goal: create and deploy one release from one exact green commit.

Tasks:

- [ ] Task: Run the complete .NET build, unit, schema, full live-Surreal
  integration, conformance, SDK, frontend audit/lint/typecheck/build, fiat
  sidecar, container, migration, Railway-template, and pruning sweep once.
- [ ] Task: Remediate all failures as one set, then repeat the integrated sweep
  only after that complete remediation set; do not promote a partially green
  result.
- [ ] Task: Require protected CI success at the exact commit and build
  digest-pinned API/frontend images with SBOM, provenance, and attestations.
- [ ] Task: Assemble and independently verify the secret-free readiness bundle:
  source/CI/package/image digests, migration and capability manifests, testnet
  references, reviewer approvals, and intended Railway target IDs.
- [ ] Task: Promote serially through SurrealDB health, schema terminal success,
  API health, and frontend health using explicit IDs and immutable image
  references.
- [ ] Task: Run post-deploy auth, KYC/provider readiness, receipt observation,
  restart persistence, health redaction, backup/restore, rollback, key rotation,
  outage, and reconciliation-backlog drills.
- [ ] Verification: Release reviewer matches the running image and deployment
  IDs to the green evidence bundle and approves the production-node milestone.
  [checkpoint marker]

Exit gate: the production-node milestone is green on Railway at the exact
reviewed commit; no real-value route is implicitly enabled by deployment.

## Phase 7: Controlled financial activation

Goal: enable only the selected reviewed route and preserve an immediate
fail-closed rollback.

Tasks:

- [ ] Task: Verify production custody, live KYC, provider conformance,
  reconciliation, account funding, operator authorization, and capability
  manifest gates all return ready for the same environment.
- [ ] Task: Materialize the selected route’s production configuration and
  operator-funded addresses through protected secrets without printing or
  committing values.
- [ ] Task: Enable real value only for the selected route, redeploy through the
  serial promotion path, and run a bounded canary plus receipt reconciliation.
- [ ] Task: Prove the emergency disable returns the route to fail-closed without
  losing pending receipts or reconciliation evidence.
- [ ] Task: Publish the final milestone result and remaining disabled-chain
  backlog; never report “all chains ready” from one route’s evidence.
- [ ] Verification: Independent security, provider, operations, and release
  reviewers approve financial activation with zero open P0/P1 findings.
  [checkpoint marker]

Exit gate: AZOA is financially launch-ready for ArdaNova on exactly the selected
provider/network, and all other provider and federation capabilities remain
truthfully out of scope or disabled.
