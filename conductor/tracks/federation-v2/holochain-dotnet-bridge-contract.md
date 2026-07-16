---
type: contract
track: federation-v2
status: deferred
created: 2026-07-13
depends_on:
  - node-conformance-manifest
  - commons-dotnet-packages
activation_gate: >-
  DHT-bound implementation remains prohibited until federation-v2 has at least
  three independent production operators and one concrete cross-node use case
  with a real counterparty. This document is a contract-only design artifact.
---

# Holochain-to-.NET bridge contract

## Purpose and non-activation

This is the binding architecture contract for the future `AZOA.Commons`
repository and its one permitted AZOA-node integration. It makes the package,
attestation, capability, error, and idempotency decisions testable without
adding a conductor, Holochain dependency, or fake gateway to this repository.

The only shipped node functionality remains the local L0 conformance document.
Nothing in this contract authorizes DHT publication, peer discovery, value
settlement, or a new package reference before the activation gate is proven and
recorded in this track. Pre-gate CI rejects every `Azoa.Commons.*` and
`Azoa.Holochain.*` package/project reference. The future change that permits only
`Azoa.Commons.Client` must cite a machine-verifiable activation-evidence artifact
containing independent production operator identities, their deployed endpoint
proofs, and one real counterparty cross-node use case; a handwritten assertion is
not evidence. It must also leave direct Contracts and Runtime references forbidden.

## Ownership and dependency direction

```
Azoa.Holochain.Runtime  ->  Azoa.Commons.Client  ->  Azoa.Commons.Contracts
                                  ^
                                  | only direct Commons package reference
                           AZOA node integration
```

| Surface | Owns | Must not contain |
|---|---|---|
| `Azoa.Commons.Contracts` (`netstandard2.0`) | Immutable protocol records, canonical serializers/vectors, capability names, typed result/error algebra, and public-fact schemas. | Holochain transport, WebSocket/MessagePack/process types, host/domain models, SurrealDB, custody, PII, settlement, payments, or ASP.NET dependencies. |
| `Azoa.Commons.Client` (`net8.0`) | The public typed facade; protocol/DNA/schema negotiation; bounded request correlation, app-token use, cancellation, timeout, backpressure, signal handling, and publication reconciliation. Its App API transport is internal. | Admin API, conductor lifecycle, native binaries, Holochain key material, host policy, automatic economic effects, or a transitive runtime dependency. |
| `Azoa.Holochain.Runtime` (`net8.0`, optional) | Explicitly opted-in local conductor lifecycle and the loopback/UDS-only Admin boundary. | A transitive dependency from Client, a public Admin socket/type, remote Admin exposure, custody, or node/economic policy. |
| AZOA node | A post-gate `IHoloFederationGateway` adapter that maps opted-in local facts to neutral Contracts and enforces local authorization, disclosure, and peer policy. | The runtime, conductor/transport implementation, DNA/zomes, Surreal access in Commons packages, or any value/custody authority in federation. |

After the recorded activation evidence is independently verified, the node may
directly reference only `Azoa.Commons.Client`; Contracts arrive as its package
dependency. `Azoa.Holochain.Runtime`, `Azoa.Commons.Contracts`, the historical
NextGen/HoloNET client, and all direct Holochain implementation namespaces remain
prohibited direct node references. The architecture ratchet rejects every Commons
package pre-gate and must be deliberately revised, not bypassed, when the evidence
permits the Client facade.

## Stable public facade

The package public API is intentionally narrow. The following is the required
semantic shape, not a request to add these types to this repository before the
gate:

```csharp
public interface ICommonsClient
{
    Task<CommonsResult<CommonsCapabilities>> GetCapabilitiesAsync(
        CommonsSession session, CancellationToken cancellationToken = default);

    Task<CommonsResult<PublicationReceipt>> PublishAsync(
        PublicationRequest request, CancellationToken cancellationToken = default);

    Task<CommonsResult<PublicationLookup>> ReconcilePublicationAsync(
        PublicationId publicationId, CancellationToken cancellationToken = default);

    Task<CommonsResult<ResolvedPublicFact>> ResolveAsync(
        PublicFactReference reference, CancellationToken cancellationToken = default);
}
```

