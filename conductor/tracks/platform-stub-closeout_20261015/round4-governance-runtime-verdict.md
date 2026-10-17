---
type: verdict
round: 4
gate: governance-node-types
status: pending
---

# Governance runtime verdict (gate for GovernanceProposalCreate / GovernanceVote / GovernanceTally)

RUNTIME_VERDICT: PENDING

The source review accepted the governance transactions (`round2-governance-source-review.md`,
`SOURCE_VERDICT: ACCEPTED`) but nothing has executed. Plan §0.2: the three node types stay in
`Services/Actions/NotEnabledActionNodeTypes.cs` until a verdict recorded **after** green real-DB
race fixtures. `tests/AZOA.WebAPI.Tests/Actions/NotEnabledActionNodeTypesTests.cs` fails if the
gate and this line disagree, in either direction.

## Lift procedure (all four, in order)

1. The integrated sweep runs `SurrealGovernanceTests` (all cases, including the race fixtures in
   `SurrealGovernanceTests.Races.cs`) green on SurrealDB 3.1.4, plus the handler/resolver cases.
   Record the run (commit, command, pass count) below.
2. An independent reviewer (separate context from the authors, prompted to refute) reads that
   evidence and the race fixtures against `round3-transaction-scope-review.md`, and confirms each
   fixture would go red if its guard were removed (mutation check).
3. The reviewer replaces the line above with `RUNTIME_VERDICT: ACCEPTED` (or `REJECTED` with
   findings) and signs the evidence section.
4. In the same change, remove the three governance members from `Blocked`. A lift without step 3,
   or a verdict without the code change, fails `NotEnabledActionNodeTypesTests`.

## Fixture inventory the verdict covers

| Race / invariant | Fixture |
|---|---|
| duplicate creation commits once | `Proposal_projection_and_receipt_commit_once_and_cancelled_origin_cannot_create` |
| concurrent finalizers publish one snapshot | `Concurrent_finalizers_publish_one_immutable_snapshot` |
| AML hit append vs vote: one serial order | `Concurrent_AML_hit_append_and_vote_have_one_serial_order_and_later_votes_are_refused` |
| run cancellation vs vote commit | `Concurrent_cancellation_and_vote_commit_in_one_serial_order` |
| run cancellation vs proposal creation | `Concurrent_cancellation_and_creation_commit_in_one_serial_order` |
| participation revocation vs vote | `Concurrent_participation_revocation_and_vote_commit_in_one_serial_order` |
| vote vs finalization at the cutoff | `Vote_and_finalization_racing_after_cutoff_leave_the_tally_equal_to_the_ledger` |
| membership change vs creation snapshot | `Membership_change_during_creation_refuses_a_stale_electorate` |
| offboarding after casting keeps the finalized result | `Finalization_is_immutable_counts_claims_only_and_refuses_before_cutoff` |

## Evidence

_None yet (no test has executed on this tree)._
