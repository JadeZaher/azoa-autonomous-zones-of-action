---
type: Track Plan
title: Governance vote of record — plan
description: Phase plan for moving the tally onto the claim ledger and retiring the holon-ballot path.
tags: [plan, governance-vote-of-record_20260906, pending]
timestamp: 2026-09-06T00:00:00Z
resource: ./metadata.json
---

# Plan

Ordered so the **attack test exists before the fix**. Every defect here came from
a guard nobody had written; writing the guard first is what stops a fourth
instance.

## Phase 0 — pin the attack ⬜

- [ ] Write the failing test FIRST: create 500 vote holons directly via the holon
      API against a live proposal, run the tally, assert the forged ballots are
      counted. **It must fail on today's code** — a regression test that never
      failed is precisely how the shipped defects got in (§8).
- [ ] Write the hostile-ordering test: two ballots whose client-supplied timestamps
      are crafted to invert real order; assert ordering follows server-set values.
- [ ] Write the post-close test: a vote after `closesAt` is refused.
- [ ] Confirm all three fail. Do not proceed until they do.

## Phase 1 — the claim carries the vote ⬜

- [ ] Extend `PersonOperationClaim` with the chosen option and a **server-set**
      `decided_at`. Keep the table node-operator-only: it is deliberately absent
      from `SearchableEntityType`, `HolonQueryRequest` and the MCP query allow-list,
      and must stay absent (§4 assumption 1).
- [ ] Extend `IPersonUniquenessGate` so a claim can be **updated in place** by its
      own owner while the operation is open, and frozen at close. Preserve the
      existing discipline: the winner is decided **inside one statement** and read
      back — a preceding read lets two concurrent writers both pass.
- [ ] Regenerate the golden (`AZOA_REGENERATE_GOLDENS=1`), never hand-edit `.surql`.
      ⚠ Note the deploy layering: `Generated/Schemas/` IS the deployed artifact for
      54 of 60 tables; `person_operation_claim` is not one of the six overlaid, so
      the regenerated golden is what ships.

## Phase 2 — the tally reads the ledger ⬜

- [ ] Rewrite `GovernanceTallyNodeHandler` to tally from `person_operation_claim`
      scoped to the proposal's operation key. **It must not read any holon table.**
- [ ] Keep the tally hash computation and storage (D42 leaves anchoring open).
- [ ] Phase 0's attack test must now pass.
- [ ] Add the guard that fails if the claim-ledger read is removed.

## Phase 3 — retire the forgeable path ⬜

- [ ] Remove or neutralize the `GovernanceVote` holon-ballot representation so it
      cannot be mistaken for a vote. If a holon is kept for display, it must be
      derived from the claim and carry no authority.
- [ ] Close the `HolonManager.UpdateAsync` metadata rewrite for any governance
      artifact.
- [ ] Fix proposal readability for eligible voters. ⚠ Do **not** reach for a broad
      public flag: `Holon.IsPublic` is absent from both create and update models,
      and widening generic read surfaces is how this repo leaked cross-tenant three
      times. Scope to what governance needs and record the choice.

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
