---
type: design
status: source-complete-pending-review
---

# Replacement NFT creation cost consent

An expired or failed-on-chain creation attempt advances the preparation generation
only after authoritative no-effect evidence. The existing immutable mint command
retains the original quote, so it cannot authorize the new generation. Replacement
must preserve that command and history while accepting a separate exact offer.

Add append-only lifecycle replacement-cost consent records, each containing the
exact reviewed lifecycle version/state/stage/generation request, immutable provider
quote, accepted independent limits, actual accepting actor authority and time.
Only the original actor and original tenant may accept. Their current credential
is separate from the original command authority; it cannot replace original grants
or revive a canceled/completed workflow. Both authorities are fenced together.

Quotation requires the exact current Preparing/Create generation, no prepared
wire, and the last recorded attempt's authoritative no-effect evidence. Construct
the original command's intent at this generation, remove its old cost receipt,
and ask the existing provider cost module for fresh physical-network, policy,
rent, levy and network-fee evidence. Persist the immutable five-minute offer with
its complete intent and original metadata draft hash. No custody or lifecycle
mutation occurs during quotation.

Acceptance checks exact row version and evidence, receipt identity, complete
intent, payer/wallet, original platform signer, and independent limits. Append
the consent and check database-time receipt expiry in one transaction with
original/current authority fences. Preserve command JSON and all prior receipts.
The intent factory selects only the receipt for the current Create generation;
preparation and first-send validate its recorded accepting authority. Receipt
expiry governs acceptance, not later exact-wire observation or receipt replay.

An exact saved request replays before mutable authority and price lookups. A
changed body cannot reuse its ID. A stale version with no matching record requires
review of the current state. The browser saves the disclosed quote and exact
acceptance before HTTP and retains them after a lost response. Fresh generations
must never inherit a prior generation's quote or cost caps implicitly.

Independent review added exact-version retirement for unanswered offers. Offer
expiry and a missing receipt do not prove that a suspended acceptance cannot
arrive. A separate immutable retirement audit advances the same lifecycle
version under current payer authority, competing atomically with acceptance.
Reconciliation returns an exact committed acceptance first, then retirement or
higher-version proof, otherwise unresolved. The UI may discard an old pending
acceptance only after applied/obsolete evidence; it must not infer retirement
from elapsed time. Backend histories, contract, service and endpoints are
authored; browser/SDK integration, fixtures and verification remain open.

Required fixtures: expiry before atomic acceptance, actor/tenant/wallet changes,
changed original platform signer, changed intent or quote limits, original and
current credential revocation, concurrent acceptance, cancellation/version race,
lost acceptance response, crash before custody, retained prior receipt/history,
new generation refusal without fresh consent, exact provider cost enforcement,
SDK bodies and browser saved-request recovery. This is implementation work, not
a deferral. Gates run with the final integrated sweep.

## Round-3 resume completion (lane N, 2026-10-16)

Source-complete, runtime NOT RUN. Review fixes: typed refusal for missing request/stage/costs
bodies; the browser no longer stalls when a saved acceptance was Applied for an earlier
generation (Applied or Obsolete now permits reviewing the later generation, with distinct
copy); SDK/root index exports for the stage, replacement and cost types the browser imports.
The in-memory store mirrors the durable append/retirement preconditions so service flows
exercise them. Fixtures: `NftReplacementCostTests` (refusal without consent, stale review,
widened limits, exact replay, rebound request ID, original command preserved, gen-1 intent
carries the replacement quote, retirement replay and fresh offer, gen-2 refusal, actor/tenant/
payer, crash before custody, rotated platform signer); `SurrealNftLifecycleStoreTests.ReplacementCosts`
(DB-time expiry, single receipt per generation, original revocation despite newer credential,
current expiry, payer wallet move, acceptance-vs-retirement and acceptance-vs-cancellation races,
first send blocked after accepting-authority expiry while observation continues); SDK exact
bodies; browser saved-request recovery. Generated schema for the new histories is final-sweep work.

