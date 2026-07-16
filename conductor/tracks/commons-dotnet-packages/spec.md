---
type: spec
track: commons-dotnet-packages
created: 2026-07-11
status: deferred
horizon: v2
repository: AZOA.Commons
activation_gate: >-
  Package design, ID reservation, contract-only work, and release tooling may
  proceed. DHT-bound client/runtime implementation MUST NOT start until
  federation-v2 has at least three independent production operators and one
  concrete cross-node use case with a real counterparty.
depends_on:
  - federation-v2
  - node-conformance-manifest
supersedes:
  - dotnet-client-sdk
---

# Track: commons-dotnet-packages

## Goal

Publish a pure-managed, version-gated .NET package family for the Commons
protocol and Holochain sidecar boundary without pulling conductor lifecycle or
native binaries into ordinary AZOA-node consumers.

## Package boundaries

| Package | TFM | Responsibility |
|---|---|---|
| `Azoa.Commons.Contracts` | `netstandard2.0` | Dependency-light public protocol records, versions, results, and errors. |
| `Azoa.Commons.Client` | `net8.0` | Typed Commons facade with internal App WebSocket/MessagePack transport, request correlation, signals, cancellation, and bounded reconnect/backpressure. This is the normal AZOA-node reference. |
| `Azoa.Holochain.Runtime` | `net8.0` | Optional Admin API and conductor supervision/install/upgrade; never transitive from Commons.Client. |

Dependency direction is `Runtime -> Client -> Contracts`; the AZOA node depends
on `Azoa.Commons.Client` only. A generic `Azoa.Holochain.Client` becomes public
only after a second non-Commons consumer proves that abstraction. DNA/wire versions are independent from package
SemVer and are negotiated fail-closed.

The normative cross-repository package, facade, attestation, capability, error,
and idempotency contract is
[`federation-v2/holochain-dotnet-bridge-contract.md`](../federation-v2/holochain-dotnet-bridge-contract.md).
It deliberately specifies contracts and proof obligations only; it does not
authorize an implementation before this track's activation gate.

## Reliability and security requirements

- Exact supported conductor/DNA matrix with golden wire vectors.
- Bounded pending calls, signals, payload sizes, reconnects, and timeouts.
- Ambiguous writes reconcile by deterministic publication ID before retry.
- Admin interface remains loopback/UDS-only and outside the public client.
- No GPL/native wrapper, conductor executable, custody, PII, private holon
  payload, or off-chain value authority enters a package.
- Source Link, symbols, deterministic builds, API compatibility baselines,
  locked dependencies, vulnerability/license checks, SPDX SBOM, signatures,
  and provenance attestations are release gates.

## Acceptance criteria

1. All packages build and pack with zero warnings and enforce dependency direction.
2. Public APIs expose no WebSocket, MessagePack, process, or Admin implementation types.
3. Protocol/DNA/schema mismatch, capability denial, backpressure, timeout, and ambiguous commit are typed and fail closed.
4. Cross-client wire vectors and every supported conductor pair pass on Windows and Linux.
5. A clean local-feed .NET 10 sample consumes packages with no project references in external and supervised-runtime modes.
6. Package validation and public API baselines pass against the previous release.
7. Signed `.nupkg`/`.snupkg`, Source Link, SPDX SBOM, and provenance verify for the exact published hashes.
8. Protected-tag CI publishes preview/stable packages without rebuilding after validation.
9. The track archives only after a NuGet consumer proves installation and runtime compatibility.
