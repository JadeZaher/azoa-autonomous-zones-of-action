---
type: implementation-checkpoint
status: source-review-pending
owner: lane-B
---

# Exchange source commitment slice

Authored immutable ExchangeHoldSourceIntent, deterministic source identity and
ExchangeHoldSource POCO, plus IExchangeHoldSourceStore implemented by the existing
SurrealHoldCommandStore. Admission is atomic with real tenant API-key and separate
participant authority. Source consumption, original Hold receipt, liability and
reserve counters share the existing typed transaction. Completed receipt replay
does not require renewed authority. Generic local commands cannot manufacture an
economic source. New source liabilities are AwaitingSourceSettlement and cannot
take ordinary Held transitions.

The real-Surreal fixtures cover no liability at admission, concurrent source
commitment, either authority revoked before commitment, unchanged receipt replay
after revocation, changed key intent/beneficiary rejection and generic source
forgery refusal. No tests, builds or schema generation have run.

This slice is unregistered. Remaining required work, in order: immutable release
disclosure/mandate composition; exact unsigned and signed group journal before
custody/egress; authoritative complete group settlement or no-effect evidence;
source settlement and compensation; exchange caller/reconciler cutover; exact
purchase-lot attribution with partial reversal consumption; legacy Hold route,
worker and SDK retirement; provider parity and participant recovery clients.
These are implementation tasks, not external deferrals.
# Prepared wire and confirmed projection checkpoint

The separate provider slice is authored in AlgorandProvider.Exchange,
ExchangeValidation and ExchangeObservation, with AlgorandExchangeWireTests.
The authorized shared receipt rename was independently reread by Lane C with no
body or grammar changes. The new module is deliberately not advertised yet;
coordinator/caller cutover and independent review remain required.

SurrealHoldCommandStore.ExchangeWire now journals exact preparation and first
egress under both original authorities. The stored maximum network fee is an
explicit original-source field; absence does not silently grant a default cap.
ExchangeSettlement atomically records the permanent final receipt, activates the
original awaiting-source hold, and credits its exact treasury once. Current
credential revocation and treasury closure cannot erase confirmed accounting;
the closed treasury remains closed. New real-store fixtures cover revocation
before egress, original wire/evidence binding and concurrent projection.

Remaining implementation order: proven-no-effect compensation; exact source
mandate opening; coordinator and original exchange API/caller cutover; partial
reversal lot lineage; quartet/legacy worker activation; provider parity and
complete caller disclosure/recovery integration. These are required work, not
external deferrals. All runtime gates remain unrun.

The next authored increment adds ExchangeAbsence: exact original no-effect proof,
liability cancellation and reserve return share the permanent receipt transaction.
Concurrent completion/compensation fixtures preserve counters and prevent cross-
verdict replay. ExchangeHoldSourceService restricts reconciliation to saved
Submitted wire or final projection; it never claims a new liability or prepares
an unsent source. Separate fresh continuation compares the actual captured
original API-key witness before joining the existing source fences. This slice
is unregistered pending review and caller cutover; no activation claim is made.
