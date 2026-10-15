---
type: Track Plan
title: Credential third-party issuance — plan
description: Phase plan for making soulbound issuance a two-party operation so issuer-authorized revocation becomes real.
tags: [plan, credential-third-party-issuance_20260906, pending]
timestamp: 2026-09-06T00:00:00Z
resource: ./metadata.json
---

# Plan

⚠ **Do not start at the revoke path.** Two remediation rounds already tried that
and produced more new defects than they closed. The revoke path authorizes against
data the mint path never writes; the mint path is the fix.

## Phase 0 — settle the open question ✅

- [x] Write the failing tests first, as in the governance track:
      issuer ≠ holder; holder cannot revoke; issuer can; clawback against a
      `defaultFrozen: true` holding. All must fail on today's code.
      → `tests/AZOA.WebAPI.Tests/Credentials/`.

### DECISION — who may issue a credential to another user: **ROLE-GATED**

Issuance requires the **`dapp:manager` role** (`AzoaDappRoles.Manager`), and a scoped
API key must additionally carry `nft:mint` (`AzoaScopes.NftMint`). Enforced at
`CredentialController.Issue`, which is the only route that reaches the two-party mint
seam. The full reasoning also lives in that method's doc-comment, next to the check.

**Why not open.** A credential is soulbound and revocable ONLY by its issuer, so an
unwanted one is permanent from the recipient's side: they cannot transfer it away and
they cannot revoke it. Open issuance is therefore a griefing primitive with no cleanup
path — anyone could staple arbitrary claims onto anyone's identity and be the only
party able to remove them.

**Why not a recipient consent grant (yet).** It is the right long-term answer and is
deliberately deferred, not dismissed. It needs an offer/accept rail with its own store,
expiry and revocation model, and it does not by itself stop griefing — unsolicited
offers are still spam. It is also not free of the same authorization question: someone
must decide who may *ask*. Recorded here as owed rather than implied by silence.

**Why role.** D41's own use case is an ORGANIZATION issuing BRONZE→DIAMOND tiers to its
members. Issuance is an organizational capability, not a peer-to-peer one. `dapp_role`
is a server-stamped claim that defaults to `dapp:user` when absent
(`AzoaDappRoles.Normalize`) and can only be assigned by an operator or an existing
manager — so unlike a bare signing-scope check (which `HasSigningScope` passes for free
for every JWT owner) this is a restriction an ordinary authenticated user actually
fails.

**The backstop the chain already provides.** An ASA cannot land in an account that has
not signed its own opt-in, so no soulbound asset reaches a recipient who never
consented. What an ungated route would pollute is AZOA's own `nft_ownership` record —
which is precisely what the role gate protects.

**Enforcement, not prose.** `tests/AZOA.WebAPI.Tests/Credentials/`
`CredentialIssuanceAuthorizationTests` fails if the role gate, the scope gate, or the
"issuer from the authenticated caller" stamping is removed.

## Phase 1 — issuance becomes two-party ✅

- [x] Soulbound mint takes an explicit recipient:
      `IUserNFTService.MintUserNFTAsync(issuerUserId, recipientUserId, model)`. The issuer
      is an ARGUMENT from the authenticated caller; `UserNFTMintModel` has no issuer
      property at all, so there is nothing for a body to supply.
- [x] `current_owner` is written at mint from the recipient's wallet on the credential's
      own chain, PROVEN from `IWalletStore` — and an issuance whose recipient has no such
      wallet is refused before any row is written, because a credential with no
      `current_owner` is permanently unrevocable.
- [x] `issuer_user_id` stays write-once in the database
      (`SurrealNftStore.SetIssuerUserIdAsync`, `WHERE issuer_user_id IS NONE`). `FromPoco`
      now projects the column so a round-tripped row re-stamps the SAME issuer and the
      guard stays idempotent instead of looking like a fresh self-minted badge.
- [x] `issuer == holder` is refused in three independent places: the service, the store
      (before the write), and `CredentialManager` (before any clawback).

**Soulbound ≠ credential.** A soulbound row minted through the one-party self-mint seam
is a self-held badge: it carries NO issuer, so the revoke path fails closed on it rather
than authorizing its holder to claw it back from themselves. Only the two-party seam
issues credentials. See `Services/Users/AGENTS.md`.

## Phase 2 — clawback preconditions prove the right thing ✅

