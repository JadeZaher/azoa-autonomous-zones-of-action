---
type: implementation-plan
status: open
---

# Open follow-up: hermetic Stripe settlement through verified issuance

This existing capability/setup blocker is separate from the scoped node-admin
change. The candidate's latest E2E collection is 4/5: tenant verification, claim,
fresh consent, checkout and hosted payment succeed; the signed webhook returns 500
because no verified Devnet platform unit exists. The positive delivery, intent
and settlement assertions remain unchanged. No prior passing Stripe journey is
established. Independent source/history review confirms the relevant simulated
provider gaps and settlement gates already exist in upstream/main.

## Design decision required before implementation

Choose one network-free authority boundary: legitimate simulated durable
fungible-creation and recipient-preparation capabilities, or an isolated chain
protocol edge exercised by a real adapter. Current E2E fakes cover Identomat and
Stripe only. The current simulated provider implements neither required module;
genesis alone cannot repair settlement. Changing to an actual Devnet provider
would permit real broadcasts and is not authorized by this follow-up.

Review the whole creation/quote/accept/continue/confirmation contract, stable
protocol identity, custody/address compatibility and recovery semantics before
authoring. Preserve finalized issuance authority and reserve registration; no
active platform-token seed or fabricated proof lineage substitutes for them.

## One collected setup and acceptance batch

- Implement and meaningfully verify the chosen network-free capability boundary,
  including exact prepared-byte submission, authoritative observations and
  durable creation accounting. Preserve recipient-owned acceptance and reserve
  funding authority; no owner-echo or always-success mocks.
- Exercise operator HTTP genesis and its original operation's explicit quote,
  acceptance, continuation and refresh routes. Prove a Completed creation and
  its exact issuance authority/reserve projection before accepting platform
  state. Supply native fee/minimum balance explicitly; polling is not a faucet.
- Declare sufficient `on-ramp` reserve budget through the operator partition
  route, and register a fresh exact tenant USD price for the verified asset.
  Platform state supply is derived from reserve-ledger accounting and declared
  total; later actual funding/readiness/transfer confirmation are separate facts.
- Preserve real tenant KYC/AML, same-user claim and fresh first-party consent.
  Also satisfy the node-direct KYC/AML context selected by platform allocation.
  Separately review whether the domain intends both contexts; do not substitute
  a tenant signing context to bypass the node value gate.
- Verify wallet custody, recipient preparation, reserve CAS, real transfer
  confirmation and durable payment-lot accounting. Default zero node fees need
  no invented schedule; changed fees must retain valid gross/net accounting.
  This flow does not require unrelated DEX liquidity or fiat payout float.
- Author all fixes and setup first, obtain independent review, then run one
  collected hermetic suite with the positive fake-vendor and three real-vendor
  negative controls. Require unchanged 200/202 signed-delivery ownership,
  complete intent and honest payment_received/funds_applied state, with no
  duplicate issuance on retry.

Likely scope is the chosen provider/transport boundary and owning docs, meaningful
unit/real-database lifecycle tests, then E2E support/setup/journey docs. It is not
a three-file fixture-only patch. No work is marked implemented or runtime-proven
by this record. Keep the current known red and wider integration backlog visible;
this follow-up provides no payment-readiness, merge/push or deployment approval.
