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
