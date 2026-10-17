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
