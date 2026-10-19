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

## Progress — 2026-10-18

Independent reviewer `recover_session` reviewed the first implementation and the batched revisions. Findings addressed: actual tenant authority instead of developer-only admission; per-tenant budget shared across keys/JWT; provider JSON kind errors; malformed/empty/config-free JSON; explicit economic/jurisdiction confirmation and obvious uncertainty; canonical/opaque confirmed handles and holon/internal hold references; provider context includes confirmed resources; SDK interview-body privacy and route-specific deadline. A compile-only pass found one unused import (removed); locked SDK build succeeded. Full tests remain deferred until the final static verdict and release checkpoint merge.

Final static review and collected feature gates subsequently passed: API build, 54 backend/policy tests, SDK type/build with 34 API/privacy tests, frontend type/lint/build with 64 builder tests, and clean SDK/frontend production audits. Lint retains 117 existing warnings; the all/development frontend audit retains seven high findings from the existing braces chain, with no compatible upstream patch. Real provider diagnosis then exposed Markdown-fenced JSON-object output. The independently reviewed native strict-schema change retained all fail-closed validation and passed 59 focused compiled tests plus the final real synthetic OpenRouter smoke. See `verification.md` for exact commits, results, advisory paths and limits. No pushes, deployment changes or full tenant browser interview are claimed.
