---
type: Directory Notes
title: tests/AZOA.WebAPI.Tests/Credentials
description: Why the D41 credential tests are shaped the way they are, and what each file pins.
tags: [tests, credential-third-party-issuance_20260906]
---

# Credential issuance + revocation tests (D41 / D19)

**Every test in this directory is RED on the commit that introduced it** (Phase 0 of
`conductor/tracks/credential-third-party-issuance_20260906`). They exist before the
fix, so the fix is proven rather than asserted.

## Why these tests, when D19 already has tests

`tests/AZOA.WebAPI.Tests/Actions/Handlers/CredentialRevokeNodeHandlerTests.cs` already
proves a great deal about `CredentialManager` — holder-cannot-revoke, third-party
cannot revoke, pending-is-not-success, a reused key cannot replay another credential.
All of it passes. **All of it mocks the two-party credential into existence.**

`ICredentialIssuerStore.FindCurrentByAssetIdAsync` is stubbed to return a record whose
`IssuerUserId` and `HolderUserId` differ and whose `HolderAddress` is set. No
production path can produce that record:

- the only mint seam is `UserNFTService.MintUserNFTAsync(callerUserId, model)`, which
  takes no recipient, so the row's `user_id` is the caller;
- `SurrealNftStore.UpsertUserNFTAsync` stamps `issuer_user_id = userNFT.UserId` — the
  same party — so the issuer check reduces to "whoever minted it may revoke it";
- nothing writes `current_owner` at mint, so `HolderAddress` is null and every real
  revoke dies at the holder-address precondition before authorization is reached.

So the authorization rule is proven only against data that cannot exist. That is the
same failure mode as the inert AML decision and the `GetByGlobalPersonKeyAsync` with
zero callers: **a control that is invoked on one path and never fed by another.** The
tests here refuse mocked premises wherever a production seam can supply them: the
arrangement runs through the real mint service, and each test states the premise it
depends on (`issuer != holder`, `HolderAddress` present) as an assertion, so a test
can never pass vacuously.

## Why reflection appears in `CredentialTrackSeams`

D41 changes an API that does not exist yet: the mint seam must take a recipient
distinct from the caller, and the ownership row must carry an issuer distinct from its
holder. Writing `model.RecipientUserId = b` directly would not compile, and a test
project that does not compile blocks every other lane's build sweep.

`CredentialTrackSeams` therefore *probes* for the seam and fails with a message naming
the shapes it accepts, instead of dictating one. It accepts either

- a `MintUserNFTAsync` overload with an explicit recipient `Guid` parameter, or
- a writable recipient `Guid`/`Guid?` on `UserNFTMintModel`

and reads the issuer from an `IssuerUserId`-shaped member on the written row. Once
Phase 1 lands, these probes resolve and the tests become ordinary behavioural tests.
Fold the reflection away at that point — it is scaffolding for the red phase, not a
permanent pattern.

`ProjectAsTheStoreWould` mirrors `SurrealNftStore.ToCredentialRecord`
(`Providers/Stores/Surreal/SurrealNftStore.cs` §ICredentialIssuerStore): `user_id` is
the HOLDER, `current_owner` is the holder's address, `issuer_user_id` is the issuer.
That mapping is the contract between the two halves of the rail; if Phase 1 changes
it, change it here in one place and nowhere else.

## What each file pins

| File | Acceptance criteria | Why it is red today |
| --- | --- | --- |
| `SoulboundIssuanceTests` | AC1, AC3 | the mint seam takes no recipient and never writes `current_owner` |
| `IssuedCredentialRevocationTests` | AC1, AC2, AC3, AC7 | the credential the production mint produces is one-party and has no holder address, so neither revoke direction is reachable |
| `FrozenSoulboundClawbackTests` | AC4 | the rail never reaches the clawback at all; the provider-level frozen proof in `Signing/AlgorandClawbackPreconditionTests` is green but nothing proves the rail reaches it |
| `ReconciledRevocationTests` | AC5, AC6 | `ReconciliationService` has no `CredentialRevoke` case: a reconciliation-confirmed revoke marks the operation `Completed` and settles its idempotency claim while `nft_ownership` still names the holder |

## The rule this directory is here to keep

If a later change makes any of these green by weakening the assertion rather than by
supplying the behaviour, the rail is inert again and the fifth instance of "a claim in
prose is not a control in code" has shipped. `SelfIssuedCredential_IsRefused...` in
particular is the guard against the vacuous case returning quietly: delete it and
`issuer == holder` becomes revocable again with no test failing.
