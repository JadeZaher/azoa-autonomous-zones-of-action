# Services/Governance

## node-governance

`NodeGovernanceGuard` is the local node's fail-closed runtime policy gate for
economic-engine generation. Its allowlist semantics are intentionally explicit:

| Value | Meaning |
| --- | --- |
| `null` allowlist | unrestricted dimension |
| empty allowlist | deny every value in that dimension |
| non-empty allowlist | case-insensitive exact membership |

Persistent parameters in `node_governance_parameters` override config once a
row exists; otherwise the guard falls back to `NodeGovernance` appsettings. A
store read error is a security decision failure, so the guard denies instead of
silently falling back.

## node-fees

`NodeFeeScheduleManager` computes `flat + floor(gross * bps / 10_000)` with
`UInt128` intermediates. Flat amounts are unsigned 64-bit base-unit strings and
basis points are limited to `0..10_000`. A quote fails closed when storage is
unavailable, arithmetic overflows, the schedule is invalid, or the fee is at
least the gross amount. The returned schedule version is part of the economic
decision and must be persisted with every consuming operation.

The current settling consumer is allocation Mint, where the fee remains unminted.
Allocation Transfer quotes the schedule but rejects a nonzero fee until a node
treasury transfer can settle it on-chain. Swap, action-completion,
federation-publication, and direct NFT routes remain explicit follow-up consumers;
do not describe their schedule entries as enforced until their value paths quote
and settle them.

`NodeFeeSettlementDraft` is the domain input for the future two-effect settlement
path. It carries only a parent idempotency key, a canonical economic decision,
and the already-validated treasury route; it is neither a user request nor a
chain command. `NodeFeeSettlementRecoveryWorker` runs an explicit, bounded,
clock-injected recovery sweep only: it claims due/stale rows through the manager
and returns them to `AwaitingReconciliation` with a retry time. It has no chain
provider dependency and is deliberately not registered as a hosted service.
No recovery result is a transfer authorization, submission, confirmation, or
fee settlement. Parent-claim atomicity, effect dispatch, and chain-derived
reconciliation remain activation prerequisites.

`RecordAcceptedAtomicGroupAsync` is an unregistered manager seam for durable
post-broadcast evidence. It accepts only a live settlement recovery lease plus
an `IAtomicTransferGroupModule` request/submission pair, delegates the immutable
economic binding and CAS to the store, and leaves the parent InProgress for
chain reconciliation. It does not submit, poll, or terminalize a transfer.
The settlement draft can precommit its request's canonical group identity at
admission. This is optional for non-atomic settlement intents, but mandatory
for receipt recording and immutable thereafter; do not permit a receipt based
only on matching amounts or treasury routing.

`NodeFeeSettlementAtomicGroupReconciler` is a scoped, explicitly invoked
observation seam registered only so an operator-controlled caller can resolve
it. It is not a hosted worker and has no controller or fee-consumer caller.
It atomically claims only a due settlement that already has the deterministic
immutable receipt, leaving ordinary `Prepared`/non-atomic/no-receipt rows
entirely untouched, then
requires its exact settlement link, precommitted group identity, persisted
transaction references, accepted receipt state, and positive pinned economics
before it reconstructs secret-free observation evidence. Signing context and the
raw parent idempotency key are intentionally absent from that evidence. A missing
receipt cannot win the receipt-gated claim and leaves its settlement untouched;
a successfully read but structurally inconsistent receipt is only deferred. A
typed receipt-read integrity/store error stops with the won lease left to expire
and performs no provider call, terminalization, or further reconciliation
mutation, so corruption/infrastructure is not misclassified as missing evidence.
The reconciler resolves the exact persisted chain/network provider and its
observation capability. Only two exact receipt transaction IDs confirmed in one
positive round use the existing paired terminal CAS. That CAS can now take the
persisted parent SHA-256 record id when the raw parent key is unavailable; it
still requires the matching parent record and operation, and never reconstructs
or logs the key. All other observations remain nonterminal and retain a retry.
The deterministic parent replay payload contains settlement/group/transaction
identifiers and confirmation round only.

## node treasury routing

Treasury destinations are separate policy per canonical chain/network. The
destination row and immutable audit entry are committed atomically under an
expected-version CAS; identical retries are value-idempotent and stale differing
writes conflict. Addresses are provider-validated on write and read. Settlement
must pin both the address and routing-policy version, so rotation cannot redirect
an in-flight fee.

