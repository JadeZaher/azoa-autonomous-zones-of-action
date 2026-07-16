---
type: plan
track: node-conformance-manifest
created: 2026-07-11
status: in_progress
---

# Plan: node-conformance-manifest

1. [x] Maintain the architecture ratchet that prevents a direct Holochain runtime,
   conductor API, or prohibited prior-art client dependency from entering AZOA.
2. [x] Define versioned descriptor, evidence, signature, and rotation contracts.
3. [x] Add a gate-result serializer backed by CI-produced G1/G2/G3/G5/G7 TRX
   artifacts; it computes artifact digests and never accepts operator pass/fail claims.
4. [x] Add isolated node-identity key custody plus canonical signing/verification.
5. [x] Expose the opt-in, bounded, credential-free well-known HTTPS endpoint.
6. [x] Ship an offline verifier and canonical golden-vector test.
7. [x] Run focused tamper, expiry, version, rotation, restore, payload-limit, and
   evidence-failure tests.
8. [ ] Run the real CI gate commands to generate and mount immutable TRX artifacts,
   add artifact freshness/provenance enforcement, independently consume a deployed
   endpoint, and then verify every acceptance criterion before archival.

## Activation limits (2026-07-13)

The local half can sign and serve a bounded descriptor only when explicitly
enabled with persistent Data Protection plus node-identity storage and a
read-only mounted G-suite evidence directory. It does **not** certify a node,
publish to a DHT, install or supervise a Holochain conductor, discover peers,
resolve cross-node holons, transfer value, or activate federation policy. Those
capabilities remain governed by `federation-v2`'s independent multi-operator
gate. The present artifact source proves a TRX result's content/digest, not CI
provenance or freshness; that operational closure remains a required follow-up.
