---
type: Directory Notes
title: Credential lifecycle regression fixtures
---

# Credential issuance and revocation

`CredentialRailHarness` runs the real `NftLifecycleService` and `CredentialManager`
against an in-memory lifecycle store and a controlled provider capability. It publishes
ownership only after the provider reports confirmed creation, recipient acceptance,
and delivery. The provider boundary is mocked; these tests do not claim protocol or
live-network execution evidence. `FrozenSoulboundClawbackTests` owns the concrete
Algorand instruction fixture separately.

`SoulboundIssuanceTests` pins separate issuer and holder, recipient signing, absent
wallet refusal, imported-asset rejection, and no ownership while confirmation is
pending. `IssuedCredentialRevocationTests` obtains its premise through that lifecycle,
then rejects holder/self-issued authority and cross-asset idempotency replay. Assertions read actual lifecycle authority and ownership; the harness does not update a secondary projection.

`CredentialIssuanceAuthorizationTests` verifies controller role/scope gates and caller
identity propagation. The remaining reflection helper only asserts that request
models cannot supply issuer identity; obsolete runtime mint-method discovery is gone.
`ReconciledRevocationTests` retains reconciliation projection coverage for prior
operation records. Newly authored and migrated fixtures remain unexecuted until the
session's integrated verification sweep.
