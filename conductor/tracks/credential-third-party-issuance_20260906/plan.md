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

## Phase 0 — settle the open question ⬜

- [ ] **Who may issue a credential to someone else?** Open issuance lets a griefer
      spam unwanted credentials onto an identity. Options: a role/scope, a consent
      grant from the recipient, or open. Record the choice and its reasoning before
      any code. Everything else in this track follows from D41; this does not.
- [ ] Write the failing tests first, as in the governance track:
      issuer ≠ holder; holder cannot revoke; issuer can; clawback against a
      `defaultFrozen: true` holding. All must fail on today's code.

## Phase 1 — issuance becomes two-party ⬜

- [ ] Soulbound mint takes an explicit recipient. **Stamp the issuer from the
      authenticated caller, never from the request body** — a body-supplied issuer
      is an authorization bypass.
- [ ] Write `current_owner` on the soulbound row at mint. Without it the revoke
      precondition is unreachable and the rail stays inert regardless of everything
      else in this track.
- [ ] Keep `issuer_user_id` write-once in the database (`WHERE issuer_user_id IS NONE`),
      not merely by caller convention — a later write would hand revocation
      authority to a different party.

## Phase 2 — clawback preconditions prove the right thing ⬜

- [ ] Replace the `RequireOptedInAsync` destination check, which refuses a frozen
      holding, with one that proves what Algorand clawback actually requires. Every
      soulbound credential is `defaultFrozen: true`, so today's guard refuses
      exactly its intended case.
- [ ] ⚠ Do **not** relax this into assuming success. Each precondition — clawback
      authority, asset exists, holder balance, rekey state — must still be **proven
      against the node before signing**. Copy `AlgorandProvider.BuildSignSubmitAsync`
      discipline; it is the reference-quality path.

## Phase 3 — confirmation and ownership ⬜

- [ ] Re-point `nft_ownership` on confirmation, on **both** the in-line path and the
      reconciliation-confirmed path. The reconciliation path is the one that was
      missed; it has no `CredentialRevoke` case at all today.
- [ ] Stamp the idempotency key into operation parameters so reconciliation can
      settle the claim, instead of leaving it `InProgress` forever.
- [ ] Include the asset id in the idempotency key, and assert on replay that the
      cached result's asset matches the request.
- [ ] Make an unconfirmed revoke unmistakable at the API seam. `IsError` is what
      every AZOA client helper branches on; a pending state hidden in a nested
      status string reads as success.

## Phase 4 — verify ⬜

- [ ] Adversarial review in a **separate context**, prompted to refute, aimed
      squarely at fabricated success: find any path that reports a successful revoke
      without a confirmed broadcast.
- [ ] **`/security-review` — non-negotiable.** This is a value path and a
      key-authority path.
- [ ] One serial sweep at the end: build + backend, then SDK and frontend.
- [ ] Record the verdict in the §2 review ledger.

## Watch for

- The `is_soulbound` / `is_current` singleton: a confirmed clawback **moved** the
  asset, it did not destroy it. The `(chain, contract, token_id, is_current = true)`
  set must keep naming exactly one row or every credential read surface loses the
  asset entirely.
- An already-revoked credential must not be revocable again into a no-op that
  reports success.
