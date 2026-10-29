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

## §intent-lost-cas — a lost intent transition is re-read, never assumed done

`FiatCheckoutIntentEventHandler` moves the funding intent with a state-version CAS
(`TryTransitionAsync`). A lost CAS used to be read as "another delivery already made this exact
transition" and answered `Applied` (or `AcceptedPending` for a park). That was only true while every
racer wrote the same target. With `SurrealTransientConflict` retry in the store, a genuine race now
surfaces as a CAS miss: a `checkout.session.completed` delivery that PARKS an async payment
(`AwaitingOutcome`) can beat `checkout.session.async_payment_succeeded` at the same version, and the
old answer marked the success applied while the row stayed `processing` forever.

`SettleLostTransitionAsync` re-reads the row after a null transition: the target state → the
success outcome; any terminal state → `Applied` (the single terminal write already happened); anything
else → `Failed`, so Stripe redelivers (and `FiatEventRecovery` re-sweeps) and the next attempt decides
on the new row. A payment event (`completed` / `async_payment_succeeded`) that finds the intent
`ABANDONED` — voided by its request key or expired — is logged at **Critical**, the level
`appsettings.Production.json` emits, because it means money may have landed under a key the node told
the client to release (`Providers/Fiat/AGENTS.md` §same-key-replay-codes). The settlement handler still
credits a confirmed payment; the log is what makes the contradiction visible.