## public transparency cursors

`NodeTransparencyManager` is the anonymous read boundary for current governance,
fee, and treasury policy plus their audit histories. Its DTOs deliberately have
no actor/user field, internal record id, raw snapshot JSON, or free-form audit
detail. Actor continuity is omitted until a separately configured, node-scoped
HMAC key exists; hashing a low-entropy user id without such a key would not be
anonymization.

History pages use descending `(occurred_at,id)` keyset order. The internal record
id is carried only inside an ASP.NET Data Protection cursor, which authenticates
and encrypts it before it reaches the wire. Deployments must persist the Data
Protection key ring if cursors must survive a node restart. The one raw read in
`SurrealNodeTransparencyStore` is a temporary, Phase-A-scoped waiver because the
current typed SurrealForge read builder cannot express the composite predicate.
Remove the waiver when that typed predicate lands; it expires before this track
can be completed.

Any environment other than Development/IntegrationTest fails startup without
`DataProtection:KeyRingPath`. That directory
must be a durable shared mount for every API replica and all replicas must use
the same versioned `ApplicationName`; a container-local directory is not enough.
The dev compose stack mounts a named volume at the configured path. Railway and
other multi-instance deployments must mount durable shared storage at
`/app/data/data-protection-keys` (or override `KeyRingPath`).

`ContentSha256` domain-separates and hashes the server's typed policy/items JSON;
it is not a cross-language canonicalization format. It backs a weak semantic HTTP ETag
(the protected cursor ciphertext may legitimately vary while its position does not).

## signed audit checkpoints

`NodeTransparencyHistoryService` is an opt-in, bounded read-only proof surface
at `/api/node-transparency/audit/checkpoint`. It turns the three existing audit
tables into a canonical, redacted, ascending chain and signs the resulting head
with the same dedicated local node-identity key used by L0 conformance — never a
wallet or governance actor credential. The last signed checkpoint is protected
beside that identity key, outside SurrealDB. A subsequent history must preserve
the checkpoint's exact prefix before it may extend it; a rewrite, truncation, or
mutation after a checkpoint fails closed. Public entries contain only a typed
public payload, timestamp, kind, and content hash: no actor/user ids, record
ids, raw persisted JSON, or free-form details.

This is not a claim that a database root credential is solved. It has a trusted
first checkpoint and bounded history (default maximum 512 entries), cannot prove
completeness/non-equivocation without an independently retained or witnessed
checkpoint, and stays unavailable until `NodeTransparencyHistory:Enabled` plus
the configured dedicated `NodeConformance` identity are present. Do not use it
to activate egress fees, federation, or value settlement. Transparency routes
remain globally rate-limited but permanently outside any paid-egress policy.


## consumer-governance

Not the node-operator lane above: `ConsumerGovernanceHolonTypes`,
`GovernanceTallyHash`, `GovernanceClaimLedgerTally` and
`GovernanceVoteOfRecordGuard` describe what a TENANT'S MEMBERS vote on. A
proposal is a Holon; the vote of record is a `person_operation_claim` row.

**The claim IS the vote (D39).** The shipped tally counted child holons, and
`POST /api/holon` writes those, so it counted forgeries: one verified user could
mint five hundred `GovernanceVote` children without ever reaching
`GovernanceVoteNodeHandler`. `GovernanceBallotRoll` -- the claim-JOINED holon
tally -- was the smaller fix and is now DELETED, because the join still let an
attacker-written holon decide WHICH option a claim was spent on (both the option
and the ordering key `castAt` came out of metadata the forger owns), and because
a join stays safe only while nobody drops it. `GovernanceClaimLedgerTally` reads
`person_operation_claim` and nothing else. Do not reintroduce the join.

**One row, so no ordering to attack (D40).** `person_operation_claim` holds
exactly one row per person per operation, written inside one single-winner
statement. The chosen option (`decision_option`) and a server-set `decided_at`
now live on that row, so voting again before the close is an IN-PLACE update: no
second ballot, no "which one is latest", nothing for a hostile client timestamp
to reorder. The decision rides on `PersonUniquenessRequirement.Decision`, so
`IPersonUniquenessGate` keeps its frozen three-method contract.

