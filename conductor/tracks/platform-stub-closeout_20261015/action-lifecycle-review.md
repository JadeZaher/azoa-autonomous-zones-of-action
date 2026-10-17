---
type: review
---

# Action lifecycle integration finding

Independent root source review during NFT integration found a remaining implementation package, not an external deferral.

`ActionNodeStepHandler` parks workflow gates/timers before the one-way Pending-to-Running node claim. NFT handlers need a different durable pending outcome after operation admission: preserve the exact external lifecycle/request identity, park without consuming ordinary failure retries, and reconcile to terminal confirmation before advancing successors. Recipient acceptance and chain finality are expected waits, not execution failures or successful completion.

`ReplayAdvancementAsync` currently returns `StepResult.Ok` for a still-Running execution. Its comment expects lease reclaim, but that return must be checked against saga completion behavior: a crash after node claim must not permanently complete the step with no successor and an orphaned Running node. The next reviewed action-engine package must cover this crash window independently of the new NFT wait.

Required evidence includes crash after claim, before/after lifecycle admission, lost first response, recipient acceptance after the ordinary retry budget, concurrent reconciliation, cancelled/failed lifecycle, and no successor before exact terminal proof. Any recovery must reuse the original operation key and maintain the existing exactly-once guards. Do not reset a node to Pending and repeat an ambiguous economic effect.

No runtime test was run in this review.

Follow-up source evidence: SagaProcessor dispatches successful StepResult to completion (lines 144 and 190), while ActionNodeStepHandler.ReplayAdvancementAsync returns Ok for Running (line 588). The crash-after-claim orphan is therefore a concrete source-level completion path, not merely a comment concern. Runtime reproduction remains pending.


The NFT author confirmed that ListPendingAsync has no worker consumer yet. Browser-closed convergence remains implementation work. A dedicated reconciliation operation should observe/rebroadcast already admitted wire and finalize projection without creating new signatures or impersonating recipient/creator acceptance. It must not blindly call a refresh path that can transition into new preparation after expiry or a confirmed intermediary stage.

