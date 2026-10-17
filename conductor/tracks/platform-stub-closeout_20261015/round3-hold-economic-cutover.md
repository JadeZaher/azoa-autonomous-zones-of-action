---
type: contract-amendment
status: accepted-for-implementation
owner: lane-B
---

# Hold economic-source cutover

Root independently accepted this mapping on 2026-10-15. The delegated participant
authority remains separate from the actual tenant principal: exact API key/scopes,
participant watermark, owned wallet and selected UserExplicit swap:sign grant are
fenced at commitment, preparation and first egress. Observation and final accounting
do not require fresh authority. Revocation of either side racing first egress must
be covered by authored fixtures. No runtime acceptance is implied.

The currently live exchange path opens a hold before persisting its blockchain
operation, requests automatic release without a cost mandate, and compensates via
a human hold identifier. Reversal provenance also uses heuristic hold matching.
Those calls cannot be translated into an unrestricted platform signing witness.
The durable Hold service rejects EconomicSource until its exact transactional
authority is implemented. This document maps that required B2 work; it does not
defer it or claim the legacy path is repaired.

## Exchange source before commitment

Persist a dedicated ExchangeHoldSource before any Hold/reserve commitment. Its
deterministic identity binds API-key identity, original actor/tenant, original
request key, exact canonical request, selected physical network/protocol, source
and target wallets, platform/project assets and quantities, reserve partition,
beneficiary, treasury reference and exact hold definition. Capture the validated
credential and selected current exchange signing grants from the controller;
historical contexts without that witness cannot create fresh commitments.

The source carries exact quoted release mechanics, beneficiary wallet and explicit
automatic-release consent when requested. Preview discloses these terms without
creating a liability. A new request must acknowledge that immutable disclosure;
an automatic release boolean cannot substitute for accepted category limits.
Absent consent parks a discoverable original-key source, before funds movement.

The Hold opening transaction locks this source at its version and stage, checks
its saved current credential/grants and actual reserve ownership, then creates
the hold, reserve draw, opening receipt and source commitment reference atomically.
It must reuse HoldLocalCommandPlan and scoped typed writers. The source's hold ID
is the network-qualified record ID; a caller's human label is display only.
An automatic mandate is created with the same opening transaction but cannot
become executable until exact exchange chain settlement is final. A failed or
ambiguous exchange cannot release the held units merely because time elapsed.

Source settlement states distinguish pre-egress, authorized wire, final success,
and authoritative no-effect. Existing exchange submission must retain its exact
signed group and first-egress authorization before transport; a post-submit-only
BlockchainOperation row is insufficient recovery evidence. Reuse existing
Algorand group construction, canonical envelope/signature validation and native
preparation journal rather than adding another signer. Final observations bind
all original group legs, amounts, assets, parties and genesis. Unknown history
never releases commitments or permits fresh generation.

## Source-authorized compensation

Exchange compensation consumes the exact source record and immutable final
no-effect evidence. Its refund commits the original Held liability cancellation,
partition counters, Hold receipt and source compensation receipt together. The
reconciler uses this receipt, rather than a separate best-effort RefundAsync or
an idempotency flag written after the effect. Current tenant expiry cannot erase
already owed no-effect accounting; no additional signature is authorized.

Fiat reversal recovery uses an immutable purchase-allocation identity tying the
original on-ramp lot to the completed exchange source and exact Hold record.
No time-window/beneficiary-only matching establishes authority. The recovery
transaction locks the actual reversal/provider evidence, lot allocation and
unconsumed attribution amount, and the exact still-Held liability. It records
the consumed attribution with refund accounting in the same commit, preventing
two refund/dispute generations or workers from recovering the same purchase twice.
Partial reversals must consume only their allocated units: refunding the full
hold while crediting a partial amount is not acceptable. Split the original
liability into a refunded portion and a remaining liability, preserving original
owner, beneficiary, mandate and source lineage, or refuse before mutation until
the exact split plan is available. This split is required implementation.

## Public and worker cutover

Legacy Hold routes become adapters to the durable command API with mandatory
original request keys, explicit network, real captured authority and participant
quote/consent. Responses expose actual pending or completed commands. Legacy
hold worker registration is removed in favor of the restricted reconciliation
worker and exact-mandate worker; neither guesses authorization from AutoRelease.
Old rows lacking source evidence remain visible with evidence-required recovery,
never silently replayed. Existing SDK methods migrate to these contracts rather
than keeping a parallel unsafe effect path.

## Acceptance evidence

Author fixtures for source admission crash before commitment, commitment crash
before submission, identical wire replay after response loss, final no-effect
compensation, unresolved submission preserving held units, hold mandate blocked
until exact exchange settlement, wrong source/tenant/network refusal, conflicting
original keys, concurrent compensation and reversal, partial attribution without
whole-hold over-recovery, and already-completed receipt replay after credential
revocation. No runtime gate has run; independent contract review precedes this
economic-source implementation.
