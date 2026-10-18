# Durable DEX stages

`SwapStageCoordinator` is the internal stage boundary. Transport/domain callers
first authorize private operation access and select the provider for the saved
chain/network. The coordinator independently binds that module and all saved
intent/plan/candidate hashes. No public request supplies accounting evidence.

Custody admission advances the planned stage and operation epochs under the
shared current signing/action fence. It does not permit egress. Cancellation
may still win while signing runs; saving and first-send authorization must
then fail their exact epochs. A retry that only finds a prior plan is never
a substitute for this live check. Browser disclosure goes only through the
explicit `external-signing` exposure transition; executable unsigned wire is
never returned through ordinary views.

Recovery observes before replay, and only sends already-authorized canonical
bytes while their operation/stage remains pending. It neither plans nor signs
new transactions. Confirmed effects with unknown incremental rent remain
separate from accounting completion; only the original provider's historical
proof can resolve that reservation. Infrastructure errors propagate to the
worker/HTTP boundary with the original durable wire intact.

Cleanup is a new direct participant command with current wallet/grant authority;
it does not reactivate a completed workflow. Rent release advances a persisted
provider checkpoint one bounded history page at a time. Only original known
allocation can be released, once. Original allocation totals remain historical;
the separate release total reduces outstanding locked balance. Unknown allocation
keeps its entire reservation until its earlier accounting proof is resolved.

The authenticated `/api/dex` boundary captures the real credential, persists
immutable quotes and command identities, and projects receipts without private
wire. Cleanup has a separate participant command and fee ceiling. Completion
requires finalized cleanup plus attributed release; it preserves any earlier
economic settlement. Missing configured modules return unavailable.

Recovery advances each due operation's retry time before contacting providers.
Accounting traverses persisted 25-stage pages, advancing the cursor before
provider work so an unresolved ancestor does not monopolize later history.
Only saved, submission-authorized wire may replay. Neither a worker nor polling
can create signatures or replace an accepted quote. The legacy `/api/swap`
quote/execute surface, `SwapManager`, `IDexAdapter` and the memory quote cache
are retired; `/api/dex` is the single execution authority.

External signing history stores a nullable real candidate plus an explicit original exposed plan for finalized absence. The absence proof is provider validated before journal admission and revalidated before a successor; it never manufactures a signature. `ServerSubmissionWithdrawnAt` disables future server authorization/replay but preserves original potentially-sent history and does not recall already admitted/in-flight requests. Observation and accounting continue. No cancellation or budget release follows from withdrawal alone.

## Capability discovery and activation

`GET /api/dex/capabilities` lists `DexCapabilityView` rows only for providers
that structurally implement `IDexTransactionModule` on an enabled network, with
the provider-read protocol identity (`DescribeDexNetworkAsync`). Grammar is not
liquidity or readiness; a module whose identity read fails is listed as not
ready with its reason. Tinyman, Jupiter and Uniswap V3 modules implement every
member but deliberately do not declare the interface until the independent
round-3 adversarial review; activation is that one declaration per provider.

Each description costs one node round-trip per enabled network. The route sits
behind the `financial` rate limiter. `DexModuleResolver.DescribeAsync` caches the
list for 30 s in the shared bounded `IMemoryCache` (entry size 1). The cache is
process-wide even though the resolver is scoped.

Read and refresh carry the same `swap:sign` ceiling as admission. `GetAsync`,
`RefreshAsync` and every command go through `ReadOwnedAsync`. Quote reads go
through `DexQuoteService.GetAsync`. Both check `SwapOperationService.SwapScope`:
owner authority, or an explicit `swap:sign` scope. A narrower API key sees
not-found. A replayed external-signing disclosure re-binds live `swap:sign`
consent (`BindConsentAsync`) before returning the stored unsigned wire, so an
earlier exposure record alone never re-discloses it.

## Value gate and pin scope (round 4 fix batch)

`SwapOperationService` consults the risk-hold-wrapped `IRealValueKycGate` (KYC + reversal account pause, X-2) at
admission (`StartAsync`) and before any custody signature, first send or external-signing disclosure of a non-Cleanup
stage. Residual-return cleanup stays open. New quotes (`DexQuoteService`) and the swap node's admission check resolve
modules through the D-7 pin (`DexModuleResolver.Resolve`); every command on an already-admitted swap and the recovery
sweep use `ResolveForExisting`, which ignores the pin so a pin flip never strands in-flight work (RV-L5-11).

## Retained provider resources

Providers may implement the optional `IDexRetainedResourceModule`. After each
recovery pass the coordinator offers the current saved confirmed stage (effect
or cleanup) to the original module, which releases only what that stage's own
evidence proves free (Ethereum: the exact allowance resource after a confirmed
swap or revocation with zero remaining allowance). The release is idempotent; a
lost release is retried by the allowance store when another operation contends
for the same resource. No absence, elapsed time or operator assertion releases.

## §action swaps

