---
type: implementation-handoff
---
# Round 1 NFT fixture migration

Authored, not built or executed. Source review is separate from approval.

Files: `tests/AZOA.WebAPI.Tests/Managers/NftManagerTests.cs`; credential
`CredentialRailHarness.cs`, `CredentialIssuanceAuthorizationTests.cs`,
`SoulboundIssuanceTests.cs`, `IssuedCredentialRevocationTests.cs`,
`CredentialTrackSeams.cs`, `AGENTS.md`; action `ComposedDagDeterminismTests.cs` and
`Handlers/Tier2ChainNodeHandlerTests.cs`.

Manager tests now use the actual durable lifecycle with a controlled provider
capability: pending evidence never publishes ownership; confirmed replay reuses the
same preparation; imported IDs, KYC refusal, unapproved fees, wrong wallet and owner
scope remain negative cases. Credential tests execute create, explicit recipient
acceptance, delivery and confirmed authority. Self-issued refusal is arranged via
an actual completed self-issued lifecycle, rather than seeding a store the manager
no longer reads. Reused revocation key against another asset must conflict.
Controller tests mock the new lifecycle boundary and expect 202 pending.

Refund action fixtures target the recipient-authorized lifecycle using the exact
succeeded same-asset source operation, including indirect predecessors. Pending
refund authorization/chain evidence is not a success. The current handler maps it
to failure; durable action waiting is a separately assigned execution package.

The obsolete reflection mint invoker was removed; its only remaining reflection
check disallows body-supplied issuer IDs. Concrete Algorand frozen-instruction tests
are owned by the core NFT author in `FrozenSoulboundClawbackTests.cs`. No shared
NftLifecycleHarness files were edited by this lane. `git diff --check` reported no
whitespace errors at handoff (line-ending warnings only).

Independent review corrections: self-issued lineage asserts absent external issuer and actor holder; revocation tests read actual lifecycle authority and ownership (including Revoked/IsActive). The harness no longer mutates an auxiliary projection. No tests executed.
