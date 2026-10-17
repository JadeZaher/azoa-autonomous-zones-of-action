---
type: Review
title: Local receipt and governance design review
status: Accepted with membership and cutoff clarifications
---

Coordinator independently reviewed the proposed per-command mutation plans and concrete governance authority mapping. Atomic typed mutation/effect receipts with immutable deterministic identities are accepted. Authorization and read-version fences must be enforced at commit, including cross-record consent/ownership races; a manager precheck alone is insufficient. A committed receipt can be recovered without permission to execute the effect again. Bounded graph transactions must preserve cycle and parent/edge invariants.

The authenticated tenant principal is an acceptable initial election administrator. Child provisioning lineage and delegated consent do not confer administrator or voter impersonation authority. Grantor membership must use live Participation-origin grants with participation references, or a separately explicit governance membership contract. An arbitrary UserExplicit wallet, read or payment grant cannot enroll a voter. Freeze the exact membership rule/version and test that unrelated consent cannot enroll.

Ballot evidence records the eligibility/verified identity at casting and immutable proposal definition. Finalization counts those valid ballots rather than re-running current membership or AML and retroactively deleting votes after offboarding. Use database time for the cutoff inside the ballot transaction; all release and decision-update paths, including generic claim APIs, must enforce the same fence. Persist canonical decision identity and frozen tally schema. Later state changes cannot mutate a finalized result.

Author may proceed after incorporating these clarifications. Source review, real transaction/race tests and gate-removal review remain outstanding. This design verdict is not executed verification.
