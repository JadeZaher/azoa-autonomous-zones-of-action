---
type: Review
status: Bounded projection accepted; client reconciliation changes required; runtime unrun
---

# B1 client and projection review

Root inspected participant creation/recovery UI, operator genesis UI, AcceptQuote/Continue lifecycle, finalized projection, current-owner attachment, and verified platform lineage.

Projection source accepted in this bounded pass: confirmed issuance, immutable authority, reserve registration and restricted genesis activation commit atomically without depending on expired participant credentials. Ownership conflicts preserve finalized issuance and expose AttachmentPending; a different current owner must authorize the exact original association. Active platform reads traverse completed operation, immutable projection/authority and matching reserve provenance; an asset ID alone is insufficient. Remaining ordinary operation-CAS/reserve-lock statements should use typed builders within the transaction.

Client correction required: a saved continue/replace/cancel/attachment command can be stranded when another tab advances the operation before its expected-version CAS commits. Exact retry alone cannot recover a permanently superseded CAS. Add explicit reconciliation using authenticated current operation evidence and strictly higher monotonic version, preserving the original operation and any already-authorized effects. Equal-version or failed reads must retain the command; do not interpret a generic conflict as proof. Attachment must also preserve exact projection identity. Operator commands require the same treatment. Existing accept-quote replacement uses higher version plus changed quote to fence the older acceptance and is appropriate.

Participant actor-keyed remount and exact uint64 text are appropriate. Operator BFF and broader lifecycle/provider verification remain separate review work. Replace stray question-mark UI separators with plain punctuation. No tests or runtime gates ran.
