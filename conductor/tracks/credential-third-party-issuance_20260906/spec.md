---
type: Track Spec
title: Credential third-party issuance
description: Make soulbound credentials issuable by one party to another, so D19's issuer-authorized revocation stops being vacuous and the rail stops being inert.
tags: [feature, credential-third-party-issuance_20260906, in_progress, value-path, rework]
timestamp: 2026-09-06T00:00:00Z
resource: ./metadata.json
---

# Credential third-party issuance

⚠ **Status (2026-09-07): Phases 0-3 below are landed** — soulbound mint is
two-party, `current_owner` is written at mint, the clawback precondition proves
the right thing, and ownership re-points on confirmation. **See `plan.md`,** whose
checkboxes are the per-item evidence. Only Phase 4 remains
(adversarial review + `/security-review`, then removing `CredentialRevoke` from
`Services/Actions/NotEnabledActionNodeTypes`), so the description below is
retained as the reason the fix was built, not as the current state of the mint
path.

## Why this track exists

The D19 revocation rail shipped in `a59bd85` and **cannot revoke anything**. It is
inert rather than dangerous, which is why it sits below governance in priority —
but it is also not a bug to patch. Its premise is missing.

D19 authorizes revocation to the credential's **issuer, not its holder**. That rule
presupposes an issuer distinct from a holder. **The only production mint path stamps
`issuer_user_id` = the minting caller, who is also the row's `user_id`/holder.** So
the authorization check reduces to *"whoever minted it may revoke it"* — the holder
revoking their own credential. The rule is not enforced; it is **vacuous**.

Three consequences, each independently confirmed:
- **No production path writes `current_owner` on a soulbound row**, so every real
  revoke dies at the holder-address precondition before authorization is even reached.
- A revoke confirmed via **reconciliation** rather than in-line never re-points
  `nft_ownership`, so AZOA keeps reporting the credential as held.
- The Algorand clawback precondition uses `RequireOptedInAsync`, which **refuses a
  frozen holding** — but soulbound credentials are minted `defaultFrozen: true`, so
  the guard refuses exactly the case it exists to serve. Algorand's clawback does
  not require an unfrozen destination; the precondition proves the wrong thing.

⚠ **This is the same class as governance**, and as the inert AML decision before it:
a control exists, is invoked on one path, and the thing that consumes its result was
never wired. Here the control is *authorization* and the missing consumer is the
*issuance* path that should have populated it.

## Decision this track implements

**D41 — soulbound credentials are issued by a third party to a recipient.** The
soulbound mint API takes a recipient distinct from the caller. The caller becomes
the issuer of record; the recipient becomes the holder. This is what ArdaNova's
BRONZE→DIAMOND tiers require — an organization issues to a member and can later
demote — and it is the premise D19 was written against.

*Consequence accepted:* this is a real API change to the mint path, not a patch to
the revoke path. Patching revoke alone cannot work, because the data it authorizes
against is never written.

## Scope

**In**
- Soulbound mint takes an explicit recipient; issuer of record is stamped from the
  authenticated caller, never from the request body.
- `current_owner` is written on soulbound rows at mint.
- Clawback preconditions prove what clawback actually needs, and stop refusing a
  legitimately frozen soulbound holding.
- Ownership is re-pointed on confirmation, on **both** the in-line and the
  reconciliation-confirmed paths.
- Idempotency key includes the asset id; replay asserts the cached result's asset
  matches the request.
- An unconfirmed revoke is unmistakable to an API client.

**Out**
- Transferable credentials. Soulbound stays soulbound.
- Revocation by anyone other than the issuer of record.
- Bulk/mass revocation.

## Acceptance criteria

- **AC1** A credential minted by issuer A to recipient B has `issuer_user_id = A`
  and holder `B`, with **A ≠ B**. A test asserts the two differ, so the vacuous
  case cannot silently return.
- **AC2** B cannot revoke it. A can. Both directions tested.
- **AC3** `current_owner` is populated at mint, so the revoke precondition is
  reachable at all. A test asserts a freshly minted credential is revocable.
- **AC4** Clawback succeeds against a `defaultFrozen: true` holding — the real
  shape of every soulbound credential.
- **AC5** After a confirmed revoke, no AZOA read surface still reports the
  credential as held by B. Tested on **both** the in-line and reconciliation paths.
- **AC6** ⚠ **No fabricated success.** No path reports a successful revoke without
  a confirmed broadcast: an unknown transaction is not a success, an exception is
  never swallowed into an `Ok`, and every precondition is **proven against the node**
  rather than assumed. This codebase has shipped this exact class three times,
  including an unknown transaction reported `CONFIRMED` on a reconcile path.
- **AC7** Revoking a different credential under a reused idempotency key does not
  replay the first credential's result.
- **AC8** `/security-review` before enabling. This is a value path.

## The question to answer before writing code — RESOLVED (Phase 0)

**Who may issue?** If any authenticated user can mint a credential to any other
user, a griefer can spam unwanted credentials onto someone's identity. Decide
whether issuance requires a role, a consent grant from the recipient, or is open —
and record it. This was the one genuinely open design question in the track; the
rest follows from D41.

**Decided: role-gated.** Issuance requires the `dapp:manager` role plus a scoped
API key carrying `nft:mint`, enforced in `CredentialController.Issue`. A
recipient-consent-grant model is the right long-term answer and is deliberately
deferred, not dismissed — it needs its own offer/accept rail and does not by
itself stop griefing (an unsolicited offer is still spam). Full reasoning:
`plan.md` Phase 0 and the doc-comment on `CredentialController.Issue` itself.
