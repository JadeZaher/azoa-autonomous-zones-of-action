---
type: release-validation
status: in_progress
---

# Combined release validation

The continuation preserves the original partial fix worktree at `649d501c`. Reviewed changes are isolated on `feat/node-admin-release`; the tenant economic workflow AI track was merged at `a42be432`. Release HEAD `59ef755b` joins upstream/main `9c2f4580` without a content change from `6ac2d021`. Main and Railway have not received the candidate changes.

## Current collected gates - 2026-10-18

The complete collected sweep passed 5,427 unit tests, 326 SDK tests in 34 files and 314 frontend tests in 55 files. SDK typecheck, frontend lint and frontend production build exited zero. API, unit and integration compilation had already passed with the existing warning baseline. Evidence is the coordinator's ignored `.omc/research/node-admin-collected-final/` logs, TRX and results manifest. CLI typecheck/build and production dependency audit previously passed; 123 CLI tests passed and two were skipped. CLI source has not changed since those checks.

The changed-class real-database run completed with 233 passes and six failures out of 239. Three wallet failures share a generated protected `$token` binding. Two NFT read/ownership fixtures attempted wallet creation through a first-party Bearer route using synthetic authentication. The inventory failure identifies missing `NodeAdminAuditLog` witnesses. The complete correction batch subsequently received independent static acceptance. Its collected API/unit/integration compilation passed, the full unit suite passed 5,427/5,427 again, and the affected real-database collection passed 62/62 without skips. A TRX name comparison confirms every one of the six earlier failures now passes; both new audit transaction witnesses pass. This rerun covers node-admin/audit, host composition, wallet authentication, NFT and store inventory; it does not imply a new complete broad-suite run. Evidence is `.omc/research/node-admin-collected-backend-corrections/`.

The wallet correction keeps the typed exact-equality SELECT and `LIMIT 1`, applying the existing shared parameter-scoping helper with `claim_lookup`. SurrealForge derives parameters from database columns: the previous captured-local rename did not change `$token`. Exact two-invite, absent-token, rollback and consumption assertions remain required. The audit addition uses real transactions to witness a losing head CAS followed by a fresh-read retry and a contiguous verified chain, plus rollback when the next row is already occupied. The two NFT historical arrangements use typed stored-wallet seeds; HTTP ownership refusals, composite bindings and real JWT/KYC/fee/durable lifecycle mutation paths remain intact.

## Wider integration baseline

The completed broad snapshot passed 1,063 tests, failed 92 and skipped three out of 1,158. Comparison with the earlier unfinished snapshot found 87 shared failures, five previously unobserved cases and eight formerly failing cases now passing. The unfinished run cannot establish a prior pass for those five cases. The earlier snapshot recorded 1,010 passes, 95 failures and three not-executed cases before an exporter shutdown stall; it was aborted and is not a completed full-suite result.

The default-enabled OpenTelemetry export option lets test hosts retain local instrumentation without an external collector. The complete unit pass uses the reviewed conservative xUnit scheduling algorithm and asynchronous contender barriers, preserving parallel execution and every domain assertion. Neither change clears the remaining broad integration backlog. Native chain/WTT environment-dependent work and other recorded fixtures/contracts remain open in the round-4 deferral ledger. Railway's CI gate must not be described as restored or globally green.

KYC identity binding retains its multi-table atomic transaction: active hosted-attempt and lease authority, evidence fencing, subject/tenant provenance and identity membership commit together. Disjoint `_attempt_*` and `_binding_*` parameters prevent a provider enum from replacing the lowercase provider key. Only optional identity fields coalesce SQL null to `NONE`; required provenance and authority remain required. Real-database regressions cover direct/tenant binding and authority-change rollback. No raw ordinary CRUD exception was added.

## UX and tenant AI evidence

The original service diagnostics used three model calls. The later real UI pass03 used two separate consented calls and exposed obsolete statutory advice, duplicate questions and narrative describing a draft that the UI did not offer. Pass03 is a recorded failure, with 12/57 routes visited, seven completed UI journeys and one partial UI journey, plus a separate authorization probe. Its original contrast violations and private raw traces remain preserved. Agent QA is not a human research study.

The reviewed correction accepts only five required closed category arrays from the model. The server owns visible wording, a geographically limited legal/tax review checklist, readiness and a canonical planning-notes graph. It emits no generated statutory thresholds or rates. The UI defaults to interview-only and requires explicit planning-only scope and fact confirmations before draft review/import. The graph does not implement approval, eligibility, caps, refunds, payouts or settlement. Import executes nothing; a later explicit tenant-context run can enqueue webhook notifications through an active registration. Notes neither wait for nor prove professional review. Full executable economic workflow generation remains unsupported.

The corrected source passed the current unit/SDK/frontend runtime gates. Refreshed exact-candidate images, full hermetic E2E and UI passes04/05 are still pending. The same single local bridge has spent two of four requests; one request is reserved for each remaining pass, with no counter reset. The Pi provider credential is currently absent from the restored key-free local API and has never entered browser state or committed artifacts. Temporary server-only configuration will be removed after the study. Two consecutive qualifying passes and independent final review remain required before closing the UX study.

## Remaining shipping steps

The collected backend correction, independent review and affected runtime gates are complete; the earlier failures remain recorded above. Commit reviewed source and public QA artifacts, repeat exact-value and committed-range secret scans, and run merge-check. Then build the local candidate images and complete E2E/UX verification. Only after the scoped release gates pass: merge `--no-ff` into main, push `upstream`, verify the exact Railway API/frontend/schema revisions and migrations, and confirm the expected unavailable node-admin challenge before owner configuration. The owner alone generates and uses their root private key for the live CLI drill. Wider release readiness remains subject to the recorded integration/vendor/CI blockers.
