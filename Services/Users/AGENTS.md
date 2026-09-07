---
type: Directory Notes
title: Services/Users
description: Why the user-NFT mint seam is two-party, and what a soulbound row does and does not mean.
tags: [services, credential-third-party-issuance_20260906]
---

# Services/Users

## `UserNFTService` — the mint seam (D41)

There are **two** mint entry points and the difference between them is the whole
credential rail:

| Seam | Holder | Issuer stamped | Is it a credential? |
| --- | --- | --- | --- |
| `MintUserNFTAsync(userId, model)` | the caller | none | **No** — a self-held badge |
| `MintUserNFTAsync(issuerUserId, recipientUserId, model)` | the recipient | the caller | **Yes** |

**Soulbound ≠ credential; *issued* ⇒ credential.** A soulbound row minted through the
self-mint seam is a badge somebody minted for themselves. It carries no
`issuer_user_id`, so `CredentialManager` fails closed on it ("no recorded issuer")
rather than authorizing its own holder to claw it back from themselves. That is the
vacuous case D19 forbids and D41 exists to end, and it is refused in three
independent places: the service (issuer == recipient), the store (before the row is
written), and the revoke path (issuer == holder).

**The issuer is never a request field.** `UserNFTMintModel` has no issuer property.
The issuer is the single input the D19 revoke decision reads, so a body-supplied one
would let any caller nominate itself the revocation authority over a credential it
did not issue. It is passed as an argument by the controller, from the authenticated
caller, and made write-once *in the database* by
`SurrealNftStore.SetIssuerUserIdAsync` (`WHERE issuer_user_id IS NONE`) rather than
by caller convention.

**Why issuance proves the holder's address first.** `current_owner` is the account a
revocation claws the credential back **from**. A credential written without it is
permanently unrevocable — a row that reads as valid and that no party on earth can
take back — which is strictly worse than refusing the mint. So the two-party seam
resolves the recipient's wallet on the credential's own chain from `IWalletStore` and
refuses the mint when it cannot. The self-mint seam is not held to this, because a
self-minted row is not a credential and never needed to be revocable.

**Who may call the issuance seam** is decided one layer up, at
`CredentialController.Issue` — role-gated on `dapp:manager`. The reasoning for that
choice (versus open issuance or a recipient consent grant) is written out there and in
`conductor/tracks/credential-third-party-issuance_20260906/plan.md` §Phase 0.
