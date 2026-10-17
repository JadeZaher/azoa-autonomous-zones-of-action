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
