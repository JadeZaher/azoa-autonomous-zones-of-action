---
type: contract-input
status: proposal for root review; no source edits
---
# Solana native cleanup and separately funded recovery

## Smallest coherent contract changes

`NativeSolanaQuote` currently holds only a linear `Stages` list. Add an optional **fallback cleanup specification** outside that successful sequence: original buffer address, loader program, authority, refund destination, `CloseBuffer` instruction plan and quoted fee. The original quote discloses normal fees plus a cleanup reserve, without double-counting locked rent. Normal successful deployment must never automatically execute the fallback. The prepared cleanup still uses the existing NativePreparationJournal with its own stage/generation key and immutable quote identity.

Separate economic truth from workflow disposition. A bounded `ResourceEffect` enum can distinguish None, BufferFunded, Deployed, Upgraded, AuthorityFinalized. A bounded terminal outcome can distinguish Succeeded, AbortedClean, FailedClean. `CleanupRequired`, `CleanupPending`, `RecoveryQuoteRequired` and `AuthorityFinalizationRequired` are **nonterminal recovery states**, not additional success strings. `NativeNextStage` should return either an exact next stage or validated terminal outcome plus resource/evidence; a failed post-deployment stage must retain confirmed program identity even though the full request is incomplete. Root may choose fewer fields if those distinctions remain unambiguous.

Current `GetNextStageAsync` rejects all FailedOnChain history and counts only confirmed linear stages. Replace that with an ordered transition validator: failures/no-effect generations belong to the same planned stage; confirmed stages advance the successful prefix; explicit durable abort selects fallback only before Deploy/Upgrade effect. Every claimed history item must match immutable intent/stage/wire/protocol and authoritative observation, not merely a caller-supplied verdict.

## Cleanup eligibility

- No current possibly executable authorized parent wire. Unknown, Pending or missing-history observations block cleanup. Timeout, operator assertion and buffer account balance alone are not proof of absence.
- All earlier authorized wires have exact finalized success/failure or authoritative expired-no-effect evidence. A failed-on-chain transaction is atomic but may have consumed fees; charge actual receipt costs.
- Buffer funding/allocation is tied to this operation's successful CreateBuffer transaction. Read the current account and validate loader owner, Buffer state, original authority and exact address before signing close. Never accept an unrelated preexisting account merely because it matches a derived address.
- User abort is allowed before the economic Deploy/Upgrade effect only after the same wire-resolution fence. Retire any merely Prepared but unauthorized parent generation by CAS; a delayed preparer may save its candidate to provider storage but cannot authorize or submit it afterward.
- Confirmed Deploy/Upgrade is not rolled back. Do not close Program/ProgramData or use loader close as a generic undo. If the loader already consumed the buffer, preserve the confirming deployment evidence and actual account disposition. A failed RemoveAuthority requires explicit current-authority retry/finalization, retaining Deployed/Upgraded truth.
- CloseBuffer confirmation reads exact finalized wire and account/balance effects. Expose actual lamports returned to the bound recipient and network fee paid. An estimated balance is only disclosure; it is not a refund receipt.

## Parent/child fencing for fee exhaustion

Reserve cleanup in the original quote, but do not assume an arbitrary number of failed attempts can fit that reserve. Once immutable cap headroom is insufficient, park `RecoveryQuoteRequired`. Create a new read-only quote for a **linked recovery command**, never mutate the original budget. The caller separately authorizes that quote, current custody, fee payer, exact cleanup or authority-finalization effect and refund destination. A new request key identifies this recovery; it cannot introduce an unrelated artifact, recipient or wallet.

Minimal durable fields on DeploymentOperation: `RecoveryMode` (None/AbortCleanup/ContinueCleanup/FinalizeAuthority), `RecoveryChildId?`, `ParentOperationId?`, `ParentRecoveryVersion?`, and immutable `RecoveryBindingHash?` over parent actor/tenant/artifact/protocol, ordered history digest, resolved last generation, exact resource/authority/destination and intended recovery effect. Store original approved intent and all failed generation accounting unchanged.

Add one atomic store operation `ClaimRecoveryChild(expectedParentVersion, expectedGeneration, expectedHistoryHash, candidateChild)` that:

1. Locks/reads the parent by key; verifies exact actor/tenant, identity, eligible recovery mode, no unresolved authorized wire, expected current version/generation/history and no competing child.
2. Atomically retires parent effect authorization, sets the stable winning child ID/binding and increments parent version, then creates or replays the exact immutable child request. Different keys may not create competing cleanup children.
3. Gives every later parent preparation/authorization CAS a predicate requiring no active linked recovery child and unchanged mode. Existing stale writes fail their version check. The worker may still observe archived wires but cannot authorize old parent effects.
4. Gives child first authorization a parent-link/binding predicate in the same transaction, plus current actor custody/value access where required. Action cancellation cannot authorize continuation of the original business effect; cleanup is an explicit user recovery command over already allocated resources, with separate audit and authority.
5. Atomically applies child confirmed evidence to the parent's recovery projection while retaining the child's own economics and original parent receipt. Duplicate completion is idempotent. Failed/unknown child retains its link and exact wire; another fee quote is permitted only after authoritative no-effect retirement of that generation and another exclusive claim.

A database fence cannot stop a sender already holding authorized wire. Therefore the parent-resolution proof is essential: child admission is forbidden until all previously authorized parent wires are final or provably unable to execute. Merely setting `SubmissionAuthorized=false` cannot make a racing previously authorized sender safe. When pre-send authorization existed, resolve the exact generation first.

## Existing implementation reuse and touchpoints

- `SolanaProvider.Native.cs`: NativeSolanaQuote/NativeSolanaStage, GetNextStageAsync, canonical preparation and exact observation.
- `SolanaProvider.NativeArtifact.cs`: seeded buffer creation and linear deploy/write/upgrade/remove-authority plan. Add exact loader buffer-close codec only from captured official loader instruction definition; no guessed discriminator.
- `DeploymentService.Recovery.cs`: currently archives FailedOnChain then marks Failed; route through provider recovery disposition instead, preserving funded/deployed truth and accounting.
- `DeploymentOperation`, `IDeploymentStore`, SurrealDeploymentStore: versioned atomic recovery linkage and guarded authorization. Ordinary CRUD stays typed; the parent+child claim/completion is a justified multi-record atomic transaction.
- Existing native receipt/UI: show original effect, remaining recovery, linked operation, cumulative actual fees/rent return and a separate new quote consent. A terminal child cleanup is not a successful original deployment.

## Required tests

Author overlap fixtures for delayed parent preparation/authorization versus child claim; previously authorized pending wire refuses cleanup; failed Write after funded buffer closes only that buffer; user abort before Deploy; successful Deploy then failed RemoveAuthority retains live resource; cap exhaustion demands explicit linked quote; concurrent child claims yield one identity; lost close response reconciles exact wire; changed refund address/authority/artifact rejected; no-effect child retry preserves audit; actual local loader close proves rent return without destroying program. Tests remain reserved for final integrated sweep.
