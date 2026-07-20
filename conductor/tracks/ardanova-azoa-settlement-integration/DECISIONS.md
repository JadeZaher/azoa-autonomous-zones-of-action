---
type: decision
track: ardanova-azoa-settlement-integration
---

# Decisions

## D1 - Stripe stays outside AZOA settlement primitives

ArdaNova owns Stripe payment collection and webhooks. AZOA exposes generic
settlement operations and receipts only. This contains PCI/payment-provider
scope and prevents a node API from becoming a tenant secret vault.

## D2 - Identomat is the first proposed live KYC adapter

Identomat publishes dashboard-issued API keys, hosted redirect/result APIs, and
broad document coverage. It preserves vendor separation from Stripe Payments.
Its published entry plan currently has a monthly minimum, so provider selection
remains deployment configuration and the adapter is not called universally
economical. It may not become available until the shared durable hosted-attempt
and external-outcome protocol is implemented and tested.

## D3 - Stripe Identity is deferred behind a separate boundary decision

Stripe Identity may be easier for eligible operators who already use Stripe,
but putting its credential in AZOA would narrow the established zero-Stripe-key
rule. This track does not approve that exception. If separately approved, it
must use a least-privilege Identity-only key and webhook secret that cannot
create payments and are never shared with ArdaNova Payments.

## D4 - Readiness is consumer policy; AZOA still authorizes independently

ArdaNova evaluates one participant-readiness decision for all of its value
actions. AZOA independently enforces provider-bound KYC, wallet, consent,
ownership, and capability rules at every settlement boundary. Either side may
deny; neither side's allow result overrides the other.

## D5 - Ambiguity is a state, never a retry instruction

Once an AZOA write might have reached the node, ArdaNova persists
`AwaitingReconciliation` and uses the same opaque receipt/idempotency binding.
Workers must never turn a timeout into a second allocation attempt.
