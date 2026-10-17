# Shared NFT lifecycle UI

NFT mutations show a server fee quote before authorization and persist the exact
request key, body and fee cap for retries. Only Completed reports confirmed chain
completion. Local storage is retry convenience: participant-scoped server discovery
is the recovery authority across devices and includes recipient/creator work.
Discovery follows nextCursor even when a filtered page has no visible items.

The initiating account pays the platform fee once. The displayed fee excludes
chain fees; participants may pay their own network fee when authorizing acceptance
or destruction. Refund requests name the original confirmed transfer and require
the current holder's explicit return authorization.

Do not clear a saved request merely because a different operation for that asset
completed. Preserve unreadable saved requests until their outcomes are reconciled.
Verification endpoints return booleans; absent or false evidence never means verified.

`nft-replacement-costs.tsx` discloses a fresh offer for a proven-no-effect Solana
creation generation and archives the exact acceptance (stage request + receipt)
before sending it. A saved acceptance blocks a new offer until reconciliation or
retirement returns Applied or Obsolete; an Applied acceptance for an earlier
generation then permits reviewing the later one. Accepting records permission
only; the lifecycle panel's explicit continue action prepares the attempt.

## §nft-stage-costs

`nft-stage-costs.tsx` is the payer's step for recipient-stage Solana costs (F-M11; server contract in
`Services/Users/AGENTS.md` §NFT recipient-stage costs). The node parks a cost-bearing `Accept`,
`Transfer` or `ReturnForBurn` stage unsigned with `needsStageCosts`, and names its only payer in
`stageCostPayerUserId` (the accepting recipient on a non-refund `Accept`, otherwise the requester).
Only that user sees the step. Others get one line saying who must act. The payer quotes the exact
reviewed stage (version, state, stage, generation, fresh request id), reviews the three limits
(`NftProtocolCostReview`, heading "Stage costs") and accepts. The acceptance is saved before it is
sent, so a lost answer is retried with the same stage request id, which the node treats as the same
acceptance.

Accepting records permission only. "Continue with accepted costs" (or "Resume accepted stage" when a
stage consent exists) prepares the stage. Before preparing, the node re-quotes read-only. If costs
grew past the accepted limits it refuses with `NFT_STAGE_COSTS_CHANGED`. The view cannot show that
(`needsStageCosts` stays false because an acceptance exists), so the panel keeps the refusal as
`stageCostsChanged` and the step offers "Review current stage costs". A later acceptance for the same
stage and generation supersedes the earlier one while no wire exists. An unsent offer for an older
version or generation is dropped on load. A saved acceptance is dropped once the node no longer asks
for costs.