`SwapNodeHandler` is a recoverable action handler. Its frozen config is a
`DexQuoteRequest`; `SwapActionResolver` (domain `swap`, request key
`action:{run}:{node}:swap`) quotes once under the node's frozen workflow
signing witness, admits the immutable operation, and then advances it only
through `SwapOperationService` (prepare, custody sign, submit, refresh), each
step re-checking live workflow authority. The node succeeds only when a
settlement receipt exists; its output is the flat `SwapActionOutput`
(actual input/output, platform fee, winning transaction IDs, retained effects).
Cancelled maps to Cancelled, NeedsOperator to Ambiguous, and cleanup started
without settlement to Failed; a network with no enabled module fails before any
quote or admission; unavailable liquidity and in-flight stages park Pending.

`DexResultCodes.Unsettleable` is the one refusal that is not retryable. It covers
an elapsed executor deadline (provider) and a next plan that no longer fits the
remaining consented caps (coordinator). In the budget case, the coordinator first
hands the unclaimed provider reservation back. The code survives
`SwapStageCoordinator` (`Carried`) and `SwapOperationService` (`Refusal`). The
resolver then cancels an `Admitted`/`Preparing` unsent operation, which releases
the unsent plan's nonce, and returns Failed. Every other refusal parks Pending. Any drift between
the frozen request, the witness and the admitted intent is Ambiguous, never a
fresh quote. Compensation remains a separately quoted reverse swap.

## Cleanup semantics

The cleanup command carries exactly one affirmative flag:
`AcceptCurrentZeroHoldingReplacement` (`CloseCurrentZeroHolding`, original output
asset; Tinyman `CloseOptIn`, Jupiter `CloseAta`) or
`AcceptCurrentAllowanceRevocation` (`RevokeCurrentAllowance`, original ERC20 input
token and quoted executor; Ethereum `RevokeAllowance`). Both or neither is no
consent. Each provider refuses the other semantic. Admission refuses it first:
`BeginCleanupAsync` requires the resolved original module's
`DexCapability.CleanupSemantics` to list the requested semantic before binding
authority or writing consent. Ethereum lists `RevokeCurrentAllowance`; Tinyman and
Jupiter list `CloseCurrentZeroHolding`. An identical consent replay is answered
before this check.

Cancelling an unsent cleanup stage (never authorized, never exposed) depends on
the operation's history:

- If any earlier stage had egress, the operation returns to `CleanupReady`.
  Retained resources (an allowance lane, a zero holding) still need their cleanup.
  The same consent may re-plan the same cleanup stage at the next generation.
  Effect plans stay refused from `CleanupReady`.
- If no stage was ever authorized or exposed, the operation returns to
  `Cancelled`. A never-sent operation has nothing on chain to clean up, and only
  `Cancelled` lets the allowance store hand its lane to another operation.

`HadEgressAsync` reads the stages before the commit. That is safe because every
egress write bumps the operation version, so a racing exposure fails the cancel's
CAS.

`CancelUnsentAsync` offers the cancelled stage to
`IDexRetainedResourceModule.ReleaseUnsentStageAsync`, so an unsigned Ethereum
nonce is refilled instead of leaving a hole. The hand-back is idempotent and
re-runs whenever the call finds the operation already `Cancelled` or at a newer
version. The resolver also re-runs it each time it observes a `Cancelled` swap
with a stage. A crash between the cancel commit and the hand-back is therefore
repaired by the next retry or observation.

## Operator settlement

A same-call nonce winner parks the stage in `NeedsOperator`: the owner's wallet
re-priced the exact planned call, and it executed. Nothing automatic may move it,
and it holds the allowance lane. `POST /api/operator/dex/operations/{id}/settle-review`
(`DexOperatorController`, recent node-operator session, `financial` limiter)
resolves it:

1. `SwapOperationService.SettleOperatorReviewAsync` resolves the original module
   as `IDexOperatorSettlementModule`. It builds the `SwapOperatorSettlement`
   audit: operator id, reason, time, and the hash of the exact review
   observation being replaced.
2. The provider re-proves everything from finalized chain state:
   `ProveOperatorSettlementAsync` returns `Confirmed` or `FailedOnChain` with the
   audit inside its evidence. Nothing is taken on trust from the review.
3. `ISwapOperationStore.SettleOperatorReviewAsync` writes it under paired version
   CAS. It requires the operation and stage to be in `NeedsOperator` and the
   audit hash to match the current review. The operation moves to
   `StageResolved` and is charged the winner's actual fee.

The immutable terminal observation is the audit row. No separate table was
added, because new tables need generated schema. A structured warning log
records the operator too.

Downstream, the settled stage is an ordinary terminal history item:

- The coordinator's `ValidatedHistory` and the provider history replay accept
  it through `ValidateOperatorSettlement`.
- `CompleteAsync` and `CompleteCleanupAsync` accept it through
  `ProvenConfirmedStage`.
- `ClaimNextStageAsync` and `BeginCleanupAsync` accept it through
  `ValidJournalObservation`.
- `ReleaseRetainedResourcesAsync` releases through `ReleaseSettledResourcesAsync`.

So a settled swap completes, a settled approval continues to the swap, and a
settled revert retries at the next generation. `RecordObservationAsync` refuses
any observation claiming operator settlement, so the worker can never write one.

The winner's fee is deliberately not bounded by the stage reservation: the
owner's wallet chose it. If it exceeds the remaining caps, the next plan is
`Unsettleable`.
