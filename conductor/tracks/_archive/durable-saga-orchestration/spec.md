# Durable Saga Orchestration — Specification

## Status
Decision record + track. Direction approved 2026-05-17 (architecture review of
the `api-safety-hardening` bridge flow). Tier 1 (architecture). **Build path:
ADR + reusable skeleton first, then bridge adoption, then generalize.**

## Goal
A **generic, reusable, hardened durable-saga / transactional-outbox / async-step
primitive** that any flow needing async retries, scheduled work, or
compensating revocations can consume. The cross-chain bridge is **consumer #1**
and the proving ground — but the module is NOT bridge-specific. It must "at
least solve the bridge", and be reusable for other queue/async needs (faucet
batching, future webhook/event delivery, NFT mint pipelines, quest step
dispatch, etc.) without redesign.

## Why (decision rationale)
The `api-safety-hardening` flow is, in disguise, a synchronous state machine +
polling reconciliation:
- `IIdempotencyStore` (insert-wins) + `ConsumedVaaRecord` ledger = effectively-once primitives
- `ExecuteUpdateAsync … WHERE Status==expected` + assert-1-row = atomic state transitions
- `ReconciliationService` (`IHostedService`) = a re-derive-from-chain convergence loop

These primitives are correct and **architecture-independent** — the saga layer
*consumes* them, it does not replace them. What is missing for "async retries +
revocations at scale": explicit step orchestration, durable scheduling with
backoff/dead-letter, and **compensation as a first-class concept** (today
`ReverseBridgeAsync` is ad hoc).

### Chosen: homebake DB-backed durable saga + transactional outbox
Rejected alternatives and why:
- **RabbitMQ / a message broker now** — a broker is a *transport*, not a saga
  engine; you would still build/borrow orchestration on top. It adds an
  ops-critical stateful external dependency *pre-launch*, reintroduces the
  dual-write problem (broker vs DB), and is **redundant with the committed
  endgame**: SurrealDB (the chosen sole engine, `surrealdb-migration`) ships
  native LIVE queries / change feeds — that *is* the eventing substrate a saga
  dispatcher wants. A broker would be ripped out at migration.
- **Off-the-shelf MassTransit/NServiceBus sagas** — mature, but couples the
  domain to a broker + a heavy framework programming model right before the
  SurrealDB migration that natively supplies the eventing. Contradicts the
  homebake / minimize-external-dependencies stance.

The broker stays a clean, **localized future swap** (one dispatcher adapter)
if a real cross-service decoupling or throughput requirement ever emerges.

## Architecture
- **Transactional outbox.** A saga/step record is written in the *same DB
  transaction* as the state transition it results from → no dual-write, no
  broker needed for atomicity. (`UPDATE status … ` and "enqueue next step"
  commit together or not at all.)
- **Durable saga state machine.** Explicit states, allowed transitions,
  per-step retry policy (max attempts, exponential backoff, jitter),
  dead-letter, and a declared **compensation step** per forward step.
- **Step processor (dispatcher).** Generalize `ReconciliationService` /
  `ReconciliationHostedService` into a step processor: claim a *due* step
  (reuse the existing conditional-UPDATE + idempotency-key claim primitive —
  exactly-once executor), run its handler, advance or schedule
  compensation/retry. **Trigger is pluggable**: polling now → SurrealDB LIVE
  queries at `surrealdb-migration` (zero domain-logic change, the processor
  only ever asks the store "what steps are due?") → external broker only if
  ever justified.
- **Idempotent handlers.** Every step handler keys its irreversible effect on
  the existing `IIdempotencyStore` / consumed-ledger primitives. Saga = the
  orchestration; idempotency spine = the exactly-once guarantee. Eventual
  consistency + compensation is the correct model precisely because the chain
  is already the source of truth.

## Non-negotiable design requirements ("perfectly hardened and extensible")
- **Reusable, not bridge-coupled.** Generic `ISaga`/`ISagaStep`/
  `IStepHandler<T>` + a typed payload; the bridge is one registered saga
  definition. Adding a new saga = register a definition + handlers, no core
  changes.
- **Exactly-once preserved.** No saga step performs an irreversible effect
  twice under duplicate/concurrent/retry/crash — enforced via the existing
  idempotency primitives, not reinvented.
- **Crash-safe / recoverable.** Process death at any point leaves the saga
  resumable from the persisted step; no orphaned in-flight effect (mirrors the
  reconciliation guarantee; in fact subsumes it).
- **First-class compensation.** Revocation/refund is a declared compensating
  step with its own idempotency key, backoff, and dead-letter — replaces
  ad-hoc `ReverseBridgeAsync`.
- **Observable.** Per-saga/step status, attempt counts, next-run-at,
  dead-letter queue queryable; metrics/OpenTelemetry hooks (aligns with
  `architecture-decoupling` observability).
- **Engine-portable.** Lives behind the persistence seam
  (`architecture-decoupling`); the durable store is EF/Postgres now, SurrealDB
  later — the saga domain logic is storage-agnostic. Trigger swap
  (poll → LIVE query) must not touch saga/handler code.
- **Pre-launch safety unchanged.** Migrating the bridge onto the saga must keep
  every `api-safety-hardening` invariant green (exactly-once, replay-reject,
  reconciliation) — the saga is an additive orchestration layer, the safety
  tests are the regression gate.

## Acceptance
- A generic durable-saga module exists, with the bridge initiate/redeem/reverse
  flow re-expressed as a saga definition, all `api-safety-hardening` safety
  tests still green (exactly-once / replay / reconciliation).
- Async retries with backoff + dead-letter demonstrably handle a failing step
  without blocking the request path; compensation runs as a first-class step.
- A *second* (non-bridge) trivial consumer is wired in a test to prove reuse
  with zero core changes.
- Trigger abstraction proven swappable (polling impl now; an interface seam a
  SurrealDB LIVE-query impl can drop into) — documented, not necessarily built
  here.
- No new external infrastructure introduced.

## Dependencies
Requires `api-safety-hardening` (consumes its idempotency/reconciliation
primitives). Aligns with `architecture-decoupling` (persistence seam +
observability). **Converges into `surrealdb-migration`**: the polling trigger
is replaced by SurrealDB LIVE queries there; reconciliation (G7) becomes a saga
concern. Carries no broker dependency forward.
