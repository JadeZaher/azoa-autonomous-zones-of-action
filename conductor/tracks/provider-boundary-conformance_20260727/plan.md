---
type: Implementation Plan
title: Provider boundary conformance plan
description: TDD-oriented delivery plan for a truthful, provider-owned multi-chain boundary.
tags: [provider-boundary-conformance_20260727, pending, launch-gate]
timestamp: 2026-07-27T00:00:00Z
resource: ./spec.md
---

# Implementation Plan: Provider boundary conformance

## Overview

Implement the [provider boundary specification](./spec.md) as a sequence of
contract-first refactors. Author the contract and regression tests before each
implementation group, but follow the repository policy of running the complete
build/lint/test sweep once in the final phase.

## Phase 0: Freeze the boundary and baseline

Goal: turn the current leaks and capability disagreements into executable
characterization gates before moving code.

Tasks:

- [ ] Task: Record the provider-family, network-profile, capability-module, and
  protocol-adapter decisions, including the Wormhole split and manifest
  versioning strategy.
- [ ] Task: Write architecture tests that inventory chain-name switches,
  chain constants, unit math, status parsing, address/signature code, and fixed
  response fields outside approved provider-owned directories.
- [ ] Task: Write contract tests that capture current .NET/SDK capability
  disagreement and the production rule that no incomplete bridge may advertise
  support.
- [ ] Task: Reconcile provider documentation so `PROVIDERS.md`, directory
  `AGENTS.md`, node-host guidance, and Railway deployment posture describe the
  same enabled/disabled capabilities.
- [ ] Verification: Review the allowlist and baseline findings; no existing leak
  may disappear from the worklist without an explicit disposition.
  [checkpoint marker]

Exit gate: provider ownership, capability truth, and protocol/provider splits
are approved; every known leak is covered by a test or migration task.

## Phase 1: Fail closed at the security boundary

Goal: remove callable false cryptography and synthetic value success before
making the provider surface easier to extend.

Tasks:

- [ ] Task: Write public-route tests proving unsupported Ethereum wallet
  generation returns a typed failure, then remove the non-Ethereum
  HMAC/SHA-256/mnemonic implementation from the reachable path.
- [ ] Task: Write configuration tests that reject raw private-key inputs and
  enabled signing capabilities without a registered custody-backed signer, then
  enforce the typed configuration boundary.
- [ ] Task: Add provider-contract tests that reject fabricated transaction IDs
  and successful bridge/asset effects without real submission evidence, then
  make every incomplete Solana/Algorand path fail closed.
- [ ] Task: Add secret-redaction tests for startup errors, manifests, health,
  logs, and normalized provider failures.
- [ ] Verification: Independent security review of the custody/signing and
  fail-closed changes. [checkpoint marker]

Exit gate: no public path exposes false Ethereum material, raw key
configuration, synthetic value success, or secret-bearing diagnostics.

## Phase 2: Extract provider-owned capabilities

Goal: make common layers chain-neutral while preserving existing behavior.

Tasks:

- [ ] Task: Write interface-level tests for required provider identity/query
  behavior and optional key, asset, codec, proof, bridge, atomic-group, and
  faucet capabilities; then introduce the typed capability interfaces.
- [ ] Task: Write network-isolation and registration tests, then adapt the
  factory to publish immutable provider/network registrations and capability
  handles without chain switches.
- [ ] Task: Write golden tests around amount normalization, address parsing,
  signature verification, and transaction status; then move their chain-specific
  implementations from shared helpers/signing services into providers.
- [ ] Task: Write bridge/DEX configuration validation tests, then move
  chain-specific Wormhole, Tinyman, and Jupiter identifiers and semantics into
  provider-owned modules while preserving protocol-generic orchestration.
- [ ] Task: Replace fixed-chain controller and response shapes with normalized
  typed provider/network records and prove unknown profiles remain representable.
- [ ] Verification: Run the chain-logic architecture scan and review every
  remaining allowlisted common-layer exception. [checkpoint marker]

Exit gate: adding a disabled network profile in an existing family requires no
shared settlement, custody, controller, or response-model chain branch.

## Phase 3: Publish one truthful capability contract

Goal: make server, SDK, frontend, and health consume the same provider truth.

Tasks:

- [ ] Task: Write snapshot/compatibility tests for the versioned secret-free
  provider manifest, then expose it from the factory/API.
