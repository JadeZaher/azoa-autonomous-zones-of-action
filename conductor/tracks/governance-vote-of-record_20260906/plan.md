---
type: Track Plan
title: Governance vote of record — plan
description: Phase plan for moving the tally onto the claim ledger and retiring the holon-ballot path.
tags: [plan, governance-vote-of-record_20260906, in_progress]
timestamp: 2026-09-06T00:00:00Z
resource: ./metadata.json
---

# Plan

Ordered so the **attack test exists before the fix**. Every defect here came from
a guard nobody had written; writing the guard first is what stops a fourth
instance.

⚠ **Reconciled 2026-09-07.** Phases 0-3 landed in commit `fa2dfcf` ("feat: move
the governance vote of record onto the claim ledger (D39/D40/D42)"), which is on
this branch's history, but the checkboxes below had not been updated to say so —
itself a small instance of the defect class this track exists to correct. Verified
directly against source before ticking anything: `Services/Governance/GovernanceClaimLedgerTally.cs`,
`Services/Governance/GovernanceVoteOfRecordGuard.cs`,
`Persistence/SurrealDb/Models/PersonOperationClaim.cs` (`DecisionOption`/`DecidedAt`),
and `tests/AZOA.WebAPI.Tests/Governance/GovernanceVoteOfRecordTests.cs` (the AC1
500-holon attack test, present and passing). Only **Phase 4 remains** — the
`NotEnabledActionNodeTypes` gate is deliberately still in place, and stays until
Phase 4's adversarial review closes.

## Phase 0 — pin the attack ✅

- [x] Write the failing test FIRST: create 500 vote holons directly via the holon
      API against a live proposal, run the tally, assert the forged ballots are
      counted. **It must fail on today's code** — a regression test that never
      failed is precisely how the shipped defects got in (§8).
      → `GovernanceVoteOfRecordTests.cs` AC1 (two 500-holon variants: claim-holder
      backdated forgeries, and a verified user with no claim at all).
- [x] Write the hostile-ordering test: two ballots whose client-supplied timestamps
      are crafted to invert real order; assert ordering follows server-set values.
      → AC3.
- [x] Write the post-close test: a vote after `closesAt` is refused. → AC4.
- [x] Confirm all three fail. Do not proceed until they do.

## Phase 1 — the claim carries the vote ✅

- [x] Extend `PersonOperationClaim` with the chosen option and a **server-set**
      `decided_at`. Keep the table node-operator-only: it is deliberately absent
      from `SearchableEntityType`, `HolonQueryRequest` and the MCP query allow-list,
      and must stay absent (§4 assumption 1). → `DecisionOption`/`DecidedAt` fields,
      with the `[SurrealNote]` on the model recording exactly why the table must
      never join a consumer read surface.
- [x] Extend `IPersonUniquenessGate` so a claim can be **updated in place** by its
      own owner while the operation is open, and frozen at close. Preserve the
      existing discipline: the winner is decided **inside one statement** and read
      back — a preceding read lets two concurrent writers both pass.
- [x] Regenerate the golden (`AZOA_REGENERATE_GOLDENS=1`), never hand-edit `.surql`.
      ⚠ Note the deploy layering: `Generated/Schemas/` IS the deployed artifact for
      54 of 60 tables; `person_operation_claim` is not one of the six overlaid, so
      the regenerated golden is what ships.

## Phase 2 — the tally reads the ledger ✅

- [x] Rewrite `GovernanceTallyNodeHandler` to tally from `person_operation_claim`
      scoped to the proposal's operation key. **It must not read any holon table.**
      → `GovernanceClaimLedgerTally` is the whole tally; it depends on
      `IPersonOperationClaimStore` and nothing else.
- [x] Keep the tally hash computation and storage (D42 leaves anchoring open).
- [x] Phase 0's attack test must now pass.
- [x] Add the guard that fails if the claim-ledger read is removed.
      → `GovernanceVoteOfRecordGuard`, reflection-checked against
      `GovernanceTallyNodeHandler`'s actual constructor dependencies, so a body
      that keeps the type but drops the call is caught by the AC2 tests instead.

## Phase 3 — retire the forgeable path ✅

- [x] Remove or neutralize the `GovernanceVote` holon-ballot representation so it
      cannot be mistaken for a vote. If a holon is kept for display, it must be
      derived from the claim and carry no authority. → `ConsumerGovernanceHolonTypes`
      keeps a consumer-readable ballot **receipt** holon that carries no authority;
      the person-key/decision pairing lives only in `person_operation_claim`.
- [x] Close the `HolonManager.UpdateAsync` metadata rewrite for any governance
      artifact.
- [x] Fix proposal readability for eligible voters. ⚠ Did **not** reach for a broad
      public flag: `Services/Governance/AGENTS.md` §AC5 records the actual shape —
      `HolonManager.CanRead` is widened for the **electorate, not the node**, via
      `ConsumerGovernanceHolonTypes.IsInElectorate`, fail-closed on a proposal that
      records no electorate, with `electorateTenant` read from the ACTING tenant
      (never from config) so an author cannot self-declare their own electorate.

## Phase 4 — verify ⬜

- [ ] Adversarial review in a **separate context**, prompted to refute. Its decisive
      question: *can a vote that never took a claim still be counted?*
- [ ] One serial sweep: `dotnet build --no-incremental` + `dotnet test`, then SDK
      and frontend. Never a test→fix→test loop.
- [ ] Update the frontend/SDK node-type mirrors, which currently lag the backend
      enum by 7 (`node-catalog.ts`, `node-output-schema.ts`, `presets.ts`,
      `sdk/azoa-wallet/src/api/client.ts`).
- [ ] Record the verdict in the §2 review ledger. A phase with no verdict is
      unreviewed, not done.

## Do not do

- Do not "fix" the tally by adding a claim join to the holon query. That was
  considered and rejected (D39): it stays safe only while nobody drops the join.
- Do not weaken any failing invariant test to make the suite green. Every one of
  the 13 failures in the Phase 5 sweep was a real contract, correctly encoded.
