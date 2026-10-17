---
type: Design Amendment
status: Accepted by Root after incarnation-race correction; implementation in progress
---

# DEX prerequisite cleanup and rent release

Cleanup requires a separate explicit owner/tenant command bound to the original
operation, expected version and immutable cleanup cap. It is not automatic
compensation or permission to transfer output. All originally authorized stages
must have authoritative terminal outcomes before a cleanup plan is admitted.
Retaining the disclosed prerequisite remains a valid visible outcome.
For workflows this is a new participant-authorized command with its own current
credential and consent. It preserves the original operation linkage but does
not require or fabricate a Running state for an already completed action node.

Add a provider-owned `PlanSwapCleanupAsync(intent, history, stage, generation)`
entry point. The domain supplies the original persisted terminal lineage and
resolved rent proofs, never browser-provided transaction evidence. The provider
chooses exact cleanup semantics; generic orchestration does not branch by chain.
The existing stage journal, current signing/action fences, original prepared
bytes, separate cleanup-fee reservation and finality gates remain mandatory.

For Tinyman, `CloseOptIn` is offered only when this operation's historical
opt-in accounting proof establishes an actual 100000 microAlgo increment. A
competing earlier opt-in with zero increment cannot offer this cleanup. The
separate cleanup confirmation explicitly authorizes closing the owner's current
zero holding for this exact ASA, including a replacement holding if the owner
closes and reopens it before execution. The saved cleanup consent records this
incarnation-independent authority; prior swap/opt-in consent does not imply it.
Preparation history is not an execution-time incarnation guard. Current holding must exist with zero
amount and the actor must not be the asset creator. The exact canonical wire is
an ASA zero transfer to self with AssetCloseTo=self, bounded fee, saved validity
window/genesis and preparation note. No rekey, clawback, group or third-party
close destination is permitted.

Self-close is intentional and prevents a balance race from sweeping assets.
Pinned go-algorand transaction validation permits it. The apply implementation
transfers any residual amount out and back into the same holding, then requires
that holding to be zero before deletion. Therefore a positive balance arriving
after preparation makes this cleanup fail atomically. Closing to the creator
would instead move newly arrived assets and is excluded. Final observation must
prove the exact signed close transaction, zero asset amount and zero closing
amount in a same-genesis finalized block. Current missing holding alone proves
neither execution nor rent release. The immutable cleanup outcome separately
states that the zero holding was closed and whether any operation-attributed
rent was released. Full exhausted history through the finalized cleanup's exact
position, not merely its preparation round, must establish attribution. If the
original holding closed/reopened in between, this cleanup receives zero credit
against the original allocation even though its explicitly consented effect
completed. Missing history leaves the allocation accounting unresolved.

Extend terminal observation with distinct `ReleasedLockedBalance`, default zero,
and the journal with cumulative released rent. Allocation remains a nonnegative
historical amount; cleanup does not encode a negative allocation. A release
must reference one exact previously operation-attributed rent allocation and
cannot consume it twice. Atomic paired stage/operation mutation caps release by
that allocation and aggregate attributed rent, and enforces allocated minus
released >= 0. Unknown rent keeps its full reservation and is not releasable.
If full history proves an intervening external close released the original
allocation, record that exact external transaction as a separately classified
release cause; it is not this cleanup's receipt or a platform compensation.
The allocation's unique release identity serializes external-release and
cleanup-release proofs, so neither can credit the same allocation twice.
Remaining-cap calculations include retained allocation minus confirmed releases
plus unresolved reservations. Completion of swap economics remains immutable
while cleanup/accounting progress is separately visible.

Required authored fixtures: real protocol self-close with zero amount; a newly
arrived positive balance refuses without moving units; close/reopen after
preparation follows the explicit current-zero-holding consent but refuses
original-allocation credit for cleanup; historical external release is accounted
separately once; competing prior opt-in cannot be closed as this operation's
effect; response loss replays identical cleanup wire; duplicate terminal proof
releases once; unknown/missing history never releases; cleanup network costs
remain within the separately accepted cap. Runtime is held for the integrated
final sweep.

Official source: go-algorand commit
`cf7ac7503efd5cb1303627036d9a9e37fb6e904f`, `ledger/apply/asset.go`
lines339-419 and `data/transactions/asset.go` lines103-112. Captured under
`.omc/research/round3-asa-close-pinned-2026-10-15.go` and
`round3-asa-close-wellformed-2026-10-15.go`. The apply source also rejects
clawback closure and closure of the creator's holding. This source evidence is
not a runtime result.
