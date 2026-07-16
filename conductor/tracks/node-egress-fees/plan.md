---
type: plan
track: node-egress-fees
created: 2026-07-11
status: pending
---

# Plan: optional node egress fees

1. Specify and persist a separate byte-dimensional egress price schedule.
2. Add deterministic quote/receipt contracts and SDK/provider parity.
3. Implement paid delivery only after `node_fee_settlement`, atomic/reconcilable
   payment, provider network isolation, and crash-window tests exist.
4. Advertise pricing/exemptions in the peer protocol without metering Holochain
   DHT traffic or public accountability endpoints.
5. Exercise one real export or peer-resolution consumer before enabling fees.
