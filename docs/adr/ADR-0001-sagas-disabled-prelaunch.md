# ADR-0001: Sagas disabled by default pre-launch

- **Status:** Accepted
- **Date:** 2026-05-18
- **Deciders:** Engineering (api-safety-hardening §4 pre-launch cleanup)
- **Related:** `GO-TO-PROD.md` §4 item 3 + hard gate #7; `conductor/tracks/durable-saga-orchestration/spec.md`

> First ADR in this repository. Architecture decisions had previously been
> recorded as prose "Decision record" sections inside
> `conductor/tracks/<track>/spec.md`. GO-TO-PROD §4 item 3 explicitly calls for
> an ADR for this decision, so `docs/adr/` with the canonical
> context/decision/consequences structure is introduced here and numbered from
> `0001`.

## Context

The durable-saga / transactional-outbox module
(`durable-saga-orchestration` Phase 1) shipped as a **reusable skeleton**:
`SagaStepRecord` (the outbox row), `EfSagaStore`, `SagaProcessor`,
`PollingSagaTrigger`, and `SagaProcessorHostedService` (a singleton
`BackgroundService`), plus the `AddSagaOutbox` EF migration.

It is deliberately **generic** — no bridge (or any domain) coupling — and is
proven via its own unit suite. But pre-launch it has **zero registered
consumer**: nothing enqueues saga steps. The cross-chain bridge still uses the
synchronous api-safety-hardening primitives (`IIdempotencyStore` insert-wins,
`ConsumedVaaRecord` ledger, conditional `ExecuteUpdateAsync … WHERE
Status==expected` + assert-one-row, and the `ReconciliationService` convergence
loop). `ReverseBridgeAsync` is still the ad-hoc compensation path.

`SagaOptions.Enabled` previously defaulted to `true`. With DI registered in
`Program.cs`, that meant a **consumerless hosted polling loop** would run inside
the production financial graph at launch: a recurring scan that can never find
work, an extra moving part in the most safety-critical surface, and an
operational signal (`SagaSteps` dead-letter, lease reclaim) with no producer to
explain it. That is pure pre-launch risk for zero pre-launch value, and it
contradicts the no-overengineering intent the architecture review affirmed.

## Decision

**`Sagas:Enabled` defaults to `false`, and prod ships it explicitly `false`.**

1. `SagaOptions.Enabled` default flipped `true → false` (fail-closed; the XML
   doc states it stays off until durable-saga Phase 2 ships a consumer).
2. `appsettings.json` carries an explicit, audit-visible top-level
   `"Sagas": { "Enabled": false }` (next to `"Reconciliation"`), satisfying
   GO-TO-PROD hard gate #7 ("`Sagas:Enabled=false` — config audit").
3. `SagaProcessorHostedService.ExecuteAsync` already early-returns and logs
   when `!Enabled`, so the hosted singleton self-disables: no tick ever runs,
   the scoped saga services are simply never resolved.
4. **The saga code, DI registration in `Program.cs`, and the `AddSagaOutbox`
   migration stay on-branch unchanged.** This is a config gate, not a deletion.
   The hosted service is harmless while disabled; the migration is an empty,
   inert schema addition (greenfield DB starts empty and is migrated cleanly);
   keeping code + ADR + migration together is the agreed pass-off posture and
   lets Phase 2 adopt it with no archaeology.

The const-string vs enum / xmin-removal / `BridgeStatus.Reversing` changes in
the same §4 cleanup are unrelated and recorded with their own code/tests.

## Consequences

**Positive**

- The pre-launch financial graph runs only the audited, consumer-backed safety
  spine. No consumerless background loop, no spurious saga ops signals.
- GO-TO-PROD gate #7 is satisfiable by a one-line config audit.
- Fail-closed: any environment that forgets to configure `Sagas` still gets the
  safe (disabled) behavior from the new default.
- Zero churn for Phase 2 — code, DI, and schema are intact and ready.

**Negative / accepted**

- The saga module ships dormant. Accepted: it is a tested skeleton whose
  activation is explicitly a later track's responsibility.
- A reader could mistake "registered DI + migration" for "active." Mitigated by
  this ADR, the `SagaOptions.Enabled` XML doc, the explicit appsettings entry,
  and the hosted service's startup log line.

## How to re-enable (durable-saga Phase 2)

Phase 2 (`durable-saga-orchestration` track) owns adoption. To turn it on:

1. Ship at least one real saga consumer (e.g. the bridge migrated onto saga
   steps) and satisfy the Phase-2 hard gate in GO-TO-PROD §4: `EfSagaStore`
   must implement true transactional-outbox semantics (explicit
   `IDbContextTransaction` around multi-write ops) **or** the docs must stop
   claiming "transactional outbox / same transaction."
2. Set `Sagas:Enabled = true` in the target environment's appsettings/secret
   config (and flip the `SagaOptions.Enabled` default only once a consumer is
   the normal case).
3. Re-run `scripts/passoff.ps1`; update GO-TO-PROD gate #7 and the
   RESIDUAL-RISK-RUNBOOK monitoring for `SagaSteps` dead-letter.
