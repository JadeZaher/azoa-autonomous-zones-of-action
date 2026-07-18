---
type: spec
track: node-conformance-manifest
created: 2026-07-11
status: in_progress
horizon: alpha
repository: azoa
activation_gate: >-
  Federation-v2 L0 local-half exception only. This track may generate, sign,
  verify, and HTTPS-serve a node descriptor and machine-readable conformance
  manifest. It MUST NOT add a Holochain runtime/client, DHT publication, peers,
  federation rules, or cross-node resolution.
depends_on:
  - final-hardening-cutover
related:
  - federation-v2
  - node-operator-governance
  - commons-dotnet-packages
---

# Track: node-conformance-manifest

## Goal

Turn real G1/G2/G3/G5/G7 gate results into a canonical, independently
verifiable node descriptor and signed conformance manifest without activating
cross-node federation.

## Scope

- Serialize real test evidence into a stable, versioned manifest.
- Sign with a dedicated node-identity key that is independent of custody and
  chain-wallet keys.
- Expose the descriptor at `/.well-known/azoa-node.json` over HTTPS.
- Provide an offline verifier for canonical bytes, signature, freshness, and
  referenced evidence digests.
- Document node-key rotation and previous-key continuity.

## Non-goals

- Holochain conductor, hApp, DHT, peers, or discovery.
- Cross-node holon resolution or federation governance.
- Publishing private SurrealDB state, PII, custody, or value authority.

## Acceptance criteria

1. The manifest is generated from actual gate output rather than operator-entered claims.
2. Canonical serialization and domain-separated signatures have golden vectors.
3. Verification fails closed on tamper, expiry, unsupported versions, and key discontinuity.
4. The well-known endpoint serves only public descriptor/conformance facts with bounded payloads.
5. Key generation, encrypted storage, rotation, backup, and recovery have a tested runbook.
6. No Holochain/runtime/client/DHT dependency enters this repository under this track.
7. Unit, integration, schema, and external verifier consumer tests pass before archival.
