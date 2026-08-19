---
type: spec
track: ardanova-financial-workflow-conformance
created: 2026-07-18
status: in_progress
horizon: alpha
depends_on:
  - integration-test-isolation-debt
related:
  - node-operator-governance
  - node-conformance-manifest
---

# Track: ArdaNova financial workflow conformance

## Goal

Prove the versioned, generic consumer-to-node contract that ArdaNova will use
for identity readiness, wallet readiness, KYC, and exactly-once financial
outcomes without exposing AZOA internals or enabling real value in tests.

ArdaNova is the first named conformance fixture, not a domain dependency in
AZOA production code. Economic policy, project tokens, task awards, checkout,
and payment truth remain in the consumer domain.

## Contract decision prerequisite

Two current documents disagree about the selected ArdaNova identity path:

- `conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md` locks self-register,
  self-run avatars and explicitly excludes `tenant:provision` for ArdaNova.
- `docs/TENANT-CUSTODIAL-ONBOARDING.md` describes the new generic tenant
  custodial path and uses ArdaNova as its example consumer.

Before an ArdaNova fixture is treated as launch evidence, record one explicit
decision: retain the self-sovereign path and test tenant custody only as a
generic alternate mode, or deliberately amend the integration contract to use
managed tenant custody. Do not let an alias field or example curl command make
that product decision implicitly.

## Acceptance criteria

1. A versioned matrix names the authoritative owner, endpoint, auth mechanism,
   idempotency scope, response state, retry rule, and secret boundary for every
   step in account bootstrap, KYC, wallet readiness, allocation, reward, and
   reconciliation.
2. Real middleware tests prove an operator can issue only the fixed tenant key
   scope, while an API key cannot self-issue, widen, or rotate its own authority.
3. The selected account path proves deterministic identity and create-only
   wallet convergence under exact replay and concurrent retry. Divergent reuse
   of an idempotency key fails closed, and another tenant or actor receives the
   same not-found result as a missing subject.
4. Capability and account projections contain no private key, seed, encrypted
   custody, KYC document reference, provider payload, reviewer identity, raw
   idempotency key, or internal exception detail.
5. KYC session/submission replay is durable and tenant/actor scoped. Manual or
   unavailable providers never satisfy a production value gate.
6. Allocation and task-reward attempts fail before any broadcast until the
   selected node reports a trusted current approval. Exact replay returns the
   original operation, and ambiguous submission enters reconciliation without
   a second settlement attempt.
7. The public SDK is contract-tested against the live in-process HTTP/Surreal
   host, including status/error/replay shapes; mocked-fetch tests alone are not
   sufficient.
8. Local build, unit, full live-Surreal integration, SDK, and CI gates are green
   at one commit before the track can archive.

## Activation limits

This track uses simulated/development-only custody and chain behavior. It does
not provision production KMS/HSM custody, a live KYC vendor, payment-provider
credentials, real task awards, real token allocation, or federation. Those
remain independently fail-closed and require their own operator evidence.

## Non-goals

- Moving consumer economics or payment truth into AZOA.
- Adding ArdaNova-branded production types, routes, configuration, or storage.
- Treating a browser redirect, submitted operation, or pending reconciliation
  as payment or settlement confirmation.