**The window is enforced three deep, and the innermost one is the real one.**
`GovernanceVoteNodeHandler.CheckVotingWindow` refuses a late vote;
`PersonUniquenessGate.TryClaimAsync` refuses again before it claims (that check is
subject-independent, so short-circuiting it leaks no timing); and the claim
statement itself will not write a decision once the window has shut. The
statement compares against the `decision_closes_at` PINNED ON THE ROW by the first
decision, falling back to the caller's value only when the row has none -- so
rewriting a proposal's `closesAt` afterwards cannot thaw a frozen claim. The gate
reads the decision BACK and answers Ineligible if the store refused it: reporting
success on a decision that was not written is the exact "claimed in prose, not
enforced in code" failure this lane exists to end.

**The tally reads no ballot, and a guard says so.** `GovernanceTallyNodeHandler`
takes an `IHolonManager` and an `IPersonOperationClaimStore` -- no holon store, no
identity store. Its constructor calls
`GovernanceVoteOfRecordGuard.ThrowIfTallyIsNotLedgerOnly()`, which refuses to let
the handler exist if the claim-ledger dependency is gone or an `IHolonStore`
comes back (directly, or one hop away inside a governance collaborator, which is
exactly how `GovernanceBallotRoll` used to be reached). The attack tests in
`tests/AZOA.WebAPI.Tests/Governance` cover the other half -- a tally that keeps
the dependency and stops reading it publishes wrong counts and goes red. Neither
half alone is the guard. `GovernanceVoteOfRecordCount.SourceTable` is checked
against `person_operation_claim` before anything is published, so a count that
arrived from somewhere else is refused rather than dressed up as a result.

The one holon the tally reads is the PROPOSAL, through the consumer-scoped
manager: it is the question (declared options, close time, may this caller see it
at all), never the answer.

**The electorate, not the node (AC5).** `HolonManager.CanRead` is
owner-or-public with NO tenant term, so `IsPublic` alone publishes node-wide, and
that rule lives in a file this lane does not own. A proposal is still published --
an unreadable notice is one only its author can vote on -- but publication is no
longer what enfranchises. `GovernanceProposalCreate` pins
`electorateTenant` from the ACTING tenant (never from config: an author must not
be able to nominate somebody else's members), and both the vote node and the tally
refuse a caller outside it via `ConsumerGovernanceHolonTypes.IsInElectorate`,
which fails CLOSED on a proposal that records no electorate. The refusal reuses
the single `governance proposal not found.` sentence, so a stranger cannot tell a
foreign proposal from a nonexistent one, and the check runs BEFORE the window and
option checks so those cannot become the oracle instead. `electorateTenant` is
also a required field on the `GovernanceProposal` registry seed. The tally derives
its uniqueness scope from that recorded electorate rather than from whoever runs
it, so the published count does not move with the caller.

**Still on the read surface:** `GET /api/holon/{id}` returns a published proposal
to any authenticated user on the node, because `CanRead` is not ours to change.
What is closed is entering or observing the vote. Narrowing `CanRead` itself is
the follow-up.

**The receipt carries no authority.** The `GovernanceVote` child holon is a
receipt for the voter: one per voter per proposal, updated on a re-vote so the
holarchy never shows two ballots from one person. It is written AFTER the vote of
record is committed and is never compensated by releasing the claim -- releasing
would discard a recorded vote (and, on a re-vote, a valid earlier one) because a
holon write failed. A failed receipt yields `voteId: null` on a successful node.
`HolonManager.UpdateAsync` still blind-merges a receipt's metadata; under D39 that
rewrite decides nothing, which is why the fix was to remove the authority rather
than to police the write.

**Never on a receipt:** a person key, a global person key, or any KYC field. The
receipt is consumer-readable through HolonQuery, so
`ConsumerGovernanceHolonTypes.AllowedVoteMetadataKeys` is CLOSED and re-checked
before the write. `decision_option` sitting beside `person_key` on
`person_operation_claim` means the node operator -- and only the node operator --
can read person-to-choice; that is the price of an unforgeable tally and the
reason the table must stay absent from `SearchableEntityType`,
`HolonQueryRequest` and the MCP query allow-list. `ListLiveByOperationAsync` is
keyed by (scope, operation) only, so it cannot be turned into a person-key oracle,
and it PROJECTS: the tally never holds a person key in process memory.

**Tier-1 (D42).** Neither the proposal, the vote, nor the tally overrides
`RequiresChainCapability`. The tally hash is still computed and published over
(proposalId, closesAt, totalVotes, sorted option counts) with no identities in the
preimage, so anchoring it later is an added `GovernanceTally -> BlockchainExecute`
step and not a redesign.
