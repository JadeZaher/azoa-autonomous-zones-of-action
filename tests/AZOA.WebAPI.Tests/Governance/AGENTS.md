# tests/AZOA.WebAPI.Tests/Governance

Phase 0 of `conductor/tracks/governance-vote-of-record_20260906`. These are the
**attack tests, written before the fix**, and they were red until D39 landed. A
regression test that never failed is how every defect in the §2 review ledger
shipped.

**Status: D39 has landed and all five are green.** They stay as written — the
point of an attack test is that it goes red again the day a holon read comes
back, so nothing here may be relaxed to match an implementation.

<a id="lane-split"></a>
**This directory owns the BEHAVIOURAL half; the unit lane owns the structural
half.** `tests/AZOA.WebAPI.Tests/Actions/Handlers/GovernanceNodeHandlerTests.cs`
was reconciled to D39 rather than left asserting the rejected claim-joined
tally: the properties that survived moved to the ledger, the ones that only made
sense while a ballot holon was the vote were deleted, and the ones this file
already pins were NOT restated there. Two of them are deliberately paired —
`Tally_CountsNoBallotHolonWrittenOutsideTheVoteNode` here (the tally stops
reflecting the ledger) and `Tally_CountsNoForgedBallotHolon_BecauseItReadsNoBallotHolonAtAll`
there (the tally regains a way to read ballots, via `GovernanceVoteOfRecordGuard`).
Neither alone is the guarantee.

## What is being pinned

The gate (`IPersonUniquenessGate`) is sound and `GovernanceVoteNodeHandler`
claims through it correctly. The bug is on the READING side: the tally derives
the outcome from child holons, and holons are writable (`POST /api/holon`) and
rewritable (`PUT /api/holon/{id}`) by any verified user.

`GovernanceBallotRoll` joins those holons to the claim ledger, which stops an
*unclaimed* voter from adding votes — but it does not stop an attacker-written
holon from deciding **which option a claim is spent on**, because the ordering
key (`CastAtOrMax`) and the option itself both come out of attacker-controlled
metadata. That is the residue these tests aim at, and it is why the track
rejected the claim-**joined** tally in favour of D39's claim-**only** tally: a
join stays safe only while nobody drops it.

| Test | AC | Defect pinned |
| --- | --- | --- |
| `Tally_CountsNoBallotHolonWrittenOutsideTheVoteNode` | AC1 | forged ballot holons decide the published count |
| `Tally_IsUnchanged_WhenClientSuppliedTimestampsAreExchanged` | AC3 | ordering key read from attacker-controlled metadata |
| `Tally_AfterClose_IsFrozen_AgainstLateBallotsAndBallotEdits` | AC4 | `HolonManager.UpdateAsync` blind-merges a cast ballot's `option`, with no window check on that path |
| `Vote_CastAgainBeforeClose_ReplacesThePriorOption_WithNoDuplicateCounted` | AC3/D40 | re-vote is refused outright instead of updating one claim row |
| `Proposal_IsReadableByAnEligibleVoter_ButNotByTheWholeNode` | AC5 | electorate readability is bought with node-wide `IsPublic` |

## Conventions

**Assert outcomes, never mechanisms.** Every assertion reads the tally node's
published output or the vote node's result. None names a store call, a query, or
a field that D39 is about to move. That is what lets these same tests turn green
under the rewrite and go red again the day a holon read comes back.

<a id="real-manager"></a>
**The real `HolonManager`, not a mock of it.** `HolonManager` takes only an
`IHolonStore` (the registry and the governance guard default), so
`GovernanceWorld` runs the production owner-or-public `CanRead`, the production
`PublishableAssetTypes` allow-list, and the production blind metadata merge on
`UpdateAsync`. A hand-mocked manager would have been asserting against a mirror
of the rules rather than the rules — and the forgery step would no longer be
"what any API token can do".

**Concrete fakes for the identity and claim stores, not Moq.** When D39 extends
`IPersonOperationClaimStore` with the read the tally needs, a mock would
auto-implement the new member and answer "nothing found", turning a missing
implementation into a green test. A concrete fake refuses to compile until
someone implements it.

**One construction site per handler.** `GovernanceWorld.TallyAsync` /
`VoteAsync` / `CreateProposalAsync` are the only places a handler is
constructed, so the D39 change to the tally's dependencies touches one line in
this directory.
