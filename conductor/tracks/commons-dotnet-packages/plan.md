---
type: plan
track: commons-dotnet-packages
created: 2026-07-11
status: deferred
---

# Plan: commons-dotnet-packages

## Pre-gate work

1. Reserve package IDs and create the separate `AZOA.Commons` repository.
2. Establish shared deterministic-pack, Source Link, API-baseline, SBOM, signing,
   and protected-tag release policy.
3. Adopt and independently review the normative
   [`holochain-dotnet-bridge-contract`](../federation-v2/holochain-dotnet-bridge-contract.md),
   including canonical publication/attestation and error vectors.
4. Land Contracts and node-conformance golden vectors without a DHT dependency.

## Post-gate work

1. Pin one conductor/DNA pair and prove App/Admin MessagePack vectors.
2. Build bounded internal Holochain transport behind the typed Commons.Client facade.
3. Add optional Runtime supervision without making it transitive.
4. Run compatibility, reconnect, backpressure, ambiguity, and chaos suites.
5. Pack once, validate a clean local-feed consumer, sign and attest the same bits,
   publish a preview, verify NuGet consumption, then promote to stable.