- [ ] Task: Write SDK drift tests against the manifest fixture, then remove
  hard-coded bridge claims and fixed provider capability declarations.
- [ ] Task: Write frontend component tests for arbitrary provider/network
  records, then render the API registry instead of a fixed chain list.
- [ ] Task: Write health tests for unobserved, disabled, degraded, and ready
  providers, then make health conservative until real observation exists.
- [ ] Task: Add startup tests for enabled-without-registration,
  registration-with-invalid-config, duplicate canonical keys, and network
  retargeting; then enforce all four failures.
- [ ] Verification: Compare API output, checked-in fixture, SDK projection, and
  frontend rendering for exact capability parity. [checkpoint marker]

Exit gate: one versioned manifest is authoritative and any server/SDK/frontend
capability drift fails CI.

## Phase 4: Migrate reference provider families

Goal: prove the boundary with distinct chain models without duplicating the
value-route backlog.

Tasks:

- [ ] Task: Move Algorand identity, units, address/signature, transaction,
  finality, ASA, and bridge-chain metadata behind its provider-owned modules and
  pass the common plus Algorand conformance fixtures.
- [ ] Task: Move Solana identity, units, address/signature, wire-transaction,
  finality, and SPL metadata behind its modules; keep every non-broadcast value
  path unsupported and align the SDK projection.
- [ ] Task: Add a disabled EVM-family provider and tests for chain-ID/network
  profiles, EIP-55 addresses, secp256k1/Keccak primitives, integer units, and
  EIP-1559 transaction coding without enabling a bridge route.
- [ ] Task: Add disabled profiles for Ethereum, Base, Arbitrum, Optimism,
  Polygon, Avalanche C-Chain, and BNB Chain using provider-owned validation.
- [ ] Task: Verify `chain-value-routes` remains the owner of real Solana,
  Wormhole, and EVM value activation and link its tasks to the new capabilities.
- [ ] Verification: Independent architecture review of Algorand, Solana, and
  EVM-family migrations. [checkpoint marker]

Exit gate: three materially different provider families fit the same boundary;
all incomplete routes remain truthfully disabled.

## Phase 5: Productize provider authoring

Goal: make safe provider expansion repeatable.

Tasks:

- [ ] Task: Add the repository-local
  [add-azoa-chain-provider](../../../.agents/skills/add-azoa-chain-provider/SKILL.md)
  skill with a family-versus-profile decision, required chain facts,
  scaffolding workflow, and fail-closed review checklist.
- [ ] Task: Add skill templates for provider modules, registration, disabled
  configuration, capability fixture, SDK contract test, golden vectors, frontend
  registry proof, and directory-level `AGENTS.md`.
- [ ] Task: Forward-test the skill with one disabled EVM network profile and one
  distinct-family dry run; correct any step that requires undocumented manual
  edits outside provider-owned surfaces.
- [ ] Task: Add the skill’s leakage, capability-parity, synthetic-success,
  integer-unit, and private-key-config scans to its final verification guidance.
- [ ] Verification: Reviewer executes the skill from its `SKILL.md` without
  hidden context and confirms both scenarios stop before unsupported enablement.
  [checkpoint marker]

Exit gate: the repository-local skill can add a disabled profile/family
consistently and cannot claim bridge support without conformance evidence.

## Phase 6: Integrated verification and closeout

Goal: prove the refactor and authoring workflow at one exact commit.

Tasks:

- [ ] Task: Run the complete .NET build, unit, live-Surreal integration,
  provider conformance, SDK test/build, frontend audit/lint/typecheck/build,
  Railway template, and documentation-drift sweep once.
- [ ] Task: Run devnet/testnet drills only for capabilities intended to be
  enabled; record transaction/finality evidence without secrets.
- [ ] Task: Run an independent security and architecture review, remediate all
  P0/P1 findings, and repeat the integrated sweep only after the full remediation
  set is applied.
- [ ] Task: Update provider documentation, capability snapshots, and the
  deployment checklist with the exact verified commit and evidence locations.
- [ ] Verification: Approve only with zero failing gates, zero unexplained
  capability drift, and no incomplete provider advertised as ready.
  [checkpoint marker]

Exit gate: all local and CI gates are green at one commit, evidence is reviewed,
and production still fails closed for every route not separately activated.
