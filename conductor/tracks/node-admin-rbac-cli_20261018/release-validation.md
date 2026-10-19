---
type: release-validation
status: in_progress
---

# Combined release validation

The continuation preserves the original partial fix worktree at `649d501c`. Reviewed release fixes are isolated on `feat/node-admin-release`; the tenant economic workflow AI track was reviewed and merged at `a42be432`. Main and Railway have not received these changes yet.

The coordinator's focused node-admin and ProgramComposition integration run passed 31/31 after the isolated database, route-case, stale-login fixture and bootstrap cancellation-lifetime fixes. CLI typecheck/build and production audit passed; 123 tests passed and two were skipped. The combined SDK/frontend sweep passed 323 SDK tests, 311 frontend tests, typecheck, lint and build. Production dependency audits passed; seven development-tool findings remain documented in the AI track. These results do not establish a passing release-wide gate.

The initial broad integration run recorded 1,010 passes, 95 failures and three not-executed cases before the coordinator stopped its exporter shutdown stall. An explicit default-enabled export flag lets test hosts retain local instrumentation while avoiding an external collector. The subsequent full unit run passed 5,416/5,420; four coordination timeouts led to the independently reviewed asynchronous start-gate batch. That batch still needs a complete passing unit rerun. The historical 92-case baseline is a count, without a named manifest proving correspondence to the current failures.

## Collected real-database corrections

The wallet claim lookup retains typed access and captures `claimToken` rather than SurrealDB's protected `token` parameter. The accompanying exact-lookup regression exercises two distinct claim invitations and an absent token. Mechanical fixtures now use guarded conditional writers, typed record links, real identity-gate initialization and current semantic result contracts. Store-family inventory includes real node-admin rollback, directory-epoch CAS and duplicate-write witnesses. All changes received independent static review; the collected runtime rerun is pending.

KYC identity binding retains its multi-table atomic transaction: active hosted-attempt and lease authority, evidence fencing, subject/tenant provenance and identity membership must commit together. Its composed queries now use disjoint `_attempt_*` and `_binding_*` parameter namespaces; the earlier provider enum parameter overwrote the lowercase active-provider key and caused valid approvals to remain pending. Only optional identity fields coalesce SQL null to `NONE`. Required authority/provenance fields and all fences remain required. See `Providers/Stores/Surreal/AGENTS.md` for the atomic invariant. New real-database regressions cover successful direct/tenant binding and rollback under changed authority. No raw single-statement CRUD exception was added.

## UX and tenant AI evidence

The AI model path passed one final typed synthetic response after two bounded diagnostic calls, three calls total. It returned follow-up questions and concerns with no draft for an incomplete brief. Provider credentials stayed server-side and were not committed. The local tenant fixture has verified developer role and live owner provisioning authority through real HTTP policies; the assistant is still disabled in the current local container. Authenticated UI interview testing requires an explicitly budgeted, guarded follow-up pass.

The refreshed read-only UX pass completed four constrained journeys and all three gold tasks. Reported friction decreased from 9.25 to 2.43 per journey. Contrast remains on four desktop routes, and 11/57 route coverage remains below the configured 20% target. The study has not met its two-consecutive-pass exit criterion. Source-free panel findings and private trace limitations are recorded in the root `uxqa` study.