`IHoloFederationGateway` is a node-only adapter over this facade. Its inputs
are already-authorized, explicitly opted-in *public facts*, never a `Holon`, a
Surreal record, wallet, avatar, payment, fee settlement, or chain operation.
It has exactly three write intents: `PublishConformance`,
`PublishHolonReference`, and `PublishQuestReceipt`; resolution is read-only.
It cannot expose a generic zome-call method. The node maps every client outcome
to its own typed application result and logs unexpected exceptions once at the
HTTP/worker boundary.

All public request and response types are immutable. Public contracts expose
neither WebSocket, MessagePack, `Process`, Admin API, native-library, nor
Holochain implementation types. `OperationCanceledException` propagates; expected
transport/protocol outcomes are `CommonsResult<T>`, not catch-all exceptions.

## Capability and version negotiation

Before an invocation, Client obtains a bounded `CommonsCapabilities` statement
that includes a protocol version, exact DNA hash, per-entry schema versions,
conductor compatibility identifier, maximum payload and pending-call limits,
and the granted set of capabilities. A request names one required capability;
the client rejects it locally without a zome call unless all of these match the
caller-supported matrix. Capabilities are only:

| Capability | Permitted action |
|---|---|
| `PublishConformance` | Publish the signed L0 conformance/binding public fact. |
| `PublishHolonReference` | Publish an owner-authorized, metadata-only reference. |
| `PublishQuestReceipt` | Publish a reference-only completion receipt, never a balance/effect. |
| `ResolvePublicFact` | Read a public fact and its provenance attestation. |

There is deliberately no capability for wallet management, asset transfer,
mint, fee collection, payout, signing with a custody key, private data query,
or arbitrary zome invocation. A schema/DNA/capability mismatch is a typed,
fail-closed result; clients never silently downgrade or select a "closest"
version.

## Attestation and resolve verification

The Commons DHT action proves DNA-level authorship and validation, but it does
not authorize a node to trust arbitrary resolved payloads. Every publishable
fact carries a canonical, fixed-size envelope with:

- `schemaVersion`, `publicationId`, fact kind, SHA-256 content digest, issuer
  node id, issuer L0 key id, issuer agent public-key digest, issued/expiry time,
  and the DHT action hash;
- a node P-256 signature over a domain-separated canonical envelope; and
- a `NodeAgentBinding`: an agent-signed/entry-authored link from the conductor
  agent key to the current L0 descriptor key, reciprocally signed by the L0 key,
  including bounded validity and rotation continuity.

On resolve, the client verifies canonical bytes, digest, bounds, expiry, DHT
identity/action consistency, node signature, and binding continuity. The node
also runs `NodeConformanceVerifier` against the issuing L0 document, then
applies its own peer/conformance threshold and disclosure policy. A malformed,
expired, mismatched, unbound, or tampered attestation yields
`InvalidAttestation`, `IdentityBindingInvalid`, or `ConformanceRejected` and
returns no usable fact. Cache entries are keyed by digest and expire no later
than the signed envelope. A stale cache may be returned only when the caller
explicitly requests stale data and the result is labelled stale; it is never
used to authorize a write or economic action.

## Idempotency and ambiguous publication

Every write has a caller-supplied `PublicationId`, deterministically derived
from protocol version, issuer node id, fact kind, logical subject id, canonical
content digest, and publication-semantics version. Timestamps, signatures,
transport correlation IDs, and retries are excluded so the same logical fact
has one identifier. Reusing an ID with a different immutable digest or kind is
`PublicationConflict`.

