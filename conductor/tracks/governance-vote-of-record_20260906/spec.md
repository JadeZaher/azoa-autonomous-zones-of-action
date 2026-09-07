---
type: Track Spec
title: Governance vote of record
description: Replace the holon-counting tally with a claim-ledger tally, so a forged ballot counts for nothing by construction rather than by a check that can be forgotten.
tags: [feature, governance-vote-of-record_20260906, pending, security, rework]
timestamp: 2026-09-06T00:00:00Z
resource: ./metadata.json
---

# Governance vote of record

## Why this track exists

The D20 governance primitive shipped in `a59bd85` and **has a live Sybil hole**.
⚠ **Correction (2026-09-07):** this spec originally said the primitive "is not
enabled". That was false when written — every handler was assembly-scanned into
DI, the config registry accepted its config, and both dispatch seams would have
run it. "Not enabled" was a claim in prose, not a control in code, which is the
very defect class this track exists to correct. A real gate now exists
(`Services/Actions/NotEnabledActionNodeTypes`), refusing at publish and at both
dispatch seams. It lifts when this track closes.

`GovernanceVoteNodeHandler` claims correctly through `IPersonUniquenessGate`.
`GovernanceTallyNodeHandler` then **counts raw child holons** and never reads the
claim ledger. So the gate protects one path while the consumer of its result
reads a different, unprotected one.

**The attack, confirmed by adversarial review:** one verified user calls
`POST /api/holon` 500 times with
`{"assetType":"GovernanceVote","parentHolonId":"<proposal>","metadata":{"option":"yes"}}`.
None pass through the handler. None take a claim. The tally counts all 500.

Two further defects compound it:
- The ballot ordering key (`CastAtOrMax`) is read from **attacker-controlled holon
  metadata**, so a forged backdated ballot sorts first and displaces the genuine one.
- `HolonManager.UpdateAsync` blind-merges caller metadata, so a voter can rewrite
  their own cast ballot's option after casting.

⚠ **This is the third instance of one recurring class** in the §2 review ledger —
the AML decision was inert for the same reason, and `GetByGlobalPersonKeyAsync`
sat with zero callers for the same reason. *A control is not wired until the thing
that READS its result is wired too.* This track is the corrective, and the shape
of the fix is chosen so the class cannot recur here.

## Decisions this track implements

**D39 — the uniqueness claim IS the vote of record.** The tally reads
`person_operation_claim` and **never reads holons**. The claim table already holds
exactly one row per person per operation, written atomically inside a single
statement, so a forged holon counts for nothing *by construction*.

*Rejected alternative, recorded so it is not revisited:* a claim-**joined** holon
tally was the smaller change and was rejected because it stays safe only while
nobody drops the join. D39 has no join to drop.

**D40 — mutable until close, last write wins.** Normally the expensive option: it
needs an ordering rule the tally must trust, and the shipped ordering key was
attacker-controlled. Under D39 it is free — one claim row per person means a
change is an **in-place update of that row's option** with a **server-set**
timestamp. The "which ballot is latest" question does not arise, so the ordering
attack **ceases to exist** rather than being defended.

**D42 — the tally is off-chain, Tier-1.** No `RequiresChainCapability`, no custody
exposure, no per-proposal fee; it can ship while Phase 3 custody is gated. The
tally hash is still computed and stored so anchoring later is an added step.

## Scope

**In**
- Carry the chosen option (and a server-set `decided_at`) on the claim row.
- Rewrite `GovernanceTallyNodeHandler` to tally from the claim ledger.
- Enforce the voting window **server-side at claim time**, not in the handler only.
- Freeze the claim at close.
- Delete or neutralize the holon-ballot path so it cannot be mistaken for a vote.
- Make proposals readable by eligible voters without making holons broadly public.

**Out**
- On-chain anchoring (D42 — deferred, not cancelled).
- Any change to `IPersonUniquenessGate`'s own semantics; it is sound and shipped.
- Delegated/weighted voting. One person, one vote, per D3.

## Acceptance criteria

- **AC1** A vote holon created directly through the holon API is **not counted**.
  A test constructs the 500-holon attack above and asserts the tally is unchanged.
- **AC2** The tally reads no holon table. A test fails if `GovernanceTallyNodeHandler`
  gains a holon read.
- **AC3** Re-voting before close changes the outcome; the second vote replaces the
  first with no duplicate counted. Ordering derives only from **server-set** values —
  a test supplies hostile client timestamps and asserts they do not affect ordering.
- **AC4** A vote after `closesAt` is refused, and a claim frozen at close cannot be
  updated.
- **AC5** A proposal is readable by a non-owner eligible voter, **without** making
  holons broadly public — this repo has leaked cross-tenant through generic read
  surfaces three times.
- **AC6** The tally remains Tier-1: `RequiresChainCapability` is false and a test
  pins it.
- **AC7** The suite stays at or above 2657 passing with no new warnings.

## The test that matters most

A test that **fails if the claim-ledger read is removed**. Every defect this track
corrects came from a control that existed and went unread; the regression guard has
to be aimed at the reading, not the writing.
