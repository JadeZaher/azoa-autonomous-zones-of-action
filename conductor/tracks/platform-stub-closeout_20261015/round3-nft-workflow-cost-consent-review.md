---
type: review
status: accepted-with-binding-clarifications
---

# Workflow NFT cost consent plan review

Independent reviewer: Lane B. Runtime: NOT RUN. Plan accepted with the following
binding implementation requirements; this is not source acceptance.

Persist an immutable acceptance receipt in the same transaction as NFT admission.
It binds client request identity/hash, exact execution record ID/version, frozen
input/reference hash, original workflow witness, actual accepting credential
witness, offer hash and limits, and derived lifecycle identity. A prior receipt
replays before mutable checks; a competing request cannot overwrite it.

The transaction must lock the keyed exact Running execution with expected version
and original signing JSON, compose unchanged ActionExecutionFence, and validate the
actual accepting current consent. Original scope bounds and revocation watermark
remain authoritative; a newer credential cannot replace them. A pre-read followed
by ordinary lifecycle Claim is insufficient.

Missing consent is a recoverable Pending observation, not a terminal rejection.
After acceptance the resolver observes the saved command, preserving the original
cost receipt and signature-free recovery. Frozen input remains unchanged.

Saved acceptance recovery needs either its exact receipt or a monotonic execution
version read followed by a receipt lookup proving the old CAS cannot still commit.
Absence alone never authorizes forgetting the saved request. Include real-store
cancel-versus-accept and competing-acceptance interleavings in the authored evidence.

The Hold opening implementation uses the same exact execution identity and atomic
receipt pattern; no shared ActionExecutionFence weakening is required.