- [x] `AlgorandProvider.ClawbackAssetAsync` no longer uses `RequireOptedInAsync` (the
      TRANSFER precondition, which refuses a frozen holding). It uses
      `RequireClawbackableHoldingAsync` + `RequireClawbackDestinationAsync`, which allow
      a frozen holding at BOTH ends — the shape of every soulbound credential — while
      still proving opt-in and balance from Algod.
- [x] Nothing is assumed: clawback AUTHORITY is proven equal to the signer
      (`RequireClawbackAuthorityAsync`, ordinal comparison), the asset is proven to exist,
      the holder is proven to hold the units, and the destination is proven opted in —
      each read from the node before a byte is signed.

**Note — this phase was already landed in the provider before this pass**
(`AlgorandProvider.ClawbackAssetAsync`); no provider code was changed here. What was
missing was a RAIL that reaches it: the mint wrote no `current_owner` and stamped the
holder as issuer, so `CredentialManager` refused long before a clawback was built. Phase 1
is what makes `FrozenSoulboundClawbackTests` reachable at all — a precondition that works
on a path no request can travel is the same inert-control class as the rest of this track.

**Gap, stated rather than implied:** the SIGNER's rekey state is not proven. If the
platform clawback-authority account were rekeyed, the platform key could no longer sign
for it and the node would reject the transaction — a refusal, not a fabricated success, so
it is a robustness gap and not a correctness one. It is not added here because the
AC4 stub deliberately serves no `/v2/accounts/{platform}` response, so adding the probe
would have to come with a matching change to a test this lane does not own.

## Phase 3 — confirmation and ownership ✅

- [x] In-line path re-points `nft_ownership` only on a CONFIRMED clawback
      (`CredentialManager.RevokeAsync` → `RecordCredentialRevokedAsync`).
- [x] Reconciliation path now has a `CredentialRevoke` case
      (`ReconciliationService.ApplyCredentialRevokeOwnershipAsync`). It runs **before** the
      status transition, because the transition is what settles the idempotency claim and
      settling a claim for a move AZOA never recorded is the exact defect. A revoke that
      cannot be re-pointed stays `PendingConfirmation`, is hard-flagged for operators, and
      its claim stays `InProgress` — never Completed.
- [x] An operation that is chain-confirmed but does not say WHICH credential moved is
      never advanced: nothing is guessed, and it is flagged as an operator problem
      regardless of age (waiting will never supply a missing `CredentialId`).
- [x] `Parameters["IdempotencyKey"]` is stamped on the row from its first write, so a
      stranded claim settles from chain truth instead of staying `InProgress` forever.
- [x] The idempotency key names the asset (`credential-revoke:{caller}:{chain}:{asset}:{tail}`)
      and replay re-checks the cached result's `AssetId`, returning `CONFLICT` on a
      mismatch rather than serving one credential's result as another's.
- [x] An unconfirmed revoke comes back `IsError = true` with
      `REVOCATION_PENDING_CONFIRMATION` and maps to **202**, not 200 and not 400.

## Phase 4 — verify ⬜

- [ ] Adversarial review in a **separate context**, prompted to refute, aimed
      squarely at fabricated success: find any path that reports a successful revoke
      without a confirmed broadcast.
- [ ] **`/security-review` — non-negotiable.** This is a value path and a
      key-authority path. Give it the ISSUANCE gate too, not only revocation:
      `dapp:manager` is now a value-adjacent authority.
- [ ] One serial sweep at the end: build + backend, then SDK and frontend.
- [ ] Record the verdict in the §2 review ledger.
- [ ] Remove `Services/Actions/NotEnabledActionNodeTypes`' `CredentialRevoke` entry as the
      FINAL step (metadata.json §enforcement_gate). Left in place by this pass.

### Owed / deferred

- A dedicated `credential:issue:sign` scope. Issuance currently reuses
  `AzoaScopes.NftMint` because the role gate is the real control and `AzoaScopes` was
  contended by another lane during this pass. Worth splitting so "mint NFTs" stops
  implying "issue credentials to third parties".
- The recipient consent grant described in Phase 0. Still owed; the role gate is the
  interim answer, not the final one.
- SDK + frontend surfaces for `POST /api/credential/issue/{recipientUserId}`.

## Watch for

- The `is_soulbound` / `is_current` singleton: a confirmed clawback **moved** the
  asset, it did not destroy it. The `(chain, contract, token_id, is_current = true)`
  set must keep naming exactly one row or every credential read surface loses the
  asset entirely.
- An already-revoked credential must not be revocable again into a no-op that
  reports success.
