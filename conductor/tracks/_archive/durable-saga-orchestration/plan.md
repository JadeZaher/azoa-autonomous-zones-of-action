# Durable Saga Orchestration — Plan

Build order: **skeleton first (this track), bridge adoption, then generalize.**
Each phase keeps the full `api-safety-hardening` suite (537+ unit tests,
exactly-once / replay / reconciliation) green — that suite is the regression
gate for every step.

## Phase 1 — Reusable skeleton (no behavior change to the bridge yet)
1. [ ] Core abstractions: `ISaga`/`ISagaDefinition`, `ISagaStep`, `IStepHandler<TPayload>`, `CompensationStep`, retry policy (max attempts, exponential backoff + jitter), `SagaStatus`/`StepStatus` enums — generic, zero bridge coupling
2. [ ] `OutboxMessage`/`SagaStepRecord` EF entity + migration; written in the SAME transaction as the producing state change (transactional outbox); unique/idempotency-key column reusing the `api-safety-hardening` spine; dead-letter fields
3. [ ] `ISagaStore` behind the persistence seam (claim-due-step = conditional `UPDATE … WHERE status=Pending AND next_run_at<=now`, assert one row — reuse the exactly-once claim primitive); EF impl now, SurrealDB-portable contract
4. [ ] `ISagaTrigger` seam with a polling `HostedService` impl (generalize `ReconciliationHostedService`); the dispatcher only ever asks the store "what steps are due?" so a SurrealDB LIVE-query impl can drop in later with no domain change
5. [ ] Step processor: claim → run handler (handler keys irreversible effects on `IIdempotencyStore`) → advance / schedule retry (backoff) / route to compensation / dead-letter; crash-safe + idempotent re-entry
6. [ ] Skeleton tests: concurrent claim → one executor; crash mid-step → resumes; retry/backoff; dead-letter; compensation invoked; a trivial **non-bridge** sample saga proving reuse with zero core changes

## Phase 2 — Bridge adoption (consumer #1)
7. [ ] Express bridge initiate/redeem/reverse as a saga definition over the existing `CrossChainBridgeService` logic (lock → await VAA → redeem → complete; compensation = reverse/refund as a first-class step with its own idempotency key)
8. [ ] Make the on-chain steps async saga steps (request path returns fast; retries happen off-thread) without weakening any invariant
9. [ ] Replace ad-hoc `ReverseBridgeAsync` with the declared compensation step
10. [ ] Reconciliation (`api-safety-hardening` G7) re-expressed as / folded into the saga recovery path (orphaned-InProgress settlement becomes saga resume)
11. [ ] All `api-safety-hardening` safety tests green against the saga-driven bridge (exactly-once, replayed-VAA, reconciliation, faucet) — regression gate

## Phase 3 — Generalize & harden
12. [ ] Second real consumer (e.g. faucet dispense batching or webhook/event delivery) on the saga module — proves reuse in anger
13. [ ] Observability: per-saga/step status, attempts, next-run, dead-letter queryable; OpenTelemetry hooks (aligns with `architecture-decoupling`)
14. [ ] Operator surface: requeue / cancel / inspect dead-letter; documented in the ops runbook
15. [ ] Load/chaos: failing-step storm, processor crash under load, compensation under concurrency — exactly-once holds

## Convergence (handed to surrealdb-migration)
16. [ ] `ISagaTrigger` SurrealDB LIVE-query implementation replaces polling (no saga/handler code change) — tracked as a `surrealdb-migration` task
17. [ ] `ISagaStore` SurrealDB adapter behind the seam; outbox/saga tables `SCHEMAFULL` (G6); conditional transitions preserved (G2); reconciliation/saga-resume green (G7)

## Verification
18. [ ] `dotnet build` — zero new warnings
19. [ ] Full unit suite green incl. all `api-safety-hardening` safety tests + new saga tests
20. [ ] Decision record (spec.md) kept current; no external infra (no broker) introduced
