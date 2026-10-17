# Payment delivery recovery

`FiatEventDispatcher` uses only the persisted, signature-verified event correlation.
Webhook delivery and recovery invoke the same idempotent handlers. Each handler
refetches provider authority before value effects. A different environment fails
before handler execution. Record references are normalized before GUID parsing.

`FiatEventRecoveryWorker` retries unfinished events every minute in bounded batches,
ordered by attempt count so old repeatedly pending events do not starve new work.
The existing outcome CAS preserves an applied event against stale failure writes.
Handlers remain responsible for effect idempotency across duplicate events and workers.
The operator replay route shares the hardened Operator policy and financial limiter.

The worker also reconciles pending operator refunds from their stored provider IDs.
It never chooses a new refund key after uncertain acceptance. API/refund contracts
are documented in `OnRamp/AGENTS.md`.

## §recovery-cap — the sweep stops replaying an event after 60 attempts (round 4)

`FiatEventRecovery.SweepAsync` used to replay every `Received`/`Failed` event every minute forever
(an `AcceptedPending` outcome is stored as `Received`). It now selects only rows with
`attempt_count < MaxAutomaticAttempts` (60 ≈ one hour of one-minute ticks, ample for chain
confirmation). Past the cap the row stays on the operator board (`ListNeedingOperatorAsync`,
statuses unchanged) and the operator replay route still applies it; nothing is marked applied or
dropped. The refund-reconciliation half of the sweep is unaffected.
