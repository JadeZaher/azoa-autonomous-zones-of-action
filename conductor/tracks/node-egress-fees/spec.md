---
type: spec
track: node-egress-fees
created: 2026-07-11
status: pending
horizon: post-alpha-federation-gated
depends_on:
  - node-operator-governance
  - node-public-governance-transparency
  - federation-v2
related:
  - node-conformance-manifest
  - data-engine-decision
---

# Track: optional node egress fees

## Goal

Allow a sovereign node to charge an optional, advertised fee for explicit
AZOA-controlled data export or federated-resolution payloads while peers remain
free to decline. This feature is deferred until a real consumer and durable
on-chain settlement boundary exist.

## Rules

- Disabled by default. Holochain DHT gossip/replication, ordinary API errors,
  authentication, health, conformance, transparency, fee quoting, and payment
  verification are never metered.
- Pricing is dimensionally explicit: flat base units plus base units per KiB, a
  free-byte allowance, and an optional maximum. It is not overloaded onto value
  BPS schedules.
- A quote pins canonical payload hash, exact byte length, schedule version,
  settlement asset, chain/network, peer/recipient, and treasury destination
  version before paid bytes leave the node.
- Payment uses durable `node_fee_settlement` state with atomic or independently
  reconcilable effects. Replay returns one receipt; ambiguous payment never
  double-charges; unconfirmed settlement never releases the paid payload.
- Peer protocols advertise price and exemptions before collaboration.

## Acceptance criteria

1. Quotes are byte-deterministic, version-pinned, recipient-bound, idempotent,
   and cannot be replayed for a different payload or peer.
2. Transparency remains reachable when every egress price is nonzero.
3. Provider network instances are isolated and durable settlement/crash recovery
   tests prove exactly-once charging.
4. At least one real export or peer-resolution consumer passes end-to-end tests
   before the feature can be enabled.
5. Holochain gossip and DHT replication remain outside the chargeable surface.