The DNA enforces one immutable fact per `(issuer agent, publicationId)` and
returns an existing receipt for exact replay. Client does not blindly retry an
ambiguous post-send failure. It returns `AmbiguousCommit` with the publication
ID; the caller must use `ReconcilePublicationAsync`. Reconciliation returns
`Committed`, `Absent`, or `Conflict` after querying the deterministic ID. Only
`Absent` permits a fresh attempt with the same bytes. Timeouts, cancellation,
and process crashes therefore cannot produce a second semantic publication.

## Result and error algebra

`CommonsResult<T>` carries either a value or exactly one stable error code plus
safe correlation metadata; it never embeds credentials, app tokens, private
payloads, stack traces, or an exception message. Required codes are:

| Code | Caller behavior |
|---|---|
| `UnsupportedProtocol`, `DnaMismatch`, `SchemaMismatch` | Disable the feature for that peer; require an explicit compatible upgrade. |
| `CapabilityDenied`, `ConformanceRejected`, `IdentityBindingInvalid`, `InvalidAttestation` | Fail closed; do not retry automatically. |
| `PayloadTooLarge`, `InvalidRequest`, `PublicationConflict` | Correct input/operator configuration; never mutate or retry unchanged. |
| `Unavailable`, `Backpressure`, `Timeout` | Surface honestly; bounded caller retry policy only before a send is accepted. |
| `AmbiguousCommit` | Reconcile by `PublicationId`; no publish retry until reconciliation says absent. |
| `NotFound` | Treat as absent only for deterministic reconciliation; never infer authority. |
| `Internal` | Correlate structured logs; no detail crosses the package boundary. |

## Mandatory proof suite

The separate Commons repository cannot call the package implementation complete
until these tests are present and run on Windows and Linux against each pinned
conductor/DNA matrix entry:

1. Canonical contracts: serialization, hashes, version negotiation, and
   P-256/agent-binding vectors match golden fixtures across independent clients.
2. Facade isolation: reflection/API-baseline tests prove public Client APIs leak
   no transport/Admin/native types and Runtime never becomes a Client dependency.
3. Capability denial/mismatch: no app/zome call occurs when the negotiated
   capability, DNA, or schema is wrong.
4. Attestation: modified bytes, wrong digest, expired envelope, unbound agent,
   broken key rotation, and insufficient conformance all fail closed.
5. Idempotency: concurrent identical publication IDs produce one immutable DHT
   fact; changed content with the same ID conflicts; post-send loss produces
   `AmbiguousCommit` and reconciliation converges without a second publish.
6. Reliability: pending calls, signals, payloads, reconnect attempts, and
   backpressure are bounded; cancellation leaks neither a call nor credentials.
7. Runtime: Admin listeners are loopback/UDS-only, no remote endpoint is
   configurable, and Runtime is absent from a clean Client-only consumer.
8. Node integration: an authorized fake client verifies that only owner-opted-in
   reference facts are mapped, all client failures remain typed, and no resolved
   fact can trigger a wallet, transfer, fee, payout, or persistence mutation.

## Activation and change control

Before implementation begins, operators must record evidence of the federation
activation gate in `federation-v2`, select one conductor/DNA compatibility
matrix, create `AZOA.Commons` as a separate repository, and approve canonical
vectors. Changes to bytes-to-sign, publication identity, capabilities, or any
error code require a new protocol/schema version, cross-client vectors, and a
compatibility decision. Package SemVer does not replace protocol/DNA versioning.

As of 2026-07-13, the node-side architecture ratchet enforces the pre-gate
package ban. It is not activation evidence: the separate node-conformance
manifest track still requires CI provenance/freshness and an independent
deployed-endpoint consumer proof before local L0 facts can support a federation
gate review.

This contract does not weaken the standing rule: value settles on a chain and
remains outside Commons. A federation receipt can reference a confirmed economic
effect; it cannot cause, price, hold, reconcile, or certify that effect.
