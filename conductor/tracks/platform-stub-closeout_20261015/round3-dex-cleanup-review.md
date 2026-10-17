---
type: Review
status: Corrected contract accepted for implementation; runtime unrun
---

# DEX cleanup contract review

Root identified that preparation-time history cannot prevent a holding from being closed/reopened before the cleanup wire executes. The revised contract resolves this by requiring separate explicit consent to close the owner's current zero holding for the exact ASA, including a replacement incarnation. Original swap consent cannot substitute for that new authority. Exact self-close forbids sweeping a positive balance; real protocol fixtures still must prove the cited rule.

Accounting is separate from the authorized cleanup effect. Exhausted history through the finalized transaction position attributes rent release; an intervening close/reopen denies this cleanup credit against the original allocation. A separately proven external close may release the original allocation once, serialized under the same unique allocation-release identity. Missing history stays unresolved, not zeroed away.

Accepted for implementation with a workflow lifecycle constraint: cleanup after a completed workflow must use the new participant-authorized cleanup command and live consent. It must not fabricate a Running original execution or depend on the old action fence after that run has succeeded. Preserve immutable origin linkage and the separately consented cleanup budget. Source and runtime review remain required.
