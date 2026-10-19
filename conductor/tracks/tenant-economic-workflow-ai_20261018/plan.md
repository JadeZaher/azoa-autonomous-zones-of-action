---
type: implementation-plan
---

# Plan

- [x] Read project/Conductor context; inspect existing builder, auth and Pi provider metadata.
- [x] Consult current OpenRouter API and official IRS/FinCEN/SEC references.
- [x] Implement bounded authenticated interview service and strict draft validation.
- [x] Add consent, iterative intake, contextual risks and explicit canvas import to tenant UI.
- [x] Add meaningful service/UI tests and UX scenarios.
- [x] Apply all fixes, then run one integrated build/test/type/lint sweep; known development-only audit findings are recorded separately.
- [x] Request independent root review; commit coherent reviewed slices and coordinate merge windows.
- [x] Bounded real synthetic model/service drill using private in-process credential injection.
- [ ] Guarded authenticated tenant UI drill on the combined local build; production configuration remains a separate root-coordinated decision.

The subgoal is recorded here as durable session state. The release session retains ownership of merges into main, pushes, Railway configuration and root-key drills.

## UI03 correction batch - reviewed and runtime gates passed; refreshed UI pending

The final review correction discloses Emit's actual later-run side effect: import performs no execution, but a later explicit run requires an active tenant registration and can enqueue webhook notifications. Checkpoints do not wait for or prove review/approval. It also suppresses selected generic questions when distinct specific questions cover the same dimension, while preserving all specifics, and gives UI regression waits an exact reply target.

The real tenant interview exposed obsolete statutory amounts/rates, internal confirmation names, repeated operational questions and narrative claiming a nonexistent draft. Previous prompt/schema transport gates did not establish legal accuracy or usable draft delivery. This batch replaces arbitrary model prose/graphs with required closed category arrays, independently strict local deserialization, server-owned checklist/state wording and a canonical private planning-notes graph.

The UI defaults to interview-only and requires explicit planning/data-only opt-in plus fact confirmations before a graph. Emit notes record manual/external handoffs; they do not implement two-person approval, eligibility, caps, deadlines, refunds, payouts or settlement. This is an editable authoring starting point, not full executable economic workflow generation. Operational gaps block; tax-year/classification/source/local-adviser questions are nonblocking professional review before operation. Whole-answer uncertainty replaces keyword scanning, preserving contextual unknown-payment recovery and user amounts.

- [x] Independent static acceptance of the complete closed-catalog batch.
- [x] Authorized compile-only proof: final API/unit/integration projects, SDK build and frontend typecheck exit 0; no tests/lint or provider calls run by this lane.
- [x] Coordinator-collected backend/SDK/frontend gates: 5,427 unit, 326 SDK and 314 frontend passes; SDK typecheck, frontend lint/build and API/unit/integration compilation passed. The reviewed backend-only correction then passed the full unit suite again and 62/62 affected real-database tests, including all six earlier failing names.
- [ ] Refreshed guarded UI proof of explicit scope, truthful state, graph review and canvas import with complete synthetic intake.
- [ ] Two consecutive valid study passes and remaining release-wide gates. No readiness or legal-compliance claim is made by this authored batch.

## Progress — 2026-10-18

Independent reviewer `recover_session` reviewed the first implementation and the batched revisions. Findings addressed: actual tenant authority instead of developer-only admission; per-tenant budget shared across keys/JWT; provider JSON kind errors; malformed/empty/config-free JSON; explicit economic/jurisdiction confirmation and obvious uncertainty; canonical/opaque confirmed handles and holon/internal hold references; provider context includes confirmed resources; SDK interview-body privacy and route-specific deadline. A compile-only pass found one unused import (removed); locked SDK build succeeded. Full tests remain deferred until the final static verdict and release checkpoint merge.

Final static review and collected feature gates subsequently passed: API build, 54 backend/policy tests, SDK type/build with 34 API/privacy tests, frontend type/lint/build with 64 builder tests, and clean SDK/frontend production audits. Lint retains 117 existing warnings; the all/development frontend audit retains seven high findings from the existing braces chain, with no compatible upstream patch. Real provider diagnosis then exposed Markdown-fenced JSON-object output. The independently reviewed native strict-schema change retained all fail-closed validation and passed 59 focused compiled tests plus the final real synthetic OpenRouter smoke. See `verification.md` for exact commits, results, advisory paths and limits. No pushes, deployment changes or full tenant browser interview are claimed.
