---
type: Track Spec
title: Provider boundary conformance
description: Make the blockchain provider boundary the sole owner of chain-specific logic, constants, capabilities, and validation.
tags: [feature, provider-boundary-conformance_20260727, pending, alpha, launch-gate]
timestamp: 2026-07-27T00:00:00Z
resource: ./metadata.json
---

# Provider boundary conformance

## Overview

Make “the provider owns all chain-specific logic and constants” an enforceable
architecture rule. A new chain family or network profile must be addable without
editing shared settlement, custody, controller, response-model, or frontend
switches. Capability claims remain fail-closed until real chain evidence proves
them.

This track establishes the provider contract, conformance suite, dynamic
capability manifest, and repository-local provider-authoring skill. It does not
turn incomplete value routes on. Route implementations remain in
[`chain-value-routes`](../chain-value-routes/spec.md), while the generic
settlement contract remains in
[`settlement-primitives-dotnet-sdk`](../settlement-primitives-dotnet-sdk/spec.md).

## Background

The provider factory and `BaseBlockchainProvider` already give AZOA a useful
network-isolated, fail-closed foundation. Chain knowledge nevertheless leaks
into shared code today:

- chain decimals, bootstrap identities, transaction-status keys, and fixed
  chain catalogs live in shared helpers and response models;
- wallet key generation and address derivation branch by chain outside the
  provider boundary;
- Wormhole, Tinyman, and Jupiter chain constants/configuration are split across
  orchestration and DEX services;
- the API and frontend expose a fixed Algorand/Solana/Ethereum shape instead of
  provider registrations;
- the TypeScript SDK advertises bridge capabilities that the .NET providers
  reject; and
- the current Ethereum wallet-generation path is not valid Ethereum
  cryptography and must not remain callable.

AZOA’s production deployment contract currently states that no provider has a
complete reviewed lock/mint/burn/release and production-custody lifecycle.
Conformance must make that statement mechanically true or false per registered
provider.

## Functional requirements

### FR-1 — Provider-owned chain model (P0)

Each chain family owns its chain identifiers, network profiles, native units,
address and signature formats, transaction codec, finality model, status
normalization, asset rules, and chain-specific bridge identifiers.

Acceptance criteria:

1. Shared code selects a provider by an opaque canonical provider/network key;
   it does not branch on `Algorand`, `Solana`, `Ethereum`, or future chain names.
2. Network endpoints, credentials, and deployment-specific vault/contract
   addresses remain environment inputs, but the provider owns their typed
   schema, validation, and semantic interpretation.
3. All amounts cross public boundaries as integer base units (`BigInteger`,
   `bigint`, or decimal strings); shared code performs no chain-decimal math.
4. Provider instances remain immutable after factory binding to one canonical
   network profile.

### FR-2 — Capability-oriented provider contract (P0)

Replace the broad implicit provider surface with a small required provider
identity/query contract and explicit optional capabilities. The initial
capability set must cover key/address handling, fungible assets, transaction
coding, wallet proof, bridge chain operations, atomic groups, and faucets.

Acceptance criteria:

1. Unsupported capabilities are absent or return a typed unsupported result;
   they never fabricate success.
2. Bridge support is reported only when lock, mint, burn, release, proof/finality
   observation, custody, and ambiguous-outcome recovery all pass conformance.
3. Protocol-wide verification such as Wormhole guardian quorum may remain in a
   single hardened protocol service, while every chain-specific emitter,
   sequence, address, transaction, and finality rule stays inside the provider.
4. The provider factory exposes a secret-free manifest of registrations and
   capabilities for API, health, SDK, and frontend consumers.

### FR-3 — Security and custody boundary (P0)

Acceptance criteria:

1. The invalid public Ethereum key/address/mnemonic generation path fails closed
   before any architectural migration proceeds.
2. Provider configuration contains no raw private-key field. Signing material is
   resolved only through the audited custody seam and zeroed according to its
   contract.
3. No value operation may return a synthetic transaction identifier or success.
4. Golden vectors prove address derivation, signature verification, and
   serialized transaction bytes for every enabled signing capability.
5. Logs, manifests, health payloads, and errors contain no key material,
   credentials, raw signed transaction, or deployment-secret value.

### FR-4 — Registration and configuration parity (P0)

Acceptance criteria:

1. Enabling a configured provider without a matching registration fails startup.
2. Registering a provider with invalid or incomplete enabled-network
   configuration fails startup.
3. Disabled provider profiles are honest, queryable, and cannot reach value
   execution.
4. A provider/network profile is instantiated once per canonical factory key;
   concurrent resolution cannot retarget another cached instance.

### FR-5 — API, SDK, and frontend capability parity (P0)

Acceptance criteria:

1. The API returns normalized provider/network/capability records rather than a
   fixed field per known chain.
2. The TypeScript SDK consumes or contract-checks the same versioned capability
   fixture and cannot claim `supportsBridging=true` when the node reports false.
3. The frontend renders the returned provider registry; adding a network profile
   does not require a new frontend chain switch.
4. Contract-drift tests fail CI when server, SDK, and checked-in capability
   fixtures disagree.

### FR-6 — Provider conformance harness (P0)

Acceptance criteria:

1. A common suite checks identity, configuration, disabled behavior,
   network isolation, capability honesty, normalized errors, cancellation, and
   secret-free observability for every registration.
2. Capability-specific suites add golden vectors and devnet/testnet round trips
   without requiring unsupported providers to implement unrelated behavior.
3. Bridge suites prove real transaction IDs, observation/finality, replay,
   reconcile-before-retry, and zero duplicate effects before a route can enable.
4. A production provider with no successful observation remains degraded or
   unknown, never optimistically healthy.

### FR-7 — Initial provider-family migration (P1)

Acceptance criteria:

1. Algorand becomes the reference fully-described provider family; its partial
   bridge capability remains false until its lifecycle evidence is complete.
2. Solana metadata and SPL paths either broadcast real wire transactions or fail
   closed; the SDK capability projection matches the provider.
3. Ethereum is represented by a generic EVM-family provider with network
   profiles, not one copy per EVM chain. Initial profiles target Ethereum, Base,
   Arbitrum, Optimism, Polygon, Avalanche C-Chain, and BNB Chain, all disabled
   until their required credentials and capability evidence exist.
4. Later distinct families—Stellar, Cosmos/CosmWasm, and XRP Ledger—can be added
   through the same boundary without changing shared settlement contracts.

### FR-8 — Repository-local authoring skill (P1)

The repository ships an
[`add-azoa-chain-provider`](../../../.agents/skills/add-azoa-chain-provider/SKILL.md)
skill that determines whether the request is a new family or a network profile
and scaffolds the provider, SDK fixture, registration, disabled configuration,
tests, documentation, and frontend registry consumption.

Acceptance criteria:

1. The skill requires chain/genesis identifiers, units, address/signature
   schemes, finality/reorg rules, RPC/indexer inputs, signer representation, and
   bridge identifiers before generating enabled behavior.
2. It rejects synthetic success, floating-point units, raw private-key config,
   SDK/server capability mismatches, and bridge claims without conformance.
3. Its verification scans shared layers for newly leaked chain constants and
   runs the repository’s integrated final gate once after implementation work.

## Non-functional requirements

- **Security:** all unknown, incomplete, ambiguous, or misconfigured capability
  states fail closed.
- **Extensibility:** a new profile in an existing family changes provider-owned
  code/configuration and fixtures only.
- **Compatibility:** public manifest changes are versioned and additive unless a
  reviewed breaking-version change is intentional.
- **Observability:** health and operation telemetry identify provider family and
  network without exposing endpoints containing credentials or custody facts.
- **Performance:** manifest reads are bounded and cached; provider resolution
  preserves the existing one-instance-per-network isolation guarantee.

## User stories

### Provider author

As a provider author, I want one family/profile workflow so that adding an EVM
network does not require editing unrelated controllers or settlement code.

Given a new disabled Base profile, when its configuration and fixture are added,
then the API and SDK expose it without claiming unsupported value capabilities.

### Node operator

As a node operator, I want startup to reject mismatched registration and
configuration so that a typo cannot silently route value to the wrong network.

Given an enabled profile with missing custody or vault configuration, when the
host starts, then startup fails with a secret-free actionable error.

### Settlement consumer

As a settlement consumer, I want truthful normalized capabilities so that I do
not offer a value action the selected node cannot execute safely.

Given the server reports Solana bridging unavailable, when the SDK loads node
capabilities, then it also reports bridging unavailable.

## Technical considerations

- Preserve `BlockchainProviderFactory` network-instance isolation and the
  existing custody chokepoint.
- Prefer typed records and discriminated capability results over
  `Dictionary<string, object>` status payloads.
- Keep protocol-generic bridge orchestration separate from provider-owned chain
  implementations; do not reintroduce an always-true provider proof verifier.
- Architecture tests should use allowlisted provider directories rather than
  brittle whole-repository string bans.
- Directory-level `AGENTS.md` files hold design rationale; source comments stay
  terse.

## Out of scope

- Federation, Holochain, DHT publication, peers, and cross-node resolution.
- Stripe, KYC-provider, or ArdaNova product-economics implementation.
- Turning every listed chain on or claiming mainnet readiness.
- Reimplementing route work already owned by `chain-value-routes`.
- Moving chain-specific signing into shared settlement or controller code.

## Open questions to resolve in Phase 0

1. Whether the versioned capability manifest is generated at build time from
   registrations or emitted from a runtime registry with a checked-in snapshot.
2. The exact split between protocol-wide Wormhole verification and each
   provider’s chain-specific Wormhole adapter.
3. Which EVM profiles belong in the first disabled catalog versus a later
   operator-supplied profile package.
